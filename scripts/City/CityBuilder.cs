using System.Collections.Generic;
using ExploreTogether.Core.City;
using ExploreTogether.Dungeon;
using Godot;
using Sides = ExploreTogether.Core.Dungeon.Sides;

namespace ExploreTogether.City;

/// <summary>
/// Builds a greybox city from a <see cref="CityLayout"/>, identically on server (for physics) and client.
///
/// All visible geometry is boxes in one vertex-colored mesh (one draw call), and every solid box also gets a
/// BoxShape3D on one StaticBody3D. Streets, sidewalks and yards are flat colored plates on top of one ground
/// collider, so the ground is perfectly flat to walk on.
///
/// - Fences (0.9 m, low enough to hop: the jump reaches about 1 m) run along every parcel edge. A parcel's
///   street edge has a gate gap in front of its door.
/// - Buildings are hollow: walls with a doorway, a ceiling over the ground floor, a roof, and windows.
/// - A 4 m wall surrounds the whole city.
/// </summary>
public static class CityBuilder
{
    public const string GateId = "city_gate";
    private const string GateScenePath = "res://scenes/props/CityGate.tscn";

    public const float FenceHeight = 0.9f;
    private const float FenceThickness = 0.12f;
    private const float GateGap = 2.4f;
    private const float WallThickness = 0.3f;
    public const float FloorHeight = 3.2f;
    private const float DoorWidth = 1.6f;
    private const float DoorHeight = 2.4f;
    private const float BoundaryHeight = 4f;

    private static readonly Color Grass = new(0.32f, 0.45f, 0.24f);
    private static readonly Color Road = new(0.34f, 0.34f, 0.36f);
    private static readonly Color Sidewalk = new(0.68f, 0.66f, 0.62f);
    private static readonly Color Fence = new(0.62f, 0.46f, 0.30f);
    private static readonly Color Stone = new(0.42f, 0.40f, 0.38f);
    private static readonly Color Window = new(0.12f, 0.16f, 0.22f);

    /// <summary>Wall and roof colors per building type.</summary>
    private static (Color Wall, Color Roof) Colors(BuildingType type) => type switch
    {
        BuildingType.Cottage => (new Color(0.86f, 0.80f, 0.66f), new Color(0.45f, 0.22f, 0.16f)),
        BuildingType.House => (new Color(0.80f, 0.74f, 0.62f), new Color(0.35f, 0.20f, 0.15f)),
        BuildingType.RowHouse => (new Color(0.62f, 0.36f, 0.28f), new Color(0.25f, 0.22f, 0.22f)),
        BuildingType.Manor => (new Color(0.90f, 0.88f, 0.82f), new Color(0.22f, 0.26f, 0.32f)),
        BuildingType.Shop => (new Color(0.55f, 0.70f, 0.78f), new Color(0.60f, 0.30f, 0.12f)),
        BuildingType.Office => (new Color(0.62f, 0.64f, 0.68f), new Color(0.30f, 0.31f, 0.33f)),
        BuildingType.Warehouse => (new Color(0.50f, 0.46f, 0.40f), new Color(0.35f, 0.33f, 0.30f)),
        BuildingType.Guardhouse => (new Color(0.45f, 0.45f, 0.50f), new Color(0.55f, 0.12f, 0.12f)),
        _ => (new Color(0.7f, 0.7f, 0.7f), new Color(0.3f, 0.3f, 0.3f)),
    };

