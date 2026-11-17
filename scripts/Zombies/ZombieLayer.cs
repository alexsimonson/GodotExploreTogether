using System;
using System.Collections.Generic;
using System.Linq;
using ExploreTogether.City;
using ExploreTogether.Core.Audio;
using ExploreTogether.Core.City;
using ExploreTogether.Core.Items;
using ExploreTogether.Core.Zombies;
using Godot;

namespace ExploreTogether.Zombies;

/// <summary>What a zombie layer needs from the city it's added to (the server fills it in; nothing else is shared).</summary>
public sealed class ZombieContext
{
    public required CityLayout City { get; init; }
    public required CityPaths Paths { get; init; }
    public required CityDoors Doors { get; init; }
    /// <summary>What's between two points (the server's sight lines).</summary>
    public required CityNav.ObstacleTest Sight { get; init; }
    /// <summary>The sun's share of the light now (0 night .. 1 day).</summary>
    public required Func<float> Daylight { get; init; }
    /// <summary>The living players: peer, position, flashlight lit.</summary>
    public required Func<IEnumerable<(long Peer, Vector3 Position, bool Flashlight)>> Players { get; init; }
    /// <summary>The city's residents (standing or not).</summary>
    public required Func<IEnumerable<CityNpc>> Residents { get; init; }
    /// <summary>Where zombie nodes go (they're replicated through the NPC spawner).</summary>
    public required Node Parent { get; init; }
    /// <summary>A new zombie node is in the tree: show it to the players.</summary>
    public required Action<ZombieNpc> Spawned { get; init; }
    /// <summary>Make a noise residents (and other zombies) hear: x, z, radius, cause.</summary>
    public required Action<float, float, float, string> Noise { get; init; }
    /// <summary>Play a sound for the players near.</summary>
    public required Action<SoundId, Vector3> Sound { get; init; }
    /// <summary>A gunshot line (muzzle, where it hit), for the tracer.</summary>
    public required Action<Vector3, Vector3> Shot { get; init; }
}

/// <summary>
/// Zombies in a city: a layer the server adds only when a city is made with zombies (see ServerGame.Zombies). Without
/// it, nothing about the city changes. It runs every <see cref="ZombieBrain"/>, and is where they meet the rest of the
/// game:
/// - they go after players and residents (players through <see cref="PlayerBitten"/>; residents it hurts directly);
/// - they hear the city's noises (gunfire draws them) and batter shut doors (CityDoors.Kick: a locked house holds a
///   while, an unlocked one doesn't);
/// - residents run from them and call for help; police with guns stand their ground and shoot (headshots count);
/// - a resident killed by a zombie gets up as one (<see cref="ResidentRisen"/>: the server removes the body).
/// Weapons reach them through <see cref="IHittable"/> on <see cref="ZombieNpc"/>; what each weapon does is in
/// <see cref="ZombieRules"/>.
/// </summary>
public partial class ZombieLayer : Node
{
    public const double TickSeconds = 0.1, SenseSeconds = 0.3;
    /// <summary>Residents notice a zombie this close (with a clear view); police shoot from this far.</summary>
    public const float NoticeRange = 12, PoliceRange = 18;

    public event Action<long, ZombieNpc, int>? PlayerBitten;
    public event Action<ZombieNpc, long, bool>? Destroyed;     // zombie, by (peer, or 0), headshot
    public event Action<CityNpc, ZombieNpc>? ResidentRisen;

    private ZombieContext _ctx = null!;
    private CityZombieWorld _world = null!;
    private readonly List<ZombieNpc> _zombies = new();
    private readonly List<Heard> _heard = new();
    private readonly Dictionary<CityNpc, double> _rising = new();   // bitten to death: when they get up
    private readonly Dictionary<CityNpc, double> _nextPoliceShot = new();
    private readonly Random _random = new();
    private double _accumulator, _senseTimer, _clock, _logTimer;
    private static readonly bool DevLog = CmdArgs.Has("dev-log-zombies");
    private int _nextId;

    public IReadOnlyList<ZombieNpc> Zombies => _zombies;
    public int Alive => _zombies.Count(z => z.State != ZombieState.Dead);

