namespace ExploreTogether.Core.Items;

/// <summary>One hotbar key's item: which item, and which one exactly when it has a serial number.</summary>
public readonly record struct HotbarEntry(int ItemId, long? Uid);

/// <summary>
/// Six quick-swap keys (1 to 6). Each remembers an item (equipment, usually a weapon), not a slot, so it keeps
/// working as things move around the inventory. Pressing a key equips that item; pressing it again while it's
/// equipped puts it away (back into the inventory), leaving the hands empty.
///
/// An item with a serial number is found by its serial (that pistol, not any pistol); an item without one is
/// found by its type. One item is on one key at most: binding it again moves it.
/// </summary>
public sealed class Hotbar
{
    public const int Size = 6;

    private readonly HotbarEntry?[] _entries = new HotbarEntry?[Size];

    public HotbarEntry? this[int index] => index >= 0 && index < Size ? _entries[index] : null;

    /// <summary>Only equipment can go on the hotbar (there's nothing to do with anything else yet).</summary>
    public static bool CanBind(int itemId) => ItemCatalog.Get(itemId) is { IsEquipment: true };

    /// <summary>Puts the item in that slot on a key (null: clears the key). Returns false if it can't go there.</summary>
    public bool Bind(int index, ItemSlot? slot)
    {
        if (index < 0 || index >= Size) return false;
        if (slot?.itemId is not { } id)
        {
            _entries[index] = null;
            return true;
        }
        if (!CanBind(id)) return false;
        var entry = new HotbarEntry(id, slot.uid);
        for (var i = 0; i < Size; i++)
            if (_entries[i] == entry) _entries[i] = null; // one key per item
        _entries[index] = entry;
        return true;
    }

    /// <summary>Where the key's item is now: its container and slot, or null if it isn't carried.</summary>
    public (ItemContainer Container, int Slot)? Find(int index, Inventory inventory, Gear gear)
    {
        if (this[index] is not { } e) return null;
        static bool Same(ItemSlot s, HotbarEntry e) => s.itemId == e.ItemId && (e.Uid == null || s.uid == e.Uid);
        foreach (var s in gear.slots)
            if (Same(s, e)) return (ItemContainer.Gear, s.index);
        foreach (var s in inventory.slots)
            if (Same(s, e)) return (ItemContainer.Inventory, s.index);
        return null;
    }

    /// <summary>
    /// Presses the key: equips the item (swapping out whatever was in its gear slot), or puts it away if it's already
    /// equipped. Returns false if nothing changed (not carried, or nowhere to put it away).
    /// </summary>
    public bool Use(int index, Inventory inventory, Gear gear) =>
        MoveFor(index, inventory, gear) is { } m && SlotContainerLogic.Move(inventory, gear, m.From, m.FromSlot, m.To, m.ToSlot);

    /// <summary>The inventory move pressing the key makes (see <see cref="Use"/>), or null if there's none.</summary>
    public (ItemContainer From, int FromSlot, ItemContainer To, int ToSlot)? MoveFor(int index, Inventory inventory, Gear gear)
    {
        if (Find(index, inventory, gear) is not { } at) return null;
        if (at.Container == ItemContainer.Gear)
        {
            var free = inventory.FindEmptySlot();
            return free >= 0 ? (ItemContainer.Gear, at.Slot, ItemContainer.Inventory, free) : null;
        }
        return ItemCatalog.Get(inventory.slots[at.Slot].itemId!.Value)?.Slot is { } type
            ? (ItemContainer.Inventory, at.Slot, ItemContainer.Gear, (int)type)
            : null;
    }

    /// <summary>The keys as (key index, entry) for saving.</summary>
    public IEnumerable<(int Index, HotbarEntry Entry)> Occupied()
    {
        for (var i = 0; i < Size; i++)
            if (_entries[i] is { } e) yield return (i, e);
    }

    /// <summary>Restores saved keys (unknown or unsuitable items are skipped).</summary>
    public void Load(IEnumerable<(int Index, HotbarEntry Entry)> saved)
    {
        Array.Clear(_entries);
        foreach (var (i, e) in saved)
            if (i >= 0 && i < Size && CanBind(e.ItemId)) _entries[i] = e;
    }

    /// <summary>For the client: item id per key (-1 empty) and serial per key (-1 none).</summary>
    public (int[] Ids, long[] Uids) Export() =>
        (_entries.Select(e => e?.ItemId ?? -1).ToArray(), _entries.Select(e => e?.Uid ?? -1).ToArray());
}
