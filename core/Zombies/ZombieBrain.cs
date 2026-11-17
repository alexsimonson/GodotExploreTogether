namespace ExploreTogether.Core.Zombies;

/// <summary>Someone a zombie could go after. <see cref="Id"/> is whatever the layer uses to tell them apart.</summary>
public readonly record struct Prey(long Id, float X, float Z, float Y = 0, bool Flashlight = false);

/// <summary>A noise a zombie may hear (it carries <see cref="ZombieRules.HearingFactor"/> times further for them).</summary>
public readonly record struct Heard(float X, float Z, float Radius);

/// <summary>A point on a zombie's way; <see cref="Door"/> >= 0: the leg to it goes through that door.</summary>
public readonly record struct ZombieStep(float X, float Z, float Y = 0, int Door = -1);

/// <summary>
/// What a zombie needs from the place it's in (a city, later maybe a dungeon): ways to walk, sight lines, and doors.
/// The brain knows nothing else about the world.
/// </summary>
public interface IZombieWorld
{
    /// <summary>A walkable way from one point to another (the last step is the destination).</summary>
    IReadOnlyList<ZombieStep> Route(float fx, float fz, float fy, float tx, float tz, float ty);
    /// <summary>A walkable point somewhere near (x, z), to shamble to.</summary>
    (float X, float Z) WanderPoint(float x, float z, Random random);
    /// <summary>Can someone at a see someone at b (no walls between)?</summary>
    bool CanSee(float ax, float az, float ay, float bx, float bz, float by);
    /// <summary>Is that door shut? Zombies can't open doors: they batter them down.</summary>
    bool DoorClosed(int door);
}

public enum ZombieState { Wander, Chase, Attack, Bash, Down, Dead }

public enum ZombieIntentKind { Bite, Bash }

/// <summary>Something a zombie does that the layer carries out: bite <see cref="Target"/>, or batter <see cref="Door"/>.</summary>
public readonly record struct ZombieIntent(ZombieIntentKind Kind, long Target = 0, int Door = -1);

/// <summary>
/// One zombie, as pure logic (positions on the ground, plus a height for floors), run by the server and unit-tested.
///
/// - <b>Wander</b>: shambles from point to point, pausing.
/// - <b>Chase</b>: after the nearest person it sees (sight depends on daylight and lit flashlights), or toward a
///   noise it heard; it keeps after them for a while once they're out of sight, then searches where they were.
/// - <b>Attack</b>: bites whoever is within reach. <b>Bash</b>: a shut door in the way gets battered until it gives.
/// - <b>Down</b>: knocked down by a blunt blow, it gets back up. <b>Dead</b>: destroyed (a headshot, or worn down).
/// </summary>
public sealed class ZombieBrain
{
    public int Id { get; }
    public float X { get; private set; }
    public float Z { get; private set; }
    public float Y { get; private set; }
    public float Yaw { get; private set; }
    public bool Moving { get; private set; }
    public int Health { get; private set; } = ZombieRules.MaxHealth;
    public ZombieState State { get; private set; } = ZombieState.Wander;
    public bool IsDown => State is ZombieState.Down or ZombieState.Dead;
    /// <summary>Who it's after (0: nobody; it may still be heading for a noise).</summary>
    public long Target { get; private set; }

    private readonly Random _random;
    private readonly Queue<ZombieStep> _route = new();
    private double _clock, _until, _nextPlan, _nextBite, _nextBash, _lastSeen = -1000, _frozenUntil = -1;
    private (float X, float Z, float Y)? _goal;
    private (float X, float Z) _plannedFor = (float.NaN, float.NaN); // the goal the current route leads to
    private readonly Queue<ZombieIntent> _intents = new();

    /// <summary>Seconds it keeps after someone it has lost sight of.</summary>
    public const float GiveUpSeconds = 8;

