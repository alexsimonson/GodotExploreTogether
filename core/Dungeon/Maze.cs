namespace ExploreTogether.Core.Dungeon;

/// <summary>A grid cell position. Ported from Unity's Vector3 (y kept for fidelity; always 0).</summary>
public readonly record struct MazePosition(int X, int Y, int Z);

/// <summary>Port of the Unity GeneratedNode (was a ScriptableObject).</summary>
public sealed class GeneratedNode
{
    public MazePosition mazePosition;
    public int index;
    public GeneratedNode? northNeighbor; // +Z
    public GeneratedNode? southNeighbor; // -Z
    public GeneratedNode? eastNeighbor;  // +X
    public GeneratedNode? westNeighbor;  // -X

    public void Init(MazePosition _mazePosition, int _index)
    {
        mazePosition = _mazePosition;
        index = _index;
    }
}

/// <summary>
/// Faithful port of the original Unity Maze.cs generation (GridRecursiveBacktrackGenerator and helpers).
/// The algorithm is intentionally unchanged. Only Unity types were replaced:
///   Vector3 -> MazePosition, ScriptableObject.CreateInstance -> new GeneratedNode(),
///   UnityEngine.Random.Range(int a, int b) -> System.Random.Next(a, b) (both exclude b),
///   Debug.Log -> a diagnostic message list.
/// The Random is seeded, so a layout can be reproduced from (seed, width, height, length).
///
/// Behaviors kept from the original:
/// - At most max_dungeon_length + 1 nodes (the length check runs after a node is added).
/// - Generation is recursive: one stack frame per generated node.
/// - Starts at (0, 0, 0), so node 0 is always the grid corner.
/// </summary>
public sealed class Maze
{
    public int prefabSize = 5;

    private readonly List<MazePosition> neighbors_visited = new();
    private readonly Stack<GeneratedNode> generating_stack = new();
    public List<GeneratedNode> generated_nodes = new();

    public int max_width;
    public int max_height;
    public int max_dungeon_length = 1000;
    public int total_count = 0;

    public readonly List<string> Diagnostics = new();
    private readonly Random random;

    public Maze(int width, int height, int dungeonLength, int seed)
    {
        max_width = width;
        max_height = height;
        max_dungeon_length = dungeonLength;
        random = new Random(seed);
    }

    /// <summary>Generation part of the original GenerateRandomMaze() (rendering/navigation/items are done elsewhere).</summary>
    public void GenerateRandomMaze()
    {
        var start_pos = new MazePosition(0, 0, 0);
        GridRecursiveBacktrackGenerator(start_pos, null, true);
    }

    private List<MazePosition> FindEmptyNeighbors(List<MazePosition> neighbors)
    {
        var filter = new List<MazePosition>();
        foreach (var neighbor in neighbors)
        {
            if (!neighbors_visited.Contains(neighbor)) filter.Add(neighbor);
        }
        return filter;
    }

    private void GridRecursiveBacktrackGenerator(MazePosition position, GeneratedNode? last_node = null, bool first_pass = false)
    {
        neighbors_visited.Add(position);
        var current_node = new GeneratedNode();
        current_node.Init(position, total_count);
        generating_stack.Push(current_node);
        generated_nodes.Add(current_node);

        if (!first_pass && last_node != null)
        {
            if (position.X > last_node.mazePosition.X)
            {
                last_node.eastNeighbor = current_node;
                current_node.westNeighbor = last_node;
            }
            else if (position.X < last_node.mazePosition.X)
            {
                last_node.westNeighbor = current_node;
                current_node.eastNeighbor = last_node;
            }
            else if (position.Z > last_node.mazePosition.Z)
            {
                last_node.northNeighbor = current_node;
                current_node.southNeighbor = last_node;
            }
            else if (position.Z < last_node.mazePosition.Z)
            {
                last_node.southNeighbor = current_node;
                current_node.northNeighbor = last_node;
            }
            else
            {
                Diagnostics.Add("SOMETHING WENT HORRIBLY WRONG");
            }
        }

        var empty_neighbors = FindEmptyNeighbors(FindNeighborsGrid(current_node.mazePosition));
        if (total_count == max_dungeon_length)
        {
            return;
        }

        bool control = true;
        while (empty_neighbors.Count <= 0 && control)
        {
            generating_stack.Pop();
            if (generating_stack.Count <= 0)
            {
                control = false;
            }
            else
            {
                current_node = generating_stack.Peek();
                empty_neighbors = FindEmptyNeighbors(FindNeighborsGrid(current_node.mazePosition));
            }
        }
        if (empty_neighbors.Count <= 0)
        {
            return;
        }

        var random_neighbor = empty_neighbors[random.Next(0, empty_neighbors.Count)];
        total_count++;
        GridRecursiveBacktrackGenerator(random_neighbor, current_node);
    }

    private List<MazePosition> FindNeighborsGrid(MazePosition position)
    {
        var x = position.X;
        var y = position.Y;
        var z = position.Z;
        var neighbors = new List<MazePosition>();
        if (x + 1 < max_width) neighbors.Add(new MazePosition(x + 1, y, z));
        if (x - 1 >= 0) neighbors.Add(new MazePosition(x - 1, y, z));
        if (z + 1 < max_height) neighbors.Add(new MazePosition(x, y, z + 1));
        if (z - 1 >= 0) neighbors.Add(new MazePosition(x, y, z - 1));
        return neighbors;
    }
}
