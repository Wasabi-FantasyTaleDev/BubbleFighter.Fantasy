---
title: "Deployment"
description: "Run the server reproducibly with release builds, Docker Compose, backups and rollback."
updated: 2026-10-18
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
| Administration / WebUI | Keep on loopback or a controlled network; remote administration needs a server started with `start`, which signs in with an administrator account. | `127.0.0.1:8080`. |
| MySQL | Reachable from the server only; never public. | `127.0.0.1:3306`, database `bf_game_db`. |

Provision client data on local storage and mount or copy it read-only where possible. The server only reads it.

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

1. Copy the environment file, set a nonempty database password and a setup token. The Compose file requires `BFF_MYSQL_PASSWORD` and `BFF_SETUP_TOKEN` (at least 32 random characters) and refuses to start without them:

   ```sh
   cp .env.example .env
   ```

2. Provision client data where the Compose file looks for it: `client-data/<build>/data` and `client-data/<build>/dataraw` next to the repository for every build you will select, or redirect it with `BFF_CLIENT_DATA_HOST`. It is mounted at `/app/client-data` in the container.

3. Build and start both services from the repository root:

   ```sh
   docker compose -f deploy/docker/compose.yaml up --build
   ```

What this starts:

- The `bff` service builds from `deploy/docker/Dockerfile`: a multi-stage build on the .NET SDK and ASP.NET 10 images, running as the unprivileged `bff` user (uid 1001). It runs the host without a command, which is first-run setup: open `http://<docker-host>:8080`, enter `BFF_SETUP_TOKEN` when the page asks for it, create the administrator account, choose MySQL with host `mysql`, the database name, user and password from `.env`, and select your clients with the data folder `/app/client-data`. Later starts run the saved installation and sign administrators in with that account. It publishes 7100 (game), 7101 (relay) and 8080 (administration and WebUI); Compose sets `BFF_ADMIN_HOST=0.0.0.0` so the published administration port reaches the container.
- The `mysql` service runs MySQL 8.4, creates the database and account from `BFF_MYSQL_DATABASE`, `BFF_MYSQL_USER` and `BFF_MYSQL_PASSWORD`, publishes `${BFF_MYSQL_PORT:-3306}` on the host, and keeps its data in the `mysql-data` volume. The server waits for its health check before starting and receives `BFF_MYSQL_HOST=mysql` inside the container network.
- Client data is mounted read-only at `/app/client-data`; the saved installation lives in the `bff-state` volume and database state in the `mysql-data` volume. The server's own logs are written inside the container and are not persisted to the host by this Compose file.

Check it worked: `curl http://127.0.0.1:8080/health` on the Docker host returns JSON with a `status` field, and the console shows the setup or installation progress. Follow the console with:

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

Check it worked with `curl http://127.0.0.1:8080/health` from the host, and with the listening-port checks below. Stop it with SIGTERM from the supervisor or `Ctrl+C` in the foreground.

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

Check it worked with `Invoke-RestMethod http://127.0.0.1:8080/health`, which returns JSON with a `status` field. Stop with `Ctrl+C`; the host also handles SIGTERM and SIGINT where the platform provides them.

## Persistent data and backups

| Data | Where it lives | Back up before upgrade |
| --- | --- | --- |
| Game and account data | MySQL database `bf_game_db`, or one SQLite save file. | Yes; every upgrade. |
| Client data | Read-only files under `client-data/<build>/`. | Once, or whenever you change your client copy. |
| Configuration and secrets | Compose `.env` or the supervisor's environment file. | Yes; store it securely. |
| Logs and packet logs | `logs/<date>/` under the working directory, or a container path. | Optional; rotate and retain per policy. |
| Previous release | A separate directory. | Keep until the rollback window closes. |

Keep the database, its backups and the SQLite save file outside the release directory, because releases are replaced. For MySQL, use the database's own backup tooling, for example a logical dump (`mysqldump`) or a consistent storage snapshot, while the server is stopped or quiet. For SQLite, stop the server first and copy the single save file. Never copy a live SQLite file while the server is writing it.

Test a restore on a separate instance before you rely on it: restore the database or copy the save file, start the release against it, and confirm `/health`, the plugin row states and a client flow. A backup you have never restored is not a proven backup.

The client data is input, not state: the server reads it, and the Docker setups mount it read-only, so replacing a release does not change it.

## Logs

The server logs to the console. Lines are single-line and carry UTC timestamps; the level comes from `BFF_LOG_LEVEL` or `--log-level` and defaults to `Information`. A normal start reports each activated row, the listening address of each listener and, once ready, `Server started with <n> active row(s).` A graceful stop ends with `Shutdown complete.`

The diagnostics row also writes packet log files. They are separate from the console and contain one line per packet and connection event:

- The default path is `logs/{date}/packets_{timestamp}.log`, relative to the working directory. Profiles override the name: `kr-812` writes `packets-kr-812_{timestamp}.log` and the `all` profile writes `packets-all_{timestamp}.log`.
- Each file rotates to `.1` at 10 MiB; the `{date}` component starts a new file when the date changes. By default up to 100 files are kept per configured name pattern, and older files are deleted when the sink starts. The filename time zone defaults to China Standard Time.
- Writes go through a bounded queue (32768 records by default) with a 10-second flush on shutdown. An overflow is reported as `packet-log-overflow` and never drops gameplay packets.
- The optional full packet recorder is off by default. Setting `BFF_PACKET_CAPTURE=true` enables it and writes separate JSONL packet files under the same `logs/` tree.

Packet log lines can contain player and connection data; treat the `logs` directory as sensitive and apply your normal retention policy. In the Docker setups, the release-root template mounts `./logs` so the packet files persist on the host, while the console output goes to the container's json-file log, limited to five files of 10 MB.

## Admin and network security

- The administration listener binds to `127.0.0.1:8080` by default. A server started with `start` (what the executable does without arguments) signs administrators in by account and password; `/health` and the WebUI files stay open so a browser can load the sign-in page. A server started with `run --profile` has no accounts, needs no sign-in and listens on loopback only.
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
