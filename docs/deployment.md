---
title: "Deployment"
description: "Run the server reproducibly with release builds, Docker Compose, backups and rollback."
updated: 2026-11-09
---

This page is for running Bubble Fighter Fantasy for other people. It covers the release build, the Docker Compose setups in this repository, manual installation on Linux and Windows, persistent data and backups, logs, network security, and how to upgrade and roll back safely.

**Who this is for:** operators who deploy and maintain a server process, not just start one on a laptop.

**Before you start:**

- Read [configuration](configuration.md). A deployment is a profile choice plus environment variables; this page does not repeat how composition works.
- Build and validate the exact revision you intend to deploy. A release directory is produced from a successful `dotnet build BubbleFighter.Fantasy.sln -c Release` with zero warnings.
- Plan the host: a supported operating system and architecture, the ASP.NET Core Runtime 10 for a framework-dependent build (or Docker), a dedicated service account, and a database you own.
- Have the client data for every build you will serve. Game profiles fail at activation without it.
- Decide where backups live. They must not live inside a release directory.

## Before deploying

This is a prerelease project. It has not been validated against every supported client, and a listed profile or a passing automated check does not establish real-client compatibility. Run it as a prerelease service, test with a real client, and keep a restore path.

Supported targets in this release are Windows x64, Windows ARM64, Linux x64 and Linux ARM64. The known deployment environment is Debian 13; other Linux distributions are not claimed as tested. The publish scripts cover Windows x64 and Linux x64/ARM64; Windows ARM64 is a listed target that uses the same Host publish with `-r win-arm64`.

The public Windows packages, the Linux install script and the one-command Docker installation are not published yet. What exists in this repository is source, the two publish scripts in `tools/`, the Docker files in `deploy/docker/`, and the Compose setups. Do not substitute an installer or registry image from somewhere else.

A deployment has four network and storage concerns:

| Concern | What to plan | Default in this release |
| --- | --- | --- |
| Game ports | One TCP listener per served build. | `kr-1256` uses 7100; legacy `kr-812` uses 7110. |
| Relay | Only builds whose profile opens a relay listener. | `kr-1256` uses 7101; legacy builds do not open one. |
| Administration / WebUI | A server started with `start` is reachable from other machines by default and signs in with an administrator account; `BFF_ADMIN_HOST=127.0.0.1` keeps it local. A `run --profile` server has no accounts and stays on loopback. | `0.0.0.0:7300` (`start`), `127.0.0.1:7300` (`run`). |
| MySQL | Reachable from the server only; never public. | `127.0.0.1:3306`, database `bf_game_db`. |

Provision client data on local storage and mount or copy it read-only where possible. The server only reads it.

## Optional shared pet equipment slot

The `equipment` row's `pet_shared_slot` option defaults to `false`. Enabling it permits one worn pet, identified by function `10001` in that build's own `Config/ShopItem2.xml`. Wearing a second pet fails; remove the current pet before wearing another. The rule also checks the final target inside the inventory transaction, and normalizes historical multiple-pet selections when the equipment view is read. Ownership, quantities and unrelated flag bits are preserved.

| Builds in the `all` profile | Pet slot opt-in |
| --- | --- |
| `kr-812`, `kr-1256`, `cn-406`, `cn-404`, `cn-408`, `cn-706`, `cn-717`, `id-400`, `id-1809`, `cn-774`, `tw-454` | Available with the build's client data and persisted equipment provider. |
| `cn-279`, `cn-324` | Refused at activation: their current catalogs have no pet rows and no native pet selection path has been established. |

For example, add this to `profiles/kr-1256/local.patch.yaml` to opt in on that build. A `replace` block replaces the entire row configuration, so retain any other deployment overrides. For another supported build, use its profile directory and copy its effective `protocol`, `client_profile` and `version_major` values.

```yaml
- replace:
    id: equipment
    config:
      protocol: kr-1256/game
      client_profile: kr-1256
      version_major: 1256
      pet_shared_slot: true
```

Keep `persist_worn_state: true` and the inventory-view provider enabled. Missing client data, an unsupported build or overlapping equipment groups causes an explicit error. This option covers owned-pet equipment and restoration from the inventory store. It does not implement feeding, growth, evolution or ability changes; those require persistent per-pet state and their own verified operations. Validate equip, refusal of a second pet, removal and login restoration with the intended native client before deploying the option.

## Optional pet growth and skills

The separate pet bundles add feeding, growth, skill replacement and regression, plus optional match rewards and combat effects. All switches default off, and native-client acceptance is still required. Ownership is shared across builds; each build keeps its own growth and skills. Modern Article identities come from durable storage. Both bags refer to the same entitlement, and removing then granting a pet creates a new identity.

