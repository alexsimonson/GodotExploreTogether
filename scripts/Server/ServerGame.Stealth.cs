using System;
using System.Collections.Generic;
using System.Linq;
using ExploreTogether.City;
using ExploreTogether.Core.City;
using ExploreTogether.Core.Combat;
using ExploreTogether.Core.Items;
using ExploreTogether.Player;
using Godot;
using Noise = ExploreTogether.Core.City.Noise;

namespace ExploreTogether.Server;

/// <summary>
/// Stealth on the server (rules: Core.City.Stealth; reactions: CityNpcBrain.Reactions):
/// - Five times a second, each resident perceives: players in their view cone, within a sight range that depends on
///   the light where the player stands (lamps, lit windows, day or night, alleys), with a clear line of sight;
///   bodies and damaged doors they can see; and noises within earshot (half as far through walls).
/// - What a player is doing (<see cref="Conduct"/>) comes from the server's own state: where they stand (trespass in
///   a yard or a building), what they hold, crouching, running, dragging or holding someone, and recent violence
///   or break-ins.
/// - What residents decide to do that touches the world (reporting to the police, shooting, hitting, grabbing,
///   arresting, helping someone up, screaming) comes back as intents, carried out here after every step.
/// - Each player's HUD shows how aware the city is of them (hidden, noticed, suspicious, wanted).
/// </summary>
public partial class ServerGame
{
    private readonly Percepts _percepts = new();

    /// <summary>Wires up a new city's stealth: lights, sight lines for the residents, perception and intents.</summary>
    private void SetUpCityStealth(ServerInstance city)
    {
        city.Lighting = new CityLighting(city.City!);
        foreach (var npc in city.Npcs.Values)
        {
            npc.Brain.Obstacles = (ax, az, bx, bz) => ObstacleBetween(city, ax, az, bx, bz);
            RefreshArms(npc);
        }
        city.Simulation!.Perception += dt => PerceiveCity(city, dt);
        city.Simulation.Stepped += () => RunIntents(city);
    }

    /// <summary>What lies between two ground points: a wall (or closed door) at head height, something low (a fence), or nothing.</summary>
    private static CityNav.Obstacle ObstacleBetween(ServerInstance city, float ax, float az, float bx, float bz)
    {
        var space = city.Nodes.Players.GetWorld3D().DirectSpaceState;
        bool Blocked(float y) => space.IntersectRay(PhysicsRayQueryParameters3D.Create(
            new Vector3(ax, y, az), new Vector3(bx, y, bz), collisionMask: 1)).Count > 0;
        if (Blocked(1.5f)) return CityNav.Obstacle.Wall;
        return Blocked(0.5f) ? CityNav.Obstacle.Low : CityNav.Obstacle.None;
    }

    /// <summary>Tells a resident's brain what they can fight with (a gun with rounds to fire, a melee weapon, or fists).</summary>
    private static void RefreshArms(CityNpc npc)
    {
        var weapon = npc.Gear.EquippedWeapon;
        var gun = weapon is { Class: ItemClass.Gun }
                  && (GunRules.Loaded(npc.Gear.WeaponSlot) > 0 || GunRules.Reserve(npc.Inventory, weapon) > 0);
        npc.Brain.HasGun = gun;
        npc.Brain.HasMeleeWeapon = weapon != null && !gun;
    }

    private float LightAt(ServerInstance city, float x, float z) =>
        city.Lighting!.LightAt(x, z, city.Simulation!.Clock.HourOfDay, parcel => city.Doors![parcel].Lit);

    /// <summary>A sound in a city, heard on the next perception tick.</summary>
    private void EmitNoise(ServerInstance city, Noise noise)
    {
        if (city.Kind != Net.InstanceKinds.City) return;
        city.Noises.Add(noise);
        if (noise.Severity >= Severity.Crime || noise.Radius >= Stealth.KickRadius)
            GD.Print($"[server] noise in city {city.Id}: {noise.Cause} at ({noise.X:0}, {noise.Z:0}), heard within {noise.Radius:0} m");
    }

