using System;
using System.Collections.Generic;
using System.Linq;
using ExploreTogether.Core.Combat;
using ExploreTogether.Player;
using Godot;

namespace ExploreTogether.Enemies;

/// <summary>
/// An enemy (port of the ModernMan prefab: GenericNPC + VisionSystem + Health), with a placeholder look.
///
/// Server: runs the EnemyBrain state machine, sees players (VisionSystem), moves with a
/// NavigationAgent3D on the instance's navigation mesh, and hurts players when its attack lands.
/// Clients: smooth toward the replicated position and show the state (attack lunge, frozen tint,
/// hit flash, death topple). Replicated: NetPosition, NetYaw, NetState, NetHealth.
///
/// Enemies collide with the world and players but not with each other (wandering enemies used to block
/// each other in narrow corridors).
///
/// Vision differs on purpose: the original cast a 50x30 grid of rays (1,500 per enemy per frame).
/// Here each player in the instance is checked directly against the same field of view (90 x 45 degrees),
/// view distance (10 m) and eye height (1.7 m), with one line-of-sight ray. Same rule, a few rays.
/// </summary>
public partial class EnemyCharacter : CharacterBody3D
{
    [Export] public Vector3 NetPosition { get; set; }
    [Export] public float NetYaw { get; set; }
    [Export] public int NetState { get; set; }
    [Export] public int NetHealth { get; set; } = MaxHealth;

    public const int MaxHealth = 100;
    public const float Radius = 0.4f;
    private const float EyeHeight = 1.7f;
    private const float FovHorizontal = 90f, FovVertical = 45f, ViewDistance = 10f;
    private const float VisionInterval = 0.1f, RepathInterval = 0.25f;
    /// <summary>Heights on a player's body the enemy can spot: head, chest, feet.</summary>
    private static readonly float[] SightPoints = [1.7f, 1.0f, 0.3f];
    private const float CorpseSeconds = 8f;

    public MultiplayerSynchronizer Sync => GetNode<MultiplayerSynchronizer>("Sync");
    public bool IsDead => (EnemyState)NetState == EnemyState.Death;
    public EnemyState State => (EnemyState)NetState;

    // ---- server ----
    private EnemyBrain _brain = null!;
    private Health _health = null!;
    private NavigationAgent3D _agent = null!;
    private Func<IEnumerable<PlayerCharacter>> _players = () => [];
    private Action<EnemyCharacter, PlayerCharacter, int>? _onPlayerHit;
    private Action<EnemyCharacter>? _onDeath;
    private Action<EnemyCharacter>? _onRemove;
    private PlayerCharacter? _target;
    private PlayerCharacter? _attackTarget;
    private float _visionTimer, _repathTimer, _deathTimer;
    private Vector3 _knockback;
    private readonly Random _random = new();
    private static readonly bool DevLog = CmdArgs.Has("dev-log-enemies");
    private Vector3 _stuckCheckFrom;
    private float _stuckTimer;
    private float _devLogTimer;

    // ---- client ----
    private Node3D _body = null!;
    private StandardMaterial3D _bodyMaterial = null!;
    private Label3D _healthLabel = null!;
    private float _flash, _lungeTime = -1, _deathAnim;
    private int _shownHealth = MaxHealth;
    private static readonly Color BodyColor = new(0.45f, 0.08f, 0.08f);

    public override void _Ready()
    {
        _body = GetNode<Node3D>("Body");
        Audio.GameAudio.Instance?.TrackFootsteps(this, 2);
        _healthLabel = GetNode<Label3D>("HealthLabel");
        _bodyMaterial = (StandardMaterial3D)GetNode<MeshInstance3D>("Body/Mesh").GetActiveMaterial(0).Duplicate();
        GetNode<MeshInstance3D>("Body/Mesh").MaterialOverride = _bodyMaterial;
        _agent = GetNode<NavigationAgent3D>("Agent");
        Position = NetPosition;
        Rotation = new Vector3(0, NetYaw, 0);
        _shownHealth = NetHealth;
        if (!Main.IsServer) _agent.QueueFree(); // clients don't navigate
    }

    // ============================================================ server

    /// <summary>Server: called once after spawning.</summary>
    public void ServerInit(Func<IEnumerable<PlayerCharacter>> players,
        Action<EnemyCharacter, PlayerCharacter, int> onPlayerHit, Action<EnemyCharacter> onDeath,
        Action<EnemyCharacter> onRemove)
    {
        _onRemove = onRemove;
        _brain = new EnemyBrain();
        _health = new Health(MaxHealth);
        _players = players;
        _onPlayerHit = onPlayerHit;
        _onDeath = onDeath;
    }

