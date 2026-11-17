using System;
using System.Collections.Generic;
using System.Linq;
using ExploreTogether.City;
using ExploreTogether.Core.City;
using ExploreTogether.Core.Dungeon;
using ExploreTogether.Dungeon;
using ExploreTogether.Enemies;
using ExploreTogether.Gathering;
using ExploreTogether.Items;
using ExploreTogether.Net;
using ExploreTogether.Player;
using Godot;
using Noise = ExploreTogether.Core.City.Noise;

namespace ExploreTogether.Server;

/// <summary>A running instance on the server: its nodes plus which peers are in it.</summary>
public sealed class ServerInstance
{
    public required int Id { get; init; }
    public required string Kind { get; init; }
    public required InstanceNodes Nodes { get; init; }
    /// <summary>Dungeons only: the maze, and its export sent to joining clients.</summary>
    public MazeLayout? Layout { get; init; }
    /// <summary>Cities only.</summary>
    public CityLayout? City { get; init; }
    /// <summary>Cities only: the clock, the residents' brains and their nodes.</summary>
    public CitySimulation? Simulation { get; set; }
    /// <summary>City residents, by node name.</summary>
    public Dictionary<string, CityNpc> Npcs { get; } = new();
    /// <summary>Cities only: every building's front door.</summary>
    public CityDoors? Doors { get; set; }
    /// <summary>Cities only: street lamps and how lit any point is (stealth).</summary>
    public CityLighting? Lighting { get; set; }
    /// <summary>Cities only: sounds made since the last perception tick.</summary>
    public List<Noise> Noises { get; } = new();
    /// <summary>The city's electricity, fuse boxes, light switches and broken lights (see Core.City.CityPower).</summary>
    public CityPower? Power { get; set; }
    /// <summary>The city's zombies, if it was made with any (null otherwise: the layer only exists when used).</summary>
    public Zombies.ZombieLayer? Zombies { get; set; }
    /// <summary>Chairs and stools someone has picked up (furniture ids): gone for the rest of the city's life.</summary>
    public HashSet<int> TakenFurniture { get; } = new();
    /// <summary>Cities only: recent calls to the police (so ten witnesses don't send ten officers).</summary>
    public List<(double At, float X, float Z, long Player)> RecentReports { get; } = new();
    public int[] LayoutData { get; init; } = [];
    public string CreatedBy { get; init; } = "";
    public DateTime CreatedAt { get; } = DateTime.UtcNow;
    /// <summary>When the last player left, for closing empty dungeons and cities. Null while occupied.</summary>
    public DateTime? EmptySince { get; set; }
    public Dictionary<long, PlayerCharacter> Players { get; } = new();
    /// <summary>Items lying in the instance, by node name.</summary>
    public Dictionary<string, ItemPickup> Items { get; } = new();
    public int NextItemNumber { get; set; } = 1;
    /// <summary>Enemies in the instance (alive or corpses), by node name.</summary>
    public Dictionary<string, EnemyCharacter> Enemies { get; } = new();
    public int NextEnemyNumber { get; set; } = 1;
    /// <summary>Gatherable resources (trees), by node name.</summary>
    public Dictionary<string, ResourceNode> Resources { get; } = new();

    // ---- Dungeon run state (unused for the hub) ----
    public long? RunId { get; set; }
    public bool ExitOpen { get; set; }
    /// <summary>Names of characters who escaped through the exit.</summary>
    public List<string> Escaped { get; } = new();

    /// <summary>Every replicated object in the instance; each must be visible exactly to the peers inside.</summary>
    public IEnumerable<MultiplayerSynchronizer> Synchronizers =>
        Players.Values.Select(p => p.Sync)
            .Concat(Items.Values.Select(i => i.Sync))
            .Concat(Enemies.Values.Select(e => e.Sync))
            .Concat(Resources.Values.Select(r => r.Sync))
            .Concat(Npcs.Values.Select(n => n.Sync))
            .Concat(LayerSyncs);

