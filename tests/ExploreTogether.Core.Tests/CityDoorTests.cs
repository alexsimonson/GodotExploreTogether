using ExploreTogether.Core.City;
using ExploreTogether.Core.Items;

namespace ExploreTogether.Core.Tests;

public class CityDoorTests
{
    [Fact]
    public void LockCodes_AreUniquePerDoor_AndMapBackToTheirDoor()
    {
        var city = CityLayout.Generate(10, 10, 3);
        var doors = new CityDoors(city);
        var codes = Enumerable.Range(0, doors.Count).Select(doors.LockCode).ToList();
        Assert.Equal(codes.Count, codes.Distinct().Count());
        for (var i = 0; i < doors.Count; i++) Assert.Equal(i, doors.ParcelForKey(doors.LockCode(i)));
        // Another city's keys fit nothing here.
        var other = new CityDoors(CityLayout.Generate(10, 10, 4));
        Assert.Equal(-1, doors.ParcelForKey(other.LockCode(5)));
    }

    [Fact]
    public void LockedDoors_DontOpen_AndOpenDoorsDontLock()
    {
        var doors = new CityDoors(CityLayout.Generate(5, 5, 1));
        var changes = 0;
        doors.Changed += _ => changes++;
        Assert.True(doors.Lock(3));
        Assert.False(doors.Open(3));
        doors.Unlock(3);
        Assert.True(doors.Open(3));
        Assert.False(doors.Lock(3));
        Assert.False(doors.IsLocked(3));
        doors.Close(3);
        Assert.Equal(4, changes); // lock, unlock, open, close
        Assert.Equal([3, 0], doors.Export().Skip(6).Take(2)); // (parcel, flags): closed, unlocked
    }

    [Fact]
    public void HasKey_ChecksTheKeysData()
    {
        var doors = new CityDoors(CityLayout.Generate(5, 5, 1));
        var inv = new Inventory();
        inv.AddItems(ItemCatalog.KeyId, 1, doors.LockCode(7));
        Assert.True(doors.HasKey(inv, 7));
        Assert.False(doors.HasKey(inv, 8));
    }
}

public class NpcLoadoutTests
{
    [Fact]
    public void Residents_CarryKeysToWhereTheyBelong_AndDressForTheirRole()
    {
        var city = CityLayout.Generate(5, 5, 2);
        var doors = new CityDoors(city);
        foreach (var person in CityPopulation.Generate(city, 2).People)
        {
            var (inv, gear) = NpcLoadout.Generate(person, city, doors, 2);
            Assert.True(doors.HasKey(inv, person.Home), $"{person.Name} has no key to their home");
            if (person.Work >= 0) Assert.True(doors.HasKey(inv, person.Work));
            var keys = inv.slots.Where(s => s.itemId == ItemCatalog.KeyId).Select(s => doors.ParcelForKey(s.data!.Value)).ToList();
            Assert.All(keys, k => Assert.True(k == person.Home || k == person.Work, $"{person.Name} has a key to {k}"));
            Assert.NotNull(gear.slots[(int)EquipmentType.Torso].itemId);
            if (person.Role == NpcRole.Police) Assert.Equal(22, gear.slots[(int)EquipmentType.Head].itemId); // helmet
            if (person.Role is NpcRole.Police or NpcRole.Security) Assert.NotNull(gear.EquippedWeapon);
            else if (!person.Has(NpcTraits.Armed)) Assert.Null(gear.EquippedWeapon);
            if (gear.EquippedWeapon is { Class: ItemClass.Gun } gun) Assert.Equal(gun.MagazineSize, gear.WeaponSlot.data); // loaded
        }
    }
}

public class LootRulesTests
{
    private static (Inventory Inv, Gear Gear, Inventory Loot) Setup()
    {
        var inv = new Inventory();
        var gear = new Gear();
        var loot = new Inventory(40);
        loot.AddItems(1515, 1);  // sword, slot 0
        loot.AddItems(1, 12);    // coins, slot 1
        loot.AddItems(21, 1);    // shirt, slot 2
        inv.AddItems(8, 1);      // axe in inventory slot 0
        inv.AddItems(1, 3);      // coins in inventory slot 1
        return (inv, gear, loot);
    }

    [Fact]
    public void Taking_IntoEmptySlotsOrOntoTheSameStack_IsAllowed()
    {
        var (inv, gear, loot) = Setup();
        Assert.True(SlotContainerLogic.IsAllowedForLoot(inv, gear, loot, ItemContainer.Loot, 0, ItemContainer.Inventory, 5));
        Assert.True(SlotContainerLogic.IsAllowedForLoot(inv, gear, loot, ItemContainer.Loot, 1, ItemContainer.Inventory, 1));
        Assert.True(SlotContainerLogic.IsAllowedForLoot(inv, gear, loot, ItemContainer.Loot, 2, ItemContainer.Gear, (int)EquipmentType.Torso));
        Assert.True(SlotContainerLogic.Move(inv, gear, ItemContainer.Loot, 1, ItemContainer.Inventory, 1, loot: loot));
        Assert.Equal(15, inv.slots[1].stack_size);
        Assert.Null(loot.slots[1].itemId);
    }

