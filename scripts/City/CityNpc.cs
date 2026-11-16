using ExploreTogether.Core.City;
using Godot;

namespace ExploreTogether.City;

/// <summary>
/// A city resident. The server moves it (from its <see cref="CityNpcBrain"/>, driven by <see cref="CitySimulation"/>);
/// clients smooth toward the replicated position and show a placeholder body colored by role, with a name tag.
///
/// Replicated: NetPosition, NetYaw, NetMoving, NetActivity (changes), NetName, NetRole, NetId (at spawn).
/// NPCs have no collision for now: players walk through them, and they through each other.
/// </summary>
public partial class CityNpc : Node3D
{
    [Export] public Vector3 NetPosition { get; set; }
    [Export] public float NetYaw { get; set; }
    [Export] public bool NetMoving { get; set; }
    [Export] public string NetActivity { get; set; } = "";
    [Export] public string NetName { get; set; } = "";
    [Export] public int NetRole { get; set; }
    [Export] public int NetId { get; set; }

    public MultiplayerSynchronizer Sync => GetNode<MultiplayerSynchronizer>("Sync");
    public NpcRole Role => (NpcRole)NetRole;

    private Node3D _body = null!;
    private Label3D _label = null!;
    private float _walkPhase;
    private string _shownActivity = "\0";

    public override void _Ready()
    {
        _body = GetNode<Node3D>("Body");
        _label = GetNode<Label3D>("Label");
        Position = NetPosition;
        Rotation = new Vector3(0, NetYaw, 0);
        if (Main.IsServer) return;

        var (clothes, hat) = Look(Role, NetId);
        var torso = GetNode<MeshInstance3D>("Body/Torso");
        torso.MaterialOverride = new StandardMaterial3D { AlbedoColor = clothes, Roughness = 0.85f };
        var hatMesh = GetNode<MeshInstance3D>("Body/Hat");
        hatMesh.Visible = hat != null;
        if (hat is { } h) hatMesh.MaterialOverride = new StandardMaterial3D { AlbedoColor = h, Roughness = 0.5f, Metallic = Role == NpcRole.Guard ? 0.6f : 0 };
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

    public override void _Process(double delta)
    {
        if (Main.IsServer) return;
        var dt = (float)delta;
        if (Position.DistanceTo(NetPosition) > 5f) Position = NetPosition;
        else Position = Position.Lerp(NetPosition, 1f - Mathf.Exp(-12f * dt));
        Rotation = new Vector3(0, Mathf.LerpAngle(Rotation.Y, NetYaw, 1f - Mathf.Exp(-10f * dt)), 0);

        // A little bob while walking.
        _walkPhase = NetMoving ? _walkPhase + dt * 9f : 0;
        _body.Position = new Vector3(0, NetMoving ? Mathf.Abs(Mathf.Sin(_walkPhase)) * 0.06f : 0, 0);

        if (_shownActivity != NetActivity)
        {
            _shownActivity = NetActivity;
            _label.Text = $"{NetName}\n{Role.ToString().ToLowerInvariant()}, {NetActivity}";
        }
    }
}
