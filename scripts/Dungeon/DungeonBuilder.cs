using System.Collections.Generic;
using ExploreTogether.Core.Dungeon;
using Godot;

namespace ExploreTogether.Dungeon;

/// <summary>
/// Builds the dungeon level from a MazeLayout, identically on server (for physics) and client.
/// Each node instantiates its original piece scene (reconstructed from the Unity prefabs by
/// tools/import_unity_dungeon.py: exact ProBuilder geometry, materials, collision and torches),
/// placed like the original InstantiateModified: position (x * 5, y, z * 5), Y rotation unchanged.
///
/// One difference: the original pieces have a 1 m thick floor whose walking surface is at y = 1.
/// The whole dungeon is lowered by FloorSurfaceY, so its floor surface is at y = 0 like the hub's.
/// </summary>
public static class DungeonBuilder
{
    private const float Cell = MazeLayout.CellSize;
    /// <summary>Height of the pieces' floor surface above their origin.</summary>
    public const float FloorSurfaceY = 1f;
    /// <summary>Distance from a cell's center to the inner face of a wall on a closed side (walls are 1 m thick).</summary>
    public const float WallInnerOffset = 1.5f;
    private const string EntranceScenePath = "res://scenes/props/DungeonEntrance.tscn";
    private const string ExitScenePath = "res://scenes/props/DungeonExit.tscn";

    private static readonly Dictionary<PieceKind, PackedScene> Pieces = new();

    private static PackedScene Piece(PieceKind kind)
    {
        if (!Pieces.TryGetValue(kind, out var scene))
            Pieces[kind] = scene = GD.Load<PackedScene>($"res://scenes/dungeon/{kind}.tscn");
        return scene;
    }

    public static Node3D Build(MazeLayout layout)
    {
        var level = new Node3D { Name = "Dungeon" };

        level.AddChild(new WorldEnvironment { Name = "Environment", Environment = CreateEnvironment() });

        var pieces = new Node3D { Name = "Pieces", Position = new Vector3(0, -FloorSurfaceY, 0) };
        level.AddChild(pieces);
        foreach (var node in layout.Nodes)
        {
            // A lone node (1x1 maze) has no piece in the original; use the dead end so it's enclosed on 3 sides.
            var placement = MazePieces.DetermineNodePiece(node) ?? new PiecePlacement(PieceKind.CJunction, 0);
            var piece = Piece(placement.Kind).Instantiate<Node3D>();
            piece.Name = $"Node{node.index}_{placement.Kind}";
            piece.Position = new Vector3(node.mazePosition.X * Cell, node.mazePosition.Y, node.mazePosition.Z * Cell);
            piece.RotationDegrees = new Vector3(0, placement.RotationDegrees, 0);
            pieces.AddChild(piece);
        }

        // Entrance (back to the hub) at node 0, like the original. Node 0 is always the grid
        // corner (0, 0), so its south side is the grid edge and always a wall: stand it against that wall, facing north.
        var entrance = GD.Load<PackedScene>(EntranceScenePath).Instantiate<Node3D>();
        entrance.Name = "DungeonEntrance";
        var start = layout.Nodes[layout.EntranceIndex].mazePosition;
        entrance.Position = new Vector3(start.X * Cell, start.Y, start.Z * Cell - WallInnerOffset + 0.3f);
        level.AddChild(entrance);

        level.AddChild(BuildExit(layout));
        return level;
    }

