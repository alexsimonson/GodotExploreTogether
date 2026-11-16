namespace ExploreTogether.Core.City;

/// <summary>What a player is doing, as a resident who sees them would judge it.</summary>
[Flags]
public enum Conduct
{
    None = 0,
    /// <summary>On private ground (a yard) they don't belong on.</summary>
    TrespassYard = 1,
    /// <summary>Inside a building they don't belong in (a shop counts while it's closed).</summary>
    TrespassInside = 2,
    /// <summary>Holding a melee weapon or tool out.</summary>
    Armed = 4,
    /// <summary>Holding a gun out.</summary>
    GunOut = 8,
    Crouching = 16,
    Running = 32,
    /// <summary>Dragging a body.</summary>
    Dragging = 64,
    /// <summary>Holding someone in a grip.</summary>
    Holding = 128,
    /// <summary>Just attacked someone, or fired a gun (the last couple of seconds).</summary>
    Violent = 256,
    /// <summary>Just kicked a door, picked a lock or shot one out (the last few seconds).</summary>
    Breaking = 512,
}

/// <summary>How bad what someone saw or heard is: 1 suspicious, 2 a crime, 3 violence.</summary>
public static class Severity
{
    public const int None = 0, Suspicious = 1, Crime = 2, Violent = 3;
}

/// <summary>A player as one resident sees them right now (the server has already checked sight lines).</summary>
/// <param name="Parcel">The parcel they're on, or -1 (street, alley).</param>
/// <param name="Clarity">How well they're seen, 0..1 (distance against sight range).</param>
public sealed record PlayerSighting(long Id, string Name, float X, float Z, Conduct Conduct, int Parcel, float Clarity);

/// <summary>A sound in the city: heard by residents within <see cref="Radius"/> m (half that through walls).</summary>
/// <param name="Player">Who made it, if anyone (0: unknown, or a resident).</param>
public readonly record struct Noise(float X, float Z, float Radius, string Cause, int Severity, long Player = 0);

/// <summary>A body (dead or unconscious) a resident can see.</summary>
public readonly record struct BodySighting(int NpcId, float X, float Z, bool Dead);

/// <summary>Everything one resident perceives in one perception tick.</summary>
public sealed class Percepts
{
    public List<PlayerSighting> Players { get; } = new();
    public List<Noise> Noises { get; } = new();
    public List<BodySighting> Bodies { get; } = new();
    /// <summary>Damaged doors (parcel ids) in sight.</summary>
    public List<int> DamagedDoors { get; } = new();

    public void Clear()
    {
        Players.Clear();
        Noises.Clear();
        Bodies.Clear();
        DamagedDoors.Clear();
    }
}

/// <summary>
/// Stealth: how residents notice players, and how much what they see worries them. Pure rules, unit-tested; the
/// server applies them each perception tick (sight lines need physics), and each resident's
/// <see cref="CityNpcBrain"/> turns suspicion into reactions.
///
/// <b>Seeing.</b> Sight range grows with the light where the player stands (<see cref="CityLighting"/>): about 40 m
/// in daylight or under a lamp, 8 m by moonlight, less again crouched. Residents see in a 120° cone ahead (200° once
/// alerted), and always notice someone within <see cref="TouchRange"/>. Asleep, they see nothing.
///
/// <b>Suspicion</b> per player, 0..100, rises while they see something wrong and fades when they don't:
/// - Trespass: in a yard is suspicious; inside a building is serious. It matters most to those with authority over
///   the place (owners, staff, security there, police); bystanders mostly mind their own business (nosy ones less so).
/// - Weapons out: a gun in public worries everyone; police mind any weapon. Worse while trespassing.
/// - Crimes seen happen at once: violence (attacking someone, gunfire), dragging a body, holding someone, breaking a
///   door. Bodies and broken doors are alarming even without a culprit.
/// - Sneaking (crouched) where you shouldn't be looks suspicious in itself.
/// At 25 a resident is curious (stops and stares), at 50 suspicious (authority confronts, others watch), at 100
/// alarmed (police arrest; others flee, call the police or, if brave, step in).
/// </summary>
public static class Stealth
{
    public const float Curious = 25, Suspicious = 50, Alarmed = 100;
    public const float TouchRange = 2.2f;
    /// <summary>Suspicion fades by this much per second while the player isn't seen doing anything wrong.</summary>
    public const float Fade = 4f;

    /// <summary>How far a resident can make out someone standing in this light (m).</summary>
    public static float SightRange(float light, bool crouching, bool running)
    {
        var range = 4f + 36f * Math.Clamp(light, 0, 1);
        if (crouching) range *= 0.6f;
        if (running) range *= 1.15f;
        return MathF.Max(range, TouchRange);
    }

