using System;
using System.Collections.Generic;
using Godot;
using GArray = Godot.Collections.Array;
using GDict = Godot.Collections.Dictionary;

namespace ExploreTogether.Net;

/// <summary>A character as shown on the character-select screen.</summary>
public sealed record CharacterSummary(long Id, string Name, Color Color, double PlayTimeSeconds, bool Hardcore = false);

/// <summary>Shared state of the dungeon run the client is in.</summary>
public sealed record DungeonStatus(bool ExitOpen, string PassHolder, string[] Escaped, int PlayersInside);

/// <summary>A running dungeon as listed in the dungeon menu.</summary>
public sealed record DungeonSummary(int Id, string CreatedBy, int Width, int Height, int Length, string[] Players);
/// <summary>A running city, as listed at the City Gate: its size (tiles per side), residents, and the time there.</summary>
public sealed record CitySummary(int Id, string CreatedBy, int Size, int People, float Hour, string[] Players);
/// <summary>
/// What a new city is made with (the City Gate's options). <see cref="People"/> below 0: the default for the size;
/// <see cref="Seed"/> 0: random.
/// </summary>
/// <see cref="Zombies"/>: how many (0: none; the zombie layer isn't even created).
public sealed record CitySettings(int Size, float StartHour, int People, float DayMinutes, int Seed, int Zombies = 0);

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
///       Interact(hub_storage) -> Storage (contents); MoveItem/DropItem involving the storage -> Inventory + Storage
///
/// Note: ENet traffic is not encrypted yet, so passwords cross the network in plain text.
/// Fine on a LAN for development; must be fixed before public hosting (see PLAN.md).
/// </summary>
public partial class NetSession : Node
{
    // ---- Server-side events (a client called us). First argument is the sender's peer id. ----
    public event Action<long, string, string>? LoginRequested;          // user, password
    public event Action<long, string, string, string>? RegisterRequested; // user, password, invite code
    public event Action<long, string, Color, bool>? CreateCharacterRequested; // name, color, hardcore
    public event Action<long, long>? SelectCharacterRequested;          // character id
    public event Action<long, int>? InstanceReadyReceived;              // instance id
    public event Action<long, string>? InteractRequested;               // interaction id
    public event Action<long, string>? LockDoorRequested;               // a door's interaction id
    public event Action<long, string>? DragBodyRequested;               // a body's interaction id, or "" to let go
    public event Action<long, int, int, int>? CreateDungeonRequested;   // width, height, length
    public event Action<long, int>? JoinDungeonRequested;               // instance id
    public event Action<long, CitySettings>? CreateCityRequested;
    public event Action<long, int, int, int>? SetHotbarRequested;       // key, container (-1 = clear), slot
    public event Action<long, int>? UseHotbarRequested;                 // key
    public event Action<long, int>? JoinCityRequested;                  // instance id
    public event Action<long, int, Vector3, float>? PlayerTransformReceived; // instance id, position, yaw
    public event Action<long, int, int, int, int>? MoveItemRequested;   // from container, from slot, to container, to slot
    public event Action<long, int, int>? DropItemRequested;             // container, slot
    public event Action<long, Vector3>? AttackRequested;                // aim direction (camera forward)
    public event Action<long, int, bool, Vector3>? AimReceived;         // instance id, aiming?, ghost position
    public event Action<long>? ReloadRequested;
    public event Action<long, string, string>? CityActionRequested;     // action, target (see ServerGame.CityActions)

    /// <summary>
    /// Server only: decides whether a request from a client is handled (rate limiting). Called with the sender's
    /// peer id and the RPC's name. Unset = everything is handled.
    /// </summary>
    public Func<long, string, bool>? RequestFilter { get; set; }

    /// <summary>True on the server if the incoming request (named after the calling RPC method) passes the filter.</summary>
    private bool Accept([System.Runtime.CompilerServices.CallerMemberName] string kind = "") =>
        Main.IsServer && (RequestFilter?.Invoke(Multiplayer.GetRemoteSenderId(), kind) ?? true);