    /// <summary>Synchronizers of things added by layers (zombies...): shown to the peers inside like everything else.</summary>
    public List<MultiplayerSynchronizer> LayerSyncs { get; } = new();

    /// <summary>Every noise made in the instance (a city), for layers that listen (zombies come to gunfire).</summary>
    public event Action<Noise>? NoiseEmitted;
    public void OnNoise(Noise noise) => NoiseEmitted?.Invoke(noise);
}

/// <summary>
/// Server-only. Creates instances, moves players into and out of them, and closes empty dungeons.
///
/// Isolation: every instance is a SubViewport with its own World3D (separate physics and
/// navigation), and a player's synchronizer is visible only to peers in the same instance, so
/// clients are only told about (and spawn) players they share an instance with.
/// </summary>
public sealed class InstanceManager
{
    public const int MaxDungeonSide = 30;
    public const int MaxDungeonLength = 1000;

    private readonly Node _instancesRoot;
    private readonly PackedScene _playerScene = GD.Load<PackedScene>(InstanceFactory.PlayerScenePath);
    private readonly Dictionary<int, ServerInstance> _instances = new();
    private readonly Dictionary<long, ServerInstance> _instanceByPeer = new();
    private int _nextId = 1;

    public InstanceManager(Node instancesRoot) => _instancesRoot = instancesRoot;

    /// <summary>An enemy's attack landed on a player: (instance, enemy, player, damage).</summary>
    public event Action<ServerInstance, EnemyCharacter, PlayerCharacter, int>? PlayerHitByEnemy;
    public event Action<ServerInstance, EnemyCharacter>? EnemyKilled;

    /// <summary>Dev: put the enemies right next to the start instead of mid-maze (for testing fights).</summary>
    private static readonly bool DevEnemiesNearStart = CmdArgs.Has("dev-enemies-near-start");

    public ServerInstance Hub { get; private set; } = null!;

    public IEnumerable<ServerInstance> Dungeons => _instances.Values.Where(i => i.Kind == InstanceKinds.Dungeon);
    public IEnumerable<ServerInstance> Cities => _instances.Values.Where(i => i.Kind == InstanceKinds.City);

    public void CreateHub()
    {
        Hub = Create(InstanceKinds.Hub, null, "");
        // Trees stand at the hub scene's ResourceSpots markers (the original demo scene had a TreeResource too).
        if (Hub.Nodes.Level.GetNodeOrNull("ResourceSpots") is { } spots)
            foreach (var spot in spots.GetChildren())
                if (spot is Node3D marker)
                    SpawnResource(Hub, Core.Gathering.ResourceCatalog.Tree.Kind, marker.Position);
    }