    public static Node3D Build(CityLayout city)
    {
        var level = new Node3D { Name = "City" };
        level.AddChild(new WorldEnvironment { Name = "Environment", Environment = CreateEnvironment() });
        level.AddChild(new DirectionalLight3D
        {
            Name = "Sun",
            ShadowEnabled = true,
            DirectionalShadowMaxDistance = 150,
            LightEnergy = 1.3f,
            RotationDegrees = new Vector3(-58, 35, 0),
        });

        var b = new Boxes(level);
        var bounds = city.Bounds;

        // Ground: one collider for the whole city (top at y = 0), drawn as grass, plus open fields beyond the wall.
        b.Add(new Vector3(bounds.MinX - 2, -1, bounds.MinZ - 2), new Vector3(bounds.MaxX + 2, 0, bounds.MaxZ + 2), Grass);
        b.Plate(bounds.MinX - 1000, bounds.MinZ - 1000, bounds.MaxX + 1000, bounds.MaxZ + 1000, -0.01f, Grass.Darkened(0.15f));

        for (var x = 0; x < city.CellsX; x++)
            for (var z = 0; z < city.CellsZ; z++)
            {
                var cell = new Cell(x, z);
                if (city.IsStreet(cell)) AddStreet(b, city, cell);
            }

        foreach (var parcel in city.Parcels)
        {
            AddYard(b, parcel);
            AddFences(b, city, parcel);
            AddBuilding(b, parcel);
            level.AddChild(Sign(parcel));
        }

        // The city wall, just outside the outermost fences.
        const float t = 1f;
        b.Add(new Vector3(bounds.MinX - t, 0, bounds.MinZ - t), new Vector3(bounds.MaxX + t, BoundaryHeight, bounds.MinZ), Stone);
        b.Add(new Vector3(bounds.MinX - t, 0, bounds.MaxZ), new Vector3(bounds.MaxX + t, BoundaryHeight, bounds.MaxZ + t), Stone);
        b.Add(new Vector3(bounds.MinX - t, 0, bounds.MinZ), new Vector3(bounds.MinX, BoundaryHeight, bounds.MaxZ), Stone);
        b.Add(new Vector3(bounds.MaxX, 0, bounds.MinZ), new Vector3(bounds.MaxX + t, BoundaryHeight, bounds.MaxZ), Stone);

        b.Finish();

        // The gate back to the hub, in a corner of the start junction (clear of the lots' gates mid-edge).
        var gate = GD.Load<PackedScene>(GateScenePath).Instantiate<Interactable>();
        gate.Name = "CityGate";
        gate.Prompt = "Return to Hub";
        gate.GetNode<Label3D>("Label").Text = "To the Hub";
        gate.Position = new Vector3(-4.2f, 0, -4.2f);
        gate.Rotation = new Vector3(0, Mathf.Pi / 4, 0); // faces the junction's center
        level.AddChild(gate);
        return level;
    }

    /// <summary>Spawn point for the i-th arriving player: around the start junction's center, facing an open street.</summary>
    public static (Vector3 Position, float Yaw) SpawnPoint(CityLayout city, int index)
    {
        // Tile (0, 0) is a corner of the grid, so its streets lead north and/or east.
        var yaw = city.TilesLinkedEast(0, 0) ? -Mathf.Pi / 2 : Mathf.Pi;
        var angle = index * Mathf.Tau / 6f;
        return (new Vector3(1 + Mathf.Cos(angle) * 0.8f, 0.3f, 1 + Mathf.Sin(angle) * 0.8f), yaw);
    }

    // ---- Pieces ----

    private const float Half = CityLayout.CellSize / 2f;

    private static void AddStreet(Boxes b, CityLayout city, Cell cell)
    {
        var (cx, cz) = CityLayout.CellCenter(cell);
        b.Plate(cx - Half, cz - Half, cx + Half, cz + Half, 0.02f, Sidewalk);
        var r = CityLayout.StreetRoadWidth / 2;
        b.Plate(cx - r, cz - r, cx + r, cz + r, 0.03f, Road);
        // Extend the road toward each neighboring street cell.
        foreach (var side in new[] { Sides.North, Sides.East, Sides.South, Sides.West })
        {
            var (dx, dz) = Core.Dungeon.MazePieces.Vector(side);
            if (!city.IsStreet(new Cell(cell.X + dx, cell.Z + dz))) continue;
            if (dx != 0) b.Plate(dx > 0 ? cx + r : cx - Half, cz - r, dx > 0 ? cx + Half : cx - r, cz + r, 0.03f, Road);
            else b.Plate(cx - r, dz > 0 ? cz + r : cz - Half, cx + r, dz > 0 ? cz + Half : cz - r, 0.03f, Road);
        }
    }

