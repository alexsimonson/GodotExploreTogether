using ExploreTogether.Core.Dungeon;

namespace ExploreTogether.Core.Tests;

public class MazeTests
{
    public static IEnumerable<object[]> Configs =>
    [
        [5, 5, 400],   // the original MazeGenerator prefab settings
        [10, 10, 1000],
        [12, 3, 20],   // length limit reached before the grid fills
        [1, 1, 5],
        [30, 30, 1000],
    ];

    [Theory, MemberData(nameof(Configs))]
    public void SameSeed_SameLayout(int w, int h, int len)
    {
        var a = MazeLayout.Generate(w, h, len, seed: 1234);
        var b = MazeLayout.Generate(w, h, len, seed: 1234);
        Assert.Equal(a.Export(), b.Export());
    }

    [Fact]
    public void DifferentSeeds_UsuallyDifferentLayouts()
    {
        var layouts = Enumerable.Range(0, 10).Select(s => string.Join(",", MazeLayout.Generate(8, 8, 1000, s).Export().Skip(5)));
        Assert.True(layouts.Distinct().Count() > 1);
    }

    [Theory, MemberData(nameof(Configs))]
    public void Layout_IsAConnectedTree_InsideTheGrid(int w, int h, int len)
    {
        for (var seed = 0; seed < 20; seed++)
        {
            var nodes = MazeLayout.Generate(w, h, len, seed).Nodes;

            // Node count: the whole grid, or length + 1 (the original checks the limit after adding a node).
            Assert.Equal(Math.Min(w * h, len + 1), nodes.Count);
            Assert.Equal(nodes.Count, nodes.Select(n => n.mazePosition).Distinct().Count());
            Assert.Equal(new MazePosition(0, 0, 0), nodes[0].mazePosition);

            var links = 0;
            foreach (var n in nodes)
            {
                Assert.InRange(n.mazePosition.X, 0, w - 1);
                Assert.InRange(n.mazePosition.Z, 0, h - 1);
                Assert.Equal(0, n.mazePosition.Y);
                CheckLink(n, n.northNeighbor, 0, 1, m => m.southNeighbor);
                CheckLink(n, n.southNeighbor, 0, -1, m => m.northNeighbor);
                CheckLink(n, n.eastNeighbor, 1, 0, m => m.westNeighbor);
                CheckLink(n, n.westNeighbor, -1, 0, m => m.eastNeighbor);
                links += new[] { n.northNeighbor, n.southNeighbor, n.eastNeighbor, n.westNeighbor }.Count(x => x != null);
            }
            Assert.Equal(2 * (nodes.Count - 1), links); // a tree: n - 1 edges, each counted from both ends

            // Every node reachable from node 0.
            Assert.Equal(nodes.Count, Reachable(nodes[0]).Count);
        }
    }

    private static HashSet<GeneratedNode> Reachable(GeneratedNode start)
    {
        var seen = new HashSet<GeneratedNode> { start };
        var queue = new Queue<GeneratedNode>([start]);
        while (queue.Count > 0)
        {
            var n = queue.Dequeue();
            foreach (var m in new[] { n.northNeighbor, n.southNeighbor, n.eastNeighbor, n.westNeighbor })
                if (m != null && seen.Add(m)) queue.Enqueue(m);
        }
        return seen;
    }

    private static void CheckLink(GeneratedNode n, GeneratedNode? m, int dx, int dz, Func<GeneratedNode, GeneratedNode?> back)
    {
        if (m == null) return;
        Assert.Equal(n.mazePosition.X + dx, m.mazePosition.X);
        Assert.Equal(n.mazePosition.Z + dz, m.mazePosition.Z);
        Assert.Same(n, back(m));
    }

    [Theory, MemberData(nameof(Configs))]
    public void ExportImport_RoundTrips(int w, int h, int len)
    {
        var original = MazeLayout.Generate(w, h, len, seed: 99);
        var copy = MazeLayout.Import(original.Export());
        Assert.Equal(original.Export(), copy.Export());
        Assert.Equal((w, h, len, 99), (copy.Width, copy.Height, copy.Length, copy.Seed));
    }

    [Fact]
    public void Import_RejectsBadData()
    {
        Assert.Throws<ArgumentException>(() => MazeLayout.Import([1, 2]));
        Assert.Throws<ArgumentException>(() => MazeLayout.Import([5, 5, 10, 0, 3, 0, 0, 0]));
    }
}

public class MazePiecesTests
{
    /// <summary>
    /// The key check for the Unity -> Godot port: for every node, the chosen piece rotated by
    /// the chosen angle (using the engine's Y-rotation formula) must open exactly toward the
    /// node's neighbors. This proves the original rotations carry over without negation.
    /// </summary>
    [Fact]
    public void EveryPlacement_OpensExactlyTowardNeighbors()
    {
        var seen = new HashSet<(PieceKind, int)>();
        for (var seed = 0; seed < 300; seed++)
        {
            foreach (var node in MazeLayout.Generate(9, 9, 1000, seed).Nodes)
            {
                var placement = MazePieces.DetermineNodePiece(node);
                if (node.mazePosition == default && MazePieces.NeighborSides(node) == Sides.None)
                {
                    Assert.Null(placement); // 1x1 maze: a lone node has no piece (as in the original)
                    continue;
                }
                Assert.NotNull(placement);
                var (kind, degrees) = placement!.Value;
                var open = MazePieces.Rotate(MazePieces.OpenSidesAtRotation0(kind), degrees);
                Assert.Equal(MazePieces.NeighborSides(node), open);
                seen.Add((kind, degrees));
            }
        }
        // All 15 original (piece, rotation) cases appear: X 0; T 0/180/-90/90; C 0/180/-90/90; Hall 0/90; Corner 0/90/180/-90.
        Assert.Equal(15, seen.Count);
    }

    [Theory]
    [InlineData(Sides.North, 90, Sides.East)]
    [InlineData(Sides.East, 90, Sides.South)]
    [InlineData(Sides.North, -90, Sides.West)]
    [InlineData(Sides.North | Sides.East, 180, Sides.South | Sides.West)]
    public void Rotate_FollowsEngineConvention(Sides input, int degrees, Sides expected) =>
        Assert.Equal(expected, MazePieces.Rotate(input, degrees));
}