    /// <summary>
    /// The exit at the last generated node, like the original Objective. That node is always a dead end
    /// (it was created last, so it only links back to its parent), so the exit stands against the wall
    /// opposite its opening, facing the corridor. In a 1x1 maze it goes on the north wall (the entrance has the south).
    /// </summary>
    private static Node3D BuildExit(MazeLayout layout)
    {
        var node = layout.Nodes[layout.ExitIndex];
        var open = MazePieces.NeighborSides(node);
        var back = open switch
        {
            Sides.North => Sides.South,
            Sides.South => Sides.North,
            Sides.East => Sides.West,
            Sides.West => Sides.East,
            _ => Sides.North,
        };
        var (vx, vz) = MazePieces.Vector(back);
        var exit = GD.Load<PackedScene>(ExitScenePath).Instantiate<Node3D>();
        exit.Name = "DungeonExit";
        exit.Position = new Vector3(node.mazePosition.X * Cell, node.mazePosition.Y, node.mazePosition.Z * Cell)
                        + new Vector3(vx, 0, vz) * (WallInnerOffset - 0.3f);
        exit.Rotation = new Vector3(0, Mathf.Atan2(-vx, -vz), 0); // front (+Z) faces into the cell
        return exit;
    }

    /// <summary>
    /// Server: bakes the navigation mesh for enemies from the maze's collision geometry (the original
    /// used NavMeshSurface per piece). Only the floor level is kept, so the tops of the ceiling slabs
    /// don't become walkable. Each instance has its own world, so its own navigation map.
    /// </summary>
    /// <param name="obstacles">
    /// Things standing in the maze that enemies must walk around (trees): their collision shapes are added
    /// to the baked geometry. They aren't under Pieces (they're replicated separately), so they aren't parsed.
    /// </param>
    public static void BakeNavigation(Node level, MazeLayout layout, IEnumerable<CollisionObject3D>? obstacles = null)
    {
        // Geometry is parsed in the Pieces node's own space (floor surface at y = FloorSurfaceY), so the
        // region lives under Pieces too. Bake only the maze's area, from the floor slab to below the ceiling.
        var min = new Vector3(-Cell / 2 - 1, FloorSurfaceY - 1f, -Cell / 2 - 1);
        var size = new Vector3(layout.Width * Cell + 2, 2.5f, layout.Height * Cell + 2);
        var pieces = level.GetNode<Node3D>("Pieces");
        var navMesh = new NavigationMesh
        {
            AgentRadius = 0.4f,
            AgentHeight = 1.8f,
            AgentMaxClimb = 0.4f,
            CellSize = 0.2f,   // radius, height and climb are whole multiples of the cell size
            CellHeight = 0.2f, // (must match navigation/3d/default_cell_size/height in project.godot)
            GeometryParsedGeometryType = NavigationMesh.ParsedGeometryType.StaticColliders,
            GeometryCollisionMask = 1,
            FilterBakingAabb = new Aabb(min, size),
        };
        var source = new NavigationMeshSourceGeometryData3D();
        NavigationServer3D.ParseSourceGeometryData(navMesh, source, pieces);
        var toPieces = pieces.GlobalTransform.AffineInverse();
        foreach (var obstacle in obstacles ?? [])
            foreach (var child in obstacle.FindChildren("*", nameof(CollisionShape3D), recursive: true, owned: false))
                if (child is CollisionShape3D shape && SolidFaces(shape.Shape) is { } faces)
                    source.AddFaces(faces, toPieces * shape.GlobalTransform);
        NavigationServer3D.BakeFromSourceGeometryData(navMesh, source);
        var region = new NavigationRegion3D { Name = "Navigation", NavigationMesh = navMesh };
        pieces.AddChild(region);
    }

    /// <summary>
    /// Triangles enclosing the shape's volume, for the navigation baker: a cylinder becomes an octagonal prism,
    /// a box a box. (Built here rather than from a Mesh, which the baker would have to read back from the renderer.)
    /// </summary>
    private static Vector3[]? SolidFaces(Shape3D? shape) => shape switch
    {
        CylinderShape3D c => Prism(8, c.Radius / Mathf.Cos(Mathf.Pi / 8), c.Height, Mathf.Pi / 8), // circumscribes the cylinder
        BoxShape3D b => Prism(4, new Vector2(b.Size.X, b.Size.Z).Length() / 2, b.Size.Y, Mathf.Atan2(b.Size.Z, b.Size.X), b.Size),
        _ => null,
    };