    public ZombieBrain(int id, float x, float z, int seed, float y = 0)
    {
        (Id, X, Z, Y) = (id, x, z, y);
        _random = new Random(unchecked(seed * 7919 + id * 104729 + 5));
        Yaw = (float)(_random.NextDouble() * Math.PI * 2);
        _until = _random.NextDouble() * 3;
    }

    public bool TryTakeIntent(out ZombieIntent intent) => _intents.TryDequeue(out intent);

    public bool Frozen => _clock < _frozenUntil;

    public void Freeze(float seconds)
    {
        if (State != ZombieState.Dead) _frozenUntil = Math.Max(_frozenUntil, _clock + seconds);
    }

    /// <summary>A hit (see <see cref="ZombieRules.Hit"/>): hurt, maybe knocked down or destroyed. Returns true if destroyed.</summary>
    public bool TakeHit(ZombieHit hit, float fromX, float fromZ)
    {
        if (State == ZombieState.Dead) return false;
        Health = Math.Max(0, Health - hit.Damage);
        if (hit.Destroyed || Health == 0)
        {
            State = ZombieState.Dead;
            Moving = false;
            _route.Clear();
            return true;
        }
        _goal ??= (fromX, fromZ, Y); // it comes for whoever hit it
        if (hit.KnockdownSeconds > 0)
        {
            State = ZombieState.Down;
            Moving = false;
            _route.Clear();
            _until = _clock + hit.KnockdownSeconds;
        }
        return false;
    }

    /// <summary>What it sees and hears (a few times a second): picks who (or what) to go after.</summary>
    public void Sense(IReadOnlyList<Prey> prey, IReadOnlyList<Heard> heard, float daylight, IZombieWorld world)
    {
        if (IsDown) return;
        Prey? best = null;
        var bestD = float.MaxValue;
        foreach (var p in prey)
        {
            var d = Dist(p.X, p.Z, X, Z);
            if (d > ZombieRules.SightRange(daylight, p.Flashlight) || MathF.Abs(p.Y - Y) > 2.5f && d > 3) continue;
            if (d < bestD && world.CanSee(X, Z, Y, p.X, p.Z, p.Y)) (best, bestD) = (p, d);
        }
        if (best is { } b)
        {
            if (Target != b.Id) _nextPlan = 0; // someone new: plan the way at once
            Target = b.Id;
            _goal = (b.X, b.Z, b.Y);
            _lastSeen = _clock;
            if (State is ZombieState.Wander) State = ZombieState.Chase;
            return;
        }
        if (Target != 0 && _clock - _lastSeen > GiveUpSeconds) Target = 0; // lost them: go where they were seen
        if (Target == 0)
            foreach (var n in heard)
                if (Dist(n.X, n.Z, X, Z) < n.Radius * ZombieRules.HearingFactor)
                {
                    _goal = (n.X, n.Z, 0);
                    _nextPlan = 0;
                    if (State is ZombieState.Wander) State = ZombieState.Chase;
                    break;
                }
    }

