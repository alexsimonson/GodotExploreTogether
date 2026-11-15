using Godot;

namespace ExploreTogether.Player;

/// <summary>
/// A player in an instance. The node's name is the owning peer's id.
///
/// Networking: the server is the only replication authority. The owning client
/// simulates its own movement locally and sends its transform to the server
/// (SubmitTransform). The server checks the sender, applies it, and replicates
/// NetPosition/NetYaw to the other peers in the instance through the "Sync"
/// MultiplayerSynchronizer. Remote copies smooth toward those values. The owner
/// ignores them, so its own movement never snaps back.
/// </summary>
public partial class PlayerCharacter : CharacterBody3D
{
    // Replicated properties (see the SceneReplicationConfig in Player.tscn).
    [Export] public Vector3 NetPosition { get; set; }
    [Export] public float NetYaw { get; set; }
    [Export] public string DisplayName { get; set; } = "";

    private const float WalkSpeed = 5f;
    private const float SprintSpeed = 8f;
    private const float JumpVelocity = 4.5f;
    private const float MouseSensitivity = 0.0025f;
    private const float RemoteSmoothing = 15f;
    private const float RemoteSnapDistance = 5f;
    private const float FallRespawnY = -20f;

    private Node3D _head = null!;
    private float _pitch;
    private Vector3 _lastSentPosition = new(float.NaN, 0, 0);
    private float _lastSentYaw = float.NaN;

    // Dev/test helpers (see Main for the args).
    private static readonly bool DevAutowalk = CmdArgs.Has("dev-autowalk");
    private static readonly bool DevLog = CmdArgs.Has("dev-log");
    private double _devLogTimer;

    public long OwnerPeerId => long.Parse(Name);
    public bool IsLocal { get; private set; }
    public MultiplayerSynchronizer Sync => GetNode<MultiplayerSynchronizer>("Sync");

    public override void _Ready()
    {
        _head = GetNode<Node3D>("Head");
        IsLocal = !Main.IsServer && OwnerPeerId == Multiplayer.GetUniqueId();

        Position = NetPosition;
        Rotation = new Vector3(0, NetYaw, 0);
        GetNode<Label3D>("NameTag").Text = DisplayName;

        if (IsLocal)
        {
            GetNode<Camera3D>("Head/Camera").Current = true;
            // First person: don't render our own body or name.
            GetNode<Node3D>("Body").Visible = false;
            GetNode<Node3D>("NameTag").Visible = false;
            Input.MouseMode = Input.MouseModeEnum.Captured;
        }

        // The server doesn't simulate players; it only applies submitted transforms.
        SetPhysicsProcess(!Main.IsServer);
        SetProcessUnhandledInput(IsLocal);

        if (!Main.IsServer)
            GD.Print($"[client] {(IsLocal ? "local" : "remote")} player {Name} ('{DisplayName}') spawned at {NetPosition}");
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventMouseMotion motion && Input.MouseMode == Input.MouseModeEnum.Captured)
        {
            RotateY(-motion.Relative.X * MouseSensitivity);
            _pitch = Mathf.Clamp(_pitch - motion.Relative.Y * MouseSensitivity, -1.5f, 1.5f);
            _head.Rotation = new Vector3(_pitch, 0, 0);
        }
        else if (@event.IsActionPressed("ui_cancel"))
        {
            Input.MouseMode = Input.MouseModeEnum.Visible;
        }
        else if (@event is InputEventMouseButton { Pressed: true } && Input.MouseMode != Input.MouseModeEnum.Captured)
        {
            Input.MouseMode = Input.MouseModeEnum.Captured;
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        if (IsLocal) SimulateLocal((float)delta);
        else SmoothRemote((float)delta);

        if (DevLog && !IsLocal && (_devLogTimer += delta) >= 2.0)
        {
            _devLogTimer = 0;
            GD.Print($"[client] remote player {Name} at {Position.Snapped(new Vector3(0.01f, 0.01f, 0.01f))}");
        }
    }

    private void SimulateLocal(float delta)
    {
        var velocity = Velocity;
        if (!IsOnFloor()) velocity += GetGravity() * delta;

        var captured = Input.MouseMode == Input.MouseModeEnum.Captured;
        var input = captured
            ? Input.GetVector(InputSetup.MoveLeft, InputSetup.MoveRight, InputSetup.MoveForward, InputSetup.MoveBack)
            : Vector2.Zero;

        if (DevAutowalk)
        {
            input = new Vector2(0, -1);
            RotateY(0.8f * delta);
        }

        if (captured && Input.IsActionJustPressed(InputSetup.Jump) && IsOnFloor())
            velocity.Y = JumpVelocity;

        var speed = captured && Input.IsActionPressed(InputSetup.Sprint) ? SprintSpeed : WalkSpeed;
        var direction = (Transform.Basis * new Vector3(input.X, 0, input.Y)).Normalized();
        velocity.X = direction.X * speed;
        velocity.Z = direction.Z * speed;
        Velocity = velocity;
        MoveAndSlide();

        if (Position.Y < FallRespawnY)
        {
            Position = new Vector3(0, 2, 0);
            Velocity = Vector3.Zero;
        }

        // Send only when something changed, to keep idle players quiet on the network.
        if (!Position.IsEqualApprox(_lastSentPosition) || !Mathf.IsEqualApprox(Rotation.Y, _lastSentYaw))
        {
            _lastSentPosition = Position;
            _lastSentYaw = Rotation.Y;
            RpcId(1, MethodName.SubmitTransform, Position, Rotation.Y);
        }
    }

    private void SmoothRemote(float delta)
    {
        if (Position.DistanceTo(NetPosition) > RemoteSnapDistance)
            Position = NetPosition;
        else
            Position = Position.Lerp(NetPosition, 1f - Mathf.Exp(-RemoteSmoothing * delta));

        Rotation = new Vector3(0, Mathf.LerpAngle(Rotation.Y, NetYaw, 1f - Mathf.Exp(-RemoteSmoothing * delta)), 0);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.UnreliableOrdered)]
    private void SubmitTransform(Vector3 position, float yaw)
    {
        if (!Main.IsServer || Multiplayer.GetRemoteSenderId() != OwnerPeerId) return;
        // Movement validation (speed checks, etc.) belongs here later.
        NetPosition = position;
        NetYaw = yaw;
        Position = position;
        Rotation = new Vector3(0, yaw, 0);
    }
}
