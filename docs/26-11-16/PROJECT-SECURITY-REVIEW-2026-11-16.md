# Security review: whole project (2026-11-16)

**Scope:** the whole Godot project as of `proto/city`:
- accounts and login
- the network layer and request handling
- persistence (SQLite)
- items and item tracking
- dungeons, combat, the hub
- the city prototype
- the dev tooling

**Builds on:**
- [ITEM-SECURITY-REVIEW-2026-11-15.md](../26-11-15/ITEM-SECURITY-REVIEW-2026-11-15.md): items, duplication, saves.
- [CITY-SECURITY-REVIEW-2026-11-16.md](CITY-SECURITY-REVIEW-2026-11-16.md): everything city-related.

Their findings are summarized here, not repeated in detail.

**Overall:** sound for a co-op game in development. The server owns all game state, every client request is filtered and rate-limited, the SQL is parameterized, and password storage follows current practice.

What stands between this and public hosting is mostly known and listed:
- traffic isn't encrypted,
- movement is trusted,
- dev switches aren't guarded,
- login can be brute-forced across connections.

## Summary

| # | Area | Finding | Severity | Status |
|---|---|---|---|---|
| 1 | Network | ENet traffic is unencrypted, so passwords cross the wire in plain text | **High** for public hosting | Known (PLAN phase 2); open |
| 2 | Dev tooling | Server dev switches (`--dev-supply`, `--dev-starter-kit`, `--dev-skip-proximity`, `--dev-kill-npcs`, …) aren't guarded | **High** if one leaks into a public server | Open |
| 3 | Gameplay | Client-driven movement isn't validated (teleporting, speed, and every reach check built on it) | **Medium–High** | Known; open |
| 4 | Auth | Login is rate-limited per connection only: brute force across reconnects, and PBKDF2 work makes CPU exhaustion possible | **Medium** | Open |
| 5 | Auth | Registration reveals whether a username exists | Low | Open |
| 6 | DoS | City creation is unlimited; body and supply moves save on every drag (city review findings 2, 3) | Medium | Open |
| 7 | Items | Earlier item findings: rate limits, relay off, retired ids, deferred saves (item review) | — | Mostly fixed |
| 8 | Persistence | SQL is parameterized; migrations back up first; paths are explicit | — | OK |
| 9 | Passwords | PBKDF2-SHA256, 100k iterations, random 16-byte salt, constant-time compare, hashed off the game thread | — | OK |
| 10 | Requests | All 16 client-to-server RPCs pass `Accept()` (server-only, rate filter); the server re-checks state and reach | — | OK |
| 11 | Client trust of the server | Clients trust whatever server they connect to | Low (by design) | Accepted |

## Findings

### 1. Unencrypted traffic (High for public hosting)

**What happens:** ENet sends everything in clear: login and registration passwords, and all game traffic. On any shared network, credentials can be read and sessions hijacked or spoofed.

**Recommendations:**
- Enable ENet DTLS (`ENetConnection.DtlsServerSetup`/`DtlsClientSetup`) with a real certificate, or move login to Steam authentication as planned.
- Until then, never host beyond a trusted LAN or localhost, and tell testers not to reuse real passwords.

### 2. Dev switches aren't guarded (High if misused)

**What happens:** the server accepts these dev switches:

| Switch | What it does |
|---|---|
| `--dev-supply` | Infinite items |
| `--dev-starter-kit` | Free weapons for new characters |
| `--dev-skip-proximity` | Disables every reach check |
| `--dev-kill-npcs` / `--dev-knockout-npcs` | Downs residents when a city opens |
| `--dev-enemies-near-start` | Puts dungeon enemies at the entrance |
| `--dev-log-enemies` / `--dev-log-npcs` | Verbose logging |

Each one is checked independently, and nothing stops them on a public server. `tools/dev-run.cmd` turns on `--dev-supply` by default. `--dev-skip-proximity` in particular turns every interaction into a remote action.

**Recommendations:**
- Group them behind one master switch (e.g. `--dev`), and refuse it unless the server listens on localhost only.
- Log a warning at startup listing every active dev switch.
- Strip them from release server builds with a `#if DEV` symbol in the export preset.

### 3. Movement isn't validated (Medium–High)