    public ServerInstance CreateDungeon(int width, int height, int length, string createdBy)
    {
        width = Math.Clamp(width, 1, MaxDungeonSide);
        height = Math.Clamp(height, 1, MaxDungeonSide);
        length = Math.Clamp(length, 1, MaxDungeonLength);
        var seed = Random.Shared.Next();
        var layout = MazeLayout.Generate(width, height, length, seed);
        var instance = Create(InstanceKinds.Dungeon, layout, createdBy);
        // The original FillMazeWithItems: the Dungeon Pass plus 20 random items (seeded from the layout).
        foreach (var placement in DungeonContents.Generate(layout, seed ^ 0x5eed))
            SpawnItem(instance, placement.ItemId, DungeonBuilder.ItemPosition(layout, placement), placement.Count);

        // Trees, as in FillMazeWithItems. Placed before the navigation bake so enemies walk around them.
        var treeNodes = DungeonContents.TreeNodes(layout, seed ^ 0x73ee);
        for (int i = 0; i < treeNodes.Count; i++)
            SpawnResource(instance, Core.Gathering.ResourceCatalog.Tree.Kind,
                DungeonBuilder.TreePosition(layout, treeNodes[i], DungeonContents.TreeOffset(seed, i)));

        // Enemies walk the navigation mesh baked from the maze.
        var started = DateTime.UtcNow;
        DungeonBuilder.BakeNavigation(instance.Nodes.Level, layout, instance.Resources.Values);
        var enemyNodes = DungeonContents.EnemyNodes(layout, seed ^ 0xe4e);
        for (int i = 0; i < enemyNodes.Count; i++)
        {
            var node = DevEnemiesNearStart ? Math.Min(1 + i / 2, layout.Nodes.Count - 1) : enemyNodes[i];
            SpawnEnemy(instance, DungeonBuilder.EnemyPosition(layout, node, (i % 2) * 0.9f - 0.45f));
        }
        GD.Print($"[server] dungeon {instance.Id}: navigation baked in {(DateTime.UtcNow - started).TotalMilliseconds:0} ms, " +
                 $"{enemyNodes.Count} enemies, {treeNodes.Count} trees");
        GD.Print($"[server] dungeon {instance.Id}: {width}x{height}, length {length}, seed {seed}, " +
                 $"{layout.Nodes.Count} cells (by '{createdBy}')");
        return instance;
    }

    /// <summary>A new greybox city (seed random unless given). Closed like a dungeon once it has been empty a while.</summary>
    /// <param name="favor">Dev: a building design to place wherever it fits (--city-favor NAME).</param>
    /// <param name="people">How many residents (default: about 1.6 per tile).</param>
    /// <param name="startHour">The city clock when it opens (game hours).</param>
    /// <param name="dayMinutes">Real minutes per game day.</param>
    public ServerInstance CreateCity(int width, int height, int? seed, int loopPercent, string createdBy, string? favor = null,
        int? people = null, float startHour = 8, float dayMinutes = 48)
    {
        width = Math.Clamp(width, 1, MaxCitySide);
        height = Math.Clamp(height, 1, MaxCitySide);
        if (favor != null && CityCatalog.Catalog.IndexOf(favor) < 0)
        {
            GD.PushWarning($"[server] --city-favor: no building design named '{favor}' (the scene file name in {CityCatalog.Folder})");
            favor = null;
        }
        var started = DateTime.UtcNow;
        var city = CityLayout.Generate(width, height, seed ?? Random.Shared.Next(), loopPercent, CityCatalog.Catalog, favor);
        var instance = Create(InstanceKinds.City, null, createdBy, city);
        AddResidents(instance, city, people, startHour, dayMinutes);
        var counts = city.Parcels.GroupBy(p => p.Building).Select(g => $"{g.Count()} {g.Key}");
        var custom = city.Parcels.Where(p => !p.Variant.IsGreybox).GroupBy(p => p.Variant.Name).Select(g => $"{g.Count()} {g.Key}").ToList();
        if (custom.Count > 0) counts = counts.Append($"custom: {string.Join(", ", custom)}");
        GD.Print($"[server] city {instance.Id}: {width}x{height}, seed {city.Seed}, loops {city.LoopPercent}%, " +
                 $"{city.Parcels.Count} parcels ({string.Join(", ", counts)}), built in " +
                 $"{(DateTime.UtcNow - started).TotalMilliseconds:0} ms (by '{createdBy}')");
        return instance;
    }

    public const int MaxCitySide = 20;

