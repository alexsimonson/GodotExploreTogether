namespace ExploreTogether.Core.Items;

/// <summary>One inventory slot (port of ItemSlot). ItemId is null when empty.</summary>
public sealed class ItemSlot
{
    public int index;
    public int? itemId;
    public int stack_size;
    /// <summary>Per-item state that travels with the item (for a gun: the rounds loaded). Null for stackables.</summary>
    public int? data;
}

/// <summary>
/// Port of the Unity Inventory's slot logic: a fixed number of slots (28 by default); a stackable
/// item goes onto an existing stack of the same item, otherwise into the first empty slot.
/// (Like the original, max_stack_size isn't enforced.) Events/UI are handled by the caller.
/// </summary>
public sealed class Inventory
{
    public const int DefaultMaxSlots = 28;

    public int max_slots;
    public ItemSlot[] slots;

    public Inventory(int maxSlots = DefaultMaxSlots)
    {
        max_slots = maxSlots == 0 ? DefaultMaxSlots : maxSlots;
        slots = new ItemSlot[max_slots];
        for (int i = 0; i < max_slots; i++)
            slots[i] = new ItemSlot { index = i, itemId = null, stack_size = 0 };
    }

    /// <summary>
    /// Returns the slot index the item went into, or -1 if the inventory is full. For non-stackable items,
    /// data is that copy's state (e.g. rounds loaded); null means a fresh copy.
    /// </summary>
    public int AddItem(int itemId, int? data = null)
    {
        var def = ItemCatalog.Get(itemId);
        if (def == null) return -1;
        if (def.Stack)
        {
            int slot_index = FindItemInSlot(itemId);
            if (slot_index >= 0)
            {
                slots[slot_index].stack_size++;
                return slot_index;
            }
        }
        int empty_slot_index = FindEmptySlot();
        if (empty_slot_index >= 0)
        {
            slots[empty_slot_index].itemId = itemId;
            slots[empty_slot_index].stack_size = 1;
            slots[empty_slot_index].data = def.Stack ? null : data ?? def.InitialData;
            return empty_slot_index;
        }
        return -1;
    }

    /// <summary>
    /// Adds several of an item (e.g. picking up a dropped stack). Stackables go onto one stack;
    /// others take a slot each. Returns how many were added (fewer if the inventory filled up).
    /// </summary>
    public int AddItems(int itemId, int count, int? data = null)
    {
        var def = ItemCatalog.Get(itemId);
        if (def == null || count <= 0) return 0;
        if (def.Stack)
        {
            int slot = AddItem(itemId);
            if (slot < 0) return 0;
            slots[slot].stack_size += count - 1;
            return count;
        }
        int added = 0;
        while (added < count && AddItem(itemId, data) >= 0) added++;
        return added;
    }

    /// <summary>Empties a slot (the whole stack), like the original RemoveItem. Returns the removed item id.</summary>
    public int? RemoveItem(int index)
    {
        var removed = slots[index].itemId;
        slots[index].itemId = null;
        slots[index].stack_size = 0;
        slots[index].data = null;
        return removed;
    }

    public int FindItemInSlot(int itemId)
    {
        for (int i = 0; i < max_slots; i++)
            if (slots[i].itemId == itemId) return i;
        return -1;
    }

    public int FindEmptySlot()
    {
        for (int i = 0; i < max_slots; i++)
            if (slots[i].itemId == null) return i;
        return -1;
    }

    public bool Contains(int itemId) => FindItemInSlot(itemId) >= 0;

    /// <summary>Occupied slots as (slot, item, count, data), e.g. for saving or sending to the client.</summary>
    public IEnumerable<(int Slot, int ItemId, int Count, int? Data)> Occupied() =>
        slots.Where(s => s.itemId != null).Select(s => (s.index, s.itemId!.Value, s.stack_size, s.data));

    /// <summary>Restores saved slots (unknown items and out-of-range slots are skipped).</summary>
    public void Load(IEnumerable<(int Slot, int ItemId, int Count, int? Data)> saved)
    {
        foreach (var (slot, itemId, count, data) in saved)
        {
            if (slot < 0 || slot >= max_slots || ItemCatalog.Get(itemId) is not { } def || count <= 0) continue;
            slots[slot].itemId = itemId;
            slots[slot].stack_size = count;
            slots[slot].data = def.Stack ? null : data ?? def.InitialData;
        }
    }

    public void Load(IEnumerable<(int Slot, int ItemId, int Count)> saved) =>
        Load(saved.Select(s => (s.Slot, s.ItemId, s.Count, (int?)null)));

    /// <summary>How many of an item the inventory holds in total (across stacks).</summary>
    public int CountOf(int itemId) => slots.Where(s => s.itemId == itemId).Sum(s => s.stack_size);
}
