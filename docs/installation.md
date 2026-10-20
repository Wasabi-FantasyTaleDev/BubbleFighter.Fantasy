---
title: "Installation"
description: "Prepare the toolchain, build the server from source, or run it with Docker."
updated: 2026-10-20
---

Bubble Fighter Fantasy is a community server for the game *Bubble Fighter*, maintained by Fantasy Tale Studio and written in C# on .NET 10. Server capability is composed from plugin rows in YAML profiles: a profile selects the plugin rows a client build needs, and the host activates them. This page takes you from a bare machine to a confirmed installation: prerequisites, the installation methods that exist today, building from source on Windows and Linux, running the Docker Compose setup, the first-run setup assistant, and the checks that prove each step.

**Who this is for:** a first-time operator who wants a server installed and confirmed to run. No prior knowledge of the project is assumed.

## What you are installing

You are installing the server host together with the data it reads at startup:

- the host executable (`BubbleFighter.Fantasy.Host`), the plugin assemblies, and the packaged `profiles`, `packets`, `data` and `bootstrap` directories;
- an installation state directory that the first-run assistant creates beside the executable;
- optionally, client data that you supply yourself for the client builds you want to run.

The repository contains no game client and no proprietary client data. Some profile rows need locally supplied files from the matching client; without them those rows fail at activation. The project is a prerelease: available gameplay and required data differ between profiles, and a listed profile or a passing automated check does not establish real-client compatibility.

Each release attaches prebuilt packages for Windows x64, Windows ARM64, Linux x64 and Linux arm64. On Linux an installation script installs them, natively or with Docker; see Install a prebuilt package below. A source build and the Docker files in this repository remain available.

## Before you begin

| Requirement | Needed for | How to check it |
| --- | --- | --- |
| Windows x64, Windows ARM64, Linux x64 or Linux arm64 | Running the server | OS settings; `uname -m` on Linux |
| .NET 10 SDK | Source builds | `dotnet --version` shows 10.x |
| Node.js 20.19 or newer | Source builds (the build compiles the web interface) | `node --version` shows 20.19 or newer |
| ASP.NET Core Runtime 10 | Framework-dependent builds | `dotnet --list-runtimes` shows `Microsoft.AspNetCore.App 10.x` |
| Git | Source builds | `git --version` |
| Docker Engine with Compose | The Docker path | `docker compose version` |
| Client data for the build you run | Game profiles | `client-data/<build>/data` exists |
| A terminal at the repository root | Every command on this page | `Get-Location` or `pwd` |

The repository states no minimum Windows or Linux version; use a system that the .NET 10 SDK supports. The optional plugin SDKs (Python, TypeScript, Go and Rust) are not needed to install or run the server.

## Choose an installation method

| Method | Status | Use it when |
| --- | --- | --- |
| Windows x64 build package | Available | You are on Windows x64 and want a ready build |
| Windows ARM64 build package | Available | You are on Windows on ARM and want a ready build |
| Linux installation script | Available | You want a prepared Linux release without Docker |
| Docker one-command install | Available | You want the server in a container without a checkout, optionally with MySQL |
| Docker built from this repository | Available | You have a checkout and Docker; server and MySQL run in containers |
| Build from source | Available | You want the build output and the first-run assistant on this machine |

## Install a prebuilt package

Each release attaches four packages and a `SHA256SUMS` checksum list. The packages are framework-dependent: the machine that runs them needs the ASP.NET Core Runtime 10. The Linux installer installs it when it is missing.

| Package | For |
| --- | --- |
| `bubblefighter-fantasy-win-x64.zip` | Windows on Intel or AMD 64-bit processors |
| `bubblefighter-fantasy-win-arm64.zip` | Windows on ARM |
| `bubblefighter-fantasy-linux-x64.tar.gz` | Linux on x86_64 |
| `bubblefighter-fantasy-linux-arm64.tar.gz` | Linux on aarch64 |

The newest release's files are always at `https://github.com/Wasabi-FantasyTaleDev/BubbleFighter.Fantasy/releases/latest/download/<package>`.

### Windows

