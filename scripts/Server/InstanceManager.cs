using System;
using System.Collections.Generic;
using System.Linq;
using ExploreTogether.Core.Dungeon;
using ExploreTogether.Dungeon;
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
        player.Position = spawn;
        player.NetPosition = spawn;
        player.NetYaw = yaw;
        instance.Nodes.Players.AddChild(player, forceReadableName: true);

        // Visibility (the scene sets public_visibility = false):
        // the new player can see everyone already here, and they can see the new player.
        foreach (var (otherPeer, other) in instance.Players)
        {
            other.Sync.SetVisibilityFor((int)peer, true);
            player.Sync.SetVisibilityFor((int)otherPeer, true);
        }
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
            foreach (var other in instance.Players.Values)
                other.Sync.SetVisibilityFor((int)peer, false);
        if (instance.Players.Count == 0 && instance.Kind == InstanceKinds.Dungeon)
            instance.EmptySince = DateTime.UtcNow;
        GD.Print($"[server] peer {peer} left instance {instance.Id}; {instance.Players.Count} player(s) remain");
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

    private static (Vector3 Position, float Yaw) SpawnPoint(ServerInstance instance, int index)
    {
        if (instance.Layout != null)
            return DungeonBuilder.SpawnPoint(instance.Layout, index);
        // Hub: spread arrivals around a small ring so players don't spawn inside each other.
        var angle = index * Mathf.Tau / 8f;
        return (new Vector3(Mathf.Cos(angle) * 2f, 1f, Mathf.Sin(angle) * 2f), 0f);
    }
}