**What happens:** each client moves its own player, and the server accepts the position (`ApplySubmittedTransform` has a "validation belongs here later" note). Everything that uses `NetPosition` inherits this:
- reach checks (`IsNear`),
- the proximity check for the dungeon exit and storage,
- the "inside" door unlock,
- dragged bodies,
- the city's residents avoiding the player.

A modified client can teleport, speed-hack, unlock any city door, loot remotely within 5 m of any target, and the like. This is known and on the "before public hosting" list.

**Recommendations:**
- Validate on the server: maximum speed per tick, plus a sweep against the instance's colliders so players can't pass through walls.
- Snap the client back when it fails.
- Then base reach and inside checks on validated positions only.

### 4. Login brute force and CPU exhaustion (Medium)

**What happens:**
- `Login` and `Register` are limited per connection (1/s, burst 3). A client that reconnects gets a fresh allowance, so passwords can be guessed indefinitely.
- Each attempt runs 100k-iteration PBKDF2 on the thread pool. Up to 64 connections retrying keeps the server's CPU busy.
- There's no per-account or per-IP lockout, and no delay after failures.

**Recommendations:**
- Count failures per account and per remote address, across connections, with a growing delay or a temporary lockout.
- Cap concurrent password verifications, e.g. with a semaphore of 2–4.
- Refuse new connections from an address that's over its limit.

### 5. Usernames can be enumerated (Low)

**What happens:** login correctly answers "Invalid username or password." for both cases. Registration, however, says when a name is taken, which reveals that the account exists.

This is common and hard to avoid with unique usernames, but combined with finding 4 it helps targeted guessing. Fixing finding 4 is the real mitigation. Accept the leak or rate-limit registration per address.

### 6. Resource exhaustion in the city (Medium)

Covered in the city review: unlimited city creation (finding 2), and a database save on every body or supply drag (finding 3). Fix them before shared hosting.

### 7. Items (mostly fixed)

The item review's findings were addressed:
- request rate limits with disconnect on abuse,
- the replication relay turned off,
- retired item ids instead of deletions,
- deferred saves for ordinary moves,
- serials with a duplicate audit at startup and while playing.

New item paths (looting, the supply, the locksmith) register serials and log events consistently (see the city review). The `--audit-items` tool remains the check after any incident.

### 8. Persistence (OK)

- **Parameterized SQL:** every value goes in as a `$parameter`. The only text built into queries is internal: column lists, and table names chosen in code (`inventory_items` / `storage_items`). No player-supplied text reaches a query string.
- **Migrations:**
  - The database is backed up before an upgrade, keeping the newest 5.
  - A newer database than the code is refused.
  - A failed step leaves the file at its last good version.
- **Paths:** the database and logs go to explicit, configurable paths, not `user://`.
- **At rest:** the database file isn't encrypted. Rely on file-system permissions on the host, and keep backups private. They hold password hashes.

### 9. Passwords (OK)

PBKDF2-SHA256, 100,000 iterations, a 16-byte random salt per account, and a scheme and iteration prefix so stronger settings can be rolled in later. Verification compares in constant time and runs off the game thread. One login per account at a time.

Optional hardening: raise the iterations to about 600k, the current OWASP figure for PBKDF2-SHA256, once finding 4 caps concurrent verifications.

### 10. Request handling (OK)

- **Filtering:** every `AnyPeer` RPC (16 of them) goes through `NetSession.Accept()`, which drops anything not running on the server and applies the per-request rate limits.
- **Re-checks:** handlers re-check session state (logged in, character selected, in an instance) and reach before acting.
- **Bounds and ids:** container and slot indices are bounds-checked in `SlotContainerLogic`. Interaction ids resolve only to objects that exist in the player's own instance.
- **Visibility:** replication is per instance (visibility filters), so clients don't receive other instances' state.

### 11. Clients trust the server (Low, by design)

A client follows whatever server it connects to: instance layouts, item data, notices. A malicious server could send oversized layout arrays or odd values.

The worst cases are a crash or a confusing screen, not code execution: data is parsed as ints, and layouts are validated by `MazeLayout.Import` / `CityLayout.Import`.

Players should only join servers they trust, and Steam authentication later makes server identity clear.

## Before any public or shared hosting

1. Encrypt traffic (finding 1).
2. Guard or strip the dev switches (finding 2).
3. Validate movement on the server (finding 3).
4. Limit login per account and address, and cap concurrent verifications (finding 4).
5. Cap cities and rate-limit body and supply saves (finding 6).

LAN play with trusted friends is fine as it is.
