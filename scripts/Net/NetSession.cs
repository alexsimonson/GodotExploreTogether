using System;
using System.Collections.Generic;
using Godot;
using GArray = Godot.Collections.Array;
using GDict = Godot.Collections.Dictionary;

namespace ExploreTogether.Net;

/// <summary>A character as shown on the character-select screen.</summary>
public sealed record CharacterSummary(long Id, string Name, Color Color, double PlayTimeSeconds);

/// <summary>Shared state of the dungeon run the client is in.</summary>
public sealed record DungeonStatus(bool ExitOpen, string PassHolder, string[] Escaped, int PlayersInside);

/// <summary>A running dungeon as listed in the dungeon menu.</summary>
public sealed record DungeonSummary(int Id, string CreatedBy, int Width, int Height, int Length, string[] Players);

/// <summary>
/// Session-level RPCs (not tied to an instance). Lives at /root/Main/Session on both
/// server and client, since RPCs need the same node path on both sides.
/// This node only transports messages; ServerGame and ClientGame handle them through the events.
///
/// Flow: Login/Register -> AuthResult (+ CharacterList)
///       CreateCharacter -> CharacterResult (+ CharacterList)
///       SelectCharacter -> EnterInstance -> client builds it -> InstanceReady -> server spawns the player
///       Interact(id) -> server checks distance and acts (e.g. ShowDungeonMenu, or EnterInstance for the hub)
///       CreateDungeon / JoinDungeon -> EnterInstance (with the maze layout) -> InstanceReady -> spawn
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
    public event Action<long, string>? InteractRequested;               // interaction id
    public event Action<long, int, int, int>? CreateDungeonRequested;   // width, height, length
    public event Action<long, int>? JoinDungeonRequested;               // instance id
    public event Action<long, int, Vector3, float>? PlayerTransformReceived; // instance id, position, yaw
    public event Action<long, int, int, int, int>? MoveItemRequested;   // from container, from slot, to container, to slot
    public event Action<long, int, int>? DropItemRequested;             // container, slot

    // ---- Client-side events (the server called us) ----
    public event Action<bool, string, string>? AuthResultReceived;       // ok, code, message
    public event Action<List<CharacterSummary>>? CharacterListReceived;
    public event Action<bool, string>? CharacterResultReceived;          // ok, message
    public event Action<int, string, int[]>? EnterInstanceReceived;      // instance id, kind, maze layout (dungeons)
    public event Action<List<DungeonSummary>>? DungeonMenuReceived;
    public event Action<string>? NoticeReceived;                         // short message for the HUD
    public event Action<int[], int[]>? InventoryReceived;                // inventory (slot, item id, count) triples; gear item ids (-1 = empty)
    public event Action<DungeonStatus>? DungeonStatusReceived;

    // =========================================================== client -> server

    public void SendLogin(string user, string password) => RpcId(1, MethodName.Login, user, password);
    public void SendRegister(string user, string password) => RpcId(1, MethodName.Register, user, password);
    public void SendCreateCharacter(string name, Color color) => RpcId(1, MethodName.CreateCharacter, name, color);
    public void SendSelectCharacter(long characterId) => RpcId(1, MethodName.SelectCharacter, characterId);
    public void SendInstanceReady(int instanceId) => RpcId(1, MethodName.InstanceReady, instanceId);
    public void SendInteract(string interactionId) => RpcId(1, MethodName.Interact, interactionId);
    public void SendCreateDungeon(int width, int height, int length) => RpcId(1, MethodName.CreateDungeon, width, height, length);
    public void SendJoinDungeon(int instanceId) => RpcId(1, MethodName.JoinDungeon, instanceId);

    /// <summary>A drag-and-drop between slots (see Core.Items.SlotContainerLogic).</summary>
    public void SendMoveItem(int fromContainer, int fromSlot, int toContainer, int toSlot) =>
        RpcId(1, MethodName.MoveItem, fromContainer, fromSlot, toContainer, toSlot);

    /// <summary>Ctrl + right-click: drop a slot's contents into the world.</summary>
    public void SendDropItem(int container, int slot) => RpcId(1, MethodName.DropItem, container, slot);

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void MoveItem(int fromContainer, int fromSlot, int toContainer, int toSlot)
    {
        if (Main.IsServer) MoveItemRequested?.Invoke(Multiplayer.GetRemoteSenderId(), fromContainer, fromSlot, toContainer, toSlot);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void DropItem(int container, int slot)
    {
        if (Main.IsServer) DropItemRequested?.Invoke(Multiplayer.GetRemoteSenderId(), container, slot);
    }

    /// <summary>
    /// The owning client's movement. Sent through this node (whose path never changes) with the
    /// instance id, so late packets after switching instances are dropped cleanly by the server
    /// instead of targeting a player node that no longer exists.
    /// </summary>
    public void SendPlayerTransform(int instanceId, Vector3 position, float yaw) =>
        RpcId(1, MethodName.PlayerTransform, instanceId, position, yaw);

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.UnreliableOrdered)]
    private void PlayerTransform(int instanceId, Vector3 position, float yaw)
    {
        if (Main.IsServer) PlayerTransformReceived?.Invoke(Multiplayer.GetRemoteSenderId(), instanceId, position, yaw);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Interact(string interactionId)
    {
        if (Main.IsServer) InteractRequested?.Invoke(Multiplayer.GetRemoteSenderId(), interactionId);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void CreateDungeon(int width, int height, int length)
    {
        if (Main.IsServer) CreateDungeonRequested?.Invoke(Multiplayer.GetRemoteSenderId(), width, height, length);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void JoinDungeon(int instanceId)
    {
        if (Main.IsServer) JoinDungeonRequested?.Invoke(Multiplayer.GetRemoteSenderId(), instanceId);
    }

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

    public void SendEnterInstance(long peer, int instanceId, string kind, int[] layoutData) =>
        RpcId(peer, MethodName.EnterInstance, instanceId, kind, layoutData);

    public void SendNotice(long peer, string message) => RpcId(peer, MethodName.Notice, message);

    /// <summary>The player's whole inventory as (slot, item id, count) triples, plus gear item ids per slot.</summary>
    public void SendInventory(long peer, int[] triples, int[] gear) => RpcId(peer, MethodName.Inventory, triples, gear);

    public void SendDungeonStatus(long peer, DungeonStatus status) =>
        RpcId(peer, MethodName.DungeonStatusUpdate, status.ExitOpen, status.PassHolder, status.Escaped, status.PlayersInside);

    public void SendDungeonMenu(long peer, IEnumerable<DungeonSummary> dungeons)
    {
        var list = new GArray();
        foreach (var d in dungeons)
            list.Add(new GDict
            {
                ["id"] = d.Id, ["by"] = d.CreatedBy, ["w"] = d.Width, ["h"] = d.Height, ["len"] = d.Length,
                ["players"] = d.Players,
            });
        RpcId(peer, MethodName.DungeonMenu, list);
    }

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
    private void EnterInstance(int instanceId, string kind, int[] layoutData) =>
        EnterInstanceReceived?.Invoke(instanceId, kind, layoutData);

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Notice(string message) => NoticeReceived?.Invoke(message);

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Inventory(int[] triples, int[] gear) => InventoryReceived?.Invoke(triples, gear);

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void DungeonStatusUpdate(bool exitOpen, string passHolder, string[] escaped, int playersInside) =>
        DungeonStatusReceived?.Invoke(new DungeonStatus(exitOpen, passHolder, escaped, playersInside));

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void DungeonMenu(GArray list)
    {
        var result = new List<DungeonSummary>();
        foreach (var item in list)
        {
            var d = item.AsGodotDictionary();
            result.Add(new DungeonSummary(d["id"].AsInt32(), d["by"].AsString(), d["w"].AsInt32(), d["h"].AsInt32(),
                d["len"].AsInt32(), d["players"].AsStringArray()));
        }
        DungeonMenuReceived?.Invoke(result);
    }

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
