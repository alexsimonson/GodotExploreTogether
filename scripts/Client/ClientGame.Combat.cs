using System.Linq;
using ExploreTogether.Core.Combat;
using ExploreTogether.Core.Items;
using ExploreTogether.Enemies;
using ExploreTogether.Net;
using ExploreTogether.Player;
using Godot;

namespace ExploreTogether.Client;

/// <summary>
/// Client side of combat (port of PlayerCombat input + PlayerGhostAim + HUD health/weapon text):
/// - Left click: attack with the equipped weapon (the server raycasts and decides).
/// - Right click held (magic): show the aim marker where the area would land (the original toggled it
///   on press and again on release, i.e. hold to aim); left click while aiming casts.
/// - R: reload (gun).
/// - HUD: health, weapon + ammo, a crosshair, and a red flash when hurt.
/// Dev: --dev-fight attacks the nearest living enemy automatically (reloading as needed).
/// </summary>
public partial class ClientGame
{
    private int _equippedWeaponId = -1;
    private int _magazine = -1, _reserve = -1;
    private Label _healthLabel = null!;
    private Label _crosshair = null!;
    private ColorRect _hurtFlash = null!;
    private MeshInstance3D? _aimMarker;
    private bool _aiming;
    private int _lastHealth = -1;
    private double _devFightTimer;

    private ItemDef? EquippedWeapon => _equippedWeaponId >= 0 ? ItemCatalog.Get(_equippedWeaponId) : null;

    private void SetUpCombatClient(CanvasLayer hud)
    {
        _session.AmmoReceived += (mag, reserve) => { _magazine = mag; _reserve = reserve; UpdateWeaponLabel(); };
        _session.MagicAreaReceived += ShowMagicArea;
        _session.CharacterLostReceived += OnCharacterLost;

        _healthLabel = new Label { Visible = false, HorizontalAlignment = HorizontalAlignment.Right };
        _healthLabel.AddThemeColorOverride("font_outline_color", Colors.Black);
        _healthLabel.AddThemeConstantOverride("outline_size", 6);
        _healthLabel.AddThemeFontSizeOverride("font_size", 22);
        hud.AddChild(_healthLabel);
        _healthLabel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomRight);
        _healthLabel.GrowHorizontal = Control.GrowDirection.Begin;
        _healthLabel.GrowVertical = Control.GrowDirection.Begin;
        _healthLabel.Position += new Vector2(-12, -40);

        _crosshair = new Label { Text = "+", Visible = false, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _crosshair.AddThemeFontSizeOverride("font_size", 26);
        _crosshair.AddThemeColorOverride("font_outline_color", Colors.Black);
        _crosshair.AddThemeConstantOverride("outline_size", 4);
        hud.AddChild(_crosshair);
        _crosshair.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.Center);
        _crosshair.GrowHorizontal = Control.GrowDirection.Both;
        _crosshair.GrowVertical = Control.GrowDirection.Both;