The profiles of `kr-812`, `kr-1256`, `cn-406`, `cn-404`, `cn-408`, `cn-706`, `cn-717`, `id-400` and `id-1809` already include these patches with feeding, growth and skills on. Rewards, combat effects and mileage stay off. Set `BFF_PETS_ENABLED=false` to turn growth, skills and the single pet slot off together. Each profile states its client's civil-clock offset in `PET_CLIENT_UTC_OFFSET_MINUTES` (Korea 540, China and Taiwan 480, Indonesia 420). `cn-774` and `tw-454` do not include them yet, because their profiles mount no native inventory-use handler for feeding.

Include `profiles/bundles/pets.patch.yaml` after the selected build's normal patch. For `kr-1256`, `cn-774` and `tw-454`, also include `pets-modern.patch.yaml`; finish with `pets-kr-1256.patch.yaml` or `pets-tw-454.patch.yaml` for the corresponding build. Other audited pet builds use the base pet patch. These patches mount the `pets.entitlements`, `pets` and `pets.combat-effects` rows. `BFF_PETS_ENABLED` also enables `equipment.pet_shared_slot`, so only one pet can be worn at a time. Keep that equipment rule enabled when customizing the growth bundle. Keep their build keys and the inventory row's filtered catalog aligned.

The `pets` row requires the matching room policy host. Pet equip/remove, feeding, level and skill requests hold manual and automatic room starts until their committed inventory and appearance publications finish. Requests received after starting are refused, as is a second concurrent pet request from the same session. `max_in_flight_sessions` bounds active requests and `max_held_rooms` bounds retained start records; both default to 128 and accept 1–65536. The replay worker owns at most one additional claimed record. Capacity exhaustion refuses new mutations or reports a held-record error; size the bounds for the protocol's expected concurrency. Other room policies are evaluated again before a held start proceeds.

Before enabling `BFF_PETS_ENABLED`, set `client_utc_offset_minutes` explicitly on both pet rows: native feeding dates use the client's civil clock. Set the feeding policy on both rows as follows:

| Client policy | Required configuration |
| --- | --- |
| Automatic growth | `client_configuration_source: None`; no nonzero feeding cooldown. |
| KR1256 and CN774 | `client_configuration_source: DynamicConfig`, explicit `feed_cooldown_seconds` and positive `mileage.display_maximum`. The mounted dialect must accept both settings, even when mileage gains are disabled. |
| TW454 | `client_configuration_source: NativePetConfig`, `native_pet_config_path: Config/PetConfig.xml`, and an explicit cooldown matching that file's `PetSystem.FeedCoolTime` in minutes. The audited file says 420 minutes, or 25200 seconds. TW does not receive the KR/CN configuration fields. |

`skill_resets_enabled` is a separate opt-in. Builds with the native mileage balance require a complete enabled `mileage` policy: an explicit gain for every reset consumable, including deliberate zero gains, and a display maximum. Optional conversion also requires a threshold, `Single` or `All` mode, `Preserve` or `Discard` remainder, a bounded conversion count and a reward bundle with explicit item kinds and quantities. No original-server gains, thresholds or rewards are assumed. The audited TW catalog has no mileage balance. Consumable, progression, mileage and reward changes commit together; refusal consumes nothing.

Enable reward and combat switches separately and supply explicit mode lists in `rewards.mode_ids` and `dialect.scene-seeds.pet_combat_mode_ids`. Reward stacking and rounding are operator policy. Cancellation seeds remain available only where the build's own consumer has been established. Test feeding, ordinary and premium growth, skill changes, login restoration, room/party/plaza appearances and match effects on each intended client before rollout. Review the additive MySQL migration files separately; coordinate their application with every node sharing that database.

For MySQL, migration `0042_inventory_slot_key_watermark.sql` retains ordinary inventory keys after an item is consumed. Before enabling pet mileage rewards, update every process that grants items in the shared database to the watermark-aware purchase allocator. An older purchase or gift writer cannot see a consumed mileage reward's retired key and could reuse it. Reading purchase history protects new writers from older purchases; it does not make older writers honor the new watermark. Keep mileage disabled until all grant writers are updated, and do not roll them back after mileage has allocated keys without preserving this allocator behavior.

## Build a release directory

The two scripts in `tools/` publish the Host and every plugin into a directory that can be copied to the target machine. Both produce a **framework-dependent** build: the .NET runtime is not bundled, so the target needs the matching ASP.NET Core Runtime 10.

