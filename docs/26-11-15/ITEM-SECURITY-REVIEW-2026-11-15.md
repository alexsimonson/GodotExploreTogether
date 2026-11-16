# Item and inventory security review — 2026-11-15

A review of the item and inventory code for duplication ("duping") and other abuse, plus how the `ItemCatalog` design compares with common practice. It is based on reading the code after phase 5; none of the findings were reproduced by running an attack. For how the system works, see `docs/ITEMS.md`.

## Summary

- **No direct duplication bug was found.** Clients only ask; the server decides every move, drop, pickup and reload, using the sender id Godot provides rather than anything the client claims. The Dungeon Pass leaves the player on every exit path. That foundation is right.
- **The biggest weakness is movement, not items.** Players report their own position, and the server uses that position for every "close enough" check. A modified client can therefore pick up every item in a dungeon, or open the exit, from anywhere.
- **One real duplication window exists**, but it needs a server crash at the wrong moment: inventory and gear are saved in two separate transactions. It matters most once storage or trading exists.
- **There's no rate limiting.** Every inventory request writes to the database on the game thread, so one client spamming requests can stall the server for everyone.

For a co-op game among friends, none of this is urgent. Items 1 and 2 below should be fixed before public hosting. Items 2, 3, 5, 7 and 8 have since been fixed; item 6 is reduced.

## Findings

Ranked by how much they would matter on a public server.

### 1. Position is trusted, so reach checks can be bypassed (high, before public hosting)
- **What:**
  - `PlayerCharacter.ApplySubmittedTransform` accepts any position the client sends. Its own comment says "Movement validation (speed checks, etc.) belongs here later."
  - `IsNear` (`ServerGame.cs`) measures the 5 m interaction reach from that position.
  - Drops are placed at it, and attacks start from it.
- **Abuse:** a modified client can:
  - send its position next to each item in turn and pick everything up within seconds;
  - jump to the Dungeon Pass, then to the exit, and escape;
  - drop items, including the Dungeon Pass, into walls or far below the floor, where nobody can reach them;
  - attack enemies from positions it never walked to.
- **Fix:** validate movement on the server.
  - Reject or clamp position updates that move faster than sprint speed plus jump and fall, allowing for lag.
  - Snap the player back when an update is rejected.
  - Optionally, raycast between the last accepted position and the new one so players can't pass through walls.
  - Clamp drop positions to near the player's last accepted position.
  - This also strengthens every existing check, since they all measure from that position.

### 2. No rate limiting; each request writes to the database — ✅ fixed 2026-11-16

