using ExploreTogether.Core.Items;
using ExploreTogether.Core.Persistence;
using C = ExploreTogether.Core.Items.ItemContainer;
using static ExploreTogether.Core.Tests.TempDatabase;

namespace ExploreTogether.Core.Tests;

public class SlotContainerLogicTests
{
    private const int Coin = 1, Log = 432, Helmet = 22, Shirt = 21, RedShirt = 24, Pants = 20, Sword = 1515, Pistol = 1337;
    private static readonly int Head = (int)EquipmentType.Head, Torso = (int)EquipmentType.Torso,
        Leg = (int)EquipmentType.Leg, Weapon = (int)EquipmentType.Weapon;

    private readonly Inventory _inv = new();
    private readonly Gear _gear = new();

    private int? Inv(int i) => _inv.slots[i].itemId;
    private int? GearAt(int i) => _gear.slots[i].itemId;

    [Fact]
    public void SwapItem_MovesIntoEmptySlot_AndSwapsOccupied()
    {
        _inv.AddItem(Coin); _inv.AddItem(Coin); _inv.AddItem(Log); // slot 0: Coin x2, slot 1: Log
        Assert.True(SlotContainerLogic.Move(_inv, _gear, C.Inventory, 0, C.Inventory, 5));
        Assert.Equal((null, Coin, 2), (Inv(0), Inv(5), _inv.slots[5].stack_size));
        Assert.True(SlotContainerLogic.Move(_inv, _gear, C.Inventory, 5, C.Inventory, 1));
        Assert.Equal((Log, Coin), (Inv(5), Inv(1)));
    }

    [Fact]
    public void DraggingEmptySlot_OrOntoItself_DoesNothing()
    {
        _inv.AddItem(Coin);
        Assert.False(SlotContainerLogic.Move(_inv, _gear, C.Inventory, 3, C.Inventory, 0));
        Assert.False(SlotContainerLogic.Move(_inv, _gear, C.Inventory, 0, C.Inventory, 0));
        Assert.False(SlotContainerLogic.Move(_inv, _gear, C.Inventory, 0, C.Inventory, 99));
    }

    [Fact]
    public void Equip_OnlyIntoMatchingSlot()
    {
        _inv.AddItem(Helmet); _inv.AddItem(Sword); _inv.AddItem(Coin);
        Assert.False(SlotContainerLogic.Move(_inv, _gear, C.Inventory, 0, C.Gear, Torso)); // helmet isn't a torso item
        Assert.False(SlotContainerLogic.Move(_inv, _gear, C.Inventory, 2, C.Gear, Head));  // coin isn't equipment
        Assert.True(SlotContainerLogic.Move(_inv, _gear, C.Inventory, 0, C.Gear, Head));
        Assert.True(SlotContainerLogic.Move(_inv, _gear, C.Inventory, 1, C.Gear, Weapon));
        Assert.Equal((Helmet, Sword, (int?)null, (int?)null), (GearAt(Head), GearAt(Weapon), Inv(0), Inv(1)));
        Assert.Equal("Sword", _gear.EquippedWeapon?.Name);
    }

    [Fact]
    public void Equip_SwapsWithWhatWasEquipped()
    {
        _inv.AddItem(Shirt); _inv.AddItem(RedShirt);
        SlotContainerLogic.Move(_inv, _gear, C.Inventory, 0, C.Gear, Torso);
        Assert.True(SlotContainerLogic.Move(_inv, _gear, C.Inventory, 1, C.Gear, Torso));
        Assert.Equal((RedShirt, Shirt), (GearAt(Torso), Inv(1)));
    }

    [Fact]
    public void AllWeaponStyles_GoInTheWeaponSlot()
    {
        foreach (var weapon in new[] { Sword, Pistol, 8 /* Axe (Tool) */, 567 /* Ice Wand */ })
        {
            var inv = new Inventory(); var gear = new Gear();
            inv.AddItem(weapon);
            Assert.True(SlotContainerLogic.Move(inv, gear, C.Inventory, 0, C.Gear, Weapon));
        }
    }

    [Fact]
    public void UnEquip_IntoEmptySlot_OrSwapWithSameType_Only()
    {
        _inv.AddItem(Pants); SlotContainerLogic.Move(_inv, _gear, C.Inventory, 0, C.Gear, Leg);
        _inv.AddItem(Coin);  // slot 0
        _inv.AddItem(Helmet); // slot 1
        Assert.False(SlotContainerLogic.Move(_inv, _gear, C.Gear, Leg, C.Inventory, 0)); // coin can't go into the leg slot
        Assert.False(SlotContainerLogic.Move(_inv, _gear, C.Gear, Leg, C.Inventory, 1)); // nor a helmet
        Assert.True(SlotContainerLogic.Move(_inv, _gear, C.Gear, Leg, C.Inventory, 7));
        Assert.Equal((Pants, (int?)null), (Inv(7), GearAt(Leg)));
    }