    private static void AddYard(Boxes b, Parcel p)
    {
        // A slightly different green per parcel, so properties read apart even from above.
        var shade = ((p.Id * 7919) % 11) / 10f - 0.5f;
        var c = Grass.Lightened(0.08f * shade + 0.04f);
        var r = p.Bounds;
        b.Plate(r.MinX, r.MinZ, r.MaxX, r.MaxZ, 0.01f, c);
    }

    private static void AddFences(Boxes b, CityLayout city, Parcel p)
    {
        foreach (var cell in p.Cells)
        {
            var rect = CityLayout.CellRect(cell);
            foreach (var side in new[] { Sides.North, Sides.East, Sides.South, Sides.West })
            {
                var (dx, dz) = Core.Dungeon.MazePieces.Vector(side);
                var other = new Cell(cell.X + dx, cell.Z + dz);
                if (!city.InBounds(other)) continue; // the city wall
                var neighbor = city.ParcelOf(other);
                if (neighbor == p) continue;
                // A fence between two parcels is shared: build it once, from the lower id.
                if (neighbor != null && neighbor.Id < p.Id) continue;

                // The fence stands just inside the parcel on a street edge, centered on the line between parcels.
                var alongX = side is Sides.North or Sides.South;
                var inset = neighbor == null ? FenceThickness / 2 : 0;
                var line = side switch
                {
                    Sides.North => rect.MaxZ - inset,
                    Sides.South => rect.MinZ + inset,
                    Sides.East => rect.MaxX - inset,
                    _ => rect.MinX + inset,
                };
                var (a0, a1) = alongX ? (rect.MinX, rect.MaxX) : (rect.MinZ, rect.MaxZ);
                float? gap = neighbor == null && cell == p.FrontCell && side == p.Front
                    ? (alongX ? p.Door.X : p.Door.Z)
                    : null;
                Segment(b, alongX, line, a0, a1, 0, FenceHeight, FenceThickness, Fence, gap, GateGap, FenceHeight);
            }
        }
    }

    private static void AddBuilding(Boxes b, Parcel p)
    {
        var f = p.Footprint;
        var (_, _, floors) = CityLayout.Size(p.Building);
        var height = floors * FloorHeight;
        var (wall, roof) = Colors(p.Building);
        const float t = WallThickness;

        // Floor inside (a plate, so the interior reads as a room).
        b.Plate(f.MinX, f.MinZ, f.MaxX, f.MaxZ, 0.04f, new Color(0.40f, 0.30f, 0.22f));

        // Walls: the front one has the doorway.
        float? GapOn(Sides s) => s == p.Front ? (s is Sides.North or Sides.South ? p.Door.X : p.Door.Z) : null;
        Segment(b, true, f.MaxZ - t / 2, f.MinX, f.MaxX, 0, height, t, wall, GapOn(Sides.North), DoorWidth, DoorHeight);
        Segment(b, true, f.MinZ + t / 2, f.MinX, f.MaxX, 0, height, t, wall, GapOn(Sides.South), DoorWidth, DoorHeight);
        Segment(b, false, f.MaxX - t / 2, f.MinZ + t, f.MaxZ - t, 0, height, t, wall, GapOn(Sides.East), DoorWidth, DoorHeight);
        Segment(b, false, f.MinX + t / 2, f.MinZ + t, f.MaxZ - t, 0, height, t, wall, GapOn(Sides.West), DoorWidth, DoorHeight);

        // Ceiling over the ground floor (upper floors are closed for now), and the roof with a small overhang.
        if (floors > 1)
            b.Add(new Vector3(f.MinX + t, FloorHeight - 0.2f, f.MinZ + t), new Vector3(f.MaxX - t, FloorHeight, f.MaxZ - t), wall.Darkened(0.3f));
        const float o = 0.3f;
        b.Add(new Vector3(f.MinX - o, height, f.MinZ - o), new Vector3(f.MaxX + o, height + 0.35f, f.MaxZ + o), roof);
        if (p.Building == BuildingType.Guardhouse) // battlements, so guardhouses stand out
            for (var x = f.MinX; x + 0.8f <= f.MaxX; x += 1.6f)
            {
                b.Add(new Vector3(x, height + 0.35f, f.MinZ - o), new Vector3(x + 0.8f, height + 0.9f, f.MinZ + 0.2f), roof);
                b.Add(new Vector3(x, height + 0.35f, f.MaxZ - 0.2f), new Vector3(x + 0.8f, height + 0.9f, f.MaxZ + o), roof);
            }

        AddWindows(b, p, floors);
    }

