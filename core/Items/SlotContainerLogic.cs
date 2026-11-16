namespace ExploreTogether.Core.Items;

public enum ItemContainer { Inventory, Gear }

/// <summary>
/// Port of SlotContainer.OnDrop: what happens when an item is dragged from one slot onto another.
/// The four cases and their rules are the original's:
///   inventory -> inventory : SwapItem       (swap the two slots, either may be empty)
///   inventory -> gear      : EquipItem      (only equipment whose type matches the gear slot; swaps with what was equipped)
///   gear -> inventory      : UnEquipItem    (into an empty slot, or swap with equipment of the same type)
///   gear -> gear           : SwapEquipment
/// Dragging from an empty slot does nothing.
///
/// Deliberate differences from the original (for review):
/// - SwapEquipment swapped two gear slots without any type check (e.g. a helmet could end up in
///   the shoe slot). Here it only swaps if each item fits the other slot, which within one player's
///   gear means it never applies; the original's comment already suspected it wasn't needed.
/// - Everything else (including keeping stack sizes with the item) matches.
/// </summary>
public static class SlotContainerLogic
{
    /// <summary>Applies a drag from (from, fromSlot) to (to, toSlot). Returns false if nothing changed.</summary>
    public static bool Move(Inventory inventory, Gear gear, ItemContainer from, int fromSlot, ItemContainer to, int toSlot)
    {
        var source = Slot(inventory, gear, from, fromSlot);
        var target = Slot(inventory, gear, to, toSlot);
        if (source == null || target == null || source.itemId == null) return false;
        if (ReferenceEquals(source, target)) return false;

        return (from, to) switch
        {
            (ItemContainer.Inventory, ItemContainer.Inventory) => SwapItem(source, target),
            (ItemContainer.Inventory, ItemContainer.Gear) => EquipItem(source, target, Gear.TypeOf(toSlot)),
            (ItemContainer.Gear, ItemContainer.Inventory) => UnEquipItem(source, target, Gear.TypeOf(fromSlot)),
            _ => SwapEquipment(source, target, Gear.TypeOf(fromSlot), Gear.TypeOf(toSlot)),
        };
    }

    private static bool SwapItem(ItemSlot drag, ItemSlot other)
    {
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
    }

    public static ItemSlot? Slot(Inventory inventory, Gear gear, ItemContainer container, int index) => container switch
    {
        ItemContainer.Inventory when index >= 0 && index < inventory.slots.Length => inventory.slots[index],
        ItemContainer.Gear when index >= 0 && index < gear.slots.Length => gear.slots[index],
        _ => null,
    };

    /// <summary>
    /// Port of DropItem (Ctrl + right-click): empties the slot and returns what was in it, for the
    /// caller to put into the world. (The original dropped a single item even for a stack, losing
    /// the rest; here the whole stack is returned.)
    /// </summary>
    public static (int ItemId, int Count, int? Data)? Drop(Inventory inventory, Gear gear, ItemContainer container, int index)
    {
        var slot = Slot(inventory, gear, container, index);
        if (slot?.itemId is not { } id) return null;
        var count = Math.Max(1, slot.stack_size);
        var data = slot.data;
        slot.itemId = null;
        slot.stack_size = 0;
        slot.data = null;
        return (id, count, data);
    }
}
