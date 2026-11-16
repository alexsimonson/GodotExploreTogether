using ExploreTogether.Core.Dungeon;

namespace ExploreTogether.Core.City;

/// <summary>
/// Walking routes through a city, made only of streets plus the straight way through one parcel's gate and door.
/// So a route never crosses anyone else's property, and a person following routes never trespasses.
///
/// Street legs go from street cell to street cell (all 12 m cells are open ground) with a fixed personal offset
/// (up to ±<see cref="MaxLaneOffset"/> m) so people don't all walk the same line. Each cell is convex and the
/// offset is the same for every point, so every leg stays on street cells.
/// </summary>
public sealed class CityPaths
{
    public const float MaxLaneOffset = 3f;

    private readonly CityLayout _city;
    public IReadOnlyList<Cell> StreetCells { get; }

    public CityPaths(CityLayout city)
    {
        _city = city;
        var cells = new List<Cell>();
        for (var x = 0; x < city.CellsX; x++)
            for (var z = 0; z < city.CellsZ; z++)
                if (city.IsStreet(new Cell(x, z))) cells.Add(new Cell(x, z));
        StreetCells = cells;
    }

    /// <summary>
    /// True if (x, z) is on a street cell and at least <paramref name="margin"/> from every edge it shares with a
    /// parcel (or the city wall): somewhere a resident can step aside to without brushing a fence.
    /// </summary>
    public static bool IsOpenStreet(CityLayout city, float x, float z, float margin = 0.5f)
    {
        var c = CityLayout.CellAt(x, z);
        if (!city.IsStreet(c)) return false;
        var r = CityLayout.CellRect(c);
        if (x - r.MinX < margin && !city.IsStreet(c with { X = c.X - 1 })) return false;
        if (r.MaxX - x < margin && !city.IsStreet(c with { X = c.X + 1 })) return false;
        if (z - r.MinZ < margin && !city.IsStreet(c with { Z = c.Z - 1 })) return false;
        if (r.MaxZ - z < margin && !city.IsStreet(c with { Z = c.Z + 1 })) return false;
        return true;
    }

    /// <summary>The street cell in front of a parcel's gate.</summary>
    public static Cell StreetOf(Parcel p)
    {
        var (dx, dz) = MazePieces.Vector(p.Front);
        return new Cell(p.FrontCell.X + dx, p.FrontCell.Z + dz);
    }

    /// <summary>Points from inside a parcel (at the given spot) out to the street in front of it.</summary>
    public static List<(float X, float Z)> WayOut(Parcel p, (float X, float Z) from)
    {
        var way = WayIn(p, from);
        way.Reverse();
        return way;
    }

    /// <summary>Points from the street in front of a parcel through its gate and door to a spot inside.</summary>
    public static List<(float X, float Z)> WayIn(Parcel p, (float X, float Z) spot)
    {
        var (ox, oz) = MazePieces.Vector(p.Front); // outward, toward the street
        var (doorX, doorZ) = p.Door;
        // Distance from the door to the parcel's street edge (the fence line).
        var toEdge = p.Front switch
        {
            Sides.North => p.Bounds.MaxZ - doorZ,
            Sides.South => doorZ - p.Bounds.MinZ,
            Sides.East => p.Bounds.MaxX - doorX,
            _ => doorX - p.Bounds.MinX,
        };
        (float, float) Out(float d) => (doorX + ox * d, doorZ + oz * d);
        return
        [
            Out(toEdge + 1.5f), // on the street, in front of the gate
            Out(0.6f),          // in the yard, in front of the door
            Out(-1.2f),         // just inside
            spot,
        ];
    }

    /// <summary>Street cells from one to another (both included), shortest first; empty if unreachable.</summary>
    public List<Cell> StreetRoute(Cell from, Cell to)
    {
        if (from == to) return [from];
        var prev = new Dictionary<Cell, Cell> { [from] = from };
        var queue = new Queue<Cell>([from]);
        while (queue.TryDequeue(out var c))
        {
            foreach (var side in new[] { Sides.North, Sides.East, Sides.South, Sides.West })
            {
                var (dx, dz) = MazePieces.Vector(side);
                var n = new Cell(c.X + dx, c.Z + dz);
                if (!_city.IsStreet(n) || prev.ContainsKey(n)) continue;
                prev[n] = c;
                if (n == to)
                {
                    var route = new List<Cell> { to };
                    for (var at = c; at != from; at = prev[at]) route.Add(at);
                    route.Add(from);
                    route.Reverse();
                    return route;
                }
                queue.Enqueue(n);
            }
        }
        return [];
    }

    /// <summary>
    /// The points of a walk along streets, from street cell to street cell, with the walker's lane offset.
    /// The first and last cells are included.
    /// </summary>
    public List<(float X, float Z)> StreetWalk(Cell from, Cell to, (float X, float Z) lane)
    {
        var points = new List<(float X, float Z)>();
        foreach (var c in StreetRoute(from, to))
        {
            var (x, z) = CityLayout.CellCenter(c);
            points.Add((x + lane.X, z + lane.Z));
        }
        return points;
    }
}
