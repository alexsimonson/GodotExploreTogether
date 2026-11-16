# Inventory & equipment UI: port notes (for review)

This is a port of the original Unity inventory and equipment system, kept as close to the
original as practical so it can be judged before deciding whether to redesign it.

## What maps to what

| Unity | Godot |
|---|---|
| `Item`, `Equipment` (+ `Type`), `Weapon` (+ `Style`, `damage`), subclasses `Melee`/`Gun`/`Magic`/`Tool`; item assets and icons | `core/Items/ItemCatalog.cs` (`ItemDef` with `Class`, `Slot`, `Style`, `Damage`, `Icon`); icons in `assets/icons/` |
| `Inventory` (28 slots, stacking) | `core/Items/Inventory.cs` |
| `Gear` (12 slots, `ExportGear`/`ImportGear`) | `core/Items/Gear.cs`; slot index = `(int)EquipmentType` |
| `SlotContainer.OnDrop` → `SwapItem` / `EquipItem` / `UnEquipItem` / `SwapEquipment`; `DropItem` | `core/Items/SlotContainerLogic.cs` (unit-tested in `GearTests.cs`) |
| `InventorySlot` / `EquipmentSlot` (drag with alpha 0.6, Ctrl + right-click drop, stack-size text) | `scripts/UI/ItemSlotButton.cs` (Godot's built-in drag-and-drop) |
| `InventoryUI` (InventoryScrollView: 4 columns, 100×100 cells, 10 px spacing, scroll) and `GearUI` (EquipmentScrollView: 12 slots at their original positions) | `scripts/UI/InventoryScreen.cs` |
| `PlayerInput` toggling both panels together | Tab toggles the screen (see "Key bindings") |
| HUD `WeaponText` | "Weapon: …" label at the bottom right |
| Saving gear with the character | Database step 3: `equipment_items` |

## Architecture differences (on purpose)

- **The server decides.** A drag sends a request (`MoveItem` / `DropItem`); the server applies the
  original rules and sends back the whole inventory + gear. The client never edits its own items.
- **One source of truth.** In the original, `InventoryUI`, `InventorySlot`, `EquipmentSlot` and
  `Inventory.slots` each held a copy of the items, kept in sync by `GameEvent`s
  (`onInventoryChanged`, `onEquipmentChanged`, …). Here the UI only displays the server's state,
  so the copies can't disagree.

## Behavior differences (for review)

1. **`SwapEquipment` checks types.** The original swapped two gear slots with no type check
   (a helmet could be dragged into the shoe slot). Now a gear-to-gear drag only works if each
   item fits the other slot, so within your own gear it does nothing. The original's comment
   already said it might never be needed.
2. **Dropping a stack keeps the stack.** The original's `RemoveItem(index, shouldDrop: true)`
   spawned one world item for a stack and the rest vanished. Now the dropped pickup carries the count.
3. **Dropped items land just in front of you** (0.8 m) rather than exactly at your feet, so you can see them.
4. **Pickups of stacks** add the whole count; if the inventory fills up partway, the rest stays on the ground.

## Bugs noticed in the original (fixed by the port, or not applicable)

- `SwapEquipment` type hole (above).
- Stack loss on drop (above).
- `Gear.FindItemInSlot` read `slots[i].item.id` without a null check (crash on an empty slot when
  adding a stackable to gear).
- `UnEquipItem`'s weapon branch compared `GetType().ToString()` to `"ExploreTogether.Weapon"`, which
  never matches since weapons are `Melee`/`Gun`/`Magic`/`Tool` subclasses. Harmless by luck: the type check below it still ran.
- UI lookups by fixed child indices and long `.parent.parent...` chains (`GetChild(3)`, six
  `.parent` hops in `EquipItem`) break whenever the prefab hierarchy changes. The port has no such lookups.
- Several `GameEvent` raises are duplicated ("this is duplicating..." comments) and `UpdateSlot`
  runs for every inventory when one changes ("how can I fix this?"). Gone with the single-state design.

## Not ported yet

- **Storage** (the hub storage chest and its second InventoryUI/GearUI pair). The drag logic
  supports it easily (it would be another container type).
- Ctrl + left-click (it only logged "Testing grab item").
- `max_stack_size` is still not enforced (the original didn't either).

## Key bindings

The original used **F** to interact and **E** to toggle the inventory. The port currently uses
**E** to interact and **Tab** for the inventory. Switching to the original bindings is a two-line change.
