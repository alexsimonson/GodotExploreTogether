# Security review: city prototype (2026-11-16)

**Scope:** everything added on `proto/city`:
- the city instance and the City Gate
- residents (NPCs)
- doors, locks and keys
- searching bodies and two-way transfer with them
- knockouts and dragging bodies
- the hub's debug supply and the locksmith

**Method:** I reviewed every new request the client can send, and every item path, against the project's rule: the server is authoritative and only a client's own movement is trusted.

The earlier reviews still apply, in particular [ITEM-SECURITY-REVIEW-2026-11-15.md](../26-11-15/ITEM-SECURITY-REVIEW-2026-11-15.md). That includes the unencrypted ENet traffic and the unvalidated movement.

## Summary

| # | Finding | Severity | Status |
|---|---|---|---|
| 1 | The debug supply creates items from nothing, and `dev-run` turns it on by default | **High** if a public server inherits the flag | Open |
| 2 | City creation has no cap, so a client can make the server run many cities at once | **Medium** (DoS) | Open |
| 3 | Body transfers and supply takes save to the database on every move | **Medium** (DoS) | Open |
| 4 | "From inside" door locking trusts the client's position | **Medium** | Fixed: server-side movement validation (see STEALTH-AND-ROLES.md) |
| 5 | Dragging and reach checks trust the client's position | Low–Medium | Fixed: server-side movement validation (see STEALTH-AND-ROLES.md) |
| 6 | The locksmith turns farmable keys into coins | Low (economy) | Open |
| 7 | "Take all" can trip the MoveItem rate limit | Low (functional) | Open |
| 8 | Items left on bodies vanish when the city closes | Low | Accepted, logged |
| 9 | Request parsing (`door:N`, `npc:Name`, containers, slots) | — | OK |
| 10 | Keys can't be forged; lock codes are only an identity | — | OK |

## Findings

### 1. The debug supply is an item printer (High if enabled on a public server)

**What happens:**
- `--dev-supply` lets any player at the hub chest create any non-retired item, unlimited.
- Every copy is registered with origin `debug supply` and logged as `created`.
- `tools/dev-run.cmd` passes `--dev-supply` by default.

So a public server started from a copied dev command line hands out everything.

**Mitigations already in place:**
- The server checks the flag, the hub instance, and reach on every move (`CanUseSupply`).
- Keys and the Dungeon Pass are excluded.
- The item log records the origin, so supply items can be found afterwards.

**Recommendations:**
- Refuse `--dev-supply` unless the server is bound to localhost, or also started with an explicit `--dev` switch.
- At startup, log a loud warning whenever it's on.
- Add an audit option that lists or removes items whose origin is `debug supply` before a database is promoted to real play.

### 2. City creation has no cap (Medium, DoS)

**What happens:**
- Using the City Gate when no city is occupied generates a new one: about 50–270 ms of CPU, and 40 to 160 NPC nodes simulated at 20 Hz.
- An empty city lives on for the empty timeout (60 s).

A client alternating "enter city" and "leave city" can keep many cities alive. Two things limit this today: the `Interact` rate limit (10/s) and the load time on each trip.

**Recommendations:**
- Cap the number of concurrent cities.
- Give each player a cooldown on creating cities.
- Reuse an empty city that hasn't closed yet instead of generating another.

### 3. A database save on every body or supply move (Medium, DoS)

**What happens:** `TakeFromBody` (both directions) and `SupplyMove` call `SaveInventory` immediately. That's a database transaction per drag, up to the `MoveItem` limit (10/s, burst 20) per client.

The item review's finding 2 already moved ordinary inventory moves to deferred saves for this reason. These new paths save immediately because they create or move tracked items across an ownership boundary.

**Recommendations:**
- Give body and supply moves their own, lower rate limit.
- Or batch them: the next autosave already writes pending events, so only serial registration needs to happen at once.

### 4. "From inside" door locking trusts the client's position (Medium)

