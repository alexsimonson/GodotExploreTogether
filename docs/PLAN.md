# ExploreTogether (Godot rebuild) — Plan

A rewrite of the Unity ExploreTogether project (reference copy at `E:\claude\_reference\ExploreTogether`) in Godot 4 with C#. Multiplayer is designed in from the start. This is a code overhaul; the art overhaul is a separate discussion.

## Decisions

| Topic | Decision |
|---|---|
| Engine | Godot 4.7.2 .NET build, at `E:\Godot Install\Godot_v4.7.2-stable_mono_win64` |
| Language | C# (.NET 8 SDK) |
| Network model | **One dedicated server** that all players connect to. No player hosts. |
| Instances | One server runs many dungeon instances at once, fully separate from each other |
| Core loop | Log in → pick character → enter a dungeon instance with your group → find the key → everyone reaches the exit |
| Dungeon generation | **Keep the original recursive backtracker logic.** Port only the Unity-specific parts (types, random, coordinates/rotation) |
| Key | Shared by the team. Once the exit door is opened, it stays open for the whole instance |
| Win condition | Every player must reach the exit themselves |
| Joining | Players can join an instance mid-run |
| Persistence | SQLite on the server. Characters belong to player accounts |
| Login | Username + password for now (hashed on the server). Steam login later |
| Platforms | Client: Windows, Linux, macOS. Server: headless build (Windows or Linux). Web deferred |
| Art | Only the original dungeon art in `_reference/.../Resources/Prefabs/Dungeon` (see `assets/README.md`) |
| Disk | Everything on E:. NuGet cache redirected to E: |

## Architecture

### One codebase, two roles
The same Godot project runs as either:
- **Server:** started with `--server`, usually headless (no window). Owns the database, all instances and all game rules.
- **Client:** the normal game, connecting by address. It renders only the one instance its player is in.

During development, you run the server and one or more clients on the same PC. A launch script or editor run configuration starts them all at once.

### The server is the authority
Clients send requests ("open this door", "pick up item 12"). The server checks them and announces the result. **Movement is the exception:** each client moves its own player and the server relays the position. That keeps controls responsive without writing prediction code, and cheating doesn't matter much in co-op. We can tighten it later.

