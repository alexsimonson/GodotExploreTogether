namespace ExploreTogether.Core.Dungeon;

/// <summary>
/// A generated maze plus what's needed to rebuild it elsewhere: the server generates it and
/// sends <see cref="Export"/> to clients, which <see cref="Import"/> it. The format follows the
/// original ExportMaze/ImportMaze: per node its position and neighbor indices (-1 = none).
/// </summary>
public sealed class MazeLayout
{
    public const int CellSize = 5;    // original prefabSize
    public const float WallHeight = 4; // original pieces' ceiling height

    public int Width { get; }
    public int Height { get; }
    public int Length { get; }
    public int Seed { get; }
    public IReadOnlyList<GeneratedNode> Nodes { get; }

    /// <summary>The original placed the DungeonEntrance at node 0 and the Objective (exit) at the last node.</summary>
    public int EntranceIndex => 0;
    public int ExitIndex => Nodes.Count - 1;

    private MazeLayout(int width, int height, int length, int seed, IReadOnlyList<GeneratedNode> nodes)
    {
        Width = width;
        Height = height;
        Length = length;
        Seed = seed;
        Nodes = nodes;
    }

    public static MazeLayout Generate(int width, int height, int length, int seed)
    {
        var maze = new Maze(width, height, length, seed);
        maze.GenerateRandomMaze();
        return new MazeLayout(width, height, length, seed, maze.generated_nodes);
    }

    private const int HeaderInts = 5;
    private const int IntsPerNode = 7; // x, y, z, north, south, east, west

    /// <summary>Flat int array: header (width, height, length, seed, count), then 7 ints per node.</summary>
    public int[] Export()
    {
        var data = new int[HeaderInts + Nodes.Count * IntsPerNode];
        data[0] = Width; data[1] = Height; data[2] = Length; data[3] = Seed; data[4] = Nodes.Count;
        for (var i = 0; i < Nodes.Count; i++)
        {
            var n = Nodes[i];
            var o = HeaderInts + i * IntsPerNode;
            data[o] = n.mazePosition.X;
            data[o + 1] = n.mazePosition.Y;
            data[o + 2] = n.mazePosition.Z;
            data[o + 3] = n.northNeighbor?.index ?? -1;
            data[o + 4] = n.southNeighbor?.index ?? -1;
            data[o + 5] = n.eastNeighbor?.index ?? -1;
            data[o + 6] = n.westNeighbor?.index ?? -1;
        }
        return data;
    }

    public static MazeLayout Import(int[] data)
    {
        if (data.Length < HeaderInts) throw new ArgumentException("Maze data too short.");
        var count = data[4];
        if (count < 1 || data.Length != HeaderInts + count * IntsPerNode)
            throw new ArgumentException("Maze data has the wrong size.");

        var nodes = new List<GeneratedNode>(count);
        for (var i = 0; i < count; i++)
        {
            var o = HeaderInts + i * IntsPerNode;
            var node = new GeneratedNode();
            node.Init(new MazePosition(data[o], data[o + 1], data[o + 2]), i);
            nodes.Add(node);
        }

        GeneratedNode? At(int index) => index >= 0 && index < count ? nodes[index] : null;
        for (var i = 0; i < count; i++)
        {
            var o = HeaderInts + i * IntsPerNode;
            nodes[i].northNeighbor = At(data[o + 3]);
            nodes[i].southNeighbor = At(data[o + 4]);
            nodes[i].eastNeighbor = At(data[o + 5]);
            nodes[i].westNeighbor = At(data[o + 6]);
        }
        return new MazeLayout(data[0], data[1], data[2], data[3], nodes);
    }
}