**Fixed:**
- Every client request goes through a per-peer, per-request token bucket (`core/Net/RequestRateLimiter.cs`, limits in `scripts/Server/ServerGame.Limits.cs`). Requests over the limit are ignored. A peer with more than 200 ignored requests within 10 s is disconnected. `NetSession.Accept()` applies the filter to every client-to-server RPC by name, so a new RPC is covered automatically (it's unlimited until it gets an entry in `RequestLimits`).
- Moving items between slots no longer saves. The move's equip/unequip events wait in `PeerSession.PendingItemEvents` and are written in the same transaction as the next save (pickup, drop, reload, autosave, disconnect), so the log still matches the saved state. A hardcore death writes them before the destroyed events.
- Unit tests: `RateLimiterTests` (7). The smoke test passes with no limit hit in normal play, including `--dev-solve`'s burst of interactions.
- Still open: a move request still answers with the full inventory (under 500 bytes, now at most 10 a second).

The original finding:
- **What:**
  - `MoveItem`, `DropItem`, `Interact` and `Reload` accept requests as fast as they arrive.
  - Every inventory change rewrites the character's inventory and gear rows, synchronously, on the main thread. That thread also runs physics and enemies for every dungeon.
  - Every move request also sends the whole inventory back, even when nothing changed.
  - A drop-and-pickup loop creates and frees a replicated node each time.
- **Abuse:** a client sending thousands of move requests a second stalls the whole server.
- **Fix:**
  - Limit requests per peer (for example 20 a second per type), and drop or disconnect when a peer exceeds it.
  - Separately, stop saving on every move: mark the character dirty and let the existing 15 s autosave and the disconnect save write it. Keep saving immediately only where losing the change would matter, for example after a pickup or a hub storage transfer.

### 3. Inventory and gear are saved in two transactions — ✅ fixed 2026-11-15

**Fixed:**
- `GameDatabase.SaveItems` writes inventory and gear in one transaction, and the server's save uses it.
- A unit test (`SaveItems_WhenAnyWriteFails_ChangesNeitherInventoryNorGear`) makes the inventory write fail after the gear rows were rewritten, and checks that neither table changed.
- When hub storage is added, its rows must go into the same transaction.

The original finding:
- **What:** `SaveInventory` (`ServerGame.Dungeon.cs`) calls `_db.SaveInventory` and then `_db.SaveGear`, each in its own transaction.
- **Abuse:** if the server crashes between the two:
  - after **unequipping**, the item is saved in the inventory while the old gear row still has it, so the item is **duplicated**;
  - after **equipping**, it's in neither, so the item is **lost**.

  It needs a crash at an exact moment, so it's unlikely by accident. Combined with any reproducible crash, though, it's the classic dupe recipe.
- **Fix:** save everything a character owns (inventory, gear, and later storage and skills) in **one transaction**. Do the same for any future transfer between two characters, such as trading or shared storage: both sides change in the same transaction or not at all.

### 4. Dungeon Pass griefing (low, design)
- **What:** the pass is shared and droppable. Its holder can:
  - keep it and never open the exit;
  - drop it somewhere awkward, or with finding 1, somewhere unreachable.
- **Fix (if it matters):**
  - Let anyone open the exit while the holder is within reach, or return the pass to its spawn spot if it's dropped out of bounds.
  - Fixing finding 1 removes the unreachable case.

### 5. Godot's server relay is on — ✅ fixed 2026-11-16

**Fixed:** the server sets `SceneMultiplayer.ServerRelay = false` at startup (`ServerGame.Limits.cs`).

The original finding:
- **What:** Godot's `SceneMultiplayer.ServerRelay` defaults to true. That lets clients send RPCs to each other through the server. The game never needs it.
- **Current state:** client-to-client messages are harmless today. Server-only RPCs check `Main.IsServer`, and client-side RPCs are `Authority` mode, so relayed ones from another client are rejected. But relay is still a path for spam, and a future RPC could forget the check.
- **Fix:** set `ServerRelay = false` on the server's multiplayer API at startup.

### 6. Spent ammo can roll back up to 15 s (low; unchanged, and slot moves now behave the same way)
- **What:** firing decrements the gun's loaded rounds in memory without saving; the next save is up to 15 s later.
- **Abuse:** after a server crash, players get those rounds back. It's a small loss for the player, not a profit, unless ammo becomes valuable.
- **Fix:** none needed now; finding 2's dirty-flag approach covers it consistently.

### 7. Removing an item from the catalog deletes it from saves — ✅ fixed 2026-11-16

**Fixed:** `ItemDef.Retired` marks an item that's out of the game: it still loads from saves, but `GenerateItem` never picks it. `ItemCatalogTests.Catalog_KeepsEveryShippedId` fails if any shipped id is removed from the catalog.

The original finding:
- **What:** loading skips unknown item ids, and the next save rewrites the rows without them.
- **Fix:** never remove ids. Mark them retired instead: they stay loadable but don't drop as loot. Alternatively, keep unknown rows untouched when saving.

### 8. No way to detect or trace a dupe (missing tooling) — ✅ fixed 2026-11-16

**Fixed** (database v6, `scripts/Server/ServerGame.ItemTracking.cs`):
- **Serials:** every copy of a non-stackable item has a serial from a registry (`item_serials`) that records where it came from:
  - loot gets its serial on first pickup;
  - starter kit items get theirs when they're created;
  - items saved before serials existed get one when their owner next loads.
- **Event log:** an append-only `item_events` table records items being created, registered, picked up, dropped, equipped, unequipped, loaded (ammo into a gun) and destroyed (hardcore death). Events are written in the same transaction as the inventory save. The log isn't tied to characters by foreign key, so it outlives a deleted character.
- **Detection:**
  - an audit of the whole database at startup;
  - a check against everyone online whenever a character loads;
  - `--audit-items`, which reports duplicated, unregistered and mismatched serials, with each one's history.
- **Tracing:** `--item-history SERIAL` and `--item-log CHARACTER`.
- **Verified:** a duplicate planted in a test database was caught by all three detection layers. The smoke test checks that the startup audit is clean.
- **Not covered:** stackables (coins, ammo) have no serials; the log records their counts. Firing a gun isn't logged, but ammo loaded into a gun is.

The original finding:
- **What:**
  - There's no record of item movements.
  - Identical non-stackable items are indistinguishable, so a duplicated rare sword can't be told apart from the original.
- **Fix, when items gain value (trading, rare drops):**
  - An append-only `item_events` table: time, character, action (pickup, drop, equip, use, trade), item id, count, instance.
  - Optionally, a unique id per copy of a non-stackable item, as MMOs commonly do, so one copy existing in two places can be spotted.

### Design note: dying costs nothing in items
A non-hardcore death keeps everything, and the run just doesn't count. That's a design choice rather than a vulnerability. Combined with finding 1, though, there is no risk in a run, so loot is free. Worth deciding before loot gets valuable.

## Checked and found safe

- **Sender identity:** every request uses `Multiplayer.GetRemoteSenderId()`, which Godot fills in. A client can't act as another peer.
- **Clients send intent only:** move (container, slot to container, slot), drop (container, slot), pickup (interaction id), reload. Clients never send item ids, counts or gun ammo, so they can't make items or set amounts.
- **Input bounds:** container numbers are validated, and slot indexes are bounds-checked in `SlotContainerLogic.Slot`. Equip rules are enforced on the server.
- **Double pickup:** requests are handled one at a time on the main thread. The first pickup removes the item from the instance's list before anyone else's request runs, so two players can't both get it. A partial pickup puts back exactly the remainder.
- **Requests during a transfer between instances:** while a player is between instances they aren't in any, so pickups, drops and interactions are rejected.
- **Dungeon Pass:**
  - never saved;
  - removed and dropped on every way out: returning to the hub, escaping (it's used up first), normal death, hardcore death and disconnecting;
  - can't be carried into the hub.
- **Two sessions on one character:** the "already logged in" check runs on the main thread after the password check finishes. Two parallel logins to one account therefore can't both succeed. Without that check, two copies of one character could each save, which is the most common dupe in online games.
- **Character ownership:** selecting a character checks that it belongs to the logged-in account. Selecting or creating while already playing is refused.
- **Gun ammo:** loaded rounds live on the gun's slot, move with it, and are taken from the inventory's ammo stacks one-for-one on reload.

## Is the `ItemCatalog` approach unusual?

No. There are three common ways to define items, and this is the first:

| Approach | Typical users | Strengths | Weaknesses |
|---|---|---|---|
| **Definitions in code** (this project) | Small and indie games, prototypes | Type-checked, refactorable, unit-testable; no loading step | Only programmers can add items; a client update is needed for any change |
| **Data files**: Unity ScriptableObjects (the original), Godot `.tres` resources, JSON/CSV, spreadsheets | Most commercial single-player and co-op games | Designers edit without code; editor tooling | Needs validation at load; typos become runtime errors |
| **Server database tables** (for example an `item_template` table) | MMOs | Live balance changes without a client patch | More infrastructure; clients still need names and icons, usually from shipped data files anyway |

**The definition approach doesn't affect security.** What matters is who owns the *state*, and here the server does. A hacked client with an edited catalog would only display wrong names; the server's catalog decides everything.

**It isn't less efficient either.** Lookups are a dictionary by integer id, and items cross the network as small integer arrays. A full inventory is under 500 bytes. Sending only changes would matter only with much larger inventories or far more frequent updates.

### What would actually be better
1. **Item behavior as optional parts, not a growing class list.**
   - Today `ItemDef` is one record with nullable gun fields, a class enum, and behavior chosen by `switch` statements spread across the code.
   - As features arrive (consumables, armor stats, magic styles, tool roles, durability), that record and those switches keep growing.
   - The common next step is composition: an item has optional parts, for example `Weapon { damage, range, cooldown }`, `Gun { magazine, ammo }`, `Magic { style }`, `Consumable { effect }`, `Equippable { slot, stats }`, `Tool { role }`.
   - Code then asks "does it have a `Gun` part?" instead of checking ids or classes. Worth doing around the third new behavior.
2. **Data files when non-programmers add items**, or when there are more than a few dozen. Godot `.tres` resources fit the engine and the original's ScriptableObject habit; JSON fits the Godot-free `core/` library better. Either way, validate at startup: unique ids, icons exist, references like `AmmoItemId` are valid.
3. **A catalog version check at login.** The server sends a hash of its catalog, and a mismatched client is told to update. This turns "wrong names on screen" into a clear message.

## Recommended order
1. **Before public hosting:** movement validation (finding 1) and the DTLS encryption already noted in `PLAN.md`. (Rate limits and turning off the relay are done.)
2. **With hub storage:** save storage in the same transaction as inventory and gear (`SaveItems`, finding 3), and save on change only where it matters (finding 2).
3. **When items gain value:** the retire-don't-delete rule (finding 7) is in place. The event log and per-copy serials (finding 8) are done; add new actions to the log as features arrive (use, trade, storage).
4. **When adding the next few item behaviors:** move to optional parts (composition).
