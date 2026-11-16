using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ExploreTogether.Core;
using ExploreTogether.Core.Items;
using ExploreTogether.Core.Persistence;
using ExploreTogether.Dungeon;
using ExploreTogether.Net;
using Godot;

namespace ExploreTogether.Server;

/// <summary>Server-side state for one connected peer.</summary>
public sealed class PeerSession(long peer)
{
    public long Peer { get; } = peer;
    public AccountRecord? Account { get; set; }
    public CharacterRecord? Character { get; set; }
    /// <summary>The character's inventory while playing (loaded on character select).</summary>
    public Inventory? Inventory { get; set; }
    /// <summary>The character's equipment (12 slots), loaded with the inventory.</summary>
    public Gear? Gear { get; set; }
    /// <summary>The character's hub storage (50 slots), loaded with the inventory. Usable only at the hub's storage chest.</summary>
    public Inventory? Storage { get; set; }
    /// <summary>The body this player is searching (node name of a dead city resident), if any.</summary>
    public string? LootingNpc { get; set; }
    /// <summary>The body this player is dragging (node name), if any, and where it was picked up.</summary>
    public string? DraggingNpc { get; set; }
    public Vector3 DragStart { get; set; }
    /// <summary>The standing resident this player is holding (node name), if any, and since when (server time).</summary>
    public string? HoldingNpc { get; set; }
    public double HoldingSince { get; set; }
    /// <summary>The resident holding this player (node name), if any, and how hard the player has struggled so far.</summary>
    public string? HeldByNpc { get; set; }
    public float Struggle { get; set; }
    /// <summary>A lock being picked: the door's parcel, when it's done, and where the player stood.</summary>
    public (int Parcel, double DoneAt, Vector3 At, int Health)? Picking { get; set; }
    public double LastKickAt { get; set; } = -1000;
    /// <summary>The resident held was grabbed by surprise (from behind, unseen); when they next try to wriggle free.</summary>
    public bool HoldingSurprised { get; set; }
    public double NextEscapeRoll { get; set; }
    public double LastPulledAt { get; set; }
    /// <summary>Stealth: when this player last did something violent (attacked, fired) or broke into somewhere.</summary>
    public double LastViolentAt { get; set; } = -1000;
    public double LastBreakingAt { get; set; } = -1000;
    /// <summary>Stealth: last position sample (for running), when the player last made footstep noise, and the HUD alert sent.</summary>
    public Vector3 LastSample { get; set; }
    public double LastSampleAt { get; set; }
    public double LastFootstepAt { get; set; }
    public float SampledSpeed { get; set; }
    public int AlertSent { get; set; }
    /// <summary>
    /// Item events for changes not saved yet (slot moves are saved with the next save, not immediately).
    /// They're written in the same transaction as that save, so the log always matches the saved items.
    /// </summary>
    public List<ItemEvent> PendingItemEvents { get; } = new();
    /// <summary>Health while playing (full again whenever the player reaches the hub).</summary>
    public Core.Combat.Health Health { get; } = new(100);
    public double LastAttackAt { get; set; } = -1000;
    public double LastGatherAt { get; set; } = -1000;
    /// <summary>Instance the client was told to load and hasn't confirmed yet.</summary>
    public int? PendingInstanceId { get; set; }
    /// <summary>True while a password hash is being computed for this peer.</summary>
    public bool AuthInProgress { get; set; }
    /// <summary>When play time was last added to the database for this character.</summary>
    public DateTime PlayTimeCountedUntil { get; set; }
}

