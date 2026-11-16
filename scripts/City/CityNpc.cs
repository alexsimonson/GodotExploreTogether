using ExploreTogether.Core.City;
using ExploreTogether.Core.Items;
using ExploreTogether.Dungeon;
using Godot;

namespace ExploreTogether.City;

/// <summary>
/// A city resident. The server moves it (from its <see cref="CityNpcBrain"/>, driven by <see cref="CitySimulation"/>);
/// clients smooth toward the replicated position and show a placeholder body colored by role, with a name tag.
///
/// Like a player, a resident has an inventory and equipment (server-side only: <see cref="Inventory"/>,
/// <see cref="Gear"/>, see <see cref="NpcLoadout"/>). They can be hurt (the "Hitbox" is on the enemies'
/// physics layer, so weapons hit it) and killed, or knocked out with a blunt weapon (a bat, a concrete block).
/// Either way they're down where they fell: players can search them (E), take what they carry, and drag them (G).
/// The unconscious wake up after a while and carry on, minus whatever was taken.
///
/// Standing residents can be grabbed (F): see ServerGame.Grapple. They notice, react and remember (see
/// <see cref="CityNpcBrain"/>'s reactions): clients show a "?" or "!" over their heads, what they say, and the
/// weapon they've drawn.
///
/// Replicated: NetPosition, NetYaw, NetMoving, NetActivity, NetHealth, NetDead, NetUnconscious, NetMood, NetSpeech,
/// NetWeapon, NetHeld (changes), NetName, NetRole, NetId (at spawn).
/// </summary>
public partial class CityNpc : Node3D
{
    public const int MaxHealth = 100;
    public const string LootPrefix = "npc:";

    [Export] public Vector3 NetPosition { get; set; }
    [Export] public float NetYaw { get; set; }
    [Export] public bool NetMoving { get; set; }
    [Export] public string NetActivity { get; set; } = "";
    [Export] public string NetName { get; set; } = "";
    [Export] public int NetRole { get; set; }
    [Export] public int NetId { get; set; }
    [Export] public int NetHealth { get; set; } = MaxHealth;
    [Export] public bool NetDead { get; set; }
    [Export] public bool NetUnconscious { get; set; }
    /// <summary>How worked up they are (<see cref="Mood"/> as an int).</summary>
    [Export] public int NetMood { get; set; }
    /// <summary>What they last said, as "n|text" (n changes each time, so saying the same thing twice shows twice).</summary>
    [Export] public string NetSpeech { get; set; } = "";
    /// <summary>The weapon they hold drawn (item id), or -1.</summary>
    [Export] public int NetWeapon { get; set; } = -1;
    /// <summary>In a player's grip.</summary>
    [Export] public bool NetHeld { get; set; }
    /// <summary>Frozen by frost magic (an icy tint on clients).</summary>
    [Export] public bool NetFrozen { get; set; }

    /// <summary>Dead or knocked out: on the ground, searchable and draggable.</summary>
    public bool IsDown => NetDead || NetUnconscious;

    public MultiplayerSynchronizer Sync => GetNode<MultiplayerSynchronizer>("Sync");
    public NpcRole Role => (NpcRole)NetRole;
    /// <summary>The body's interaction id (search it), used once dead.</summary>
    public string LootId => LootPrefix + Name;

    // ---- server ----
    public CityNpcBrain Brain { get; set; } = null!;
    /// <summary>What they carry. While they're down, players searching them take from this (and <see cref="Gear"/>).</summary>
    public Inventory Inventory { get; set; } = new();
    public Gear Gear { get; set; } = new();
    /// <summary>The peer dragging this body, or 0.</summary>
    public long DraggedBy { get; set; }
    /// <summary>The peer holding this (standing) resident in a grip, or 0.</summary>
    public long HeldBy { get; set; }
    /// <summary>The player this resident holds in a grip (peer), or 0.</summary>
    public long Holding { get; set; }
    /// <summary>When the brain's last speech was copied to <see cref="NetSpeech"/>.</summary>
    public double SpokeAt { get; set; } = -1000;
    /// <summary>Server: they came round (whoever was dragging or searching them has to let go).</summary>
    public event System.Action<CityNpc>? WokeUp;
    private double _wakeIn;

    private Node3D _body = null!;
    private Label3D _label = null!;
    private Interactable _hitbox = null!;
    private float _walkPhase, _fall;
    private string _shownLabel = "\0";
    private Label3D? _speech;
    private string _shownSpeech = "";
    private double _speechLeft;
    private MeshInstance3D? _weaponMesh;
    private int _shownWeapon = -1;
    private StandardMaterial3D? _torsoMaterial;
    private Color _clothes;
    private bool _shownFrozen;

