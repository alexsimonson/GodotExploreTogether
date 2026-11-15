using System;
using System.Collections.Generic;
using Godot;
using GArray = Godot.Collections.Array;
using GDict = Godot.Collections.Dictionary;

namespace ExploreTogether.Net;

/// <summary>A character as shown on the character-select screen.</summary>
public sealed record CharacterSummary(long Id, string Name, Color Color, double PlayTimeSeconds);

/// <summary>
/// Session-level RPCs (not tied to an instance). Lives at /root/Main/Session on both
/// server and client, since RPCs need the same node path on both sides.
/// This node only transports messages; ServerGame and ClientGame handle them through the events.
///
/// Flow: Login/Register -> AuthResult (+ CharacterList)
///       CreateCharacter -> CharacterResult (+ CharacterList)
///       SelectCharacter -> EnterInstance -> client builds it -> InstanceReady -> server spawns the player
///
/// Note: ENet traffic is not encrypted yet, so passwords cross the network in plain text.
/// Fine on a LAN for development; must be fixed before public hosting (see PLAN.md).
/// </summary>
public partial class NetSession : Node
{
    // ---- Server-side events (a client called us). First argument is the sender's peer id. ----
    public event Action<long, string, string>? LoginRequested;          // user, password
    public event Action<long, string, string>? RegisterRequested;       // user, password
    public event Action<long, string, Color>? CreateCharacterRequested; // name, color
    public event Action<long, long>? SelectCharacterRequested;          // character id
    public event Action<long, int>? InstanceReadyReceived;              // instance id

    // ---- Client-side events (the server called us) ----
    public event Action<bool, string, string>? AuthResultReceived;       // ok, code, message
    public event Action<List<CharacterSummary>>? CharacterListReceived;
    public event Action<bool, string>? CharacterResultReceived;          // ok, message
    public event Action<int, string>? EnterInstanceReceived;             // instance id, kind

    // =========================================================== client -> server

    public void SendLogin(string user, string password) => RpcId(1, MethodName.Login, user, password);
    public void SendRegister(string user, string password) => RpcId(1, MethodName.Register, user, password);
    public void SendCreateCharacter(string name, Color color) => RpcId(1, MethodName.CreateCharacter, name, color);
    public void SendSelectCharacter(long characterId) => RpcId(1, MethodName.SelectCharacter, characterId);
    public void SendInstanceReady(int instanceId) => RpcId(1, MethodName.InstanceReady, instanceId);

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Login(string user, string password)
    {
        if (Main.IsServer) LoginRequested?.Invoke(Multiplayer.GetRemoteSenderId(), user, password);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Register(string user, string password)
    {
        if (Main.IsServer) RegisterRequested?.Invoke(Multiplayer.GetRemoteSenderId(), user, password);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void CreateCharacter(string name, Color color)
    {
        if (Main.IsServer) CreateCharacterRequested?.Invoke(Multiplayer.GetRemoteSenderId(), name, color);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void SelectCharacter(long characterId)
    {
        if (Main.IsServer) SelectCharacterRequested?.Invoke(Multiplayer.GetRemoteSenderId(), characterId);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void InstanceReady(int instanceId)
    {
        if (Main.IsServer) InstanceReadyReceived?.Invoke(Multiplayer.GetRemoteSenderId(), instanceId);
    }

    // =========================================================== server -> client

    public void SendAuthResult(long peer, bool ok, string code, string message) =>
        RpcId(peer, MethodName.AuthResult, ok, code, message);

    public void SendCharacterResult(long peer, bool ok, string message) =>
        RpcId(peer, MethodName.CharacterResult, ok, message);

    public void SendEnterInstance(long peer, int instanceId, string kind) =>
        RpcId(peer, MethodName.EnterInstance, instanceId, kind);

    public void SendCharacterList(long peer, IEnumerable<CharacterSummary> characters)
    {
        var list = new GArray();
        foreach (var c in characters)
            list.Add(new GDict { ["id"] = c.Id, ["name"] = c.Name, ["color"] = c.Color, ["play_time"] = c.PlayTimeSeconds });
        RpcId(peer, MethodName.CharacterList, list);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void AuthResult(bool ok, string code, string message) => AuthResultReceived?.Invoke(ok, code, message);

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void CharacterResult(bool ok, string message) => CharacterResultReceived?.Invoke(ok, message);

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void EnterInstance(int instanceId, string kind) => EnterInstanceReceived?.Invoke(instanceId, kind);

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void CharacterList(GArray list)
    {
        var result = new List<CharacterSummary>();
        foreach (var item in list)
        {
            var d = item.AsGodotDictionary();
            result.Add(new CharacterSummary(d["id"].AsInt64(), d["name"].AsString(), d["color"].AsColor(), d["play_time"].AsDouble()));
        }
        CharacterListReceived?.Invoke(result);
    }
}