    [Fact]
    public void SwapEquipment_BetweenDifferentSlotTypes_IsRejected()
    {
        _inv.AddItem(Helmet); SlotContainerLogic.Move(_inv, _gear, C.Inventory, 0, C.Gear, Head);
        Assert.False(SlotContainerLogic.Move(_inv, _gear, C.Gear, Head, C.Gear, (int)EquipmentType.Shoe));
        Assert.Equal(Helmet, GearAt(Head));
    }

    [Fact]
    public void Drop_ReturnsWholeStack_AndEmptiesSlot()
    {
        _inv.AddItems(Coin, 5);
        Assert.Equal((Coin, 5, (int?)null, (long?)null), SlotContainerLogic.Drop(_inv, _gear, C.Inventory, 0));
        Assert.Null(Inv(0));
        Assert.Null(SlotContainerLogic.Drop(_inv, _gear, C.Inventory, 0));
    }

    [Fact]
    public void AddItems_StacksOrFillsSlots()
    {
        Assert.Equal(4, _inv.AddItems(Coin, 4));
        Assert.Equal(4, _inv.slots[0].stack_size);
        var small = new Inventory(maxSlots: 2);
        Assert.Equal(2, small.AddItems(Sword, 3)); // only room for two
    }

    [Fact]
    public void Gear_ExportImport_SkipsItemsThatDontFit()
    {
        var ids = Enumerable.Repeat(-1, Items.Gear.SlotCount).ToArray();
        ids[Head] = Helmet;
        ids[Torso] = Sword;  // doesn't fit: skipped
        ids[Weapon] = Pistol;
        var gear = new Gear();
        gear.ImportGear(ids);
        Assert.Equal(Helmet, gear.slots[Head].itemId);
        Assert.Null(gear.slots[Torso].itemId);
        Assert.Equal("Pistol", gear.EquippedWeapon?.Name);
        Assert.Equal(-1, gear.ExportGear()[Torso]);
    }

    [Fact]
    public void Catalog_EquipmentTypesMatchOriginalAssets()
    {
        Assert.Equal(EquipmentType.Head, ItemCatalog.Get(Helmet)!.Slot);
        Assert.Equal(EquipmentType.Torso, ItemCatalog.Get(RedShirt)!.Slot);
        Assert.Equal(EquipmentType.Leg, ItemCatalog.Get(Pants)!.Slot);
        Assert.Equal(WeaponStyle.Gun, ItemCatalog.Get(Pistol)!.Style);
        Assert.False(ItemCatalog.Get(Coin)!.IsEquipment);
        // New items without original art have no icon yet.
        Assert.All(ItemCatalog.All.Where(i => i.Id is not (ItemCatalog.PistolRoundsId or ItemCatalog.KeyId or ItemCatalog.BatId or ItemCatalog.ConcreteBlockId)),
            i => Assert.EndsWith(".png", i.Icon));
    }
}

public class GearPersistenceTests
{
    [Fact]
    public void Gear_SavesAndLoads()
    {
        using var db = GameDatabase.OpenInMemory();
        var acct = db.CreateAccount("kermit", "h")!;
        var c = db.CreateCharacter(acct.Id, "Kermit", "000000").Character!.Id;
        var ids = Enumerable.Repeat(-1, Gear.SlotCount).ToArray();
        ids[(int)EquipmentType.Weapon] = 1515;
        db.SaveGear(c, ids);
        Assert.Equal(ids, db.LoadGear(c, Gear.SlotCount));
    }

    [Fact]
    public void RealSchema_V2Data_SurvivesUpgradeToV3()
    {
        using var temp = new TempDatabase();
        temp.CreateAtVersion(2, conn =>
        {
            Exec(conn, "INSERT INTO accounts (id, username, password_hash, created_at) VALUES (1, 'kermit', 'h', '2026-11-15T00:00:00.0000000Z');");
            Exec(conn, "INSERT INTO characters (id, account_id, name, color, created_at) VALUES (7, 1, 'Kermit', '3cb043', '2026-11-15T00:00:00.0000000Z');");
            Exec(conn, "INSERT INTO inventory_items (character_id, slot, item_id, count) VALUES (7, 0, 1, 4);");
        });
        using var db = GameDatabase.Open(temp.Path);
        Assert.Equal([new SlotRecord(0, 1, 4)], db.LoadInventory(7));
        Assert.All(db.LoadGear(7, Gear.SlotCount), id => Assert.Equal(-1, id));
    }
}