    public override void _Ready()
    {
        _body = GetNode<Node3D>("Body");
        _label = GetNode<Label3D>("Label");
        _hitbox = GetNode<Interactable>("Hitbox");
        Position = NetPosition;
        Rotation = new Vector3(0, NetYaw, 0);
        _hitbox.InteractionId = LootId;
        if (IsDown) MarkDown();
        else MarkStanding();
        if (Main.IsServer) return;

        _speech = new Label3D
        {
            Name = "Speech",
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            FontSize = 36,
            OutlineSize = 8,
            PixelSize = 0.005f,
            Modulate = new Color(1f, 1f, 0.85f),
            Position = new Vector3(0, 2.75f, 0),
            Visible = false,
            NoDepthTest = true,
        };
        AddChild(_speech);

        var (clothes, hat) = Look(Role, NetId);
        var torso = GetNode<MeshInstance3D>("Body/Torso");
        _clothes = clothes;
        _torsoMaterial = new StandardMaterial3D { AlbedoColor = clothes, Roughness = 0.85f };
        torso.MaterialOverride = _torsoMaterial;
        var hatMesh = GetNode<MeshInstance3D>("Body/Hat");
        hatMesh.Visible = hat != null;
        if (hat is { } h) hatMesh.MaterialOverride = new StandardMaterial3D { AlbedoColor = h, Roughness = 0.5f, Metallic = Role == NpcRole.Police ? 0.6f : 0 };
        if (IsDown) _fall = 1;
    }

    /// <summary>Clothes by role: guards in steel blue with a helmet, workers in work browns, civilians in varied colors.</summary>
    private static (Color Clothes, Color? Hat) Look(NpcRole role, int id)
    {
        switch (role)
        {
            case NpcRole.Police:
                return (new Color(0.18f, 0.24f, 0.42f), new Color(0.65f, 0.67f, 0.72f));
            case NpcRole.Security:
                return (new Color(0.16f, 0.16f, 0.18f), new Color(0.12f, 0.12f, 0.14f));
            case NpcRole.Shopkeeper:
                return (new Color(0.22f, 0.45f, 0.30f), null);
            case NpcRole.Worker:
                Color[] work = [new(0.55f, 0.36f, 0.20f), new(0.62f, 0.45f, 0.18f), new(0.40f, 0.33f, 0.25f)];
                return (work[id % work.Length], new Color(0.35f, 0.25f, 0.15f));
            default:
                var hue = (id * 0.618034f) % 1f; // spread colors evenly
                return (Color.FromHsv(hue, 0.45f, 0.75f), null);
        }
    }

    // ============================================================ server

    /// <summary>Server: damage from a player. Returns true if it killed them (an unconscious person too).</summary>
    public bool TakeDamage(int amount)
    {
        if (NetDead) return false;
        NetHealth = Mathf.Max(0, NetHealth - amount);
        if (NetHealth > 0) return false;
        Brain.Die();
        NetDead = true;
        NetUnconscious = false;
        NetMoving = false;
        NetActivity = "dead";
        GoDown();
        return true;
    }

    /// <summary>Server: a blunt blow. They drop, out cold, for <paramref name="seconds"/>. Returns false if already down.</summary>
    public bool KnockOut(double seconds)
    {
        if (IsDown) return false;
        Brain.KnockOut();
        NetUnconscious = true;
        NetMoving = false;
        NetActivity = "unconscious";
        _wakeIn = seconds;
        GoDown();
        return true;
    }

    /// <summary>Down: they can be searched (their inventory and equipment, as they are) and dragged.</summary>
    private void GoDown() => MarkDown();

    /// <summary>
    /// Poses the body and its hitbox for a fall of 0 (standing) to 1 (lying on its back, head toward local +Z), so
    /// aiming, interacting and the server's reach checks all use where the body actually is. The hitbox sits at the
    /// body's middle (0.9 m up the body from the feet).
    /// </summary>
    private void Pose(float fall)
    {
        var tilt = Mathf.Pi / 2 * fall * fall;
        var lift = 0.15f * fall;
        _body.Rotation = new Vector3(tilt, 0, 0);
        _body.Position = new Vector3(0, lift, 0);
        _hitbox.Rotation = new Vector3(tilt, 0, 0);
        _hitbox.Position = new Vector3(0, 0.9f * Mathf.Cos(tilt) + lift, 0.9f * Mathf.Sin(tilt));
        _label.Position = new Vector3(0, 2.15f - 1.4f * fall, 0.9f * fall);
    }

    /// <summary>The body becomes searchable and draggable (and stops counting as solid).</summary>
    private void MarkDown()
    {
        if (Main.IsServer) Pose(1); // clients animate the fall
        _hitbox.Prompt = $"Search {NetName}  ·  [G] Drag";
    }

