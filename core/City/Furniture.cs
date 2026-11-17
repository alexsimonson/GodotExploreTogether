namespace ExploreTogether.Core.City;

public enum FurnitureKind { Bed, Table, Chair, Counter, Shelf, Desk, Crate, Bar, Stool, Wardrobe, Sofa }

/// <summary>
/// One piece of furniture in a building, on a floor. <see cref="Rect"/> is its footprint (world, axis-aligned: buildings
/// only ever turn by quarter turns); its front faces (<see cref="FacingX"/>, <see cref="FacingZ"/>), into the room.
/// <see cref="Id"/> is its index in the city's list (the same on server and client).
/// </summary>
public sealed record FurniturePiece(int Id, int Parcel, FurnitureKind Kind, int Floor, GroundRect Rect, float FacingX, float FacingZ)
{
    public float Y => Floor * BuildingInterior.FloorHeight;

    /// <summary>Chairs and stools can be picked up (and swung at someone: they break on the first hit).</summary>
    public bool Pickable => Kind is FurnitureKind.Chair or FurnitureKind.Stool;

    public float Height => Furniture.HeightOf(Kind);
}

/// <summary>
/// Furnishes the greybox buildings: beds and wardrobes upstairs in homes, a sofa and a dining table downstairs, a
/// counter and shelves in shops, desks in offices, crates in warehouses, a bar, stools and tables in taverns...
/// Custom building designs bring their own interiors and get none.
///
/// Placement keeps out of everyone's way. Inside a room, residents only ever walk straight lines between its doorways,
/// stair ends, the front door and the spots where they stand, so no piece may come within
/// <see cref="PathClear"/> of any of those lines. Pieces stand against the walls, and also keep clear of doorways,
/// the front door and the stairs. Chairs go in front of their table or desk, under the same rules. A bed that
/// doesn't fit head to the wall is tried lying along it.
/// </summary>
public static class Furniture
{
    /// <summary>Clearances (m): around doorway gaps, the front door, stair ends; around standing spots.</summary>
    public const float DoorwayClear = 0.9f, FrontDoorClear = 1.3f, StairClear = 0.9f, SpotClear = 0.55f, WallGap = 0.05f,
        PathClear = 0.45f;

    public static float HeightOf(FurnitureKind kind) => kind switch
    {
        FurnitureKind.Bed => 0.55f,
        FurnitureKind.Table => 0.75f,
        FurnitureKind.Chair => 0.95f,
        FurnitureKind.Counter or FurnitureKind.Bar => 1.05f,
        FurnitureKind.Shelf => 1.9f,
        FurnitureKind.Desk => 0.75f,
        FurnitureKind.Crate => 0.9f,
        FurnitureKind.Stool => 0.75f,
        FurnitureKind.Wardrobe => 2.0f,
        FurnitureKind.Sofa => 0.85f,
        _ => 1f,
    };

    /// <summary>A piece to place: its kind and size (along the wall, out from it), and seats to set in front of it.</summary>
    private readonly record struct Want(FurnitureKind Kind, float Along, float Out, int Seats = 0, FurnitureKind Seat = FurnitureKind.Chair);

    private static Want Bed => new(FurnitureKind.Bed, 1.0f, 2.0f);
    private static Want Wardrobe => new(FurnitureKind.Wardrobe, 1.0f, 0.6f);
    private static Want Sofa => new(FurnitureKind.Sofa, 1.8f, 0.8f);
    private static Want Shelf(float along = 1.0f) => new(FurnitureKind.Shelf, along, 0.4f);
    private static Want Table(int seats = 2) => new(FurnitureKind.Table, 1.2f, 0.8f, seats);
    private static Want Counter(float along = 1.8f) => new(FurnitureKind.Counter, along, 0.6f);
    private static Want Desk => new(FurnitureKind.Desk, 1.4f, 0.7f, 1);
    private static Want Crate => new(FurnitureKind.Crate, 1.0f, 1.0f);
    private static Want Chair => new(FurnitureKind.Chair, 0.5f, 0.5f);
    private static Want Bar => new(FurnitureKind.Bar, 3.0f, 0.7f, 3, FurnitureKind.Stool);

    /// <summary>What goes in a room: by building, floor, and whether it's the room the front door opens into.</summary>
    private static Want[] Recipe(BuildingType type, int floor, int floors, bool entrance, int roomIndex)
    {
        var home = CityPopulation.HomeCapacity(type) > 0;
        if (home)
        {
            var bedroom = floor > 0 || floors == 1 && !entrance;
            if (bedroom) return roomIndex % 2 == 0 ? [Bed, Wardrobe, Chair] : [Bed, Shelf(), Chair];
            return entrance ? [Sofa, Table(), Shelf()] : [Counter(), Table(), Shelf(0.8f)];
        }
        return type switch
        {
            BuildingType.Shop => [Counter(2.0f), Shelf(1.4f), Shelf(1.4f), Shelf(1.4f)],
            BuildingType.Office => [Desk, Desk, Shelf()],
            BuildingType.Warehouse when floor == 0 => [Crate, Crate, Crate, Shelf(2.0f), Crate, Crate, Shelf(2.0f), Crate],
            BuildingType.Warehouse => [Desk, Crate, Crate],
            BuildingType.Guardhouse when floor == 0 => [Desk, Shelf(), Table()],
            BuildingType.Guardhouse => [Bed, Bed, Wardrobe],
            BuildingType.Tavern when floor == 0 => [Bar, Table(), Table(), Table(), Shelf(1.4f)],
            BuildingType.Tavern => [Bed, Wardrobe, Chair],
            BuildingType.PowerStation => [Desk, Crate, Shelf(2.0f), Crate, Crate],
            _ => [],
        };
    }

