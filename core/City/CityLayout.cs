using ExploreTogether.Core.Dungeon;

namespace ExploreTogether.Core.City;

public enum BuildingType
{
    Cottage,
    House,
    RowHouse,
    Manor,
    Shop,
    Office,
    Warehouse,
    Guardhouse,
    /// <summary>Public in the evening and at night (see <see cref="Stealth.IsOpen"/>): where people gather after dark.</summary>
    Tavern,
    /// <summary>The city's power station (one per city): the main breaker inside feeds every district (see <see cref="CityPower"/>).</summary>
    PowerStation,
}

public enum Zone
{
    Residential,
    Commercial,
}

/// <summary>A 12 m city cell. Cell (1, 1) is the center of tile (0, 0), at world (0, 0).</summary>
public readonly record struct Cell(int X, int Z);

/// <summary>An axis-aligned rectangle on the ground, in world meters.</summary>
public readonly record struct GroundRect(float MinX, float MinZ, float MaxX, float MaxZ)
{
    public float Width => MaxX - MinX;
    public float Depth => MaxZ - MinZ;
    public float CenterX => (MinX + MaxX) / 2;
    public float CenterZ => (MinZ + MaxZ) / 2;
    public bool Contains(float x, float z) => x >= MinX && x < MaxX && z >= MinZ && z < MaxZ;
}

/// <summary>
/// One property: 1 to 3 cells in a line, fenced off from its neighbors, with one building facing the street.
/// Everything inside <see cref="Bounds"/> (building and yard) belongs to it: entering it is what trespass means.
/// </summary>
public sealed class Parcel
{
    public required int Id { get; init; }
    public required IReadOnlyList<Cell> Cells { get; init; }
    public required Zone Zone { get; init; }
    /// <summary>The cell whose street side holds the building's door.</summary>
    public required Cell FrontCell { get; init; }
    /// <summary>The direction from the parcel to its street.</summary>
    public required Sides Front { get; init; }
    public BuildingType Building { get; internal set; }
    /// <summary>The building design placed here (a greybox one, or a custom building scene).</summary>
    public BuildingVariant Variant { get; internal set; } = null!;
    public required GroundRect Bounds { get; init; }
    public GroundRect Footprint { get; internal set; }
    /// <summary>The middle of the doorway, on the building's street face.</summary>
    public (float X, float Z) Door { get; internal set; }
    /// <summary>The middle of the building's front edge: the origin of the building's local coordinates.</summary>
    public (float X, float Z) Origin { get; internal set; }
    /// <summary>Spots on the building's ground floor where people stand (world x, z).</summary>
    public IReadOnlyList<(float X, float Z)> Spots { get; internal set; } = [];
    /// <summary>Spots on every floor people can reach (the ground floor's are <see cref="Spots"/>).</summary>
    public IReadOnlyList<Place> Places { get; internal set; } = [];
    /// <summary>Rooms, stairs and routes inside (greybox designs); null for custom building scenes (one floor, open).</summary>
    public BuildingInterior? Interior { get; internal set; }

    /// <summary>The way between two places inside the building (doorways, stairs); straight there without an interior.</summary>
    public List<Place> RouteInside(Place from, Place to) => Interior?.Route(from, to) ?? [to with { Floor = 0 }];

    /// <summary>The point just inside the front door (ground floor).</summary>
    public Place JustInside
    {
        get
        {
            var way = CityPaths.WayIn(this, Door);
            return new Place(way[2].X, way[2].Z);
        }
    }
    /// <summary>Sides with an alley along them: the parcel's fence there stands back from the cell edge.</summary>
    public Sides AlleySides { get; internal set; }

    /// <summary>The building's rotation about +Y in radians: its local +Z (front) turned toward the street.</summary>
    public float Yaw => Front switch
    {
        Sides.North => 0,
        Sides.East => MathF.PI / 2,
        Sides.South => MathF.PI,
        _ => -MathF.PI / 2,
    };

    /// <summary>A world point in the building's local coordinates (the inverse of <see cref="ToWorld"/>); z &lt; 0 is inside the front wall.</summary>
    public (float X, float Z) ToLocal(float x, float z)
    {
        var (ox, oz) = MazePieces.Vector(Front);
        var (ax, az) = (oz, -ox);
        float dx = x - Origin.X, dz = z - Origin.Z;
        return (dx * ax + dz * az, dx * ox + dz * oz);
    }

