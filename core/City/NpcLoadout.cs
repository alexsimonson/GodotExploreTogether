using ExploreTogether.Core.Items;

namespace ExploreTogether.Core.City;

/// <summary>
/// What a city resident carries and wears, using the same inventory (28 slots) and gear (12 slots) as players.
/// - Keys: one to their home's door and one to their workplace's (guards: their guardhouse).
/// - Clothes: a shirt and pants; guards a helmet too.
/// - Guards are armed: a sword, or now and then a pistol with spare rounds.
/// - Some coins, sometimes a health kit; warehouse workers carry an axe.
/// Nothing has a serial yet: items get one when a player takes them (like dungeon loot).
/// </summary>
public static class NpcLoadout
{
    private static readonly int[] Shirts = [21, 23, 24, 25]; // Shirt, Blue, Red, Green
    private const int CoinId = 1, HealthKitId = 2, AxeId = 8, PantsId = 20, HelmetId = 22, PistolId = 1337, SwordId = 1515;

    public static (Inventory Inventory, Gear Gear) Generate(Person person, CityLayout city, CityDoors doors, int seed)
    {
        var random = new Random(unchecked(seed * 977 + person.Id * 7907 + 3));
        var inventory = new Inventory();
        var gear = new Gear();

        void Wear(int itemId)
        {
            var def = ItemCatalog.Get(itemId)!;
            var slot = gear.slots[(int)def.Slot!.Value];
            slot.itemId = itemId;
            slot.stack_size = 1;
            slot.data = def.InitialData;
        }

        inventory.AddItems(ItemCatalog.KeyId, 1, doors.LockCode(person.Home));
        if (person.Work >= 0 && person.Work != person.Home)
            inventory.AddItems(ItemCatalog.KeyId, 1, doors.LockCode(person.Work));

        Wear(person.Role == NpcRole.Guard ? 23 : Shirts[random.Next(Shirts.Length)]); // guards in blue
        Wear(PantsId);
        if (person.Role == NpcRole.Guard)
        {
            Wear(HelmetId);
            if (random.Next(100) < 30)
            {
                Wear(PistolId);
                inventory.AddItems(ItemCatalog.PistolRoundsId, 10 + random.Next(21));
            }
            else Wear(SwordId);
        }
        if (person.Role == NpcRole.Worker && city.Parcels[person.Work].Building == BuildingType.Warehouse)
            inventory.AddItems(AxeId, 1);

        var coins = random.Next(0, 26);
        if (coins > 0) inventory.AddItems(CoinId, coins);
        if (random.Next(100) < 20) inventory.AddItems(HealthKitId, 1);
        return (inventory, gear);
    }

    /// <summary>
    /// Everything a body carries, as one container to loot: the inventory's items, then the gear's.
    /// </summary>
    public static Inventory ToLoot(Inventory inventory, Gear gear)
    {
        var loot = new Inventory(Inventory.DefaultMaxSlots + Gear.SlotCount);
        foreach (var s in inventory.slots.Concat(gear.slots).Where(s => s.itemId != null))
        {
            var target = loot.slots[loot.FindEmptySlot()];
            (target.itemId, target.stack_size, target.data, target.uid) = (s.itemId, s.stack_size, s.data, s.uid);
        }
        return loot;
    }
}