    /// <summary>Is (tx, tz) within the field of view of someone at (x, z) facing <paramref name="yaw"/>?</summary>
    public static bool InView(float yaw, float x, float z, float tx, float tz, bool alert)
    {
        float dx = tx - x, dz = tz - z;
        var d = MathF.Sqrt(dx * dx + dz * dz);
        if (d <= TouchRange) return true;
        float fx = -MathF.Sin(yaw), fz = -MathF.Cos(yaw);
        var half = (alert ? 100f : 60f) * MathF.PI / 180f;
        return (fx * dx + fz * dz) / d >= MathF.Cos(half);
    }

    /// <summary>How clearly someone at distance <paramref name="d"/> is seen, 0..1 (0 beyond range).</summary>
    public static float Clarity(float d, float range) => d <= TouchRange ? 1 : Math.Clamp(1 - (d - TouchRange) / MathF.Max(range - TouchRange, 0.1f), 0, 1);

    /// <summary>
    /// How fast (per second, at full clarity) what a resident sees raises their suspicion of a player, how serious
    /// it is, and why. A rate of <see cref="float.PositiveInfinity"/> means at once.
    /// </summary>
    /// <param name="hour">Hour of day (shops are public only while open; sneaking at night is more suspicious).</param>
    public static (float Rate, int Severity, string Reason) Judge(Person observer, PlayerSighting p, CityLayout city, float hour)
    {
        var c = p.Conduct;
        var authority = observer.HasAuthorityOver(p.Parcel);
        var police = observer.Role == NpcRole.Police;
        var nosy = observer.Has(NpcTraits.Nosy);
        var rate = 0f;
        var severity = Severity.None;
        var reason = "";
        void Add(float r, int s, string why)
        {
            rate += r;
            if (s > severity || s == severity && reason == "") (severity, reason) = (s, why);
        }

        if ((c & Conduct.Violent) != 0) return (float.PositiveInfinity, Severity.Violent, "attacking someone");
        if ((c & Conduct.Dragging) != 0) return (float.PositiveInfinity, Severity.Crime, "dragging a body");
        if ((c & Conduct.Holding) != 0) return (float.PositiveInfinity, Severity.Crime, "grabbing someone");
        if ((c & Conduct.Breaking) != 0)
        {
            if (authority || nosy) return (float.PositiveInfinity, Severity.Crime, "breaking in");
            Add(60, Severity.Crime, "breaking in");
        }

        if ((c & Conduct.TrespassInside) != 0)
            Add(authority ? 80 : nosy ? 25 : 8, authority ? Severity.Crime : Severity.Suspicious, "trespassing");
        else if ((c & Conduct.TrespassYard) != 0)
            Add(authority ? 30 : nosy ? 10 : 2, Severity.Suspicious, "trespassing");

        var trespass = (c & (Conduct.TrespassInside | Conduct.TrespassYard)) != 0;
        if ((c & Conduct.GunOut) != 0)
            Add(police ? 30 : observer.Has(NpcTraits.Timid) ? 20 : 12,
                trespass ? Severity.Crime : Severity.Suspicious, "brandishing a gun");
        else if ((c & Conduct.Armed) != 0)
            Add(police ? 10 : nosy ? 6 : 2, trespass ? Severity.Crime : Severity.Suspicious, "carrying a weapon");

        var night = CityLighting.Daylight(hour) < 0.3f;
        if ((c & Conduct.Crouching) != 0 && (trespass || night))
            Add(authority || police ? 12 : nosy ? 6 : 2, Severity.Suspicious, "sneaking around");
        return (rate * MathF.Max(p.Clarity, 0.25f), severity, reason);
    }

    /// <summary>Shops are open (public) from 8:00 to 18:00 while unlocked; otherwise they're private like any building.</summary>
    public static bool ShopOpen(float hour) => hour >= 8 && hour < 18;

    // ---- noises ----

    public const float GunshotRadius = 55, ShotgunRadius = 65, KickRadius = 28, BreakRadius = 35, PickRadius = 4,
        ScreamRadius = 22, ShoutRadius = 16, FightRadius = 14, FootstepsRadius = 6;

    /// <summary>Is a noise heard at (x, z)? Through a building's walls (either side) it carries half as far.</summary>
    public static bool Hears(Noise n, float x, float z, bool throughWalls)
    {
        var r = throughWalls ? n.Radius / 2 : n.Radius;
        return (n.X - x) * (n.X - x) + (n.Z - z) * (n.Z - z) <= r * r;
    }

    /// <summary>Does a noise this loud wake a sleeper (heavy sleepers only for gunfire)?</summary>
    public static bool Wakes(Noise n, Person sleeper) =>
        sleeper.Has(NpcTraits.HeavySleeper) ? n.Radius >= GunshotRadius : n.Radius >= KickRadius - 1;
}