    public void Init(ZombieContext context)
    {
        _ctx = context;
        _world = new CityZombieWorld(context.City, context.Paths, context.Doors, context.Sight);
    }

    /// <summary>A noise somewhere in the city (from the server's noise hook): zombies may come to it.</summary>
    public void Hear(float x, float z, float radius) => _heard.Add(new Heard(x, z, radius));

    /// <summary>Puts a zombie at (x, z), on height y.</summary>
    public ZombieNpc Spawn(float x, float z, float y = 0)
    {
        var node = GD.Load<PackedScene>(ZombieNpc.ScenePath).Instantiate<ZombieNpc>();
        var id = _nextId++;
        node.Name = $"Zombie{id}";
        node.Brain = new ZombieBrain(id, x, z, _random.Next(), y);
        node.Layer = this;
        node.NetPosition = new Vector3(x, y, z);
        node.Position = node.NetPosition;
        _ctx.Parent.AddChild(node, forceReadableName: true);
        _zombies.Add(node);
        _ctx.Spawned(node);
        return node;
    }

    /// <summary>Spreads <paramref name="count"/> zombies about the streets, between <paramref name="awayFrom"/> and <paramref name="within"/> m of (x, z).</summary>
    public void SpawnAround(int count, float x, float z, float awayFrom, float within = float.MaxValue)
    {
        for (var i = 0; i < count; i++)
        {
            var (sx, sz) = CityZombieWorld.SpawnPoint(_ctx.Paths, _random, x, z, awayFrom, within);
            Spawn(sx, sz);
        }
    }

    // ------------------------------------------------------------------ hits

    public void Hit(ZombieNpc zombie, ItemDef weapon, int damage, bool head, Vector3 from, long attacker)
    {
        if (zombie.Brain is not { } brain || brain.State == ZombieState.Dead) return;
        var hit = ZombieRules.Hit(weapon, damage, head && weapon.Class == ItemClass.Gun);
        var destroyed = brain.TakeHit(hit, from.X, from.Z);
        _ctx.Sound(destroyed ? SoundId.ZombieDeath : hit.KnockdownSeconds > 0 ? SoundId.HitBlunt : SoundId.HitFlesh, zombie.GlobalPosition + Vector3.Up);
        Apply(zombie);
        if (destroyed) Destroy(zombie, attacker, head && weapon.Class == ItemClass.Gun);
        else if (hit.KnockdownSeconds > 0) GD.Print($"[server] zombie {brain.Id} knocked down with {weapon.Name}");
    }

    public void AreaHit(ZombieNpc zombie, int damage, bool frost, Vector3 center, long attacker)
    {
        if (zombie.Brain is not { } brain || brain.State == ZombieState.Dead) return;
        if (brain.TakeHit(ZombieRules.Hit(null, damage, false), center.X, center.Z)) Destroy(zombie, attacker, false);
        else if (frost)
        {
            brain.Freeze(Core.Combat.CombatRules.FrostFreezeSeconds);
            GD.Print($"[server] zombie {brain.Id} is frozen");
        }
        Apply(zombie);
    }

    private void Destroy(ZombieNpc zombie, long by, bool head)
    {
        GD.Print($"[server] zombie {zombie.Brain!.Id} destroyed{(head ? " (headshot)" : "")}");
        Destroyed?.Invoke(zombie, by, head);
    }

    // ------------------------------------------------------------------ the simulation

    public override void _PhysicsProcess(double delta)
    {
        _accumulator += delta;
        while (_accumulator >= TickSeconds)
        {
            _accumulator -= TickSeconds;
            Step((float)TickSeconds);
        }
    }

    private void Step(float seconds)
    {
        _clock += seconds;
        var prey = Prey();
        if ((_senseTimer += seconds) >= SenseSeconds)
        {
            _senseTimer = 0;
            var daylight = _ctx.Daylight();
            foreach (var z in _zombies) z.Brain!.Sense(prey, _heard, daylight, _world);
            _heard.Clear();
            ResidentsReact();
        }
        foreach (var z in _zombies)
        {
            z.Brain!.Update(seconds, _world, prey);
            while (z.Brain.TryTakeIntent(out var intent)) Carry(z, intent);
        }
        KeepApart();
        foreach (var z in _zombies) Apply(z);
        Rise();
        if (DevLog && (_logTimer += seconds) >= 3)
        {
            _logTimer = 0;
            GD.Print($"[server] {Summary()}; " + string.Join(" ", _zombies.Where(z => z.State != ZombieState.Dead)
                .Select(z => $"#{z.Brain!.Id}({z.Brain.X:0},{z.Brain.Z:0}){(z.Brain.Target != 0 ? $"->{z.Brain.Target}" : "")}")));
        }
    }