| Script | Target | Notes |
| --- | --- | --- |
| `tools/publish-windows-x64.ps1` | `win-x64` | Verifies the apphost is Windows x64 and contains no bundled runtime. |
| `tools/publish-linux.ps1` | `linux-x64` (default) or `linux-arm64` | Verifies the apphost is ELF of the requested architecture. |

Both scripts require a PowerShell session and take a mandatory `-OutputPath`. The directory may be absent, but if it exists it must be **empty**: publishing into a directory with an older release would retain runtime files from that release. The scripts fail instead of overwriting.

### Windows

From a PowerShell prompt at the repository root:

```powershell
.\tools\publish-windows-x64.ps1 -OutputPath C:\BFF\release-2026-10-18
```

A successful run prints `Windows x64 framework-dependent publish ready: <path>` and reminds you that the target needs the x64 ASP.NET Core Runtime 10.0.

### Linux

Publish from a PowerShell 7 session on any machine; the output is Linux binaries:

```powershell
pwsh -File tools/publish-linux.ps1 -OutputPath /srv/bff-release/server
```

Add `-Runtime linux-arm64` for an ARM64 target. A successful run prints `Linux framework-dependent publish ready: <path> (<runtime>)` and checks that the InGame plugin was published. PowerShell is only the script host; the target machine needs no PowerShell.

### What the publish contains

- `BubbleFighter.Fantasy.Host` (the apphost) and `BubbleFighter.Fantasy.Host.dll` with the host's dependencies and `runtimeconfig.json`.
- `plugins/<ProjectName>/` for every plugin project, which is the default plugin root next to the host.
- `profiles/` with one directory per profile, without any `local.patch.yaml`.
- `packets/` with the packet tables and `builds.yaml`.
- `bootstrap/` with the loader profiles, and `data/level-experience.csv` and `data/locales/*.json`.

The alternative to the scripts is `dotnet publish src/BubbleFighter.Fantasy.Host -c Release -o <directory>`, which runs the same Host publish including the plugin publish target and the content copy. It skips the scripts' empty-directory and apphost checks, so use a fresh directory and verify the result yourself. For Windows ARM64, use this form with `-r win-arm64`.

Check the result before you copy it: the directory contains the host executable, `plugins/` with one directory per plugin, and the `profiles/`, `packets/`, `bootstrap/` and `data/` directories. No `coreclr.dll`, `libcoreclr.so` or equivalent runtime files are present; if you find any, the publish bundled a runtime and is not the release you meant to build.

## Docker Compose from this repository

`deploy/docker/compose.yaml` starts a server and a MySQL database from this repository's source. It is the shortest path to a running container setup.

1. Copy the environment file and set a nonempty database password. The Compose file requires `BFF_MYSQL_PASSWORD` and refuses to start without it; `BFF_SETUP_TOKEN` is optional and, when set (at least 32 random characters), a remote first-run setup asks for it:

   ```sh
   cp .env.example .env
   ```

2. Provision client data where the Compose file looks for it: `client-data/<build>/data` and `client-data/<build>/dataraw` next to the repository for every build you will select, or redirect it with `BFF_CLIENT_DATA_HOST`. It is mounted at `/app/client-data` in the container.

3. Build and start both services from the repository root:

   ```sh
   docker compose -f deploy/docker/compose.yaml up --build
   ```

What this starts:

- The `bff` service builds from `deploy/docker/Dockerfile`: a multi-stage build on the .NET SDK and ASP.NET 10 images, running as the unprivileged `bff` user (uid 1001). It runs the host without a command, which is first-run setup: open `http://<docker-host>:7300`, enter `BFF_SETUP_TOKEN` when the page asks for it, create the administrator account, choose MySQL with host `mysql`, the database name, user and password from `.env`, and select your clients with the data folder `/app/client-data`. Later starts run the saved installation and sign administrators in with that account. It publishes 7100 (game), 7101 (relay) and 7300 (administration and WebUI); Compose sets `BFF_ADMIN_HOST=0.0.0.0` so the published administration port reaches the container.
- The `mysql` service runs the image named by `BFF_DB_IMAGE` (MariaDB 11.4 with a `.env` copied from `.env.example`; MySQL 8.4 when the line is absent, so an existing `mysql-data` volume keeps the server that created it), creates the database and account from `BFF_MYSQL_DATABASE`, `BFF_MYSQL_USER` and `BFF_MYSQL_PASSWORD`, publishes `${BFF_MYSQL_PORT:-3306}` on the host, and keeps its data in the `mysql-data` volume. The server waits for its health check before starting and receives `BFF_MYSQL_HOST=mysql` inside the container network.
- Client data is mounted read-only at `/app/client-data`; the saved installation lives in the `bff-state` volume and database state in the `mysql-data` volume. The server's own logs are written inside the container and are not persisted to the host by this Compose file.

