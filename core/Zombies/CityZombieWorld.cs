using ExploreTogether.Core.City;

namespace ExploreTogether.Core.Zombies;

/// <summary>
/// A city as zombies see it: they walk the streets and through buildings (CityNav), stop at shut doors (and batter
/// them), and see along the server's sight lines. Wandering stays on the streets near where they are.
/// </summary>
public sealed class CityZombieWorld(CityLayout city, CityPaths paths, CityDoors doors, CityNav.ObstacleTest? sight = null)
    : IZombieWorld
{
    public IReadOnlyList<ZombieStep> Route(float fx, float fz, float fy, float tx, float tz, float ty) =>
        CityNav.Route(city, paths, Place.At(fx, fy, fz), Place.At(tx, ty, tz), (0, 0), sight)
            .Select(p => new ZombieStep(p.X, p.Z, p.Y, p.Door)).ToList();

    public (float X, float Z) WanderPoint(float x, float z, Random random)
    {
        var here = CityNav.NearestStreet(city, paths, (x, z));
        var near = paths.StreetCells.Where(c => Math.Abs(c.X - here.X) + Math.Abs(c.Z - here.Z) <= 4).ToList();
        var cell = near.Count > 0 ? near[random.Next(near.Count)] : here;
        var (cx, cz) = CityLayout.CellCenter(cell);
        return (cx + (float)(random.NextDouble() * 6 - 3), cz + (float)(random.NextDouble() * 6 - 3));
    }

    public bool CanSee(float ax, float az, float ay, float bx, float bz, float by) =>
        sight == null || sight(ax, az, bx, bz, ay) != CityNav.Obstacle.Wall;

    public bool DoorClosed(int door) => door >= 0 && door < doors.Count && !doors.IsOpen(door) && !doors.IsBroken(door);

    /// <summary>A street point between <paramref name="awayFrom"/> and <paramref name="within"/> metres of (x, z), for a zombie to start at.</summary>
    public static (float X, float Z) SpawnPoint(CityPaths paths, Random random, float x, float z, float awayFrom, float within = float.MaxValue)
    {
        var far = paths.StreetCells.Where(c =>
        {
            var (cx, cz) = CityLayout.CellCenter(c);
            var d = MathF.Sqrt((cx - x) * (cx - x) + (cz - z) * (cz - z));
            return d >= awayFrom && d <= within;
        }).ToList();
        var cell = far.Count > 0 ? far[random.Next(far.Count)] : paths.StreetCells[random.Next(paths.StreetCells.Count)];
        var (sx, sz) = CityLayout.CellCenter(cell);
        return (sx + (float)(random.NextDouble() * 4 - 2), sz + (float)(random.NextDouble() * 4 - 2));
    }
}
