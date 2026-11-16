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
/// physics layer, so weapons hit it) and killed: the body then lies where it fell, and players can search it (E)
/// and take what it carried.
///
/// Replicated: NetPosition, NetYaw, NetMoving, NetActivity, NetHealth, NetDead (changes), NetName, NetRole, NetId (at spawn).
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

    public MultiplayerSynchronizer Sync => GetNode<MultiplayerSynchronizer>("Sync");
    public NpcRole Role => (NpcRole)NetRole;
    /// <summary>The body's interaction id (search it), used once dead.</summary>
    public string LootId => LootPrefix + Name;

    // ---- server ----
    public CityNpcBrain Brain { get; set; } = null!;
    public Inventory Inventory { get; set; } = new();
    public Gear Gear { get; set; } = new();
    /// <summary>What the body holds, once dead (inventory and gear together); players take from it.</summary>
    public Inventory? Loot { get; private set; }

    private Node3D _body = null!;
    private Label3D _label = null!;
    private Interactable _hitbox = null!;
    private float _walkPhase, _fall;
    private string _shownLabel = "\0";

    public override void _Ready()
    {
        _body = GetNode<Node3D>("Body");
        _label = GetNode<Label3D>("Label");
        _hitbox = GetNode<Interactable>("Hitbox");
        Position = NetPosition;
        Rotation = new Vector3(0, NetYaw, 0);
        if (NetDead) MarkDead();
        if (Main.IsServer) return;

        var (clothes, hat) = Look(Role, NetId);
        var torso = GetNode<MeshInstance3D>("Body/Torso");
        torso.MaterialOverride = new StandardMaterial3D { AlbedoColor = clothes, Roughness = 0.85f };
        var hatMesh = GetNode<MeshInstance3D>("Body/Hat");
        hatMesh.Visible = hat != null;
        if (hat is { } h) hatMesh.MaterialOverride = new StandardMaterial3D { AlbedoColor = h, Roughness = 0.5f, Metallic = Role == NpcRole.Guard ? 0.6f : 0 };
        if (NetDead) _fall = 1;
    }

    /// <summary>Clothes by role: guards in steel blue with a helmet, workers in work browns, civilians in varied colors.</summary>
    private static (Color Clothes, Color? Hat) Look(NpcRole role, int id)
    {
        switch (role)
        {
            case NpcRole.Guard:
                return (new Color(0.18f, 0.24f, 0.42f), new Color(0.65f, 0.67f, 0.72f));
            case NpcRole.Worker:
                Color[] work = [new(0.55f, 0.36f, 0.20f), new(0.62f, 0.45f, 0.18f), new(0.40f, 0.33f, 0.25f)];
                return (work[id % work.Length], new Color(0.35f, 0.25f, 0.15f));
            default:
                var hue = (id * 0.618034f) % 1f; // spread colors evenly
                return (Color.FromHsv(hue, 0.45f, 0.75f), null);
        }
    }

    // ============================================================ server

    /// <summary>Server: damage from a player. Returns true if it killed them.</summary>
    public bool TakeDamage(int amount)
    {
        if (NetDead) return false;
        NetHealth = Mathf.Max(0, NetHealth - amount);
        if (NetHealth > 0) return false;
        Brain.Die();
        NetDead = true;
        NetMoving = false;
        NetActivity = "dead";
        Loot = NpcLoadout.ToLoot(Inventory, Gear);
        Inventory = new Inventory();
        Gear = new Gear();
        MarkDead();
        return true;
    }

    /// <summary>The body becomes searchable (and stops counting as solid).</summary>
    private void MarkDead()
    {
        _hitbox.InteractionId = LootId;
        _hitbox.Prompt = $"Search {NetName}";
    }

    // ============================================================ client

    public override void _Process(double delta)
    {
        if (Main.IsServer) return;
        var dt = (float)delta;
        if (Position.DistanceTo(NetPosition) > 5f) Position = NetPosition;
        else Position = Position.Lerp(NetPosition, 1f - Mathf.Exp(-12f * dt));
        Rotation = new Vector3(0, Mathf.LerpAngle(Rotation.Y, NetYaw, 1f - Mathf.Exp(-10f * dt)), 0);

        if (NetDead)
        {
            if (_hitbox.InteractionId != LootId) MarkDead();
            // Topple backwards over about half a second, and stay down.
            _fall = Mathf.Min(1f, _fall + dt * 2f);
            _body.Rotation = new Vector3(Mathf.Pi / 2 * _fall * _fall, 0, 0);
            _body.Position = new Vector3(0, 0.15f * _fall, 0);
        }
        else
        {
            // A little bob while walking.
            _walkPhase = NetMoving ? _walkPhase + dt * 9f : 0;
            _body.Position = new Vector3(0, NetMoving ? Mathf.Abs(Mathf.Sin(_walkPhase)) * 0.06f : 0, 0);
        }

        var text = NetDead ? $"{NetName}\n(dead)"
            : $"{NetName}\n{Role.ToString().ToLowerInvariant()}, {NetActivity}" + (NetHealth < MaxHealth ? $"  ♥{NetHealth}" : "");
        if (_shownLabel != text) _label.Text = _shownLabel = text;
    }
}