    /// <summary>Everyone a zombie could go after: players (by peer), and standing residents (as -(id + 1)).</summary>
    private List<Prey> Prey()
    {
        var list = _ctx.Players().Select(p => new Prey(p.Peer, p.Position.X, p.Position.Z, p.Position.Y, p.Flashlight)).ToList();
        foreach (var r in _ctx.Residents())
            if (!r.IsDown) list.Add(new Prey(-(r.NetId + 1), r.NetPosition.X, r.NetPosition.Z, r.NetPosition.Y));
        return list;
    }

    private void Carry(ZombieNpc z, ZombieIntent intent)
    {
        switch (intent.Kind)
        {
            case ZombieIntentKind.Bite when intent.Target > 0:
                _ctx.Sound(SoundId.ZombieBite, z.GlobalPosition + Vector3.Up * 1.3f);
                PlayerBitten?.Invoke(intent.Target, z, ZombieRules.BiteDamage);
                break;
            case ZombieIntentKind.Bite:
                var id = (int)(-intent.Target - 1);
                if (_ctx.Residents().FirstOrDefault(r => r.NetId == id) is not { } victim || victim.IsDown) break;
                _ctx.Sound(SoundId.ZombieBite, z.GlobalPosition + Vector3.Up * 1.3f);
                victim.Brain.Threatened(z.GlobalPosition.X, z.GlobalPosition.Z, holdGround: false);
                if (victim.TakeDamage(ZombieRules.BiteDamage * 2))
                {
                    _rising[victim] = _clock + ZombieRules.RiseSeconds;
                    GD.Print($"[server] zombie {z.Brain!.Id} kills {victim.NetName} ({victim.Role})");
                }
                break;
            case ZombieIntentKind.Bash:
                var p = _ctx.City.Parcels[intent.Door];
                var broke = _ctx.Doors.Kick(intent.Door, Breaching.KicksToBreak(p.Building) * 2);
                _ctx.Sound(broke ? SoundId.DoorBreak : SoundId.ZombieBash, new Vector3(p.Door.X, 1.2f, p.Door.Z));
                _ctx.Noise(p.Door.X, p.Door.Z, broke ? Stealth.BreakRadius : 20, broke ? "door broken" : "banging on a door");
                if (broke) GD.Print($"[server] zombie {z.Brain!.Id} breaks down door {intent.Door}");
                break;
        }
    }

    /// <summary>Residents who see a zombie close: run and call for help; police with a gun stand and shoot.</summary>
    private void ResidentsReact()
    {
        var standing = _zombies.Where(z => z.State is not (ZombieState.Dead or ZombieState.Down)).ToList();
        if (standing.Count == 0) return;
        foreach (var r in _ctx.Residents())
        {
            if (r.IsDown) continue;
            var police = r.Role == NpcRole.Police && r.Brain.HasGun;
            var range = police ? PoliceRange : NoticeRange;
            ZombieNpc? nearest = null;
            var best = range;
            foreach (var z in standing)
            {
                var d = r.NetPosition.DistanceTo(z.GlobalPosition);
                if (d < best && Mathf.Abs(r.NetPosition.Y - z.GlobalPosition.Y) < 2
                    && _ctx.Sight(r.NetPosition.X, r.NetPosition.Z, z.GlobalPosition.X, z.GlobalPosition.Z, r.NetPosition.Y) != CityNav.Obstacle.Wall)
                    (nearest, best) = (z, d);
            }
            if (nearest == null) continue;
            r.Brain.Threatened(nearest.GlobalPosition.X, nearest.GlobalPosition.Z, holdGround: police);
            if (police) PoliceShoot(r, nearest, best);
        }
    }

