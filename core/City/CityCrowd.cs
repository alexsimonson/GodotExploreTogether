namespace ExploreTogether.Core.City;

/// <summary>
/// Who stands where in a city, so residents don't walk into players or each other.
///
/// Rules (applied by <see cref="CityNpcBrain"/> as it walks):
/// - A move is blocked if it would bring the resident closer than the two bodies' radii to a player or another
///   resident. Moving away from someone they already overlap is always allowed, so overlaps resolve.
/// - On the street, a blocked resident sidesteps (to the right first, so two people meeting head-on pass each
///   other), but only onto open street: never into a yard.
/// - In yards, gateways, doorways and rooms (narrow, walled places) they wait instead. If another resident is in
///   the way for more than <see cref="GhostAfterSeconds"/>, they squeeze past them (a doorway can't jam forever).
///   They never pass through a player: a player in the doorway makes them wait.
/// - Near the end of a walk, being blocked counts as arriving: they stop beside whoever stands at their spot.
/// </summary>
public sealed class CityCrowd
{
    public const float NpcRadius = 0.35f;
    public const float PlayerRadius = 0.4f;
    public const float GhostAfterSeconds = 2.5f;

    public enum Blocker { None, Npc, Player }

    private readonly List<CityNpcBrain> _npcs = new();
    private readonly List<(float X, float Z, float Y)> _players = new();

    /// <summary>People more than this far apart in height (a floor apart) never get in each other's way.</summary>
    public const float SameFloor = 1.6f;

    public IReadOnlyList<CityNpcBrain> Npcs => _npcs;

    /// <summary>The city's doors, which residents open, close and lock as they pass (null: no doors).</summary>
    public CityDoors? Doors { get; set; }

    private CityLayout? _city;

    /// <summary>
    /// Starts the doors in a state that fits where everyone is: closed; locked where nobody is inside, and homes
    /// locked at night.
    /// </summary>
    public void SetUpDoors(CityLayout city, CityDoors doors, double time)
    {
        (_city, Doors) = (city, doors);
        var hour = time % 24;
        var night = hour >= 21 || hour < 6;
        foreach (var p in city.Parcels)
        {
            var occupied = _npcs.Any(b => b.Inside == p.Id);
            if (!occupied || night && CityPopulation.HomeCapacity(p.Building) > 0) doors.Lock(p.Id);
        }
    }

    /// <summary>Is a player within <paramref name="radius"/> of (x, z)?</summary>
    public bool PlayerNear(float x, float z, float radius) =>
        _players.Any(p => p.Y < SameFloor && Dist(p.X, p.Z, x, z) < radius);

    /// <summary>Is anyone who lives or works on the parcel (other than <paramref name="self"/>) on it right now?</summary>
    public bool AnyoneBelongingInside(int parcel, CityNpcBrain self) =>
        _city != null && _npcs.Any(b => b != self && !b.IsDown && (b.Person.Home == parcel || b.Person.Work == parcel)
                                       && _city.ParcelAt(b.X, b.Z)?.Id == parcel);

    public void Add(CityNpcBrain brain) => _npcs.Add(brain);

    public void Remove(CityNpcBrain brain) => _npcs.Remove(brain);

    /// <summary>Where the players are now (call before each step).</summary>
    public void SetPlayers(IEnumerable<(float X, float Z)> players)
    {
        _players.Clear();
        _players.AddRange(players.Select(p => (p.X, p.Z, 0f)));
    }

    /// <summary>Where the players are now, with their heights (call before each step).</summary>
    public void SetPlayers(IEnumerable<(float X, float Z, float Y)> players)
    {
        _players.Clear();
        _players.AddRange(players);
    }

    /// <summary>Moves everyone by one step.</summary>
    public void Step(float seconds, double time)
    {
        foreach (var b in _npcs) b.Update(seconds, time, this);
    }

    /// <summary>What (if anything) stops <paramref name="self"/> moving from (fx, fz) to (x, z).</summary>
    public Blocker Check(CityNpcBrain self, float fx, float fz, float x, float z, bool ignoreNpcs = false)
    {
        foreach (var (px, pz, py) in _players)
            if (MathF.Abs(py - self.Y) < SameFloor && Closer(fx, fz, x, z, px, pz, NpcRadius + PlayerRadius)) return Blocker.Player;
        if (ignoreNpcs) return Blocker.None;
        foreach (var other in _npcs)
            if (other != self && !other.IsDown && MathF.Abs(other.Y - self.Y) < SameFloor
                && Closer(fx, fz, x, z, other.X, other.Z, NpcRadius * 2)) return Blocker.Npc;
        return Blocker.None;
    }

    /// <summary>The nearest resident overlapping (x, z), other than <paramref name="self"/>, if any.</summary>
    public CityNpcBrain? Overlapping(CityNpcBrain self, float x, float z)
    {
        CityNpcBrain? best = null;
        var bestD = NpcRadius * 2;
        foreach (var other in _npcs)
        {
            if (other == self || other.IsDown || MathF.Abs(other.Y - self.Y) >= SameFloor) continue;
            var d = Dist(x, z, other.X, other.Z);
            if (d < bestD) (best, bestD) = (other, d);
        }
        return best;
    }

    /// <summary>True if the move ends within <paramref name="space"/> of (ox, oz) and gets closer to it.</summary>
    private static bool Closer(float fx, float fz, float x, float z, float ox, float oz, float space)
    {
        var after = Dist(x, z, ox, oz);
        return after < space && after < Dist(fx, fz, ox, oz) - 1e-4f;
    }

    private static float Dist(float ax, float az, float bx, float bz) =>
        MathF.Sqrt((ax - bx) * (ax - bx) + (az - bz) * (az - bz));
}