Check it worked: `curl http://127.0.0.1:7300/health` on the Docker host returns JSON with a `status` field, and the console shows the setup or installation progress. Follow the console with:

```sh
docker compose -f deploy/docker/compose.yaml logs -f bff
```

Stop both services with `docker compose -f deploy/docker/compose.yaml down`. This keeps the database volume. `down -v` also deletes it, so do not use `-v` unless you intend to discard the database.

Review this Compose file before exposing it: it publishes MySQL on the host, leaves Docker's default log handling in place (no size limit is configured), and serves the default game profile. It is a development-style setup, not a hardened production template.

## Docker on a Linux release root with an external database

`deploy/docker/compose.external-db.yaml` is a different artifact: a template for a release root on a Linux host that already runs a database. It expects this layout, which you create on the target machine:

```text
<release-root>/
  compose.yaml     copy of deploy/docker/compose.external-db.yaml
  Dockerfile       copy of deploy/docker/Dockerfile.published
  .env             deployment variables, including BFF_RELEASE
  server/          framework-dependent Linux publish output
  logs/            written by the container
  client-data/     optional; or point BFF_CLIENT_DATA_HOST elsewhere
```

1. Publish the release into `server/` with `tools/publish-linux.ps1`. The publish directory must be empty, so publish into `server/` before copying anything else into it.
2. Copy `deploy/docker/compose.external-db.yaml` to `<release-root>/compose.yaml` and `deploy/docker/Dockerfile.published` to `<release-root>/Dockerfile`.
3. Create `<release-root>/.env`. It must set `BFF_RELEASE` (the image tag) and the MySQL values of the local database. Review the addresses and tokens: `BFF_BIND_HOST`, `BFF_MYSQL_HOST` and the administration address are forced by the template.
4. Start it from `<release-root>`:

   ```sh
   docker compose up -d --build
   ```

The template provides: a `bf-fantasy:<release>` image built from `server/`; host networking, so the game, relay and administration ports configured in `.env` are used directly and a database on `127.0.0.1` is reachable; a read-only container filesystem with a small writable `/tmp`; all capabilities dropped; `no-new-privileges`; a 30-second stop grace period; restart unless stopped; the `./logs` directory mounted at `/app/logs` for packet log files; client data mounted read-only at `/app/client-data`; and json-file logging limited to five files of 10 MB each.

The Dockerfile variant starts the image with `run --profile all`, so the release serves every brought-up build on its own port and needs client data for those builds. To serve a single build, change the `CMD` in your copy of the Dockerfile, which is an operator-managed file.

`docker compose down` stops the service and keeps the `logs/` directory. The database is not managed by this template: back it up and upgrade it with its own tooling. `deploy/docker/Dockerfile.published` is the repository file this layout builds from; no image is published to any registry, so the image is always built on the target.

## Manual deployment on Linux

Use the manual path when you do not want a container: copy the Linux publish output to the target and run the host directly.

1. Publish with `tools/publish-linux.ps1` and copy the output to `<release-root>`. Keep the directory layout of the publish unchanged.
2. Create a dedicated service account, for example `bff`, that owns the release directory but has no interactive login. The host only needs to read `profiles/`, `packets/`, `bootstrap/`, `plugins/` and `data/`; it needs a writable working directory for `logs/` and a writable location for the SQLite file if you use SQLite.
3. Make the apphost executable if the copy lost the mode: `chmod +x BubbleFighter.Fantasy.Host`.
4. Provide the deployment variables in the process environment. A release directory contains no repository marker, so a `.env` placed beside it is **not** read by the host; the `.env` loading rule applies only inside a checkout. Use an environment file your supervisor or shell sources, for example:

   ```sh
   set -a
   . <release-root>/deployment.env
   set +a
   ```

   The file holds the same `KEY=VALUE` pairs as `.env.example`, with the real MySQL password, the public address and any administration token.

5. Change into the release root and start the host in the foreground:

   ```sh
   cd <release-root>
   ./BubbleFighter.Fantasy.Host run --profile kr-1256
   ```

   The working directory matters: `profiles`, `packets`, `client-data` and `logs` resolve against it. `Ctrl+C` or SIGTERM requests a graceful shutdown and the console ends with `Shutdown complete.`

