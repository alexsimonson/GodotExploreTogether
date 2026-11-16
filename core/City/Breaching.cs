namespace ExploreTogether.Core.City;

/// <summary>
/// Getting past a locked door without its key (plan: docs/26-11-16/DOOR-BREACHING-PLAN.md), and wrestling with
/// people. The server applies these; the numbers live here so they're unit-tested.
///
/// - Lockpicking (hold L, with a lockpick): quiet and slow. 4 to 8 s depending on the building; 70% success. A
///   failure breaks the pick. Success unlocks the door without a trace.
/// - Kicking (K): loud. A locked door gives after 2 (cottage) to 6 (police station) kicks; an unlocked one at once.
///   It ends <see cref="CityDoors.DoorState.Broken"/>, forced open for good.
/// - Shotgun slugs at the deadbolt (from close up): very loud, instant; the lock is destroyed, the door intact.
/// - Grappling: a player can hold a resident as long as they don't wriggle free (each second, a chance from their
///   grit). Strangling them (hold the attack button) fills a meter over <see cref="StrangleSeconds"/>: full, they're
///   subdued (unconscious). They fight hardest at first and weaken as it goes; ease off and they get their breath
///   back. A resident holding a player arrests them
///   (police) after <see cref="ArrestSeconds"/> unless the player struggles free (mashing the grab key).
/// </summary>
public static class Breaching
{
    public const float PickChance = 0.7f;
    public const float KickCooldown = 0.8f;
    /// <summary>Reach for kicking and picking (m, from the player to the door's middle).</summary>
    public const float DoorReach = 2.2f;

    public static float PickSeconds(BuildingType type) => type switch
    {
        BuildingType.Cottage or BuildingType.RowHouse => 4f,
        BuildingType.House or BuildingType.Shop => 5f,
        BuildingType.Warehouse => 6f,
        BuildingType.Manor or BuildingType.Office => 7f,
        BuildingType.Guardhouse => 8f,
        _ => 5f,
    };

    public static int KicksToBreak(BuildingType type) => type switch
    {
        BuildingType.Cottage or BuildingType.RowHouse => 2,
        BuildingType.House or BuildingType.Shop => 3,
        BuildingType.Manor or BuildingType.Office or BuildingType.Warehouse => 4,
        BuildingType.Guardhouse => 6,
        _ => 3,
    };

    public static bool RollPick(Random random) => random.NextDouble() < PickChance;

    // ---- grappling ----

    /// <summary>How far in front of the holder a held person is kept (m).</summary>
    public const float HoldDistance = 0.7f;
    /// <summary>How close you must be to grab someone (m).</summary>
    public const float GrabReach = 1.8f;
    /// <summary>
    /// How long a strangle takes to subdue someone: 3.5 s for the feeblest to 6.5 s for the toughest, 15% quicker
    /// when they were taken by surprise.
    /// </summary>
    public static float StrangleSeconds(float grit, bool surprised) =>
        (3.5f + 3f * Math.Clamp(grit, 0, 1)) * (surprised ? 0.85f : 1f);

    /// <summary>Easing off: the strangle meter drains this much per second (they get their breath back).</summary>
    public const float StrangleRecoveryPerSecond = 0.15f;

    /// <summary>
    /// Their chance of wriggling free (per second) while being strangled: desperate at first (140% of
    /// <see cref="EscapeChancePerSecond"/>), fading to almost nothing as they black out.
    /// </summary>
    public static float EscapeWhileStrangled(float chance, float progress) =>
        chance * MathF.Max(0.1f, 1.4f - 1.3f * Math.Clamp(progress, 0, 1));

    /// <summary>Held by police this long: arrested.</summary>
    public const float ArrestSeconds = 5f;

    /// <summary>
    /// A held resident's chance (per second) of wriggling free: 5% for the feeblest, up to about 35% for the
    /// toughest. Grabbed from behind by surprise, half that (and they can't shout).
    /// </summary>
    public static float EscapeChancePerSecond(float grit, bool surprised) =>
        (0.05f + 0.3f * Math.Clamp(grit, 0, 1)) * (surprised ? 0.5f : 1f);

    /// <summary>
    /// A player struggling in a resident's grip: each press of the grab key frees them by this much (1 = free).
    /// About 6 presses against the feeblest, 15 against the strongest.
    /// </summary>
    public static float StrugglePerPress(float holderGrit) => 1f / (6f + 9f * Math.Clamp(holderGrit, 0, 1));

    /// <summary>Was a grab from behind (outside the resident's view, more than 100 degrees off where they face)?</summary>
    public static bool FromBehind(float yaw, float npcX, float npcZ, float px, float pz)
    {
        float fx = -MathF.Sin(yaw), fz = -MathF.Cos(yaw); // facing (yaw 0 faces -Z)
        float dx = px - npcX, dz = pz - npcZ;
        var d = MathF.Sqrt(dx * dx + dz * dz);
        if (d < 1e-4f) return false;
        return (fx * dx + fz * dz) / d < MathF.Cos(100f * MathF.PI / 180f);
    }
}