    /// <summary>A point in the building's local coordinates (see <see cref="BuildingVariant"/>) in world space.</summary>
    public (float X, float Z) ToWorld(float localX, float localZ)
    {
        var (ox, oz) = MazePieces.Vector(Front); // local +Z
        var (ax, az) = (oz, -ox);                // local +X
        return (Origin.X + ax * localX + ox * localZ, Origin.Z + az * localX + oz * localZ);
    }

    /// <summary>Open to anyone while it's open for business (see <see cref="Stealth"/> for opening hours).</summary>
    public bool IsPublic => Building is BuildingType.Shop or BuildingType.Tavern;
}

/// <summary>
/// A narrow, unlit passage between two neighboring lots (dense, commercial areas only): public ground, but
/// nobody walks there and no street lamp lights it, so it's where people go to not be seen.
/// <see cref="Mouths"/> are points on the street just outside each open end.
/// </summary>
public sealed record Alley(GroundRect Rect, IReadOnlyList<(float X, float Z)> Mouths);

/// <summary>
/// A city made from the original maze: each maze node is a 36 m tile of 3×3 cells (12 m). The tile's
/// center cell is street, and so is each side cell that the maze connects (exactly the dungeon's corridors,
/// scaled up). Every other cell is a lot. Lots are grouped into fenced parcels that each face a street.
///
/// The street network can get a few extra connections (<see cref="LoopPercent"/>) so it isn't a perfect maze.
/// Everything comes from (width, height, seed, loopPercent), so a seed recreates the city, like dungeons.
/// </summary>
public sealed class CityLayout
{
    public const int CellSize = 12;
    public const int TileCells = 3;
    public const int TileSize = CellSize * TileCells;
    public const float StreetRoadWidth = 8;
    public const float FrontSetback = 1.5f;
    private const int FormatVersion = 3; // 3: alleys

    public int Width { get; }
    public int Height { get; }
    public int Seed { get; }
    public int LoopPercent { get; }
    public BuildingCatalog Catalog { get; }
    /// <summary>Dev: a variant placed wherever it fits (index into the catalog), or -1.</summary>
    public int FavoredVariant { get; }
    public MazeLayout Maze { get; }
    public int CellsX => Width * TileCells;
    public int CellsZ => Height * TileCells;
    public IReadOnlyList<Parcel> Parcels => parcels;
    public IReadOnlyList<Alley> Alleys => alleys;

    /// <summary>Alleys are this wide (m), taken half from each lot.</summary>
    public const float AlleyWidth = 2.6f;

    private readonly bool[,] street;   // [cx, cz]
    private readonly int[,] parcelAt;  // [cx, cz], -1 for street
    private readonly bool[,] linkEast; // [tx, tz]: tile connects to tile tx + 1
    private readonly bool[,] linkNorth; // [tx, tz]: tile connects to tile tz + 1
    private readonly List<Parcel> parcels = new();
    private readonly List<Alley> alleys = new();
    private readonly Random random;

    private CityLayout(int width, int height, int seed, int loopPercent, BuildingCatalog catalog, int favoredVariant)
    {
        if (width < 1 || height < 1) throw new ArgumentException("City must be at least 1×1.");
        Width = width;
        Height = height;
        Seed = seed;
        LoopPercent = Math.Clamp(loopPercent, 0, 100);
        Catalog = catalog;
        FavoredVariant = favoredVariant >= 0 && favoredVariant < catalog.Variants.Count ? favoredVariant : -1;
        // Length = every tile: the backtracker then visits the whole grid (at most length + 1 nodes).
        Maze = MazeLayout.Generate(width, height, width * height, seed);
        random = new Random(unchecked(seed * 486187739 + 1013904223));
        street = new bool[CellsX, CellsZ];
        parcelAt = new int[CellsX, CellsZ];
        linkEast = new bool[width, height];
        linkNorth = new bool[width, height];

        ReadMazeLinks();
        AddLoops();
        MarkStreets();
        BuildParcels();
        AssignBuildings();
        PlaceGuardhouses();
        PlaceTaverns();
        PlacePowerStation();
    }

