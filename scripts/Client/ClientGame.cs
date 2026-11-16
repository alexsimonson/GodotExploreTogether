using System.Collections.Generic;
using System.Linq;
using ExploreTogether.Core;
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
///   --dev-overview angle|top        in the city, view it from a camera high above (for screenshots)
///   --dev-check-city                in the city, raycast every doorway, gate fence and front wall against the layout
/// </summary>
public partial class ClientGame : Node
{
    private Main _main = null!;
    private NetSession _session = null!;
    private LoginMenu _loginMenu = null!;
    private CharacterMenu _characterMenu = null!;
    private Label _debugLabel = null!;
    private Label _promptLabel = null!;
    private Label _noticeLabel = null!;
    private DungeonMenu _dungeonMenu = null!;
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
        _session.NoticeReceived += ShowNotice;
        _session.InventoryReceived += OnInventory;
        _session.DungeonStatusReceived += OnDungeonStatus;

        _inventory = new InventoryScreen { Name = "Inventory", Visible = false };
        AddChild(_inventory);
        _inventory.MoveRequested += (fc, fs, tc, ts) => _session.SendMoveItem((int)fc, fs, (int)tc, ts);
        _inventory.DropRequested += (c, slot) => _session.SendDropItem((int)c, slot);

        _dungeonMenu = new DungeonMenu { Name = "DungeonMenu", Visible = false };
        AddChild(_dungeonMenu);
        _dungeonMenu.CreateRequested += (w, h, len) => _session.SendCreateDungeon(w, h, len);
        _dungeonMenu.JoinRequested += id => _session.SendJoinDungeon(id);

        var hud = new CanvasLayer { Name = "DebugHud" };
        AddChild(hud);
        _debugLabel = new Label { Position = new Vector2(10, 8), Visible = false };
        _debugLabel.AddThemeColorOverride("font_outline_color", Colors.Black);
        _debugLabel.AddThemeConstantOverride("outline_size", 6);
        hud.AddChild(_debugLabel);

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
        if (HandleCombatInput(@event))
        {
            GetViewport().SetInputAsHandled();
            return;
        }
        if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.F1 } && _currentInstanceId != null)
        {
            MaterialDebug.Toggle(_main.Instances);
            UpdateDebugLabel();
        }
        else if (@event.IsActionPressed(InputSetup.Interact) && _focused != null && !_dungeonMenu.Visible)
        {
            _session.SendInteract(_focused.InteractionId);
        }
        else if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Tab }
                 && _currentInstanceId != null && !_dungeonMenu.Visible)
        {
            _inventory.Toggle();
        }
    }

    public override void _Process(double delta)
    {
        if (_noticeTimeLeft > 0 && (_noticeTimeLeft -= delta) <= 0)
            _noticeLabel.Visible = false;
        CombatProcess(delta);
        StorageProcess(delta);
    }

    public override void _PhysicsProcess(double delta) => UpdateFocus();

    /// <summary>Looks along the local camera for an interactable within reach and shows its prompt.</summary>
    private void UpdateFocus()
    {
        _focused = null;
        var player = PlayerCharacter.Local;
        if (player != null && Input.MouseMode == Input.MouseModeEnum.Captured && !_dungeonMenu.Visible)
        {
            var camera = player.Camera;
            var from = camera.GlobalPosition;
            var to = from - camera.GlobalBasis.Z * Interactable.ReachDistance;
            var query = PhysicsRayQueryParameters3D.Create(from, to, exclude: [player.GetRid()]);
            var hit = camera.GetWorld3D().DirectSpaceState.IntersectRay(query);
            if (hit.Count > 0 && hit["collider"].AsGodotObject() is Interactable target)
                _focused = target;
        }
        _promptLabel.Visible = _focused != null;
        if (_focused != null) _promptLabel.Text = $"[E] {_focused.Prompt}";
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
        _inventory.SetState(quads, gear, gearData);
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
        else _session.SendRegister(_username, _password);
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
        _dungeonMenu.Visible = false;
        _inventory.Visible = false;
        _statusLabel.Visible = false;
        InstanceFactory.Build(_main.Instances, instanceId, kind, layoutData, isServer: false);
        _currentInstanceId = instanceId;
        _currentKind = kind;
        MaterialDebug.Apply(_main.Instances);
        UpdateDebugLabel();
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
                var (count, failures) = City.CityChecks.Verify(Core.City.CityLayout.Import(layoutData, City.CityCatalog.Catalog), p.GetWorld3D());
                GD.Print($"[client] city check: {count - failures.Count}/{count} doors, fences and walls match the layout");
                foreach (var f in failures) GD.Print($"[client]   {f}");
            };
        }
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
        UpdateDebugLabel();
        _characterMenu.Visible = false;
        _dungeonMenu.Visible = false;
        _inventory.Visible = false;
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

    private void UpdateDebugLabel()
    {
        _debugLabel.Visible = _currentInstanceId != null;
        _debugLabel.Text = $"Materials: {MaterialDebug.Describe(MaterialDebug.Current)}   [F1 to toggle]";
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
