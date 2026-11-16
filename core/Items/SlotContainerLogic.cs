namespace ExploreTogether.Core.Items;

/// <summary>
/// Where a slot is. Storage is the hub storage (port of Storage.storage_inventory), an inventory of its own.
/// New:
/// - Loot and LootGear: the inventory and the equipment of someone who's down (dead or knocked out) being searched.
///   The server only allows taking from them.
/// - Supply: the hub's debug supply (dev servers only): every item, unlimited; dropping an item onto it deletes it.
/// </summary>
public enum ItemContainer { Inventory, Gear, Storage, Loot, LootGear, Supply }

/// <summary>
/// Port of SlotContainer.OnDrop: what happens when an item is dragged from one slot onto another.
/// The four cases and their rules are the original's:
///   inventory -> inventory : SwapItem       (swap the two slots, either may be empty)
///   inventory -> gear      : EquipItem      (only equipment whose type matches the gear slot; swaps with what was equipped)
///   gear -> inventory      : UnEquipItem    (into an empty slot, or swap with equipment of the same type)
///   gear -> gear           : SwapEquipment
/// Dragging from an empty slot does nothing.
///
/// Storage follows the inventory's rules, as in the original (it was an Inventory with isStorage set, and
/// SlotContainer ran the same SwapItem/EquipItem/UnEquipItem for it): storage and inventory swap freely
/// with each other, equipment can be equipped straight from storage and unequipped into it.
///
/// Deliberate differences from the original (for review):
/// - SwapEquipment swapped two gear slots without any type check (e.g. a helmet could end up in
///   the shoe slot). Here it only swaps if each item fits the other slot, which within one player's
///   gear means it never applies; the original's comment already suspected it wasn't needed.
/// - Dropping a stack onto a stack of the same stackable item merges them (the original swapped them,
///   which did nothing visible). Needed for storing gathered resources onto an existing stack.
/// - Everything else (including keeping stack sizes with the item) matches.
/// </summary>
public static class SlotContainerLogic
{
    /// <summary>Applies a drag from (from, fromSlot) to (to, toSlot). Returns false if nothing changed.</summary>
    /// <param name="storage">The hub storage, or null when it isn't available (then moves involving it fail).</param>
    /// <param name="loot">The inventory of the body being searched, or null (then moves involving it fail).</param>
    /// <param name="lootGear">The equipment of the body being searched, or null.</param>
    public static bool Move(Inventory inventory, Gear gear, ItemContainer from, int fromSlot, ItemContainer to, int toSlot,
        Inventory? storage = null, Inventory? loot = null, Gear? lootGear = null)
    {
        var source = Slot(inventory, gear, from, fromSlot, storage, loot, lootGear);
        var target = Slot(inventory, gear, to, toSlot, storage, loot, lootGear);
        if (source == null || target == null || source.itemId == null) return false;
        if (ReferenceEquals(source, target)) return false;

        return (from is ItemContainer.Gear or ItemContainer.LootGear, to is ItemContainer.Gear or ItemContainer.LootGear) switch
        {
            (false, false) => SwapItem(source, target),
            (false, true) => EquipItem(source, target, Gear.TypeOf(toSlot)),
            (true, false) => UnEquipItem(source, target, Gear.TypeOf(fromSlot)),
            (true, true) => SwapEquipment(source, target, Gear.TypeOf(fromSlot), Gear.TypeOf(toSlot)),
        };
    }

    /// <summary>
    /// Bodies are take-only: nothing goes into the loot, and taking must not swap anything back into it
    /// (the target slot is empty, or holds the same stackable item to merge onto). Moves not involving loot pass.
    /// </summary>
    public static bool IsAllowedForLoot(Inventory inventory, Gear gear, Inventory loot, ItemContainer from, int fromSlot,
        ItemContainer to, int toSlot, Gear? lootGear = null)
    {
        if (to is ItemContainer.Loot or ItemContainer.LootGear) return false;
        if (from is not (ItemContainer.Loot or ItemContainer.LootGear)) return true;
        var source = Slot(inventory, gear, from, fromSlot, loot: loot, lootGear: lootGear);
        var target = Slot(inventory, gear, to, toSlot, loot: loot, lootGear: lootGear);
        if (source?.itemId is not { } id || target == null) return false;
        return target.itemId == null || target.itemId == id && ItemCatalog.Get(id) is { Stack: true };
    }