    [Fact]
    public void Nothing_GoesIntoABody()
    {
        var (inv, gear, loot) = Setup();
        Assert.False(SlotContainerLogic.IsAllowedForLoot(inv, gear, loot, ItemContainer.Inventory, 0, ItemContainer.Loot, 10));
        // Taking onto an occupied slot would swap the axe into the body.
        Assert.False(SlotContainerLogic.IsAllowedForLoot(inv, gear, loot, ItemContainer.Loot, 0, ItemContainer.Inventory, 0));
        Assert.False(SlotContainerLogic.IsAllowedForLoot(inv, gear, loot, ItemContainer.Loot, 5, ItemContainer.Inventory, 6)); // empty
        Assert.True(SlotContainerLogic.IsAllowedForLoot(inv, gear, loot, ItemContainer.Inventory, 0, ItemContainer.Inventory, 7)); // no loot involved
    }

    [Fact]
    public void TheBodysEquipment_CanBeTakenAndWorn_ButNothingGoesOntoIt()
    {
        var (inv, gear, loot) = Setup();
        var bodyGear = new Gear();
        bodyGear.slots[(int)EquipmentType.Head].itemId = 22; // helmet
        bodyGear.slots[(int)EquipmentType.Head].stack_size = 1;
        var head = (int)EquipmentType.Head;
        Assert.True(SlotContainerLogic.IsAllowedForLoot(inv, gear, loot, ItemContainer.LootGear, head, ItemContainer.Gear, head, bodyGear));
        Assert.True(SlotContainerLogic.Move(inv, gear, ItemContainer.LootGear, head, ItemContainer.Gear, head, loot: loot, lootGear: bodyGear));
        Assert.Equal(22, gear.slots[head].itemId);
        Assert.Null(bodyGear.slots[head].itemId);
        Assert.False(SlotContainerLogic.IsAllowedForLoot(inv, gear, loot, ItemContainer.Gear, head, ItemContainer.LootGear, head, bodyGear));
    }
}

public class BodyDragTests
{
    [Fact]
    public void TheBody_TrailsOnTheRope_AndPointsItsHeadAtThePlayer()
    {
        // Player 5 m north of the body: the body is pulled to 2.1 m behind them, head north (+Z: yaw 0).
        var (x, z, yaw) = BodyDrag.Follow(0, 0, 0, 5);
        Assert.Equal(0, x, 3);
        Assert.Equal(5 - BodyDrag.RopeLength, z, 3);
        Assert.Equal(0, yaw, 3);
        // Player east: head toward +X (yaw 90 degrees).
        Assert.Equal(MathF.PI / 2, BodyDrag.Follow(0, 0, 4, 0).Yaw, 3);
    }

    [Fact]
    public void WalkingBackToTheBody_DoesntPushIt()
    {
        var (x, z, _) = BodyDrag.Follow(1, 1, 1.5f, 1.5f);
        Assert.Equal((1f, 1f), (x, z));
    }

    [Fact]
    public void TurningACorner_SwingsTheBodyAround()
    {
        // Walk north 5 m, then east 5 m: the body follows the corner rather than staying south of the player.
        float bx = 0, bz = -BodyDrag.RopeLength, px = 0, pz = 0;
        for (var i = 0; i < 50; i++) { pz += 0.1f; (bx, bz, _) = BodyDrag.Follow(bx, bz, px, pz); }
        for (var i = 0; i < 50; i++) { px += 0.1f; (bx, bz, _) = BodyDrag.Follow(bx, bz, px, pz); }
        var d = MathF.Sqrt((px - bx) * (px - bx) + (pz - bz) * (pz - bz));
        Assert.Equal(BodyDrag.RopeLength, d, 2);
        Assert.True(bx > 2.5f, $"body at ({bx:0.0}, {bz:0.0}) didn't come round the corner");
    }
}

public class KnockOutLootTests
{
    [Fact]
    public void BluntWeapons_KnockOut_AndAreNotRandomLoot()
    {
        Assert.True(ItemCatalog.Get(ItemCatalog.BatId)!.KnocksOut);
        Assert.True(ItemCatalog.Get(ItemCatalog.ConcreteBlockId)!.KnocksOut);
        Assert.False(ItemCatalog.Get(1515)!.KnocksOut);
        Assert.Equal(ItemClass.Melee, ItemCatalog.Get(ItemCatalog.BatId)!.Class);
    }
}

public class DebugSupplyTests
{
    [Fact]
    public void Supply_OffersEveryUsableItem_ButNoPassOrKeys()
    {
        Assert.DoesNotContain(DebugSupply.Items, i => i.Id is ItemCatalog.DungeonPassId or ItemCatalog.KeyId);
        Assert.Contains(DebugSupply.Items, i => i.Id == ItemCatalog.BatId);
        Assert.Equal(DebugSupply.Items.Count * 4, DebugSupply.Quads().Length);
        Assert.Equal(30, DebugSupply.CountPerTake(ItemCatalog.Get(ItemCatalog.PistolRoundsId)!));
        Assert.Equal(1, DebugSupply.CountPerTake(ItemCatalog.Get(1515)!));
    }
}
