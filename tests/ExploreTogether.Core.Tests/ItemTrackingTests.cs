using ExploreTogether.Core.Items;
using ExploreTogether.Core.Persistence;
using C = ExploreTogether.Core.Items.ItemContainer;
using static ExploreTogether.Core.Tests.TempDatabase;

namespace ExploreTogether.Core.Tests;

/// <summary>Item serials travel with their copy through every container operation.</summary>
public class ItemSerialTests
{
    private const int Coin = 1, Sword = 1515, Pistol = 1337, Helmet = 22;
    private static readonly int Weapon = (int)EquipmentType.Weapon, Head = (int)EquipmentType.Head;

    [Fact]
    public void Serial_StaysWithItsCopy_ThroughMovesEquipsAndDrops()
    {
        var inv = new Inventory();
        var gear = new Gear();
        inv.AddItem(Sword, uid: 100);
        inv.AddItem(Sword, uid: 200);

        SlotContainerLogic.Move(inv, gear, C.Inventory, 0, C.Inventory, 5);  // rearrange
        Assert.Equal(100, inv.slots[5].uid);
        Assert.Null(inv.slots[0].uid);

        SlotContainerLogic.Move(inv, gear, C.Inventory, 5, C.Gear, Weapon);  // equip #100
        SlotContainerLogic.Move(inv, gear, C.Inventory, 1, C.Gear, Weapon);  // swap in #200
        Assert.Equal(200, gear.WeaponSlot.uid);
        Assert.Equal(100, inv.slots[1].uid);

        Assert.Equal((Sword, 1, (int?)null, (long?)100), SlotContainerLogic.Drop(inv, gear, C.Inventory, 1));
        Assert.Null(inv.slots[1].uid);
    }

    [Fact]
    public void Stackables_NeverCarryASerial()
    {
        var inv = new Inventory();
        inv.AddItem(Coin, uid: 5);
        inv.AddItems(Coin, 3, uid: 6);
        Assert.Null(inv.slots[0].uid);
    }

    [Fact]
    public void AddingSeveralCopies_DoesNotShareOneSerial()
    {
        var inv = new Inventory();
        Assert.Equal(2, inv.AddItems(Sword, 2, uid: 7));
        Assert.Null(inv.slots[0].uid);
        Assert.Null(inv.slots[1].uid);
        inv.AddItems(Sword, 1, uid: 8);
        Assert.Equal(8, inv.slots[2].uid);
    }

    [Fact]
    public void RemoveItem_ClearsTheSerial()
    {
        var inv = new Inventory();
        inv.AddItem(Sword, uid: 9);
        inv.RemoveItem(0);
        inv.AddItem(Coin);
        Assert.Null(inv.slots[0].uid);
    }

    [Fact]
    public void Records_RoundTrip_ForInventoryAndGear()
    {
        var inv = new Inventory();
        inv.AddItem(Pistol, data: 4, uid: 11);
        inv.AddItems(Coin, 3);
        var copy = new Inventory();
        copy.Load(inv.Occupied());
        Assert.Equal(inv.Occupied(), copy.Occupied());

        var gear = new Gear();
        gear.Load([new SlotRecord(Head, Helmet, 1, Uid: 12), new SlotRecord(Head, Sword, 1, Uid: 13)]); // a sword can't go on the head
        Assert.Equal([new SlotRecord(Head, Helmet, 1, Uid: 12)], gear.Occupied());
    }
}

/// <summary>The serial registry, the item event log and the duplicate audit in the database.</summary>
public class ItemTrackingDatabaseTests : IDisposable
{
    private const int Coin = 1, Sword = 1515, Helmet = 22;
    private static readonly int Weapon = (int)EquipmentType.Weapon;
    private readonly GameDatabase _db = GameDatabase.OpenInMemory();
    public void Dispose() => _db.Dispose();

    private long NewCharacter(string name)
    {
        var acct = _db.CreateAccount(name.ToLowerInvariant(), "h")!;
        return _db.CreateCharacter(acct.Id, name, "000000").Character!.Id;
    }

    private static ItemEvent Event(string action, int itemId, long? uid, long character, string name, int count = 1) =>
        new(action, itemId, count, uid, character, name);

    [Fact]
    public void RegisterItem_IssuesIncreasingSerials_AndRemembersTheOrigin()
    {
        var a = _db.RegisterItem(Sword, "loot, run 1");
        var b = _db.RegisterItem(Helmet, "dev starter kit");
        Assert.True(b > a);
        Assert.Equal((Sword, "loot, run 1"), (_db.GetItemSerial(a)!.Value.ItemId, _db.GetItemSerial(a)!.Value.Origin));
        Assert.Null(_db.GetItemSerial(9999));
    }

    [Fact]
    public void Audit_IsClean_WhenEverySerialIsRegisteredAndInOnePlace()
    {
        var c = NewCharacter("Kermit");
        var uid = _db.RegisterItem(Sword, "test");
        _db.SaveItems(c, [new(0, Coin, 5), new(1, Sword, 1, Uid: uid)], []);
        Assert.Empty(_db.AuditItems());
    }

