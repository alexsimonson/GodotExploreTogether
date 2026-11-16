using System.Collections.Generic;
using System.Linq;
using ExploreTogether.Net;
using Godot;

namespace ExploreTogether.Player;

/// <summary>
/// A player in an instance. The node's name is the owning peer's id.
///
/// Networking: the server is the only replication authority. The owning client
/// simulates its own movement locally and sends its transform to the server
/// (NetSession.SendPlayerTransform). The server checks the sender, applies it, and replicates
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
    [Export] public Color PlayerColor { get; set; } = PlayerLook.DefaultColor;
    /// <summary>Server-owned health, replicated for the HUD (own player) and others' view.</summary>
    [Export] public int NetHealth { get; set; } = 100;
    /// <summary>Server-validated magic aim, replicated so other players see this player's ghost pentagram.</summary>
    [Export] public bool AimActive { get; set; }
    [Export] public Vector3 AimPoint { get; set; }
    private Node3D? _aimGhost;

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

    /// <summary>This client's own player, if spawned.</summary>
    public static PlayerCharacter? Local { get; private set; }

    public Camera3D Camera => GetNode<Camera3D>("Head/Camera");

    public long OwnerPeerId => long.Parse(Name);
    public bool IsLocal { get; private set; }
    public MultiplayerSynchronizer Sync => GetNode<MultiplayerSynchronizer>("Sync");

    public override void _Ready()
    {
        _head = GetNode<Node3D>("Head");
        IsLocal = !Main.IsServer && OwnerPeerId == Multiplayer.GetUniqueId();

        Position = NetPosition;
        Rotation = new Vector3(0, NetYaw, 0);
        var tag = GetNode<Label3D>("NameTag");
        tag.Text = DisplayName;
        tag.Modulate = PlayerColor.Lightened(0.35f);
        GetNode<MeshInstance3D>("Body").MaterialOverride = new StandardMaterial3D { AlbedoColor = PlayerColor, Roughness = 0.6f };

        if (IsLocal)
        {
            Local = this;
            Camera.Current = true;
            ApplyDevCamera();
            // First person: don't render our own body or name.
            GetNode<Node3D>("Body").Visible = false;
            GetNode<Node3D>("NameTag").Visible = false;
            Input.MouseMode = Input.MouseModeEnum.Captured;
        }

        // The server doesn't simulate players; it only applies submitted transforms.
        SetPhysicsProcess(!Main.IsServer);
        SetProcessUnhandledInput(IsLocal);

        if (!Main.IsServer)
            GD.Print($"[client] {(IsLocal ? "local" : "remote")} player {Name} ('{DisplayName}', #{PlayerColor.ToHtml(false)}) spawned at {NetPosition}");
    }

    /// <summary>--dev-camera YAW,PITCH (degrees): point the view, for scripted screenshots.</summary>
    private void ApplyDevCamera()
    {
        var parts = CmdArgs.Get("dev-camera").Split(',');
        if (parts.Length != 2
            || !float.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out var yaw)
            || !float.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var pitch)) return;
        Rotation = new Vector3(0, Mathf.DegToRad(yaw), 0);
        _pitch = Mathf.DegToRad(pitch);
        _head.Rotation = new Vector3(_pitch, 0, 0);
    }

    public override void _ExitTree()
    {
        if (Local == this) Local = null;
        if (!Main.IsServer && !IsLocal)
            GD.Print($"[client] remote player {Name} ('{DisplayName}') despawned");
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
        var chaseTarget = DevChaseEnemy
            ? SolidNeighbours().Select(n => n.Body).OfType<Enemies.EnemyCharacter>().OrderBy(e => e.GlobalPosition.DistanceTo(GlobalPosition)).FirstOrDefault() as Node3D
            : DevChaseNpc ? DevNpcTarget()
            : DevChase ? SolidNeighbours().Select(n => n.Body).OfType<PlayerCharacter>().FirstOrDefault() : null;
        if (chaseTarget is { } target)
        {
            // Walk straight at another player (tests player-vs-player collision).
            LookAt(new Vector3(target.Position.X, Position.Y, target.Position.Z));
            input = new Vector2(0, -1);
        }

        if (captured && Input.IsActionJustPressed(InputSetup.Jump) && IsOnFloor())
            velocity.Y = JumpVelocity;

        var speed = captured && Input.IsActionPressed(InputSetup.Sprint) ? SprintSpeed : WalkSpeed;
        var direction = (Transform.Basis * new Vector3(input.X, 0, input.Y)).Normalized();
        velocity.X = direction.X * speed;
        velocity.Z = direction.Z * speed;
        velocity = BlockByOtherPlayers(velocity);
        Velocity = velocity;
        MoveAndSlide();
        SeparateFromOtherPlayers();

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
            GetNode<NetSession>("/root/Main/Session").SendPlayerTransform(InstanceFactory.InstanceIdOf(this), Position, Rotation.Y);
        }
    }

    // ---- Player-vs-player collision ----------------------------------------------------------
    // Players aren't solid to each other in the physics engine (collision_mask = world only).
    // Physics resolves overlaps in the shortest direction, which for stacked capsules is up, and
    // because each client moves only its own player against the others' copies, two clients pushing
    // "up and out" of each other formed a loop that launched both players into the sky.
    // Instead, each client keeps its own player out of the others HORIZONTALLY only: it can't walk
    // into them, and if they overlap (spawning, lag), it slides itself sideways out. Both clients do
    // this, so overlaps resolve in a frame or two, never vertically. (No standing on heads.)

    private const float BodyRadius = 0.4f;   // matches the capsule in Player.tscn
    private const float BodyHeight = 1.8f;

    private static readonly bool DevChase = CmdArgs.Has("dev-chase");
    private static readonly bool DevChaseEnemy = CmdArgs.Has("dev-chase-enemy");
    /// <summary>--dev-chase-npc: run at the nearest walking city resident, logging the closest we get to any resident.</summary>
    private static readonly bool DevChaseNpc = CmdArgs.Has("dev-chase-npc");
    private float _devClosestNpc = float.MaxValue;
    private double _devNpcLogTimer;

    private Node3D? DevNpcTarget()
    {
        var npcs = SolidNeighbours().Select(n => n.Body).OfType<City.CityNpc>().ToList();
        foreach (var n in npcs)
            _devClosestNpc = Mathf.Min(_devClosestNpc, new Vector2(Position.X - n.Position.X, Position.Z - n.Position.Z).Length());
        if ((_devNpcLogTimer += GetPhysicsProcessDeltaTime()) >= 2 && npcs.Count > 0)
        {
            _devNpcLogTimer = 0;
            GD.Print($"[client] closest resident so far: {_devClosestNpc:0.00} m");
        }
        // Only someone we can run straight at (no wall or fence in between); otherwise wait for someone to come by.
        var space = GetWorld3D().DirectSpaceState;
        bool Clear(Node3D n) => space.IntersectRay(PhysicsRayQueryParameters3D.Create(
            Position + Vector3.Up * 0.5f, n.Position + Vector3.Up * 0.5f, collisionMask: 1, exclude: [GetRid()])).Count == 0;
        return npcs.Where(n => n.NetMoving && n.Position.DistanceTo(Position) < 30 && Clear(n))
            .OrderBy(n => n.Position.DistanceTo(Position)).FirstOrDefault();
    }

    /// <summary>
    /// Other players, living enemies and city residents in this instance: the bodies we keep out of horizontally,
    /// with how close we may come to each (our radius plus theirs).
    /// </summary>
    private IEnumerable<(Node3D Body, float Space)> SolidNeighbours()
    {
        foreach (var p in GetParent().GetChildren().OfType<PlayerCharacter>())
            if (p != this) yield return (p, BodyRadius * 2f);
        var instance = GetParent().GetParent();
        if (instance?.GetNodeOrNull("Enemies") is { } enemies)
            foreach (var e in enemies.GetChildren().OfType<Enemies.EnemyCharacter>())
                if (!e.IsDead) yield return (e, BodyRadius + Enemies.EnemyCharacter.Radius);
        if (instance?.GetNodeOrNull("Npcs") is { } npcs)
            foreach (var n in npcs.GetChildren().OfType<City.CityNpc>())
                yield return (n, BodyRadius + Core.City.CityCrowd.NpcRadius);
    }

    /// <summary>Horizontal offset from another body and whether we overlap it vertically.</summary>
    private (Vector2 Offset, bool SameLevel) Relative(Node3D other) =>
        (new Vector2(Position.X - other.Position.X, Position.Z - other.Position.Z),
         Mathf.Abs(Position.Y - other.Position.Y) < BodyHeight);

    /// <summary>Removes the part of our horizontal velocity that would take us into another player.</summary>
    private Vector3 BlockByOtherPlayers(Vector3 velocity)
    {
        foreach (var (other, space) in SolidNeighbours())
        {
            var (offset, sameLevel) = Relative(other);
            var dist = offset.Length();
            if (!sameLevel || dist >= space + 0.05f || dist < 0.0001f) continue;
            var away = offset / dist;
            var horizontal = new Vector2(velocity.X, velocity.Z);
            var into = horizontal.Dot(away);
            if (into < 0)
            {
                horizontal -= away * into;
                velocity.X = horizontal.X;
                velocity.Z = horizontal.Y;
            }
        }
        return velocity;
    }

    /// <summary>If we overlap another player, slide ourselves sideways out (respecting walls).</summary>
    private void SeparateFromOtherPlayers()
    {
        foreach (var (other, space) in SolidNeighbours())
        {
            var (offset, sameLevel) = Relative(other);
            var dist = offset.Length();
            if (!sameLevel || dist >= space) continue;
            // Exactly on top: split along X; between players the peer ids decide, so both clients agree.
            var otherId = other is PlayerCharacter op ? op.OwnerPeerId : 0;
            var away = dist > 0.0001f ? offset / dist : new Vector2(OwnerPeerId > otherId ? 1 : -1, 0);
            // Another player moves itself the other half; enemies and residents don't yield, so take the whole gap.
            var push = (other is PlayerCharacter ? (space - dist) * 0.5f : space - dist) + 0.01f;
            MoveAndCollide(new Vector3(away.X * push, 0, away.Y * push));
        }
    }

    /// <summary>Other players' magic ghosts, in their color (our own is drawn by ClientGame).</summary>
    private void UpdateRemoteAimGhost(float delta)
    {
        if (!AimActive)
        {
            if (_aimGhost != null) _aimGhost.Visible = false;
            return;
        }
        if (_aimGhost == null)
        {
            _aimGhost = Combat.MagicAim.CreatePentagram(PlayerColor);
            _aimGhost.TopLevel = true; // positioned in the world, not relative to the player
            AddChild(_aimGhost);
            _aimGhost.GlobalPosition = AimPoint;
            GD.Print($"[client] showing {DisplayName}'s aim ghost at {AimPoint.Snapped(Vector3.One * 0.1f)}");
        }
        _aimGhost.Visible = true;
        var target = AimPoint + new Vector3(0, 0.03f, 0);
        _aimGhost.GlobalPosition = _aimGhost.GlobalPosition.DistanceTo(target) > 3f
            ? target
            : _aimGhost.GlobalPosition.Lerp(target, 1f - Mathf.Exp(-20f * delta));
    }

    private void SmoothRemote(float delta)
    {
        UpdateRemoteAimGhost(delta);
        if (Position.DistanceTo(NetPosition) > RemoteSnapDistance)
            Position = NetPosition;
        else
            Position = Position.Lerp(NetPosition, 1f - Mathf.Exp(-RemoteSmoothing * delta));

        Rotation = new Vector3(0, Mathf.LerpAngle(Rotation.Y, NetYaw, 1f - Mathf.Exp(-RemoteSmoothing * delta)), 0);
    }

    /// <summary>Server: applies the owner's submitted transform (see NetSession.SendPlayerTransform).</summary>
    public void ApplySubmittedTransform(Vector3 position, float yaw)
    {
        // Movement validation (speed checks, etc.) belongs here later.
        NetPosition = position;
        NetYaw = yaw;
        Position = position;
        Rotation = new Vector3(0, yaw, 0);
    }
}
