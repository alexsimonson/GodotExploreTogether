using ExploreTogether.Core.Dungeon;
using ExploreTogether.Core.Gathering;
using ExploreTogether.Core.Items;

namespace ExploreTogether.Core.Tests;

/// <summary>Port of the original Resource component's rules (TreeResource: axe, logs, chance depletion).</summary>
public class GatheringTests
{
    private static readonly ItemDef Axe = ItemCatalog.Get(8)!;
    private static readonly ItemDef Sword = ItemCatalog.Get(1515)!;
    private static readonly ResourceDef Tree = ResourceCatalog.Tree;

    [Fact]
    public void Tree_IsTheOriginalPrefab()
    {
        Assert.Equal("Tree", Tree.Name);
        Assert.Equal(ToolRole.Axe, Tree.RequiredRole);
        Assert.Equal([ItemCatalog.LogId], Tree.Drops);
        Assert.False(Tree.SingleUse);
        Assert.Equal(5, Tree.RespawnSeconds);
        Assert.Same(Tree, ResourceCatalog.Get("tree"));
    }

    [Fact]
    public void Axe_IsAnAxe_AndLogsStack()
    {
        Assert.Equal(ToolRole.Axe, Axe.Role);
        Assert.True(ItemCatalog.Get(ItemCatalog.LogId)!.Stack);
    }

    [Fact]
    public void NeedsTheRightTool()
    {
        var random = new Random(1);
        Assert.Equal(HarvestOutcome.WrongTool, ResourceRules.Harvest(Tree, false, null, random).Outcome);
        Assert.Equal(HarvestOutcome.WrongTool, ResourceRules.Harvest(Tree, false, Sword, random).Outcome);
        var pickaxe = Axe with { Id = -1, Role = ToolRole.Pickaxe };
        Assert.Equal(HarvestOutcome.WrongTool, ResourceRules.Harvest(Tree, false, pickaxe, random).Outcome);
    }

    [Fact]
    public void DepletedResource_GivesNothing()
    {
        Assert.Equal(HarvestOutcome.Depleted, ResourceRules.Harvest(Tree, true, Axe, new Random(1)).Outcome);
    }

    [Fact]
    public void Odds_MatchTheOriginalRolls()
    {
        // Success on 12..399 of 0..511 = 388/512 = 75.8%; depletion after a success on 100..200 = 101/512 = 19.7%.
        var random = new Random(42);
        int gathered = 0, depleted = 0;
        const int tries = 100_000;
        for (int i = 0; i < tries; i++)
        {
            var r = ResourceRules.Harvest(Tree, false, Axe, random);
            if (r.Outcome != HarvestOutcome.Gathered) continue;
            Assert.Equal(ItemCatalog.LogId, r.ItemId);
            gathered++;
            if (r.DepletesNow) depleted++;
        }
        Assert.InRange(gathered / (double)tries, 388 / 512.0 - 0.01, 388 / 512.0 + 0.01);
        Assert.InRange(depleted / (double)gathered, 101 / 512.0 - 0.01, 101 / 512.0 + 0.01);
    }

    [Fact]
    public void SingleUse_AlwaysDepletesOnSuccess()
    {
        var bush = Tree with { Kind = "bush", SingleUse = true };
        var random = new Random(3);
        for (int i = 0; i < 200; i++)
            if (ResourceRules.Harvest(bush, false, Axe, random) is { Outcome: HarvestOutcome.Gathered } r)
                Assert.True(r.DepletesNow);
    }

    [Fact]
    public void EveryDropCanComeUp()
    {
        var mixed = Tree with { Drops = [ItemCatalog.LogId, 1] };
        var random = new Random(5);
        var seen = new HashSet<int>();
        for (int i = 0; i < 500; i++)
            if (ResourceRules.Harvest(mixed, false, Axe, random).ItemId is { } id) seen.Add(id);
        Assert.Equal([1, ItemCatalog.LogId], seen.Order());
    }

    [Fact]
    public void CanAdd_KnowsAboutStacksAndFreeSlots()
    {
        var inv = new Inventory(2);
        Assert.True(inv.CanAdd(ItemCatalog.LogId));
        inv.AddItem(ItemCatalog.LogId);
        inv.AddItem(1515);
        Assert.True(inv.CanAdd(ItemCatalog.LogId)); // onto the existing stack
        Assert.False(inv.CanAdd(1515));             // full
        Assert.False(inv.CanAdd(1));                // a new stack needs a free slot
        Assert.False(inv.CanAdd(123456));           // unknown item
    }

    [Fact]
    public void DungeonTrees_FollowTheOriginalLoop_AndAreSeeded()
    {
        var layout = MazeLayout.Generate(5, 5, 400, 77);
        var trees = DungeonContents.TreeNodes(layout, 1234);
        Assert.Equal(5, trees.Count); // i % 4 == 2 for i in 0..19
        Assert.Equal(trees, DungeonContents.TreeNodes(layout, 1234));
        Assert.All(trees, n => Assert.InRange(n, 1, layout.Nodes.Count - 2)); // never on the entrance or exit

        for (int i = 0; i < 5; i++)
        {
            var (x, z) = DungeonContents.TreeOffset(99, i);
            Assert.Equal(DungeonContents.TreeCornerOffset, Math.Abs(x));
            Assert.Equal(DungeonContents.TreeCornerOffset, Math.Abs(z));
            Assert.Equal((x, z), DungeonContents.TreeOffset(99, i));
        }
    }
}
