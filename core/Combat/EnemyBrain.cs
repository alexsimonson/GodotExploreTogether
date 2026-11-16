namespace ExploreTogether.Core.Combat;

/// <summary>Port of GenericNPC.State.</summary>
public enum EnemyState { Wander, Chase, Attack, Defend, Flee, Knockback, Frozen, Death }

/// <summary>
/// The decision part of the original GenericNPC, engine-free so it can be unit-tested.
/// The engine side (EnemyCharacter) supplies what the enemy sees and moves it; this class decides the state.
///
/// Ported rules (ModernMan prefab values):
/// - SetState: Death is final; repeated states are ignored; no change while attacking (except to Death);
///   the previous state is remembered so Attack/Frozen can return to it.
/// - Vision: seeing a player switches to Chase (not while attacking or frozen); losing sight in Chase -> Wander.
/// - Chase: within chaseDistance (10) keep chasing; within actionDistance (2) start an Attack.
/// - Attack: lasts 2 s; at 1 s the target takes 20 damage if still within 2 m ("EVADED ATTACK" otherwise);
///   then return to the previous state.
/// - Frozen: for the freeze time, then return to the previous state. Knockback: 0.2 s, then Wander.
/// </summary>
public sealed class EnemyBrain
{
    public const float ChaseSpeed = 5f;
    public const float WanderSpeed = 3.5f;     // Unity NavMeshAgent default speed (the prefab didn't set one)
    public const float ChaseDistance = 10f;
    public const float ActionDistance = 2f;
    public const float AttackDuration = 2f;
    public const float AttackHitTime = 1f;
    public const float AttackReach = 2f;
    public const int AttackDamage = 20;
    public const float KnockbackDuration = 0.2f;
    public const float WanderRange = 5f;

    public EnemyState State { get; private set; } = EnemyState.Wander;
    private EnemyState _previous = EnemyState.Wander;
    private float _stateTime;
    private float _freezeTime;
    private bool _attackHitDone;

    public bool IsAttacking => State == EnemyState.Attack;
    public bool IsFrozen => State == EnemyState.Frozen;
    public bool IsDead => State == EnemyState.Death;

    /// <summary>Port of GenericNPC.SetState.</summary>
    public bool SetState(EnemyState newState)
    {
        if (State == EnemyState.Death && newState != EnemyState.Death) return false;
        if (State == newState) return false;
        if (IsAttacking && newState != EnemyState.Death) return false;
        _previous = State;
        State = newState;
        _stateTime = 0;
        _attackHitDone = false;
        return true;
    }

    /// <summary>What the engine should do this tick.</summary>
    public readonly record struct Decision(
        bool MoveToTarget, bool Wander, float Speed, bool FaceTarget, bool DealAttackDamage, bool Stop);

    /// <summary>
    /// Advances the brain. targetDistance is the distance to the visible player, or null if none is visible.
    /// </summary>
    public Decision Tick(float delta, float? targetDistance)
    {
        _stateTime += delta;

        // VisionSystem.Update: seeing a player starts a chase, unless attacking or frozen.
        if (!IsAttacking && !IsFrozen && !IsDead && State != EnemyState.Knockback && targetDistance != null)
            SetState(EnemyState.Chase);

        switch (State)
        {
            case EnemyState.Wander:
                return new Decision(false, true, WanderSpeed, false, false, false);

            case EnemyState.Chase:
                if (targetDistance is not { } d || d > ChaseDistance)
                {
                    SetState(EnemyState.Wander);
                    return new Decision(false, true, WanderSpeed, false, false, false);
                }
                if (d <= ActionDistance)
                {
                    SetState(EnemyState.Attack);
                    return new Decision(false, false, 0, true, false, true);
                }
                return new Decision(true, false, ChaseSpeed, true, false, false);

            case EnemyState.Attack:
            {
                var hit = false;
                if (!_attackHitDone && _stateTime >= AttackHitTime)
                {
                    _attackHitDone = true;
                    hit = targetDistance is { } dist && dist <= AttackReach; // CheckMeleeAttackDistance
                }
                if (_stateTime >= AttackDuration)
                {
                    var back = _previous;
                    State = EnemyState.Wander; // leave Attack first (SetState refuses while attacking)
                    SetState(back == EnemyState.Attack ? EnemyState.Wander : back);
                }
                return new Decision(false, false, 0, true, hit, true);
            }

            case EnemyState.Frozen:
                if (_stateTime >= _freezeTime)
                {
                    var back = _previous;
                    State = EnemyState.Wander;
                    SetState(back == EnemyState.Frozen ? EnemyState.Wander : back);
                }
                return new Decision(false, false, 0, false, false, true);

            case EnemyState.Knockback:
                if (_stateTime >= KnockbackDuration) SetState(EnemyState.Wander);
                return new Decision(false, false, 0, false, false, false);

            default: // Death, and the original's empty Defend/Flee
                return new Decision(false, false, 0, false, false, true);
        }
    }

    /// <summary>Port of HelpFreezeNPC.</summary>
    public void Freeze(float seconds)
    {
        if (IsDead) return;
        _freezeTime = seconds;
        if (!SetState(EnemyState.Frozen) && IsFrozen) _stateTime = 0; // refreeze extends
    }

    /// <summary>Port of Knockback (the original set the state directly, even mid-attack).</summary>
    public void Knockback()
    {
        if (IsDead) return;
        _previous = State;
        State = EnemyState.Knockback;
        _stateTime = 0;
    }

    public void Die() => SetState(EnemyState.Death);
}