    private Conduct ConductOf(PeerSession s, PlayerCharacter player, ServerInstance city, float hour)
    {
        var c = Conduct.None;
        var pos = player.NetPosition;
        if (city.City!.ParcelAt(pos.X, pos.Z) is { } parcel)
        {
            var open = parcel.IsPublic && Stealth.ShopOpen(hour) && !city.Doors!.IsLocked(parcel.Id);
            if (!open) c |= parcel.Footprint.Contains(pos.X, pos.Z) ? Conduct.TrespassInside : Conduct.TrespassYard;
        }
        switch (s.Gear?.EquippedWeapon?.Class)
        {
            case ItemClass.Gun: c |= Conduct.GunOut; break;
            case ItemClass.Melee or ItemClass.Magic: c |= Conduct.Armed; break;
        }
        if (player.NetCrouching) c |= Conduct.Crouching;
        if (s.SampledSpeed > 5.5f) c |= Conduct.Running;
        if (s.DraggingNpc != null) c |= Conduct.Dragging;
        if (s.HoldingNpc != null) c |= Conduct.Holding;
        if (Now - s.LastViolentAt < 2.5) c |= Conduct.Violent;
        if (Now - s.LastBreakingAt < 3 || s.Picking != null) c |= Conduct.Breaking;
        return c;
    }

    /// <summary>Dev (--dev-oblivious): residents never notice players (scenarios that test something else).</summary>
    private static readonly bool DevOblivious = CmdArgs.Has("dev-oblivious");

