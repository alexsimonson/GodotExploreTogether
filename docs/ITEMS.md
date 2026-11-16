# Items

How items work in the Godot rebuild as of 2026-11-15 (after phase 5), and how easily the system can grow. Written for planning hub features like storage, gathering and crafting.

## Short answer

- **There is no item database.** Item definitions are a hard-coded list in C#: `ItemCatalog.All` in [core/Items/ItemCatalog.cs](../core/Items/ItemCatalog.cs). It is a direct port of the original's `Resources/Items/*.asset` files, with the same ids, names and icons.
- **The SQLite database stores only who owns what:** per character, which item id is in which slot, how many, and one optional number of per-copy state.
- **Adding a plain item is one line plus an icon.** Adding an item that *does* something new (a new weapon behavior, a consumable, a crafting input with a recipe) needs code in several places. The system is easy to extend in data and moderately easy in behavior; the rough edges are listed under [Extendability](#extendability).

## How it works

### Definitions: `ItemDef` and `ItemCatalog`

Every kind of item is an `ItemDef` record:

| Field | Meaning |
|---|---|
| `Id` | Stable number, same as the original. **Never renumber or reuse**: saves store only this. |
| `Name`, `Description` | Display text |
| `Stack` | Stackable (all copies share one slot with a count) or not (one copy per slot) |
| `Icon` | File name in `assets/icons/`; empty or missing means no icon |
| `Class` | `Item`, `Equipment`, `Melee`, `Gun`, `Magic`, `Tool`: the Unity class the asset came from. Decides combat behavior. |
| `Slot` | Which gear slot it equips into (`EquipmentType`: Head, Neck, Torso, Leg, Glove, Shoe, Back, Eye, Coat, Ring, Weapon, Offhand), or none |
| `Style` | `Melee`, `Gun`, `Magic` (ported, but combat decides by `Class`) |
| `Damage` | Ported from the assets, but **not used** (see below) |
| `MagazineSize`, `AmmoItemId` | Guns only: how many rounds it holds, and which item reloads it |
| `LootCount` | How many make one pickup when generated as loot (Pistol Rounds: 10) |

`ItemCatalog` holds the 17 definitions with `Get(id)` and `NameOf(id)`, plus `GenerateItem(random)` for loot. It lives in `core/`, so the server, the client and the unit tests all compile the same list. Two named ids are used in code: `DungeonPassId` (999) and `PistolRoundsId` (1338).

### Containers: inventory and gear

All in `core/Items/`, Godot-free and unit-tested:
- **`Inventory`**: 28 slots. Stackables go onto an existing stack; others take a free slot each.
- **`Gear`**: 12 slots, one per `EquipmentType`, in the original's order.
- **`ItemSlot`**: item id, count, and `data`.
- **`SlotContainerLogic`**: the original's drag rules between the two containers (swap, equip only into the matching slot, unequip, swap equipment). It also handles drop: Ctrl + right-click drops the whole stack.

### Per-copy state (`data`)

Each slot can carry one nullable integer that travels with that copy of the item: when it is moved, equipped, dropped, picked up or saved. Today it holds only **a gun's loaded rounds**: `InitialData` gives a fresh pistol 10, and reloading moves rounds from Pistol Rounds stacks into it. Stackable items never have data.

### Serials (per-copy identity)

Each slot also carries `uid`, the copy's **serial**, for non-stackable items (everything except stacks and the Dungeon Pass). The server issues serials from a registry (`item_serials`) that records where each copy came from:
- loot gets its serial on first pickup;
- the dev starter kit creates items with serials;
- items saved before serials existed get one when their owner next loads.

The serial travels with the copy exactly like `data`: moves, equips, drops, pickups and saves. The same serial in two places means a duplicated item. Clients never see serials.

### Saving

Schema in [core/Persistence/Migrations.cs](../core/Persistence/Migrations.cs):

- `inventory_items (character_id, slot, item_id, count, data, uid)`
- `equipment_items (character_id, slot, item_id, data, uid)`
- `item_serials (uid, item_id, created_at, origin)`: every non-stackable copy ever issued
- `item_events (at, character_id, character_name, action, item_id, count, uid, instance_id, run_id, detail)`: an append-only history (created, registered, picked_up, dropped, equipped, unequipped, loaded, destroyed). It isn't tied to characters by foreign key, so it outlives a deleted character.

The server loads items when a character is selected and saves at checkpoints. Each save rewrites all of a character's inventory and gear rows, plus the events describing the change, in **one transaction** (`GameDatabase.SaveItems`). The Dungeon Pass is never saved or tracked. Item definitions are not in the database; only ids are.

Duplicates are detected in three ways:
- an audit of the whole database at server startup;
- a check against everyone online when a character loads;
- the `--audit-items` tool. `--item-history SERIAL` and `--item-log CHARACTER` trace what happened.

The tracking code is in [scripts/Server/ServerGame.ItemTracking.cs](../scripts/Server/ServerGame.ItemTracking.cs).

### Networking

The server is authoritative: clients ask to move, drop or pick up, and the server applies the rules and sends back the result.
- The inventory goes to the client as flat integer arrays: `[slot, id, count, data]` per occupied slot, plus gear ids and gear data (`NetSession.SendInventory`).
- The client looks names and icons up in its own copy of the catalog. **Client and server therefore must have the same catalog**, which they do as long as they're the same build.

### In the world

- `ItemPickup` ([scripts/Items/ItemPickup.cs](../scripts/Items/ItemPickup.cs)) is an item lying in an instance. It has an id, a count and server-side data, is replicated per instance, and is picked up with E.
- Every item uses the same generic loot visual and a name label, except the Dungeon Pass, which has its own model and a glow.
- Dungeon loot (`DungeonContents`): the Dungeon Pass plus 20 items from `GenerateItem`, which picks **uniformly from the whole catalog** except the pass.

### UI

- `ItemSlotButton` draws an icon (through `IconCache`, from `res://assets/icons/<Icon>`) and a count. For guns it shows the loaded rounds instead.
- The inventory screen and gear panel are a port of the original UI.

## Where items get special treatment in code

| What | Where | Keyed by |
|---|---|---|
| Combat behavior (range, damage, cooldown, hitscan or area) | `CombatRules.ProfileFor` (core), `ServerGame.Combat.cs` | `Class` |
| Damage amounts | `CombatRules` constants: melee/tool 20, gun 100, magic 0–49 | `Class`, **not** the item's `Damage` |
| Frost vs Blood magic | `CombatRules` | **item id 567** (Ice Wand); everything else is Blood |
| Aiming, crosshair, reload key, ammo readout | `ClientGame.Combat.cs` | `Class` |
| Reload and ammo counting | `GunRules`, `ServerGame.Combat.cs` | `MagazineSize`, `AmmoItemId` |
| Dungeon Pass: unlocks the exit, drops when its holder leaves, never saved, own world model | `ServerGame.Dungeon.cs`, `ServerGame.Inventory.cs`, `ItemPickup.cs` | `DungeonPassId` (9 places, including loot generation) |
| Dev starter kit | `ServerGame.cs` | a list of ids |

## Extendability

### What's easy
- **A new plain item** (resource, junk, quest item): add one line to `ItemCatalog.All` with a new unused id, and put a PNG in `assets/icons/`. It stacks, moves, drops, saves, shows in the UI and appears as dungeon loot automatically.
- **New equipment** (a ring, boots): the same, with `ItemClass.Equipment` and a `Slot`. It equips into its slot with no other changes, but it does nothing yet, because no stats exist.
- **A new weapon of an existing class** (a second sword or gun): one line. It behaves exactly like the others of its class; see the first limit below.
- **A new container** (hub storage, a chest): `Inventory` already takes a slot count. The storage work is a second container, a new table, and widening `SlotContainerLogic` beyond the two containers it knows.

### What needs code, and the current limits
1. **Weapons of the same class are identical.** Damage comes from per-class constants, so the `Damage` field on each item is ignored. A stronger sword needs combat code to read per-item stats (damage, and perhaps range and cooldown) from the `ItemDef`.
2. **Magic style is chosen by item id.** A third wand, or Fire/Earth, means editing `CombatRules`. A `MagicStyle` field on `ItemDef` would remove that.
3. **Items have no use action.** Health Kit and Coin do nothing. Consumables need a "use" request (for example a right-click in the inventory), a server handler, and an effect per item.
4. **No item stats on equipment.** Armor or bonuses need stat fields and something that reads them (damage taken, move speed, and so on).
5. **Loot is uniform over everything.** Every new item changes the odds of every other one. The placeholder "Default Item" can drop, and so can Log and Pistol Rounds. Rarity weights, per-source loot tables (dungeon, tree, enemy), or a "not loot" flag are needed before the catalog grows much.
6. **One integer of per-copy state.** Enough for ammo. Durability plus ammo, enchantments or a custom name would need more than one value: a second column, or a small serialized field.
7. **No per-item world models.** Everything on the floor looks like the same loot object, apart from the pass.
8. **Removing an item from the catalog deletes it from saves.** On load, unknown ids are skipped, and the next save rewrites the rows without them. That's fine while data isn't precious; later, keep retired ids in the catalog (marked retired) rather than deleting them.
9. **No validation test for the catalog itself.** A duplicate id crashes at startup, which is loud, but nothing checks that icons exist or that `AmmoItemId` points at a real stackable item.

### Code list or data files?
Keeping definitions in C# is a good fit for now:
- they're type-checked, unit-testable and shared by client and server with no loading step;
- only programmers add items.

Moving them to data files (JSON, or Godot `.tres` resources) becomes worth it if non-programmers start adding items, or if there are hundreds. Either way, the server should remain the source of truth: a client with a mismatched list would show wrong names but couldn't cheat.

### Suggested groundwork before hub features
In rough order, each small:
1. Use per-item `Damage` in combat, and add a `MagicStyle` field (limits 1–2).
2. Add a "not loot" flag or loot weights, and hide Default Item (limit 5).
3. Add a catalog test: unique ids, icons exist, ammo references are valid (limit 9).
4. When building storage, generalize `ItemContainer` and `SlotContainerLogic` to any number of containers rather than adding a third special case.
5. Add a use action when the first consumable is needed (limit 3).

## Adding an item: checklist
1. Pick an id not used in `ItemCatalog.All` (and never used before, if saves exist).
2. Add the `ItemDef` line: name, description, stackable, icon file, and class/slot if it's equipment.
3. Put the icon in `assets/icons/`, then re-import (`<godot>_console.exe --headless --path . --import`).
4. If it should be a weapon with new behavior, or a consumable, see the limits above.
5. Run `dotnet test ExploreTogether.sln`; for combat items, also `tools\smoke-test.cmd`.