    [Fact]
    public void Audit_FindsTheSameSerialOnTwoCharacters()
    {
        var kermit = NewCharacter("Kermit");
        var berry = NewCharacter("Blueberry");
        var uid = _db.RegisterItem(Sword, "test");
        _db.SaveItems(kermit, [new(3, Sword, 1, Uid: uid)], []);
        _db.SaveItems(berry, [], [new(Weapon, Sword, 1, Uid: uid)]);

        var problem = Assert.Single(_db.AuditItems());
        Assert.Equal((ItemAuditKind.Duplicate, uid, Sword), (problem.Kind, problem.Uid, problem.ItemId));
        Assert.Equal(2, problem.Places.Count);
        Assert.Contains(problem.Places, p => p.Contains("Kermit") && p.Contains("inventory slot 3"));
        Assert.Contains(problem.Places, p => p.Contains("Blueberry") && p.Contains("gear slot 10"));
    }

    [Fact]
    public void Audit_FindsTheSameSerialTwiceOnOneCharacter()
    {
        var c = NewCharacter("Kermit");
        var uid = _db.RegisterItem(Sword, "test");
        _db.SaveItems(c, [new(0, Sword, 1, Uid: uid)], [new(Weapon, Sword, 1, Uid: uid)]);
        Assert.Equal(ItemAuditKind.Duplicate, Assert.Single(_db.AuditItems()).Kind);
    }

    [Fact]
    public void Audit_FindsSerialsTheRegistryNeverIssued_OrIssuedForAnotherItem()
    {
        var c = NewCharacter("Kermit");
        var helmetSerial = _db.RegisterItem(Helmet, "test");
        _db.SaveItems(c, [new(0, Sword, 1, Uid: 777), new(1, Sword, 1, Uid: helmetSerial)], []);

        var problems = _db.AuditItems();
        Assert.Contains(problems, p => p is { Kind: ItemAuditKind.Unregistered, Uid: 777 });
        Assert.Contains(problems, p => p.Kind == ItemAuditKind.WrongItem && p.Uid == helmetSerial);
    }

    [Fact]
    public void Events_AreSavedWithTheItems_AndReadBackInOrder()
    {
        var c = NewCharacter("Kermit");
        var uid = _db.RegisterItem(Sword, "loot, run 3");
        _db.SaveItems(c, [new(0, Sword, 1, Uid: uid)], [],
            [Event(ItemActions.Created, Sword, uid, c, "Kermit"), Event(ItemActions.PickedUp, Sword, uid, c, "Kermit")]);
        _db.SaveItems(c, [], [new(Weapon, Sword, 1, Uid: uid)], [Event(ItemActions.Equipped, Sword, uid, c, "Kermit")]);

        var history = _db.GetItemHistory(uid);
        Assert.Equal([ItemActions.Created, ItemActions.PickedUp, ItemActions.Equipped], history.Select(h => h.Event.Action));
        Assert.All(history, h => Assert.Equal("Kermit", h.Event.CharacterName));
    }

    [Fact]
    public void Events_AreNotLogged_WhenTheSaveFails()
    {
        var c = NewCharacter("Kermit");
        var uid = _db.RegisterItem(Sword, "test");
        Assert.ThrowsAny<Exception>(() => _db.SaveItems(c, [new(0, Coin, 1), new(0, Coin, 1)], [],
            [Event(ItemActions.PickedUp, Sword, uid, c, "Kermit")]));
        Assert.Empty(_db.GetItemHistory(uid));
    }

    [Fact]
    public void History_OutlivesADeletedCharacter()
    {
        var c = NewCharacter("Grape");
        var uid = _db.RegisterItem(Sword, "test");
        _db.SaveItems(c, [new(0, Sword, 1, Uid: uid)], [], [Event(ItemActions.PickedUp, Sword, uid, c, "Grape")]);
        _db.LogItemEvents([Event(ItemActions.Destroyed, Sword, uid, c, "Grape")]);
        _db.DeleteCharacter(c);

        Assert.Equal([ItemActions.PickedUp, ItemActions.Destroyed], _db.GetItemHistory(uid).Select(h => h.Event.Action));
        Assert.Equal(2, _db.GetCharacterItemHistory("grape").Count); // by name, any case
        Assert.Empty(_db.AuditItems()); // the deleted character's items are gone from the audit too
    }

    [Fact]
    public void RealSchema_V5Data_SurvivesUpgradeToV6_WithoutSerials()
    {
        using var temp = new TempDatabase();
        temp.CreateAtVersion(5, conn =>
        {
            Exec(conn, "INSERT INTO accounts (id, username, password_hash, created_at) VALUES (1, 'kermit', 'h', '2026-11-15T00:00:00.0000000Z');");
            Exec(conn, "INSERT INTO characters (id, account_id, name, color, created_at) VALUES (7, 1, 'Kermit', '3cb043', '2026-11-15T00:00:00.0000000Z');");
            Exec(conn, "INSERT INTO inventory_items (character_id, slot, item_id, count, data) VALUES (7, 0, 1337, 1, 4);");
            Exec(conn, "INSERT INTO equipment_items (character_id, slot, item_id) VALUES (7, 10, 1515);");
        });
        using var db = GameDatabase.Open(temp.Path);
        Assert.Equal([new SlotRecord(0, 1337, 1, 4)], db.LoadInventory(7));
        Assert.Equal([new SlotRecord(Weapon, Sword, 1)], db.LoadGearRecords(7));
        Assert.Empty(db.AuditItems()); // no serials yet; the server gives them out when the character next loads
        Assert.True(db.RegisterItem(Sword, "saved before item serials existed") > 0);
    }
}
