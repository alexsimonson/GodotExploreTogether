using ExploreTogether.Core;
using ExploreTogether.Net;
using ExploreTogether.UI;
using Godot;

namespace ExploreTogether.Client;

/// <summary>
/// Client-only root. Shows the connect menu, connects to the server, and builds
/// whichever instance the server sends this client into.
/// </summary>
public partial class ClientGame : Node
{
    private Main _main = null!;
    private NetSession _session = null!;
    private ConnectMenu _menu = null!;
    private string _displayName = "";
    private Color _color;
    private Label _debugLabel = null!;
    private int? _currentInstanceId;

    public override void _Ready()
    {
        _main = GetParent<Main>();
        _session = _main.Session;

        _menu = new ConnectMenu { Name = "ConnectMenu" };
        AddChild(_menu);
        _menu.ConnectRequested += Connect;

        Multiplayer.ConnectedToServer += OnConnected;
        Multiplayer.ConnectionFailed += () => ResetToMenu("Could not connect to the server.");
        Multiplayer.ServerDisconnected += () => ResetToMenu("Disconnected from the server.");

        _session.EnterInstanceReceived += OnEnterInstance;
        _session.JoinRejectedReceived += reason => ResetToMenu($"Join rejected: {reason}");

        var hud = new CanvasLayer { Name = "DebugHud" };
        AddChild(hud);
        _debugLabel = new Label { Position = new Vector2(10, 8), Visible = false };
        _debugLabel.AddThemeColorOverride("font_outline_color", Colors.Black);
        _debugLabel.AddThemeConstantOverride("outline_size", 6);
        hud.AddChild(_debugLabel);

        if (CmdArgs.Has("connect"))
            CallDeferred(MethodName.Connect, CmdArgs.Get("name", "Player"),
                PlayerLook.ParseColor(CmdArgs.Get("color"), PlayerLook.DefaultColor), CmdArgs.Get("connect", "127.0.0.1"));
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.F1 } && _currentInstanceId != null)
        {
            MaterialDebug.Toggle(_main.Instances);
            UpdateDebugLabel();
        }
    }

    private void UpdateDebugLabel()
    {
        _debugLabel.Visible = _currentInstanceId != null;
        _debugLabel.Text = $"Materials: {MaterialDebug.Describe(MaterialDebug.Current)}   [F1 to toggle]";
    }

    private void Connect(string displayName, Color color, string address)
    {
        _displayName = displayName;
        _color = color;
        var (host, port) = ParseAddress(address);

        var peer = new ENetMultiplayerPeer();
        var err = peer.CreateClient(host, port);
        if (err != Error.Ok)
        {
            _menu.SetBusy(false, $"Could not start connection: {err}");
            return;
        }
        Multiplayer.MultiplayerPeer = peer;
        _menu.SetBusy(true, $"Connecting to {host}:{port}...");
        GD.Print($"[client] connecting to {host}:{port} as '{displayName}'");
    }

    private void OnConnected()
    {
        GD.Print($"[client] connected as peer {Multiplayer.GetUniqueId()}");
        _menu.SetBusy(true, "Joining...");
        _session.SendJoinRequest(_displayName, _color);
    }

    private void OnEnterInstance(int instanceId, string kind)
    {
        ClearInstances();
        InstanceFactory.Build(_main.Instances, instanceId, kind, isServer: false);
        _currentInstanceId = instanceId;
        MaterialDebug.Apply(_main.Instances);
        UpdateDebugLabel();
        _menu.Visible = false;
        GD.Print($"[client] entered instance {instanceId} ({kind})");
        _session.SendInstanceReady(instanceId);
    }

    private void ResetToMenu(string status)
    {
        GD.Print($"[client] {status}");
        ClearInstances();
        Multiplayer.MultiplayerPeer?.Close();
        Multiplayer.MultiplayerPeer = new OfflineMultiplayerPeer();
        Input.MouseMode = Input.MouseModeEnum.Visible;
        UpdateDebugLabel();
        _menu.Visible = true;
        _menu.SetBusy(false, status);
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

    private static (string Host, int Port) ParseAddress(string address)
    {
        address = address.StripEdges();
        var colon = address.LastIndexOf(':');
        if (colon > 0 && int.TryParse(address[(colon + 1)..], out var port))
            return (address[..colon], port);
        return (address.Length > 0 ? address : "127.0.0.1", GameInfo.DefaultPort);
    }
}