    /// <summary>Every piece in the city, in a fixed order (ids are indices).</summary>
    public static List<FurniturePiece> Generate(CityLayout city)
    {
        var all = new List<FurniturePiece>();
        foreach (var p in city.Parcels)
            if (p.Variant.IsGreybox && p.Interior is { } interior)
                Furnish(p, interior, all);
        return all;
    }

    private static void Furnish(Parcel p, BuildingInterior interior, List<FurniturePiece> all)
    {
        var random = new Random(unchecked(p.Id * 92821 + 7));
        for (var r = 0; r < interior.Rooms.Count; r++)
        {
            var room = interior.Rooms[r];
            var keep = KeepOuts(p, interior, room.Floor);
            var entrance = room.Floor == 0 && Inflate(room.Rect, 0.4f).Contains(p.JustInside.X, p.JustInside.Z);
            var placed = all.Where(f => f.Parcel == p.Id && f.Floor == room.Floor).Select(f => f.Rect).ToList();
            var blocked = interior.Stairs.Where(s => s.FromFloor == room.Floor || s.FromFloor == room.Floor - 1)
                .Select(s => Inflate(s.Rect, 0.3f)).ToList();
            // The lines residents walk in this room: between every pair of its doorways, stair ends and spots.
            var nodes = keep.Where(k => Inflate(room.Rect, 0.35f).Contains(k.X, k.Z)).Select(k => (k.X, k.Z)).ToList();
            var lines = new List<((float X, float Z) A, (float X, float Z) B)>();
            for (var i = 0; i < nodes.Count; i++)
                for (var j = i + 1; j < nodes.Count; j++) lines.Add((nodes[i], nodes[j]));
            var recipe = Recipe(p.Building, room.Floor, interior.Floors, entrance, r);
            // A home with only one room on the ground and no upstairs still needs somewhere to sleep.
            if (entrance && interior.Floors == 1 && CityPopulation.HomeCapacity(p.Building) > 0
                && interior.Rooms.Count(o => o.Floor == 0) == 1)
                recipe = [Bed, .. recipe];
            foreach (var want in recipe)
                if (!TryPlace(p, room, want, keep, lines, placed, blocked, random, all) && want.Kind is FurnitureKind.Bed or FurnitureKind.Sofa)
                    TryPlace(p, room, want with { Along = want.Out, Out = want.Along }, keep, lines, placed, blocked, random, all);
        }
    }

    /// <summary>Points to keep clear on a floor, and how far: doorways, the front door, stair ends, standing spots.</summary>
    private static List<(float X, float Z, float R)> KeepOuts(Parcel p, BuildingInterior interior, int floor)
    {
        var keep = new List<(float, float, float)>();
        foreach (var part in interior.Partitions.Where(w => w.Floor == floor))
            foreach (var (x, z) in part.Gaps) keep.Add((x, z, DoorwayClear));
        foreach (var s in interior.Stairs)
        {
            if (s.FromFloor == floor) { keep.Add((s.BottomApproach.X, s.BottomApproach.Z, StairClear)); keep.Add((s.Bottom.X, s.Bottom.Z, StairClear)); }
            if (s.FromFloor + 1 == floor) { keep.Add((s.TopApproach.X, s.TopApproach.Z, StairClear)); keep.Add((s.Top.X, s.Top.Z, StairClear)); }
        }
        foreach (var spot in interior.Spots.Where(s => s.Floor == floor)) keep.Add((spot.X, spot.Z, SpotClear));
        if (floor == 0)
        {
            keep.Add((p.Door.X, p.Door.Z, FrontDoorClear + 0.4f));
            keep.Add((p.JustInside.X, p.JustInside.Z, FrontDoorClear));
        }
        return keep;
    }