    /// <summary>Server: damage from a player attack. Returns true if it killed.</summary>
    public bool TakeDamage(int amount, Vector3? knockbackDirection = null)
    {
        if (_brain == null || _brain.IsDead) return false;
        var killed = _health.Damage(amount);
        NetHealth = _health.Current;
        if (killed)
        {
            _brain.Die();
            NetState = (int)EnemyState.Death;
            CollisionLayer = 0; // corpses don't block anyone
            Velocity = Vector3.Zero;
            _onDeath?.Invoke(this);
            return true;
        }
        if (knockbackDirection is { } dir)
        {
            // Port of Knockback(owner.transform.forward): a short shove away from the attacker.
            _brain.Knockback();
            _knockback = new Vector3(dir.X, 0, dir.Z).Normalized() * 6f;
        }
        return false;
    }

    /// <summary>Server: Frost magic (port of HelpFreezeNPC).</summary>
    public void Freeze(float seconds) => _brain?.Freeze(seconds);

    public override void _PhysicsProcess(double delta)
    {
        if (Main.IsServer) ServerTick((float)delta);
        else ClientTick((float)delta);
    }

    private void ServerTick(float dt)
    {
        if (_brain == null) return;
        if (_brain.IsDead)
        {
            if ((_deathTimer += dt) >= CorpseSeconds && _onRemove is { } remove)
            {
                _onRemove = null;
                remove(this); // the instance manager frees it, which despawns it on every client
            }
            return;
        }

        if ((_visionTimer -= dt) <= 0)
        {
            _visionTimer = VisionInterval;
            _target = FindVisiblePlayer();
        }
        float? targetDistance = _target is { } t && IsInstanceValid(t) && t.IsInsideTree()
            ? GlobalPosition.DistanceTo(t.GlobalPosition) : null;
        if (targetDistance == null) _target = null;

        var wasAttacking = _brain.IsAttacking;
        var decision = _brain.Tick(dt, targetDistance);
        if (!wasAttacking && _brain.IsAttacking) _attackTarget = _target; // GetChaseTargetObject at attack start
        if (decision.DealAttackDamage && _attackTarget is { } victim && IsInstanceValid(victim))
        {
            _onPlayerHit?.Invoke(this, victim, EnemyBrain.AttackDamage);
            // The blow may have killed the player, who is then removed from this instance straight away.
            if (!IsInstanceValid(victim) || !victim.IsInsideTree())
            {
                _attackTarget = null;
                if (_target == victim) _target = null;
            }
        }

        var velocity = Velocity;
        if (!IsOnFloor()) velocity += GetGravity() * dt;
        else velocity.Y = 0;

        Vector3 horizontal = Vector3.Zero;
        if (_brain.State == EnemyState.Knockback)
        {
            horizontal = _knockback;
            _knockback = _knockback.Lerp(Vector3.Zero, 1f - Mathf.Exp(-10f * dt));
        }
        else if (decision.MoveToTarget && _target != null)
        {
            if ((_repathTimer -= dt) <= 0)
            {
                _repathTimer = RepathInterval;
                _agent.TargetPosition = _target.GlobalPosition;
            }
            horizontal = SteerAlongPath(decision.Speed);
        }
        else if (decision.Wander)
        {
            // New wander spot when there, or when barely moving for a second (e.g. blocked by a player).
            var stuck = false;
            if ((_stuckTimer += dt) >= 1f)
            {
                stuck = GlobalPosition.DistanceTo(_stuckCheckFrom) < 0.2f;
                _stuckCheckFrom = GlobalPosition;
                _stuckTimer = 0;
            }
            if (stuck || _agent.IsNavigationFinished() || _agent.TargetPosition == Vector3.Zero)
                _agent.TargetPosition = RandomNavPoint(EnemyBrain.WanderRange);
            horizontal = SteerAlongPath(decision.Speed);
        }

        if (decision.FaceTarget && _target != null) FaceToward(_target.GlobalPosition, dt);
        else if (horizontal.LengthSquared() > 0.01f) FaceToward(GlobalPosition + horizontal, dt);

        velocity.X = horizontal.X;
        velocity.Z = horizontal.Z;
        Velocity = velocity;
        MoveAndSlide();

        NetPosition = Position;
        NetYaw = Rotation.Y;
        NetState = (int)_brain.State;

        if (DevLog && (_devLogTimer += dt) >= 1f)
        {
            _devLogTimer = 0;
            GD.Print($"[server] {Name}: {_brain.State} at {Position.Snapped(Vector3.One * 0.1f)} target={_target?.Name ?? "-"} " +
                     $"nav={(_agent.IsNavigationFinished() ? "done" : "moving")} next={_agent.GetNextPathPosition().Snapped(Vector3.One * 0.1f)} " +
                     $"vel={Velocity.Snapped(Vector3.One * 0.1f)} floor={IsOnFloor()} hits={GetSlideCollisionCount()}" +
                     (GetSlideCollisionCount() > 0 ? $" first={((Node)GetSlideCollision(0).GetCollider()).Name}" : ""));
        }
    }

    private Vector3 SteerAlongPath(float speed)
    {
        if (_agent.IsNavigationFinished()) return Vector3.Zero;
        var next = _agent.GetNextPathPosition();
        var dir = next - GlobalPosition;
        dir.Y = 0;
        return dir.LengthSquared() < 0.0001f ? Vector3.Zero : dir.Normalized() * speed;
    }

