namespace ExploreTogether.Core.Net;

/// <summary>
/// Server-side check of a player's submitted movement (city security review, findings 4 and 5).
///
/// Clients simulate their own movement and send positions, so the server can't recompute it exactly. Instead it
/// keeps a distance budget: it refills at the player's current top speed (walking, sprinting, dragging a body, or
/// held by someone) and each submitted move spends its horizontal length. Packets that arrive bunched up after a
/// hiccup are covered by what built up meanwhile (up to <see cref="BurstSeconds"/>). A move that costs more than the
/// budget is a teleport or a speed hack: it's rejected, and the server sends the client back to the last accepted
/// position. Rising is limited the same way (jumps and stairs), falling isn't.
///
/// The server also rejects moves that pass through a wall or a closed door (a ray between the two positions);
/// that part needs physics and lives in ServerGame.
/// </summary>
public sealed class MoveGuard
{
    /// <summary>How much unused movement can build up (seconds at top speed).</summary>
    public const float BurstSeconds = 1.5f;
    /// <summary>Slack for rounding and the client's collision pushes (m).</summary>
    public const float Slack = 0.75f;
    /// <summary>Fastest climb (m/s): a jump is 4.5 m/s, stairs at a sprint a little more.</summary>
    public const float MaxRiseSpeed = 9f;

    public float X { get; private set; }
    public float Y { get; private set; }
    public float Z { get; private set; }

    private double _lastTime;
    private float _budget, _riseBudget;

    public MoveGuard(float x, float y, float z, double now) => Reset(x, y, z, now);

    /// <summary>The server put the player somewhere (spawn, respawn, correction): start over from there.</summary>
    public void Reset(float x, float y, float z, double now)
    {
        (X, Y, Z, _lastTime) = (x, y, z, now);
        _budget = Slack;
        _riseBudget = Slack;
    }

    /// <summary>
    /// Checks a submitted position at time <paramref name="now"/> (seconds) for a player whose top speed is
    /// <paramref name="maxSpeed"/> m/s. Accepted moves become the new reference; rejected ones change nothing.
    /// </summary>
    public bool Check(float x, float y, float z, double now, float maxSpeed)
    {
        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z)) return false;
        var dt = (float)Math.Clamp(now - _lastTime, 0, BurstSeconds);
        _lastTime = now;
        _budget = MathF.Min(_budget + maxSpeed * dt, maxSpeed * BurstSeconds + Slack);
        _riseBudget = MathF.Min(_riseBudget + MaxRiseSpeed * dt, MaxRiseSpeed * 0.5f + Slack);

        float dx = x - X, dz = z - Z, rise = MathF.Max(0, y - Y);
        var horizontal = MathF.Sqrt(dx * dx + dz * dz);
        if (horizontal > _budget || rise > _riseBudget) return false;
        _budget -= horizontal;
        _riseBudget -= rise;
        (X, Y, Z) = (x, y, z);
        return true;
    }
}

/// <summary>Players' top speeds (m/s), shared by the client's movement and the server's <see cref="MoveGuard"/>.</summary>
public static class PlayerSpeeds
{
    public const float Walk = 5f;
    public const float Sprint = 8f;
    /// <summary>Sneaking (crouched): slow and quiet.</summary>
    public const float Crouch = 2.4f;
    /// <summary>Dragging a body (see City.BodyDrag.DragSpeed) or holding someone.</summary>
    public const float Dragging = 2.2f;
    /// <summary>Held by a resident: you can barely move until you break free.</summary>
    public const float Held = 0.8f;
}
