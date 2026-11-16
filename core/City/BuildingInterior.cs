namespace ExploreTogether.Core.City;

/// <summary>A point in the city, on a floor: 0 is the ground (streets, yards, ground floors), 1 the first floor up...</summary>
public readonly record struct Place(float X, float Z, int Floor = 0)
{
    public float Y => Floor * BuildingInterior.FloorHeight;

    /// <summary>The floor someone standing at height <paramref name="y"/> is on.</summary>
    public static int FloorAt(float y) => Math.Max(0, (int)MathF.Round(y / BuildingInterior.FloorHeight));

    public static Place At(float x, float y, float z) => new(x, z, FloorAt(y));

    public float DistanceTo(Place o) =>
        MathF.Sqrt((X - o.X) * (X - o.X) + (Z - o.Z) * (Z - o.Z) + (Y - o.Y) * (Y - o.Y));
}

/// <summary>A room: a rectangle of floor (world coordinates) on one floor.</summary>
public sealed record Room(int Floor, GroundRect Rect);

/// <summary>
/// An interior wall on a floor, from A to B (world, axis-aligned), with doorway gaps (their middles, along it).
/// </summary>
public sealed record Partition(int Floor, (float X, float Z) A, (float X, float Z) B, IReadOnlyList<(float X, float Z)> Gaps);

/// <summary>
/// A straight flight of stairs from <see cref="FromFloor"/> to the floor above, filling <see cref="Rect"/> (the
/// stairwell; the floor above has a hole there). It rises from <see cref="Bottom"/> (on the lower floor) to
/// <see cref="Top"/> (on the upper), toward the back of the building. The approaches are just off it in a room.
/// </summary>
public sealed record Stair(int FromFloor, GroundRect Rect, (float X, float Z) Bottom, (float X, float Z) Top,
    (float X, float Z) BottomApproach, (float X, float Z) TopApproach);

/// <summary>
/// The inside of a greybox building: its floors, rooms, partition walls with doorways, and stairs; where people stand
/// on each floor; and routes between any two points inside (through doorways and up or down stairs).
/// Generated from the parcel and the design's size (seeded by nothing: the same building is the same inside), so
/// server and client agree. <see cref="CityBuilder"/> builds it; residents walk it.
///
/// Layout (building-local: x across the front, z from the front face (0) back to -depth):
/// - Stairs run along a side wall, 1.2 m wide, rising 3.2 m over 5 m toward the back. Flights alternate sides
///   (the first on the side away from the front door), so every floor has a stairwell up on one side and the hole of
///   the one below on the other.
/// - Between the stair strips, each floor is split into rooms: a cross wall halfway back (front and back rooms), and
///   in wide buildings walls front to back about every 6 m. Every wall has a 1.1 m doorway into each room it
///   separates. A side strip without stairs on that floor belongs to the room beside it. Warehouses' ground floors
///   stay one open hall.
/// - Buildings too small for stairs (less than 5.4 m wide or 7.8 m deep) keep their upper floors closed.
/// </summary>
public sealed class BuildingInterior
{
    public const float FloorHeight = 3.2f, WallThickness = 0.3f, StairWidth = 1.2f, StairRun = 5.0f,
        GapWidth = 1.1f, SlabThickness = 0.2f;

    /// <summary>Floors people can reach (1 when there are no stairs).</summary>
    public int Floors { get; }
    public IReadOnlyList<Room> Rooms { get; }
    public IReadOnlyList<Partition> Partitions { get; }
    public IReadOnlyList<Stair> Stairs { get; }
    /// <summary>Where people stand, on every floor.</summary>
    public IReadOnlyList<Place> Spots { get; }

    private readonly List<(Place At, List<int> Rooms)> _nodes = new();
    private readonly List<(int A, int B)> _links = new(); // stair links (other links: nodes sharing a room)

    private BuildingInterior(int floors, List<Room> rooms, List<Partition> partitions, List<Stair> stairs, List<Place> spots)
    {
        (Floors, Rooms, Partitions, Stairs, Spots) = (floors, rooms, partitions, stairs, spots);
        BuildGraph();
    }

    public static bool HasStairs(float width, float depth, int floors) => floors > 1 && width >= 5.4f && depth >= 7.8f;

