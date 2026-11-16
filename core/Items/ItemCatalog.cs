namespace ExploreTogether.Core.Items;

/// <summary>Port of Equipment.Type: which gear slot an item goes in. Same order and values as the original.</summary>
public enum EquipmentType
{
    Head, Neck, Torso, Leg, Glove, Shoe, Back, Eye, Coat, Ring, Weapon, Offhand,
}

/// <summary>Port of Weapon.Style.</summary>
public enum WeaponStyle { Melee, Gun, Magic }

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
    int Damage = 0)
{
    /// <summary>The original's valid_equipment_types: Equipment and every Weapon subclass.</summary>
    public bool IsEquipment => Class != ItemClass.Item;
    public bool IsWeapon => Slot == EquipmentType.Weapon;
}

/// <summary>
/// The item bank, from the original Assets/Resources/Items/*.asset files: same ids, names, descriptions,
/// stack flags, classes, equipment types, weapon styles and damage (Weapon's default of 10 where the asset
/// didn't override it), and icons (Assets/Resources/UI).
/// </summary>
public static class ItemCatalog
{
    /// <summary>The original exit key: "Allows you to pass to the next dungeon".</summary>
    public const int DungeonPassId = 999;

    public static readonly IReadOnlyList<ItemDef> All =
    [
        new(0, "Default Item", "Placeholder really", true, "default_item.png"),
        new(1, "Coin", "Money, Currency, Paper", true, "coin.png"),
        new(2, "Health Kit", "This is a health kit", true, "health_kit.png"),
        new(8, "Axe", "", false, "axe.png", ItemClass.Tool, EquipmentType.Weapon, WeaponStyle.Melee, 10),
        new(20, "Pants", "Just some pants", false, "pants.png", ItemClass.Equipment, EquipmentType.Leg),
        new(21, "Shirt", "Just a shirt", false, "shirt.png", ItemClass.Equipment, EquipmentType.Torso),
        new(22, "Helmet", "Just a helmet", false, "helmet.png", ItemClass.Equipment, EquipmentType.Head),
        new(23, "Blue Shirt", "a blue shirt", false, "shirt_blue.png", ItemClass.Equipment, EquipmentType.Torso),
        new(24, "Red Shirt", "a red shirt", false, "shirt_red.png", ItemClass.Equipment, EquipmentType.Torso),
        new(25, "Green Shirt", "a green shirt", false, "shirt_green.png", ItemClass.Equipment, EquipmentType.Torso),
        new(432, "Log", "A tree's log", false, "log.png"),
        new(567, "Ice Wand", "WCasdc", false, "IceWand.png", ItemClass.Magic, EquipmentType.Weapon, WeaponStyle.Magic, 10),
        new(666, "Blood Wand", "asdf", false, "BloodWand.png", ItemClass.Magic, EquipmentType.Weapon, WeaponStyle.Magic, 0),
        new(DungeonPassId, "Dungeon Pass", "Allows you to pass to the next dungeon", false, "dungeonpass.png"),
        new(1337, "Pistol", "A pistol as a gun", false, "BasicPistol.png", ItemClass.Gun, EquipmentType.Weapon, WeaponStyle.Gun, 10),
        new(1515, "Sword", "Just a sword", false, "sword.png", ItemClass.Melee, EquipmentType.Weapon, WeaponStyle.Melee, 10),
    ];

    private static readonly Dictionary<int, ItemDef> ById = All.ToDictionary(i => i.Id);

    public static ItemDef? Get(int id) => ById.GetValueOrDefault(id);

    public static string NameOf(int id) => Get(id)?.Name ?? $"Unknown item {id}";

    /// <summary>Port of Manager.GenerateItem: a random item from the bank, never the Dungeon Pass.</summary>
    public static ItemDef GenerateItem(Random random)
    {
        ItemDef? generated = null;
        while (generated == null || generated.Id == DungeonPassId)
            generated = All[random.Next(0, All.Count)];
        return generated;
    }
}
