# Item tracking and safe saves — 2026-11-16

What was built in response to the item security review (`docs/26-11-15/ITEM-SECURITY-REVIEW-2026-11-15.md`), with examples of the tools in use, how to investigate a duplicated item, and what remains open.

## Summary

Two findings from the review are fixed:

| Finding | Problem | Fix |
|---|---|---|
| **3. Inventory and gear saved in two transactions** | A crash between the two saves could duplicate an item that was just unequipped, or lose one that was just equipped | Inventory, gear and the item log are saved in **one transaction** (`GameDatabase.SaveItems`) |
| **8. No way to detect or trace a dupe** | A duplicated sword was indistinguishable from the original, and nothing recorded item movements | **Serials** for every non-stackable copy, an **item event log**, **three detection layers**, and **command-line tools** to investigate |

The database schema moves from v5 to **v6**. The server backs up the old file before upgrading, as usual.

## Finding 3: one transaction per save

Before, the server's save ran:
```
SaveInventory(...)   ← transaction 1
SaveGear(...)        ← transaction 2   (a crash in between = inconsistent save)
```
Now it runs:
```
SaveItems(character, inventory, gear, events)   ← one transaction: gear + inventory + item log
```
- If any write fails, nothing is saved, and the database keeps the previous consistent state.
- Every place that saves a character's items uses it: moves, equips, drops, pickups, reloads, the 15 s autosave, and the disconnect save.
- **Rule for future features:** anything that moves items between containers (hub storage, trading, crafting) must write all affected rows through one `SaveItems`-style transaction.

## Finding 8: item tracking

### Serials
Every copy of a **non-stackable** item (weapons, armor, wands, tools) carries a serial number (`uid`). The server issues serials from a registry table, `item_serials`, which records the item and where the copy came from:

| Origin | When the serial is issued | Example origin text |
|---|---|---|
| Dungeon loot | The first time anyone picks it up | `loot, run 1` |
| Dev starter kit | When a new character is given the kit | `dev starter kit` |
| Items saved before v6 | When the owner next loads the character | `saved before item serials existed` |

- The serial moves with its copy through every move, equip, swap, drop, pickup and save, exactly like a gun's loaded rounds.
- A dropped item keeps its serial on the ground, so whoever picks it up gets the same copy.
- **Not tracked:** stackables (coins, ammo, health kits, logs), whose copies are identical, so the log records their counts instead. Also the Dungeon Pass, which is never saved.
- Clients never see serials; they stay on the server.

### Event log
`item_events` is an append-only history. Each row records the time, character, action, item, count, serial, instance, run, and a detail.

| Action | When |
|---|---|
| `created` | A new copy appears (loot on first pickup, starter kit); detail = origin |
| `registered` | An old copy gets its first serial on load |
| `picked_up` | Taken from the ground |
| `dropped` | Put on the ground |
| `equipped` / `unequipped` | Moved into or out of a gear slot (both sides of a swap are logged) |
| `loaded` | Ammo moved into a gun; count = rounds, detail = the gun and its serial |
| `destroyed` | Gone for good; currently everything a hardcore character carried when they died |

- Events are saved **in the same transaction** as the inventory change they describe, so the log can't disagree with the saved state.
- The log is not tied to characters by foreign key, so a deleted (hardcore) character's history stays.
- The action is stored as text, so new actions (use, trade, store) need no migration.

### Detection
1. **Startup audit:** when the server starts, it checks every saved serial and warns about problems.
2. **Online check:** when a character loads, the server compares its serials with everyone online and warns about any match.
3. **`--audit-items` tool:** the full report, with each problem's history. It exits with code 1 if anything is found, so it can run from a script.

The audit finds three kinds of problem:

| Kind | Meaning |
|---|---|
| `Duplicate` | The same serial is saved in more than one place (any characters, inventory or gear) |
| `Unregistered` | The registry never issued that serial: forged, or created outside the server |
| `WrongItem` | The serial was issued for a different item (for example a helmet's serial on a sword) |

## Examples

All output below is real, from test runs on 2026-11-16.

### A character's item history (`--item-log`)
```
> <godot>_console.exe --headless --path . -- --server --db <db> --item-log Tangerine
[items] 8 recent item event(s) for 'Tangerine':
[items]   2026-11-16T05:03:51.7729053Z Tangerine created Sword #13 (dev starter kit)
[items]   2026-11-16T05:03:51.7729218Z Tangerine created Pistol #14 (dev starter kit)
[items]   2026-11-16T05:03:51.7729302Z Tangerine created Ice Wand #15 (dev starter kit)
[items]   2026-11-16T05:03:51.7729374Z Tangerine created Blood Wand #16 (dev starter kit)
[items]   2026-11-16T05:03:51.7729467Z Tangerine created Axe #17 (dev starter kit)
[items]   2026-11-16T05:03:51.7729537Z Tangerine created Helmet #18 (dev starter kit)
[items]   2026-11-16T05:03:51.7729606Z Tangerine created Pistol Rounds x30 (dev starter kit)
[items]   2026-11-16T05:03:51.9483435Z Tangerine equipped Pistol #14
```

### One item's history (`--item-history`)
```
> ... --server --db <db> --item-history 8
[items] serial 8: Pistol, created 2026-11-16T05:03:51.7455552Z (dev starter kit)
[items]   2026-11-16T05:03:51.7552630Z Blueberry created Pistol #8 (dev starter kit)
```

### Catching a planted duplicate
To test detection end to end, a copy of a test database was edited by hand: Blueberry was given a second copy of Kermit's Red Shirt (serial 1), which Kermit had looted in run 1.

**The audit tool** (exit code 1):
```
[audit] 1 problem(s):
[audit] Duplicate: serial 1 (Red Shirt) at Kermit (#1) inventory slot 1; Blueberry (#2) inventory slot 0
[audit]     2026-11-16T05:03:29.9140976Z Kermit created Red Shirt #1 in run 1 (loot, run 1)
[audit]     2026-11-16T05:03:29.9142601Z Kermit picked_up Red Shirt #1 in run 1
```
The history shows only Kermit ever legitimately had it: there is no drop by Kermit and no pickup by Blueberry. So Blueberry's copy didn't arrive through the game.

**The startup audit**, before anyone connected:
```
WARNING: [server] item audit found 1 problem(s); run the server with --audit-items for the full list
WARNING: [server]   Duplicate: serial 1 (Red Shirt) at Kermit (#1) inventory slot 1; Blueberry (#2) inventory slot 0
```

**The online check**, the moment Blueberry loaded while Kermit was online:
```
WARNING: [server] DUPLICATE ITEM: serial 1 (Red Shirt) is held by both 'Blueberry' and 'Kermit'. History: --item-history 1
```

### Upgrading existing characters
A test database had its serials removed to simulate items saved before v6. When Kermit logged in:
```
[server] gave serials to 15 item(s) of 'Kermit'
```
Kermit had 17 inventory rows; the other 2 were Health Kit stacks, which aren't tracked. The log then showed `Kermit registered Sword #27`, `Kermit registered Blue Shirt #28`, and so on, and the audit stayed clean. Your dev database (`.localdata/server.db`) will go through the same step the first time each character loads.

## Investigating a suspected dupe

1. Run the audit: `<godot>_console.exe --headless --path . -- --server --audit-items`. Stop the live server first, or use `--db` with a copy.
2. For each `Duplicate`, read the history printed under it. A legitimate transfer always looks like `dropped` by one character, then `picked_up` by the other. Two copies with no transfer between them point to the moment of duplication: the last event before the copies diverge.
3. Check what the involved characters did around that time with `--item-log NAME`, including events for stackables.
4. Decide what to remove. Delete the illegitimate row from `inventory_items` or `equipment_items` while the server is stopped, and run the audit again.

## What's not covered

- **Stackables** (coins, ammo, logs, health kits) have no serials. A duplicated coin stack can't be pointed at directly; the log's counts (picked up, dropped, loaded) allow reconciling by hand.
- **Firing** a gun isn't logged, because it's too frequent. Rounds loaded into a gun are.
- **Items lying on the ground** aren't saved; they vanish when a dungeon closes, as before. A serial issued to an item that was then dropped and left behind simply stops appearing in the audit.
- **Detection, not prevention:** a duplicate is reported, never blocked, so a bug can't cause a player's saves to fail. The serial indexes are deliberately not unique for that reason.

## Still open from the security review

| Finding | Status | When |
|---|---|---|
| 1. Client-reported position is trusted (teleport looting) | Open | Before public hosting |
| 2. No rate limiting; each request writes to the database | Open | Before public hosting |
| 3. Two-transaction save | ✅ Fixed | — |
| 4. Dungeon Pass griefing | Open (design) | If it becomes a problem |
| 5. Godot server relay on | Open | Before public hosting |
| 6. Spent ammo can roll back up to 15 s | Open (minor) | With finding 2 |
| 7. Removing an item from the catalog deletes it from saves | Open | Before real player data matters |
| 8. No way to detect or trace a dupe | ✅ Fixed | — |

## Testing

| | Before | After |
|---|---|---|
| Unit tests | 106 at the 2026-11-15 review | **122**, all passing |
| Smoke test checks | 69 | **75**, all passing |

New unit tests:
- `ItemTests.cs`:
  - inventory and gear are saved together;
  - a failed save changes neither (the finding 3 case).
- `ItemTrackingTests.cs` (14 tests), checking that:
  - serials survive moves, equips, swaps and drops;
  - stackables never carry one;
  - one serial is never shared by several copies;
  - the registry and its origins work;
  - the audit catches all three problem kinds;
  - events are saved with the items, and are dropped when a save fails;
  - history survives deleting a character;
  - a v5 database upgrades to v6 with its data intact.

New smoke test checks, in every scenario:
- the startup audit is clean;
- no duplicate warnings appear while playing.

## Files

| File | Change |
|---|---|
| `core/Items/Inventory.cs` | `ItemSlot.uid`; `SlotRecord` (slot, item, count, data, serial) replaces the old tuples |
| `core/Items/Gear.cs` | `Occupied()` and `Load()` with records |
| `core/Items/SlotContainerLogic.cs` | Serials swap with their item; `Drop` returns the serial |
| `core/Persistence/Migrations.cs` | v6: `uid` columns, `item_serials`, `item_events` |
| `core/Persistence/GameDatabase.cs` | `SaveItems` (one transaction), `RegisterItem`, event log, `AuditItems`, history queries |
| `core/Persistence/ItemTracking.cs` | New: `ItemEvent`, `ItemActions`, audit result types |
| `scripts/Server/ServerGame.ItemTracking.cs` | New: issuing serials, the online check, the startup audit, the command-line tools |
| `scripts/Server/ServerGame*.cs`, `InstanceManager.cs`, `ItemPickup.cs` | Every item action carries serials and logs events |
| `tests/.../ItemTrackingTests.cs` | New tests (above) |
| `tools/smoke-test.ps1` | Audit checks |
| `CLAUDE.md`, `docs/ITEMS.md`, the security review | Documentation |