    /// <param name="width">The building's width along the street (m).</param>
    /// <param name="depth">Its depth back from the street.</param>
    /// <param name="openGround">One open hall on the ground floor (warehouses).</param>
    public static BuildingInterior Generate(Parcel p, float width, float depth, int floors, bool openGround)
    {
        const float t = WallThickness, sw = StairWidth;
        float x0 = -width / 2 + t, x1 = width / 2 - t, zF = -t, zB = -depth + t;
        var reachable = HasStairs(width, depth, floors) ? floors : 1;

        // Local -> world helpers (the building is turned by a multiple of 90 degrees, so rectangles stay rectangles).
        (float X, float Z) W(float lx, float lz) => p.ToWorld(lx, lz);
        GroundRect R(float ax, float az, float bx, float bz)
        {
            var (wx0, wz0) = W(ax, az);
            var (wx1, wz1) = W(bx, bz);
            return new GroundRect(MathF.Min(wx0, wx1), MathF.Min(wz0, wz1), MathF.Max(wx0, wx1), MathF.Max(wz0, wz1));
        }

        var (doorLx, _) = p.ToLocal(p.Door.X, p.Door.Z);
        var firstSide = doorLx > 0 ? -1 : 1; // the first flight on the side away from the door
        int SideOf(int stair) => stair % 2 == 0 ? firstSide : -firstSide;
        (float Lo, float Hi) Strip(int side) => side > 0 ? (x1 - sw, x1) : (x0, x0 + sw);
        float zb = zF - 1.3f, zt = zb - StairRun;

        var stairs = new List<Stair>();
        for (var i = 0; i + 1 < reachable; i++)
        {
            var side = SideOf(i);
            var (lo, hi) = Strip(side);
            var cx = (lo + hi) / 2;
            var inner = side > 0 ? lo - 0.7f : hi + 0.7f; // just off the strip, in the room beside it
            stairs.Add(new Stair(i, R(lo, zt, hi, zb), W(cx, zb + 0.15f), W(cx, zt - 0.15f),
                W(inner, zb + 0.6f), W(inner, zt - 0.6f)));
        }

        var rooms = new List<Room>();
        var partitions = new List<Partition>();
        var spots = new List<Place>();
        for (var f = 0; f < reachable; f++)
        {
            // A side strip is busy on this floor if a flight starts here or the one from below arrives (its hole).
            bool Busy(int side) => stairs.Any(s => (s.FromFloor == f || s.FromFloor == f - 1) && SideOf(s.FromFloor) == side);
            var mx0 = Busy(-1) ? x0 + sw : x0;
            var mx1 = Busy(1) ? x1 - sw : x1;
            var open = openGround && f == 0;
            var columns = open ? 1 : Math.Max(1, (int)MathF.Round((mx1 - mx0) / 6f));
            var cuts = new List<float>();
            for (var c = 1; c < columns; c++)
            {
                var x = mx0 + (mx1 - mx0) * c / columns;
                if (f == 0 && MathF.Abs(x - doorLx) < 1.4f) continue; // never a wall into the front door
                cuts.Add(x);
            }
            var edges = new List<float> { mx0 };
            edges.AddRange(cuts);
            edges.Add(mx1);
            var cross = !open && zF - zB >= 6.5f;
            var zc = (zF + zB) / 2;

            for (var c = 0; c + 1 < edges.Count; c++)
            {
                var (a, b) = (edges[c], edges[c + 1]);
                // A free side strip belongs to the outermost rooms.
                var ra = c == 0 ? x0 : a;
                var rb = c + 2 == edges.Count ? x1 : b;
                if (c == 0 && Busy(-1)) ra = a;
                if (c + 2 == edges.Count && Busy(1)) rb = b;
                var zones = cross ? new[] { (zc, zF), (zB, zc) } : new[] { (zB, zF) };
                foreach (var (za, zz) in zones)
                {
                    rooms.Add(new Room(f, R(ra, za, rb, zz)));
                    var (mx, mz) = ((a + b) / 2, (za + zz) / 2);
                    spots.Add(new Place(W(mx, mz).X, W(mx, mz).Z, f));
                    if (b - a > 4.5f)
                        foreach (var dx in new[] { -(b - a) / 4, (b - a) / 4 })
                        {
                            var (sx, sz) = W(mx + dx, mz - (zz - za) / 4);
                            spots.Add(new Place(sx, sz, f));
                        }
                }
            }
            if (cross)
                partitions.Add(new Partition(f, W(mx0, zc), W(mx1, zc),
                    Enumerable.Range(0, edges.Count - 1).Select(c => W((edges[c] + edges[c + 1]) / 2, zc)).ToList()));
            foreach (var x in cuts)
            {
                var gaps = cross ? new[] { W(x, (zc + zF) / 2), W(x, (zB + zc) / 2) } : new[] { W(x, (zB + zF) / 2) };
                partitions.Add(new Partition(f, W(x, zF), W(x, zB), gaps));
            }
        }
        return new BuildingInterior(reachable, rooms, partitions, stairs, spots);
    }