    /// <summary>Dark window panes on the front and back faces (visual only), one row per floor, skipping the doorway.</summary>
    private static void AddWindows(Boxes b, Parcel p, int floors)
    {
        var f = p.Footprint;
        var alongX = p.Front is Sides.North or Sides.South;
        var (a0, a1) = alongX ? (f.MinX, f.MaxX) : (f.MinZ, f.MaxZ);
        var door = alongX ? p.Door.X : p.Door.Z;
        var count = Mathf.Max(1, (int)((a1 - a0 - 1) / 2.6f));
        var step = (a1 - a0) / count;
        const float d = 0.04f; // how far a pane stands out of the wall
        foreach (var front in new[] { true, false })
        {
            float face = (p.Front, front) switch
            {
                (Sides.North, true) or (Sides.South, false) => f.MaxZ,
                (Sides.South, true) or (Sides.North, false) => f.MinZ,
                (Sides.East, true) or (Sides.West, false) => f.MaxX,
                _ => f.MinX,
            };
            var outward = (p.Front is Sides.North or Sides.East) == front ? 1 : -1;
            for (var floor = 0; floor < floors; floor++)
                for (var i = 0; i < count; i++)
                {
                    var mid = a0 + step * (i + 0.5f);
                    if (floor == 0 && front && Mathf.Abs(mid - door) < DoorWidth / 2 + 0.8f) continue;
                    var y0 = floor * FloorHeight + 1.0f;
                    var (lo, hi) = (face, face + outward * d);
                    if (lo > hi) (lo, hi) = (hi, lo);
                    var min = alongX ? new Vector3(mid - 0.5f, y0, lo) : new Vector3(lo, y0, mid - 0.5f);
                    var max = alongX ? new Vector3(mid + 0.5f, y0 + 1.2f, hi) : new Vector3(hi, y0 + 1.2f, mid + 0.5f);
                    b.Add(min, max, Window, solid: false);
                }
        }
    }

    /// <summary>
    /// A straight wall or fence along X (at z = line) or along Z (at x = line), from a0 to a1, optionally with a
    /// gap of the given width centered at gapCenter, open from the ground up to gapTop (a lintel fills the rest).
    /// </summary>
    private static void Segment(Boxes b, bool alongX, float line, float a0, float a1, float y0, float y1, float thickness,
        Color color, float? gapCenter, float gapWidth, float gapTop)
    {
        void Piece(float from, float to, float bottom, float top)
        {
            if (to - from < 0.01f || top - bottom < 0.01f) return;
            var h = thickness / 2;
            b.Add(alongX ? new Vector3(from, bottom, line - h) : new Vector3(line - h, bottom, from),
                  alongX ? new Vector3(to, top, line + h) : new Vector3(line + h, top, to), color);
        }
        if (gapCenter is not { } g)
        {
            Piece(a0, a1, y0, y1);
            return;
        }
        var (g0, g1) = (Mathf.Max(a0, g - gapWidth / 2), Mathf.Min(a1, g + gapWidth / 2));
        Piece(a0, g0, y0, y1);
        Piece(g1, a1, y0, y1);
        Piece(g0, g1, gapTop, y1);
    }