    /// <summary>Standing: can be grabbed (and, held for a while, choked unconscious).</summary>
    private void MarkStanding() => _hitbox.Prompt = $"[F] Grab {NetName}";

    public override void _PhysicsProcess(double delta)
    {
        if (!Main.IsServer || !NetUnconscious || NetDead) return;
        if ((_wakeIn -= delta) > 0) return;
        WakeNow();
    }

    /// <summary>Server: comes round now (their time is up, or someone helped them up).</summary>
    public void WakeNow()
    {
        if (!NetUnconscious || NetDead) return;
        // Coming round (minus whatever was taken), they find their way back to the street.
        NetUnconscious = false;
        MarkStanding();
        Pose(0);
        Brain.WakeUp();
        NetActivity = Brain.Activity;
        WokeUp?.Invoke(this);
    }

    // ============================================================ client

    public override void _Process(double delta)
    {
        if (Main.IsServer) return;
        var dt = (float)delta;
        if (Position.DistanceTo(NetPosition) > 5f) Position = NetPosition;
        else Position = Position.Lerp(NetPosition, 1f - Mathf.Exp(-12f * dt));
        Rotation = new Vector3(0, Mathf.LerpAngle(Rotation.Y, NetYaw, 1f - Mathf.Exp(-10f * dt)), 0);

        if (IsDown)
        {
            if (!_hitbox.Prompt.StartsWith("Search")) MarkDown();
            // Topple backwards over about half a second, and stay down.
            _fall = Mathf.Min(1f, _fall + dt * 2f);
        }
        else
        {
            if (_hitbox.Prompt.StartsWith("Search")) MarkStanding(); // came round
            _fall = Mathf.Max(0f, _fall - dt); // get back up
            // A little bob while walking.
            _walkPhase = NetMoving && !NetFrozen ? _walkPhase + dt * 9f : 0;
        }
        Pose(_fall);
        if (NetMoving && !IsDown) _body.Position += new Vector3(0, Mathf.Abs(Mathf.Sin(_walkPhase)) * 0.06f, 0);

        var marker = IsDown ? "" : (Mood)NetMood switch
        {
            Mood.Curious => "?  ",
            Mood.Suspicious => "?!  ",
            Mood.Alarmed => "!  ",
            Mood.Hostile => "!!  ",
            _ => "",
        };
        var text = NetDead ? $"{NetName}\n(dead)"
            : NetUnconscious ? $"{NetName}\n(unconscious)"
            : $"{marker}{NetName}\n{Role.ToString().ToLowerInvariant()}, {(NetFrozen ? "frozen" : NetActivity)}" + (NetHealth < MaxHealth ? $"  ♥{NetHealth}" : "");
        if (_shownLabel != text)
        {
            _label.Text = _shownLabel = text;
            _label.Modulate = (Mood)NetMood switch
            {
                Mood.Curious => new Color(1f, 0.95f, 0.5f),
                Mood.Suspicious => new Color(1f, 0.75f, 0.3f),
                Mood.Alarmed => new Color(1f, 0.5f, 0.25f),
                Mood.Hostile => new Color(1f, 0.25f, 0.2f),
                _ => Colors.White,
            };
        }
        UpdateSpeech(dt);
        UpdateWeapon();
        if (_torsoMaterial != null && _shownFrozen != (NetFrozen && !IsDown))
        {
            // Frozen: icy blue, like frozen dungeon enemies.
            _shownFrozen = NetFrozen && !IsDown;
            _torsoMaterial.AlbedoColor = _shownFrozen ? new Color(0.35f, 0.6f, 1f) : _clothes;
        }
    }

    private void UpdateSpeech(float dt)
    {
        if (_speech == null) return;
        if (NetSpeech != _shownSpeech)
        {
            _shownSpeech = NetSpeech;
            var bar = NetSpeech.IndexOf('|');
            var said = bar >= 0 ? NetSpeech[(bar + 1)..] : NetSpeech;
            _speech.Text = $"\u201c{said}\u201d";
            _speech.Visible = said != "";
            _speechLeft = 3.5;
            if (said != "") GD.Print($"[client] {NetName} says: {said}");
        }
        if (_speech.Visible && (_speechLeft -= dt) <= 0) _speech.Visible = false;
    }

    private void UpdateWeapon()
    {
        var weapon = IsDown ? -1 : NetWeapon;
        if (weapon == _shownWeapon) return;
        _shownWeapon = weapon;
        _weaponMesh?.QueueFree();
        _weaponMesh = null;
        if (ItemCatalog.Get(weapon) is not { } def) return;
        _weaponMesh = WeaponLook.Create(def);
        _weaponMesh.Position = new Vector3(0.35f, 1.05f, -0.3f);
        _body.AddChild(_weaponMesh);
    }
}