    /// <param name="catalog">The building designs to use (default: greybox only).</param>
    /// <param name="favoredVariant">Dev: a variant name to place wherever it fits, to try out a new building.</param>
    public static CityLayout Generate(int width, int height, int seed, int loopPercent = 15,
        BuildingCatalog? catalog = null, string? favoredVariant = null)
    {
        catalog ??= BuildingCatalog.Greybox;
        var favored = favoredVariant == null ? -1 : catalog.IndexOf(favoredVariant);
        if (favoredVariant != null && favored < 0)
            throw new ArgumentException($"No building variant named '{favoredVariant}'.");
        return new CityLayout(width, height, seed, loopPercent, catalog, favored);
    }

    /// <summary>Everything a client needs to rebuild the same city (with the same building catalog).</summary>
    public int[] Export() => [FormatVersion, Width, Height, Seed, LoopPercent, Catalog.Checksum, FavoredVariant];

    /// <exception cref="ArgumentException">The data is malformed, or was made with a different building catalog.</exception>
    public static CityLayout Import(int[] data, BuildingCatalog? catalog = null)
    {
        catalog ??= BuildingCatalog.Greybox;
        if (data.Length != 7 || data[0] != FormatVersion) throw new ArgumentException("Unsupported city data.");
        if (data[5] != catalog.Checksum)
            throw new ArgumentException("The city was made with different building designs (server and client building scenes differ).");
        return new CityLayout(data[1], data[2], data[3], data[4], catalog, data[6]);
    }

    // ---- Queries ----

    public bool InBounds(Cell c) => c.X >= 0 && c.Z >= 0 && c.X < CellsX && c.Z < CellsZ;
    public bool IsStreet(Cell c) => InBounds(c) && street[c.X, c.Z];
    public bool TilesLinkedEast(int tx, int tz) => linkEast[tx, tz];
    public bool TilesLinkedNorth(int tx, int tz) => linkNorth[tx, tz];

    public Parcel? ParcelOf(Cell c) => InBounds(c) && parcelAt[c.X, c.Z] >= 0 ? parcels[parcelAt[c.X, c.Z]] : null;

    /// <summary>The parcel at a world position, or null on a street, in an alley, or outside the city.</summary>
    public Parcel? ParcelAt(float x, float z) => ParcelOf(CellAt(x, z)) is { } p && p.Bounds.Contains(x, z) ? p : null;

    /// <summary>The alley at a world position, if any.</summary>
    public Alley? AlleyAt(float x, float z)
    {
        foreach (var a in alleys)
            if (a.Rect.Contains(x, z)) return a;
        return null;
    }

    public static Cell CellAt(float x, float z) =>
        new((int)MathF.Floor((x + CellSize * 1.5f) / CellSize), (int)MathF.Floor((z + CellSize * 1.5f) / CellSize));

    public static (float X, float Z) CellCenter(Cell c) => ((c.X - 1) * CellSize, (c.Z - 1) * CellSize);

    public static GroundRect CellRect(Cell c)
    {
        var (x, z) = CellCenter(c);
        const float h = CellSize / 2f;
        return new GroundRect(x - h, z - h, x + h, z + h);
    }

    /// <summary>The whole city's ground, edge to edge.</summary>
    public GroundRect Bounds => new(-CellSize * 1.5f, -CellSize * 1.5f,
        (CellsX - 1.5f) * CellSize, (CellsZ - 1.5f) * CellSize);

    // ---- Generation ----

    private void ReadMazeLinks()
    {
        foreach (var n in Maze.Nodes)
        {
            var p = n.mazePosition;
            if (n.eastNeighbor != null) linkEast[p.X, p.Z] = true;
            if (n.northNeighbor != null) linkNorth[p.X, p.Z] = true;
        }
    }

    /// <summary>Opens a few extra streets between neighboring tiles, so the network has some loops.</summary>
    private void AddLoops()
    {
        if (LoopPercent <= 0) return;
        for (var tz = 0; tz < Height; tz++)
            for (var tx = 0; tx < Width; tx++)
            {
                if (tx + 1 < Width && !linkEast[tx, tz] && random.Next(100) < LoopPercent) linkEast[tx, tz] = true;
                if (tz + 1 < Height && !linkNorth[tx, tz] && random.Next(100) < LoopPercent) linkNorth[tx, tz] = true;
            }
    }

    private Sides TileStreets(int tx, int tz) =>
        (tz + 1 < Height && linkNorth[tx, tz] ? Sides.North : 0) |
        (tz > 0 && linkNorth[tx, tz - 1] ? Sides.South : 0) |
        (tx + 1 < Width && linkEast[tx, tz] ? Sides.East : 0) |
        (tx > 0 && linkEast[tx - 1, tz] ? Sides.West : 0);

