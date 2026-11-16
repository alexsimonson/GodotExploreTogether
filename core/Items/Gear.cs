namespace ExploreTogether.Core.Items;

/// <summary>
/// Port of Gear: the 12 equipment slots. Slot index = (int)EquipmentType, so slot 10 is the weapon.
/// (The original assigned indices per slot in the prefab; using the type keeps saved gear unambiguous.)
/// </summary>
public sealed class Gear
{
    public const int SlotCount = 12;

    public readonly ItemSlot[] slots;

    public Gear()
    {
        slots = new ItemSlot[SlotCount];
        for (int i = 0; i < SlotCount; i++)
            slots[i] = new ItemSlot { index = i, itemId = null, stack_size = 0 };
    }

    public static EquipmentType TypeOf(int slot) => (EquipmentType)slot;

    public ItemDef? EquippedWeapon =>
        slots[(int)EquipmentType.Weapon].itemId is { } id ? ItemCatalog.Get(id) : null;

    /// <summary>Port of ExportGear: item id per slot, -1 when empty.</summary>
    public int[] ExportGear() => slots.Select(s => s.itemId ?? -1).ToArray();

    /// <summary>Each slot's item state (e.g. rounds loaded), -1 when none.</summary>
    public int[] ExportGearData() => slots.Select(s => s.data ?? -1).ToArray();

    /// <summary>Port of ImportGear (unknown items and items that don't fit the slot are skipped).</summary>
    public void ImportGear(IReadOnlyList<int> gearItemIds, IReadOnlyList<int>? gearData = null)
    {
        for (int i = 0; i < Math.Min(gearItemIds.Count, SlotCount); i++)
        {
            var def = ItemCatalog.Get(gearItemIds[i]);
            if (def == null || def.Slot != TypeOf(i)) continue;
            slots[i].itemId = def.Id;
            slots[i].stack_size = 1;
            var saved = gearData != null && i < gearData.Count && gearData[i] >= 0 ? gearData[i] : (int?)null;
            slots[i].data = saved ?? def.InitialData;
        }
    }

    public ItemSlot WeaponSlot => slots[(int)EquipmentType.Weapon];

    /// <summary>Equipped items as records (Slot = the gear slot index), e.g. for saving.</summary>
    public IEnumerable<SlotRecord> Occupied() =>
        slots.Where(s => s.itemId != null).Select(s => new SlotRecord(s.index, s.itemId!.Value, 1, s.data, s.uid));

    /// <summary>Restores saved gear (unknown items and items that don't fit their slot are skipped).</summary>
    public void Load(IEnumerable<SlotRecord> saved)
    {
        foreach (var r in saved)
        {
            if (r.Slot < 0 || r.Slot >= SlotCount || ItemCatalog.Get(r.ItemId) is not { } def || def.Slot != TypeOf(r.Slot)) continue;
            slots[r.Slot].itemId = def.Id;
            slots[r.Slot].stack_size = 1;
            slots[r.Slot].data = r.Data ?? def.InitialData;
            slots[r.Slot].uid = r.Uid;
        }
    }
}
