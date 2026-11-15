using System.Collections.Generic;
using System.Linq;
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
    public Dictionary<long, PlayerCharacter> Players { get; } = new();
}

/// <summary>
/// Server-only. Creates instances and moves players into and out of them.
/// Replication visibility is per instance: a player's synchronizer is visible only to
/// peers in the same instance, so clients are only told about (and spawn) players
/// they share an instance with.
/// </summary>
public sealed class InstanceManager
{
    private readonly Node _instancesRoot;
    private readonly PackedScene _playerScene = GD.Load<PackedScene>(InstanceFactory.PlayerScenePath);
    private readonly Dictionary<int, ServerInstance> _instances = new();
    private readonly Dictionary<long, ServerInstance> _instanceByPeer = new();
    private int _nextId = 1;

    public InstanceManager(Node instancesRoot) => _instancesRoot = instancesRoot;

    public ServerInstance Hub { get; private set; } = null!;

    public void CreateHub() => Hub = Create(InstanceKinds.Hub);

    public ServerInstance Create(string kind)
    {
        var id = _nextId++;
        var nodes = InstanceFactory.Build(_instancesRoot, id, kind, isServer: true);
        var instance = new ServerInstance { Id = id, Kind = kind, Nodes = nodes };
        _instances[id] = instance;
        GD.Print($"[server] created instance {id} ({kind})");
        return instance;
    }

    public ServerInstance? Get(int id) => _instances.GetValueOrDefault(id);

    /// <summary>
    /// Spawns a player for the peer in the instance. A saved position (e.g. the character's
    /// last hub spot) is used if given and sane; otherwise a default spawn point.
    /// </summary>
    public void AddPlayer(ServerInstance instance, long peer, string displayName, Color color,
        Vector3? savedPosition = null, float savedYaw = 0f)
    {
        RemovePeer(peer);

        var player = _playerScene.Instantiate<PlayerCharacter>();
        player.Name = peer.ToString();
        player.DisplayName = displayName;
        player.PlayerColor = color;
        var useSaved = savedPosition is { } sp && sp.Y > -10f && sp.IsFinite();
        var spawn = useSaved ? savedPosition!.Value : SpawnPoint(instance.Players.Count);
        player.Position = spawn;
        player.NetPosition = spawn;
        player.NetYaw = useSaved ? savedYaw : 0f;
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
        _instanceByPeer[peer] = instance;
        GD.Print($"[server] peer {peer} ('{displayName}') entered instance {instance.Id}; " +
                 $"{instance.Players.Count} player(s) there");
    }

    /// <summary>The instance and player node for a peer, if the peer is currently spawned somewhere.</summary>
    public (ServerInstance Instance, PlayerCharacter Player)? FindPlayer(long peer) =>
        _instanceByPeer.TryGetValue(peer, out var instance) && instance.Players.TryGetValue(peer, out var player)
            ? (instance, player)
            : null;

    public void RemovePeer(long peer)
    {
        if (!_instanceByPeer.Remove(peer, out var instance)) return;
        if (instance.Players.Remove(peer, out var player))
        {
            // Freeing the node makes the spawner despawn it on every peer that could see it.
            player.GetParent().RemoveChild(player);
            player.QueueFree();
        }
        GD.Print($"[server] peer {peer} left instance {instance.Id}; {instance.Players.Count} player(s) remain");
    }

    private static Vector3 SpawnPoint(int index)
    {
        // Spread arrivals around a small ring so players don't spawn inside each other.
        var angle = index * Mathf.Tau / 8f;
        return new Vector3(Mathf.Cos(angle) * 2f, 1f, Mathf.Sin(angle) * 2f);
    }

    public IEnumerable<ServerInstance> All => _instances.Values.ToList();
}