    /// <summary>Generates the city's residents and starts its clock and simulation.</summary>
    private void AddResidents(ServerInstance instance, CityLayout city, int? people, float startHour, float dayMinutes)
    {
        var population = CityPopulation.Generate(city, city.Seed, people);
        var clock = new CityClock(startHour, 24f / Math.Max(0.1f, dayMinutes * 60f));
        var simulation = new CitySimulation { Name = "CitySimulation" };
        simulation.Init($"city {instance.Id}", clock, population, () => instance.Players.Values.Select(p => p.NetPosition));
        instance.Nodes.Container.AddChild(simulation);
        instance.Simulation = simulation;

        var paths = new CityPaths(city);
        var doors = new CityDoors(city);
        instance.Doors = doors;
        var scene = GD.Load<PackedScene>(InstanceFactory.NpcScenePath);
        foreach (var person in population.People)
        {
            var brain = new CityNpcBrain(person, city, paths, city.Seed, clock.Time);
            var npc = scene.Instantiate<CityNpc>();
            npc.Name = $"Npc{person.Id}";
            npc.NetName = person.Name;
            npc.NetRole = (int)person.Role;
            npc.NetId = person.Id;
            npc.Brain = brain;
            (npc.Inventory, npc.Gear) = NpcLoadout.Generate(person, city, doors, city.Seed);
            brain.HasKeyTo = parcel => doors.HasKey(npc.Inventory, parcel); // their real keys (a body has none)
            simulation.Add(brain, npc); // sets the position before the node enters the tree (and is spawned)
            instance.Nodes.Npcs.AddChild(npc, forceReadableName: true);
            instance.Npcs[npc.Name] = npc;
        }

        // The server's door nodes follow the state too (closed doors stop bullets).
        doors.Changed += parcel =>
        {
            if (instance.Nodes.Level.GetNodeOrNull<CityDoor>($"Door{parcel}") is { } node)
                node.SetState(doors[parcel], animate: false);
        };
        simulation.SetUpDoors(city, doors);
        var roles = population.People.GroupBy(p => p.Role).OrderBy(g => g.Key).Select(g => g.Key switch
        {
            NpcRole.Police => $"{g.Count()} police",
            NpcRole.Security => $"{g.Count()} security guards",
            _ => $"{g.Count()} {g.Key.ToString().ToLowerInvariant()}s",
        });
        GD.Print($"[server] city {instance.Id}: {population.People.Count} people ({string.Join(", ", roles)}), " +
                 $"clock {CityClock.Format(clock.HourOfDay)}, {dayMinutes:0.#} min per day; {simulation.Summary()}");
    }

    private ServerInstance Create(string kind, MazeLayout? layout, string createdBy, CityLayout? city = null)
    {
        var id = _nextId++;
        var data = city?.Export() ?? layout?.Export() ?? [];
        var nodes = InstanceFactory.Build(_instancesRoot, id, kind, data, isServer: true);
        var instance = new ServerInstance
        {
            Id = id, Kind = kind, Nodes = nodes, Layout = layout, City = city, LayoutData = data, CreatedBy = createdBy,
            EmptySince = kind != InstanceKinds.Hub ? DateTime.UtcNow : null,
        };
        _instances[id] = instance;
        GD.Print($"[server] created instance {id} ({kind})");
        return instance;
    }

    public ServerInstance? Get(int id) => _instances.GetValueOrDefault(id);

    /// <summary>
    /// Spawns a player for the peer in the instance. A saved position (e.g. the character's
    /// last hub spot) is used if given and sane; otherwise the instance's spawn point.
    /// </summary>
    public void AddPlayer(ServerInstance instance, long peer, string displayName, Color color,
        Vector3? savedPosition = null, float savedYaw = 0f)
    {
        RemovePeer(peer);

        var player = _playerScene.Instantiate<PlayerCharacter>();
        player.Name = peer.ToString();
        player.DisplayName = displayName;
        player.PlayerColor = color;
        player.NetOutfit = color; // until a shirt says otherwise (see ServerGame.LookOf)

        Vector3 spawn;
        float yaw;
        if (savedPosition is { } sp && sp.Y > -10f && sp.IsFinite())
            (spawn, yaw) = (sp, savedYaw);
        else
            (spawn, yaw) = SpawnPoint(instance, instance.Players.Count);
        spawn = FreeSpotNear(instance, spawn);
        player.Position = spawn;
        player.NetPosition = spawn;
        player.NetYaw = yaw;
        player.Guard = new Core.Net.MoveGuard(spawn.X, spawn.Y, spawn.Z, Time.GetTicksMsec() / 1000.0);
        instance.Nodes.Players.AddChild(player, forceReadableName: true);

        // Visibility (scenes set public_visibility = false): the new peer sees every player and item
        // already here, and everyone here (including the new peer) sees the new player.
        foreach (var sync in instance.Synchronizers)
            sync.SetVisibilityFor((int)peer, true);
        foreach (var otherPeer in instance.Players.Keys)
            player.Sync.SetVisibilityFor((int)otherPeer, true);
        player.Sync.SetVisibilityFor((int)peer, true);

        instance.Players[peer] = player;
        instance.EmptySince = null;
        _instanceByPeer[peer] = instance;
        GD.Print($"[server] peer {peer} ('{displayName}') entered instance {instance.Id}; " +
                 $"{instance.Players.Count} player(s) there");
    }

