# ExploreTogether (Godot 4.7 / C#)

Co-op dungeon game. The plan and its decisions are in `docs/PLAN.md`; read it before changing architecture. The original Unity version is at `E:\claude\_reference\ExploreTogether` (port its maze logic faithfully; see PLAN.md).

See `E:\claude\CLAUDE.md` for the environment setup (the .NET 10 SDK on E:, never writing to C:).

## Layout
- `core/`: `ExploreTogether.Core`, plain C# with **no Godot references** (maze generation, rules, persistence). Unit-tested.
- `tests/`: xUnit tests for `core/`.
- `scripts/`: Godot-side C# (`Core`, `Net`, `Server`, `Client`, `Dungeon`, `Player`, `Items`, `Persistence`, `UI`).
- `scenes/`, `assets/`: Godot scenes and imported assets. **Only use art from the original `Resources/Prefabs/Dungeon` folder** (image assets by a friend; the dungeon prefabs are the owner's own), plus the torch it references; see `assets/README.md`.
- `core/` and `tests/` contain `.gdignore` so Godot doesn't scan them. The Godot csproj excludes them from compilation and references Core as a project.

## Commands (with the env from E:\claude\CLAUDE.md)
- Build: `dotnet build ExploreTogether.sln`
- Test: `dotnet test ExploreTogether.sln`
- Smoke test (server + headless clients through co-op, combat and death scenarios; pass/fail, ~90 s): `tools\smoke-test.cmd [-Only coop,combat,death] [-NoBuild]`. Logs in `E:\claude\_tmp\smoke\<scenario>\`. Run after changes to networking, dungeons or combat.
- Item tracking tools (server args; print a report and quit, add `--db <path>` for another database): `--audit-items` (duplicated/unregistered item serials; exit code 1 if any), `--item-history SERIAL`, `--item-log CHARACTER`. E.g. `<godot>_console.exe --headless --path . -- --server --audit-items`.
- Play locally: `tools\dev-run.cmd [clients]` (closes any previous session, builds, then opens a server console and client windows that auto-log in as Kermit, Blueberry, ...)
- Close a dev session: `tools\dev-stop.cmd`
- Dev database: `.localdata/server.db` (git-ignored). Use `--db <path>` on the server for a throwaway test database.
- Run the server headless: `<godot>_console.exe --headless --path . -- --server`
- Run a client: `<godot>_console.exe --path . -- --connect 127.0.0.1 --name Alice`
- Scripted network test: run a server plus headless clients with `--dev-quit-after N`, `--dev-autowalk`, `--dev-log` and read their output (see Main.cs and ClientGame.cs for all args). Use a separate `--port` and `--db` so it doesn't clash with the user's `dev-run` session.
  - Dungeons: server `--dev-skip-proximity --dungeon-empty-timeout 3`; clients `--dev-dungeon new[:WxHxLEN]` or `join:NAME`, `--dev-return-after S`, `--dev-check-walls`, `--dev-solve` (take everything, open exit, escape), `--dev-escape-after S`.
  - Combat: server `--dev-starter-kit` (new characters get every weapon), `--dev-enemies-near-start`, `--dev-log-enemies`; clients `--dev-weapon NAME`, `--dev-fight` (auto-attack the nearest enemy), `--hardcore` (auto-created character is hardcore).
  - Screenshots without disturbing the user: a windowed client with `--resolution 800x450 --position -3000,100` plus `--dev-camera YAW,PITCH --dev-screenshot E:\claude\_tmp\shots\x.png`, then view the PNG.
- Dungeon pieces: regenerate with `python -I tools/import_unity_dungeon.py E:/claude/_reference/ExploreTogether`, then re-import.
- Transforms written by hand in `.tscn` files are **row-major**: `Transform3D(row0, row1, row2, origin)`.
- After adding or changing assets, run `<godot>_console.exe --headless --path . --import` before headless runs.

## Conventions
- The dedicated server is authoritative. Clients send requests; the server validates and broadcasts. Only a client's own movement is client-driven.
- Files the game writes (database, logs) go to explicit, configurable paths, not hard-coded `user://` (which defaults to C:).
- Target framework: net10.0.
