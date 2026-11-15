using System.Collections.Generic;
using System.Linq;
using ExploreTogether.Core;
using ExploreTogether.Net;
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
/// </summary>
public partial class ClientGame : Node
{
    private Main _main = null!;
    private NetSession _session = null!;
    private LoginMenu _loginMenu = null!;
    private CharacterMenu _characterMenu = null!;
    private Label _debugLabel = null!;

    private LoginMenu.AuthAction _pendingAction;
    private string _username = "";
    private string _password = "";
    private int? _currentInstanceId;

    // Dev auto-login state.
    private readonly bool _devAuto = CmdArgs.Has("user");
    private bool _devCreateAttempted;

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
        _characterMenu.CreateRequested += (name, color) =>
        {
            _characterMenu.SetStatus("Creating...");
            _session.SendCreateCharacter(name, color);
        };
        _characterMenu.LogoutRequested += () => ResetToLogin("Logged out.");

        Multiplayer.ConnectedToServer += OnConnected;
        Multiplayer.ConnectionFailed += () => ResetToLogin("Could not connect to the server.");
        Multiplayer.ServerDisconnected += () => ResetToLogin("Disconnected from the server.");

        _session.AuthResultReceived += OnAuthResult;
        _session.CharacterListReceived += OnCharacterList;
        _session.CharacterResultReceived += (ok, message) => _characterMenu.SetBusy(false, ok ? "" : message);
        _session.EnterInstanceReceived += OnEnterInstance;

        var hud = new CanvasLayer { Name = "DebugHud" };
        AddChild(hud);
        _debugLabel = new Label { Position = new Vector2(10, 8), Visible = false };
        _debugLabel.AddThemeColorOverride("font_outline_color", Colors.Black);
        _debugLabel.AddThemeConstantOverride("outline_size", 6);
        hud.AddChild(_debugLabel);

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
        if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.F1 } && _currentInstanceId != null)
        {
            MaterialDebug.Toggle(_main.Instances);
            UpdateDebugLabel();
        }
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
            _session.SendCreateCharacter(wanted, PlayerLook.ParseColor(CmdArgs.Get("color"), PlayerLook.DefaultColor));
        }
    }

    // ------------------------------------------------------------- instances

    private void OnEnterInstance(int instanceId, string kind)
    {
        ClearInstances();
        InstanceFactory.Build(_main.Instances, instanceId, kind, isServer: false);
        _currentInstanceId = instanceId;
        MaterialDebug.Apply(_main.Instances);
        UpdateDebugLabel();
        _loginMenu.Visible = false;
        _characterMenu.Visible = false;
        GD.Print($"[client] entered instance {instanceId} ({kind})");
        _session.SendInstanceReady(instanceId);
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
