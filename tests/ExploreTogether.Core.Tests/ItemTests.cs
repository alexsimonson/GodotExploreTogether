using ExploreTogether.Core.Dungeon;
using ExploreTogether.Core.Items;
using ExploreTogether.Core.Persistence;
using static ExploreTogether.Core.Tests.TempDatabase;

namespace ExploreTogether.Core.Tests;

public class InventoryTests
{
    private const int Coin = 1, Sword = 1515, Pass = ItemCatalog.DungeonPassId;

    [Fact]
    public void Stackable_GoesOntoExistingStack()
    {
        var inv = new Inventory();
        Assert.Equal(0, inv.AddItem(Coin));
        Assert.Equal(0, inv.AddItem(Coin));
        Assert.Equal(2, inv.slots[0].stack_size);
        Assert.Equal(1, inv.AddItem(Sword));
    }

    [Fact]
    public void NonStackable_TakesItsOwnSlot()
    {
        var inv = new Inventory();
        Assert.Equal(0, inv.AddItem(Sword));
        Assert.Equal(1, inv.AddItem(Sword));
    }

    [Fact]
    public void Full_ReturnsMinusOne()
    {
        var inv = new Inventory(maxSlots: 2);
        inv.AddItem(Sword);
        inv.AddItem(Sword);
        Assert.Equal(-1, inv.AddItem(Sword));
        Assert.Equal(-1, inv.AddItem(Coin)); // a new stack doesn't fit either
    }

    [Fact]
    public void DefaultsTo28Slots_AndRejectsUnknownItems()
    {
        var inv = new Inventory();
        Assert.Equal(28, inv.slots.Length);
        Assert.Equal(-1, inv.AddItem(123456));
    }

    [Fact]
    public void Remove_EmptiesWholeSlot_AndFreesItForReuse()
    {
        var inv = new Inventory();
        inv.AddItem(Pass);
        inv.AddItem(Sword);
        Assert.True(inv.Contains(Pass));
        Assert.Equal(Pass, inv.RemoveItem(inv.FindItemInSlot(Pass)));
        Assert.False(inv.Contains(Pass));
        Assert.Equal(0, inv.AddItem(Coin));
    }

    [Fact]
    public void OccupiedAndLoad_RoundTrip()
    {
        var inv = new Inventory();
        inv.AddItem(Coin); inv.AddItem(Coin); inv.AddItem(Sword);
        var copy = new Inventory();
        copy.Load(inv.Occupied());
        Assert.Equal(inv.Occupied(), copy.Occupied());
    }
}

public class DungeonContentsTests
{
    [Fact]
    public void OriginalSize_HasOnePassAnd20Items_AwayFromEnds()
    {
        var layout = MazeLayout.Generate(5, 5, 400, seed: 7);
        for (var seed = 0; seed < 50; seed++)
        {
            var contents = DungeonContents.Generate(layout, seed);
            Assert.Equal(21, contents.Count);
            Assert.Single(contents, c => c.ItemId == ItemCatalog.DungeonPassId);
            Assert.All(contents, c =>
            {
                Assert.InRange(c.NodeIndex, 5, layout.Nodes.Count - 6); // original padding: [5, count - 5)
                Assert.NotNull(ItemCatalog.Get(c.ItemId));
                Assert.InRange(c.OffsetX, -DungeonContents.MaxOffset, DungeonContents.MaxOffset);
            });
        }
    }

    [Fact]
    public void SameSeed_SameContents()
    {
        var layout = MazeLayout.Generate(8, 8, 1000, seed: 1);
        Assert.Equal(DungeonContents.Generate(layout, 42), DungeonContents.Generate(layout, 42));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(11)]
    public void SmallMazes_StillGetValidNodes(int count)
    {
        var random = new Random(3);
        for (var i = 0; i < 200; i++)
        {
            var index = DungeonContents.RandomNodeIndex(count, DungeonContents.ItemPadding, random);
            Assert.InRange(index, 0, count - 1);
            if (count >= 3) Assert.InRange(index, 1, count - 2); // avoid entrance and exit cells
        }
    }
}

public class InventoryAndRunPersistenceTests : IDisposable
{
    private readonly GameDatabase _db = GameDatabase.OpenInMemory();
    public void Dispose() => _db.Dispose();

    private long NewCharacter(string name)
    {
        var acct = _db.CreateAccount(name.ToLowerInvariant(), "h")!;
        return _db.CreateCharacter(acct.Id, name, "000000").Character!.Id;
    }

    [Fact]
    public void Inventory_SaveReplacesAndLoads()
    {
        var c = NewCharacter("Kermit");
        _db.SaveInventory(c, [(0, 1, 5), (3, 1515, 1)]);
        _db.SaveInventory(c, [(1, 22, 1)]);
        Assert.Equal([(1, 22, 1, (int?)null)], _db.LoadInventory(c));
    }

    [Fact]
    public void Run_RecordsParticipantsEscapesAndOutcomeOnce()
    {
        var a = NewCharacter("Kermit");
        var b = NewCharacter("Grape");
        var run = _db.CreateRun(seed: 5, 5, 5, 400, a);
        _db.AddRunParticipant(run, a);
        _db.AddRunParticipant(run, b);
        _db.AddRunParticipant(run, a); // rejoin: no duplicate
        _db.MarkEscaped(run, a);
        _db.EndRun(run, "completed");
        _db.EndRun(run, "abandoned"); // ignored: already ended

        Assert.Equal(("completed", 2, 1), _db.GetRunSummary(run));
    }

    [Fact]
    public void RealSchema_V1Data_SurvivesUpgradeToV2_WithEmptyInventory()
    {
        using var temp = new TempDatabase();
        temp.CreateAtVersion(1, conn =>
        {
            Exec(conn, "INSERT INTO accounts (id, username, password_hash, created_at) VALUES (1, 'kermit', 'h', '2026-11-15T00:00:00.0000000Z');");
            Exec(conn, "INSERT INTO characters (id, account_id, name, color, created_at) VALUES (7, 1, 'Kermit', '3cb043', '2026-11-15T00:00:00.0000000Z');");
        });

        using var db = GameDatabase.Open(temp.Path);
        Assert.NotNull(db.BackupPath);
        Assert.Equal("Kermit", db.GetCharacter(7)!.Name);
        Assert.Empty(db.LoadInventory(7));
        db.SaveInventory(7, [(0, 1, 3)]);
        Assert.Equal([(0, 1, 3, (int?)null)], db.LoadInventory(7));
    }
}
