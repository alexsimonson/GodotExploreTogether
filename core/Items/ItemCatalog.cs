namespace ExploreTogether.Core.Items;

/// <summary>Port of Equipment.Type: which gear slot an item goes in. Same order and values as the original.</summary>
public enum EquipmentType
{
    Head, Neck, Torso, Leg, Glove, Shoe, Back, Eye, Coat, Ring, Weapon, Offhand,
}

/// <summary>Port of Weapon.Style.</summary>
public enum WeaponStyle { Melee, Gun, Magic }

/// <summary>Port of Tool.Role: what kind of tool it is. A resource needs a tool of a particular role (a tree needs an axe).</summary>
public enum ToolRole { Axe, Pickaxe, Shovel, Rake, Screwdriver, Hammer }

/// <summary>The Unity class an item asset was made from (Item, Equipment, or a Weapon subclass).</summary>
public enum ItemClass { Item, Equipment, Melee, Gun, Magic, Tool }

/// <summary>
/// An item definition: port of the Item ScriptableObject and its subclasses
/// (Equipment adds a slot type; Weapon adds a style and damage). Icon is a file in assets/icons.
/// </summary>
public sealed record ItemDef(
    int Id, string Name, string Description, bool Stack, string Icon,
    ItemClass Class = ItemClass.Item,
    EquipmentType? Slot = null,
    WeaponStyle? Style = null,
    int Damage = 0,
    int? MagazineSize = null,  // guns: rounds the weapon holds (loaded rounds travel with the weapon)
    int? AmmoItemId = null,    // guns: which item reloads it
    int LootCount = 1,         // how many make up one pickup when generated as loot
    bool Retired = false,      // no longer generated as loot, but still loads from saves (never delete an id)
    ToolRole? Role = null)     // tools only: which resources it can gather from
{
    /// <summary>The original's valid_equipment_types: Equipment and every Weapon subclass.</summary>
    public bool IsEquipment => Class != ItemClass.Item;
    public bool IsWeapon => Slot == EquipmentType.Weapon;

    /// <summary>Per-item state a fresh copy starts with: a gun comes loaded (the original's magazineRounds = 10).</summary>
    public int? InitialData => MagazineSize;
}

/// <summary>
/// The item bank, from the original Assets/Resources/Items/*.asset files: same ids, names, descriptions,
/// stack flags, classes, equipment types, weapon styles and damage (Weapon's default of 10 where the asset
/// didn't override it), and icons (Assets/Resources/UI).
///
/// <para><b>Never remove an id from this list.</b> Loading skips unknown ids and the next save rewrites the
/// character's rows without them, so removing an id deletes every saved copy. To take an item out of the
/// game, set <c>Retired: true</c> instead. <c>ItemTests.Catalog_KeepsEveryShippedId</c> guards this.</para>
/// </summary>
public static class ItemCatalog
{
    /// <summary>The original exit key: "Allows you to pass to the next dungeon".</summary>
    public const int DungeonPassId = 999;

    public const int PistolRoundsId = 1338;

    /// <summary>What trees give.</summary>
    public const int LogId = 432;

    /// <summary>
    /// New (not in the original): a key to one city door. Its data is the lock's code (see CityDoors.LockCode),
    /// so every key fits exactly one door. Never generated as random loot (a key without a lock is useless).
    /// </summary>
    public const int KeyId = 1700;

    public static readonly IReadOnlyList<ItemDef> All =
    [
        new(0, "Default Item", "Placeholder really", true, "default_item.png"),
        new(1, "Coin", "Money, Currency, Paper", true, "coin.png"),
        new(2, "Health Kit", "This is a health kit", true, "health_kit.png"),
        new(8, "Axe", "", false, "axe.png", ItemClass.Tool, EquipmentType.Weapon, WeaponStyle.Melee, 10, Role: ToolRole.Axe),
        new(20, "Pants", "Just some pants", false, "pants.png", ItemClass.Equipment, EquipmentType.Leg),
        new(21, "Shirt", "Just a shirt", false, "shirt.png", ItemClass.Equipment, EquipmentType.Torso),
        new(22, "Helmet", "Just a helmet", false, "helmet.png", ItemClass.Equipment, EquipmentType.Head),
        new(23, "Blue Shirt", "a blue shirt", false, "shirt_blue.png", ItemClass.Equipment, EquipmentType.Torso),
        new(24, "Red Shirt", "a red shirt", false, "shirt_red.png", ItemClass.Equipment, EquipmentType.Torso),
        new(25, "Green Shirt", "a green shirt", false, "shirt_green.png", ItemClass.Equipment, EquipmentType.Torso),
        // Stackable here (the original asset wasn't): trees give logs over and over, and one slot per log
        // would fill the 28-slot inventory after a few swings.
        new(LogId, "Log", "A tree's log", true, "log.png"),
        new(567, "Ice Wand", "WCasdc", false, "IceWand.png", ItemClass.Magic, EquipmentType.Weapon, WeaponStyle.Magic, 10),
        new(666, "Blood Wand", "asdf", false, "BloodWand.png", ItemClass.Magic, EquipmentType.Weapon, WeaponStyle.Magic, 0),
        new(DungeonPassId, "Dungeon Pass", "Allows you to pass to the next dungeon", false, "dungeonpass.png"),
        new(1337, "Pistol", "A pistol as a gun", false, "BasicPistol.png", ItemClass.Gun, EquipmentType.Weapon, WeaponStyle.Gun, 10,
            MagazineSize: 10, AmmoItemId: PistolRoundsId),
        // New (not in the original): the original Gun kept a bulletCount reserve on the shared asset;
        // here reserve ammo is an item you carry. No icon yet.
        new(PistolRoundsId, "Pistol Rounds", "Ammunition for the pistol", true, "", LootCount: 10),
        new(1515, "Sword", "Just a sword", false, "sword.png", ItemClass.Melee, EquipmentType.Weapon, WeaponStyle.Melee, 10),
        new(KeyId, "Key", "Opens one particular door", false, ""),
    ];

    private static readonly Dictionary<int, ItemDef> ById = All.ToDictionary(i => i.Id);

    public static ItemDef? Get(int id) => ById.GetValueOrDefault(id);

    public static string NameOf(int id) => Get(id)?.Name ?? $"Unknown item {id}";

    /// <summary>Port of Manager.GenerateItem: a random item from the bank, never the Dungeon Pass, a key or a retired item.</summary>
    public static ItemDef GenerateItem(Random random)
    {
        ItemDef? generated = null;
        while (generated == null || generated.Id is DungeonPassId or KeyId || generated.Retired)
            generated = All[random.Next(0, All.Count)];
        return generated;
    }
}
