using System;
using Godot;

namespace ExploreTogether.Net;

/// <summary>
/// Session-level RPCs (not tied to an instance). Lives at /root/Main/Session on both
/// server and client, since RPCs need the same node path on both sides.
/// This node only transports messages; ServerGame and ClientGame handle them through the events.
/// </summary>
public partial class NetSession : Node
{
    // ---- Server-side events (raised when a client calls us) ----
    public event Action<long, string, Color>? JoinRequested; // peer, display name, color
    public event Action<long, int>? InstanceReadyReceived; // peer, instance id

    // ---- Client-side events (raised when the server calls us) ----
    public event Action<int, string>? EnterInstanceReceived; // instance id, kind
    public event Action<string>? JoinRejectedReceived;       // reason

    // ---- Client -> server ----

    public void SendJoinRequest(string displayName, Color color) =>
        RpcId(1, MethodName.RequestJoin, displayName, color);

    public void SendInstanceReady(int instanceId) =>
        RpcId(1, MethodName.InstanceReady, instanceId);

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestJoin(string displayName, Color color)
    {
        if (!Main.IsServer) return;
        JoinRequested?.Invoke(Multiplayer.GetRemoteSenderId(), displayName, color);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void InstanceReady(int instanceId)
    {
        if (!Main.IsServer) return;
        InstanceReadyReceived?.Invoke(Multiplayer.GetRemoteSenderId(), instanceId);
    }

    // ---- Server -> client ----

    public void SendEnterInstance(long peer, int instanceId, string kind) =>
        RpcId(peer, MethodName.EnterInstance, instanceId, kind);

    public void SendJoinRejected(long peer, string reason) =>
        RpcId(peer, MethodName.JoinRejected, reason);

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void EnterInstance(int instanceId, string kind) =>
        EnterInstanceReceived?.Invoke(instanceId, kind);

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void JoinRejected(string reason) =>
        JoinRejectedReceived?.Invoke(reason);
}
