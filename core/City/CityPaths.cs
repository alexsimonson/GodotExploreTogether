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

    /// <summary>
    /// From anywhere on a parcel (say, where someone was dragged and left) back out to the street: from inside the
    /// building through its door, otherwise across the yard to the door's front (around the house if it's in the
    /// way), then out through the gate. Returns the points and whether the walk starts inside the building
    /// (then the second leg passes through the door).
    /// </summary>
    public static (List<(float X, float Z)> Points, bool ThroughDoor) WayToStreet(Parcel p, (float X, float Z) from)
    {
        var way = WayIn(p, from); // [street at the gate, yard in front of the door, just inside, ...]
        var (gateOut, yard, inside) = (way[0], way[1], way[2]);
        if (p.Footprint.Contains(from.X, from.Z))
            return ([inside, yard, gateOut], true);

        // In the yard: go round the building if the straight line to the door's front crosses it.
        var block = Grow(p.Footprint, CityCrowd.NpcRadius + 0.1f);
        var corners = new List<(float X, float Z)>();
        var g = Grow(p.Footprint, 0.9f);
        foreach (var c in new[] { (g.MinX, g.MinZ), (g.MaxX, g.MinZ), (g.MaxX, g.MaxZ), (g.MinX, g.MaxZ) })
            corners.Add((Math.Clamp(c.Item1, p.Bounds.MinX + 0.4f, p.Bounds.MaxX - 0.4f),
                         Math.Clamp(c.Item2, p.Bounds.MinZ + 0.4f, p.Bounds.MaxZ - 0.4f)));
        bool Clear((float X, float Z) a, (float X, float Z) b) => !SegmentHitsRect(a, b, block);
        static float Len((float X, float Z) a, (float X, float Z) b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));

        List<(float X, float Z)>? best = Clear(from, yard) ? [] : null;
        var bestLength = best != null ? Len(from, yard) : float.MaxValue;
        foreach (var c in corners)
            if (Clear(from, c) && Clear(c, yard) && Len(from, c) + Len(c, yard) < bestLength)
                (best, bestLength) = ([c], Len(from, c) + Len(c, yard));
        if (best == null)
            foreach (var c1 in corners)
                foreach (var c2 in corners)
                    if (c1 != c2 && Clear(from, c1) && Clear(c1, c2) && Clear(c2, yard)
                        && Len(from, c1) + Len(c1, c2) + Len(c2, yard) < bestLength)
                        (best, bestLength) = ([c1, c2], Len(from, c1) + Len(c1, c2) + Len(c2, yard));
        var points = best ?? [];
        points.Add(yard);
        points.Add(gateOut);
        return (points, false);
    }

    private static GroundRect Grow(GroundRect r, float by) => new(r.MinX - by, r.MinZ - by, r.MaxX + by, r.MaxZ + by);

    /// <summary>Does the segment a-b pass through the rectangle (Liang-Barsky clipping)?</summary>
    public static bool SegmentHitsRect((float X, float Z) a, (float X, float Z) b, GroundRect r)
    {
        float t0 = 0, t1 = 1, dx = b.X - a.X, dz = b.Z - a.Z;
        foreach (var (p, q) in new[] { (-dx, a.X - r.MinX), (dx, r.MaxX - a.X), (-dz, a.Z - r.MinZ), (dz, r.MaxZ - a.Z) })
        {
            if (MathF.Abs(p) < 1e-6f)
            {
                if (q < 0) return false;
                continue;
            }
            var t = q / p;
            if (p < 0) t0 = MathF.Max(t0, t);
            else t1 = MathF.Min(t1, t);
            if (t0 > t1) return false;
        }
        return true;
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