    /// <summary>The instance and player node for a peer, if the peer is currently spawned somewhere.</summary>
    public (ServerInstance Instance, PlayerCharacter Player)? FindPlayer(long peer) =>
        _instanceByPeer.TryGetValue(peer, out var instance) && instance.Players.TryGetValue(peer, out var player)
            ? (instance, player)
            : null;

    /// <param name="stillConnected">False when the peer has disconnected (then there's no view to revoke).</param>
    public void RemovePeer(long peer, bool stillConnected = true)
    {
        if (!_instanceByPeer.Remove(peer, out var instance)) return;
        if (instance.Players.Remove(peer, out var player))
        {
            // Freeing the node makes the spawner despawn it on every peer that could see it.
            player.GetParent().RemoveChild(player);
            player.QueueFree();
        }
        // And the leaving peer must stop seeing everyone still here (this despawns them on its client),
        // or it would keep receiving their updates from an instance it's no longer in.
        if (stillConnected)
            foreach (var sync in instance.Synchronizers)
                sync.SetVisibilityFor((int)peer, false);
        if (instance.Players.Count == 0 && instance.Kind != InstanceKinds.Hub)
            instance.EmptySince = DateTime.UtcNow;
        GD.Print($"[server] peer {peer} left instance {instance.Id}; {instance.Players.Count} player(s) remain");
    }

    /// <summary>Puts an item into the instance, visible to everyone inside.</summary>
    public ItemPickup SpawnItem(ServerInstance instance, int itemId, Vector3 position, int count = 1, int? data = null,
        long? uid = null)
    {
        var item = GD.Load<PackedScene>(InstanceFactory.ItemScenePath).Instantiate<ItemPickup>();
        item.Name = $"Item{instance.NextItemNumber++}";
        item.ItemId = itemId;
        item.Count = count;
        item.Data = data;
        item.Uid = uid;
        item.Position = position;
        instance.Nodes.Items.AddChild(item, forceReadableName: true);
        instance.Items[item.Name] = item;
        foreach (var peer in instance.Players.Keys)
            item.Sync.SetVisibilityFor((int)peer, true);
        return item;
    }

    /// <summary>Removes an item (despawns it for everyone who could see it).</summary>
    public void RemoveItem(ServerInstance instance, ItemPickup item)
    {
        instance.Items.Remove(item.Name);
        item.GetParent().RemoveChild(item);
        item.QueueFree();
    }

    /// <summary>Puts a gatherable resource (e.g. a tree) into the instance, visible to everyone inside.</summary>
    public ResourceNode SpawnResource(ServerInstance instance, string kind, Vector3 position)
    {
        var resource = GD.Load<PackedScene>(InstanceFactory.ResourceScenePath).Instantiate<ResourceNode>();
        resource.Name = $"Resource{instance.Resources.Count + 1}";
        resource.Kind = kind;
        resource.Position = position;
        instance.Nodes.Resources.AddChild(resource, forceReadableName: true);
        instance.Resources[resource.Name] = resource;
        foreach (var peer in instance.Players.Keys)
            resource.Sync.SetVisibilityFor((int)peer, true);
        return resource;
    }

