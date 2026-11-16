# Dedicated dev server — setup plan

Goal: a spare PC running Linux that hosts the ExploreTogether game server and its SQLite database in containers, so the server runs all the time on the LAN and clients on the dev PC (or friends' PCs) connect to it.

Status: **plan only**, nothing built yet. Tasks are in [§8 Rollout checklist](#8-rollout-checklist).

---

## 1. The key decision: SQLite isn't a separate server

SQLite is a library that reads and writes a file. It doesn't listen on a port. Our game server already **embeds** it (`Microsoft.Data.Sqlite` in `core/Persistence`), and `PLAN.md` makes the game server the only owner of the database.

So "a SQLite server" in this setup means **the game server container plus the database file on a persistent volume**, with small helper containers around it:

| Container | Purpose | Always on? |
|---|---|---|
| `game-server` | Headless Linux export of the game, run with `--server --db /data/server.db`. Owns the database. | Yes |
| `db-backup` | Takes an online snapshot of the database (`sqlite3 .backup`) every 6 h and keeps 14 days. | Yes |
| `db-admin` | `sqlite-web`, a read-only browser UI for looking at tables. | Only when you start it |

**Rejected alternative:** a networked SQLite server (libSQL `sqld`, rqlite) with the game connecting over the network. It would mean rewriting `GameDatabase` against a different client, and losing what we rely on now: local transactions, atomic item saves, file-based migration backups (`server.db.v{N}.*.bak`) and `TempDatabase` tests. It only pays off with several game servers sharing one database, which isn't planned. If that changes, PostgreSQL is the better move.

---

## 2. Hardware

The server runs a headless Godot process: physics and navigation per instance, no rendering. Load is light for dev use.

| Part | Minimum | Recommended | Notes |
|---|---|---|---|
| CPU | 64-bit x86 (x86_64), 2 cores | 4+ cores from the last ~8 years | Each dungeon instance adds physics and enemy AI. Nav baking takes about 40 ms per 5×5 dungeon. Stick to x86_64, which matches the export target; ARM would need a separate export. |
| RAM | 4 GB | 8–16 GB | The OS and Docker take about 1 GB, the game server a few hundred MB. Room for extra test servers. |
| Storage | 64 GB SSD | 256 GB+ SATA or NVMe SSD | **Use an SSD.** SQLite waits for each commit to reach the disk, and a hard drive makes that slow. Keep the database on a **local** disk, never on a network share: NFS and SMB break SQLite's locking and can corrupt it. |
| Network | Wired Ethernet | Gigabit Ethernet | Avoid Wi-Fi, because ENet uses UDP and jitter hurts. |
| Power | — | A small UPS (optional) | WAL mode plus regular backups make sudden power loss survivable anyway. |

BIOS/UEFI settings:
- **Restore on AC power loss: Power On**, so the PC restarts by itself after an outage.
- Turn off sleep and hibernate if the BIOS offers them.
- Enable virtualization (VT-x/AMD-V). Docker doesn't need it, but it keeps the option of VMs open.
- Disable Secure Boot only if the distro installer has trouble with it. Ubuntu works with it on.

You need a monitor and keyboard only for the install. After that, manage it over SSH.

---

## 3. Operating system

**Ubuntu Server 24.04 LTS** (or Ubuntu 26.04 LTS once its first point release is out). Debian 13 works equally well. Reasons: long support, Docker's official packages, and the most how-to coverage. Install the **server** edition, which has no desktop.

During install:
- Filesystem: **ext4** on the SSD, using the whole disk. LVM is fine. Skip ZFS and btrfs; they work, but they add tuning work for SQLite.
- Hostname: e.g. `et-dev`.
- Create your admin user. Tick **Install OpenSSH server** and import your SSH public key.
- Don't pick Docker from the installer's "snaps" list. The snap version has known volume/permission quirks; install it from Docker's repository instead (§4).

After install:

```sh
sudo apt update && sudo apt full-upgrade -y
sudo apt install -y unattended-upgrades sqlite3 rsync htop
sudo dpkg-reconfigure -plow unattended-upgrades       # automatic security updates
sudo timedatectl set-timezone UTC                     # logs and run timestamps in UTC
timedatectl status                                    # "System clock synchronized: yes"
```

**Network:** give the box a fixed LAN address, preferably with a DHCP reservation in the router (e.g. `192.168.1.50`). Optionally add a hosts entry `192.168.1.50 et-dev` on the Windows PC (`C:\Windows\System32\drivers\etc\hosts`; it's a one-line edit).

**SSH hardening** (`/etc/ssh/sshd_config.d/10-hardening.conf`):
```
PasswordAuthentication no
PermitRootLogin no
```
Then run `sudo systemctl reload ssh`, and **test a new SSH session before closing the current one**.

**Firewall (ufw):**
```sh
sudo ufw default deny incoming
sudo ufw allow from 192.168.1.0/24 to any port 22 proto tcp
sudo ufw allow from 192.168.1.0/24 to any port 24565 proto udp
sudo ufw enable
```
⚠️ **Docker gotcha:** ports that Docker publishes **bypass ufw**, because Docker writes its own iptables rules. So the compose file (§5) also limits exposure itself: the admin UI binds only to `127.0.0.1`, and you can bind the game port to the LAN IP. Don't port-forward 24565 on the router (see §7).

---

## 4. Container runtime: Docker Engine (on containerd)

Docker Engine runs on **containerd**. Installing Docker gives you containerd plus the `docker` CLI and **Compose**, which is what makes multi-container setups easy. Using containerd directly (`ctr`/`nerdctl`) has no advantage here. If you ever want it, `nerdctl compose` reads the same `compose.yaml`.

Install from Docker's apt repository (from docs.docker.com/engine/install/ubuntu):

```sh
sudo apt install -y ca-certificates curl
sudo install -m 0755 -d /etc/apt/keyrings
sudo curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc
echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] \
  https://download.docker.com/linux/ubuntu $(. /etc/os-release && echo $VERSION_CODENAME) stable" \
  | sudo tee /etc/apt/sources.list.d/docker.list
sudo apt update
sudo apt install -y docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
sudo usermod -aG docker $USER        # log out and back in afterwards
docker run --rm hello-world          # check
docker compose version
```

Limit log size for all containers in `/etc/docker/daemon.json`, then run `sudo systemctl restart docker`:
```json
{ "log-driver": "json-file", "log-opts": { "max-size": "10m", "max-file": "5" } }
```

Docker starts at boot by default (`systemctl is-enabled docker` shows `enabled`). Containers with `restart: unless-stopped` come back with it.

---

## 5. Building and running the game server

### 5.1 Export a Linux server build (on the Windows dev PC)

Export from the existing Windows setup rather than installing Godot in the container. It reuses the toolchain that already works, and the image stays small.

One-time setup:
1. **Install the export templates for 4.7.2 mono.** None are installed yet: `editor_data/export_templates` is empty. In the editor, use *Editor → Manage Export Templates → Download and Install*. In self-contained mode they go to `E:\Godot Install\...\editor_data\export_templates\`, so nothing lands on C:.
2. Add an export preset **"Linux Server"** (*Project → Export → Add → Linux*):
   - Architecture `x86_64`, export path e.g. `E:\claude\_tmp\server-export\ExploreTogether.x86_64`.
   - Resources: export all. Exclude filters: `core/*, tests/*, .localdata/*, docs/*, tools/*`.
   - Leave **Export Mode: Dedicated Server** (which strips visual resources) **off** at first. The server builds dungeon collision and navigation from the imported pieces, so check that stripping doesn't break that before turning it on. It's a later size optimization only.
   - Commit `export_presets.cfg`. It has no secrets as long as no encryption key is set.
3. Add `tools/export-server.cmd`, which sets the env from `E:\claude\CLAUDE.md` and runs:
   ```
   Godot_v4.7.2-stable_mono_win64_console.exe --headless --path . --export-release "Linux Server" E:\claude\_tmp\server-export\ExploreTogether.x86_64
   ```
   The output is `ExploreTogether.x86_64`, `ExploreTogether.pck` and a `data_ExploreTogether_linuxbsd_x86_64/` folder holding the .NET assemblies and the bundled .NET runtime (Godot .NET exports are self-contained, so the image doesn't need .NET installed).

A Godot export runs the script's "template" path, so without `--db` the database would go to `<exe dir>/data/server.db` (`ServerPaths.cs`). The container always passes `--db /data/server.db` explicitly.

### 5.2 Required code change: clean shutdown on SIGTERM

`docker compose stop` (and a host reboot) sends **SIGTERM**, then SIGKILL after the grace period. On shutdown, `ServerGame._ExitTree` saves characters and marks open runs `interrupted`. That only happens if the process quits through Godot's normal path. Don't rely on Godot's default signal handling. Add an explicit handler in the server startup:

```csharp
// ServerGame._Ready (server only)
_sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
{
    ctx.Cancel = true;                                   // don't let the runtime kill us
    Callable.From(() => GetTree().Quit()).CallDeferred(); // quit on the main thread → _ExitTree saves
});
// keep _sigterm in a field (and do the same for SIGINT) so it isn't garbage-collected
```

Test it as part of the rollout (§8): stop the container mid-run, then check that the log shows the shutdown save and that the run row is `interrupted`.

### 5.3 Files on the Linux box

```
/srv/exploretogether/
  compose.yaml
  Dockerfile
  backup/Dockerfile    # 2-line image for db-backup (§5.5)
  .env                 # optional overrides (port, LAN IP)
  build/               # the exported server (rsync'd from Windows)
  data/                # server.db, -wal, -shm, migration .bak files, Godot user data   (owned by uid 10001)
  backups/             # snapshots from db-backup                                       (owned by uid 10001)
```

```sh
sudo mkdir -p /srv/exploretogether/{build,data,backups}
sudo chown -R $USER: /srv/exploretogether
sudo chown 10001:10001 /srv/exploretogether/{data,backups}
```

### 5.4 Dockerfile

```dockerfile
FROM debian:trixie-slim

# libicu: .NET globalization. tini: forwards SIGTERM properly and reaps zombies. sqlite3: handy for `docker compose exec`.
RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates libicu76 tini sqlite3 \
 && rm -rf /var/lib/apt/lists/*

RUN useradd --system --uid 10001 --home-dir /app --shell /usr/sbin/nologin game
WORKDIR /app
COPY --chown=game:game build/ /app/
RUN chmod +x /app/ExploreTogether.x86_64

USER game
# Godot's user:// (logs, settings) goes under the data volume instead of the image
ENV XDG_DATA_HOME=/data/godot XDG_CONFIG_HOME=/data/godot
EXPOSE 24565/udp
ENTRYPOINT ["/usr/bin/tini", "--", "/app/ExploreTogether.x86_64", "--headless", "--"]
CMD ["--server", "--db", "/data/server.db"]
```

If the binary fails to start, run `docker run --rm --entrypoint ldd exploretogether-server:dev /app/ExploreTogether.x86_64` to list missing shared libraries, and add them. In `--headless` mode Godot doesn't load X11/Wayland/audio libraries.

### 5.5 compose.yaml

```yaml
name: exploretogether

services:
  game-server:
    build: .
    image: exploretogether-server:${GIT_REV:-dev}
    restart: unless-stopped
    ports:
      - "${BIND_IP:-0.0.0.0}:24565:24565/udp"   # set BIND_IP=192.168.1.50 in .env to listen on the LAN only
    volumes:
      - ./data:/data
    stop_signal: SIGTERM
    stop_grace_period: 30s                      # time to save every character before SIGKILL
    # extra server args, e.g. for testing:
    # command: ["--server", "--db", "/data/server.db", "--dungeon-empty-timeout", "60"]

  db-backup:
    build: ./backup               # backup/Dockerfile: FROM alpine:3.22 + RUN apk add --no-cache sqlite
    image: exploretogether-backup:local
    restart: unless-stopped
    user: "10001:10001"
    volumes:
      - ./data:/data              # read-write: WAL readers need the -shm file
      - ./backups:/backups
    entrypoint: ["/bin/sh", "-c"]
    command:
      - |
        while true; do
          if [ -f /data/server.db ]; then
            f=/backups/server-$$(date -u +%Y%m%d-%H%M).db
            sqlite3 /data/server.db ".backup '$$f'" && sqlite3 "$$f" "PRAGMA integrity_check;" | grep -qx ok \
              && echo "backup ok $$f" || echo "BACKUP FAILED $$f"
            find /backups -name 'server-*.db' -mtime +14 -delete
          fi
          sleep 21600
        done

  db-admin:
    image: coleifer/sqlite-web:latest   # pin to a digest after the first pull
    profiles: ["admin"]                 # only starts with --profile admin
    user: "10001:10001"
    ports:
      - "127.0.0.1:8080:8080"           # local only, reach it through an SSH tunnel
    volumes:
      - ./data:/data
    command: ["sqlite_web", "--host", "0.0.0.0", "--port", "8080", "--read-only", "--no-browser", "/data/server.db"]
```

Notes:
- `backup/Dockerfile` is two lines: `FROM alpine:3.22` and `RUN apk add --no-cache sqlite`. Installing at build time lets the container run as the unprivileged uid 10001. `$$` in the compose file is an escaped `$`.
- `.backup` uses SQLite's online backup API, so it is safe while the server is writing (WAL mode). **Never `cp` a live `server.db`:** the `-wal` file holds recent commits.
- `db-admin` is read-only, so nothing is edited behind the game's back. To change data, stop `game-server` first, then use `sqlite3` (§6).

---

## 6. Day-to-day operations

### Deploy a new build (from Windows)

Draft of a future `tools/deploy-devserver.ps1`, which uses the OpenSSH client built into Windows:
```powershell
tools\export-server.cmd
$rev = git rev-parse --short HEAD
scp -r E:\claude\_tmp\server-export\* et-dev:/srv/exploretogether/build/    # or rsync from Git Bash/WSL
ssh et-dev "cd /srv/exploretogether && GIT_REV=$rev docker compose up -d --build game-server"
```
**Clients must be built from the same commit as the server**, because the network protocol changes freely between commits. The image tag carries the git revision, so `docker compose images` shows what is running. Before a deploy that changes the schema, the server backs up the database itself (`server.db.v{N}.*.bak` in `data/`).

### Common commands (on the server)

| Task | Command |
|---|---|
| Status | `docker compose ps` |
| Follow server log | `docker compose logs -f game-server` |
| Restart (saves on the way down) | `docker compose restart game-server` |
| Stop everything | `docker compose down` (the data stays on disk) |
| CPU / RAM | `docker stats` |
| Open admin UI | `docker compose --profile admin up -d db-admin`, then on Windows `ssh -L 8080:127.0.0.1:8080 et-dev` and browse to `http://localhost:8080` |
| Item audit (against a snapshot, not the live file) | `docker compose run --rm --no-deps -v $PWD/backups:/backups game-server --server --db /backups/<snapshot>.db --audit-items --port 24600` |
| Query by hand (server stopped) | `docker compose stop game-server && sqlite3 data/server.db` |

### Restore a backup

```sh
docker compose stop game-server
mv data/server.db data/server.db.broken; rm -f data/server.db-wal data/server.db-shm
cp backups/server-YYYYMMDD-HHMM.db data/server.db && chown 10001:10001 data/server.db
docker compose start game-server
```

### Seed the server with your local dev database (optional)

On Windows, with `dev-run` stopped: `sqlite3 .localdata\server.db ".backup E:\claude\_tmp\seed.db"`. Then `scp` the file to `data/server.db` on the server, while `game-server` is stopped.

### Off-box backup copy

`backups/` sits on the same disk as the database. For dev data that's probably enough. If you want a second copy, pull it to the Windows PC on a schedule (`scp et-dev:/srv/exploretogether/backups/* E:\claude\_backups\devserver\`), or point Litestream at an external target later.

---

## 7. Security boundaries

- **LAN only.** `PLAN.md` notes that ENet traffic, **including login passwords, is unencrypted**. Don't port-forward 24565 on the router until DTLS or Steam auth is in place.
- **Friends outside the LAN:** use **Tailscale** (or WireGuard) on the server and on their PCs, and give them the tailnet IP. Nothing gets exposed to the internet. With Tailscale, allow 24565/udp on `tailscale0` in ufw.
- Use dev passwords only. Don't reuse real passwords on this server.
- The admin UI is reachable only through an SSH tunnel and is read-only.
- The containers run as the unprivileged uid 10001, and nothing runs `--privileged`.

---

## 8. Rollout checklist

Each step ends with something you can check.

1. **Hardware + OS:** BIOS settings (§2), Ubuntu Server installed, updates on, fixed IP, key-only SSH, ufw.
   ✔ `ssh et-dev` works from Windows with a key; `sudo ufw status` shows rules 22/tcp and 24565/udp.
2. **Docker:** installed from Docker's repository, log limits set.
   ✔ `docker run --rm hello-world`; the PC reboots and `docker info` works without manual steps.
3. **Linux export:** templates installed, "Linux Server" preset, `tools/export-server.cmd`.
   ✔ The export folder contains `ExploreTogether.x86_64`, the `.pck` and the `data_*` folder.
4. **SIGTERM handler** (§5.2) in `ServerGame`, plus `dotnet test`. Also run the smoke test locally so nothing else regresses.
5. **Image + compose:** files from §5.3–5.5, `docker compose up -d --build`.
   ✔ The log shows `starting as SERVER` and the migration to the current version; `data/server.db` exists.
6. **Connect from Windows:** `tools\dev-run.cmd`-style client with `--connect 192.168.1.50`.
   ✔ Register, create a character, enter a dungeon. Then `docker compose restart game-server`, log back in, and the character is at its saved spot.
7. **Shutdown test:** stop the container while a dungeon run is active.
   ✔ The log shows the shutdown save; in `db-admin`, the run is `interrupted` and the player's hub position is saved.
8. **Backups:** temporarily set the interval to 60 s.
   ✔ Snapshots appear in `backups/` with `backup ok`; a test restore (§6) works. Then put the interval back to 6 h.
9. **Reboot test:** `sudo reboot`.
   ✔ All always-on containers come back without anyone logging in, and a client can connect.
10. **Docs:** add the deploy commands to the project `CLAUDE.md` and tick off "Deploying the server somewhere other than this PC" in `PLAN.md`.

Optional later:
- Build the image on the Linux box from git (a multi-stage Dockerfile that downloads Godot 4.7.2 mono and the templates, then exports), so deploying is just `git pull` plus a build, with no Windows step.
- Run the smoke test against the container from CI or on a schedule.
- Run more than one server (e.g. `stable` and `test`) on different ports and data folders, with one compose project per folder.
