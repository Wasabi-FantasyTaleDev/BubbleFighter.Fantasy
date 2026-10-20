# Bubble Fighter Fantasy

Recreating the quiet days of Boom Hill with modern technology.

Bubble Fighter Fantasy is a community server emulator for the 3D shooter *Bubble Fighter*,
maintained by **Fantasy Tale Studio**. The server is written in **C# on .NET 10**. There is no
privileged core: listeners, protocol framing and ciphers, packet routing, game logic,
persistence, the admin HTTP API and the web interface are all plugins, selected and configured
through YAML profiles. Python, TypeScript, Go and Rust are optional plugin SDKs; you do not
need them to run the server.

## Project status

This is a **prerelease** project. It has not been validated against every supported client,
and a profile being listed or an automated check passing does not establish real-client
compatibility.

- The repository contains neither the game client nor its proprietary data. Game profiles
  need client data supplied separately; see [Client data](#client-data).
- Profiles describe several historical client builds (Korean, Chinese, Indonesian and
  Taiwanese). The range of implemented gameplay differs between them.
- Each release publishes prebuilt packages for Windows x64/ARM64 and Linux x64/arm64; see
  [Install a prebuilt package](#install-a-prebuilt-package).

## Requirements

| Requirement | Needed for |
| --- | --- |
| [.NET SDK 10.0](https://dotnet.microsoft.com/) | building and running from source |
| [Node.js 20.19 or newer](https://nodejs.org/) | building from source (the build compiles the web interface) |
| ASP.NET Core Runtime 10.0 | running a framework-dependent release build |
| Git | cloning the repository |
| Docker with the Compose plugin | the container setup (optional) |
| Client data for the chosen build | game profiles only (optional for management/testing) |

Supported targets in this release: Windows x64, Windows ARM64, Linux x64 and Linux ARM64. The
known deployment environment is Debian 13; other Linux distributions are not claimed as
tested. Windows and Linux from-source instructions are below.

## Install a prebuilt package

The packages need the ASP.NET Core Runtime 10 on the machine that runs them.

- **Windows:** download `bubblefighter-fantasy-win-x64.zip` or `bubblefighter-fantasy-win-arm64.zip`
  from the [latest release](https://github.com/Wasabi-FantasyTaleDev/BubbleFighter.Fantasy/releases/latest),
  extract it to a short folder such as `C:\BubbleFighterFantasy` and double-click
  `BubbleFighter.Fantasy.Host.exe`. The setup assistant opens in your browser.
- **Linux:** `curl -fsSL https://wasabi-fantasytaledev.github.io/install.sh | bash`, then run
  `~/bubblefighter-fantasy/bff` and open `http://127.0.0.1:8080/`. The script installs the runtime
  itself when it is missing.
- **Docker:** `curl -fsSL https://wasabi-fantasytaledev.github.io/install.sh | bash -s -- --docker`
  runs the server in a container; add `--with-mysql` for a bundled MySQL 8.4.

[Installation](docs/installation.md#install-a-prebuilt-package) lists every option.

## Quick start from source (Windows PowerShell)

```powershell
git clone https://github.com/Wasabi-FantasyTaleDev/BubbleFighter.Fantasy.git
cd BubbleFighter.Fantasy
dotnet --version   # must report a .NET 10 SDK
dotnet build BubbleFighter.Fantasy.sln -c Release
Copy-Item .env.example .env
```

The build must finish with **zero warnings and zero errors**. Edit the git-ignored `.env` and
set `BFF_MYSQL_PASSWORD` to a nonempty disposable value; profile interpolation requires it even
when a check runs without MySQL.

Start a local server with in-memory storage and no game data, then verify it:

```powershell
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- run --profile management --patch tools/local-checks/isolated-memory.patch.yaml
```

In another terminal:

```powershell
Invoke-RestMethod http://127.0.0.1:8080/health
```

`{"status":"ok", ...}` means the host is up. Stop the server with `Ctrl+C`; data kept in memory
is lost. The `management` profile runs administration and storage without game or relay ports.
For a game profile, provide client data first and choose the matching profile; see
[Installation](docs/installation.md), [Quickstart](docs/quickstart.md) and
[Configuration](docs/configuration.md).

The build output is also a runnable package directory: start
`src/BubbleFighter.Fantasy.Host/bin/Release/net10.0/BubbleFighter.Fantasy.Host.exe` with no
arguments to open the first-run setup assistant, which prints its WebUI address
(`WebUI: http://127.0.0.1:8080/`).

## Quick start from source (Linux)

```sh
git clone https://github.com/Wasabi-FantasyTaleDev/BubbleFighter.Fantasy.git
cd BubbleFighter.Fantasy
dotnet --version   # must report a .NET 10 SDK
dotnet build BubbleFighter.Fantasy.sln -c Release
cp .env.example .env
```

Set `BFF_MYSQL_PASSWORD` in `.env` to a nonempty disposable value, then run the same local
check:

```sh
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- run --profile management --patch tools/local-checks/isolated-memory.patch.yaml
```

```sh
curl http://127.0.0.1:8080/health
```

Stop with `Ctrl+C`.

## Docker Compose

The repository ships a Compose setup with the server and a MySQL database. The default
container profile is a game profile, so provide client data first (see below), or the container
exits at startup when the data is missing.

```sh
cp .env.example .env      # set BFF_MYSQL_PASSWORD and review the values
docker compose -f deploy/docker/compose.yaml up --build
```

The server publishes 7100 (game), 7101 (relay) and 8080 (admin API and web interface); MySQL
listens on `${BFF_MYSQL_PORT:-3306}`. The client-data directory is mounted read-only at
`/app/client-data`, and database state lives in the `mysql-data` volume. The admin listener
binds `127.0.0.1` inside the container by default, so add `BFF_ADMIN_HOST=0.0.0.0` to `.env`
if you need to reach it through the published 8080 port. See
[Deployment](docs/deployment.md) for the external-database Compose template, backups, upgrades
and rollback.

## Client data

The repository does not include the game client or its data files, and this project does not
distribute them. Game profiles read locally supplied files from `client-data/<build>/data`
(client archives at their original relative paths, such as `Packs/...` and `Config/...`) and
`client-data/<build>/dataraw` (loose unpacked files at the same relative paths). Set
`BFF_CLIENT_DATA` to use another root directory. A profile whose data is missing fails its
`clientdata` row at activation; the error names the two paths it looked for. The setup
assistant can also import a prepared folder or ZIP into the server's state directory.

The project will not provide download links for proprietary client files.

## Documentation

| Guide | What it covers |
| --- | --- |
| [Installation](docs/installation.md) | prerequisites, source builds, Docker, first-run setup |
| [Quickstart](docs/quickstart.md) | a verified local server in a few minutes |
| [Configuration](docs/configuration.md) | profiles, patches, environment variables, ports, persistence, client data |
| [Plugins](docs/plugins.md) | the extension model and a first native plugin |
| [SDK protocol](docs/sdk-protocol.md) | optional Python, TypeScript, Go and Rust plugins |
| [Deployment](docs/deployment.md) | release builds, Docker Compose, backups, upgrades, security |
| [Troubleshooting](docs/troubleshooting.md) | symptom → check → cause → fix |
| [Contributing](CONTRIBUTING.md) | build, tests, review rules and packet-data workflow |
| [Changelog](CHANGELOG.md) | dated user-visible changes |

The documentation site publishes English, 中文（简体）, 中文（繁体） and 한국어 from an explicit
page list; see [the site instructions](docs-site/README.md). The Chinese README is
[README.zh.md](README.zh.md).

## Repository layout

| Path | Contents |
| --- | --- |
| `src/BubbleFighter.Fantasy.Abstractions`, `src/BubbleFighter.Fantasy.Contracts` | shared plugin contracts and seams |
| `src/BubbleFighter.Fantasy.Kernel` | plugin runtime: registry, activation, effects, profiles |
| `src/BubbleFighter.Fantasy.Host` | the `bff` console host and its commands |
| `src/Plugins/` | listeners, dialects, game features, persistence, administration, loaders |
| `packets/` | packet layout data and per-build inheritance |
| `profiles/` | plugin rows, build differences, opcode tables |
| `sdk/` | optional Python, TypeScript, Go and Rust plugin SDKs |
| `tests/`, `tools/` | xunit suites, CI gates and local checks |
| `docs/`, `docs-site/` | published guides and the VitePress site |

## Development

Read [AGENTS.md](AGENTS.md) and [CONTRIBUTING.md](CONTRIBUTING.md) before changing code. Make a
focused change on its own branch, explain the behaviour and provide validation evidence.
Builds must produce zero warnings; public types need meaningful XML documentation; deploy-time
values are validated configuration and packet layouts are data under `packets/`.

```sh
dotnet build BubbleFighter.Fantasy.sln -c Release
dotnet test BubbleFighter.Fantasy.sln -c Release
python tools/ci/serial-gates.py --public --results-directory artifacts/validation/public-01
```

The public gate excludes only `Category=LocalClientData` cases and is not the complete release
gate; see CONTRIBUTING.md for the local client workbooks the complete gate requires. Automated
checks do not replace a real client test.

## License

MIT. See [LICENSE](LICENSE). Third-party dependencies remain under their respective licenses.

Report vulnerabilities privately as described in [the security policy](SECURITY.md).