### Instances
Each instance (the hub, or one group's dungeon run) is a separate subtree on the server with **its own physics world and navigation map**, using Godot's `World3D`. That gives:
- **Nothing can leak between instances.** Collisions, raycasts, enemy pathfinding and area triggers in one instance can't touch another.
- **Every instance uses the same coordinates** (start cell at 0,0,0), so no offset bookkeeping.
- **Network filtering per instance:** a client only receives updates (players, items, enemies) for the instance it's in.

The alternative is putting each instance at a different spot in one shared world, e.g. 10 km apart. It's simpler at first, but every system then has to avoid accidentally seeing other instances, and far-away coordinates lose floating-point precision. I recommend separate worlds; it's a contained decision if you'd rather do it the other way.

Instances live at matching node paths on server and client (e.g. `/root/Game/Instances/<id>/...`), which Godot's replication needs.

An `InstanceManager` on the server creates, tracks and shuts down instances, and moves players between them. An empty dungeon instance is closed after a timeout.

### Dungeon generation (preserved)
The original `Maze.cs` algorithm is kept as-is: the recursive backtracker over a `max_width × max_height` grid, stopping at `max_dungeon_length`, then choosing corner/hall/T/X/C pieces from neighbor counts. Only these change:
- **Unity types:** `Vector3`, `ScriptableObject` and `Random.Range` become Godot/C# equivalents. `Random.Range(int a, int b)` already excludes `b`, which matches `System.Random.Next(a, b)`.
- **Coordinates:** both engines are Y-up and use the X/Z plane, so grid positions carry over unchanged (x → X, z → Z; north = +Z, east = +X).
  - **Rotations also carry over unchanged.** (An earlier version of this plan wrongly said to negate them.) A +90° Y rotation sends +Z to +X in both engines. Unity draws that as clockwise from above and Godot as counter-clockwise, but the coordinate transform is identical. Negation would only be needed if an axis were flipped.
  - Proven twice: `MazePiecesTests` checks every placement's openings against its neighbors, and the in-engine wall check (`--dev-check-walls`) raycasts every cell side of the built geometry.
- **Seeded:** `UnityEngine.Random` → a seeded `System.Random`, so any dungeon can be rebuilt from (seed, width, height, length). The server logs the seed.
- **Sending it to clients:** the server generates the maze and sends the node list in the original `ExportMaze` shape (position and neighbor indices per node) when a client joins. Clients rebuild the pieces locally.

### Accounts and persistence
- SQLite file on the server, accessed with `Microsoft.Data.Sqlite`.
- Tables: `accounts` (username, password hash), `characters` (belong to an account), `inventory_items`, `instances`/`runs` (for history and stats). Versioned migrations from day one.
- Character data loads at login and is saved at checkpoints: leaving an instance, finishing a run, disconnecting, and on a periodic autosave.
- A dev setting skips the login screen while testing.

### Swappable pieces
- **Transport:** ENet now. Steam's networking can replace the client-to-server connection later through Godot's `MultiplayerPeer`, but a dedicated server more commonly keeps ENet and uses Steam only for login and friends/invites. We'll decide when we get there.
- **Logic stays separate from presentation:** maze generation, inventory rules and persistence are plain C# classes, unit-tested with no Godot dependencies.

## Phases
Each phase ends with something runnable.

### Phase 0 — Setup
- ✅ Godot 4.7.2 .NET and the .NET 10 SDK on E:. NuGet caches on E:.
- ✅ Godot C# project (targeting net10.0), `.gitignore`, git repo, project `CLAUDE.md`, folder layout:
  `scenes/`, `scripts/` (Godot-side code, split into `Core/`, `Net/`, `Server/`, `Client/`, `Dungeon/`, `Player/`, `Items/`, `Persistence/`, `UI/`), `core/` (Godot-free logic library), `tests/` (xUnit), `assets/`.
- ✅ **Done:** the project runs as server (`-- --server`) and as client on Godot 4.7.2 / .NET 10.0.15, and `dotnet test` passes.

### Phase 1 — Server and two clients
- ✅ `--server` startup path with headless mode. Client connect menu: name + server address.
- ✅ Hub instance (floor, walls, crates using the old textures), already built through the instance system: a `SubViewport` with its own world on the server, and visibility per instance.
- ✅ Join handshake: `RequestJoin` → `EnterInstance` → client builds the instance → `InstanceReady` → server spawns the player.
- ✅ First-person controller (walk, sprint, jump, mouse look). Clients send their own transform; the server relays it to the instance.
- ✅ Clean disconnects in both directions.
- ✅ **Done:** verified with a headless server and two headless clients (`--dev-autowalk`, `--dev-log`). `tools/dev-run.cmd` opens a server plus client windows for manual play.

### Phase 2 — Accounts and characters
- ✅ SQLite (`Microsoft.Data.Sqlite` 10.x) in `core/Persistence`, with versioned migrations (`PRAGMA user_version`). Steps can be SQL or C#.
  - Before upgrading an existing database, the server backs it up (`server.db.v{N}.{time}.bak`, newest 5 kept).
  - The server refuses to open a database newer than its code.
  - A failing step leaves the file at the last good version.
  - `TempDatabase` in the tests builds an old-version database with sample rows, to prove each upgrade keeps data.
  - Data policy: shipped steps may still be edited until real player data matters; after that, only add new steps.
- ✅ Register/login with PBKDF2 password hashes (computed off the game thread). One login per account at a time.
- ✅ Character select: up to 5 characters per account, names unique server-wide, color chosen at creation.
- ✅ Hub position and play time saved on disconnect, autosaved every 15 s and at shutdown, and restored on login.
- ✅ Dev auto-login (`--user/--password/--character/--color`); `dev-run.cmd` uses it. `dev-stop.cmd` closes a dev session.
- ✅ **Done:** verified with two headless sessions against one database (the character comes back at its saved spot), plus wrong-password and double-login rejection. 30 unit tests.
- ⚠️ **Before hosting publicly:** ENet traffic is unencrypted, so passwords cross the network in plain text. Enable ENet's DTLS (or move login to Steam auth) first.

### Phase 3 — Instances and dungeons
- ✅ `InstanceManager`: each instance has its own world. Player visibility is per instance and is revoked both ways on leaving. Movement is tagged with the instance id, so stale packets are dropped. Empty dungeons close after 60 s (`--dungeon-empty-timeout`).
- ✅ Faithful port of `Maze.cs` (`core/Dungeon/Maze.cs`) and of the piece/rotation selection (`MazePieces`). Original settings by default: 5×5, length 400.
- ✅ Dungeon pieces reconstructed from the Unity prefabs (`tools/import_unity_dungeon.py`): exact ProBuilder geometry with pillars, the original materials, collision, and the torches (1 in the T, 4 in the X) as a Godot `Torch.tscn` (light + fire particles + model).
  - The pieces' floor is 1 m thick, so the dungeon is lowered 1 m and its floor surface is at y = 0.
  - Lessons: Godot's `.tscn` `Transform3D(...)` text is **row-major** (verified in-engine). Unity mirrors FBX models on X at import and Godot doesn't, so the torch model is mirrored back.
- ✅ Interaction system (port of `IInteraction`): look and press E. The client raycasts; the server checks the player is within 5 m.
- ✅ Hub ↔ dungeon flow through the `DungeonEntrance`: the hub's entrance opens a menu to join a running dungeon (mid-run is fine) or start one (width/height/length). The entrance at node 0 leads back to the hub, which you re-enter in front of its entrance.
- ✅ Players have their own physics layer ("players"), separate from "world".
- ✅ **Done:** four headless clients, with two groups in two separate dungeons, one player joining mid-run and returning to the hub, and one staying in the hub. Each client saw exactly the players in its own instance. The wall check passed on every side of 5×5, 12×9 and 30×30 (900-cell) dungeons. The proximity check was verified for both allow and reject. 60 unit tests.
- Not yet: navigation baking (needed for enemies in phase 5), key/exit (phase 4).

### Phase 4 — Key and exit (first full loop)
- ✅ Interaction system (done in phase 3).
- ✅ Item bank ported (`core/Items/ItemCatalog`: the 16 original items, same ids). Inventory ported (`Inventory`: 28 slots, stackables stack).
- ✅ Dungeon contents ported from `FillMazeWithItems`: the Dungeon Pass (999) plus 20 random items on nodes in [5, count − 5). Seeded, with small-maze guards.
- ✅ Networked pickups: items are replicated per instance (spawner + visibility, as for players), so mid-run joiners see what's left. Look + E, validated by the server.
- ✅ The Dungeon Pass is the shared key. Whoever holds it unlocks the exit (at the last node, like the original Objective), and the pass is used up. The exit then stays open for the whole instance. The pass is never saved; if its holder leaves, it drops where they stood.
- ✅ Each player escapes on their own at the open exit and returns to the hub. The run ends when the dungeon empties: `completed` if anyone escaped, else `abandoned` (`interrupted` on server shutdown).
- ✅ Database v2: `inventory_items`, `runs`, `run_participants` (joined/escaped times). Upgrade from v1 tested with `TempDatabase`.
- ✅ Players collide with each other **horizontally**, in code (`PlayerCharacter`: `BlockByOtherPlayers` and `SeparateFromOtherPlayers`), not through the physics engine.
  - Why: with client-driven movement, physics-based overlap resolution pushed stacked players up out of each other's copies on both clients, a loop that launched them into the sky.
  - Now each client stops its own player at another's edge and slides it sideways out of any overlap, so it never moves vertically.
  - Trade-off: you can't stand on a teammate's head; that would need server-side resolution.
  - Arrivals are also placed at the nearest free spot. Test with `--dev-chase`.
- ✅ Inventory & equipment UI ported from the Unity version: a drag-and-drop inventory grid, a 12-slot paper-doll equipment panel, item icons, Ctrl + right-click to drop, and an equipped-weapon readout. Gear is saved (database v3). The server applies the original drop rules. Review notes: `docs/inventory-port-notes.md`. Redesign pending review.
- ✅ Client: Tab inventory panel, dungeon status in the top-right corner (pass holder, exit state, who escaped, who's inside), notices.
- ✅ **Done:** two groups at once, headless. In one, a player opened the exit and escaped, a mid-run joiner escaped too, and a third left early, recorded as "completed, 2/3 escaped". In the other, the pass was dropped when its holder left, and the run was recorded as "abandoned". Inventory survives a re-login without the pass. 76 unit tests.

### Phase 5 — Enemies and combat
- ✅ Enemies: `GenericNPC`'s state machine ported to `core/Combat/EnemyBrain`, unit-tested. It runs on the server (`EnemyCharacter`) and is replicated per instance.
  - Prefab values: 100 health; chases at 5 within 10 m; attacks within 2 m, a 2 s attack dealing 20 at 1 s if the target is still within 2 m. Freeze and knockback.
  - 5 per dungeon, placed like `FillMazeWithItems` (nodes in [10, count − 10)).
  - Placeholder look (the user chose this; the original model wasn't used).
- ✅ Navigation: baked per dungeon on the server from the maze colliders (about 40 ms for 5×5). Enemies use `NavigationAgent3D`.
- ✅ Vision: same rule as `VisionSystem` (90°×45° view, 10 m, eye at 1.7 m, walls block), but checked directly per player instead of with 1,500 rays per frame.
- ✅ Player combat, decided by the server (`ServerGame.Combat.cs`), from the original numbers:
  - Melee/Tool: 4 m, 20 damage + knockback. Tool also uses interactables, as in the original.
  - Gun: 100 damage, magazine 10 + 30 reserve, R to reload. Ammo is per player; the original stored it on the shared item asset.
  - Magic: hold right-click to aim, left-click to place a 5 s area within 10 m that rolls 0–49 damage once per enemy entering. Frost freezes for 5 s; Blood heals the caster 10 per hit.
  - Added short cooldowns (the original had none).
- ✅ Health: 100, replicated, restored in the hub. HUD: health, weapon + ammo, crosshair, red flash when hurt.
- ✅ Death:
  - Non-hardcore: back to the hub keeping items (the run doesn't count you as escaped; the Dungeon Pass drops).
  - Hardcore: a checkbox at character creation (database v4). Death deletes the character with its inventory and gear, and returns the player to character select.
- ✅ Players stay out of enemies horizontally (same scheme as player-vs-player). Enemies don't collide with each other (avoids corridor deadlocks).
- ✅ **Done:** five dungeons at once, headless:
  - Kills with sword, pistol and both wands (Frost froze, Blood healed).
  - A non-hardcore death returned to the hub with items intact.
  - A hardcore death deleted the character.
  - A screenshot confirmed the look. 102 unit tests.
- Not ported: the original's enemy model and animations (placeholder for now), the held-weapon models, trees/resources, the Wave Survival mode.

### Later (not ordered)
- Steam login/friends/invites.
- Hub features: storage, skills, crafting/resources, wave survival mode.
- Inventory/gear UI polish. Art overhaul.
- Deploying the server somewhere other than this PC (a Linux VPS, etc.).
- Exports for Linux/macOS clients.

## Web export: deferred
A browser client would need a WebSocket transport instead of ENet. Unlike before, the database and hosting are no longer obstacles, since they live on the server. A server could accept both connection types later, so the door stays open.

## Open questions
None blocking right now.