/// <summary>
/// Server-only root. Listens for clients and runs the session flow:
///   connect -> Login/Register -> character list -> Create/SelectCharacter
///   -> EnterInstance(hub) -> client builds it -> InstanceReady -> player spawned
///   hub: Interact(dungeon_entrance) -> dungeon menu -> CreateDungeon/JoinDungeon -> EnterInstance(dungeon)
///   dungeon: Interact(dungeon_entrance) -> back to the hub
///   hub: Interact(city_gate) -> EnterInstance(city), joining an occupied city or generating a new one (ServerGame.City); city: Interact(city_gate) -> back
///   anywhere: Interact(resource:NAME) -> gather (ServerGame.Gathering); hub: Interact(hub_storage) -> storage (ServerGame.Storage)
/// Owns the database; character progress is saved on disconnect, every AutosaveSeconds, and at shutdown.
/// </summary>
public partial class ServerGame : Node
{
    private const int MaxClients = 64;
    private const double AutosaveSeconds = 15;
    private const string DungeonEntranceId = "dungeon_entrance";

    /// <summary>Where players reappear in the hub after a dungeon: in front of the entrance, facing away from it.</summary>
    private static readonly SavedPosition HubReturnPoint = new(0f, 0.5f, 16f, 0f);
    /// <summary>Where players reappear in the hub after a city: in front of the City Gate.</summary>
    private static readonly SavedPosition CityReturnPoint = new(-6f, 0.5f, 16f, 0f);

    private NetSession _session = null!;
    private InstanceManager _instances = null!;
    private GameDatabase _db = null!;
    private readonly Dictionary<long, PeerSession> _sessions = new();

    // Dev/test switches.
    private static readonly bool DevSkipProximity = CmdArgs.Has("dev-skip-proximity");
    private static readonly TimeSpan EmptyDungeonTimeout = TimeSpan.FromSeconds(CmdArgs.GetFloat("dungeon-empty-timeout", 60));

    public override void _Ready()
    {
        var main = GetParent<Main>();
        _session = main.Session;

        var dbPath = ServerPaths.DatabasePath();
        try
        {
            _db = GameDatabase.Open(dbPath);
        }
        catch (Exception e)
        {
            // Includes DatabaseTooNewException and a failing migration (the file stays at its last good version).
            GD.PushError($"[server] could not open database {dbPath}: {e.Message}");
            _db = null!;
            GetTree().Quit(1);
            return;
        }
        if (_db.BackupPath != null)
            GD.Print($"[server] backed up database before upgrading: {_db.BackupPath}");
        GD.Print($"[server] database {dbPath} (schema v{_db.SchemaVersion})");
        if (RunItemToolIfRequested()) return; // --audit-items, --item-history, --item-log: report and quit
        AuditItemsAtStartup();

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
        SetUpRequestLimits();
        Multiplayer.PeerConnected += id =>
        {
            _sessions[id] = new PeerSession(id);
            GD.Print($"[server] peer {id} connected");
        };
        Multiplayer.PeerDisconnected += OnPeerDisconnected;

        _session.LoginRequested += OnLogin;
        _session.RegisterRequested += OnRegister;
        _session.CreateCharacterRequested += OnCreateCharacter;
        _session.SelectCharacterRequested += OnSelectCharacter;
        _session.InstanceReadyReceived += OnInstanceReady;
        _session.InteractRequested += OnInteract;
        _session.CreateDungeonRequested += OnCreateDungeon;
        _session.JoinDungeonRequested += OnJoinDungeon;
        _session.MoveItemRequested += OnMoveItem;
        SetUpCombat();
        SetUpCityActions();
        _session.DropItemRequested += OnDropItem;
        _session.LockDoorRequested += OnLockDoor;
        _session.DragBodyRequested += OnDragBody;
        _session.PlayerTransformReceived += (peer, instanceId, position, yaw) =>
        {
            // Ignore movement for an instance the player has already left.
            if (_instances.FindPlayer(peer) is { } f && f.Instance.Id == instanceId && AcceptMove(peer, f.Player, position))
                f.Player.ApplySubmittedTransform(position, yaw); // a body being dragged follows in the physics tick
        };

        var autosave = new Timer { WaitTime = AutosaveSeconds, Autostart = true };
        autosave.Timeout += SaveAll;
        AddChild(autosave);

        var cleanup = new Timer { WaitTime = 1, Autostart = true };
        cleanup.Timeout += () => _instances.CloseEmptyInstances(EmptyDungeonTimeout,
            _sessions.Values.Where(s => s.PendingInstanceId != null).Select(s => s.PendingInstanceId!.Value).ToHashSet());
        AddChild(cleanup);

        GD.Print($"[server] listening on port {port}");
    }