    /// <param name="prey">Where everyone is now (to bite whoever is in reach, and to follow the target).</param>
    public void Update(float seconds, IZombieWorld world, IReadOnlyList<Prey> prey)
    {
        _clock += seconds;
        if (State == ZombieState.Dead) return;
        if (State == ZombieState.Down)
        {
            if (_clock < _until) return;
            State = _goal != null ? ZombieState.Chase : ZombieState.Wander;
        }
        if (Frozen)
        {
            Moving = false;
            return;
        }

        // Someone in reach: bite.
        foreach (var p in prey)
            if (Dist(p.X, p.Z, X, Z) < ZombieRules.BiteRange && MathF.Abs(p.Y - Y) < 1.2f)
            {
                State = ZombieState.Attack;
                Moving = false;
                Face(p.X - X, p.Z - Z);
                if (_clock >= _nextBite)
                {
                    _nextBite = _clock + ZombieRules.BiteSeconds;
                    _intents.Enqueue(new ZombieIntent(ZombieIntentKind.Bite, p.Id));
                }
                return;
            }
        if (State == ZombieState.Attack) State = _goal != null ? ZombieState.Chase : ZombieState.Wander;

        if (State == ZombieState.Wander)
        {
            if (_route.Count == 0 && _clock >= _until)
            {
                var (wx, wz) = world.WanderPoint(X, Z, _random);
                Plan(world, wx, wz, 0);
                _until = _clock + 2 + _random.NextDouble() * 4;
            }
            Walk(seconds, world, ZombieRules.WanderSpeed);
            return;
        }

        // Chasing (someone, or a noise): follow the target where they are now, replanning now and then.
        if (Target != 0 && prey.FirstOrDefault(p => p.Id == Target) is { Id: not 0 } t && _clock - _lastSeen < 1)
            _goal = (t.X, t.Z, t.Y);
        if (_goal is not { } g)
        {
            State = ZombieState.Wander;
            return;
        }
        // Replan when the goal has moved (a moving target), or the way ran out; not otherwise (a fresh route starts
        // from the middle of the street, so replanning all the time would keep it walking back and forth).
        var moved = float.IsNaN(_plannedFor.X) || Dist(g.X, g.Z, _plannedFor.X, _plannedFor.Z) > 1.5f;
        if (_route.Count == 0 || _clock >= _nextPlan && moved)
        {
            _nextPlan = _clock + 1;
            if (Dist(g.X, g.Z, X, Z) < 1 && MathF.Abs(g.Y - Y) < 1)
            {
                // Got there and nobody's here: back to wandering.
                _goal = null;
                Target = 0;
                State = ZombieState.Wander;
                _until = _clock + 1;
                return;
            }
            Plan(world, g.X, g.Z, g.Y);
        }
        Walk(seconds, world, ZombieRules.ChaseSpeed);
    }

    private void Plan(IZombieWorld world, float x, float z, float y)
    {
        _plannedFor = (x, z);
        _route.Clear();
        foreach (var s in world.Route(X, Z, Y, x, z, y)) _route.Enqueue(s);
    }

    private void Walk(float seconds, IZombieWorld world, float speed)
    {
        Moving = false;
        var step = speed * seconds;
        while (step > 1e-5f && _route.Count > 0)
        {
            var next = _route.Peek();
            if (next.Door >= 0 && world.DoorClosed(next.Door))
            {
                // A shut door: batter it until it gives.
                if (State != ZombieState.Wander)
                {
                    State = ZombieState.Bash;
                    Face(next.X - X, next.Z - Z);
                    if (_clock >= _nextBash)
                    {
                        _nextBash = _clock + 1.2;
                        _intents.Enqueue(new ZombieIntent(ZombieIntentKind.Bash, Door: next.Door));
                    }
                }
                else _route.Clear(); // just shambling about: go somewhere else
                return;
            }
            if (State == ZombieState.Bash) State = ZombieState.Chase;
            float dx = next.X - X, dz = next.Z - Z;
            var d = MathF.Sqrt(dx * dx + dz * dz);
            if (d < 1e-4f)
            {
                Y = next.Y;
                _route.Dequeue();
                continue;
            }
            Face(dx, dz);
            var move = MathF.Min(step, d);
            X += dx / d * move;
            Z += dz / d * move;
            Y += (next.Y - Y) * move / d;
            step -= move;
            Moving = true;
            if (move >= d)
            {
                Y = next.Y;
                _route.Dequeue();
            }
        }
    }

    /// <summary>Pushed (by the crowd keeping zombies apart): moves without turning.</summary>
    public void Nudge(float dx, float dz)
    {
        if (State == ZombieState.Dead) return;
        X += dx;
        Z += dz;
    }

    private void Face(float dx, float dz)
    {
        if (dx * dx + dz * dz > 1e-6f) Yaw = MathF.Atan2(-dx, -dz);
    }

    private static float Dist(float ax, float az, float bx, float bz) =>
        MathF.Sqrt((ax - bx) * (ax - bx) + (az - bz) * (az - bz));
}
