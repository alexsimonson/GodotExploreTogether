using ExploreTogether.Core.Items;

namespace ExploreTogether.Core.City;

/// <summary>
/// What a city resident carries and wears, using the same inventory (28 slots) and gear (12 slots) as players.
/// - Keys: one to their home's door and one to their workplace's (police: their station).
/// - Clothes: a shirt and pants; police a helmet and a blue shirt.
/// - Weapons (in the weapon slot, so they're what the resident fights with, and what a looter finds):
///   police carry a pistol or a bat, security a bat, pistol or pump shotgun, and armed civilians, workers and
///   shopkeepers (the Armed trait) whatever they keep at hand: a bat, a pistol, or a shotgun (shopkeepers favor
///   shotguns). Guns come loaded, with a few spare rounds.
/// - Some coins, sometimes a health kit or a lockpick; warehouse workers carry an axe (and often a concrete block).
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

        void Gun(int gunId, int spareMin, int spareMax)
        {
            Wear(gunId);
            var ammo = ItemCatalog.Get(gunId)!.AmmoItemId!.Value;
            inventory.AddItems(ammo, spareMin + random.Next(spareMax - spareMin + 1));
        }

        inventory.AddItems(ItemCatalog.KeyId, 1, doors.LockCode(person.Home));
        if (person.Work >= 0 && person.Work != person.Home)
            inventory.AddItems(ItemCatalog.KeyId, 1, doors.LockCode(person.Work));

        var uniformed = person.Role is NpcRole.Police or NpcRole.Security;
        Wear(uniformed ? 23 : Shirts[random.Next(Shirts.Length)]); // police and security in blue
        Wear(PantsId);
        if (person.Role == NpcRole.Police) Wear(HelmetId);

        var roll = random.Next(100);
        switch (person.Role)
        {
            case NpcRole.Police:
                if (roll < 55) Gun(PistolId, 10, 30);
                else if (roll < 85) Wear(ItemCatalog.BatId);
                else Wear(SwordId);
                break;
            case NpcRole.Security:
                if (roll < 45) Wear(ItemCatalog.BatId);
                else if (roll < 75) Gun(PistolId, 5, 20);
                else Gun(ItemCatalog.PumpShotgunId, 4, 10);
                break;
            default:
                if (!person.Has(NpcTraits.Armed)) break;
                var shotgunOdds = person.Role == NpcRole.Shopkeeper ? 50 : 15;
                if (roll < shotgunOdds)
                    Gun(random.Next(2) == 0 ? ItemCatalog.DoubleBarrelId : ItemCatalog.PumpShotgunId, 2, 8);
                else if (roll < shotgunOdds + 30) Gun(PistolId, 0, 10);
                else Wear(ItemCatalog.BatId);
                break;
        }

        if (person.Role == NpcRole.Worker && city.Parcels[person.Work].Building == BuildingType.Warehouse)
        {
            inventory.AddItems(AxeId, 1);
            if (random.Next(100) < 50) inventory.AddItems(ItemCatalog.ConcreteBlockId, 1);
        }

        var coins = random.Next(0, 26) + (person.Role == NpcRole.Shopkeeper ? 15 : 0);
        if (coins > 0) inventory.AddItems(CoinId, coins);
        if (random.Next(100) < 20) inventory.AddItems(HealthKitId, 1);
        if (random.Next(100) < (person.Role == NpcRole.Police ? 25 : 6)) inventory.AddItems(ItemCatalog.LockpickId, 1 + random.Next(3));
        return (inventory, gear);
    }
}
