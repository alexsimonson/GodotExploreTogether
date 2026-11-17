using System.Collections.Generic;
using System.Linq;
using ExploreTogether.Core;
using ExploreTogether.Core.Items;
using ExploreTogether.Dungeon;
using ExploreTogether.Net;
using ExploreTogether.Player;
using ExploreTogether.UI;
using Godot;

namespace ExploreTogether.Client;

/// <summary>
/// Client-only root. Runs the menu flow (login -> character select), connects to the
/// server, and builds whichever instance the server sends this client into.
///
/// Dev auto-login (--user NAME [--password P] [--character NAME] [--color C]):
/// registers the account if it doesn't exist (otherwise logs in), creates the character
/// if missing, and plays it, with no clicks needed.
///
/// Dev dungeon automation (needs the server's --dev-skip-proximity):
///   --dev-dungeon new[:WxHxLEN] | join:NAME   on reaching the hub, start a dungeon / join the one NAME started
///   --dev-check-walls               in a dungeon, verify the built walls match the maze layout
///   --dev-solve                     in a dungeon, pick up every item, open the exit and escape
///   --dev-escape-after SECONDS      in a dungeon, use the exit after this long
///   --dev-return-after SECONDS      in a dungeon, go back to the hub after this long
///
/// Dev city automation (needs the server's --dev-skip-proximity):
///   --dev-city                      on reaching the hub, go through the City Gate
///   --dev-return-after SECONDS      (also) in the city, go back to the hub after this long
///   --dev-overview angle|top|near   in the city, view it from a fixed camera (high above, or low over the start)
///   --dev-check-city                in the city, raycast every doorway, gate fence and front wall against the layout
/// </summary>
public partial class ClientGame : Node
{
    private Main _main = null!;
    private NetSession _session = null!;
    private LoginMenu _loginMenu = null!;
    private CharacterMenu _characterMenu = null!;
    private Label _promptLabel = null!;
    private Label _noticeLabel = null!;
    private DungeonMenu _dungeonMenu = null!;
    private CityMenu _cityMenu = null!;
    private HotbarBar _hotbar = null!;
    private int[] _lastHotbar = [];
    private bool _devHotbarDone, _shownFlashlight;

    /// <summary>The dungeon entrance's or City Gate's menu is open (the mouse is free; no walking or attacking).</summary>
    private bool MenuOpen => _dungeonMenu.Visible || _cityMenu.Visible;
    private InventoryScreen _inventory = null!;
    private Label _weaponLabel = null!;
    private Label _statusLabel = null!;
    private Interactable? _focused;
    private double _noticeTimeLeft;

    private LoginMenu.AuthAction _pendingAction;
    private string _username = "";
    private string _password = "";
    private int? _currentInstanceId;
    private string _currentKind = "";

    // Dev auto-login state.
    private readonly bool _devAuto = CmdArgs.Has("user");
    private bool _devCreateAttempted;
    private readonly string _devDungeon = CmdArgs.Get("dev-dungeon");
    private bool _devDungeonDone;
    private bool _devCityDone;
    private readonly float _devReturnAfter = CmdArgs.GetFloat("dev-return-after", 0);