    /// <summary>A small sign over the door ("House 14"), facing the street, readable up close.</summary>
    private static Label3D Sign(Parcel p)
    {
        var (dx, dz) = Core.Dungeon.MazePieces.Vector(p.Front);
        return new Label3D
        {
            Name = $"Sign{p.Id}",
            Text = $"{p.Building} {p.Id}",
            FontSize = 40,
            OutlineSize = 8,
            PixelSize = 0.006f,
            Position = new Vector3(p.Door.X + dx * 0.05f, DoorHeight + 0.35f, p.Door.Z + dz * 0.05f),
            // A Label3D reads along +Z: turn it to face away from the building.
            Rotation = new Vector3(0, Mathf.Atan2(dx, dz), 0),
            VisibilityRangeEnd = 30,
        };
    }

    private static Godot.Environment CreateEnvironment() => new()
    {
        BackgroundMode = Godot.Environment.BGMode.Sky,
        Sky = new Sky { SkyMaterial = new ProceduralSkyMaterial() },
        // Greybox: a strong, slightly blue ambient so streets in building shadow stay readable.
        AmbientLightSource = Godot.Environment.AmbientSource.Color,
        AmbientLightColor = new Color(0.70f, 0.75f, 0.85f),
        AmbientLightEnergy = 0.5f,
        ReflectedLightSource = Godot.Environment.ReflectionSource.Sky,
        TonemapMode = Godot.Environment.ToneMapper.Filmic,
    };

    /// <summary>
    /// Collects colored boxes into one mesh, and gives each solid box a collision shape on one static body.
    /// Plates are thin visual-only boxes lying on the ground (streets, yards).
    /// </summary>
    private sealed class Boxes
    {
        private readonly Node3D _parent;
        private readonly SurfaceTool _st = new();
        private readonly StaticBody3D _body = new() { Name = "Collision" };
        private int _shapes;

        public Boxes(Node3D parent)
        {
            _parent = parent;
            _st.Begin(Mesh.PrimitiveType.Triangles);
        }

        public void Plate(float x0, float z0, float x1, float z1, float top, Color color) =>
            Add(new Vector3(x0, top - 0.01f, z0), new Vector3(x1, top, z1), color, solid: false);

        public void Add(Vector3 min, Vector3 max, Color color, bool solid = true)
        {
            _st.SetColor(color);
            Vector3 Corner(int i) => new((i & 1) != 0 ? max.X : min.X, (i & 2) != 0 ? max.Y : min.Y, (i & 4) != 0 ? max.Z : min.Z);
            // Each face: four corners (as bit patterns x|y<<1|z<<2) and its normal.
            Face(Corner(0), Corner(4), Corner(6), Corner(2), Vector3.Left);
            Face(Corner(5), Corner(1), Corner(3), Corner(7), Vector3.Right);
            Face(Corner(1), Corner(0), Corner(2), Corner(3), Vector3.Forward);
            Face(Corner(4), Corner(5), Corner(7), Corner(6), Vector3.Back);
            Face(Corner(2), Corner(6), Corner(7), Corner(3), Vector3.Up);
            Face(Corner(0), Corner(1), Corner(5), Corner(4), Vector3.Down);
            if (!solid) return;
            _body.AddChild(new CollisionShape3D
            {
                Name = $"S{_shapes++}",
                Shape = new BoxShape3D { Size = max - min },
                Position = (min + max) / 2,
            });
        }

        private void Face(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal)
        {
            _st.SetNormal(normal);
            foreach (var v in new[] { a, b, c, a, c, d })
                _st.AddVertex(v);
        }

        public void Finish()
        {
            var material = new StandardMaterial3D
            {
                VertexColorUseAsAlbedo = true,
                Roughness = 0.95f,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            };
            _st.SetMaterial(material);
            _parent.AddChild(new MeshInstance3D { Name = "Geometry", Mesh = _st.Commit() });
            _parent.AddChild(_body);
        }
    }
}