    public override void _ExitTree()
    {
        if (_db == null) return;
        SaveAll();
        foreach (var d in _instances.Dungeons)
            if (d.RunId is { } runId) _db.EndRun(runId, "interrupted");
        _db.Dispose();
        GD.Print("[server] saved and closed database");
    }

    // ------------------------------------------------------------------ auth

    private async void OnLogin(long peer, string username, string password)
    {
        if (!_sessions.TryGetValue(peer, out var s) || s.AuthInProgress) return;
        if (s.Account != null)
        {
            _session.SendAuthResult(peer, false, "already_logged_in", "Already logged in.");
            return;
        }

        var account = _db.FindAccount(username.StripEdges());
        s.AuthInProgress = true;
        // Hashing is slow on purpose; do it off the game thread. 'await' resumes on the game thread.
        var ok = account != null && await Task.Run(() => PasswordHasher.Verify(password, account.PasswordHash));
        s.AuthInProgress = false;
        if (!_sessions.ContainsKey(peer)) return; // disconnected meanwhile

        if (!ok)
        {
            _session.SendAuthResult(peer, false, "invalid", "Invalid username or password.");
            return;
        }
        CompleteLogin(s, account!);
    }

    private async void OnRegister(long peer, string username, string password)
    {
        if (!_sessions.TryGetValue(peer, out var s) || s.AuthInProgress) return;
        if (s.Account != null)
        {
            _session.SendAuthResult(peer, false, "already_logged_in", "Already logged in.");
            return;
        }

        username = username.StripEdges();
        var error = GameRules.ValidateUsername(username) ?? GameRules.ValidatePassword(password);
        if (error != null)
        {
            _session.SendAuthResult(peer, false, "invalid", error);
            return;
        }
        if (_db.FindAccount(username) != null)
        {
            _session.SendAuthResult(peer, false, "taken", "That username is taken.");
            return;
        }

        s.AuthInProgress = true;
        var hash = await Task.Run(() => PasswordHasher.Hash(password));
        s.AuthInProgress = false;
        if (!_sessions.ContainsKey(peer)) return;

        var account = _db.CreateAccount(username, hash);
        if (account == null) // registered by someone else while we were hashing
        {
            _session.SendAuthResult(peer, false, "taken", "That username is taken.");
            return;
        }
        GD.Print($"[server] registered account '{username}'");
        CompleteLogin(s, account);
    }

    private void CompleteLogin(PeerSession s, AccountRecord account)
    {
        if (_sessions.Values.Any(o => o.Account?.Id == account.Id))
        {
            _session.SendAuthResult(s.Peer, false, "already_online", "That account is already logged in.");
            return;
        }
        s.Account = account;
        _db.RecordLogin(account.Id);
        GD.Print($"[server] peer {s.Peer} logged in as '{account.Username}'");
        _session.SendAuthResult(s.Peer, true, "ok", "");
        SendCharacterList(s);
    }

    // ------------------------------------------------------------ characters

    private void SendCharacterList(PeerSession s)
    {
        var list = _db.ListCharacters(s.Account!.Id)
            .Select(c => new CharacterSummary(c.Id, c.Name, ColorFromHex(c.ColorHex), c.PlayTimeSeconds, c.Hardcore));
        _session.SendCharacterList(s.Peer, list);
    }

