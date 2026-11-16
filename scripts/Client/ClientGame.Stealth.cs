using System.Collections.Generic;
using System.Linq;
using ExploreTogether.City;
using ExploreTogether.Core.City;
using ExploreTogether.Net;
using ExploreTogether.Player;
using Godot;

namespace ExploreTogether.Client;

/// <summary>
/// Client side of stealth, breaking in and grappling:
/// - Keys: C crouch (hold), F grab a resident / let go / struggle free when held, left mouse (held) strangle the
///   resident you're holding until they're subdued, K kick the door looked at,
///   L on a locked door without its key: hold to pick it (letting go cancels).
/// - HUD (cities): an awareness line (Unseen, Noticed, Suspicious, Wanted) with the light where you stand (dark, dim,
///   lit, bright: the same numbers the residents see by), and a progress bar for picking locks and struggling.
/// - Gunshots: a brief tracer and muzzle flash for every shot fired near you (players and residents).
/// - The server's movement corrections (it rejected a move): back to where it says.
/// </summary>
public partial class ClientGame
{
    private Label _alertLabel = null!;
    private ProgressBar _progress = null!;
    private Label _progressLabel = null!;
    private double _progressTotal, _progressLeft;
    private bool _picking;
    private string _grabState = "";
    private int _alertLevel;
    private bool _sentCrouch;
    private CityLighting? _lighting;
    private readonly Dictionary<int, CityDoors.DoorState> _doorStates = new();

