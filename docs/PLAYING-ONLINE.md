# Playing with a friend over the internet

How to host a session on your PC and have a friend somewhere else join it. The game is server-authoritative: you run the **server** (the world), and you and your friend each run a **client**.

You need three things:

1. Your friend has a copy of the game, exactly the same version as yours.
2. Your friend's PC can reach yours, using one of the two network options below.
3. You run the server; you both connect to it.

---

## 1. Give your friend the game

Both sides must run **the same commit**. A mismatch shows up as a refused connection, or a city that fails to load ("made with different building designs").

### Option A: an exported build (easiest for your friend)

There's no export preset in the project yet; you make one once:

1. Open the editor: `E:\claude\tools\godot.cmd -e --path E:\claude\GodotExploreTogether` (it keeps Godot's data on E:).
2. **Editor > Manage Export Templates > Download and Install** for 4.7.2 (.NET).
3. **Project > Export > Add... > Windows Desktop**. Keep the defaults and **Export Project** into an empty folder, for example `E:\claude\_tmp\export\ExploreTogether.exe`.
4. Zip the whole folder: the `.exe`, the `.pck` and the `data_ExploreTogether...` folder (the .NET assemblies). Send the zip.

Your friend unzips it anywhere and runs `ExploreTogether.exe`. They don't need Godot or .NET installed.

Re-export and resend whenever you change code or scenes.

### Option B: run from source

Your friend installs:

- Godot 4.7.2 **.NET** (mono).
- The .NET 10 SDK.

Then they take a copy of the repository at the same commit as yours (a zip of the folder works; leave out `.localdata` and `.godot`). Once, in the project folder, they run:

```
dotnet build ExploreTogether.sln
"<their Godot folder>\Godot_v4.7.2-stable_mono_win64_console.exe" --headless --path . --import
```

After that they run the client with:

```
"<their Godot folder>\Godot_v4.7.2-stable_mono_win64.exe" --path <project folder>
```

---

## 2. Let your friend reach your PC

The server listens on **UDP port 24565** (change it with `--port N` on the server, and add `:N` to the address your friend types).

### Option 1 (recommended): a private network with Tailscale

No router changes, and it works behind most home connections.

1. You and your friend each install [Tailscale](https://tailscale.com/download) and sign in.
2. Bring your friend's PC into your Tailscale network: in the Tailscale admin console, **share your PC** with them, or invite them as a user.
3. Find your PC's Tailscale address (it starts with `100.`) in the Tailscale tray icon. That's the address your friend connects to.

ZeroTier and Radmin VPN work the same way, if you prefer them.

### Option 2: forward the port on your router

1. Give your PC a fixed address on your home network (a DHCP reservation in the router).
2. In the router's port forwarding page, forward **UDP 24565** to that address.
3. Your friend connects to your public IP (look it up at a "what is my IP" site).

This doesn't work if your provider puts you behind CGNAT: your router's WAN address won't match your public IP. Use Option 1 then.

### Windows Firewall (both options)

Allow the server through, once, in an **administrator** command prompt:

```
netsh advfirewall firewall add rule name="ExploreTogether server" dir=in action=allow protocol=UDP localport=24565
```

Or click **Allow access** the first time Windows asks about the Godot console. With Tailscale, tick **Private** and **Public**.

---

## 3. Run the session

### You (host)

The quickest way starts the server and your own client:

```
tools\dev-run.cmd 1
```

Extra server options go after the client count. For example, a city that starts at midnight:

```
tools\dev-run.cmd 1 --city-start-hour 0
```

The server window should say `listening on port 24565`. It saves to `.localdata\server.db`.

Note that `dev-run` turns on the hub's **debug supply** (every item, free), which is handy with a trusted friend. To host without it, start the server yourself:

```
set DOTNET_ROOT=E:\claude\tools\dotnet
set PATH=E:\claude\tools\dotnet;%PATH%
"E:\Godot Install\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe" --headless --path E:\claude\GodotExploreTogether -- --server --db .localdata\server.db
```

Then start your own client with `E:\claude\tools\godot.cmd --path E:\claude\GodotExploreTogether`.

### Your friend

1. Start the game.
2. In the login screen, set **Server address** to your address and the port, for example `100.101.102.103:24565` (Tailscale) or `203.0.113.7:24565` (port forwarding).
3. Pick a username and password and press **Register** the first time; **Log in** afterwards. Accounts live on your server.

A shortcut can fill in the address, or log straight in:

```
<game or Godot exe> --path <project folder> -- --connect 100.101.102.103:24565
<game or Godot exe> --path <project folder> -- --connect 100.101.102.103:24565 --user Name --password Secret
```

The first pre-fills the login screen. The second registers (or logs in) and goes straight to character selection. With an exported build, drop `--path <project folder>`.

Once in, the hub's **City Gate** takes you both to the same city. Dungeons are joined from the dungeon menu.

---

## Troubleshooting

| Problem | Try |
|---|---|
| Friend gets "connection failed" | Is the server running (`listening on port 24565`)? Can they reach you at all (`ping <your address>`)? Is the firewall rule there? With port forwarding: is it **UDP**, to the right PC, and are you not behind CGNAT? |
| Connects, but the city doesn't load / "different building designs" | You're on different versions. Re-export (A) or copy the same commit (B). |
| "Failed to load .NET runtime" (Option B) | Install the .NET 10 SDK and Godot **.NET** 4.7.2 (not the standard build). |
| Rubber-banding (snapped back while walking) | The server rejected moves (lag spikes over about 1.5 s, or a speed it doesn't expect). The server log says `rejected a move`. Brief spikes are normal over the internet. |
| Friend can't log in | Accounts are per server: they need to **Register** on yours first. |

## Good to know

- Traffic isn't encrypted, and the server trusts clients' own movement within limits (see `docs/26-11-16/PROJECT-SECURITY-REVIEW-2026-11-16.md`). That's fine with people you trust; don't host it publicly as is.
- Closing the server window ends the session for everyone; progress is saved to `.localdata\server.db`.
- To stop everything started by `dev-run`: `tools\dev-stop.cmd`.