6. The repository ships no service unit. For a long-running deployment, run the host under a supervisor you manage (for example a systemd service), with the working directory set to `<release-root>` and the variables supplied through its environment file. Keep the service account unprivileged and give it access only to the release, the logs directory and, if used, the SQLite file.

Check it worked with `curl http://127.0.0.1:7300/health` from the host, and with the listening-port checks below. Stop it with SIGTERM from the supervisor or `Ctrl+C` in the foreground.

## Manual deployment on Windows

1. Publish with `tools/publish-windows-x64.ps1` and copy the output to the deployment directory.
2. Set the deployment variables for the process before starting it, for example in the PowerShell session that will own the process:

   ```powershell
   $env:BFF_MYSQL_HOST = '127.0.0.1'
   $env:BFF_MYSQL_PASSWORD = '<your-database-password>'
   $env:BFF_PUBLIC_HOST = '<public-address-clients-use>'
   ```

   As on Linux, a `.env` copied into the release directory is not read, because the host loads a repository `.env` only when it can find the repository root.

3. Start the host from the release directory so relative `profiles`, `packets`, `client-data` and `logs` paths resolve there:

   ```powershell
   Set-Location C:\BFF\<release>
   .\BubbleFighter.Fantasy.Host.exe run --profile kr-1256
   ```

4. Allow only the planned game, relay and administration ports through the firewall. Keep the administration port reachable only from trusted networks, and keep MySQL off the public interface.
5. There is no Windows service wrapper in this repository. Run the executable under a supervisor you manage if it must start with the machine; keep the service account unprivileged.

Check it worked with `Invoke-RestMethod http://127.0.0.1:7300/health`, which returns JSON with a `status` field. Stop with `Ctrl+C`; the host also handles SIGTERM and SIGINT where the platform provides them.

## Persistent data and backups

| Data | Where it lives | Back up before upgrade |
| --- | --- | --- |
| Game and account data | MySQL database `bf_game_db`, or one SQLite save file. | Yes; every upgrade. |
| Client data | Read-only files under `client-data/<build>/`. | Once, or whenever you change your client copy. |
| Configuration and secrets | Compose `.env` or the supervisor's environment file. | Yes; store it securely. |
| Logs and packet logs | `logs/<date>/` under the working directory, or a container path. | Optional; rotate and retain per policy. |
| Compiled client data | `state/compiled-data/<build>.bffdata` under the working directory, or `BFF_COMPILED_DATA`. | No; it is a cache the server rebuilds. |
| Previous release | A separate directory. | Keep until the rollback window closes. |

Keep the database, its backups and the SQLite save file outside the release directory, because releases are replaced. For MySQL, use the database's own backup tooling, for example a logical dump (`mysqldump`) or a consistent storage snapshot, while the server is stopped or quiet. For SQLite, stop the server first and copy the single save file. Never copy a live SQLite file while the server is writing it.

Test a restore on a separate instance before you rely on it: restore the database or copy the save file, start the release against it, and confirm `/health`, the plugin row states and a client flow. A backup you have never restored is not a proven backup.

The client data is input, not state: the server reads it, and the Docker setups mount it read-only, so replacing a release does not change it.

## Memory

The Host ships its memory settings in `BubbleFighter.Fantasy.Host.runtimeconfig.json`: the workstation collector, concurrent collection, and `System.GC.ConserveMemory` set to 9, which makes the collector compact eagerly. When every row is active, the Host compacts the heap once and logs `Startup allocated N MB in C collection(s); the managed heap was compacted from A MB to B MB committed`. B is close to what the loaded profile keeps; the process is larger than B by the runtime's own code and the mapped program files.