1. Install the [ASP.NET Core Runtime 10](https://dotnet.microsoft.com/download/dotnet/10.0) for your processor (x64 or Arm64), or the .NET 10 SDK, which includes it.
2. Download the ZIP for your processor and extract it to a short folder such as `C:\BubbleFighterFantasy`. Windows cannot load a plugin file whose full path reaches about 259 characters; the server refuses to start from a folder that deep and names the longest file.
3. Double-click `BubbleFighter.Fantasy.Host.exe`. The setup assistant starts and the WebUI opens in your default browser; continue with First-run setup below. Closing the console window stops the server. If startup fails, the window stays open and shows the error until you press a key.

### Linux installation script

```bash
curl -fsSL https://wasabi-fantasytaledev.github.io/install.sh | bash
```

The script detects x86_64 or aarch64, downloads the matching package and `SHA256SUMS` from the latest release, and checks the checksum. When the system has no ASP.NET Core Runtime 10, it installs one into the installation directory with Microsoft's `dotnet-install.sh` and leaves the system-wide .NET alone. It installs into `~/bubblefighter-fantasy`, or `/opt/bubblefighter-fantasy` as root:

| Path | Contents |
| --- | --- |
| `releases/<tag>/` | One unpacked package per installed release |
| `current` | Link to the active release |
| `state/` | The setup assistant's saved installation, kept across upgrades |
| `dotnet/` | The runtime, only when the installer had to install it |
| `bff` | The launcher; with no arguments it starts the setup assistant |

Start the server with `~/bubblefighter-fantasy/bff` and open `http://127.0.0.1:8080/`. The assistant listens on loopback only; on a remote server, forward the port from your own computer first with `ssh -L 8080:127.0.0.1:8080 <user>@<server>`.

Pass options after `bash -s --`, for example `curl -fsSL https://wasabi-fantasytaledev.github.io/install.sh | bash -s -- --service`:

| Option | Effect |
| --- | --- |
| `--version <tag>` | Install that release instead of the latest |
| `--dir <path>` | Install directory |
| `--admin-port <n>` | Port of the setup assistant and WebUI (default 8080) |
| `--service` | As root: create and start the `bubblefighter-fantasy` systemd service, running as the `bff` system user |
| `--start` | Start the server when the installation finishes |
| `--no-runtime` | Fail instead of installing the runtime |
| `--from <dir>` | Install from a directory holding the release files, without downloading |

To upgrade, run the same command again. The new release is unpacked beside the old one and checked by listing its profiles; only then does it become `current`. `state/` is never touched.

### Docker one-command install

```bash
curl -fsSL https://wasabi-fantasytaledev.github.io/install.sh | bash -s -- --docker
```

This needs Docker Engine with the Compose plugin. The installer unpacks the Linux package into `~/bubblefighter-fantasy/docker`, builds a local image from it, and starts the server container with Docker Compose. The container uses host networking, so the game ports of the clients you select are reachable. No database container is started: in the setup assistant, choose SQLite, which keeps its file in the `bff-state` volume, or enter an existing MySQL server.

To run a bundled MySQL 8.4 as well, add `--with-mysql`:

```bash
curl -fsSL https://wasabi-fantasytaledev.github.io/install.sh | bash -s -- --docker --with-mysql
```

MySQL then listens on `127.0.0.1:3306` only (`--mysql-port` changes it), and the generated `.env` holds random database passwords that only its owner can read. In the assistant, choose MySQL with host `127.0.0.1`, the port the installer printed, database `bf_game_db`, user `bff`, and the `BFF_MYSQL_PASSWORD` value from `~/bubblefighter-fantasy/docker/.env`.

Run the same command again to upgrade; `.env` and the volumes are kept, and a stack created with MySQL keeps it. To follow the log, run `docker compose logs -f bff` in that directory.

## Build from source on Windows

Run these steps in Windows PowerShell. Choose any folder for the checkout; all later commands run from the repository root.

### 1. Clone the repository

```powershell
git clone <repository-url> BubbleFighter.Fantasy
cd BubbleFighter.Fantasy
```

`<repository-url>` is the address the project publishes for the repository. If you already have a checkout or a source archive, open PowerShell in that directory instead.

### 2. Check the .NET SDK

```powershell
dotnet --version
```

The command must print a 10.x version. If it reports another version or is not found, install the .NET 10 SDK first.

### 3. Build every project {#build-all-projects-windows}

```powershell
dotnet build BubbleFighter.Fantasy.sln -c Release
```

The first build downloads packages and can take a few minutes. The build treats warnings as errors, so a successful build ends with:

```text
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

### 4. Find the build output

| What | Where |
| --- | --- |
| Host executable and libraries | `src/BubbleFighter.Fantasy.Host/bin/Release/net10.0/` |
| Plugin build output | `artifacts/plugins/<AssemblyName>/` |
| Copy used at runtime | `<host output>/plugins/` |
| Packaged data beside the host | `<host output>/profiles/`, `packets/`, `data/`, `bootstrap/` |

The host build copies the plugin directories into `<host output>/plugins/` and replaces that folder on every build, so a removed plugin does not linger. The copied `profiles/` directory excludes `local.patch.yaml` files; that file is a git-ignored developer override that is applied only when present in the checkout.

### 5. Run the built host

From the repository root, so the default `./profiles` directory resolves:

```powershell
.\src\BubbleFighter.Fantasy.Host\bin\Release\net10.0\BubbleFighter.Fantasy.Host.exe profiles list
```

This prints one line per packaged profile and proves that the executable and its profile data are in place. The packaged first-run path that starts the setup assistant is described under First-run setup below.

**Check it worked:** the build ends with `0 Warning(s)` and `0 Error(s)`, and the command above prints profile lines instead of an error.

## Build from source on Linux

Run these steps in a POSIX shell. The checkout directory is your choice; all later commands run from the repository root.

### 1. Clone the repository

```sh
git clone <repository-url> BubbleFighter.Fantasy
cd BubbleFighter.Fantasy
```

### 2. Check the .NET SDK

```sh
dotnet --version
```

The command must print a 10.x version.

### 3. Build every project {#build-all-projects-linux}

```sh
dotnet build BubbleFighter.Fantasy.sln -c Release
```

A successful build ends with `Build succeeded.`, `0 Warning(s)` and `0 Error(s)`.

### 4. Run the built host

```sh
dotnet src/BubbleFighter.Fantasy.Host/bin/Release/net10.0/BubbleFighter.Fantasy.Host.dll profiles list
```

Run it from the repository root so the default `./profiles` directory resolves. The compiled apphost next to the DLL works the same way.

### 5. Note the runtime requirement

Building needs the .NET 10 SDK and Node.js 20.19 or newer: the WebUi project compiles the web interface with npm the first time, and again only when its sources change. Framework-dependent output, such as a release directory produced by `tools/publish-linux.ps1` or copied out of a container image, needs the ASP.NET Core Runtime 10 on the machine that runs it. Check with `dotnet --list-runtimes` and look for `Microsoft.AspNetCore.App 10.x`. The Docker path below supplies the runtime inside the image.

**Check it worked:** the build is clean and `profiles list` prints profile lines.

## Run with Docker from this repository

Run these steps from the repository root with Docker Engine and the Compose plugin installed.

### 1. Create the environment file

PowerShell:

```powershell
Copy-Item .env.example .env
```

Linux shell:

```sh
cp .env.example .env
```

Set `BFF_MYSQL_PASSWORD` in `.env` to a nonempty disposable value before starting. Compose uses it for the MySQL container, and `.env` is git-ignored; never commit it. The MySQL connection variables are documented in `.env.example`.

### 2. Start the server and the database

```sh
docker compose -f deploy/docker/compose.yaml --env-file .env up --build
```

The compose file starts two services:

- `bff` is built from `deploy/docker/Dockerfile` with the repository as the build context. The Dockerfile publishes the host without a bundled runtime, then runs it on the ASP.NET Core Runtime 10 image as a non-root system user, with the entrypoint `dotnet BubbleFighter.Fantasy.Host.dll run --profile kr-1256`. The image exposes ports 7100, 7101 and 8080.
- `mysql` uses the `mysql:8.4` image with the `bf_game_db` database, the `bff` user and utf8mb4 character settings, stores its files in the `mysql-data` volume and is health-checked before the server starts.

Published ports are 7100, 7101 and 8080 for the server, and `${BFF_MYSQL_PORT:-3306}` for MySQL. The server mounts client data read-only at `/app/client-data` from `${BFF_CLIENT_DATA_HOST:-../../client-data}`, which resolves to the repository's `client-data` directory; the container needs `client-data/kr-1256/data` or `client-data/kr-1256/dataraw`, and the clientdata row fails without it. Both services use `restart: unless-stopped`.

The container path starts MySQL deliberately. It is not an isolated local check and does not use the local-check patch. The administration listener binds `127.0.0.1:8080` inside the container, so add `BFF_ADMIN_HOST=0.0.0.0` to `.env` if you need to reach the WebUI through the published 8080 port.

### 3. Follow the logs

```sh
docker compose -f deploy/docker/compose.yaml logs -f bff
```

The startup lines should match the ones described under First-run setup below. Press Ctrl+C to stop following the log; that stops only the log command, not the services started by `up`.

### 4. Stop the containers

Press Ctrl+C in the terminal that runs the compose command, or from another terminal:

```sh
docker compose -f deploy/docker/compose.yaml down
```

`down` keeps the `mysql-data` volume, so a later `up` reuses the database. `docker compose -f deploy/docker/compose.yaml down -v` also deletes that volume and all game data in it; use it only when the database data is disposable.

### Manual single-container path

If you run one container yourself instead of the compose pair, build the image from the repository root and pass a reachable MySQL and a client-data mount:

```sh
docker build -f deploy/docker/Dockerfile -t bff-fantasy .
docker run --rm --env-file .env -e BFF_MYSQL_HOST=<mysql-host> -e BFF_ADMIN_HOST=0.0.0.0 -p 7100:7100 -p 7101:7101 -p 8080:8080 -v "<client-data-directory>:/app/client-data:ro" bff-fantasy
```

The image copies the published host and the `profiles` directory; it contains neither a database nor client data. Point `BFF_MYSQL_HOST` at an address the container can reach.

**Check it worked:** `docker compose -f deploy/docker/compose.yaml ps` lists the services as running, and the server log shows the startup lines from the next section. The first build can take several minutes.

## First-run setup

The packaged first-run experience starts the built-in `setup` profile: an installation assistant and the administration WebUI, without any game listener. It starts on a fresh machine with no client data and no database; you choose and test a database before applying.

### 1. Start the host with no arguments

On Windows, open a terminal in the build output directory and run the executable:

```powershell
.\BubbleFighter.Fantasy.Host.exe
```

Keep the packaged `profiles`, `packets` and `plugins` directories beside the executable; the build output already has them. The process sets its own working directory to the executable's directory, so where you launch it from does not matter. The `start` command never reads a repository `.env`; the assistant keeps its settings in the state directory.

The state directory defaults to `state/` beside the executable. `BFF_STATE_DIR` or `--state-dir <directory>` overrides it; a relative value resolves against the directory the process is launched from.

### 2. Read the printed address

The console prints the administration listener and the WebUI address:

```text
Row 'admin.http' admin API listening on 127.0.0.1:8080.
WebUI: http://127.0.0.1:8080/
```

Open `http://127.0.0.1:8080/` in a browser. The admin listener defaults to `127.0.0.1:8080`; set `BFF_ADMIN_HOST` or `BFF_ADMIN_PORT` in the process environment to change it (`start` does not read `.env`).

### 3. Complete the assistant

1. Choose the interface language: English, Simplified Chinese, Traditional Chinese or Korean.
2. Give the server a name (1 to 80 characters).
3. Select one or more client versions to bring up. Each selected version gets its own client-data choice; you may leave a version without data now and import it later from the server settings.
4. Optionally import client data now: choose a local folder or a prepared ZIP package, review the filtered file count and size, then press Start import. The browser's own folder confirmation may count every file in the folder; the assistant filters the selection to client archives and `data`/`dataraw` files before showing the summary. An import stages a copy in the state directory; a failed or cancelled import removes only its temporary files.
5. Choose the database. MySQL is selected by default: host `127.0.0.1`, port `3306`, database `bf_game_db`, user `bff`. SQLite stores its database in the state directory by default. Run Test connection; the assistant does not continue until the test succeeds.
6. Create the administrator account. The username is 3 to 64 characters; the password is at least 12 characters. This account manages the server, is stored with the installation, and is separate from game accounts.
7. Review the choices and apply. The host starts the selected profile rows and reports an error code you can retry if activation fails.

### 4. What a successful apply leaves on disk

In the state directory you will find:

- `installation.json` with the saved settings and the administrator credential hash;
- `installation.lock`, the lock that prevents two installations from managing the same state;
- `runtime.generated.patch.yaml`, the derived persistence overlay (placeholders, never database credentials);
- `administration/client-imports/<token>/`, one directory per imported client-data copy;
- `saves/fantasy.db` when SQLite is the selected database.

### 5. Import rules and limits

A folder import accepts client archives at their original client paths with the client's letter case, or loose files whose path contains `data` or `dataraw`. A ZIP import accepts one standard single-volume ZIP whose entries are `<build>/data` or `<build>/dataraw`, or those paths below up to two wrapper folders such as `client-data/<build>/data`. A package that contains folders for several builds imports only the selected version. ZIP64 and split archives are not supported; unpack them locally and use the folder picker.

An installed game folder is also accepted: the server generates its required data without Python. Archives containing only server data are copied into `data/`; selected small files from mixed archives are extracted into `dataraw/`, leaving models and textures out. Generation checks required file names before publishing to `<state-dir>/client-data/<build>`, and reports missing names. Existing generated data is never overwritten; select prepared data to change the configured source. Prepared folder and ZIP imports remain independent copies under `administration/client-imports/<token>/`. Upload, extraction, storage, file-count and time limits apply during generation.

Prepared data imports copy files into the installation state; their required tables and archive contents are checked when the game services start. Importing successfully does not prove gameplay or client compatibility.

The default bounds are 8 GiB per upload, 16 GiB of extracted data per import, 32,768 files per import, 64 retained copies, 64 GiB of total import storage and 20 minutes per import. Adjust them on the `installation` row with `client_data_upload_bytes`, `client_data_extracted_bytes`, `client_data_file_limit`, `client_data_copy_limit`, `client_data_storage_bytes` and `client_data_import_seconds`. Importing again does not delete earlier successful copies.

**Check it worked:** the assistant reports the installation complete and the WebUI switches to the administration interface. The `/health` check below answers while the assistant is running, even before an administrator exists.

## Check the installation

Run these commands from the repository root of a source build. They check different layers, so run all three.

### 1. List the packaged profiles

```powershell
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- profiles list
```

The command prints one line per packaged profile: an id and a description. This release lists 19, including `setup` ("Database-independent first-run installation and administration.") and `management` ("Administration and durable storage only; no game or relay ports."). It proves the profile files are readable and compose; it does not prove that any plugin activates.

### 2. List the discovered plugins

```powershell
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- plugins list
```

The command prints one line per discovered plugin definition: the plugin id, its version, the client builds it supports and where it was loaded from. It proves that the plugin directories next to the host are discoverable; it does not prove that a profile's rows activate.

### 3. Compose a profile without starting it

```powershell
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- run --profile kr-812 --patch tools/local-checks/isolated-memory.patch.yaml --dump-config
```

This needs `BFF_MYSQL_PASSWORD` to be set, because the profile interpolates `${BFF_MYSQL_PASSWORD}` while composing the MySQL row; the final patch disables that row afterwards. Prepare `.env` as in [Quickstart](quickstart.md) if you have not done so.

The command prints the effective row list as YAML, one `- id:` block per row, then exits with code 0. Confirm these points in the output:

- the `persistence.mysql` row has `disabled: true`;
- the `persistence.memory` row has no `disabled: true` line, so it is enabled;
- the `clientdata` row shows the resolved `data_directory` and `raw_directory` for `kr-812`.

The dump proves composition, interpolation and the patch order: CLI patches apply last and override a developer `.env` or local patch that points at a real database. It does not start a listener, so it proves nothing about activation or client data.

::: warning
A configuration dump can contain interpolated values, including credentials, when a patch enables MySQL. Treat a dump as sensitive and do not paste one into a chat or an issue.
:::

### Browser checks for the setup-assistant path

With the assistant running on the default address:

| Check | Command | What you should see |
| --- | --- | --- |
| Health | `Invoke-RestMethod http://127.0.0.1:8080/health` | `status` = `ok` and a UTC timestamp |
| Setup status | `Invoke-RestMethod http://127.0.0.1:8080/api/setup/status` | `phase`, `installed`, `profiles` and the import limits |

On Linux, use `curl -s http://127.0.0.1:8080/health` and `curl -s http://127.0.0.1:8080/api/setup/status`. The `/health` endpoint and the WebUI files stay open; before an administrator exists, `/api/plugins` answers 401 by design, so set up the administrator through the assistant first.

**Check it worked:** all three source-build commands exit successfully and the browser checks return the described JSON.

## Where everything lives

| Item | Location |
| --- | --- |
| Repository checkout | The directory you cloned into |
| Host build output | `src/BubbleFighter.Fantasy.Host/bin/Release/net10.0/` |
| Plugin build output | `artifacts/plugins/<AssemblyName>/` (git-ignored) |
| Files beside the host | `<host output>/profiles/`, `packets/`, `data/`, `bootstrap/`, `plugins/` |
| Installation state | `state/` beside the executable; `BFF_STATE_DIR` or `--state-dir` overrides it |
| Environment file | `.env` at the repository root; git-ignored; not read by `start` |
| Logs | Console; packet logs under `logs/{date}/` in the working directory |
| Client data | `client-data/<build>/data` or `dataraw`; `BFF_CLIENT_DATA` overrides |
| Docker data | The `mysql-data` volume; client data mounted read-only |

The `.env` file is applied to unset process variables before profile composition for every command except `start`; existing environment variables win and values are never logged.

## Stop and remove

- **Stop a source build or the assistant:** press Ctrl+C in the terminal. The console prints `Shutdown complete.` and the process exits.
- **Stop Docker:** press Ctrl+C in the foreground compose command, or run `docker compose -f deploy/docker/compose.yaml down`. Add `-v` only when you also want to delete the database volume.
- **Delete the installation state:** removes the saved installation settings, the administrator account, the imported client-data copies and the default SQLite save. This is irreversible; stop the server first.
- **Delete the database:** removes durable game data. Back it up first and never delete a live database that players use. The [deployment](deployment.md) page covers backups and upgrades.
- **Delete the build output:** safe; rebuild with the build command to restore it.

## If something goes wrong

- **The build fails:** warnings are errors in this repository. The first `error` line names the project and cause; fix it and rebuild. See [troubleshooting](troubleshooting.md).
- **A framework-dependent build does not start:** the ASP.NET Core Runtime 10 is missing. Confirm it with `dotnet --list-runtimes`, then install the runtime for the same architecture.
- **A port is already in use:** the assistant uses port 8080 and a game profile uses its own game port, for example 7110 for `kr-812`. Set `BFF_ADMIN_PORT` or the build's port variable in `.env`, or stop the process that owns the port.
- **A game profile fails on client data:** the `clientdata` row reports that neither its `data_directory` nor its `raw_directory` exists. Supply the matching client data, or import it through the setup assistant, then start the profile again. See [troubleshooting](troubleshooting.md).

## Next

Continue with [Quickstart](quickstart.md) to start an isolated local server and check it is alive, then read [Server configuration](configuration.md) to choose persistence, ports and profile changes. For a long-running service, backups and upgrades, continue with [Deployment](deployment.md). Client data is always supplied separately: this repository contains neither the game client nor its data. The companion Fantasy Tale Launcher has its own guide: [Launcher](launcher.md).
