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

        return level;
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