Starting costs far more memory than running. In our measurements one client build peaked near 140 MB while its client data was parsed and settled near 40 MB of private memory; every build together peaked near 300 MB (see [Small servers](#small-servers)). Give a small machine swap for the start rather than a limit below the peak.

Environment variables override the file. Two details matter:

- The runtime reads numeric `DOTNET_GC*` variables as **hexadecimal**. `DOTNET_GCHeapHardLimit=20000000` is 512 MB, not 20 MB. Write `0x20000000` so the base is visible.
- A heap limit that the start reaches stops the server with `OutOfMemoryException`. The start peak includes the parsing of every build's client data, so a limit that fits the idle server can still stop the start. In a container with a memory limit the runtime applies a heap limit of 75 percent of it by itself. Leave the heap limit unset unless you have measured that your profile starts under it.

`python tools/perf/measure.py --profile <id>` measures a start on your own machine; `tools/perf/README.md` explains the numbers.

A connection holds a socket, a session and a read buffer from the moment it is accepted. Every supported client sends its first frame as soon as it has the greeting, so a connection that sends no complete frame within `first_frame_seconds` (a key of each `protocol` row, default 30) is closed. Raise it only if a client of yours is measured to wait longer.

A client that stops reading makes the server queue what it sends to it. Each session stops at `send_queue_bytes` (4 MB) and is closed; `total_send_queue_bytes` (each `protocol` row, default 64 MB) caps what all sessions of the row hold together, so many stalled clients at once cannot add up. When a frame would cross it, the session holding the most queued bytes is closed and the frame is sent; no sender waits. On a small machine lower it to what the machine can spare; sessions log the most they queued at Debug when they end.

### Small servers

What a server needs is set by the client builds it serves, the database it uses and what starts at the same time. The numbers below were measured with `tools/perf/measure.py` on a release build with memory persistence; use them to choose, then measure your own profile the same way.

| | One build (`kr-812`) | Every build (`all`) |
| --- | --- | --- |
| Peak while starting | about 140 MB | about 300 MB (up to 370 MB on the first start) |
| Private memory when idle | about 40 MB | about 95 MB |
| CPU of the first start, one fast core | about 5 s | about 27 s |
| CPU of later starts | about 2 s | about 16 s |

A slow VPS core needs two to four times the start CPU. The peak lasts only while client data is parsed; the server then returns that memory.

**Compiled client data.** The first start of a build parses the client's workbooks and keeps the result in `state/compiled-data/<build>.bffdata` under the working directory (set `BFF_COMPILED_DATA` to move the folder). Later starts read that file, which is why they need far less CPU. The file is a cache: the server uses it only while the client data and the server release are the ones it was written from, rebuilds it otherwise, and works the same without it, so it can be deleted at any time and needs no backup. After an upgrade the first start is a first start again. `bff data compile <profile>` does that first start and stops, which lets a deployment pay for it before players are let in. In a container, mount the folder as a volume or every new container starts from nothing.

**512 MB.** Serve one or two builds with SQLite (a patch that enables the `persistence.sqlite` row, see [SQLite](configuration.md#sqlite)), not MySQL: a database server alone needs most of such a machine. Add 1 GB of swap or zram so the start peak never meets the out-of-memory killer. Leave `BFF_PACKET_LOG` off.

**1 GB.** Serve a few builds with SQLite, or one or two builds with MariaDB configured as below. Keep 512 MB of swap.

**2 GB and more.** Serve every build (`profiles/all`) with MariaDB or MySQL.

**The small-server patch.** `profiles/bundles/small-vps.patch.yaml` turns off the administration web page and the client and plugin distribution rows, which a server that only serves players does not need. The administration API stays on. Add `--patch profiles/bundles/small-vps.patch.yaml` after the profile's own patches, to `run` or to `start`. Each build's listeners accept 512 connections by default (`max_connections` on its listener rows); lower them in your own patch if the machine cannot hold that many players.

**Builds that sleep.** A server that serves several builds from one process (`profiles/all`, or a server profile of your own that lists `builds`) can leave a build unloaded until a player needs it. Set `BFF_BUILD_ACTIVATION=on_demand`; in your own server profile write `build_activation: on_demand`. Each build's listeners and wire dialect start with the server, so every port accepts connections from the first second. The rest of a build loads when the first client connects to it, and unloads after `BFF_BUILD_IDLE_MINUTES` (default 30) with no connection and no room. In our measurements `all` was ready in about 2 s with 30 MB of private memory instead of about 16 s of CPU and 95 MB; with two builds loaded it peaked near 155 MB instead of 300 MB, and returned to 32 MB once both were asleep again. The price is paid by the first player of a sleeping build, whose connection waits while the build loads: about 11 s for the first build after a server start and about 2 s for a later one on the test machine, more on a slow core. A connection that would wait longer than `wake_timeout_seconds` (30, on the `core.activation-groups` row) is closed, and the client can connect again once the build is up. Loading or unloading one build does not disturb the players of another, the administration API or the launcher channel. A row that must always run can say `activation: always` in a patch. This was measured on one node; a profile that mounts cluster rows has not been examined with sleeping builds, so leave it at `always` there.

MariaDB on a small machine, in `[mysqld]`:

```ini
innodb_buffer_pool_size = 64M    # the game's tables are small; raise it if the database grows past it
performance_schema      = OFF
max_connections         = 50
innodb_log_buffer_size  = 8M
key_buffer_size         = 8M
aria_pagecache_buffer_size = 16M
tmp_table_size          = 16M
max_heap_table_size     = 16M
```

Bind it to `127.0.0.1` when the game server runs on the same machine. In our measurements MariaDB held about 32 MB resident beside a running server; MySQL 8.4 with its defaults reserves several hundred megabytes, which is why new Docker installations use MariaDB.

**Docker.** `deploy/docker/compose.yaml` and `install.sh --docker --with-mysql` start MariaDB 11.4 for a new installation (`BFF_DB_IMAGE=mariadb:11.4`). An installation whose database MySQL 8.4 created has no `BFF_DB_IMAGE` line and keeps MySQL, because MariaDB cannot open that volume; to move it, dump the database and restore it into a new one. To cap the containers, add a Compose override such as this one for a 1 GB machine:

```yaml
services:
  bff:
    mem_limit: 512m
    memswap_limit: 1g
  mysql:
    mem_limit: 192m
    memswap_limit: 384m
    command: ["--character-set-server=utf8mb4", "--collation-server=utf8mb4_unicode_ci",
              "--innodb-buffer-pool-size=64M", "--performance-schema=OFF", "--max-connections=50"]
```

The runtime limits its heap to 75 percent of a container's memory limit, so keep the server's limit well above the start peak in the table.

Do not set a heap limit below the start peak (see the hexadecimal note above): the server would stop while it starts.

## Logs

Set `BFF_LOG_FILE` or `--log-file` to retain Host `ILogger` records beside the console output. File logging is disabled when no path is set. Its threshold defaults to `Warning`; `BFF_LOG_FILE_LEVEL` or `--log-file-level` selects it independently of `BFF_LOG_LEVEL`. Normal logging-factory category and provider filters still apply. Each accepted record includes a UTC timestamp, level, category, event id and the full exception text, including inner exceptions and stacks.

Choose a writable path inside an existing persistent volume. For `deploy/docker/compose.yaml`, `/app/state/logs/host.log` is inside the `bff-state:/app/state` mount; add the variable to the container environment. Native services should use their own persistent data directory. The writer creates the parent directory and holds `host.log.lock` exclusively while running. Another process cannot write to the same log. An invalid path, permissions failure or locked destination is a startup error.

Files rotate through fixed slots: `host.log`, `host.1.log`, and so on. The defaults retain five files, including the active file, and limit new writes to 16 MiB per file. The count also applies after restart or a count reduction. An existing oversized active file rotates into the first backup at startup; historical backups may exceed a newly reduced size bound until normal retention replaces them. With one retained slot, rotation replaces the active file. A single writer uses bounded record and UTF-8 byte budgets. Callers never wait for disk: a full queue, exhausted byte budget, oversized record or formatting failure drops the newest complete record and produces a counted `dropped_entries=N` line. Exceptions are never shortened to fit. On a write, flush or rotation I/O failure, the writer emits one bounded stderr line containing the exception type, closes the stream and counts records as dropped while unavailable. It retries opening with exponential backoff, by default from one second up to 60 seconds. After recovery it writes the pending drop report before new records. A failed rotation resumes from its last completed file move. Warning and higher records and drop reports are flushed to the operating system before the next write; shutdown drains for a bounded interval and reports failure if it cannot finish. An I/O failure can leave a partial record, and this does not guarantee recovery after power loss. See [configuration](configuration.md) for every limit. Full exceptions can contain account or connection data; restrict access and apply your normal retention policy.

The server logs to the console. Lines are single-line and carry UTC timestamps; the level comes from `BFF_LOG_LEVEL` or `--log-level` and defaults to `Information`. A normal start reports each activated row, the listening address of each listener and, once ready, `Server started with <n> active row(s).` A graceful stop ends with `Shutdown complete.`

The diagnostics row can also write packet log files. They are off by default, because every packet then costs a formatted line and a file write; set `BFF_PACKET_LOG=true` to turn them on while debugging. Connection events are logged to the console either way. The files are separate from the console and contain one line per packet and connection event:

- The default path is `logs/{date}/packets_{timestamp}.log`, relative to the working directory. Profiles override the name: `kr-812` writes `packets-kr-812_{timestamp}.log` and the `all` profile writes `packets-all_{timestamp}.log`.
- Each file rotates to `.1` at 10 MiB; the `{date}` component starts a new file when the date changes. By default up to 100 files are kept per configured name pattern, and older files are deleted when the sink starts. The filename time zone defaults to China Standard Time.
- Writes go through a bounded queue (32768 records by default) with a 10-second flush on shutdown. An overflow is reported as `packet-log-overflow` and never drops gameplay packets.
- The optional full packet recorder is off by default. Setting `BFF_PACKET_CAPTURE=true` enables it and writes separate JSONL packet files under the same `logs/` tree.

Packet log lines can contain player and connection data; treat the `logs` directory as sensitive and apply your normal retention policy. In the Docker setups, the release-root template mounts `./logs` so the packet files persist on the host, while the console output goes to the container's json-file log, limited to five files of 10 MB.

## Admin and network security

- A server started with `start` (what the executable does without arguments) binds the administration listener to `0.0.0.0:7300` by default and signs administrators in by account and password; `/health` and the WebUI files stay open so a browser can load the sign-in page. Until the first administrator exists, anyone who reaches the port can complete the setup, so finish it right after the first start or set `BFF_SETUP_TOKEN`. On a reachable address any host name is accepted unless `BFF_ADMIN_ALLOWED_HOSTS` lists them. A server started with `run --profile` has no accounts, needs no sign-in and listens on loopback only.
- Before exposing the administration port beyond loopback, restrict the source network. If you cannot restrict it, keep it on loopback and use a tunnel or a reverse proxy you manage.
- Open only the game and relay ports that the selected profiles serve on the firewall. Do not publish the administration port or MySQL to the public interface.
- Use a dedicated database account, keep the MySQL port on loopback or a private network, and never reuse the database administrator account for the server.
- Keep secrets in the deployment-local `.env` (Compose) or the supervisor environment file (manual). Restrict its permissions and never commit it. `--dump-config` shows interpolated secrets, so do not attach its output to public reports.
- Do not publish client data or packet logs; they can contain proprietary client files and player data.

## Upgrade and rollback

Upgrades are side-by-side: install the new release into a new directory, keep the old one, and switch by stopping one process and starting the other.

1. Announce the maintenance window and stop accepting new work. Let matches finish or end them deliberately; then stop the server gracefully with SIGTERM or `Ctrl+C` and wait for `Shutdown complete.`
2. Back up the database as described above, and record the exact release directory and configuration you are running.
3. Publish the new release into a fresh empty directory. Do not publish over the previous release.
4. Copy or reuse the deployment variables and the client data. Client data does not change between releases unless you update the client.
5. Start the new release from its directory and verify it: `/health`, the plugin row states, the listening ports, the storage connection line and the absence of errors in the console.
6. Keep the previous release directory and the pre-upgrade backup until you no longer need to roll back.

To roll back, stop the new release, start the previous release directory with the same configuration, and restore the pre-upgrade database backup if the new release changed the schema. A binary-only rollback is not guaranteed: MySQL migrations are applied automatically when the persistence row activates and are forward-only. There is no downgrade path in the server, so an older binary may not be able to run against a database that a newer release has already migrated. The safe rollback pair is the previous release plus the backup taken before the upgrade.

## Post-deploy checks

- [ ] `GET /health` returns JSON with `status` equal to `ok`.
- [ ] The WebUI plugin list (or `GET /api/plugins` from a signed-in session) shows every expected row without a failure state.
- [ ] The listening ports match the plan on the host, checked with `ss -ltn` on Linux or `Get-NetTCPConnection -State Listen` in PowerShell.
- [ ] The console shows the storage connection line: MySQL connected, or SQLite active.
- [ ] Every served game build's `clientdata` row is active, not failed.
- [ ] The log level is appropriate for production, and the packet log directory is writable and growing if packet logging is expected.
- [ ] A backup has been taken and restored at least once on a test instance.
- [ ] A real client has been tested by the owner. Automated checks and a listed profile are not client acceptance.

## If something goes wrong

- **The container exits immediately.** The default profile is a game profile; its client data is missing. Add `client-data/<build>/data` or `dataraw`, or run a profile that does not need client data.
- **The server cannot reach MySQL.** Check the address, port, account and database name. A release directory does not read a local `.env`, so verify the variables in the process environment.
- **`/api` returns 401.** The request carries no signed-in administrator session; sign in on the WebUI first.
- **A port is already in use.** Stop the conflicting process or change the per-profile port variable, then restart.

See [troubleshooting](troubleshooting.md) for the ordered investigation checklist.

## Next

Review [configuration](configuration.md) for the profile, port and storage keys used above, and [plugins](plugins.md) for how rows activate and reload.

## Installation credentials

`state/administration/installation.json` stores the MySQL password in plain text so the server can reconnect after a restart. Keep the state directory owner-only: on Windows grant access only to the service account with the file security properties; on Linux use `chown` for the service user and `chmod 700 state/administration` plus `chmod 600 state/administration/installation.json`. The server warns when a remote MySQL host uses `SslMode=None`; enable TLS when the database is not local.
