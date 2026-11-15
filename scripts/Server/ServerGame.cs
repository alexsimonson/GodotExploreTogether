using System.Collections.Generic;
using ExploreTogether.Core;
using ExploreTogether.Net;
using Godot;

namespace ExploreTogether.Server;

/// <summary>
/// Server-only root. Listens for clients, runs the join handshake and owns the instances.
///
/// Join flow:
///   client connects -> client RequestJoin(name)
///   server EnterInstance(id, kind) -> client builds the instance locally
///   client InstanceReady(id) -> server spawns the player into that instance
/// </summary>
public partial class ServerGame : Node
{
    private const int MaxClients = 64;
    private const int MaxNameLength = 24;

    private NetSession _session = null!;
    private InstanceManager _instances = null!;

    /// <summary>Peers that asked to join, keyed by peer id, with the instance they were sent to.</summary>
    private readonly Dictionary<long, (string Name, Color Color, int InstanceId)> _pending = new();

    private readonly Dictionary<long, string> _names = new();

    public override void _Ready()
    {
        var main = GetParent<Main>();
        _session = main.Session;
        _instances = new InstanceManager(main.Instances);
        _instances.CreateHub();

        var port = CmdArgs.GetInt("port", GameInfo.DefaultPort);
        var peer = new ENetMultiplayerPeer();
        var err = peer.CreateServer(port, MaxClients);
        if (err != Error.Ok)
        {
            GD.PushError($"[server] failed to listen on port {port}: {err}");
            GetTree().Quit(1);
            return;
        }
        Multiplayer.MultiplayerPeer = peer;
        Multiplayer.PeerConnected += id => GD.Print($"[server] peer {id} connected");
        Multiplayer.PeerDisconnected += OnPeerDisconnected;

        _session.JoinRequested += OnJoinRequested;
        _session.InstanceReadyReceived += OnInstanceReady;

        GD.Print($"[server] listening on port {port}");
    }

    private void OnJoinRequested(long peer, string requestedName, Color requestedColor)
    {
        var name = requestedName.StripEdges();
        if (name.Length == 0) name = $"Player{peer % 10000}";
        if (name.Length > MaxNameLength) name = name[..MaxNameLength];

        _names[peer] = name;
        var target = _instances.Hub;
        _pending[peer] = (name, PlayerLook.Sanitize(requestedColor), target.Id);
        _session.SendEnterInstance(peer, target.Id, target.Kind);
    }

    private void OnInstanceReady(long peer, int instanceId)
    {
        if (!_pending.Remove(peer, out var pending) || pending.InstanceId != instanceId)
        {
            GD.PushWarning($"[server] unexpected InstanceReady({instanceId}) from peer {peer}");
            return;
        }
        var instance = _instances.Get(instanceId);
        if (instance == null) return;
        _instances.AddPlayer(instance, peer, pending.Name, pending.Color);
    }

    private void OnPeerDisconnected(long peer)
    {
        GD.Print($"[server] peer {peer} disconnected");
        _pending.Remove(peer);
        _names.Remove(peer);
        _instances.RemovePeer(peer);
    }
}
