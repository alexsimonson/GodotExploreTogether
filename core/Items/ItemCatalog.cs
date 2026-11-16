namespace ExploreTogether.Core.Items;

/// <summary>An item definition (port of the Unity Item ScriptableObject; icon/weight omitted for now).</summary>
public sealed record ItemDef(int Id, string Name, string Description, bool Stack);

/// <summary>
/// The item bank, from the original Assets/Resources/Items/*.asset files (same ids, names, descriptions, stack flags).
/// </summary>
public static class ItemCatalog
{
    /// <summary>The original exit key: "Allows you to pass to the next dungeon".</summary>
    public const int DungeonPassId = 999;

    public static readonly IReadOnlyList<ItemDef> All =
    [
        new(0, "Default Item", "Placeholder really", true),
        new(1, "Coin", "Money, Currency, Paper", true),
        new(2, "Health Kit", "This is a health kit", true),
        new(8, "Axe", "", false),
        new(20, "Pants", "Just some pants", false),
        new(21, "Shirt", "Just a shirt", false),
        new(22, "Helmet", "Just a helmet", false),
        new(23, "Blue Shirt", "a blue shirt", false),
        new(24, "Red Shirt", "a red shirt", false),
        new(25, "Green Shirt", "a green shirt", false),
        new(432, "Log", "A tree's log", false),
        new(567, "Ice Wand", "WCasdc", false),
        new(666, "Blood Wand", "asdf", false),
        new(DungeonPassId, "Dungeon Pass", "Allows you to pass to the next dungeon", false),
        new(1337, "Pistol", "A pistol as a gun", false),
        new(1515, "Sword", "Just a sword", false),
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
