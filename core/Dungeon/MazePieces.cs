namespace ExploreTogether.Core.Dungeon;

/// <summary>The original dungeon prefabs.</summary>
public enum PieceKind
{
    Corner,
    Hall,
    TJunction,
    XJunction,
    CJunction, // dead end
}

[Flags]
public enum Sides
{
    None = 0,
    North = 1, // +Z
    East = 2,  // +X
    South = 4, // -Z
    West = 8,  // -X
    All = North | East | South | West,
}

public readonly record struct PiecePlacement(PieceKind Kind, int RotationDegrees);

/// <summary>
/// Faithful port of the piece selection in the original Maze.DetermineAndInstantiateNodePiece:
/// which prefab a node uses and its Y rotation in degrees.
///
/// Rotation carries over to Godot unchanged: grid positions map 1:1 (x -> X, z -> Z), and a
/// Y rotation of +90 degrees sends +Z to +X in both engines (Unity draws it as clockwise,
/// Godot as counter-clockwise, but the coordinate transform is identical).
/// MazePiecesTests verifies every placement opens exactly toward the node's neighbors.
/// </summary>
public static class MazePieces
{
    /// <summary>Open sides of each piece at rotation 0, as implied by the original selection logic.</summary>
    public static Sides OpenSidesAtRotation0(PieceKind kind) => kind switch
    {
        PieceKind.XJunction => Sides.All,
        PieceKind.TJunction => Sides.North | Sides.South | Sides.West, // closed: East
        PieceKind.CJunction => Sides.East,
        PieceKind.Hall => Sides.East | Sides.West,
        PieceKind.Corner => Sides.North | Sides.East,
        _ => Sides.None,
    };

    /// <summary>Returns null for a node with no neighbors (the original logged and skipped it).</summary>
    public static PiecePlacement? DetermineNodePiece(GeneratedNode node)
    {
        int neighbor_count = 0;
        if (node.northNeighbor != null) neighbor_count++;
        if (node.southNeighbor != null) neighbor_count++;
        if (node.eastNeighbor != null) neighbor_count++;
        if (node.westNeighbor != null) neighbor_count++;
        if (neighbor_count == 0)
        {
            return null;
        }

        PieceKind node_prefab;
        int rotation_degrees = 0;
        if (neighbor_count == 4)
        {
            node_prefab = PieceKind.XJunction;
        }
        else if (neighbor_count == 3)
        {
            node_prefab = PieceKind.TJunction;
            if (node.eastNeighbor == null)
            {
                // rotation_degrees already set to 0
            }
            else
            {
                if (node.westNeighbor == null)
                {
                    rotation_degrees = 180;
                }
                else if (node.northNeighbor == null)
                {
                    rotation_degrees = -90;
                }
                else if (node.southNeighbor == null)
                {
                    rotation_degrees = 90;
                }
            }
        }
        else if (neighbor_count == 1)
        {
            node_prefab = PieceKind.CJunction;
            if (node.eastNeighbor == null)
            {
                if (node.westNeighbor != null)
                {
                    rotation_degrees = 180;
                }
                else if (node.northNeighbor != null)
                {
                    rotation_degrees = -90;
                }
                else if (node.southNeighbor != null)
                {
                    rotation_degrees = 90;
                }
            }
        }
        else
        {
            if (node.northNeighbor != null && node.southNeighbor != null)
            {
                node_prefab = PieceKind.Hall;
                rotation_degrees = 90;
            }
            else if (node.eastNeighbor != null && node.westNeighbor != null)
            {
                node_prefab = PieceKind.Hall;
            }
            else
            {
                node_prefab = PieceKind.Corner;
                if (node.northNeighbor == null && node.eastNeighbor == null)
                {
                    rotation_degrees = 180;
                }
                else if (node.northNeighbor == null)
                {
                    // this means the east neighbor must exist, and the other is south
                    rotation_degrees = 90;
                }
                else if (node.eastNeighbor == null)
                {
                    // this means the north neighbor must exist, and the other is west
                    rotation_degrees = -90;
                }
            }
        }
        return new PiecePlacement(node_prefab, rotation_degrees);
    }

    /// <summary>Sides that actually lead to a neighbor.</summary>
    public static Sides NeighborSides(GeneratedNode node) =>
        (node.northNeighbor != null ? Sides.North : 0) |
        (node.eastNeighbor != null ? Sides.East : 0) |
        (node.southNeighbor != null ? Sides.South : 0) |
        (node.westNeighbor != null ? Sides.West : 0);

    /// <summary>
    /// Rotates sides about +Y the way both engines transform coordinates:
    /// x' = x cos(a) + z sin(a), z' = -x sin(a) + z cos(a). So +90 maps North (+Z) to East (+X).
    /// </summary>
    public static Sides Rotate(Sides sides, int degrees)
    {
        var result = Sides.None;
        foreach (var side in new[] { Sides.North, Sides.East, Sides.South, Sides.West })
        {
            if ((sides & side) == 0) continue;
            var (x, z) = Vector(side);
            var a = degrees * Math.PI / 180.0;
            var rx = (int)Math.Round(x * Math.Cos(a) + z * Math.Sin(a));
            var rz = (int)Math.Round(-x * Math.Sin(a) + z * Math.Cos(a));
            result |= FromVector(rx, rz);
        }
        return result;
    }

    public static (int X, int Z) Vector(Sides side) => side switch
    {
        Sides.North => (0, 1),
        Sides.East => (1, 0),
        Sides.South => (0, -1),
        Sides.West => (-1, 0),
        _ => throw new ArgumentException($"Not a single side: {side}"),
    };

    private static Sides FromVector(int x, int z) => (x, z) switch
    {
        (0, 1) => Sides.North,
        (1, 0) => Sides.East,
        (0, -1) => Sides.South,
        (-1, 0) => Sides.West,
        _ => throw new ArgumentException($"Not a unit grid direction: ({x}, {z})"),
    };
}
