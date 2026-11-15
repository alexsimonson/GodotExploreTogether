# ExploreTogether (Godot 4.7 / C#)

Co-op dungeon game. The plan and its decisions are in `docs/PLAN.md`; read it before changing architecture. The original Unity version is at `E:\claude\_reference\ExploreTogether` (port its maze logic faithfully; see PLAN.md).

See `E:\claude\CLAUDE.md` for the environment setup (the .NET 10 SDK on E:, never writing to C:).

## Layout
- `core/`: `ExploreTogether.Core`, plain C# with **no Godot references** (maze generation, rules, persistence). Unit-tested.
- `tests/`: xUnit tests for `core/`.
- `scripts/`: Godot-side C# (`Core`, `Net`, `Server`, `Client`, `Dungeon`, `Player`, `Items`, `Persistence`, `UI`).
- `scenes/`, `assets/`: Godot scenes and imported assets.
- `core/` and `tests/` contain `.gdignore` so Godot doesn't scan them. The Godot csproj excludes them from compilation and references Core as a project.

## Commands (with the env from E:\claude\CLAUDE.md)
- Build: `dotnet build ExploreTogether.sln`
- Test: `dotnet test ExploreTogether.sln`
- Play locally: `tools\dev-run.cmd [clients]` (builds, then opens a server console and client windows)
- Run the server headless: `<godot>_console.exe --headless --path . -- --server`
- Run a client: `<godot>_console.exe --path . -- --connect 127.0.0.1 --name Alice`
- Scripted network test: run a server plus headless clients with `--dev-quit-after N`, `--dev-autowalk`, `--dev-log` and read their output (see Main.cs for all args).
- After adding or changing assets, run `<godot>_console.exe --headless --path . --import` before headless runs.

## Conventions
- The dedicated server is authoritative. Clients send requests; the server validates and broadcasts. Only a client's own movement is client-driven.
- Files the game writes (database, logs) go to explicit, configurable paths, not hard-coded `user://` (which defaults to C:).
- Target framework: net10.0.
