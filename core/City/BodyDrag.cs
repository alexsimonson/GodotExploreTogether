namespace ExploreTogether.Core.City;

/// <summary>
/// Dragging a body (stealth-game style): the body trails the player on a short "rope", head first, so it swings
/// around corners behind them instead of staying rigidly behind their back. The server applies this to each of
/// the dragging player's movements, then keeps the body out of walls (a ray from the player).
/// </summary>
public static class BodyDrag
{
    /// <summary>From the player's position to the body's feet (the body is about 1.75 m long, head toward the player).</summary>
    public const float RopeLength = 2.1f;

    /// <summary>How close the player must be to grab a body (to its middle, roughly).</summary>
    public const float GrabReach = 2.5f;

    /// <summary>The player's walking speed while dragging (m/s); no sprinting or jumping.</summary>
    public const float DragSpeed = 2.2f;

    /// <summary>
    /// Where the body's feet go when the player is at (px, pz): pulled along the line toward the player until it's
    /// at most a rope's length away (never pushed: walking back toward it leaves it where it is). Also returns the
    /// yaw that points the body's head (its local +Z when lying on its back) at the player.
    /// </summary>
    public static (float X, float Z, float Yaw) Follow(float bodyX, float bodyZ, float px, float pz, float rope = RopeLength)
    {
        float dx = px - bodyX, dz = pz - bodyZ;
        var dist = MathF.Sqrt(dx * dx + dz * dz);
        if (dist < 1e-4f) return (bodyX, bodyZ, 0);
        var yaw = MathF.Atan2(dx, dz); // local +Z rotated by yaw = (sin, cos): toward the player
        if (dist <= rope) return (bodyX, bodyZ, yaw);
        var pull = dist - rope;
        return (bodyX + dx / dist * pull, bodyZ + dz / dist * pull, yaw);
    }
}