    private void OnCreateCharacter(long peer, string name, Color color, bool hardcore)
    {
        if (!_sessions.TryGetValue(peer, out var s) || s.Account == null || s.Character != null) return;

        name = name.StripEdges();
        var error = GameRules.ValidateCharacterName(name);
        if (error != null)
        {
            _session.SendCharacterResult(peer, false, error);
            return;
        }

        var hex = PlayerLook.Sanitize(color).ToHtml(false);
        var (result, character) = _db.CreateCharacter(s.Account.Id, name, hex, hardcore);
        switch (result)
        {
            case CreateResult.NameTaken:
                _session.SendCharacterResult(peer, false, "That character name is taken.");
                return;
            case CreateResult.LimitReached:
                _session.SendCharacterResult(peer, false, $"You can have at most {GameRules.MaxCharactersPerAccount} characters.");
                return;
        }
        GD.Print($"[server] '{s.Account.Username}' created {(hardcore ? "hardcore " : "")}character '{character!.Name}'");
        if (DevStarterKit) GiveStarterKit(character);
        _session.SendCharacterResult(peer, true, "");
        SendCharacterList(s);
    }

    private void OnSelectCharacter(long peer, long characterId)
    {
        if (!_sessions.TryGetValue(peer, out var s) || s.Account == null || s.Character != null) return;

        var character = _db.GetCharacter(characterId);
        if (character == null || character.AccountId != s.Account.Id)
        {
            _session.SendCharacterResult(peer, false, "Character not found.");
            return;
        }

        s.Character = character;
        s.Inventory = new Inventory();
        s.Inventory.Load(_db.LoadInventory(character.Id));
        s.Gear = new Gear();
        s.Gear.Load(_db.LoadGearRecords(character.Id));
        s.Storage = new Inventory(Inventory.HubStorageSlots);
        s.Storage.Load(_db.LoadStorage(character.Id));
        OnCharacterItemsLoaded(s); // serials for old items, and a duplicate check against everyone online
        s.PlayTimeCountedUntil = DateTime.UtcNow;
        SendToInstance(s, _instances.Hub);
    }

    // ------------------------------------------------------------- instances

    /// <summary>Removes the player from where they are and tells their client to load the target.</summary>
    private async void SendToInstance(PeerSession s, ServerInstance target)
    {
        LetGoOfBody(s, notify: true);
        var from = _instances.FindPlayer(s.Peer)?.Instance;
        if (from?.Kind == InstanceKinds.City) OnLeavingCity(s, from);
        if (from?.Kind == InstanceKinds.Dungeon) OnLeavingDungeon(s, from, connected: true);
        _instances.RemovePeer(s.Peer);
        if (from?.Kind == InstanceKinds.Dungeon) OnLeftDungeon(from);
        s.PendingInstanceId = target.Id;

        // Replication sends the resulting despawns on the next network frames. Wait for them to go
        // out first, so the client clears the old instance's players before it tears the instance down.
        for (var i = 0; i < 2; i++)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        if (!_sessions.ContainsKey(s.Peer) || s.PendingInstanceId != target.Id) return;

        _session.SendEnterInstance(s.Peer, target.Id, target.Kind, target.LayoutData);
    }

    private void OnInstanceReady(long peer, int instanceId)
    {
        if (!_sessions.TryGetValue(peer, out var s) || s.Character == null || s.PendingInstanceId != instanceId)
        {
            GD.PushWarning($"[server] unexpected InstanceReady({instanceId}) from peer {peer}");
            return;
        }
        s.PendingInstanceId = null;
        var instance = _instances.Get(instanceId);
        if (instance == null)
        {
            // Closed while the client was loading; fall back to the hub.
            _session.SendNotice(peer, "That dungeon has closed.");
            SendToInstance(s, _instances.Hub);
            return;
        }

        var c = s.Character;
        if (instance == _instances.Hub) s.Health.Reset();
        var saved = instance == _instances.Hub ? c.HubPosition : null;
        _instances.AddPlayer(instance, peer, c.Name, ColorFromHex(c.ColorHex),
            saved is { } p ? new Vector3(p.X, p.Y, p.Z) : null, saved?.Yaw ?? 0f);

        SendInventory(s);
        SyncHealth(s);
        if (instance.Kind == InstanceKinds.Dungeon)
        {
            if (instance.RunId is { } runId) _db.AddRunParticipant(runId, c.Id);
            BroadcastDungeonStatus(instance);
        }
        if (instance.Kind == InstanceKinds.City) OnEnteredCity(s, instance);
    }

