using ExploreTogether.Core.Items;
using ExploreTogether.Core.Persistence;
using C = ExploreTogether.Core.Items.ItemContainer;
using static ExploreTogether.Core.Tests.TempDatabase;

namespace ExploreTogether.Core.Tests;

/// <summary>The hub storage follows the inventory's drag rules (as the original's Storage did).</summary>
public class StorageMoveTests
{
    private const int Coin = 1, Log = ItemCatalog.LogId, Sword = 1515, Helmet = 22, Shirt = 21;
    private static readonly int Weapon = (int)EquipmentType.Weapon, Head = (int)EquipmentType.Head;

    private readonly Inventory _inv = new();
    private readonly Gear _gear = new();
    private readonly Inventory _storage = new(Inventory.HubStorageSlots);

    private bool Move(C from, int fromSlot, C to, int toSlot) =>
        SlotContainerLogic.Move(_inv, _gear, from, fromSlot, to, toSlot, _storage);

    [Fact]
    public void Storage_HasTheOriginalSize() => Assert.Equal(50, _storage.max_slots);

    [Fact]
    public void Inventory_And_Storage_SwapLikeTwoInventories()
    {
        _inv.AddItem(Sword, uid: 5);
        _storage.AddItem(Coin);
        _storage.slots[0].stack_size = 7;

        Assert.True(Move(C.Inventory, 0, C.Storage, 0));
        Assert.Equal(Sword, _storage.slots[0].itemId);
        Assert.Equal(5, _storage.slots[0].uid); // the serial goes with the copy
        Assert.Equal(Coin, _inv.slots[0].itemId);
        Assert.Equal(7, _inv.slots[0].stack_size);

        Assert.True(Move(C.Storage, 0, C.Storage, 49)); // rearranging inside the storage
        Assert.Equal(Sword, _storage.slots[49].itemId);
        Assert.Null(_storage.slots[0].itemId);
    }

    [Fact]
    public void SameStackable_Merges()
    {
        _inv.AddItems(Log, 4);
        _storage.AddItems(Log, 10);
        Assert.True(Move(C.Inventory, 0, C.Storage, 0));
        Assert.Equal(14, _storage.slots[0].stack_size);
        Assert.Null(_inv.slots[0].itemId);
        Assert.Equal(0, _inv.slots[0].stack_size);
    }

    [Fact]
    public void Equipment_EquipsFromStorage_AndUnequipsIntoIt()
    {
        _storage.AddItem(Helmet, uid: 9);
        Assert.False(Move(C.Storage, 0, C.Gear, Weapon)); // wrong slot
        Assert.True(Move(C.Storage, 0, C.Gear, Head));
        Assert.Equal(9, _gear.slots[Head].uid);

        _storage.AddItem(Shirt);
        Assert.False(Move(C.Gear, Head, C.Storage, 0)); // a shirt can't go into the head slot in exchange
        Assert.True(Move(C.Gear, Head, C.Storage, 1));
        Assert.Equal(Helmet, _storage.slots[1].itemId);
        Assert.Null(_gear.slots[Head].itemId);
    }

    [Fact]
    public void WithoutStorage_MovesInvolvingItFail()
    {
        _inv.AddItem(Sword);
        Assert.False(SlotContainerLogic.Move(_inv, _gear, C.Inventory, 0, C.Storage, 0));
        Assert.False(Move(C.Inventory, 0, C.Storage, 50)); // out of range
        Assert.Equal(Sword, _inv.slots[0].itemId);
    }

    [Fact]
    public void Drop_FromStorage()
    {
        _storage.AddItems(Log, 3);
        Assert.Equal((Log, 3, (int?)null, (long?)null), SlotContainerLogic.Drop(_inv, _gear, C.Storage, 0, _storage));
        Assert.Null(_storage.slots[0].itemId);
    }
}

public class StoragePersistenceTests : IDisposable
{
    private const int Coin = 1, Sword = 1515;
    private readonly GameDatabase _db = GameDatabase.OpenInMemory();
    public void Dispose() => _db.Dispose();

    private long NewCharacter(string name)
    {
        var acct = _db.CreateAccount(name.ToLowerInvariant(), "h")!;
        return _db.CreateCharacter(acct.Id, name, "000000").Character!.Id;
    }

    [Fact]
    public void Storage_IsSavedWithTheInventory_AndPerCharacter()
    {
        var kermit = NewCharacter("Kermit");
        var berry = NewCharacter("Blueberry");
        var uid = _db.RegisterItem(Sword, "test");
        _db.SaveItems(kermit, [new(0, Coin, 2)], [], storage: [new(3, Sword, 1, Uid: uid), new(49, ItemCatalog.LogId, 20)]);

        Assert.Equal([new SlotRecord(3, Sword, 1, Uid: uid), new SlotRecord(49, ItemCatalog.LogId, 20)], _db.LoadStorage(kermit));
        Assert.Empty(_db.LoadStorage(berry));

        // Saving without storage leaves it alone; saving an empty one clears it.
        _db.SaveItems(kermit, [], []);
        Assert.Equal(2, _db.LoadStorage(kermit).Count);
        _db.SaveItems(kermit, [], [], storage: []);
        Assert.Empty(_db.LoadStorage(kermit));
    }

    [Fact]
    public void Audit_SeesSerialsInStorage()
    {
        var c = NewCharacter("Grape");
        var uid = _db.RegisterItem(Sword, "test");
        _db.SaveItems(c, [new(0, Sword, 1, Uid: uid)], [], storage: [new(0, Sword, 1, Uid: uid)]);
        var problem = Assert.Single(_db.AuditItems());
        Assert.Equal(ItemAuditKind.Duplicate, problem.Kind);
        Assert.Contains(problem.Places, p => p.Contains("storage slot 0"));
    }

    [Fact]
    public void Storage_IsDeletedWithTheCharacter()
    {
        var c = NewCharacter("Grape");
        _db.SaveItems(c, [], [], storage: [new(0, Coin, 5)]);
        _db.DeleteCharacter(c);
        Assert.Empty(_db.LoadStorage(c));
    }

    [Fact]
    public void RealSchema_V6Data_SurvivesUpgrade_WithAnEmptyStorage()
    {
        using var temp = new TempDatabase();
        temp.CreateAtVersion(6, conn =>
        {
            Exec(conn, "INSERT INTO accounts (id, username, password_hash, created_at) VALUES (1, 'kermit', 'h', '2026-11-16T00:00:00.0000000Z');");
            Exec(conn, "INSERT INTO characters (id, account_id, name, color, created_at) VALUES (7, 1, 'Kermit', '3cb043', '2026-11-16T00:00:00.0000000Z');");
            // A log saved while logs weren't stackable yet (one per slot, with a serial).
            Exec(conn, "INSERT INTO inventory_items (character_id, slot, item_id, count, data, uid) VALUES (7, 0, 432, 1, NULL, 12);");
        });
        using var db = GameDatabase.Open(temp.Path);
        Assert.Equal(Migrations.LatestVersion, db.SchemaVersion); // v7 storage, and every step since
        Assert.Equal([new SlotRecord(0, ItemCatalog.LogId, 1, null, 12)], db.LoadInventory(7));
        Assert.Empty(db.LoadStorage(7));

        // Loading drops the now-meaningless serial from the (now stackable) log.
        var inv = new Inventory();
        inv.Load(db.LoadInventory(7));
        Assert.Null(inv.slots[0].uid);
    }
}
