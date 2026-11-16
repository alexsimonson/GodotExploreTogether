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
        Assert.Equal([3, 0, 0], doors.Export().Skip(9).Take(3));
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
            if (person.Role == NpcRole.Guard)
            {
                Assert.Equal(22, gear.slots[(int)EquipmentType.Head].itemId); // helmet
                Assert.NotNull(gear.EquippedWeapon);
            }
            else Assert.Null(gear.EquippedWeapon);
        }
    }

    [Fact]
    public void Loot_HoldsEverythingTheyCarriedAndWore()
    {
        var city = CityLayout.Generate(5, 5, 2);
        var doors = new CityDoors(city);
        var guard = CityPopulation.Generate(city, 2).People.First(p => p.Role == NpcRole.Guard);
        var (inv, gear) = NpcLoadout.Generate(guard, city, doors, 2);
        var loot = NpcLoadout.ToLoot(inv, gear);
        var before = inv.slots.Concat(gear.slots).Where(s => s.itemId != null).Select(s => (s.itemId, s.stack_size, s.data)).Order().ToList();
        var after = loot.slots.Where(s => s.itemId != null).Select(s => (s.itemId, s.stack_size, s.data)).Order().ToList();
        Assert.Equal(before, after);
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
}