    private static readonly Sides[] AllSides = [Sides.North, Sides.East, Sides.South, Sides.West];

    private static Cell Step(Cell c, Sides side)
    {
        var (dx, dz) = MazePieces.Vector(side);
        return new Cell(c.X + dx, c.Z + dz);
    }

    private static Cell TileCenter(int tx, int tz) => new(tx * TileCells + 1, tz * TileCells + 1);

    private void MarkStreets()
    {
        for (var tz = 0; tz < Height; tz++)
            for (var tx = 0; tx < Width; tx++)
            {
                var center = TileCenter(tx, tz);
                street[center.X, center.Z] = true;
                var open = TileStreets(tx, tz);
                foreach (var side in AllSides)
                    if ((open & side) != 0)
                    {
                        var arm = Step(center, side);
                        street[arm.X, arm.Z] = true;
                    }
            }
    }

    private Zone ZoneOf(int tx, int tz)
    {
        // Distance from the city center, 0 (middle) to about 1 (edge).
        var dx = (tx - (Width - 1) / 2f) / Math.Max(1f, Width / 2f);
        var dz = (tz - (Height - 1) / 2f) / Math.Max(1f, Height / 2f);
        return MathF.Sqrt(dx * dx + dz * dz) < 0.45f ? Zone.Commercial : Zone.Residential;
    }

    /// <summary>The two corner cells next to a side arm (in fixed order).</summary>
    private static (Cell A, Cell B) CornersBeside(Cell center, Sides side) => side switch
    {
        Sides.North or Sides.South => (Step(Step(center, side), Sides.West), Step(Step(center, side), Sides.East)),
        _ => (Step(Step(center, side), Sides.South), Step(Step(center, side), Sides.North)),
    };

    private void BuildParcels()
    {
        for (var x = 0; x < CellsX; x++)
            for (var z = 0; z < CellsZ; z++)
                parcelAt[x, z] = -1;

        var groups = new List<(List<Cell> Cells, Zone Zone, Cell Center)>();
        int Claim(List<Cell> cells, Zone zone, Cell center)
        {
            groups.Add((cells, zone, center));
            foreach (var c in cells) parcelAt[c.X, c.Z] = groups.Count - 1;
            return groups.Count - 1;
        }
        bool Free(Cell c) => !street[c.X, c.Z] && parcelAt[c.X, c.Z] < 0;

        for (var tz = 0; tz < Height; tz++)
            for (var tx = 0; tx < Width; tx++)
            {
                var center = TileCenter(tx, tz);
                var zone = ZoneOf(tx, tz);
                var open = TileStreets(tx, tz);

                // 1. Wide parcels: a closed side's full strip (corner, side, corner) is 36 × 12 m.
                var stripChance = zone == Zone.Commercial ? 55 : 12;
                foreach (var side in AllSides)
                {
                    if ((open & side) != 0) continue;
                    var arm = Step(center, side);
                    var (a, b) = CornersBeside(center, side);
                    if (Free(arm) && Free(a) && Free(b) && random.Next(100) < stripChance)
                        Claim([a, arm, b], zone, center);
                }

                // 2. A corner with no street beside it (both its arms are closed) joins one of those arms as a back garden.
                foreach (var (sideZ, sideX) in new[] { (Sides.North, Sides.East), (Sides.South, Sides.East), (Sides.South, Sides.West), (Sides.North, Sides.West) })
                {
                    var corner = Step(Step(center, sideZ), sideX);
                    if (!Free(corner) || (open & (sideZ | sideX)) != 0) continue;
                    var side = random.Next(2) == 0 ? sideZ : sideX;
                    var arm = Step(center, side);
                    var existing = parcelAt[arm.X, arm.Z];
                    if (existing >= 0)
                    {
                        groups[existing].Cells.Add(corner);
                        parcelAt[corner.X, corner.Z] = existing;
                    }
                    else
                    {
                        Claim([arm, corner], zone, center);
                    }
                }

                // 3. Every other lot cell is a parcel of its own.
                for (var dz = -1; dz <= 1; dz++)
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var c = new Cell(center.X + dx, center.Z + dz);
                        if (Free(c)) Claim([c], zone, center);
                    }
            }