    // ---- Client-side events (the server called us) ----
    public event Action<bool, string, string>? AuthResultReceived;       // ok, code, message
    public event Action<List<CharacterSummary>>? CharacterListReceived;
    public event Action<bool, string>? CharacterResultReceived;          // ok, message
    public event Action<int, string, int[]>? EnterInstanceReceived;      // instance id, kind, maze layout (dungeons)
    public event Action<List<DungeonSummary>>? DungeonMenuReceived;
    public event Action<int[], long[]>? HotbarReceived;                  // item id per key (-1 empty), serial per key (-1 none)
    public event Action<List<CitySummary>, CitySettings>? CityMenuReceived; // running cities, the server's defaults for a new one
    public event Action<string>? NoticeReceived;                         // short message for the HUD
    /// <summary>inventory (slot, item id, count, data) quads (data -1 = none); gear item ids and gear data per slot (-1 = empty/none)</summary>
    public event Action<int[], int[], int[]>? InventoryReceived;
    /// <summary>The hub storage as (slot, item id, count, data) quads: sent when the player opens it and after each change.</summary>
    public event Action<int[]>? StorageReceived;
    public event Action<DungeonStatus>? DungeonStatusReceived;
    public event Action<float, float>? CityClockReceived;                // hour of day, game hours per real second
    public event Action<int[]>? DoorStatesReceived;                      // (parcel, open, locked) triples
    /// <summary>whose body; their inventory as (slot, item, count, data) quads; their gear item ids and data per slot</summary>
    public event Action<string, int[], int[], int[]>? LootReceived;
    public event Action<int[]>? SupplyReceived;                          // the debug supply as quads
    public event Action<string>? DragStateReceived;                      // the name of whoever we're dragging, or "" 
    public event Action<int, int>? AmmoReceived;                         // magazine, reserve
    public event Action<Vector3, int, float, Color>? MagicAreaReceived;  // position, magic style, lifetime, caster's color
    public event Action<string>? CharacterLostReceived;                  // hardcore death: back to character select
    public event Action<Vector3>? PositionCorrectionReceived;            // the server rejected our movement: go back here
    public event Action<string, float>? ProgressReceived;                // a timed action (label, seconds; 0 = cancelled)
    public event Action<string, string>? GrabStateReceived;              // "holding"/"held"/"" and the other one's name
    public event Action<int[]>? FurnitureTakenReceived;
    public event Action<int[]>? PowerStateReceived;                       // Core.City.CityPower.Export                  // chair/stool ids picked up
    public event Action<int, Vector3>? SoundReceived;                    // a Core.Audio.SoundId, and where
    public event Action<Vector3, Vector3>? ShotReceived;                 // a gunshot: muzzle and where it hit
    public event Action<int, string>? AlertReceived;                     // how aware the city is of us (0..3), and why

    // =========================================================== client -> server

    public void SendLogin(string user, string password) => RpcId(1, MethodName.Login, user, password);
    public void SendRegister(string user, string password, string invite) => RpcId(1, MethodName.Register, user, password, invite);
    public void SendCreateCharacter(string name, Color color, bool hardcore) =>
        RpcId(1, MethodName.CreateCharacter, name, color, hardcore);
    public void SendSelectCharacter(long characterId) => RpcId(1, MethodName.SelectCharacter, characterId);
    public void SendInstanceReady(int instanceId) => RpcId(1, MethodName.InstanceReady, instanceId);
    public void SendInteract(string interactionId) => RpcId(1, MethodName.Interact, interactionId);
    public void SendLockDoor(string interactionId) => RpcId(1, MethodName.LockDoor, interactionId);
    public void SendDragBody(string interactionId) => RpcId(1, MethodName.DragBody, interactionId);
    public void SendCreateDungeon(int width, int height, int length) => RpcId(1, MethodName.CreateDungeon, width, height, length);
    public void SendJoinDungeon(int instanceId) => RpcId(1, MethodName.JoinDungeon, instanceId);
    public void SendCreateCity(CitySettings c) => RpcId(1, MethodName.CreateCity, c.Size, c.StartHour, c.People, c.DayMinutes, c.Seed, c.Zombies);
    public void SendJoinCity(int instanceId) => RpcId(1, MethodName.JoinCity, instanceId);
    public void SendSetHotbar(int key, int container, int slot) => RpcId(1, MethodName.SetHotbar, key, container, slot);
    public void SendUseHotbar(int key) => RpcId(1, MethodName.UseHotbar, key);