    private static bool SwapItem(ItemSlot drag, ItemSlot other)
    {
        if (drag.itemId == other.itemId && ItemCatalog.Get(drag.itemId!.Value) is { Stack: true })
        {
            // Same stackable item: merge onto the target stack.
            other.stack_size += drag.stack_size;
            Clear(drag);
            return true;
        }
        Swap(drag, other);
        return true;
    }

    private static bool EquipItem(ItemSlot inventorySlot, ItemSlot gearSlot, EquipmentType gearType)
    {
        var def = ItemCatalog.Get(inventorySlot.itemId!.Value);
        if (def == null || !def.IsEquipment || def.Slot != gearType) return false;
        Swap(inventorySlot, gearSlot);
        return true;
    }

    private static bool UnEquipItem(ItemSlot gearSlot, ItemSlot inventorySlot, EquipmentType gearType)
    {
        if (inventorySlot.itemId is { } existing)
        {
            // The item coming back into the gear slot must be equipment of that slot's type.
            var def = ItemCatalog.Get(existing);
            if (def == null || !def.IsEquipment || def.Slot != gearType) return false;
        }
        Swap(gearSlot, inventorySlot);
        return true;
    }

    private static bool SwapEquipment(ItemSlot drag, ItemSlot other, EquipmentType dragType, EquipmentType otherType)
    {
        if (ItemCatalog.Get(drag.itemId!.Value)?.Slot != otherType) return false;
        if (other.itemId is { } o && ItemCatalog.Get(o)?.Slot != dragType) return false;
        Swap(drag, other);
        return true;
    }

    private static void Swap(ItemSlot a, ItemSlot b)
    {
        (a.itemId, b.itemId) = (b.itemId, a.itemId);
        (a.stack_size, b.stack_size) = (b.stack_size, a.stack_size);
        (a.data, b.data) = (b.data, a.data); // e.g. a gun's loaded rounds go with the gun
        (a.uid, b.uid) = (b.uid, a.uid);     // and so does its serial number
    }

    private static void Clear(ItemSlot slot)
    {
        slot.itemId = null;
        slot.stack_size = 0;
        slot.data = null;
        slot.uid = null;
    }

    public static ItemSlot? Slot(Inventory inventory, Gear gear, ItemContainer container, int index, Inventory? storage = null,
        Inventory? loot = null, Gear? lootGear = null) =>
        container switch
        {
            ItemContainer.Inventory when index >= 0 && index < inventory.slots.Length => inventory.slots[index],
            ItemContainer.Gear when index >= 0 && index < gear.slots.Length => gear.slots[index],
            ItemContainer.Storage when storage != null && index >= 0 && index < storage.slots.Length => storage.slots[index],
            ItemContainer.Loot when loot != null && index >= 0 && index < loot.slots.Length => loot.slots[index],
            ItemContainer.LootGear when lootGear != null && index >= 0 && index < lootGear.slots.Length => lootGear.slots[index],
            _ => null,
        };

    /// <summary>
    /// Port of DropItem (Ctrl + right-click): empties the slot and returns what was in it, for the
    /// caller to put into the world. (The original dropped a single item even for a stack, losing
    /// the rest; here the whole stack is returned.)
    /// </summary>
    public static (int ItemId, int Count, int? Data, long? Uid)? Drop(Inventory inventory, Gear gear, ItemContainer container, int index,
        Inventory? storage = null)
    {
        var slot = Slot(inventory, gear, container, index, storage);
        if (slot?.itemId is not { } id) return null;
        var count = Math.Max(1, slot.stack_size);
        var (data, uid) = (slot.data, slot.uid);
        Clear(slot);
        return (id, count, data, uid);
    }
}
