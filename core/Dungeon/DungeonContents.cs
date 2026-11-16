using ExploreTogether.Core.Items;

namespace ExploreTogether.Core.Dungeon;

/// <summary>An item lying in the dungeon: which item, in which cell, and an offset from the cell center.</summary>
public readonly record struct ItemPlacement(int ItemId, int NodeIndex, float OffsetX, float OffsetZ);

/// <summary>
/// What the original Maze.FillMazeWithItems put into a new dungeon (the item part; enemies and
/// trees come in later phases):
/// - one Dungeon Pass (item 999) on a random node,
/// - 20 random items (never the pass), each on a random node,
/// both via RandomSpawnPointInGenerated(5): a node index in [5, count - 5), i.e. away from both ends.
/// The exit (Objective) is at the last node and the entrance at node 0 (see MazeLayout).
/// Seeded, so the same dungeon always gets the same contents.
/// </summary>
public static class DungeonContents
{
    public const int DefaultItemCount = 20;
    public const int ItemPadding = 5;

    /// <summary>Items wander up to this far from the cell center so they don't stack on one spot.</summary>
    public const float MaxOffset = 1.2f;

    public static List<ItemPlacement> Generate(MazeLayout layout, int seed, int itemCount = DefaultItemCount)
    {
        var random = new Random(seed);
        var result = new List<ItemPlacement> { Place(ItemCatalog.DungeonPassId, layout, random) };
        for (int i = 0; i < itemCount; i++)
            result.Add(Place(ItemCatalog.GenerateItem(random).Id, layout, random));
        return result;
    }

    private static ItemPlacement Place(int itemId, MazeLayout layout, Random random)
    {
        float Offset() => (float)(random.NextDouble() * 2 - 1) * MaxOffset;
        return new ItemPlacement(itemId, RandomNodeIndex(layout.Nodes.Count, ItemPadding, random), Offset(), Offset());
    }

    /// <summary>
    /// Port of RandomSpawnPointInGenerated's index choice: Random.Range(padding, count - padding).
    /// The original assumed big mazes; for small ones the padding shrinks so the range stays valid,
    /// still avoiding the entrance (node 0) and the exit (last node) when possible.
    /// </summary>
    public static int RandomNodeIndex(int count, int padding, Random random)
    {
        if (count <= 2) return count - 1;
        padding = Math.Min(padding, (count - 1) / 2);
        padding = Math.Max(padding, 1);
        return random.Next(padding, count - padding);
    }
}