    private void OnInteract(long peer, string interactionId)
    {
        if (!_sessions.TryGetValue(peer, out var s) || !IsNear(s, interactionId, out var instance)) return;

        if (interactionId.StartsWith(Items.ItemPickup.InteractionPrefix))
        {
            PickUp(s, instance, interactionId);
            return;
        }
        if (interactionId.StartsWith(Gathering.ResourceNode.InteractionPrefix))
        {
            Gather(s, instance, interactionId);
            return;
        }
        if (interactionId == HubStorageId && instance == _instances.Hub)
        {
            OpenStorage(s);
            return;
        }
        if (interactionId == DebugSupplyId && instance == _instances.Hub)
        {
            OpenSupply(s);
            return;
        }
        if (interactionId == LocksmithId && instance == _instances.Hub)
        {
            TradeInKeys(s);
            return;
        }
        if (interactionId == DungeonExit.Id && instance.Kind == InstanceKinds.Dungeon)
        {
            UseExit(s, instance);
            return;
        }
        if (interactionId == City.CityBuilder.GateId)
        {
            UseCityGate(s, instance);
            return;
        }
        if (instance.Kind == InstanceKinds.City && City.CityDoor.TryParse(interactionId, out var doorParcel))
        {
            UseDoor(s, instance, doorParcel);
            return;
        }
        if (instance.Kind == InstanceKinds.City && interactionId.StartsWith(City.CityNpc.LootPrefix))
        {
            SearchBody(s, instance, interactionId[City.CityNpc.LootPrefix.Length..]);
            return;
        }
        if (interactionId != DungeonEntranceId) return;
        if (instance == _instances.Hub)
        {
            _session.SendDungeonMenu(peer, _instances.Dungeons.Select(d => new DungeonSummary(
                d.Id, d.CreatedBy, d.Layout!.Width, d.Layout.Height, d.Layout.Length,
                d.Players.Values.Select(p => p.DisplayName).ToArray())));
        }
        else
        {
            GD.Print($"[server] '{s.Character!.Name}' returns to the hub from instance {instance.Id}");
            SendToInstance(s, _instances.Hub);
        }
    }

    private void OnCreateDungeon(long peer, int width, int height, int length)
    {
        if (!_sessions.TryGetValue(peer, out var s) || !IsNear(s, DungeonEntranceId, out var instance)
            || instance != _instances.Hub) return;

        LeaveHubViaEntrance(s);
        var dungeon = _instances.CreateDungeon(width, height, length, s.Character!.Name);
        var layout = dungeon.Layout!;
        dungeon.RunId = _db.CreateRun(layout.Seed, layout.Width, layout.Height, layout.Length, s.Character.Id);
        SendToInstance(s, dungeon);
    }

    private void OnJoinDungeon(long peer, int instanceId)
    {
        if (!_sessions.TryGetValue(peer, out var s) || !IsNear(s, DungeonEntranceId, out var instance)
            || instance != _instances.Hub) return;

        var dungeon = _instances.Get(instanceId);
        if (dungeon == null || dungeon.Kind != InstanceKinds.Dungeon)
        {
            _session.SendNotice(peer, "That dungeon no longer exists.");
            return;
        }
        LeaveHubViaEntrance(s);
        SendToInstance(s, dungeon);
    }

    /// <summary>Saves progress with the hub position set to the return point, so a later login doesn't start inside the entrance.</summary>
    private void LeaveHubViaEntrance(PeerSession s) => Save(s, HubReturnPoint);