    private void PerceiveCity(ServerInstance city, float dt)
    {
        if (city.Simulation is not { } sim || city.City is not { } layout) return;
        var hour = sim.Clock.HourOfDay;
        var space = city.Nodes.Players.GetWorld3D().DirectSpaceState;
        var now = Now;

        // The players as anyone would see them.
        var players = new List<(long Peer, PeerSession S, PlayerCharacter P, Conduct C, float Light, int Parcel)>();
        foreach (var (peer, player) in city.Players)
        {
            if (!_sessions.TryGetValue(peer, out var s) || s.Character == null || s.Health.IsDead) continue;
            var pos = player.NetPosition;
            if (now - s.LastSampleAt >= 0.2)
            {
                var flat = new Vector2(pos.X - s.LastSample.X, pos.Z - s.LastSample.Z).Length();
                s.SampledSpeed = s.LastSampleAt > 0 ? flat / (float)(now - s.LastSampleAt) : 0;
                (s.LastSample, s.LastSampleAt) = (pos, now);
            }
            var c = ConductOf(s, player, city, hour);
            players.Add((peer, s, player, c, LightAt(city, pos.X, pos.Z), layout.ParcelAt(pos.X, pos.Z)?.Id ?? -1));
            // Footsteps: running anywhere, or walking about inside a building (not crouched).
            var inside = (c & Conduct.TrespassInside) != 0;
            if (!player.NetCrouching && now - s.LastFootstepAt > 0.6 && (s.SampledSpeed > 5.5f || inside && s.SampledSpeed > 1))
            {
                s.LastFootstepAt = now;
                city.Noises.Add(new Noise(pos.X, pos.Z, s.SampledSpeed > 5.5f ? Stealth.FootstepsRadius : 3.5f, "footsteps", Severity.Suspicious, peer));
            }
        }
        var bodies = city.Npcs.Values.Where(n => n.IsDown).ToList();
        var damaged = Enumerable.Range(0, city.Doors!.Count).Where(city.Doors.IsDamaged).ToList();

        foreach (var npc in city.Npcs.Values)
        {
            var brain = npc.Brain;
            if (npc.IsDown || brain.Held) continue;
            _percepts.Clear();
            var me = new Vector3(brain.X, 0, brain.Z);
            var head = me + new Vector3(0, 1.6f, 0);
            var meInside = InsideBuilding(layout, brain.X, brain.Z);
            foreach (var n in city.Noises)
                if (Stealth.Hears(n, brain.X, brain.Z, meInside != InsideBuilding(layout, n.X, n.Z)))
                    _percepts.Noises.Add(n);

            var alert = brain.Mood >= Mood.Suspicious;
            bool Sees(Vector3 target, float range)
            {
                var flat = new Vector2(target.X - me.X, target.Z - me.Z).Length();
                if (flat > range || !Stealth.InView(brain.Yaw, me.X, me.Z, target.X, target.Z, alert)) return false;
                var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(head, target, collisionMask: 1));
                return hit.Count == 0;
            }

            foreach (var (peer, s, player, c, light, parcel) in players)
            {
                if (DevOblivious) break;
                var running = (c & Conduct.Running) != 0;
                var range = Stealth.SightRange(light, player.NetCrouching, running);
                var chest = player.NetPosition + new Vector3(0, player.NetCrouching ? 0.7f : 1.2f, 0);
                if (!Sees(chest, range)) continue;
                var d = new Vector2(chest.X - me.X, chest.Z - me.Z).Length();
                _percepts.Players.Add(new PlayerSighting(peer, s.Character!.Name, player.NetPosition.X, player.NetPosition.Z, c,
                    parcel, Stealth.Clarity(d, range)));
            }
            foreach (var body in bodies)
            {
                if (body == npc) continue;
                var at = body.Position + new Vector3(0, 0.3f, 0);
                if (Sees(at, Stealth.SightRange(LightAt(city, at.X, at.Z), false, false) * 0.8f))
                    _percepts.Bodies.Add(new BodySighting(body.NetId, at.X, at.Z, body.NetDead));
            }
            foreach (var parcel in damaged)
            {
                var (dx, dz) = layout.Parcels[parcel].Door;
                var at = new Vector3(dx, 1.2f, dz);
                var flat = new Vector2(dx - me.X, dz - me.Z).Length();
                if (flat > 18 || !Stealth.InView(brain.Yaw, me.X, me.Z, dx, dz, alert)) continue;
                var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(head, at, collisionMask: 1));
                if (hit.Count == 0 || hit["collider"].AsGodotObject() is CityDoor { ParcelId: var p } && p == parcel
                    || hit["collider"].AsGodotObject() is CityDeadbolt { Door.ParcelId: var q } && q == parcel)
                    _percepts.DamagedDoors.Add(parcel);
            }
            brain.Perceive(_percepts, dt, hour);
        }
        city.Noises.Clear();
        city.RecentReports.RemoveAll(r => now - r.At > 20);
        foreach (var (peer, s, _, _, light, _) in players) UpdateAlert(city, peer, s);
    }

    private static bool InsideBuilding(CityLayout layout, float x, float z) =>
        layout.ParcelAt(x, z) is { } p && p.Footprint.Contains(x, z);

    /// <summary>The HUD's awareness meter: 0 unseen, 1 noticed, 2 suspicious / being searched for, 3 wanted (arrest or attack).</summary>
    private void UpdateAlert(ServerInstance city, long peer, PeerSession s)
    {
        var level = 0;
        foreach (var npc in city.Npcs.Values)
        {
            if (npc.IsDown) continue;
            var (sus, engaged, hostile) = npc.Brain.SuspicionOf(peer);
            var l = hostile ? 3 : engaged || sus >= Stealth.Suspicious ? 2 : sus >= Stealth.Curious ? 1 : 0;
            level = Math.Max(level, l);
        }
        if (s.HeldByNpc != null) level = 3;
        if (level == s.AlertSent) return;
        s.AlertSent = level;
        _session.SendAlert(peer, level, level switch
        {
            1 => "Noticed",
            2 => "Suspicious",
            3 => "Wanted",
            _ => "Unseen",
        });
    }

    // ------------------------------------------------------------------ intents

    private void RunIntents(ServerInstance city)
    {
        foreach (var npc in city.Npcs.Values)
            while (npc.Brain.TryTakeIntent(out var intent))
                RunIntent(city, npc, intent);
    }

    private void RunIntent(ServerInstance city, CityNpc npc, NpcIntent intent)
    {
        switch (intent.Kind)
        {
            case NpcIntentKind.Noise:
                EmitNoise(city, new Noise(intent.X, intent.Z, intent.Radius, intent.Text, intent.Severity));
                break;
            case NpcIntentKind.Report:
                ReportToPolice(city, npc, intent);
                break;
            case NpcIntentKind.Shoot:
                NpcShoot(city, npc, intent.Player);
                break;
            case NpcIntentKind.Strike:
                NpcStrike(city, npc, intent.Player);
                break;
            case NpcIntentKind.Grab:
                NpcGrab(city, npc, intent.Player);
                break;
            case NpcIntentKind.Release:
                NpcRelease(npc, "lets go of");
                break;
            case NpcIntentKind.Arrest:
                ArrestPlayer(city, npc, intent.Player);
                break;
            case NpcIntentKind.Revive:
                if (city.Npcs.Values.FirstOrDefault(n => n.NetId == intent.NpcId && n.NetUnconscious) is { } down)
                {
                    GD.Print($"[server] {npc.NetName} helps {down.NetName} up");
                    down.WakeNow();
                }
                break;
        }
    }

    /// <summary>
    /// Someone tells the police. Every officer hears the description (they'll recognize the player); the nearest free
    /// one (two for violence) goes to look. Reports of the same thing within 20 s and 20 m only update the description.
    /// </summary>
    private void ReportToPolice(ServerInstance city, CityNpc reporter, NpcIntent report)
    {
        var now = Now;
        var duplicate = city.RecentReports.Any(r => now - r.At < 20 && (r.Player == report.Player || report.Player == 0)
                                                   && new Vector2(r.X - report.X, r.Z - report.Z).Length() < 20);
        var responders = duplicate ? 0 : report.Severity >= Severity.Violent ? 2 : 1;
        var sent = new List<string>();
        var police = city.Npcs.Values.Where(n => n.Role == NpcRole.Police && !n.IsDown)
            .OrderBy(n => new Vector2(n.Position.X - report.X, n.Position.Z - report.Z).Length());
        foreach (var officer in police)
            if (officer.Brain.Dispatch(report.X, report.Z, report.Player, report.Severity, respond: sent.Count < responders))
                sent.Add(officer.NetName);
        // Bystanders who were told (or saw it) recognize them too: tell everyone nearby.
        if (report.Player != 0)
            foreach (var other in city.Npcs.Values.Where(n => n.Role != NpcRole.Police && !n.IsDown
                         && new Vector2(n.Position.X - report.X, n.Position.Z - report.Z).Length() < 25))
                other.Brain.Dispatch(report.X, report.Z, report.Player, report.Severity, respond: false);
        if (duplicate) return;
        city.RecentReports.Add((now, report.X, report.Z, report.Player));
        var who = report.Player != 0 && _sessions.TryGetValue(report.Player, out var s) ? $" ('{s.Character?.Name}')" : "";
        GD.Print($"[server] {reporter.NetName} ({reporter.Role}) reports {report.Text}{who} at ({report.X:0}, {report.Z:0}), " +
                 $"severity {report.Severity}: {(sent.Count > 0 ? string.Join(", ", sent) + " responding" : "no police free")}");
        if (report.Player != 0 && city.Players.ContainsKey(report.Player))
            _session.SendNotice(report.Player, sent.Count > 0 ? "Someone called the police!" : "Someone's calling for the police!");
    }

    /// <summary>Damage dealt to players by residents (players have 100 health).</summary>
    private static int NpcDamage(ItemDef? weapon, float distance) => weapon switch
    {
        { Class: ItemClass.Gun, Breaches: true } => distance < 12 ? 45 : 30,
        { Class: ItemClass.Gun } => 18,
        { KnocksOut: true } => 12,
        { Class: ItemClass.Tool } => 14,
        { Class: ItemClass.Melee } => 15,
        _ => 6, // fists
    };

    /// <summary>A resident fires at a player: they may need to reload; the hit is rolled (distance, light, movement) and walls stop it.</summary>
    private void NpcShoot(ServerInstance city, CityNpc npc, long peer)
    {
        if (npc.Gear.EquippedWeapon is not { Class: ItemClass.Gun } gun) { RefreshArms(npc); return; }
        if (!city.Players.TryGetValue(peer, out var player) || !_sessions.TryGetValue(peer, out var s) || s.Health.IsDead) return;
        var slot = npc.Gear.WeaponSlot;
        if (GunRules.Loaded(slot) == 0)
        {
            var loaded = GunRules.Reload(slot, gun, npc.Inventory);
            if (loaded > 0) GD.Print($"[server] {npc.NetName} reloads ({loaded})");
            RefreshArms(npc); // none left: they fight with it as a club
            return;
        }
        GunRules.TryFire(slot);
        var from = npc.Position + new Vector3(0, 1.45f, 0) + new Vector3(-Mathf.Sin(npc.NetYaw), 0, -Mathf.Cos(npc.NetYaw)) * 0.4f;
        var target = player.NetPosition + new Vector3(0, player.NetCrouching ? 0.7f : 1.2f, 0);
        var d = from.DistanceTo(target);
        var light = LightAt(city, target.X, target.Z);
        var chance = Mathf.Clamp(0.85f - d * 0.018f - (1 - light) * 0.3f - (s.SampledSpeed > 5.5f ? 0.15f : 0)
                                 - (player.NetCrouching ? 0.05f : 0) + (gun.Breaches && d < 8 ? 0.1f : 0), 0.1f, 0.92f);
        var hitPlayer = _combatRandom.NextDouble() < chance;
        var aim = hitPlayer ? target : target + new Vector3((float)_combatRandom.NextDouble() - 0.5f, (float)_combatRandom.NextDouble() - 0.3f, (float)_combatRandom.NextDouble() - 0.5f) * 2.5f;
        var end = from + (aim - from).Normalized() * Mathf.Max(d + 6, 10);
        var wall = player.GetWorld3D().DirectSpaceState.IntersectRay(PhysicsRayQueryParameters3D.Create(from, aim, collisionMask: 1));
        if (wall.Count > 0)
        {
            hitPlayer = false;
            end = (Vector3)wall["position"];
        }
        else if (hitPlayer) end = target;
        foreach (var other in city.Players.Keys) _session.SendShot(other, from, end);
        EmitNoise(city, new Noise(from.X, from.Z, gun.Breaches ? Stealth.ShotgunRadius : Stealth.GunshotRadius, "gunshot", Severity.Violent));
        var damage = NpcDamage(gun, d);
        GD.Print($"[server] {npc.NetName} ({npc.Role}) fires {gun.Name} at '{s.Character!.Name}' from {d:0.0} m: " +
                 (hitPlayer ? $"hit for {damage}" : "miss") + $" ({chance:P0})");
        if (hitPlayer) HurtPlayerByResident(city, s, npc, damage);
    }

    /// <summary>A resident hits a player within arm's reach (fists, a club, a blade, or a gun's butt).</summary>
    private void NpcStrike(ServerInstance city, CityNpc npc, long peer)
    {
        if (!city.Players.TryGetValue(peer, out var player) || !_sessions.TryGetValue(peer, out var s) || s.Health.IsDead) return;
        var d = new Vector2(player.NetPosition.X - npc.Position.X, player.NetPosition.Z - npc.Position.Z).Length();
        if (d > 2.3f) return;
        var weapon = npc.Gear.EquippedWeapon;
        var damage = weapon is { Class: ItemClass.Gun } ? 8 : NpcDamage(weapon, d);
        EmitNoise(city, new Noise(npc.Position.X, npc.Position.Z, Stealth.FightRadius, "fight", Severity.Violent));
        GD.Print($"[server] {npc.NetName} ({npc.Role}) hits '{s.Character!.Name}' with {weapon?.Name ?? "fists"} for {damage}");
        HurtPlayerByResident(city, s, npc, damage);
    }

    private void HurtPlayerByResident(ServerInstance city, PeerSession s, CityNpc npc, int damage)
    {
        var died = s.Health.Damage(damage);
        SyncHealth(s);
        if (!died) return;
        GD.Print($"[server] '{s.Character!.Name}' was killed by {npc.NetName} in city {city.Id}");
        NotifyInstance(city, $"{s.Character.Name} was killed by {npc.NetName}.", except: s.Peer);
        OnLeavingCity(s, city);
        HandlePlayerDeath(s, city);
    }

    /// <summary>A player leaves a city (or dies, or is arrested): let go of anyone they hold or who holds them, and be forgotten.</summary>
    private void OnLeavingCity(PeerSession s, ServerInstance city, bool connected = true)
    {
        s.Picking = null;
        if (s.HoldingNpc != null) LetGoOfResident(s, escaped: false, notify: false);
        if (s.HeldByNpc != null && city.Npcs.TryGetValue(s.HeldByNpc, out var holder)) NpcRelease(holder, "loses hold of", notify: false);
        s.HeldByNpc = null;
        foreach (var npc in city.Npcs.Values) npc.Brain.ForgetPlayer(s.Peer);
        s.AlertSent = 0;
        if (!connected) return;
        _session.SendAlert(s.Peer, 0, "");
        _session.SendGrabState(s.Peer, "", "");
        _session.SendProgress(s.Peer, "", 0);
    }
}