        _hurtFlash = new ColorRect { Color = new Color(0.8f, 0, 0, 0), MouseFilter = Control.MouseFilterEnum.Ignore };
        hud.AddChild(_hurtFlash);
        _hurtFlash.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
    }

    /// <summary>Called with each inventory update (the gear tells us the weapon).</summary>
    private void UpdateEquippedWeapon(int[] gear)
    {
        var slot = (int)EquipmentType.Weapon;
        _equippedWeaponId = slot < gear.Length ? gear[slot] : -1;
        if (EquippedWeapon?.Class != ItemClass.Magic) SetAiming(false);
        UpdateWeaponLabel();
    }

    private void UpdateWeaponLabel()
    {
        var w = EquippedWeapon;
        var text = $"Weapon: {w?.Name ?? "none"}";
        if (w?.Class == ItemClass.Gun && _magazine >= 0) text += $"  {_magazine} / {_reserve}";
        _weaponLabel.Text = text;
        _crosshair.Text = w?.Class == ItemClass.Gun ? "+" : "·"; // the original showed its crosshair for guns
    }

    private bool CombatInputAllowed =>
        _currentInstanceId != null && PlayerCharacter.Local != null
        && Input.MouseMode == Input.MouseModeEnum.Captured
        && !_inventory.Visible && !_dungeonMenu.Visible;

    /// <summary>Returns true if the event was a combat input and was used.</summary>
    private bool HandleCombatInput(InputEvent @event)
    {
        if (!CombatInputAllowed || EquippedWeapon is not { } weapon || CombatRules.ProfileFor(weapon) == null) return false;

        if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true })
        {
            if (weapon.Class == ItemClass.Magic && !_aiming) return true; // the original only cast while the aim was out
            _session.SendAttack(AimDirection());
            return true;
        }
        if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Right } rmb && weapon.Class == ItemClass.Magic)
        {
            SetAiming(rmb.Pressed); // hold to aim
            return true;
        }
        if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.R } && weapon.Class == ItemClass.Gun)
        {
            _session.SendReload();
            return true;
        }
        return false;
    }

    private static Vector3 AimDirection() => -PlayerCharacter.Local!.Camera.GlobalBasis.Z;

    private void CombatProcess(double delta)
    {
        var inInstance = _currentInstanceId != null && PlayerCharacter.Local != null;
        _crosshair.Visible = inInstance && !_inventory.Visible && !_dungeonMenu.Visible;
        _healthLabel.Visible = inInstance;

        if (PlayerCharacter.Local is { } me)
        {
            var hp = me.NetHealth;
            _healthLabel.Text = $"Health: {hp}";
            _healthLabel.Modulate = hp <= 30 ? new Color(1, 0.4f, 0.4f) : Colors.White;
            if (_lastHealth >= 0 && hp < _lastHealth)
            {
                _hurtFlash.Color = new Color(0.8f, 0, 0, 0.35f);
                GD.Print($"[client] took {_lastHealth - hp} damage ({hp} left)");
            }
            _lastHealth = hp;
        }
        else _lastHealth = -1;
        if (_hurtFlash.Color.A > 0)
            _hurtFlash.Color = _hurtFlash.Color with { A = Mathf.Max(0, _hurtFlash.Color.A - (float)delta * 1.2f) };

        UpdateAimMarker();
        DevFight(delta);
    }

    // ---- magic aim marker (PlayerGhostAim.DrawGhostAim) ----

    private void SetAiming(bool aiming)
    {
        _aiming = aiming;
        if (!aiming && _aimMarker != null) _aimMarker.Visible = false;
    }

    private void UpdateAimMarker()
    {
        if (!_aiming || PlayerCharacter.Local is not { } me) return;
        var cam = me.Camera;
        var from = cam.GlobalPosition;
        var hit = cam.GetWorld3D().DirectSpaceState.IntersectRay(
            PhysicsRayQueryParameters3D.Create(from, from + AimDirection() * CombatRules.MagicPlaceRange, collisionMask: 1));
        _aimMarker ??= MakeAreaDisc(new Color(1, 1, 1, 0.35f));
        if (_aimMarker.GetParent() == null) _main.Instances.AddChild(_aimMarker);
        _aimMarker.Visible = hit.Count > 0;
        if (hit.Count > 0) _aimMarker.GlobalPosition = hit["position"].AsVector3() + new Vector3(0, 0.03f, 0);
    }

    private void ShowMagicArea(Vector3 position, int style, float lifetime)
    {
        var color = (MagicStyle)style == MagicStyle.Frost ? new Color(0.4f, 0.7f, 1f, 0.5f) : new Color(0.9f, 0.1f, 0.1f, 0.5f);
        var disc = MakeAreaDisc(color);
        _main.Instances.AddChild(disc);
        disc.GlobalPosition = position + new Vector3(0, 0.04f, 0);
        GetTree().CreateTimer(lifetime).Timeout += () => { if (IsInstanceValid(disc)) disc.QueueFree(); };
    }

    private static MeshInstance3D MakeAreaDisc(Color color) => new()
    {
        Mesh = new CylinderMesh
        {
            TopRadius = CombatRules.MagicAreaRadius, BottomRadius = CombatRules.MagicAreaRadius, Height = 0.05f,
            Material = new StandardMaterial3D
            {
                AlbedoColor = color,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            },
        },
    };

    // ---- hardcore death ----

    private void OnCharacterLost(string message)
    {
        GD.Print($"[client] {message}");
        ClearInstances();
        SetAiming(false);
        _aimMarker = null;
        _inventory.Visible = false;
        _statusLabel.Visible = false;
        _weaponLabel.Visible = false;
        Input.MouseMode = Input.MouseModeEnum.Visible;
        _characterMenu.Visible = true;
        _characterMenu.SetBusy(false, message);
        _devCreateAttempted = true; // dev auto-login: don't recreate the lost character
    }

    // ---- dev ----

    private void DevFight(double delta)
    {
        if (!CmdArgs.Has("dev-fight") || _currentKind != InstanceKinds.Dungeon || PlayerCharacter.Local is not { } me) return;
        if ((_devFightTimer -= delta) > 0) return;
        _devFightTimer = 0.3;

        if (EquippedWeapon?.Class == ItemClass.Gun && _magazine == 0) { _session.SendReload(); return; }
        var container = _main.Instances.GetNodeOrNull(InstanceFactory.NodeName(_currentInstanceId!.Value));
        var target = container?.GetNodeOrNull("Enemies")?.GetChildren().OfType<EnemyCharacter>()
            .Where(e => !e.IsDead).OrderBy(e => e.GlobalPosition.DistanceTo(me.GlobalPosition)).FirstOrDefault();
        if (target == null) return;

        // Aim from the head: at the enemy's chest, or at its feet for magic (the area lands on the floor).
        var head = me.GlobalPosition + new Vector3(0, 1.6f, 0);
        var aimAt = target.GlobalPosition + new Vector3(0, EquippedWeapon?.Class == ItemClass.Magic ? 0.05f : 1.0f, 0);
        var dir = (aimAt - head).Normalized();
        me.LookAt(new Vector3(target.GlobalPosition.X, me.GlobalPosition.Y, target.GlobalPosition.Z));
        _session.SendAttack(dir);
    }
}