**What happens:** `OnLockDoor` lets a player lock or unlock without the key when `CityDoor.IsInside(player.NetPosition)`. `NetPosition` is client-reported, and movement isn't validated (an existing, known gap).

A modified client can claim a position just behind any door, then unlock it and walk in. The same applies to opening doors through `IsNear`.

**Recommendation:**
- This closes with server-side movement validation, which is already on the "before public hosting" list.
- Until then, also require that the player's last few accepted positions were inside the building. That's cheap and catches teleports.

### 5. Dragging and reach checks trust the client's position (Low–Medium)

**What happens:**
- A dragged body follows the client-reported position. A teleporting client drags a body anywhere it can get a clear ray to.
- The server's wall ray stops the body passing through walls, but a client "inside" a building can pull the body in with it.
- Searching, grabbing and door interactions all use `IsNear` on `NetPosition`.

**Recommendation:** the same as finding 4. Bodies have no lasting value, so the impact is limited until stealth gives body placement meaning.

**Fixed (2026-11-16):** `MoveGuard` (core) caps each player's movement at their current top speed (with a small burst allowance for network hiccups), and the server rejects moves that cross a wall, fence or closed door, sending the client back. Every position-based check now sees only accepted positions. Remaining gap: a client can still choose *where* to go within its speed, e.g. hop a fence; that's ordinary play.

### 6. The locksmith turns farmable keys into coins (Low, economy)

**What happens:** each city's residents carry keys, and every visit generates a new city, so keys are effectively unlimited: kill or knock out residents, take keys, sell them at 5 coins each.

Coins have no use yet, so nothing is at stake today.

**Recommendations, before coins matter:**
- Pay less, or nothing, for keys from cities that are still open.
- Cap trade-ins per player per day.
- Record which city a key came from (for example a city id issued by the database; see the key question in the conversation) and only buy keys from closed cities.

### 7. "Take all" can trip the MoveItem rate limit (Low, functional)

**What happens:** the client sends one move per item. A body can carry up to 40 items, which is over the `MoveItem` burst of 20, so the rest are dropped without notice.

Repeated large bursts also count toward the limiter's disconnect threshold.

**Recommendation:** a single server-side `TakeAll(body)` request that moves everything in one go and saves once. This also helps finding 3.

### 8. Items left on bodies vanish when the city closes (Low, accepted)

**What happens:** two-way transfer lets players put items on a body. Bodies aren't saved, so those items are gone when the city closes.

**Mitigations already in place:** each one is logged as `dropped`, with the detail "left on the body of …". The duplicate audit and the item history stay consistent, because the item leaves the player's saved containers in the same save.

**Recommendation:** none needed. Optionally, warn the player the first time.

### 9. Request parsing and validation (OK)

- **`door:N`:** parsed with `int.TryParse`, and bounds-checked against the door count (`UseDoor`). `OnLockDoor` first resolves the interactable by id with `IsNear`, so only existing doors are reachable.
- **`npc:Name`:** a dictionary lookup in the player's own city instance. Searching, grabbing and moves all re-check that the person is down and within reach.
- **Containers:** checked against the enum range. Slots are bounds-checked in `SlotContainerLogic.Slot`. Gear moves (including a body's gear) are type-checked.
- **New requests:** `LockDoor` (5/s) and `DragBody` (5/s) are rate-limited. Interact, MoveItem and DropItem keep their earlier limits.
- **Instance scoping:** door changes, the clock and the loot views go only to peers in that city.

### 10. Keys can't be forged (OK)

- Items live on the server. A client can't create a key or change its data.
- Lock codes are predictable from the seed, but knowing a code grants nothing without the item.
- Keys aren't sold or offered by the supply, and aren't generated as random loot.

## Recommended order

1. Finding 1: guard `--dev-supply` before any shared or public server.
2. Findings 2, 3 and 7: city cap and cooldown; a `TakeAll` request; rate limits for body and supply moves.
3. Findings 4 and 5: server-side movement validation (already planned).
4. Finding 6: once coins are worth something.