    private void PoliceShoot(CityNpc officer, ZombieNpc z, float distance)
    {
        if (_nextPoliceShot.TryGetValue(officer, out var next) && _clock < next) return;
        _nextPoliceShot[officer] = _clock + 1.2;
        var gun = officer.Gear.EquippedWeapon;
        var from = officer.NetPosition + new Vector3(0, 1.45f, 0);
        var onTarget = _random.NextDouble() < Mathf.Clamp(0.8f - distance * 0.025f, 0.25f, 0.8f);
        var head = onTarget && _random.NextDouble() < 0.3;
        var aim = z.GlobalPosition + new Vector3(0, head ? 1.6f : 1.1f, 0);
        if (!onTarget) aim += new Vector3((float)_random.NextDouble() - 0.5f, (float)_random.NextDouble() * 0.6f, (float)_random.NextDouble() - 0.5f) * 2;
        _ctx.Shot(from, aim);
        _ctx.Sound(gun?.Breaches == true ? SoundId.ShotgunShot : SoundId.PistolShot, from);
        _ctx.Noise(from.X, from.Z, Stealth.GunshotRadius, "gunshot");
        if (!onTarget || gun == null) return;
        var brain = z.Brain!;
        if (brain.TakeHit(ZombieRules.Hit(gun, gun.Damage, head), from.X, from.Z))
        {
            _ctx.Sound(SoundId.ZombieDeath, z.GlobalPosition + Vector3.Up);
            Apply(z);
            GD.Print($"[server] {officer.NetName} (Police) shoots zombie {brain.Id}{(head ? " in the head" : "")}");
            Destroy(z, 0, head);
        }
    }

    /// <summary>Bitten to death a while ago: they get up as a zombie, where they lay.</summary>
    private void Rise()
    {
        foreach (var (corpse, at) in _rising.ToList())
        {
            if (_clock < at) continue;
            _rising.Remove(corpse);
            if (!IsInstanceValid(corpse) || !corpse.NetDead) continue;
            var p = corpse.NetPosition;
            var zombie = Spawn(p.X, p.Z, p.Y);
            GD.Print($"[server] {corpse.NetName} rises as zombie {zombie.Brain!.Id}");
            _ctx.Sound(SoundId.ZombieGroan, p + Vector3.Up);
            ResidentRisen?.Invoke(corpse, zombie);
        }
    }

    /// <summary>Zombies don't stand inside each other: overlapping ones are pushed apart.</summary>
    private void KeepApart()
    {
        for (var i = 0; i < _zombies.Count; i++)
            for (var j = i + 1; j < _zombies.Count; j++)
            {
                var (a, b) = (_zombies[i].Brain!, _zombies[j].Brain!);
                if (a.State == ZombieState.Dead || b.State == ZombieState.Dead || MathF.Abs(a.Y - b.Y) > 1.5f) continue;
                float dx = b.X - a.X, dz = b.Z - a.Z;
                var d = MathF.Sqrt(dx * dx + dz * dz);
                if (d >= 0.7f) continue;
                if (d < 1e-3f) (dx, dz, d) = (1, 0, 1);
                var push = (0.7f - d) / 2;
                a.Nudge(-dx / d * push, -dz / d * push);
                b.Nudge(dx / d * push, dz / d * push);
            }
    }

    private static void Apply(ZombieNpc z)
    {
        var b = z.Brain!;
        z.NetPosition = new Vector3(b.X, b.Y, b.Z);
        z.Position = z.NetPosition;
        z.NetYaw = b.Yaw;
        z.Rotation = new Vector3(0, b.Yaw, 0);
        if (z.NetState != (int)b.State) z.NetState = (int)b.State;
        if (z.NetMoving != b.Moving) z.NetMoving = b.Moving;
        if (z.NetHealth != b.Health) z.NetHealth = b.Health;
    }

    /// <summary>"7 zombies (2 chasing, 1 down)": for logs.</summary>
    public string Summary() =>
        $"{Alive} zombie(s): " + string.Join(", ", _zombies.GroupBy(z => z.State).Select(g => $"{g.Count()} {g.Key.ToString().ToLowerInvariant()}"));
}