    // ---- routes ----

    private static bool InRoom(Room r, Place p, float slack = 0.25f) =>
        r.Floor == p.Floor && p.X >= r.Rect.MinX - slack && p.X <= r.Rect.MaxX + slack
        && p.Z >= r.Rect.MinZ - slack && p.Z <= r.Rect.MaxZ + slack;

    private List<int> RoomsOf(Place p)
    {
        var list = new List<int>();
        for (var i = 0; i < Rooms.Count; i++)
            if (InRoom(Rooms[i], p)) list.Add(i);
        if (list.Count == 0)
        {
            // Not in a room (in a stairwell strip, or standing in a doorway): use the nearest room on that floor.
            var best = -1;
            var bestD = float.MaxValue;
            for (var i = 0; i < Rooms.Count; i++)
            {
                if (Rooms[i].Floor != p.Floor) continue;
                var r = Rooms[i].Rect;
                var dx = MathF.Max(0, MathF.Max(r.MinX - p.X, p.X - r.MaxX));
                var dz = MathF.Max(0, MathF.Max(r.MinZ - p.Z, p.Z - r.MaxZ));
                if (dx * dx + dz * dz < bestD) (best, bestD) = (i, dx * dx + dz * dz);
            }
            if (best >= 0) list.Add(best);
        }
        return list;
    }

    private void BuildGraph()
    {
        foreach (var part in Partitions)
            foreach (var (gx, gz) in part.Gaps)
            {
                var at = new Place(gx, gz, part.Floor);
                _nodes.Add((at, RoomsOf(at)));
            }
        foreach (var s in Stairs)
        {
            var ba = new Place(s.BottomApproach.X, s.BottomApproach.Z, s.FromFloor);
            var ta = new Place(s.TopApproach.X, s.TopApproach.Z, s.FromFloor + 1);
            var i = _nodes.Count;
            _nodes.Add((ba, RoomsOf(ba)));
            _nodes.Add((new Place(s.Bottom.X, s.Bottom.Z, s.FromFloor), []));
            _nodes.Add((new Place(s.Top.X, s.Top.Z, s.FromFloor + 1), []));
            _nodes.Add((ta, RoomsOf(ta)));
            _links.Add((i, i + 1));
            _links.Add((i + 1, i + 2));
            _links.Add((i + 2, i + 3));
        }
    }

    /// <summary>
    /// The points to walk through, inside, from one place to another (the last is <paramref name="end"/>): through
    /// doorways, and up or down the stairs (the leg between a stair's bottom and top is the flight itself).
    /// </summary>
    public List<Place> Route(Place start, Place end)
    {
        if (start.Floor >= Floors) start = start with { Floor = Floors - 1 };
        if (end.Floor >= Floors) end = end with { Floor = Floors - 1 };
        var fromRooms = RoomsOf(start);
        var toRooms = RoomsOf(end);
        if (fromRooms.Intersect(toRooms).Any()) return [end];

        // Dijkstra over the doorway and stair nodes, plus the two ends (index n: start, n + 1: end).
        var n = _nodes.Count;
        Place At(int i) => i < n ? _nodes[i].At : i == n ? start : end;
        List<int> Rms(int i) => i < n ? _nodes[i].Rooms : i == n ? fromRooms : toRooms;
        var dist = Enumerable.Repeat(float.MaxValue, n + 2).ToArray();
        var prev = Enumerable.Repeat(-1, n + 2).ToArray();
        var done = new bool[n + 2];
        dist[n] = 0;
        for (var round = 0; round < n + 2; round++)
        {
            var u = -1;
            for (var i = 0; i < n + 2; i++)
                if (!done[i] && dist[i] < float.MaxValue && (u < 0 || dist[i] < dist[u])) u = i;
            if (u < 0 || u == n + 1) break;
            done[u] = true;
            void Relax(int v)
            {
                var d = dist[u] + At(u).DistanceTo(At(v));
                if (d < dist[v]) (dist[v], prev[v]) = (d, u);
            }
            var ru = Rms(u);
            for (var v = 0; v < n + 2; v++)
                if (v != u && !done[v] && ru.Count > 0 && Rms(v).Intersect(ru).Any()) Relax(v);
            foreach (var (a, b) in _links)
            {
                if (a == u && !done[b]) Relax(b);
                if (b == u && !done[a]) Relax(a);
            }
        }
        if (prev[n + 1] < 0) return [end]; // unreachable (shouldn't happen): go straight
        var path = new List<Place>();
        for (var i = n + 1; i != n; i = prev[i]) path.Add(At(i));
        path.Reverse();
        return path;
    }
}