    /// <summary>True if the peer is spawned in an instance with that interactable, within reach.</summary>
    private bool IsNear(PeerSession s, string interactionId, out ServerInstance instance)
    {
        instance = null!;
        if (s.Character == null || _instances.FindPlayer(s.Peer) is not { } found) return false;
        instance = found.Instance;
        var target = InstanceFactory.FindInteractable(found.Instance.Nodes.Container, interactionId);
        if (target == null) return false;
        if (DevSkipProximity) return true;

        var distance = found.Player.NetPosition.DistanceTo(target.GlobalPosition);
        if (distance <= Interactable.ServerMaxDistance) return true;
        GD.Print($"[server] rejected '{interactionId}' from peer {s.Peer}: {distance:0.0} m away");
        return false;
    }

    // ---------------------------------------------------------------- saving

    private void OnPeerDisconnected(long peer)
    {
        GD.Print($"[server] peer {peer} disconnected");
        var from = _instances.FindPlayer(peer)?.Instance;
        if (_sessions.TryGetValue(peer, out var leaving))
        {
            LetGoOfBody(leaving, notify: false);
            if (from?.Kind == InstanceKinds.City) OnLeavingCity(leaving, from, connected: false);
        }
        if (_sessions.Remove(peer, out var s))
        {
            if (from?.Kind == InstanceKinds.Dungeon) OnLeavingDungeon(s, from, connected: false);
            Save(s);
        }
        _instances.RemovePeer(peer, stillConnected: false);
        if (from?.Kind == InstanceKinds.Dungeon) OnLeftDungeon(from);
    }

    private void SaveAll()
    {
        foreach (var s in _sessions.Values) Save(s);
    }

    /// <param name="hubPositionOverride">Hub position to store instead of the current one.</param>
    private void Save(PeerSession s, SavedPosition? hubPositionOverride = null)
    {
        if (s.Character == null) return;

        var now = DateTime.UtcNow;
        var playDelta = (now - s.PlayTimeCountedUntil).TotalSeconds;
        s.PlayTimeCountedUntil = now;

        var hubPos = hubPositionOverride;
        if (hubPos == null && _instances.FindPlayer(s.Peer) is { } found && found.Instance == _instances.Hub)
        {
            var pos = found.Player.NetPosition;
            hubPos = new SavedPosition(pos.X, pos.Y, pos.Z, found.Player.NetYaw);
        }

        _db.SaveCharacterProgress(s.Character.Id, playDelta, hubPos);
        SaveInventory(s);
        s.Character = s.Character with
        {
            PlayTimeSeconds = s.Character.PlayTimeSeconds + playDelta,
            HubPosition = hubPos ?? s.Character.HubPosition,
        };
    }

    private static Color ColorFromHex(string hex) => Color.FromString("#" + hex, PlayerLook.DefaultColor);

    /// <summary>Dev (--dev-starter-kit): new characters start with one of each weapon and a helmet.</summary>
    private static readonly bool DevStarterKit = CmdArgs.Has("dev-starter-kit");

    private void GiveStarterKit(CharacterRecord character)
    {
        const string origin = "dev starter kit";
        var inv = new Inventory();
        var events = new List<ItemEvent>();
        foreach (var id in new[] { 1515, 1337, 567, 666, 8, 22, ItemCatalog.BatId, ItemCatalog.DoubleBarrelId, ItemCatalog.PumpShotgunId })
        {
            var uid = _db.RegisterItem(id, origin);
            inv.AddItem(id, uid: uid);
            events.Add(new ItemEvent(ItemActions.Created, id, 1, uid, character.Id, character.Name, Detail: origin));
        }
        foreach (var (id, count) in new[] { (ItemCatalog.PistolRoundsId, 30), (ItemCatalog.SlugId, 20), (ItemCatalog.LockpickId, 6) })
        {
            inv.AddItems(id, count);
            events.Add(new ItemEvent(ItemActions.Created, id, count, null, character.Id, character.Name, Detail: origin));
        }
        _db.SaveItems(character.Id, inv.Occupied(), [], events);
    }
}