    /// <summary>A drag-and-drop between slots (see Core.Items.SlotContainerLogic).</summary>
    public void SendMoveItem(int fromContainer, int fromSlot, int toContainer, int toSlot) =>
        RpcId(1, MethodName.MoveItem, fromContainer, fromSlot, toContainer, toSlot);

    /// <summary>Left-click with a weapon: the server raycasts from the player's head along this direction.</summary>
    public void SendAttack(Vector3 aimDirection) => RpcId(1, MethodName.Attack, aimDirection);

    public void SendReload() => RpcId(1, MethodName.Reload);

    /// <summary>City actions: kick a door, pick a lock, grab or let go, struggle, crouch (see ServerGame.CityActions).</summary>
    public void SendCityAction(string action, string target) => RpcId(1, MethodName.CityAction, action, target);

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void CityAction(string action, string target)
    {
        if (Accept()) CityActionRequested?.Invoke(Multiplayer.GetRemoteSenderId(), action, target);
    }

    /// <summary>Where this player's magic ghost is (or that they stopped aiming), so others can see it.</summary>
    public void SendAim(int instanceId, bool aiming, Vector3 position) =>
        RpcId(1, MethodName.Aim, instanceId, aiming, position);

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.UnreliableOrdered)]
    private void Aim(int instanceId, bool aiming, Vector3 position)
    {
        if (Accept()) AimReceived?.Invoke(Multiplayer.GetRemoteSenderId(), instanceId, aiming, position);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Attack(Vector3 aimDirection)
    {
        if (Accept()) AttackRequested?.Invoke(Multiplayer.GetRemoteSenderId(), aimDirection);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Reload()
    {
        if (Accept()) ReloadRequested?.Invoke(Multiplayer.GetRemoteSenderId());
    }

    /// <summary>Ctrl + right-click: drop a slot's contents into the world.</summary>
    public void SendDropItem(int container, int slot) => RpcId(1, MethodName.DropItem, container, slot);

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void MoveItem(int fromContainer, int fromSlot, int toContainer, int toSlot)
    {
        if (Accept()) MoveItemRequested?.Invoke(Multiplayer.GetRemoteSenderId(), fromContainer, fromSlot, toContainer, toSlot);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void DropItem(int container, int slot)
    {
        if (Accept()) DropItemRequested?.Invoke(Multiplayer.GetRemoteSenderId(), container, slot);
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
        if (Accept()) PlayerTransformReceived?.Invoke(Multiplayer.GetRemoteSenderId(), instanceId, position, yaw);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Interact(string interactionId)
    {
        if (Accept()) InteractRequested?.Invoke(Multiplayer.GetRemoteSenderId(), interactionId);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void LockDoor(string interactionId)
    {
        if (Accept()) LockDoorRequested?.Invoke(Multiplayer.GetRemoteSenderId(), interactionId);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void DragBody(string interactionId)
    {
        if (Accept()) DragBodyRequested?.Invoke(Multiplayer.GetRemoteSenderId(), interactionId);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void CreateDungeon(int width, int height, int length)
    {
        if (Accept()) CreateDungeonRequested?.Invoke(Multiplayer.GetRemoteSenderId(), width, height, length);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void JoinDungeon(int instanceId)
    {
        if (Accept()) JoinDungeonRequested?.Invoke(Multiplayer.GetRemoteSenderId(), instanceId);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void CreateCity(int size, float startHour, int people, float dayMinutes, int seed, int zombies)
    {
        if (Accept()) CreateCityRequested?.Invoke(Multiplayer.GetRemoteSenderId(), new CitySettings(size, startHour, people, dayMinutes, seed, zombies));
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void SetHotbar(int key, int container, int slot)
    {
        if (Accept()) SetHotbarRequested?.Invoke(Multiplayer.GetRemoteSenderId(), key, container, slot);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void UseHotbar(int key)
    {
        if (Accept()) UseHotbarRequested?.Invoke(Multiplayer.GetRemoteSenderId(), key);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void JoinCity(int instanceId)
    {
        if (Accept()) JoinCityRequested?.Invoke(Multiplayer.GetRemoteSenderId(), instanceId);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Login(string user, string password)
    {
        if (Accept()) LoginRequested?.Invoke(Multiplayer.GetRemoteSenderId(), user, password);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Register(string user, string password, string invite)
    {
        if (Accept()) RegisterRequested?.Invoke(Multiplayer.GetRemoteSenderId(), user, password, invite);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void CreateCharacter(string name, Color color, bool hardcore)
    {
        if (Accept()) CreateCharacterRequested?.Invoke(Multiplayer.GetRemoteSenderId(), name, color, hardcore);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void SelectCharacter(long characterId)
    {
        if (Accept()) SelectCharacterRequested?.Invoke(Multiplayer.GetRemoteSenderId(), characterId);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void InstanceReady(int instanceId)
    {
        if (Accept()) InstanceReadyReceived?.Invoke(Multiplayer.GetRemoteSenderId(), instanceId);
    }

    // =========================================================== server -> client

    public void SendAuthResult(long peer, bool ok, string code, string message) =>
        RpcId(peer, MethodName.AuthResult, ok, code, message);

    public void SendCharacterResult(long peer, bool ok, string message) =>
        RpcId(peer, MethodName.CharacterResult, ok, message);

    public void SendEnterInstance(long peer, int instanceId, string kind, int[] layoutData) =>
        RpcId(peer, MethodName.EnterInstance, instanceId, kind, layoutData);

    public void SendNotice(long peer, string message) => RpcId(peer, MethodName.Notice, message);

    public void SendAmmo(long peer, int magazine, int reserve) => RpcId(peer, MethodName.Ammo, magazine, reserve);

    public void SendMagicArea(long peer, Vector3 position, int style, float lifetime, Color color) =>
        RpcId(peer, MethodName.MagicArea, position, style, lifetime, color);

    public void SendCharacterLost(long peer, string message) => RpcId(peer, MethodName.CharacterLost, message);

    public void SendPositionCorrection(long peer, Vector3 position) => RpcId(peer, MethodName.PositionCorrection, position);
    public void SendProgress(long peer, string label, float seconds) => RpcId(peer, MethodName.Progress, label, seconds);
    public void SendGrabState(long peer, string state, string other) => RpcId(peer, MethodName.GrabState, state, other);
    public void SendShot(long peer, Vector3 from, Vector3 to) => RpcId(peer, MethodName.Shot, from, to);
    /// <summary>Play a sound (a Core.Audio.SoundId) at a point. Unreliable: a lost sound isn't worth resending late.</summary>
    public void SendSound(long peer, int sound, Vector3 at) => RpcId(peer, MethodName.Sound, sound, at);
    public void SendAlert(long peer, int level, string reason) => RpcId(peer, MethodName.Alert, level, reason);

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void PositionCorrection(Vector3 position) => PositionCorrectionReceived?.Invoke(position);

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Progress(string label, float seconds) => ProgressReceived?.Invoke(label, seconds);

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void GrabState(string state, string other) => GrabStateReceived?.Invoke(state, other);

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.UnreliableOrdered)]
    private void Shot(Vector3 from, Vector3 to) => ShotReceived?.Invoke(from, to);

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Unreliable)]
    private void Sound(int sound, Vector3 at) => SoundReceived?.Invoke(sound, at);

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Alert(int level, string reason) => AlertReceived?.Invoke(level, reason);

    /// <summary>The player's whole inventory as (slot, item id, count, data) quads, plus gear item ids and data per slot.</summary>
    public void SendInventory(long peer, int[] quads, int[] gear, int[] gearData) =>
        RpcId(peer, MethodName.Inventory, quads, gear, gearData);

    public void SendStorage(long peer, int[] quads) => RpcId(peer, MethodName.Storage, quads);

    public void SendCityClock(long peer, float hour, float hoursPerSecond) =>
        RpcId(peer, MethodName.CityClock, hour, hoursPerSecond);

    public void SendDoorStates(long peer, int[] triples) => RpcId(peer, MethodName.DoorStates, triples);
    /// <summary>The city's power and lights (Core.City.CityPower.Export), whenever they change.</summary>
    public void SendPowerState(long peer, int[] state) => RpcId(peer, MethodName.PowerState, state);

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void PowerState(int[] state) => PowerStateReceived?.Invoke(state);

    /// <summary>Furniture (chair and stool ids) that has been picked up, so clients hide it.</summary>
    public void SendFurnitureTaken(long peer, int[] pieces) => RpcId(peer, MethodName.FurnitureTaken, pieces);

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void FurnitureTaken(int[] pieces) => FurnitureTakenReceived?.Invoke(pieces);

    public void SendLoot(long peer, string name, int[] quads, int[] gear, int[] gearData) =>
        RpcId(peer, MethodName.Loot, name, quads, gear, gearData);

    public void SendSupply(long peer, int[] quads) => RpcId(peer, MethodName.Supply, quads);

    public void SendDragState(long peer, string draggingName) => RpcId(peer, MethodName.DragState, draggingName);

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

    public void SendHotbar(long peer, int[] ids, long[] uids) => RpcId(peer, MethodName.Hotbar, ids, uids);

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Hotbar(int[] ids, long[] uids) => HotbarReceived?.Invoke(ids, uids);

    public void SendCityMenu(long peer, IEnumerable<CitySummary> cities, CitySettings defaults)
    {
        var list = new GArray();
        foreach (var c in cities)
            list.Add(new GDict
            {
                ["id"] = c.Id, ["by"] = c.CreatedBy, ["size"] = c.Size, ["people"] = c.People, ["hour"] = c.Hour,
                ["players"] = c.Players,
            });
        RpcId(peer, MethodName.CityMenu, list, defaults.Size, defaults.StartHour, defaults.People, defaults.DayMinutes, defaults.Zombies);
    }

    public void SendCharacterList(long peer, IEnumerable<CharacterSummary> characters)
    {
        var list = new GArray();
        foreach (var c in characters)
            list.Add(new GDict { ["id"] = c.Id, ["name"] = c.Name, ["color"] = c.Color, ["play_time"] = c.PlayTimeSeconds, ["hardcore"] = c.Hardcore });
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
    private void Ammo(int magazine, int reserve) => AmmoReceived?.Invoke(magazine, reserve);

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void MagicArea(Vector3 position, int style, float lifetime, Color color) =>
        MagicAreaReceived?.Invoke(position, style, lifetime, color);

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void CharacterLost(string message) => CharacterLostReceived?.Invoke(message);

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Inventory(int[] quads, int[] gear, int[] gearData) => InventoryReceived?.Invoke(quads, gear, gearData);

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Storage(int[] quads) => StorageReceived?.Invoke(quads);

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void CityClock(float hour, float hoursPerSecond) => CityClockReceived?.Invoke(hour, hoursPerSecond);

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void DoorStates(int[] triples) => DoorStatesReceived?.Invoke(triples);

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Loot(string name, int[] quads, int[] gear, int[] gearData) => LootReceived?.Invoke(name, quads, gear, gearData);

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Supply(int[] quads) => SupplyReceived?.Invoke(quads);

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void DragState(string draggingName) => DragStateReceived?.Invoke(draggingName);

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
    private void CityMenu(GArray list, int size, float startHour, int people, float dayMinutes, int zombies)
    {
        var result = new List<CitySummary>();
        foreach (var item in list)
        {
            var d = item.AsGodotDictionary();
            result.Add(new CitySummary(d["id"].AsInt32(), d["by"].AsString(), d["size"].AsInt32(), d["people"].AsInt32(),
                d["hour"].AsSingle(), d["players"].AsStringArray()));
        }
        CityMenuReceived?.Invoke(result, new CitySettings(size, startHour, people, dayMinutes, 0, zombies));
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void CharacterList(GArray list)
    {
        var result = new List<CharacterSummary>();
        foreach (var item in list)
        {
            var d = item.AsGodotDictionary();
            result.Add(new CharacterSummary(d["id"].AsInt64(), d["name"].AsString(), d["color"].AsColor(), d["play_time"].AsDouble(),
                d.ContainsKey("hardcore") && d["hardcore"].AsBool()));
        }
        CharacterListReceived?.Invoke(result);
    }
}