        var shrink = FindAlleys(groups);
        for (var gi = 0; gi < groups.Count; gi++)
        {
            var (cells, zone, center) = groups[gi];
            // The front: a side of one of its cells that touches a street. A side cell's front faces the tile
            // center (which is always street); prefer it, since it makes wide parcels face along their length.
            var options = new List<(Cell Cell, Sides Side)>();
            foreach (var c in cells)
                foreach (var side in AllSides)
                    if (IsStreet(Step(c, side))) options.Add((c, side));
            var preferred = options.Where(o => Step(o.Cell, o.Side) == center).ToList();
            var (frontCell, front) = preferred.Count > 0 ? preferred[0] : options[random.Next(options.Count)];

            var rect = cells.Select(CellRect).Aggregate((r, s) =>
                new GroundRect(Math.Min(r.MinX, s.MinX), Math.Min(r.MinZ, s.MinZ), Math.Max(r.MaxX, s.MaxX), Math.Max(r.MaxZ, s.MaxZ)));
            var sides = shrink.GetValueOrDefault(gi);
            const float h = AlleyWidth / 2;
            rect = new GroundRect(rect.MinX + ((sides & Sides.West) != 0 ? h : 0), rect.MinZ + ((sides & Sides.South) != 0 ? h : 0),
                rect.MaxX - ((sides & Sides.East) != 0 ? h : 0), rect.MaxZ - ((sides & Sides.North) != 0 ? h : 0));
            parcels.Add(new Parcel
            {
                Id = parcels.Count,
                Cells = cells.OrderBy(c => c.Z).ThenBy(c => c.X).ToList(),
                Zone = zone,
                FrontCell = frontCell,
                Front = front,
                Bounds = rect,
                AlleySides = sides,
            });
        }
    }

    /// <summary>
    /// Alleys: between two neighboring single-lot parcels in the commercial zone (about half the time), when at least
    /// one end of the edge between them opens onto a street. Each lot gives up half the alley's width on that side.
    /// Returns the sides to shrink per group.
    /// </summary>
    private Dictionary<int, Sides> FindAlleys(List<(List<Cell> Cells, Zone Zone, Cell Center)> groups)
    {
        var shrink = new Dictionary<int, Sides>();
        for (var gi = 0; gi < groups.Count; gi++)
        {
            if (groups[gi].Cells.Count != 1 || groups[gi].Zone != Zone.Commercial) continue;
            var c = groups[gi].Cells[0];
            foreach (var side in new[] { Sides.East, Sides.North }) // each pair once
            {
                var n = Step(c, side);
                if (!InBounds(n) || parcelAt[n.X, n.Z] is var gn && (gn < 0 || gn == gi)) continue;
                if (groups[gn].Cells.Count != 1 || groups[gn].Zone != Zone.Commercial) continue;
                // The edge's two ends: along the edge, before and after.
                var (endA, endB) = side == Sides.East ? (Sides.South, Sides.North) : (Sides.West, Sides.East);
                var mouths = new List<(float X, float Z)>();
                var rc = CellRect(c);
                // The edge line, and the alley's span across it.
                var line = side == Sides.East ? rc.MaxX : rc.MaxZ;
                const float h = AlleyWidth / 2;
                foreach (var end in new[] { endA, endB })
                {
                    bool streetC = IsStreet(Step(c, end)), streetN = IsStreet(Step(n, end));
                    if (!streetC && !streetN) continue;
                    // The open part of the mouth: both halves, or only the half beside a street.
                    var lo = streetC ? line - h : line;
                    var hi = streetN ? line + h : line;
                    var mid = (lo + hi) / 2;
                    var (ex, ez) = MazePieces.Vector(end);
                    var edge = side == Sides.East
                        ? (end == Sides.North ? rc.MaxZ : rc.MinZ)
                        : (end == Sides.East ? rc.MaxX : rc.MinX);
                    mouths.Add(side == Sides.East ? (mid, edge + ez * 1.5f) : (edge + ex * 1.5f, mid));
                }
                if (mouths.Count == 0 || random.Next(100) >= 50) continue;
                shrink[gi] = shrink.GetValueOrDefault(gi) | side;
                shrink[gn] = shrink.GetValueOrDefault(gn) | (side == Sides.East ? Sides.West : Sides.South);
                var rect = side == Sides.East
                    ? new GroundRect(line - h, rc.MinZ, line + h, rc.MaxZ)
                    : new GroundRect(rc.MinX, line - h, rc.MaxX, line + h);
                alleys.Add(new Alley(rect, mouths));
            }
        }
        return shrink;
    }

    private void AssignBuildings()
    {
        var favored = FavoredVariant >= 0 ? Catalog.Variants[FavoredVariant] : null;
        foreach (var p in parcels)
        {
            // Dev (--city-favor): try out one design by placing it wherever it fits.
            if (favored != null && Place(p, favored) is { } forced)
            {
                Apply(p, favored, forced);
                continue;
            }
            var size = p.Cells.Count;
            var type = (p.Zone, size) switch
            {
                (Zone.Commercial, >= 3) => Pick((BuildingType.Office, 5), (BuildingType.Warehouse, 3), (BuildingType.Manor, 1)),
                (Zone.Commercial, 2) => Pick((BuildingType.Shop, 3), (BuildingType.Warehouse, 2), (BuildingType.House, 1)),
                (Zone.Commercial, _) => Pick((BuildingType.Shop, 5), (BuildingType.RowHouse, 3), (BuildingType.House, 1)),
                (_, >= 3) => Pick((BuildingType.Manor, 3), (BuildingType.Warehouse, 1)),
                (_, 2) => Pick((BuildingType.House, 3), (BuildingType.Cottage, 2), (BuildingType.Manor, 1)),
                _ => Pick((BuildingType.Cottage, 4), (BuildingType.House, 4), (BuildingType.RowHouse, 2), (BuildingType.Shop, 1)),
            };
            SetBuilding(p, type);
        }
    }

    /// <summary>One guardhouse per ~9 tiles (at least one), spread out across the city.</summary>
    private void PlaceGuardhouses()
    {
        var wanted = Math.Max(1, (int)Math.Round(Width * Height / 9.0));
        var candidates = parcels.Where(p => p.Cells.Count <= 2).OrderBy(_ => random.Next()).ToList();
        if (candidates.Count == 0) candidates = parcels.OrderBy(_ => random.Next()).ToList();
        var chosen = new List<Parcel>();
        // Greedy farthest-point picks: each new guardhouse is the candidate farthest from those already chosen.
        while (chosen.Count < wanted && chosen.Count < candidates.Count)
        {
            var next = chosen.Count == 0
                ? candidates[0]
                : candidates.Where(c => !chosen.Contains(c))
                    .MaxBy(c => chosen.Min(g => Dist2(g, c)))!;
            chosen.Add(next);
        }
        foreach (var p in chosen) SetBuilding(p, BuildingType.Guardhouse);

        static float Dist2(Parcel a, Parcel b)
        {
            float dx = a.Bounds.CenterX - b.Bounds.CenterX, dz = a.Bounds.CenterZ - b.Bounds.CenterZ;
            return dx * dx + dz * dz;
        }
    }

    /// <summary>
    /// The nightlife district: one tavern per ~10 tiles (at least one), the first in the commercial middle of town and
    /// the rest on the parcels nearest to it, so people gather in one lively area at night.
    /// </summary>
    private void PlaceTaverns()
    {
        var wanted = Math.Max(1, (int)Math.Round(Width * Height / 10.0));
        var candidates = parcels.Where(p => p.Cells.Count <= 2 && p.Building != BuildingType.Guardhouse).ToList();
        if (candidates.Count == 0) return;
        var center = candidates.Where(p => p.Zone == Zone.Commercial).OrderBy(_ => random.Next()).FirstOrDefault()
                     ?? candidates[random.Next(candidates.Count)];
        var chosen = candidates.OrderBy(c => Dist2(c, center)).ThenBy(c => c.Id).Take(wanted).ToList();
        foreach (var p in chosen) SetBuilding(p, BuildingType.Tavern);

        static float Dist2(Parcel a, Parcel b)
        {
            float dx = a.Bounds.CenterX - b.Bounds.CenterX, dz = a.Bounds.CenterZ - b.Bounds.CenterZ;
            return dx * dx + dz * dz;
        }
    }

    /// <summary>The power station: on the two-lot parcel farthest from the middle of town (out on the edge).</summary>
    private void PlacePowerStation()
    {
        var b = Bounds;
        var candidates = parcels.Where(p => p.Cells.Count == 2 && p.Building is not (BuildingType.Guardhouse or BuildingType.Tavern)).ToList();
        if (candidates.Count == 0) return;
        var station = candidates.MaxBy(p => MathF.Abs(p.Bounds.CenterX - b.CenterX) + MathF.Abs(p.Bounds.CenterZ - b.CenterZ))!;
        SetBuilding(station, BuildingType.PowerStation);
    }

    private BuildingType Pick(params (BuildingType Type, int Weight)[] options)
    {
        var roll = random.Next(options.Sum(o => o.Weight));
        foreach (var (type, weight) in options)
        {
            if (roll < weight) return type;
            roll -= weight;
        }
        return options[^1].Type;
    }

    /// <summary>The parcel sizes (in 12 m lots) each building type is chosen for.</summary>
    public static IReadOnlyList<int> LotSizesFor(BuildingType type) => type switch
    {
        BuildingType.Cottage or BuildingType.House => [1, 2],
        BuildingType.RowHouse => [1],
        BuildingType.Shop => [1, 2],
        BuildingType.Manor => [2, 3],
        BuildingType.Office => [3],
        BuildingType.Warehouse => [2, 3],
        BuildingType.Guardhouse => [1, 2],
        BuildingType.Tavern => [1, 2],
        BuildingType.PowerStation => [2],
        _ => [1],
    };

    /// <summary>
    /// The parcel sizes (in lots) a design fits on, found by placing it in a few test cities. Empty means it never
    /// fits (too big, or its door can't line up with a gate).
    /// </summary>
    public static IReadOnlyList<int> FitsOnLots(BuildingVariant variant)
    {
        var catalog = BuildingCatalog.WithCustom(variant.IsGreybox ? [] : [variant]);
        var sizes = new SortedSet<int>();
        for (var seed = 0; seed < 4; seed++)
            foreach (var p in Generate(6, 6, seed, 15, catalog, variant.Name).Parcels)
                if (p.Variant.Name == variant.Name) sizes.Add(p.Cells.Count);
        return sizes.ToList();
    }

    /// <summary>Greybox building size: width along the street, depth back from it (meters), and floors.</summary>
    public static (float Width, float Depth, int Floors) Size(BuildingType type) => type switch
    {
        BuildingType.Cottage => (7, 6, 1),
        BuildingType.House => (9, 8, 2),
        BuildingType.RowHouse => (6, 9, 3),
        BuildingType.Manor => (18, 9, 2),
        BuildingType.Shop => (9, 8, 1),
        BuildingType.Office => (26, 9, 3),
        BuildingType.Warehouse => (22, 9, 2),
        BuildingType.Guardhouse => (9, 8, 2),
        BuildingType.Tavern => (10, 9, 2),
        BuildingType.PowerStation => (20, 9, 1),
        _ => (8, 8, 1),
    };

    /// <summary>Picks a design of the type that fits the parcel (weighted), and places it.</summary>
    private void SetBuilding(Parcel p, BuildingType type)
    {
        var options = new List<(BuildingVariant Variant, Placement Placement)>();
        foreach (var v in Catalog.Variants)
            if (v.Type == type && v.Weight > 0 && Place(p, v) is { } placement)
                options.Add((v, placement));
        if (options.Count == 0)
        {
            // Only if the catalog has no greybox design for the type with weight > 0: shrink a stand-in.
            var (w, d, floors) = Size(type);
            var fallback = new BuildingVariant(BuildingCatalog.GreyboxPrefix + type, type, w, d, floors);
            Apply(p, fallback, Place(p, fallback)!.Value);
            return;
        }
        var roll = random.Next(options.Sum(o => o.Variant.Weight));
        foreach (var (variant, placement) in options)
        {
            if (roll < variant.Weight)
            {
                Apply(p, variant, placement);
                return;
            }
            roll -= variant.Weight;
        }
    }

    /// <summary>Where a design goes in a parcel: its size, the middle of its front edge (along the street) and its door.</summary>
    private readonly record struct Placement(float Width, float Depth, float Mid, float DoorAlong);

    /// <summary>Sign of local +X on the street axis (X for north/south fronts, Z for east/west); see <see cref="Parcel.ToWorld"/>.</summary>
    private static int AlongSign(Sides front) => front is Sides.North or Sides.West ? 1 : -1;

    /// <summary>
    /// Fits a design into a parcel, or returns null. The building is set back from the street edge and stays 1 m
    /// inside the fences; its door must be in front of the parcel's front cell (where the fence has its gate).
    /// Greybox designs shrink to fit and put their door wherever that is; custom ones keep their size and door.
    /// </summary>
    private Placement? Place(Parcel p, BuildingVariant v)
    {
        var b = p.Bounds;
        var alongX = p.Front is Sides.North or Sides.South; // the street edge runs along X
        var frontLength = alongX ? b.Width : b.Depth;
        var backLength = alongX ? b.Depth : b.Width;
        var maxW = frontLength - 2;
        var maxD = backLength - FrontSetback - 1;
        var (w, d) = (v.Width, v.Depth);
        if (v.IsGreybox) (w, d) = (Math.Min(w, maxW), Math.Min(d, maxD));
        else if (w > maxW || d > maxD) return null;

        var (fx, fz) = CellCenter(p.FrontCell);
        var cellMid = alongX ? fx : fz;
        var s = AlongSign(p.Front);
        var lo = (alongX ? b.MinX : b.MinZ) + 1 + w / 2;
        var hi = (alongX ? b.MaxX : b.MaxZ) - 1 - w / 2;
        if (v.IsGreybox)
        {
            var mid = Math.Clamp(w > CellSize - 2 ? (alongX ? b.CenterX : b.CenterZ) : cellMid, lo, hi);
            return new Placement(w, d, mid, Math.Clamp(cellMid, mid - w / 2 + 1.5f, mid + w / 2 - 1.5f));
        }
        // Custom: aim the door at the front cell's middle, then keep the building inside the fences.
        var m = Math.Clamp(cellMid - s * v.DoorOffset, lo, hi);
        var door = m + s * v.DoorOffset;
        if (Math.Abs(door - cellMid) > CellSize / 2f - 1.2f) return null; // the door would miss the gate
        return new Placement(w, d, m, door);
    }

    private static void Apply(Parcel p, BuildingVariant v, Placement pl)
    {
        p.Building = v.Type;
        p.Variant = v;
        var b = p.Bounds;
        var (w, d, mid) = (pl.Width, pl.Depth, pl.Mid);
        GroundRect rect;
        switch (p.Front)
        {
            case Sides.North:
                rect = new GroundRect(mid - w / 2, b.MaxZ - FrontSetback - d, mid + w / 2, b.MaxZ - FrontSetback);
                p.Origin = (mid, rect.MaxZ);
                p.Door = (pl.DoorAlong, rect.MaxZ);
                break;
            case Sides.South:
                rect = new GroundRect(mid - w / 2, b.MinZ + FrontSetback, mid + w / 2, b.MinZ + FrontSetback + d);
                p.Origin = (mid, rect.MinZ);
                p.Door = (pl.DoorAlong, rect.MinZ);
                break;
            case Sides.East:
                rect = new GroundRect(b.MaxX - FrontSetback - d, mid - w / 2, b.MaxX - FrontSetback, mid + w / 2);
                p.Origin = (rect.MaxX, mid);
                p.Door = (rect.MaxX, pl.DoorAlong);
                break;
            default:
                rect = new GroundRect(b.MinX + FrontSetback, mid - w / 2, b.MinX + FrontSetback + d, mid + w / 2);
                p.Origin = (rect.MinX, mid);
                p.Door = (rect.MinX, pl.DoorAlong);
                break;
        }
        p.Footprint = rect;

        // Greybox buildings get an interior (rooms, stairs, spots on every floor; see BuildingInterior).
        // Custom designs keep their own spots (or a back row and two side spots), all on the ground floor.
        if (v.IsGreybox)
        {
            p.Interior = BuildingInterior.Generate(p, w, d, v.Floors, openGround: v.Type is BuildingType.Warehouse or BuildingType.Tavern or BuildingType.PowerStation);
            p.Places = p.Interior.Spots;
            p.Spots = p.Places.Where(s => s.Floor == 0).Select(s => (s.X, s.Z)).ToList();
            return;
        }
        IReadOnlyList<(float X, float Z)> local = v.Spots.Count > 0
            ? v.Spots
            : [(-(w / 2 - 1.3f), -(d - 1.3f)), (0, -(d - 1.3f)), (w / 2 - 1.3f, -(d - 1.3f)),
               (-(w / 2 - 1.3f), -d / 2), (w / 2 - 1.3f, -d / 2)];
        p.Interior = null;
        p.Spots = local.Select(l => p.ToWorld(l.X, l.Z)).ToList();
        p.Places = p.Spots.Select(s => new Place(s.X, s.Z)).ToList();
    }
}