    /// <summary>A closed prism around the Y axis, centered on the origin. For a box, the corners are taken from its size.</summary>
    private static Vector3[] Prism(int sides, float radius, float height, float startAngle, Vector3? box = null)
    {
        var ring = new Vector2[sides];
        for (int i = 0; i < sides; i++)
        {
            var a = startAngle + i * Mathf.Tau / sides;
            ring[i] = box is { } b
                ? new Vector2((i is 0 or 3 ? 1 : -1) * b.X / 2, (i < 2 ? 1 : -1) * b.Z / 2)
                : new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * radius;
        }
        float y0 = -height / 2, y1 = height / 2;
        var faces = new System.Collections.Generic.List<Vector3>();
        for (int i = 0; i < sides; i++)
        {
            var (p, q) = (ring[i], ring[(i + 1) % sides]);
            Vector3 a0 = new(p.X, y0, p.Y), a1 = new(p.X, y1, p.Y), b0 = new(q.X, y0, q.Y), b1 = new(q.X, y1, q.Y);
            faces.AddRange([a0, b0, b1, a0, b1, a1]); // side
            faces.AddRange([new Vector3(0, y1, 0), a1, b1]); // top
            faces.AddRange([new Vector3(0, y0, 0), b0, a0]); // bottom
        }
        return faces.ToArray();
    }

    /// <summary>Where a tree stands in a node: towards one corner of the cell, on the floor.</summary>
    public static Vector3 TreePosition(MazeLayout layout, int nodeIndex, (float X, float Z) offset)
    {
        var n = layout.Nodes[nodeIndex].mazePosition;
        return new Vector3(n.X * Cell + offset.X, n.Y, n.Z * Cell + offset.Z);
    }

    /// <summary>Where an enemy starts in a node (cell center, on the floor).</summary>
    public static Vector3 EnemyPosition(MazeLayout layout, int nodeIndex, float offsetX = 0)
    {
        var n = layout.Nodes[nodeIndex].mazePosition;
        return new Vector3(n.X * Cell + offsetX, n.Y + 0.1f, n.Z * Cell);
    }

    /// <summary>World position of an item placed in a node (floor surface, plus a little height).</summary>
    public static Vector3 ItemPosition(MazeLayout layout, Core.Dungeon.ItemPlacement p)
    {
        var n = layout.Nodes[p.NodeIndex].mazePosition;
        return new Vector3(n.X * Cell + p.OffsetX, n.Y + 0.8f, n.Z * Cell + p.OffsetZ);
    }

    /// <summary>
    /// Dungeon lighting: a dim, warm sky that is never drawn (the background stays black) supplies
    /// ambient light and reflections. Without it, metallic surfaces like the chess floor have
    /// nothing to reflect and render black. (The Unity scene used a reflection probe for this.)
    /// </summary>
    private static Godot.Environment CreateEnvironment()
    {
        var warmDark = new Color(0.20f, 0.15f, 0.10f);
        return new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = new Color(0.02f, 0.02f, 0.03f),
            Sky = new Sky
            {
                SkyMaterial = new ProceduralSkyMaterial
                {
                    SkyTopColor = warmDark, SkyHorizonColor = warmDark,
                    GroundHorizonColor = warmDark, GroundBottomColor = warmDark,
                },
            },
            AmbientLightSource = Godot.Environment.AmbientSource.Sky,
            ReflectedLightSource = Godot.Environment.ReflectionSource.Sky,
            TonemapMode = Godot.Environment.ToneMapper.Filmic,
        };
    }

    /// <summary>Spawn point for the i-th arriving player: around a spot just north of the start cell's center, facing north (+Z).</summary>
    public static (Vector3 Position, float Yaw) SpawnPoint(MazeLayout layout, int index)
    {
        var start = layout.Nodes[layout.EntranceIndex].mazePosition;
        var center = new Vector3(start.X * Cell, start.Y + 0.3f, start.Z * Cell + 0.4f);
        var angle = index * Mathf.Tau / 6f;
        return (center + new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * 0.8f, Mathf.Pi);
    }
}
