using ExploreTogether.Core.Zombies;
using Godot;

namespace ExploreTogether.Zombies;

/// <summary>
/// One zombie in the world: the replicated node for a <see cref="ZombieBrain"/> (which the server's
/// <see cref="ZombieLayer"/> runs). Clients see it shamble, lunge, fall and get up, and hear it groan.
/// Replicated: NetPosition, NetYaw, NetState (a <see cref="ZombieState"/>), NetMoving, NetHealth.
///
/// It plugs into the game through the generic hooks only: <see cref="IHittable"/> (weapons and magic hit it; the
/// layer decides what that does) and <see cref="ISolidBody"/> (players can't walk through it).
/// </summary>
public partial class ZombieNpc : Node3D, IHittable, ISolidBody
{
    public const string ScenePath = "res://scenes/Zombie.tscn";

    [Export] public Vector3 NetPosition { get; set; }
    [Export] public float NetYaw { get; set; }
    [Export] public int NetState { get; set; }
    [Export] public bool NetMoving { get; set; }
    [Export] public int NetHealth { get; set; } = ZombieRules.MaxHealth;

    public MultiplayerSynchronizer Sync => GetNode<MultiplayerSynchronizer>("Sync");
    public ZombieState State => (ZombieState)NetState;

    /// <summary>Server: the layer that runs it (hits are handed to it).</summary>
    public ZombieLayer? Layer { get; set; }
    public ZombieBrain? Brain { get; set; }

    // ---- hooks ----

    public bool CanBeHit => State != ZombieState.Dead;
    public bool Solid => State is not (ZombieState.Dead or ZombieState.Down);
    public float Radius => 0.35f;

    public void TakeWeaponHit(Core.Items.ItemDef weapon, int damage, Vector3 at, Vector3 from, long attacker) =>
        Layer?.Hit(this, weapon, damage, at.Y - GlobalPosition.Y >= ZombieRules.HeadHeight, from, attacker);

    public void TakeAreaHit(int damage, bool frost, Vector3 center, long attacker) =>
        Layer?.AreaHit(this, damage, frost, center, attacker);

    // ---- client look ----

    private Node3D _body = null!;
    private float _fall, _walkPhase;
    private double _nextGroan;

    private static readonly StandardMaterial3D Skin = new() { AlbedoColor = new Color(0.47f, 0.55f, 0.40f), Roughness = 0.9f };
    private static readonly StandardMaterial3D Rags = new() { AlbedoColor = new Color(0.30f, 0.27f, 0.24f), Roughness = 1f };
    private static readonly StandardMaterial3D Eyes = new()
    {
        AlbedoColor = new Color(0.9f, 0.85f, 0.4f), EmissionEnabled = true, Emission = new Color(0.8f, 0.7f, 0.2f),
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
    };

    public override void _Ready()
    {
        Position = NetPosition;
        Rotation = new Vector3(0, NetYaw, 0);
        _body = new Node3D { Name = "Body" };
        AddChild(_body);
        _body.AddChild(Part(new CapsuleMesh { Radius = 0.27f, Height = 1.4f, Material = Rags }, new Vector3(0, 0.72f, 0)));
        _body.AddChild(Part(new SphereMesh { Radius = 0.17f, Height = 0.34f, Material = Skin }, new Vector3(0.03f, 1.55f, -0.05f)));
        foreach (var side in new[] { -1f, 1f })
        {
            // Arms held out in front, the classic shamble.
            _body.AddChild(Part(new BoxMesh { Size = new Vector3(0.09f, 0.09f, 0.62f), Material = Skin }, new Vector3(side * 0.22f, 1.25f, -0.33f)));
            _body.AddChild(Part(new SphereMesh { Radius = 0.025f, Height = 0.05f, Material = Eyes }, new Vector3(0.03f + side * 0.06f, 1.58f, -0.2f)));
        }
        if (Main.IsServer) return;
        Audio.GameAudio.Instance?.TrackFootsteps(this, -6);
        _nextGroan = GD.RandRange(1, 8);
    }

    private static MeshInstance3D Part(Mesh mesh, Vector3 at) =>
        new() { Mesh = mesh, Position = at, CastShadow = GeometryInstance3D.ShadowCastingSetting.On };

    public override void _Process(double delta)
    {
        if (Main.IsServer) return;
        var d = (float)delta;
        Position = Position.DistanceTo(NetPosition) > 4 ? NetPosition : Position.Lerp(NetPosition, 1 - Mathf.Exp(-12 * d));
        Rotation = new Vector3(0, Mathf.LerpAngle(Rotation.Y, NetYaw, 1 - Mathf.Exp(-10 * d)), 0);
        // Falls when down or destroyed; gets back up when it rises.
        var down = State is ZombieState.Down or ZombieState.Dead;
        _fall = Mathf.MoveToward(_fall, down ? 1 : 0, d * (down ? 4 : 1.5f));
        _walkPhase += NetMoving ? d * 7 : 0;
        _body.Rotation = new Vector3(-_fall * Mathf.Pi / 2, 0, Mathf.Sin(_walkPhase) * 0.12f * (1 - _fall));
        _body.Position = new Vector3(0, 0.15f * _fall, 0);
        if (State == ZombieState.Attack) _body.Rotation += new Vector3(0.25f, 0, 0); // lunging
        // Groans now and then, louder when it's after someone.
        if (State != ZombieState.Dead && (_nextGroan -= delta) <= 0)
        {
            _nextGroan = State is ZombieState.Chase or ZombieState.Attack or ZombieState.Bash ? GD.RandRange(2.5, 5) : GD.RandRange(6, 14);
            Audio.GameAudio.Instance?.Play3D("zombie_groan", GlobalPosition + Vector3.Up * 1.5f, -4, 0.15f, 25);
        }
    }
}
