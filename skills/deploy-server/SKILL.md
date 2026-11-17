---
name: deploy-server
description: Install, upgrade and troubleshoot a Bubble Fighter Fantasy server on Windows or Linux with the official install scripts (install.ps1, install.sh), including Docker, a background service, the database choice, opening the setup WebUI through an SSH tunnel, and fixes for common failures. Use when asked to deploy, install, upgrade or repair a Bubble Fighter Fantasy server.
---

# Deploy a Bubble Fighter Fantasy server

Bubble Fighter Fantasy is a community server for the game *Bubble Fighter*
(<https://github.com/Wasabi-FantasyTaleDev/BubbleFighter.Fantasy>). Every release publishes prebuilt packages for
Windows x64, Windows ARM64, Linux x64 and Linux arm64, a `SHA256SUMS` list, and two install scripts. Always deploy
with these scripts: they pick the right package, verify its checksum, install the ASP.NET Core Runtime 10 privately
when it is missing, and keep earlier releases and the server state across upgrades.

The scripts install the server and start its **setup assistant**, a WebUI on port 7300. The person who owns the
server finishes the installation there: language, server name, client versions, client data, database and the
administrator account. Your job is to get the server installed, running and reachable, prepare the database the
person chose, and hand over the setup address.

## The setup wizard belongs to the person

Installing ends at the setup assistant's WebUI. **Never skip or complete the first-run setup wizard yourself**:
do not call its API, write `installation.json` or other state files, start a game profile with `run --profile` to
avoid it, or pass answers for it in any other way. The person opens the WebUI and goes through the wizard (language,
server name, client versions, client data, database, administrator account) themselves. Do it for them only when
they explicitly ask you to, and then only with the values they give you.

## 1. Collect what you need

Ask only for what you cannot detect yourself:

- Where the server runs: this machine, or a remote server you reach over SSH (address and SSH user).
- The operating system (detect it: `uname -s` / `uname -m` on Linux, `$PSVersionTable` and
  `$env:PROCESSOR_ARCHITECTURE` on Windows).
- On Linux: run directly on the system (native) or in Docker.
- The database: SQLite (no server needed), an existing MySQL or MariaDB server, a bundled MariaDB 11.4 container
  (Docker mode only), or a MySQL/MariaDB server you should install natively. On the person's own PC, recommend
  SQLite: it needs no database server to install, secure or keep running.
- Whether the server should start with the machine (a service) or only run now.

Never print passwords, setup tokens or `.env`/`settings.env` contents into shared logs or chat beyond what the person
needs to read once.

## 2. Make sure .NET is there (every install except Docker)

The server is a .NET 10 application. The packages are framework-dependent, so a native install (Windows, or Linux
without Docker) needs the **ASP.NET Core Runtime 10** (`Microsoft.AspNetCore.App 10.x`) for the machine's processor.
Docker mode needs no .NET on the machine: its image already contains the runtime.

- Check first: `dotnet --list-runtimes` must list `Microsoft.AspNetCore.App 10.x`. A missing `dotnet` command or no
  10.x line means it is not installed.
- By default the install scripts install a private copy into `<dir>/dotnet` with Microsoft's `dotnet-install` script
  when the runtime is missing, and leave any system-wide .NET alone. Let them do it unless the person wants a
  system-wide runtime.
- To install it system-wide instead, take it from Microsoft (<https://dotnet.microsoft.com/download/dotnet/10.0>):
  on Windows `winget install Microsoft.DotNet.AspNetCore.10` or the official installer for x64 or Arm64; on Linux the
  distribution's or Microsoft's package `aspnetcore-runtime-10.0`. The .NET 10 SDK also contains the runtime.
- Only building from source needs the .NET 10 SDK (and Node.js 20.19 or newer for the web interface); the install
  scripts never build.
- With `--no-runtime` / `-NoRuntime` the scripts fail instead of installing it; install it first in that case.

After installing, run `dotnet --list-runtimes` again (or let the script check) before you continue.

## 3. Run the install script

The newest release is always at `https://github.com/Wasabi-FantasyTaleDev/BubbleFighter.Fantasy/releases/latest/download/<file>`.
An agent's shell has no interactive console, so always pass the options explicitly and add the no-prompt flag.

### Windows (PowerShell 5.1 or 7)

```powershell
# A server that starts with Windows (needs an administrator PowerShell):
& ([scriptblock]::Create((irm https://github.com/Wasabi-FantasyTaleDev/BubbleFighter.Fantasy/releases/latest/download/install.ps1))) -NoPrompt -Service

# A personal PC: install, start it in its own window and open the setup page:
& ([scriptblock]::Create((irm https://github.com/Wasabi-FantasyTaleDev/BubbleFighter.Fantasy/releases/latest/download/install.ps1))) -NoPrompt -Start
```

| Option | Effect |
| --- | --- |
| `-Version <tag>` | Install that release instead of the latest |
| `-Dir <path>` | Install folder (default `C:\BubbleFighterFantasy`; keep it short, see Troubleshooting) |
| `-Arch x64\|arm64` | Package to install (default: this processor) |
| `-NoRuntime` | Fail instead of installing the ASP.NET Core Runtime 10 when it is missing |
| `-Service` | As administrator: scheduled task `BubbleFighterFantasy`, runs as SYSTEM, starts with Windows |
| `-Start` | Start the server in its own window and open the setup page |
| `-AdminPort <n>` | Port of the setup assistant and WebUI (default 7300) |
| `-RemoteAdmin` / `-LocalAdmin` | Let other machines open the WebUI (default) / keep it on this machine only |
| `-SetupToken` / `-NoSetupToken` | Require a generated token before the setup wizard can be used / do not (default) |
| `-NoPrompt` | Ask nothing; use the options given and the defaults |
| `-From <dir>` | Install from a folder holding the release files (needs `-Version`) |

Result: `releases\<tag>\`, a `current` junction, `state\`, `settings.env` (WebUI access, token, port) and `bff.cmd`,
which starts the server. Stop or start the service with `Stop-ScheduledTask BubbleFighterFantasy` /
`Start-ScheduledTask BubbleFighterFantasy`.

### Linux (bash, x86_64 or aarch64)

```bash
# Native, as a systemd service (as root):
curl -fsSL https://github.com/Wasabi-FantasyTaleDev/BubbleFighter.Fantasy/releases/latest/download/install.sh | bash -s -- --no-prompt --service

# Docker, installing Docker itself when missing, with a bundled MariaDB 11.4 (as root):
curl -fsSL https://github.com/Wasabi-FantasyTaleDev/BubbleFighter.Fantasy/releases/latest/download/install.sh | bash -s -- --no-prompt --docker --install-docker --with-mysql
```

| Option | Effect |
| --- | --- |
| `--version <tag>` | Install that release instead of the latest |
| `--dir <path>` | Install directory (default `/opt/bubblefighter-fantasy` as root, `~/bubblefighter-fantasy` otherwise) |
| `--no-runtime` | Fail instead of installing the ASP.NET Core Runtime 10 when it is missing |
| `--service` | As root: systemd unit `bubblefighter-fantasy.service`, running as the `bff` system user |
| `--start` | Start the server in the foreground when the installation finishes |
| `--docker` | Run the server with Docker Compose (host networking) |
| `--install-docker` | As root: install Docker Engine and the Compose plugin with Docker's official script when missing; implies `--docker` |
| `--with-mysql` | With Docker: also run a database container (MariaDB 11.4 for a new stack) |
| `--mysql-port <n>` | Loopback port of that database container (default 3306) |
| `--admin-port <n>` | Port of the setup assistant and WebUI (default 7300) |
| `--remote-admin` / `--local-admin` | Let other machines open the WebUI (default) / keep it on this machine only |
| `--setup-token` / `--no-setup-token` | Require a generated setup token / do not (default) |
| `--no-prompt` | Ask nothing; use the options given and the defaults |
| `--from <dir>` | Install from a directory holding the release files (needs `--version`) |

`--service` and `--docker` cannot be combined; Compose restarts the containers itself. Native mode leaves
`releases/<tag>/`, `current`, `state/`, `settings.env` and the `bff` launcher. Docker mode writes the stack into
`<dir>/docker` (`compose.yaml`, `.env`); follow its log with `docker compose logs -f bff` in that directory.

To upgrade, run the same command again: the new release is unpacked beside the old one, checked, and only then
made current. The state directory, `settings.env` and the Docker `.env` and volumes are kept.

## 4. Prepare the database

The setup assistant asks for the database and refuses to continue until **Test connection** succeeds.

- **SQLite (recommended on a personal PC):** nothing to prepare. The database file lives in the state directory (in the `bff-state` volume with
  Docker).
- **Bundled MariaDB (Docker `--with-mysql`):** nothing to prepare. In the assistant choose MySQL with host
  `127.0.0.1`, the port the script printed, database `bf_game_db`, user `bff`, and the `BFF_MYSQL_PASSWORD` value from
  `<dir>/docker/.env`. MariaDB is used as MySQL.
- **An existing MySQL or MariaDB:** collect host, port, user and password. Use a dedicated account, not the database
  administrator. The database does not have to exist: the test says so and applying the setup creates it. When it
  already holds tables, the assistant asks whether to keep the data (default) or replace it.
- **Installing MySQL or MariaDB natively:** use the operating system's packages, keep the server on loopback or a
  private network, create a dedicated user with rights on the database `bf_game_db`, and give the person these
  values for the assistant. Never expose the database port to the internet.

## 5. Open the setup page

By default the WebUI accepts other machines (it listens on `0.0.0.0:7300`); `--local-admin` / `-LocalAdmin` keeps
it on the server itself. Until the first administrator exists, anyone who reaches the port can complete the setup,
so the person should finish it right away; `--setup-token` / `-SetupToken` adds a token the wizard asks for.

- On the same machine: open `http://127.0.0.1:7300/`.
- From another computer, without opening any port: tunnel it with `ssh -L 7300:127.0.0.1:7300 <user>@<server>`,
  then open `http://127.0.0.1:7300/` on that computer while the SSH session stays open. Prefer this.
- Directly at `http://<server>:7300/` only when the person asks for it and opens TCP 7300 in the firewall or cloud
  security group themselves (or asks you to). Never open it on your own.

Check that the server answers: `curl http://127.0.0.1:7300/health` (Linux) or
`Invoke-RestMethod http://127.0.0.1:7300/health` (Windows) returns `"status":"ok"`.

## 6. Game ports

Each client version listens on its own game port; for example `kr-1256` uses 7100 (and its relay 7101) and `kr-812`
uses 7110. After setup, open only the game and relay ports of the versions the person selected in the firewall or
cloud security group. Never publish the WebUI port or the database to the internet. When players connect from
other machines, the server must advertise an address they can reach (`BFF_PUBLIC_HOST`).

## 7. Hand over

Leave the setup wizard untouched (see above) and tell the person, in plain words: where the server is installed, how it starts and stops, the setup address (and the
tunnel command when needed), the database values to enter, the setup token if one was generated, the ports to open,
and how to upgrade (run the same command again).

## Troubleshooting

| Problem | Fix |
| --- | --- |
| A port is already in use | Find the owner first (`Get-NetTCPConnection -LocalPort 7300 -State Listen` / `sudo ss -ltnp \| grep :7300`). Stop only that process, or reinstall with another `--admin-port` / `-AdminPort`. A game port moves with its variable, e.g. `KR_812_GAME_PORT`. |
| `the checksum ... does not match SHA256SUMS` | The download was damaged or interrupted. Run the script again; never skip the check. |
| ASP.NET Core Runtime 10 missing | Run without `--no-runtime` / `-NoRuntime`; the script installs a private copy into `<dir>/dotnet`. |
| Windows refuses to start, naming a long file | Windows cannot load plugin files whose full path reaches about 259 characters. Install into a short folder such as `C:\BubbleFighterFantasy`. |
| A game version fails on client data | The client data for that version was not supplied. Import it in the setup assistant or from the server settings; the project ships no game client or client data. |
| MySQL access denied / unknown database | Check host, port, user and password; the test message ends with the database's own reason. An unknown database is created when the setup is applied. |
| The WebUI cannot be reached from another computer | Use the SSH tunnel. Direct access also needs TCP 7300 open in the firewall or security group, and an installation made with `--local-admin` / `-LocalAdmin` must be reinstalled with `--remote-admin` / `-RemoteAdmin`. |
| Players cannot connect | Open the version's game and relay ports in the firewall or security group, and set `BFF_PUBLIC_HOST` to an address players can reach. |
| `--docker needs the Docker Compose plugin` | Add `--install-docker` (as root), or install the Compose plugin. |
| `--service needs root` / `needs systemd` | Run as root on a systemd system, or start the server with `<dir>/bff` instead. |
| `-Service needs an administrator PowerShell` | Open PowerShell as administrator, or use `-Start` on a personal PC. |
| The server does not answer `/health` after `-Service` | Run `bff.cmd` in a console to see the error. |

Full guides: installation, deployment and troubleshooting at
<https://github.com/Wasabi-FantasyTaleDev/BubbleFighter.Fantasy/tree/main/docs>.
