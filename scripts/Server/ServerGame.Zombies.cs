using System.Linq;
using ExploreTogether.City;
using ExploreTogether.Core.Audio;
using ExploreTogether.Core.City;
using ExploreTogether.Net;
using ExploreTogether.Zombies;
using Godot;

namespace ExploreTogether.Server;

/// <summary>
/// Zombies (optional): a city made with zombies (the City Gate's "Zombies" count, or --city-zombies N for the default)
/// gets a <see cref="ZombieLayer"/>. This file is the only place the server knows about them: it hands the layer what
/// it needs from the city (see <see cref="ZombieContext"/>) and carries out what it reports (bites, risen bodies).
/// A city made without zombies never gets a layer, so nothing here runs. Dev: --dev-zombies-near-start puts them
/// near the gate.
/// </summary>
public partial class ServerGame
{
    private static readonly int CityZombies = CmdArgs.GetInt("city-zombies", 0);
    private static readonly bool DevZombiesNearStart = CmdArgs.Has("dev-zombies-near-start");
    public const int MaxZombies = 200;

    private void AddZombies(ServerInstance city, int count)
    {
        var layer = new ZombieLayer { Name = "Zombies" };
        city.Nodes.Container.AddChild(layer);
        city.Zombies = layer;
        layer.Init(new ZombieContext
        {
            City = city.City!,
            Paths = new CityPaths(city.City!),
            Doors = city.Doors!,
            Sight = (ax, az, bx, bz, y) => ObstacleBetween(city, ax, az, bx, bz, y),
            Daylight = () => CityLighting.Daylight(city.Simulation!.Clock.HourOfDay),
            Players = () => city.Players
                .Where(kv => _sessions.TryGetValue(kv.Key, out var s) && s.Character != null && !s.Health.IsDead)
                .Select(kv => (kv.Key, kv.Value.GlobalPosition, _sessions[kv.Key].FlashlightOn)),
            Residents = () => city.Npcs.Values,
            Parent = city.Nodes.Npcs,
            Spawned = zombie =>
            {
                city.LayerSyncs.Add(zombie.Sync);
                foreach (var peer in city.Players.Keys) zombie.Sync.SetVisibilityFor((int)peer, true);
            },
            Noise = (x, z, radius, cause) => EmitNoise(city, new Core.City.Noise(x, z, radius, cause, Severity.Crime)),
            Sound = (id, at) => PlaySound(city, id, at),
            Shot = (from, to) => { foreach (var peer in city.Players.Keys) _session.SendShot(peer, from, to); },
        });
        // They hear everything the city hears (their own noises included: a battered door draws more of them).
        city.NoiseEmitted += noise => layer.Hear(noise.X, noise.Z, noise.Radius);
        layer.PlayerBitten += (peer, zombie, damage) => ZombieBitesPlayer(city, peer, zombie, damage);
        layer.ResidentRisen += (corpse, _) => RemoveResident(city, corpse);
        layer.Destroyed += (zombie, by, head) =>
        {
            if (by != 0 && _sessions.TryGetValue(by, out var killer))
                GD.Print($"[server] '{killer.Character?.Name}' destroys zombie {zombie.Brain!.Id}{(head ? " with a headshot" : "")}");
        };
        // Away from the gate (where players arrive, at the middle of the start junction), or (dev) close to it.
        if (DevZombiesNearStart) layer.SpawnAround(count, 0, 0, awayFrom: 12, within: 30);
        else layer.SpawnAround(count, 0, 0, awayFrom: 40);
        GD.Print($"[server] city {city.Id}: {count} zombie(s) added");
    }

    private void ZombieBitesPlayer(ServerInstance city, long peer, ZombieNpc zombie, int damage)
    {
        if (!_sessions.TryGetValue(peer, out var s) || s.Character == null || s.Health.IsDead) return;
        if (!city.Players.TryGetValue(peer, out var player)) return;
        var died = s.Health.Damage(damage);
        SyncHealth(s);
        PlaySound(city, died ? SoundId.PlayerDeath : SoundId.PlayerHurt, player.GlobalPosition + Vector3.Up);
        GD.Print($"[server] zombie {zombie.Brain!.Id} bites '{s.Character.Name}' for {damage} ({s.Health.Current} left)");
        if (!died) return;
        NotifyInstance(city, $"{s.Character.Name} was eaten by a zombie.", except: s.Peer);
        HandlePlayerDeath(s, city);
    }

    /// <summary>A resident's body goes (it got up as a zombie): out of the city, and out of anyone's hands.</summary>
    private void RemoveResident(ServerInstance city, CityNpc npc)
    {
        foreach (var s in _sessions.Values)
        {
            if (s.LootingNpc == npc.Name) s.LootingNpc = null;
            if (s.DraggingNpc == npc.Name)
            {
                s.DraggingNpc = null;
                _session.SendDragState(s.Peer, "");
            }
        }
        city.Npcs.Remove(npc.Name);
        city.Simulation?.Remove(npc);
        npc.QueueFree();
    }
}