    private void SetUpStealthClient(CanvasLayer hud)
    {
        _session.PositionCorrectionReceived += pos =>
        {
            if (PlayerCharacter.Local is not { } me) return;
            me.Position = pos;
            me.Velocity = Vector3.Zero;
            GD.Print($"[client] position corrected by the server to {pos.Snapped(Vector3.One * 0.1f)}");
        };
        _session.ProgressReceived += OnProgress;
        _session.GrabStateReceived += OnGrabState;
        _session.ShotReceived += ShowShot;
        _session.AlertReceived += (level, reason) =>
        {
            if (level > _alertLevel && level >= 2) GD.Print($"[client] alert: {reason}");
            _alertLevel = level;
        };

        _alertLabel = HudLabel(hud, Control.LayoutPreset.TopLeft, Vector2.Zero);
        _alertLabel.HorizontalAlignment = HorizontalAlignment.Left;
        _alertLabel.GrowHorizontal = Control.GrowDirection.End; // grow to the right of the corner, not off-screen
        _alertLabel.Position = new Vector2(12, 34);

        _progress = new ProgressBar { Visible = false, ShowPercentage = false, CustomMinimumSize = new Vector2(260, 14), MaxValue = 1 };
        hud.AddChild(_progress);
        _progress.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.CenterBottom);
        _progress.GrowHorizontal = Control.GrowDirection.Both;
        _progress.Position += new Vector2(-130, -140);
        _progressLabel = HudLabel(hud, Control.LayoutPreset.CenterBottom, new Vector2(0, -160));
    }

    /// <summary>Returns true if the event was one of these keys and was used.</summary>
    private bool HandleStealthInput(InputEvent @event)
    {
        if (_currentKind != InstanceKinds.City || _dungeonMenu.Visible || Input.MouseMode != Input.MouseModeEnum.Captured) return false;
        // Holding someone: the attack button strangles while held down; right-click strikes them.
        if (_grabState == "holding" && @event is InputEventMouseButton { ButtonIndex: MouseButton.Left } strangle)
        {
            _session.SendCityAction("strangle", strangle.Pressed ? "1" : "0");
            return true;
        }
        if (_grabState == "holding" && @event is InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: true })
        {
            _session.SendCityAction("hit", "");
            return true;
        }
        if (@event.IsActionPressed(InputSetup.Grab))
        {
            if (_grabState == "held") _session.SendCityAction("struggle", "");
            else if (_grabState == "holding") _session.SendCityAction("grab", "");
            else if (_focused?.InteractionId.StartsWith(CityNpc.LootPrefix) == true && _focused.Prompt.StartsWith("[F]"))
                _session.SendCityAction("grab", _focused.InteractionId);
            return true;
        }
        if (@event.IsActionPressed(InputSetup.KickDoor) && FocusedDoor() is { } kick)
        {
            _session.SendCityAction("kick", kick.InteractionId);
            return true;
        }
        if (@event.IsActionPressed(InputSetup.LockDoor) && FocusedDoor() is { } door)
        {
            _session.SendLockDoor(door.InteractionId); // locks/unlocks with a key or from inside; otherwise starts picking
            return true;
        }
        if (@event.IsActionReleased(InputSetup.LockDoor) && _picking)
        {
            _session.SendCityAction("pickcancel", "");
            return true;
        }
        return false;
    }

    private CityDoor? FocusedDoor() => _focused switch
    {
        CityDoor d => d,
        CityDeadbolt b => b.Door,
        _ => null,
    };

    private void OnProgress(string label, float seconds)
    {
        if (label == "" )
        {
            _progress.Visible = _progressLabel.Visible = _picking = false;
            return;
        }
        _progressLabel.Text = label;
        _progressLabel.Visible = _progress.Visible = true;
        if (seconds < 0)
        {
            // A fraction done (struggling): no countdown.
            _progressTotal = 0;
            _progress.Value = -seconds;
            return;
        }
        _picking = label.StartsWith("Picking");
        (_progressTotal, _progressLeft) = (seconds, seconds);
        _progress.Value = 0;
        GD.Print($"[client] {label} ({seconds:0.#} s)");
    }

    private void OnGrabState(string state, string other)
    {
        _grabState = state;
        if (PlayerCharacter.Local is { } me)
        {
            me.Held = state == "held";
            me.Dragging = state == "holding" || _draggingName != "";
        }
        if (state == "")
        {
            _progress.Visible = _progressLabel.Visible = _picking;
        }
        GD.Print(state == "" ? "[client] grab: free" : $"[client] grab: {state} {other}");
    }

    private void StealthProcess(double delta)
    {
        // Crouching is the player's own movement; the server needs to know (speed limit, how visible they are).
        if (PlayerCharacter.Local is { } me && me.NetCrouching != _sentCrouch && _currentInstanceId != null)
        {
            _sentCrouch = me.NetCrouching;
            _session.SendCityAction("crouch", _sentCrouch ? "1" : "0");
        }
        if (_progress.Visible && _progressTotal > 0)
        {
            _progressLeft -= delta;
            _progress.Value = 1 - _progressLeft / _progressTotal;
        }

        var inCity = _currentKind == InstanceKinds.City && PlayerCharacter.Local != null;
        _alertLabel.Visible = inCity;
        if (!inCity || _lighting == null || _cityHour < 0) return;
        var pos = PlayerCharacter.Local!.GlobalPosition;
        var light = _lighting.LightAt(pos.X, pos.Z, _cityHour, p => _doorStates.TryGetValue(p, out var d) && d.Lit);
        var (text, color) = _alertLevel switch
        {
            1 => ("Noticed", new Color(1f, 0.92f, 0.45f)),
            2 => ("Suspicious", new Color(1f, 0.65f, 0.25f)),
            3 => ("WANTED", new Color(1f, 0.3f, 0.25f)),
            _ => ("Unseen", new Color(0.75f, 0.85f, 0.95f)),
        };
        _alertLabel.Text = $"{text}  ·  {CityLighting.Describe(light)}{(PlayerCharacter.Local.NetCrouching ? "  ·  sneaking" : "")}";
        _alertLabel.Modulate = color;
    }

    /// <summary>A city was entered: set up the light meter for it (same lamps as the server).</summary>
    private void SetUpCityLighting(CityLayout city)
    {
        _lighting = new CityLighting(city);
        _doorStates.Clear();
        _alertLevel = 0;
        _grabState = "";
    }

    /// <summary>A shot: a thin bright line from the muzzle to where it stopped, and a flash, for a tenth of a second.</summary>
    private void ShowShot(Vector3 from, Vector3 to)
    {
        if (_currentInstanceId is not { } id || _main.Instances.GetNodeOrNull<Node3D>(InstanceFactory.NodeName(id)) is not { } root) return;
        var length = from.DistanceTo(to);
        if (length < 0.05f) return;
        var tracer = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(0.025f, 0.025f, length), Material = TracerMaterial },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        root.AddChild(tracer);
        tracer.GlobalPosition = (from + to) / 2;
        if (!from.IsEqualApprox(to)) tracer.LookAt(to, Mathf.Abs((to - from).Normalized().Y) > 0.99f ? Vector3.Right : Vector3.Up);
        var flash = new OmniLight3D { LightEnergy = 4, OmniRange = 7, LightColor = new Color(1f, 0.8f, 0.4f), ShadowEnabled = false };
        root.AddChild(flash);
        flash.GlobalPosition = from;
        GetTree().CreateTimer(0.08).Timeout += () =>
        {
            if (IsInstanceValid(tracer)) tracer.QueueFree(); // gone already if we left the instance
            if (IsInstanceValid(flash)) flash.QueueFree();
        };
    }

    private static readonly StandardMaterial3D TracerMaterial = new()
    {
        AlbedoColor = new Color(1f, 0.9f, 0.6f),
        EmissionEnabled = true,
        Emission = new Color(1f, 0.85f, 0.5f),
        EmissionEnergyMultiplier = 4,
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
    };
}
