# ExploreTogether (Godot 4.7 / C#)

Co-op dungeon game. The plan and its decisions are in `docs/PLAN.md`; read it before changing architecture. The original Unity version is at `E:\claude\_reference\ExploreTogether` (port its maze logic faithfully; see PLAN.md).

See `E:\claude\CLAUDE.md` for the environment setup (the .NET 10 SDK on E:, never writing to C:).

## Layout
- `core/`: `ExploreTogether.Core`, plain C# with **no Godot references** (maze generation, rules, persistence). Unit-tested.
- `tests/`: xUnit tests for `core/`.
- `scripts/`: Godot-side C# (`Core`, `Net`, `Server`, `Client`, `Dungeon`, `City`, `Player`, `Items`, `Persistence`, `UI`).
- `scenes/`, `assets/`: Godot scenes and imported assets. **Only use art from the original `Resources/Prefabs/Dungeon` folder** (image assets by a friend; the dungeon prefabs are the owner's own), plus the torch it references; see `assets/README.md`.
- `core/` and `tests/` contain `.gdignore` so Godot doesn't scan them. The Godot csproj excludes them from compilation and references Core as a project.

## Commands (with the env from E:\claude\CLAUDE.md)
- Build: `dotnet build ExploreTogether.sln`
- Test: `dotnet test ExploreTogether.sln`
- Smoke test (server + headless clients through city, crowd, doors, drag, breach, floors, stealth, furniture, power, hotbar, co-op, combat, death and gathering scenarios; pass/fail, ~12 min): `tools\smoke-test.cmd [-Only city,crowd,doors,drag,breach,floors,stealth,furniture,power,hotbar,coop,combat,death,gather] [-NoBuild]`. From PowerShell, pass `-Only` a comma list directly (`& .	ools\smoke-test.ps1 -Only a,b`); through `cmd` it arrives as one string. Don't build or edit scenes while it runs (the clients load the build and `res://` scenes when they start). Logs in `E:\claude\_tmp\smoke\<scenario>\`. Run after changes to networking, dungeons or combat.
- Item tracking tools (server args; print a report and quit, add `--db <path>` for another database): `--audit-items` (duplicated/unregistered item serials; exit code 1 if any), `--item-history SERIAL`, `--item-log CHARACTER`. E.g. `<godot>_console.exe --headless --path . -- --server --audit-items`.
- Play locally: `tools\dev-run.cmd [clients]` (closes any previous session, builds, then opens a server console and client windows that auto-log in as Kermit, Blueberry, ...)
- Close a dev session: `tools\dev-stop.cmd`
- Dev database: `.localdata/server.db` (git-ignored). Use `--db <path>` on the server for a throwaway test database.
- Run the server headless: `<godot>_console.exe --headless --path . -- --server`
- Run a client: `<godot>_console.exe --path . -- --connect 127.0.0.1 --name Alice`
- Scripted network test: run a server plus headless clients with `--dev-quit-after N`, `--dev-autowalk`, `--dev-log` and read their output (see Main.cs and ClientGame.cs for all args). Use a separate `--port` and `--db` so it doesn't clash with the user's `dev-run` session.
  - Dungeons: server `--dev-skip-proximity --dungeon-empty-timeout 3`; clients `--dev-dungeon new[:WxHxLEN]` or `join:NAME`, `--dev-return-after S`, `--dev-check-walls`, `--dev-solve` (take everything, open exit, escape), `--dev-escape-after S`.
  - Combat: server `--dev-starter-kit` (new characters get every weapon), `--dev-enemies-near-start`, `--dev-log-enemies`; clients `--dev-weapon NAME`, `--dev-fight` (auto-attack the nearest enemy), `--hardcore` (auto-created character is hardcore).
  - City (plan in `docs/26-11-16/CITY-PROTOTYPE-2026-11-16.md`): the City Gate opens a menu (join a running city, or a new one with size, start hour, people, day length, seed); the server args below are its defaults, and `--dev-city` clients skip the menu (join whatever city others are in, else make one with the defaults). Server `--city-size N --city-seed S --city-loops P --city-favor NAME` (place a custom building design wherever it fits; designs are scenes in `scenes/city/buildings/`, see `docs/CITY-BUILDINGS.md`); `tools\dev-run.cmd [clients] [server args...]` passes extra server args; server residents: `--city-people N` (default 2.4 per tile; 0 = nobody) `--city-start-hour H` (default 0) `--city-day-minutes M --dev-log-npcs`; clients `--dev-city` (go through the hub's City Gate), `--dev-check-city` (also reports residents in view), `--dev-overview angle|top|near` (with `--dev-screenshot`), `--dev-return-after S`, `--dev-chase-npc` (run at visible walking residents; logs the closest approach). Doors, loot, dragging: server `--dev-kill-npcs N`, `--dev-knockout-npcs N`, `--city-wake-seconds S`; clients `--dev-drag` (grab the nearest body, walk 4 s, let go); clients `--dev-doors` (search a body, take all, use its key on its door), `--dev-fight` (also shoots visible residents in a city), `--dev-open-nearest-door`.
  - Stealth and breaking in (see `docs/26-11-16/STEALTH-AND-ROLES.md`): server `--dev-oblivious` (residents ignore players); clients `--dev-breach` (kick, pick and shoot out three locked doors; needs `--dev-starter-kit`), `--dev-trespass` (walk into an occupied building), `--dev-grab` (grab a passer-by), `--dev-struggle` (mash free when held), `--dev-shoot-air [--dev-shoot-delay S]`, `--dev-upstairs [--dev-shots DIR]` (climb to a top floor, screenshots on the way). Smoke scenarios `breach`, `floors`, `stealth`.
  - The server validates player movement (`MoveGuard`, wall rays): dev automation must walk for real (`PlayerCharacter.DevWalkTarget`), not teleport.
  - Furniture, power, hotbar, flashlight: client `--dev-chair` (take the nearest chair, hold it), `--dev-power` (fuse box, street lamp, light switch, substation, main breaker; needs a pistol and `--dev-skip-proximity`), `--dev-hotbar` (bind and use keys, then the flashlight and the two-hands rule).
  - Gathering/storage: client `--dev-weapon Axe --dev-gather N` (chop the hub trees until carrying N logs, then store them; needs the server's `--dev-skip-proximity --dev-starter-kit`).
  - Screenshots without disturbing the user: a windowed client with `--resolution 800x450 --position -3000,100` plus `--dev-camera YAW,PITCH --dev-screenshot E:\claude\_tmp\shots\x.png`, then view the PNG.
- Dungeon pieces: regenerate with `python -I tools/import_unity_dungeon.py E:/claude/_reference/ExploreTogether`, then re-import.
- Transforms written by hand in `.tscn` files are **row-major**: `Transform3D(row0, row1, row2, origin)`.
- After adding or changing assets, run `<godot>_console.exe --headless --path . --import` before headless runs.

- Audio: `scripts/Audio/GameAudio.cs` (client), server sounds via `SoundId` (`core/Audio/Sounds.cs`) and `ServerGame.Audio.cs`. Files are looked up by name in `assets/audio/` (real) then `assets/audio/placeholder/` (generated by `python -I tools/generate_placeholder_sounds.py`). What exists and what to source: `docs/26-11-16/AUDIO-REPORT.md`. Clients take `--mute` / `--volume DB`. F1 in game (or `--dev-sound-monitor`) lists the sounds playing and what each imitates.
- Playing with a friend over the internet (export, Tailscale or port forwarding, firewall): `docs/PLAYING-ONLINE.md`.

## Conventions
- The dedicated server is authoritative. Clients send requests; the server validates and broadcasts. Only a client's own movement is client-driven.
- Files the game writes (database, logs) go to explicit, configurable paths, not hard-coded `user://` (which defaults to C:).
- Target framework: net10.0.
