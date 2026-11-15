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
- **Coordinates:** both engines are Y-up and use the X/Z plane, so the grid positions carry over unchanged. Two things differ:
  - Godot's "forward" is −Z where Unity's is +Z. This only matters for which way "north" faces the camera.
  - **A positive Y rotation turns the opposite way** (Unity: clockwise seen from above; Godot: counter-clockwise). Piece rotations like `rotation_degrees = 90` must be negated when placing them.
  - The piece meshes are checked once on import, so their open sides line up.
- **Sending it to clients:** the server generates the maze and sends the resulting node list (positions and links) to clients joining the instance. This is small (about 1,000 nodes at most) and needs no changes to the generation logic, not even a seed. Clients then build the pieces locally.

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
- SQLite, migrations, register/login, create/select character.
- Character position and basic stats saved and restored.
- **Done when:** you can quit, log back in and get the same character back.

### Phase 3 — Instances and dungeons
- `InstanceManager` with separate worlds and per-instance network filtering.
- Port `Maze.cs` as described above, plus the dungeon piece scenes using the old textures.
- Hub → dungeon flow: a player creates a dungeon instance, others join it (including mid-run), and the maze layout is sent on join.
- **Done when:** two separate groups run two different dungeons on one server at the same time, and neither group can see or affect the other.

### Phase 4 — Key and exit (first full loop)
- Interaction system (port of `IInteraction`): look at an object, press E, and the client sends a request to the server.
- Networked pickups and a minimal inventory (port of `Inventory`), saved to the database.
- Key: any player picking it up opens the door for the instance. Exit: each player finishes individually by walking through, and the run is complete when everyone has. Results are recorded, and players return to the hub.
- **Done when:** a full co-op run works from hub to dungeon to exit and back to the hub, with the result in the database.

### Phase 5 — Enemies and combat
- Port `NPC`/`GenericNPC`/`VisionSystem` as server-run AI on each instance's navigation map.
- Health and damage handled by the server. Melee first, then guns and magic.
- Death and respawn rules (hardcore death returns as an option).

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