    /// <summary>Port of RandomNavMeshPoint: a random spot within range, snapped to the navigation mesh.</summary>
    private Vector3 RandomNavPoint(float range)
    {
        var angle = (float)_random.NextDouble() * Mathf.Tau;
        var dist = (float)_random.NextDouble() * range;
        var guess = GlobalPosition + new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * dist;
        var map = GetWorld3D().NavigationMap;
        return NavigationServer3D.MapGetIterationId(map) == 0 ? GlobalPosition : NavigationServer3D.MapGetClosestPoint(map, guess);
    }

    private void FaceToward(Vector3 point, float dt)
    {
        var dir = point - GlobalPosition;
        dir.Y = 0;
        if (dir.LengthSquared() < 0.0001f) return;
        var targetYaw = Mathf.Atan2(-dir.X, -dir.Z); // forward is -Z
        Rotation = new Vector3(0, Mathf.LerpAngle(Rotation.Y, targetYaw, 1f - Mathf.Exp(-5f * dt)), 0);
    }

    /// <summary>VisionSystem: the nearest player inside the view cone with a clear line of sight.</summary>
    private PlayerCharacter? FindVisiblePlayer()
    {
        var space = GetWorld3D().DirectSpaceState;
        var eye = GlobalPosition + new Vector3(0, EyeHeight, 0);
        var forward = -GlobalBasis.Z;
        PlayerCharacter? best = null;
        var bestDist = float.MaxValue;
        foreach (var p in _players())
        {
            if (!IsInstanceValid(p) || !p.IsInsideTree()) continue;
            // The original's ray grid saw any part of the player's body. Checking only one point (the chest)
            // missed players standing close: their chest drops below the 22.5 degree lower edge of the view.
            // So: the player is seen if their head, chest or feet is inside the view with a clear line of sight.
            foreach (var height in SightPoints)
            {
                var point = p.GlobalPosition + new Vector3(0, height, 0);
                var toTarget = point - eye;
                var dist = toTarget.Length();
                if (dist > ViewDistance || dist < 0.001f) continue;

                var flat = new Vector3(toTarget.X, 0, toTarget.Z);
                if (flat.LengthSquared() < 0.0001f) continue;
                var horizontalAngle = Mathf.RadToDeg(forward.SignedAngleTo(flat.Normalized(), Vector3.Up));
                var verticalAngle = Mathf.RadToDeg(Mathf.Atan2(toTarget.Y, flat.Length()));
                if (Mathf.Abs(horizontalAngle) > FovHorizontal / 2 || Mathf.Abs(verticalAngle) > FovVertical / 2) continue;

                // Walls block sight (world layer only).
                var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(eye, point, collisionMask: 1));
                if (hit.Count > 0) continue;

                if (dist < bestDist) { best = p; bestDist = dist; }
                break;
            }
        }
        return best;
    }

    // ============================================================ client

    private void ClientTick(float dt)
    {
        // Smooth toward the server's position and facing (like remote players).
        if (Position.DistanceTo(NetPosition) > 5f) Position = NetPosition;
        else Position = Position.Lerp(NetPosition, 1f - Mathf.Exp(-15f * dt));
        Rotation = new Vector3(0, Mathf.LerpAngle(Rotation.Y, NetYaw, 1f - Mathf.Exp(-15f * dt)), 0);

        // Hit flash and health label.
        if (NetHealth < _shownHealth) _flash = 0.25f;
        _shownHealth = NetHealth;
        _healthLabel.Visible = NetHealth < MaxHealth && !IsDead;
        _healthLabel.Text = $"{NetHealth} / {MaxHealth}";

        var color = State == EnemyState.Frozen ? new Color(0.35f, 0.6f, 1f) : BodyColor;
        if (_flash > 0) { _flash -= dt; color = new Color(1f, 0.3f, 0.3f); }
        _bodyMaterial.AlbedoColor = color;

        // Attack: lunge forward when the blow lands (1 s into the 2 s attack).
        if (State == EnemyState.Attack && _lungeTime < 0) _lungeTime = 0;
        if (State != EnemyState.Attack) _lungeTime = -1;
        var lunge = 0f;
        if (_lungeTime >= 0)
        {
            _lungeTime += dt;
            var t = _lungeTime - EnemyBrain.AttackHitTime;
            lunge = t is > -0.15f and < 0.25f ? 0.45f * (1f - Mathf.Abs(t - 0.05f) / 0.2f) : 0f;
            lunge = Mathf.Max(0, lunge);
        }

        // Death: topple over.
        if (IsDead) _deathAnim = Mathf.Min(1f, _deathAnim + dt * 2f);
        _body.Position = new Vector3(0, 0, -lunge);
        _body.Rotation = new Vector3(-Mathf.Pi / 2 * _deathAnim, 0, 0);
    }
}