    public override void _Ready()
    {
        _main = GetParent<Main>();
        _session = _main.Session;
        AddChild(new Audio.GameAudio { Name = "GameAudio" });
        _session.SoundReceived += (sound, at) => Audio.GameAudio.Instance?.PlayServerSound(sound, at);

        _loginMenu = new LoginMenu { Name = "LoginMenu" };
        AddChild(_loginMenu);
        _loginMenu.Submitted += StartAuth;

        _characterMenu = new CharacterMenu { Name = "CharacterMenu", Visible = false };
        AddChild(_characterMenu);
        _characterMenu.PlayRequested += id =>
        {
            _characterMenu.SetBusy(true, "Entering world...");
            _session.SendSelectCharacter(id);
        };
        _characterMenu.CreateRequested += (name, color, hardcore) =>
        {
            _characterMenu.SetStatus("Creating...");
            _session.SendCreateCharacter(name, color, hardcore);
        };
        _characterMenu.LogoutRequested += () => ResetToLogin("Logged out.");

        Multiplayer.ConnectedToServer += OnConnected;
        Multiplayer.ConnectionFailed += () => ResetToLogin("Could not connect to the server.");
        Multiplayer.ServerDisconnected += () => ResetToLogin("Disconnected from the server.");

        _session.AuthResultReceived += OnAuthResult;
        _session.CharacterListReceived += OnCharacterList;
        _session.CharacterResultReceived += (ok, message) => _characterMenu.SetBusy(false, ok ? "" : message);
        _session.EnterInstanceReceived += OnEnterInstance;
        _session.DungeonMenuReceived += OnDungeonMenu;
        _session.CityMenuReceived += OnCityMenu;
        _session.NoticeReceived += ShowNotice;
        _session.InventoryReceived += OnInventory;
        _session.DungeonStatusReceived += OnDungeonStatus;
        _session.CityClockReceived += OnCityClock;
        _session.DoorStatesReceived += OnDoorStates;
        _session.PowerStateReceived += OnPowerState;
        _session.FurnitureTakenReceived += pieces =>
        {
            if (_currentInstanceId is not { } id) return;
            GD.Print($"[client] furniture taken: {string.Join(", ", pieces)}");
            foreach (var piece in pieces)
                _main.Instances.GetNodeOrNull<City.CityChair>($"{InstanceFactory.NodeName(id)}/Level/Furniture/Furniture{piece}")?.SetTaken(true);
        };
        _session.LootReceived += OnLoot;
        _session.DragStateReceived += OnDragState;

        _inventory = new InventoryScreen { Name = "Inventory", Visible = false };
        AddChild(_inventory);
        _inventory.MoveRequested += (fc, fs, tc, ts) => _session.SendMoveItem((int)fc, fs, (int)tc, ts);
        _inventory.DropRequested += (c, slot) => _session.SendDropItem((int)c, slot);
        _inventory.TakeAllRequested += TakeAllLoot;
        _inventory.QuickTransferRequested += QuickTransfer;
        _inventory.QuickEquipRequested += QuickEquip;

        _dungeonMenu = new DungeonMenu { Name = "DungeonMenu", Visible = false };
        AddChild(_dungeonMenu);
        _dungeonMenu.CreateRequested += (w, h, len) => _session.SendCreateDungeon(w, h, len);
        _dungeonMenu.JoinRequested += id => _session.SendJoinDungeon(id);

        _hotbar = new HotbarBar { Name = "Hotbar", Visible = false };
        AddChild(_hotbar);
        _hotbar.BindRequested += (key, container, slot) => _session.SendSetHotbar(key, (int)container, slot);
        _hotbar.ClearRequested += key => _session.SendSetHotbar(key, -1, -1);
        _session.HotbarReceived += (ids, _) =>
        {
            _lastHotbar = ids;
            _hotbar.Show(ids, _lastInventoryQuads, _lastGear);
        };

        _cityMenu = new CityMenu { Name = "CityMenu", Visible = false };
        AddChild(_cityMenu);
        _cityMenu.CreateRequested += settings => _session.SendCreateCity(settings);
        _cityMenu.JoinRequested += id => _session.SendJoinCity(id);

        var hud = new CanvasLayer { Name = "DebugHud" };
        AddChild(hud);

        _promptLabel = HudLabel(hud, Control.LayoutPreset.CenterBottom, new Vector2(0, -90));
        _noticeLabel = HudLabel(hud, Control.LayoutPreset.CenterTop, new Vector2(0, 40));
        _statusLabel = new Label { Visible = false, HorizontalAlignment = HorizontalAlignment.Right };
        _statusLabel.AddThemeColorOverride("font_outline_color", Colors.Black);
        _statusLabel.AddThemeConstantOverride("outline_size", 6);
        hud.AddChild(_statusLabel);
        _statusLabel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopRight);
        _statusLabel.GrowHorizontal = Control.GrowDirection.Begin;
        _statusLabel.Position += new Vector2(-10, 8);

