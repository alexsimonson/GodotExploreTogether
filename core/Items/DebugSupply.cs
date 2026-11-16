namespace ExploreTogether.Core.Items;

/// <summary>
/// The hub's debug supply (dev servers only, --dev-supply): one slot per item, never runs out. Taking makes a fresh
/// copy; dropping one of your items onto it deletes that item. Not offered: the Dungeon Pass (it belongs to a
/// dungeon), keys (a key needs a lock) and retired items.
/// </summary>
public static class DebugSupply
{
    public static IReadOnlyList<ItemDef> Items { get; } =
        ItemCatalog.All.Where(i => !i.Retired && i.Id is not (ItemCatalog.DungeonPassId or ItemCatalog.KeyId)).ToList();

    /// <summary>How many one take gives: one of a non-stackable; a few loot-sized handfuls of a stackable.</summary>
    public static int CountPerTake(ItemDef def) => !def.Stack ? 1 : Math.Max(10, def.LootCount * 3);

    /// <summary>What the supply shows, as (slot, item, count, data) quads. Stackables show what one take gives.</summary>
    public static int[] Quads() =>
        Items.SelectMany((def, slot) => new[] { slot, def.Id, CountPerTake(def), -1 }).ToArray();

    public static ItemDef? At(int slot) => slot >= 0 && slot < Items.Count ? Items[slot] : null;
}