    private static bool TryPlace(Parcel p, Room room, Want want, List<(float X, float Z, float R)> keep,
        List<((float X, float Z) A, (float X, float Z) B)> lines, List<GroundRect> placed,
        List<GroundRect> blocked, Random random, List<FurniturePiece> all)
    {
        const float inset = 0.18f; // off the walls (partitions are centered on the rooms' edges)
        var inner = Inflate(room.Rect, -inset);
        // The four walls, as (direction out of the room toward the wall), tried from a random one.
        var walls = new[] { (0f, -1f), (1f, 0f), (0f, 1f), (-1f, 0f) };
        var first = random.Next(4);
        for (var k = 0; k < 4; k++)
        {
            var (wx, wz) = walls[(first + k) % 4];
            var alongX = wz != 0; // a north or south wall runs along X
            var (lo, hi) = alongX ? (inner.MinX, inner.MaxX) : (inner.MinZ, inner.MaxZ);
            if (hi - lo < want.Along) continue;
            var wallLine = wx > 0 ? inner.MaxX : wx < 0 ? inner.MinX : wz > 0 ? inner.MaxZ : inner.MinZ;
            var steps = (int)((hi - lo - want.Along) / 0.25f) + 1;
            var start = random.Next(steps);
            for (var i = 0; i < steps; i++)
            {
                var a = lo + ((start + i) % steps) * 0.25f;
                var rect = Against(alongX, wallLine, wx + wz, a, a + want.Along, want.Out);
                if (!Fits(rect, inner, keep, placed, blocked) || Crosses(rect, lines)) continue;
                // Seats in front (into the room), spread along it; all of them must fit, or try elsewhere.
                var seats = new List<GroundRect>();
                for (var s = 0; s < want.Seats; s++)
                {
                    var mid = a + want.Along * (s + 1) / (want.Seats + 1);
                    var front = wallLine - (wx + wz) * (want.Out + 0.05f); // the piece's front edge, toward the room
                    var seat = Against(alongX, front, wx + wz, mid - 0.24f, mid + 0.24f, 0.48f);
                    if (Fits(seat, inner, keep, placed.Concat(seats).Append(rect).ToList(), blocked) && !Crosses(seat, lines)) seats.Add(seat);
                }
                if (seats.Count < want.Seats) continue;
                Add(p, want.Kind, room.Floor, rect, -wx, -wz, placed, all);
                foreach (var seat in seats) Add(p, want.Seat, room.Floor, seat, wx, wz, placed, all); // facing the table
                return true;
            }
        }
        return false;
    }

    /// <summary>Does any walking line pass within <see cref="PathClear"/> of the rectangle?</summary>
    private static bool Crosses(GroundRect r, List<((float X, float Z) A, (float X, float Z) B)> lines)
    {
        var grown = Inflate(r, PathClear);
        foreach (var (a, b) in lines)
            if (SegmentHits(grown, a.X, a.Z, b.X, b.Z)) return true;
        return false;
    }

    /// <summary>Does the segment from (ax, az) to (bx, bz) touch the rectangle (slab test)?</summary>
    public static bool SegmentHits(GroundRect r, float ax, float az, float bx, float bz)
    {
        float t0 = 0, t1 = 1;
        bool Clip(float p, float q)
        {
            if (MathF.Abs(p) < 1e-7f) return q >= 0;
            var t = q / p;
            if (p < 0) { if (t > t1) return false; if (t > t0) t0 = t; }
            else { if (t < t0) return false; if (t < t1) t1 = t; }
            return true;
        }
        float dx = bx - ax, dz = bz - az;
        return Clip(-dx, ax - r.MinX) && Clip(dx, r.MaxX - ax) && Clip(-dz, az - r.MinZ) && Clip(dz, r.MaxZ - az);
    }

    private static void Add(Parcel p, FurnitureKind kind, int floor, GroundRect rect, float fx, float fz, List<GroundRect> placed,
        List<FurniturePiece> all)
    {
        all.Add(new FurniturePiece(all.Count, p.Id, kind, floor, rect, fx, fz));
        placed.Add(rect);
    }

    /// <summary>A rectangle with its back on the wall line (sign: which side the wall is), spanning a0..a1 along it.</summary>
    private static GroundRect Against(bool alongX, float wallLine, float sign, float a0, float a1, float depth)
    {
        var (back, front) = sign > 0 ? (wallLine, wallLine - depth) : (wallLine, wallLine + depth);
        var (d0, d1) = (MathF.Min(back, front), MathF.Max(back, front));
        return alongX ? new GroundRect(a0, d0, a1, d1) : new GroundRect(d0, a0, d1, a1);
    }

    private static bool Fits(GroundRect r, GroundRect inner, List<(float X, float Z, float R)> keep, List<GroundRect> placed,
        List<GroundRect> blocked)
    {
        if (r.MinX < inner.MinX - 1e-3f || r.MaxX > inner.MaxX + 1e-3f || r.MinZ < inner.MinZ - 1e-3f || r.MaxZ > inner.MaxZ + 1e-3f)
            return false;
        foreach (var (x, z, radius) in keep)
            if (DistanceTo(r, x, z) < radius) return false;
        var padded = Inflate(r, WallGap);
        return !placed.Any(o => Overlaps(padded, o)) && !blocked.Any(o => Overlaps(r, o));
    }

    public static float DistanceTo(GroundRect r, float x, float z)
    {
        var dx = MathF.Max(0, MathF.Max(r.MinX - x, x - r.MaxX));
        var dz = MathF.Max(0, MathF.Max(r.MinZ - z, z - r.MaxZ));
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    public static bool Overlaps(GroundRect a, GroundRect b) =>
        a.MinX < b.MaxX && b.MinX < a.MaxX && a.MinZ < b.MaxZ && b.MinZ < a.MaxZ;

    private static GroundRect Inflate(GroundRect r, float by) => new(r.MinX - by, r.MinZ - by, r.MaxX + by, r.MaxZ + by);
}