        // Like the original HUD's WeaponText (bottom right).
        _weaponLabel = new Label { Visible = false, HorizontalAlignment = HorizontalAlignment.Right };
        _weaponLabel.AddThemeColorOverride("font_outline_color", Colors.Black);
        _weaponLabel.AddThemeConstantOverride("outline_size", 6);
        hud.AddChild(_weaponLabel);
        _weaponLabel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomRight);
        _weaponLabel.GrowHorizontal = Control.GrowDirection.Begin;
        _weaponLabel.GrowVertical = Control.GrowDirection.Begin;
        _weaponLabel.Position += new Vector2(-12, -12);

        SetUpCombatClient(hud);
        SetUpStealthClient(hud);
        SetUpStorageClient();

        if (_devAuto)
        {
            // Try to register first; if the account already exists, OnAuthResult falls back to logging in.
            var address = CmdArgs.Get("connect", $"127.0.0.1:{CmdArgs.GetInt("port", GameInfo.DefaultPort)}");
            CallDeferred(MethodName.StartAuthDeferred, (int)LoginMenu.AuthAction.Register, address,
                CmdArgs.Get("user"), CmdArgs.Get("password", "devpass"));
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (HandleStealthInput(@event) || HandleCombatInput(@event))
        {
            GetViewport().SetInputAsHandled();
            return;
        }
        if (@event.IsActionPressed(InputSetup.Interact) && _focused != null && !MenuOpen)
        {
            NoteInteraction(_focused.InteractionId);
            _session.SendInteract(_focused.InteractionId);
        }
        else if (@event.IsActionPressed(InputSetup.DragBody) && !MenuOpen && _currentKind == InstanceKinds.City)
        {
            // G: let go of the body being dragged, or take hold of the one looked at.
            if (_draggingName != "") _session.SendDragBody("");
            else if (_focused?.InteractionId.StartsWith(City.CityNpc.LootPrefix) == true) _session.SendDragBody(_focused.InteractionId);
        }
        else if (@event.IsActionPressed(InputSetup.ToggleLight) && _currentInstanceId != null && !MenuOpen && !_inventory.Visible)
        {
            _session.SendCityAction("flashlight", "");
        }
        else if (HotbarKey(@event) is { } key && _currentInstanceId != null && !MenuOpen)
        {
            // Over an inventory or equipment slot: put that item on the key. Otherwise: use the key.
            if (_inventory.Visible && ItemSlotButton.Hovered is { ItemId: not null, Container: ItemContainer.Inventory or ItemContainer.Gear } slot)
                _session.SendSetHotbar(key, (int)slot.Container, slot.Index);
            else if (!_inventory.Visible)
                _session.SendUseHotbar(key);
        }
        else if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Tab }
                 && _currentInstanceId != null && !MenuOpen)
        {
            _inventory.Toggle();
            Audio.GameAudio.Instance?.Play2D(_inventory.Visible ? "ui_open" : "ui_close", -8);
        }
    }

    /// <summary>1 to 6 (top row or keypad) as hotbar key 0 to 5, else null.</summary>
    private static int? HotbarKey(InputEvent @event) => @event is InputEventKey { Pressed: true, Echo: false } k
        ? k.PhysicalKeycode switch
        {
            >= Key.Key1 and <= Key.Key6 => (int)(k.PhysicalKeycode - Key.Key1),
            >= Key.Kp1 and <= Key.Kp6 => (int)(k.PhysicalKeycode - Key.Kp1),
            _ => null,
        }
        : null;

    public override void _Process(double delta)
    {
        if (_noticeTimeLeft > 0 && (_noticeTimeLeft -= delta) <= 0)
            _noticeLabel.Visible = false;
        CombatProcess(delta);
        if (PlayerCharacter.Local?.NetFlashlight is { } lit && lit != _shownFlashlight)
        {
            _shownFlashlight = lit;
            UpdateWeaponLabel();
        }
        StorageProcess(delta);
        CityProcess(delta);
        StealthProcess(delta);
        DevStealthProcess(delta);
    }

    public override void _PhysicsProcess(double delta) => UpdateFocus();

    /// <summary>Looks along the local camera for an interactable within reach and shows its prompt.</summary>
    private void UpdateFocus()
    {
        _focused = null;
        var player = PlayerCharacter.Local;
        if (player != null && Input.MouseMode == Input.MouseModeEnum.Captured && !MenuOpen)
        {
            var camera = player.Camera;
            var from = camera.GlobalPosition;
            var to = from - camera.GlobalBasis.Z * Interactable.ReachDistance;
            var query = PhysicsRayQueryParameters3D.Create(from, to, exclude: [player.GetRid()]);
            var hit = camera.GetWorld3D().DirectSpaceState.IntersectRay(query);
            // (A living resident's body is an interactable with no id yet: nothing to do with them for now.)
            if (hit.Count > 0 && hit["collider"].AsGodotObject() is Interactable { InteractionId.Length: > 0 } target)
                _focused = target;
        }
        _promptLabel.Visible = _focused != null;
        if (_focused != null) _promptLabel.Text = _focused.Prompt.StartsWith('[') ? _focused.Prompt : $"[E] {_focused.Prompt}";
    }

    private void ShowNotice(string message)
    {
        GD.Print($"[client] notice: {message}");
        _noticeLabel.Text = message;
        _noticeLabel.Visible = true;
        _noticeTimeLeft = 4;
    }

    private void OnInventory(int[] quads, int[] gear, int[] gearData)
    {
        _lastInventoryQuads = quads;
        _lastGear = gear;
        _inventory.SetState(quads, gear, gearData);
        _hotbar.Show(_lastHotbar, quads, gear);
        _hotbar.Visible = true;
        UpdateEquippedWeapon(gear);
        _weaponLabel.Visible = true;

        var names = new List<string>();
        for (int i = 0; i + 3 < quads.Length; i += 4)
            names.Add(Core.Items.ItemCatalog.NameOf(quads[i + 1])
                      + (quads[i + 2] > 1 ? $" x{quads[i + 2]}" : "")
                      + (quads[i + 3] >= 0 ? $" ({quads[i + 3]} loaded)" : ""));
        var equipped = new List<string>();
        for (int i = 0; i < gear.Length; i++)
            if (gear[i] >= 0) equipped.Add($"{(Core.Items.EquipmentType)i}={Core.Items.ItemCatalog.NameOf(gear[i])}"
                                           + (i < gearData.Length && gearData[i] >= 0 ? $" ({gearData[i]} loaded)" : ""));
        GD.Print($"[client] inventory ({names.Count} slots): {string.Join(", ", names)} | gear: {string.Join(", ", equipped)}");
        DevInventoryActions(quads, gear);
    }

    /// <summary>
    /// --dev-equip: equip every item that fits an empty gear slot. --dev-drop NAME: drop the first stack of NAME ("*" = any).
    /// Each runs once per inventory update and stops when there's nothing left to do.
    /// </summary>
    private void DevInventoryActions(int[] quads, int[] gear)
    {
        if (_currentKind != InstanceKinds.Hub) return;
        if (CmdArgs.Has("dev-equip"))
        {
            for (int i = 0; i + 3 < quads.Length; i += 4)
            {
                var def = Core.Items.ItemCatalog.Get(quads[i + 1]);
                if (def?.Slot is not { } type || gear[(int)type] >= 0) continue;
                _session.SendMoveItem((int)Core.Items.ItemContainer.Inventory, quads[i], (int)Core.Items.ItemContainer.Gear, (int)type);
                return; // one move per update; the next update continues
            }
        }
        // --dev-weapon NAME: equip that weapon (from the inventory) before heading out.
        if (CmdArgs.Get("dev-weapon") is { Length: > 0 } weaponName)
        {
            var slot = (int)Core.Items.EquipmentType.Weapon;
            var current = gear[slot] >= 0 ? Core.Items.ItemCatalog.NameOf(gear[slot]) : "";
            if (current != weaponName)
                for (int i = 0; i + 3 < quads.Length; i += 4)
                    if (Core.Items.ItemCatalog.NameOf(quads[i + 1]) == weaponName)
                    {
                        _session.SendMoveItem((int)Core.Items.ItemContainer.Inventory, quads[i], (int)Core.Items.ItemContainer.Gear, slot);
                        return;
                    }
        }
        if (CmdArgs.Get("dev-drop") is { Length: > 0 } dropName && !_devDropDone)
        {
            for (int i = 0; i + 3 < quads.Length; i += 4)
            {
                if (dropName != "*" && Core.Items.ItemCatalog.NameOf(quads[i + 1]) != dropName) continue;
                _devDropDone = true;
                _session.SendDropItem((int)Core.Items.ItemContainer.Inventory, quads[i]);
                return;
            }
        }
    }

    private bool _devDropDone;

    private void OnDungeonStatus(DungeonStatus status)
    {
        if (_currentInstanceId is { } id
            && InstanceFactory.FindInteractable(_main.Instances, DungeonExit.Id) is DungeonExit exit
            && exit.IsOpen != status.ExitOpen)
            exit.SetOpen(status.ExitOpen);

        var pass = status.ExitOpen ? "used" : status.PassHolder == "" ? "not found" : $"held by {status.PassHolder}";
        var escaped = status.Escaped.Length == 0 ? "nobody yet" : string.Join(", ", status.Escaped);
        _statusLabel.Text = $"Dungeon Pass: {pass}\nExit: {(status.ExitOpen ? "OPEN" : "locked")}\n" +
                            $"Escaped: {escaped}\nStill inside: {status.PlayersInside}";
        _statusLabel.Visible = true;
        GD.Print($"[client] dungeon status: pass {pass}, exit {(status.ExitOpen ? "open" : "locked")}, escaped [{escaped}], inside {status.PlayersInside}");
    }

    private void OnDungeonMenu(List<DungeonSummary> dungeons)
    {
        GD.Print($"[client] dungeon menu: {dungeons.Count} running");
        if (DevDungeonStep(dungeons)) return;
        _dungeonMenu.Open(dungeons);
    }

    private void OnCityMenu(List<CitySummary> cities, CitySettings defaults)
    {
        GD.Print($"[client] city menu: {cities.Count} running");
        if (CmdArgs.Has("dev-city"))
        {
            _session.SendJoinCity(0); // dev: wherever the others are, or a new city with the server's defaults
            return;
        }
        _cityMenu.Open(cities, defaults);
    }

    private static Label HudLabel(CanvasLayer hud, Control.LayoutPreset preset, Vector2 offset)
    {
        var label = new Label { Visible = false, HorizontalAlignment = HorizontalAlignment.Center };
        label.AddThemeColorOverride("font_outline_color", Colors.Black);
        label.AddThemeConstantOverride("outline_size", 8);
        label.AddThemeFontSizeOverride("font_size", 22);
        hud.AddChild(label);
        label.SetAnchorsAndOffsetsPreset(preset);
        label.GrowHorizontal = Control.GrowDirection.Both;
        label.Position += offset;
        return label;
    }

    // ------------------------------------------------------------------ auth

    private void StartAuthDeferred(int action, string address, string username, string password) =>
        StartAuth((LoginMenu.AuthAction)action, address, username, password);

    private void StartAuth(LoginMenu.AuthAction action, string address, string username, string password)
    {
        _pendingAction = action;
        _username = username;
        _password = password;

        if (IsConnected())
        {
            SendAuth();
            return;
        }

        var (host, port) = ParseAddress(address);
        var peer = new ENetMultiplayerPeer();
        var err = peer.CreateClient(host, port);
        if (err != Error.Ok)
        {
            _loginMenu.SetBusy(false, $"Could not start connection: {err}");
            return;
        }
        Multiplayer.MultiplayerPeer = peer;
        _loginMenu.SetBusy(true, $"Connecting to {host}:{port}...");
        GD.Print($"[client] connecting to {host}:{port}");
    }

    private void OnConnected()
    {
        GD.Print($"[client] connected as peer {Multiplayer.GetUniqueId()}");
        SendAuth();
    }

    private void SendAuth()
    {
        _loginMenu.SetBusy(true, _pendingAction == LoginMenu.AuthAction.Login ? "Logging in..." : "Registering...");
        if (_pendingAction == LoginMenu.AuthAction.Login) _session.SendLogin(_username, _password);
        else _session.SendRegister(_username, _password, _loginMenu.InviteCode);
    }

    private void OnAuthResult(bool ok, string code, string message)
    {
        if (ok)
        {
            GD.Print($"[client] logged in as '{_username}'");
            _password = "";
            _loginMenu.Visible = false;
            _characterMenu.Visible = true;
            _characterMenu.SetBusy(false, "Loading characters...");
            return;
        }

        if (_devAuto && code == "taken" && _pendingAction == LoginMenu.AuthAction.Register)
        {
            _pendingAction = LoginMenu.AuthAction.Login;
            SendAuth();
            return;
        }

        GD.Print($"[client] auth failed: {message}");
        _loginMenu.SetBusy(false, message);
    }

    // ------------------------------------------------------------ characters

    private void OnCharacterList(List<CharacterSummary> characters)
    {
        _characterMenu.SetCharacters(characters);
        _characterMenu.SetBusy(false);
        if (!_devAuto) return;

        var wanted = CmdArgs.Get("character", CmdArgs.Get("user"));
        var existing = characters.FirstOrDefault(c => string.Equals(c.Name, wanted, System.StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            _session.SendSelectCharacter(existing.Id);
        }
        else if (!_devCreateAttempted)
        {
            _devCreateAttempted = true;
            _session.SendCreateCharacter(wanted, PlayerLook.ParseColor(CmdArgs.Get("color"), PlayerLook.DefaultColor),
                CmdArgs.Has("hardcore"));
        }
    }

    // ------------------------------------------------------------- instances

    private void OnEnterInstance(int instanceId, string kind, int[] layoutData)
    {
        ClearInstances();
        _cityHour = -1; // until the city's clock arrives
        _draggingName = "";
        if (kind == InstanceKinds.City)
        {
            _cityLayoutData = layoutData;
            SetUpCityLighting(Core.City.CityLayout.Import(layoutData, City.CityCatalog.Catalog));
        }
        _inventory.CloseLoot();
        _inventory.CloseSupply();
        _dungeonMenu.Visible = false;
        _cityMenu.Visible = false;
        _inventory.Visible = false;
        _statusLabel.Visible = false;
        InstanceFactory.Build(_main.Instances, instanceId, kind, layoutData, isServer: false);
        _currentInstanceId = instanceId;
        _currentKind = kind;
        Audio.GameAudio.Instance?.EnterPlace(kind, _main.Instances.GetNodeOrNull<Node3D>(InstanceFactory.NodeName(instanceId)),
            kind == InstanceKinds.City ? Core.City.CityLayout.Import(layoutData, City.CityCatalog.Catalog) : null);
        _loginMenu.Visible = false;
        _characterMenu.Visible = false;
        GD.Print($"[client] entered instance {instanceId} ({kind})" +
                 (kind == InstanceKinds.Dungeon ? $", {(layoutData.Length - 5) / 7} cells" : ""));
        _session.SendInstanceReady(instanceId);
        DevAfterEnter(kind, instanceId, layoutData);
    }

    // ----------------------------------------------------- dev automation

    private void DevAfterEnter(string kind, int instanceId, int[] layoutData)
    {
        if (kind == InstanceKinds.Dungeon && CmdArgs.Has("dev-solve"))
            GetTree().CreateTimer(1.5).Timeout += () => DevSolve(instanceId);
        if (kind == InstanceKinds.Dungeon && CmdArgs.Has("dev-escape-after"))
            GetTree().CreateTimer(CmdArgs.GetFloat("dev-escape-after", 5)).Timeout += () =>
            {
                if (_currentInstanceId == instanceId) _session.SendInteract(DungeonExit.Id);
            };

        // --dev-screenshot PATH: save what the camera sees a few seconds after arriving, then quit.
        // --dev-open-inventory: open the inventory screen on arrival (for screenshots).
        if (CmdArgs.Has("dev-open-inventory") && !_inventory.Visible)
            GetTree().CreateTimer(0.5).Timeout += () => { if (!_inventory.Visible) _inventory.Toggle(); };

        // In the dungeon when --dev-dungeon is used, in the city with --dev-city, otherwise wherever the client arrives.
        var screenshotKind = CmdArgs.Has("dev-dungeon") ? InstanceKinds.Dungeon : CmdArgs.Has("dev-city") ? InstanceKinds.City : kind;
        if (kind == InstanceKinds.City && CmdArgs.Has("dev-overview")) // after the local player (and its camera) has spawned
            GetTree().CreateTimer(1.5).Timeout += () => DevOverviewCamera(instanceId, CmdArgs.Get("dev-overview"), layoutData);
        if (kind == InstanceKinds.City && CmdArgs.Has("dev-check-city"))
        {
            GetTree().CreateTimer(0.5).Timeout += () =>
            {
                if (PlayerCharacter.Local is not { } p) return;
                var (count, failures) = City.CityChecks.Verify(Core.City.CityLayout.Import(layoutData, City.CityCatalog.Catalog), p.GetWorld3D(),
                    _main.Instances.GetNode($"{InstanceFactory.NodeName(instanceId)}/Level"));
                GD.Print($"[client] city check: {count - failures.Count}/{count} doors, fences and walls match the layout");
                foreach (var f in failures) GD.Print($"[client]   {f}");
            };
            GetTree().CreateTimer(3.0).Timeout += () => { if (_currentInstanceId == instanceId) DevReportNpcs(instanceId); };
        }
        if (kind == InstanceKinds.City && CmdArgs.Has("dev-open-nearest-door"))
            GetTree().CreateTimer(1.0).Timeout += () =>
            {
                // Dev (screenshots): open the nearest unlocked door.
                if (PlayerCharacter.Local is not { } me || _currentInstanceId != instanceId) return;
                var door = _main.Instances.GetNode($"{InstanceFactory.NodeName(instanceId)}/Level").GetChildren()
                    .OfType<City.CityDoor>().Where(d => !d.IsLocked)
                    .MinBy(d => d.GlobalPosition.DistanceTo(me.GlobalPosition));
                if (door == null) return;
                _session.SendInteract(door.InteractionId);
                me.LookAt(new Vector3(door.GlobalPosition.X, me.GlobalPosition.Y, door.GlobalPosition.Z));
            };
        if (kind == InstanceKinds.City && CmdArgs.Has("dev-drag"))
            GetTree().CreateTimer(CmdArgs.GetFloat("dev-drag-delay", 2.5f)).Timeout +=
                () => { if (_currentInstanceId == instanceId) DevDragStart(instanceId); };
        if (kind == InstanceKinds.Hub) GetTree().CreateTimer(0.5).Timeout += DevHubStations;
        if (kind == InstanceKinds.Hub && CmdArgs.Has("dev-hotbar") && !_devHotbarDone)
        {
            // --dev-hotbar: put the Sword on key 1 and the Bat on key 2, then press 1, 2, and 2 again (hands empty).
            _devHotbarDone = true;
            int SlotOf(string name)
            {
                for (var i = 0; i + 3 < _lastInventoryQuads.Length; i += 4)
                    if (ItemCatalog.NameOf(_lastInventoryQuads[i + 1]) == name) return _lastInventoryQuads[i];
                return -1;
            }
            var steps = new (double At, System.Action Do)[]
            {
                (1.0, () => _session.SendSetHotbar(0, (int)ItemContainer.Inventory, SlotOf("Sword"))),
                (1.3, () => _session.SendSetHotbar(1, (int)ItemContainer.Inventory, SlotOf("Bat"))),
                (2.0, () => _session.SendUseHotbar(0)),
                (3.0, () => _session.SendUseHotbar(1)),
                (4.0, () => _session.SendUseHotbar(1)),
                (5.0, () => GD.Print($"[client] dev-hotbar: keys {string.Join(",", _lastHotbar.Select(ItemCatalog.NameOf))}")),
                // The flashlight: into the offhand, switched on; then a two-handed shotgun won't go in the main hand.
                (5.5, () => _session.SendMoveItem((int)ItemContainer.Inventory, SlotOf("Flashlight"), (int)ItemContainer.Gear, (int)EquipmentType.Offhand)),
                (6.0, () => _session.SendCityAction("flashlight", "")),
                (6.5, () => _session.SendMoveItem((int)ItemContainer.Inventory, SlotOf("Pump Shotgun"), (int)ItemContainer.Gear, (int)EquipmentType.Weapon)),
            };
            foreach (var (at, step) in steps) GetTree().CreateTimer(at).Timeout += step;
        }
        if (kind == InstanceKinds.City) DevStealthStart(instanceId);
        if (kind == InstanceKinds.City && CmdArgs.Has("dev-chair"))
        {
            // --dev-chair: pick up the nearest chair (needs the server's --dev-skip-proximity), then hold it.
            GetTree().CreateTimer(2.0).Timeout += () =>
            {
                if (_currentInstanceId != instanceId || PlayerCharacter.Local is not { } me) return;
                var city = Core.City.CityLayout.Import(_cityLayoutData, City.CityCatalog.Catalog);
                var chair = Core.City.Furniture.Generate(city).Where(f => f.Kind == Core.City.FurnitureKind.Chair)
                    .MinBy(f => new Vector2(f.Rect.CenterX - me.GlobalPosition.X, f.Rect.CenterZ - me.GlobalPosition.Z).Length());
                if (chair == null) return;
                GD.Print($"[client] dev-chair: taking chair {chair.Id}");
                _session.SendInteract(City.CityFurniture.IdFor(chair.Id));
            };
            GetTree().CreateTimer(3.0).Timeout += () =>
            {
                for (var i = 0; i + 3 < _lastInventoryQuads.Length; i += 4)
                    if (_lastInventoryQuads[i + 1] == ItemCatalog.ChairId)
                        _session.SendMoveItem((int)ItemContainer.Inventory, _lastInventoryQuads[i], (int)ItemContainer.Gear, (int)EquipmentType.Weapon);
            };
        }
        if (kind == InstanceKinds.City && CmdArgs.Has("dev-doors"))
            GetTree().CreateTimer(2.5).Timeout += () => { if (_currentInstanceId == instanceId) DevDoorsStart(instanceId); };
        if (kind == InstanceKinds.Hub && CmdArgs.Has("dev-city") && !_devCityDone)
        {
            _devCityDone = true;
            GetTree().CreateTimer(1.0).Timeout += () => _session.SendInteract(City.CityBuilder.GateId);
        }
        if (kind == InstanceKinds.City && _devReturnAfter > 0)
            GetTree().CreateTimer(_devReturnAfter).Timeout += () =>
            {
                if (_currentInstanceId == instanceId) _session.SendInteract(City.CityBuilder.GateId);
            };
        if (CmdArgs.Has("dev-screenshot") && kind == screenshotKind)
        {
            GetTree().CreateTimer(CmdArgs.GetFloat("dev-screenshot-delay", 3)).Timeout += () =>
            {
                var path = CmdArgs.Get("dev-screenshot");
                var err = GetViewport().GetTexture().GetImage().SavePng(path);
                GD.Print($"[client] screenshot {path}: {err}");
                GetTree().Quit();
            };
        }
        if (kind == InstanceKinds.Dungeon && CmdArgs.Has("dev-check-walls"))
        {
            // Wait a moment so the new physics bodies are registered.
            GetTree().CreateTimer(0.5).Timeout += () =>
            {
                if (PlayerCharacter.Local is not { } p) return;
                var (count, failures) = DungeonChecks.VerifyWalls(Core.Dungeon.MazeLayout.Import(layoutData), p.GetWorld3D());
                GD.Print($"[client] wall check: {count - failures.Count}/{count} sides match the maze layout");
                foreach (var f in failures) GD.Print($"[client]   {f}");
            };
        }
        if (kind == InstanceKinds.Hub && _devDungeon != "" && !_devDungeonDone)
            GetTree().CreateTimer(1.0).Timeout += () => _session.SendInteract("dungeon_entrance");
        if (kind == InstanceKinds.Dungeon && _devReturnAfter > 0)
            GetTree().CreateTimer(_devReturnAfter).Timeout += () =>
            {
                if (_currentInstanceId == instanceId) _session.SendInteract("dungeon_entrance");
            };
    }

    /// <summary>--dev-overview: a fixed camera high above the city, straight down ("top") or at an angle from the south-west.</summary>
    private void DevOverviewCamera(int instanceId, string mode, int[] layoutData)
    {
        var container = _main.Instances.GetNodeOrNull<Node>(InstanceFactory.NodeName(instanceId));
        if (container == null) return;
        var b = Core.City.CityLayout.Import(layoutData, City.CityCatalog.Catalog).Bounds;
        var center = new Vector3(b.CenterX, 0, b.CenterZ);
        var size = Mathf.Max(b.Width, b.Depth);
        var camera = new Camera3D { Name = "DevOverview", Far = 4000 };
        container.AddChild(camera);
        if (mode == "top")
        {
            camera.Projection = Camera3D.ProjectionType.Orthogonal;
            camera.Size = size * 1.05f;
            camera.Position = center + new Vector3(0, 300, 0);
            camera.Rotation = new Vector3(-Mathf.Pi / 2, Mathf.Pi, 0); // looking straight down, north (+Z) at the top
        }
        else if (mode.StartsWith("lamp"))
        {
            // At eye height on the street, 9 m from the n-th nearest street lamp to the start ("lamp", "lamp2", ...).
            var city = Core.City.CityLayout.Import(layoutData, City.CityCatalog.Catalog);
            var digits = new string(mode.Where(char.IsDigit).ToArray());
            var n = digits.Length > 0 ? int.Parse(digits) - 1 : 0;
            var (lx, lz) = new Core.City.CityLighting(city).Lamps.OrderBy(l => l.X * l.X + l.Z * l.Z).ElementAt(n);
            var (cx, cz) = Core.City.CityLayout.CellCenter(Core.City.CityLayout.CellAt(lx, lz));
            var away = new Vector3(cx - lx, 0, cz - lz).Normalized();
            if (mode.EndsWith("top"))
            {
                camera.Position = new Vector3(lx, 16, lz) + away * 2;
                camera.LookAt(new Vector3(lx, 0, lz), Vector3.Forward);
            }
            else
            {
                camera.Position = new Vector3(lx, 1.7f, lz) + away * 9 + new Vector3(away.Z, 0, -away.X) * 3;
                camera.LookAt(new Vector3(lx, 1.2f, lz), Vector3.Up);
            }
        }
        else if (mode == "near")
        {
            // Low over the start junction, looking along the streets: close enough to see people.
            camera.Position = new Vector3(-10, 14, -10);
            camera.LookAt(new Vector3(18, 0, 18), Vector3.Up);
        }
        else
        {
            camera.Position = center + new Vector3(-size * 0.45f, size * 0.55f, -size * 0.75f);
            camera.LookAt(center, Vector3.Up);
        }
        camera.MakeCurrent();
    }

    /// <summary>--dev-solve: pick up every item in this instance, then open the exit and escape.</summary>
    private async void DevSolve(int instanceId)
    {
        var container = _main.Instances.GetNodeOrNull(InstanceFactory.NodeName(instanceId));
        if (container == null) return;
        foreach (var item in container.GetNode("Items").GetChildren())
            if (item is Items.ItemPickup pickup)
                _session.SendInteract(pickup.InteractionId);
        await ToSignal(GetTree().CreateTimer(1.0), SceneTreeTimer.SignalName.Timeout);
        if (_currentInstanceId != instanceId) return;
        _session.SendInteract(DungeonExit.Id); // unlock with the pass
        await ToSignal(GetTree().CreateTimer(1.0), SceneTreeTimer.SignalName.Timeout);
        if (_currentInstanceId != instanceId) return;
        _session.SendInteract(DungeonExit.Id); // escape
    }

    /// <summary>Handles the dungeon menu automatically for --dev-dungeon. Returns true if handled.</summary>
    private bool DevDungeonStep(List<DungeonSummary> dungeons)
    {
        if (_devDungeon == "" || _devDungeonDone) return false;
        if (_devDungeon == "new" || _devDungeon.StartsWith("new:"))
        {
            // new  or  new:WIDTHxHEIGHTxLENGTH  (default: the original 5x5, length 400)
            int w = 5, h = 5, len = 400;
            var parts = _devDungeon.Length > 4 ? _devDungeon[4..].Split('x') : [];
            if (parts.Length == 3) { int.TryParse(parts[0], out w); int.TryParse(parts[1], out h); int.TryParse(parts[2], out len); }
            _devDungeonDone = true;
            _session.SendCreateDungeon(w, h, len);
            return true;
        }
        if (_devDungeon.StartsWith("join:"))
        {
            var owner = _devDungeon["join:".Length..];
            var target = dungeons.FirstOrDefault(d => string.Equals(d.CreatedBy, owner, System.StringComparison.OrdinalIgnoreCase));
            if (target == null)
            {
                // Not started yet; ask again shortly.
                GetTree().CreateTimer(1.0).Timeout += () => _session.SendInteract("dungeon_entrance");
                return true;
            }
            _devDungeonDone = true;
            _session.SendJoinDungeon(target.Id);
            return true;
        }
        return false;
    }

    private void ResetToLogin(string status)
    {
        GD.Print($"[client] {status}");
        ClearInstances();
        Multiplayer.MultiplayerPeer?.Close();
        Multiplayer.MultiplayerPeer = new OfflineMultiplayerPeer();
        Input.MouseMode = Input.MouseModeEnum.Visible;
        _characterMenu.Visible = false;
        _dungeonMenu.Visible = false;
        _cityMenu.Visible = false;
        _inventory.Visible = false;
        _hotbar.Visible = false;
        _statusLabel.Visible = false;
        _loginMenu.Visible = true;
        _loginMenu.SetBusy(false, status);
    }

    private void ClearInstances()
    {
        foreach (var child in _main.Instances.GetChildren())
        {
            _main.Instances.RemoveChild(child);
            child.QueueFree();
        }
        _currentInstanceId = null;
    }

    private bool IsConnected() =>
        Multiplayer.MultiplayerPeer is ENetMultiplayerPeer p
        && p.GetConnectionStatus() == MultiplayerPeer.ConnectionStatus.Connected;

    private static (string Host, int Port) ParseAddress(string address)
    {
        address = address.StripEdges();
        var colon = address.LastIndexOf(':');
        if (colon > 0 && int.TryParse(address[(colon + 1)..], out var port))
            return (address[..colon], port);
        return (address.Length > 0 ? address : "127.0.0.1", GameInfo.DefaultPort);
    }
}
