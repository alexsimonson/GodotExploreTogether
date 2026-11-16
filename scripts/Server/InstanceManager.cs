using System;
using System.Collections.Generic;
using System.Linq;
using ExploreTogether.Core.Dungeon;
using ExploreTogether.Dungeon;
using ExploreTogether.Items;
using ExploreTogether.Net;
using ExploreTogether.Player;
using Godot;

namespace ExploreTogether.Server;

/// <summary>A running instance on the server: its nodes plus which peers are in it.</summary>
public sealed class ServerInstance
{
    public required int Id { get; init; }
    public required string Kind { get; init; }
    public required InstanceNodes Nodes { get; init; }
    /// <summary>Dungeons only: the maze, and its export sent to joining clients.</summary>
    public MazeLayout? Layout { get; init; }
    public int[] LayoutData { get; init; } = [];
    public string CreatedBy { get; init; } = "";
    public DateTime CreatedAt { get; } = DateTime.UtcNow;
    /// <summary>When the last player left, for closing empty dungeons. Null while occupied.</summary>
    public DateTime? EmptySince { get; set; }
    public Dictionary<long, PlayerCharacter> Players { get; } = new();
    /// <summary>Items lying in the instance, by node name.</summary>
    public Dictionary<string, ItemPickup> Items { get; } = new();
    public int NextItemNumber { get; set; } = 1;

    // ---- Dungeon run state (unused for the hub) ----
    public long? RunId { get; set; }
    public bool ExitOpen { get; set; }
    /// <summary>Names of characters who escaped through the exit.</summary>
    public List<string> Escaped { get; } = new();

    /// <summary>Every replicated object in the instance; each must be visible exactly to the peers inside.</summary>
    public IEnumerable<MultiplayerSynchronizer> Synchronizers =>
        Players.Values.Select(p => p.Sync).Concat(Items.Values.Select(i => i.Sync));
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

    public ServerInstance Hub { get; private set; } = null!;

    public IEnumerable<ServerInstance> Dungeons => _instances.Values.Where(i => i.Kind == InstanceKinds.Dungeon);

    public void CreateHub() => Hub = Create(InstanceKinds.Hub, null, "");

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
            SpawnItem(instance, placement.ItemId, DungeonBuilder.ItemPosition(layout, placement));
        GD.Print($"[server] dungeon {instance.Id}: {width}x{height}, length {length}, seed {seed}, " +
                 $"{layout.Nodes.Count} cells (by '{createdBy}')");
        return instance;
    }

    private ServerInstance Create(string kind, MazeLayout? layout, string createdBy)
    {
        var id = _nextId++;
        var data = layout?.Export() ?? [];
        var nodes = InstanceFactory.Build(_instancesRoot, id, kind, data, isServer: true);
        var instance = new ServerInstance
        {
            Id = id, Kind = kind, Nodes = nodes, Layout = layout, LayoutData = data, CreatedBy = createdBy,
            EmptySince = kind == InstanceKinds.Dungeon ? DateTime.UtcNow : null,
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
        if (instance.Players.Count == 0 && instance.Kind == InstanceKinds.Dungeon)
            instance.EmptySince = DateTime.UtcNow;
        GD.Print($"[server] peer {peer} left instance {instance.Id}; {instance.Players.Count} player(s) remain");
    }

    /// <summary>Puts an item into the instance, visible to everyone inside.</summary>
    public ItemPickup SpawnItem(ServerInstance instance, int itemId, Vector3 position, int count = 1)
    {
        var item = GD.Load<PackedScene>(InstanceFactory.ItemScenePath).Instantiate<ItemPickup>();
        item.Name = $"Item{instance.NextItemNumber++}";
        item.ItemId = itemId;
        item.Count = count;
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

    /// <summary>Closes dungeons that have had no players for at least the timeout.</summary>
    public void CloseEmptyDungeons(TimeSpan timeout, ICollection<int> instancesWithArrivals)
    {
        var now = DateTime.UtcNow;
        foreach (var instance in Dungeons.ToList())
        {
            if (instance.EmptySince is not { } since || now - since < timeout) continue;
            if (instancesWithArrivals.Contains(instance.Id)) continue; // someone is loading into it
            _instances.Remove(instance.Id);
            instance.Nodes.Container.QueueFree();
            GD.Print($"[server] closed empty dungeon {instance.Id}");
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
        // Hub: spread arrivals around a small ring so players don't spawn inside each other.
        var angle = index * Mathf.Tau / 8f;
        return (new Vector3(Mathf.Cos(angle) * 2f, 1f, Mathf.Sin(angle) * 2f), 0f);
    }
}