    public EnemyCharacter SpawnEnemy(ServerInstance instance, Vector3 position)
    {
        var enemy = GD.Load<PackedScene>(InstanceFactory.EnemyScenePath).Instantiate<EnemyCharacter>();
        enemy.Name = $"Enemy{instance.NextEnemyNumber++}";
        enemy.Position = position;
        enemy.NetPosition = position;
        enemy.NetYaw = (float)GD.RandRange(0, Mathf.Tau);
        enemy.Rotation = new Vector3(0, enemy.NetYaw, 0);
        instance.Nodes.Enemies.AddChild(enemy, forceReadableName: true);
        enemy.ServerInit(
            players: () => instance.Players.Values,
            onPlayerHit: (e, p, damage) => PlayerHitByEnemy?.Invoke(instance, e, p, damage),
            onDeath: e => EnemyKilled?.Invoke(instance, e),
            onRemove: e => RemoveEnemy(instance, e));
        instance.Enemies[enemy.Name] = enemy;
        foreach (var peer in instance.Players.Keys)
            enemy.Sync.SetVisibilityFor((int)peer, true);
        return enemy;
    }

    public void RemoveEnemy(ServerInstance instance, EnemyCharacter enemy)
    {
        if (!instance.Enemies.Remove(enemy.Name)) return;
        enemy.GetParent().RemoveChild(enemy);
        enemy.QueueFree();
    }

    /// <summary>Closes dungeons and cities that have had no players for at least the timeout.</summary>
    public void CloseEmptyInstances(TimeSpan timeout, ICollection<int> instancesWithArrivals)
    {
        var now = DateTime.UtcNow;
        foreach (var instance in _instances.Values.Where(i => i.Kind != InstanceKinds.Hub).ToList())
        {
            if (instance.EmptySince is not { } since || now - since < timeout) continue;
            if (instancesWithArrivals.Contains(instance.Id)) continue; // someone is loading into it
            _instances.Remove(instance.Id);
            instance.Nodes.Container.QueueFree();
            GD.Print($"[server] closed empty {instance.Kind} {instance.Id}");
        }
    }

    /// <summary>
    /// The desired spot, or the nearest nearby one that no player is standing on, so arrivals don't
    /// appear inside each other (e.g. several players returning to the hub's return point).
    /// </summary>
    private static Vector3 FreeSpotNear(ServerInstance instance, Vector3 desired)
    {
        const float minGap = 1.0f;
        bool Free(Vector3 p) => instance.Players.Values.All(o =>
            new Vector2(o.NetPosition.X - p.X, o.NetPosition.Z - p.Z).Length() >= minGap);

        if (Free(desired)) return desired;
        // Dungeon cells are narrow (walls 1.5 m from the center), so only try one small ring there.
        var rings = instance.Layout != null ? 1 : 3;
        for (var ring = 1; ring <= rings; ring++)
            for (var i = 0; i < 8; i++)
            {
                var angle = i * Mathf.Tau / 8f;
                var p = desired + new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * (ring * 1.1f);
                if (Free(p)) return p;
            }
        return desired; // very crowded; players don't collide with each other, so this is still safe
    }

    private static (Vector3 Position, float Yaw) SpawnPoint(ServerInstance instance, int index)
    {
        if (instance.Layout != null)
            return DungeonBuilder.SpawnPoint(instance.Layout, index);
        if (instance.City != null)
            return CityBuilder.SpawnPoint(instance.City, index);
        // Hub: spread arrivals around a small ring so players don't spawn inside each other.
        var angle = index * Mathf.Tau / 8f;
        return (new Vector3(Mathf.Cos(angle) * 2f, 1f, Mathf.Sin(angle) * 2f), 0f);
    }
}
