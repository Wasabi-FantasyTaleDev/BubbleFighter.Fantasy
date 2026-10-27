---
title: "Server configuration"
description: "Compose plugin profiles, set environment variables, ports and persistence."
updated: 2026-10-18
---

Bubble Fighter Fantasy has no fixed set of server components. A **profile** under `profiles/` selects the plugin rows that run, the client build they serve, the listeners they open and the storage they use. This page explains how a profile becomes a running server, where the configuration files live, how environment variables and ports work, and how to switch persistence and client data.

**Who this is for:** operators who need to change what the server runs, and developers who need to inspect the effective configuration before starting it.

**Before you start:**

- Build the server once: `dotnet build BubbleFighter.Fantasy.sln -c Release` must finish with zero warnings. See [installation](installation.md).
- Open a terminal at the repository root. Commands on this page that use `dotnet run` assume that directory, because profiles, packets and client data are resolved relative to the working directory.
- Copy `.env.example` to `.env` and set a nonempty `BFF_MYSQL_PASSWORD`. Profile interpolation requires it even for a check that disables MySQL. Never put real credentials in a file you intend to commit.
- Have client data ready if you intend to start a game build. The repository does not include it; see [Client data](#client-data).

## How configuration works

There are two ways to start the server:

- `bff run --profile <id>` composes a profile from YAML files and starts its rows. Every section below describes this path.
- `bff start` (also used when the executable is started with no arguments) runs the packaged first-run setup profile with the installation assistant and administration rows. It deliberately ignores the repository `.env`; see [Environment variables](#environment-variables).

### Composition order

A profile is built up in layers. Later layers change the result of earlier ones:

1. `profiles/<id>/profile.yaml` names the profile, the client build it serves, the bundles it uses, its own `vars`, and the patch files to apply.
2. Each bundle in `profiles/bundles/<name>.yaml` contributes its plugin rows, in file order.
3. The profile's `patches:` files are applied in the listed order. A build profile usually applies its own `patch.yaml` and then shared supplement patches such as `profiles/bundles/shop-supplement-lucci.yaml`.
4. `profiles/<id>/local.patch.yaml` is applied next when the profile lists it. The file is git-ignored and skipped silently when absent, so it is the place for developer overrides.
5. Every `--patch <file>` given on the command line is applied after all profile patches, in the order given.
6. String values are interpolated against the profile's `vars` and the process environment. `--dump-config` shows the result.

Because CLI patches apply last, a local override file can point `persistence.mysql` at a database and a CLI patch can still switch it off for one run.

### The plugin row

Everything that runs is a **plugin row**. A row names the plugin implementation and passes it configuration:

```yaml
- id: admin.http
  plugin: admin.http
  config:
    listen: "${BFF_ADMIN_HOST:-127.0.0.1}:${BFF_ADMIN_PORT:-8080}"
```

| Key | Required | Meaning |
| --- | --- | --- |
| `id` | yes | Stable row identity used by patches and by the admin API. Unique within a profile. |
| `plugin` | yes | Plugin implementation id discovered from the plugin directories. |
| `config` | no | Mapping bound to the plugin's option type. Unknown keys fail binding. |
| `disabled` | no | `true` keeps the row composed but prevents activation; defaults to `false`. |

Advanced rows may also declare `parent_id`, `isolated_services`, `version` and `lifecycle`. Those keys control nesting, per-build service isolation and resource budgets; the supplied profiles already set them where needed. Treat them as part of the profile definition rather than as deployment settings.

Rows are activated when the services they require become available, not in list order. Two active rows must not publish the same service interface: the registry refuses a second registration. This is why exactly one persistence provider is enabled at a time and why switching storage is a composition change, not a runtime toggle.

### Patch operations

A patch file is a YAML list of single-key operations:

| Operation | Shape | Effect |
| --- | --- | --- |
| `replace` | `{id, plugin?, disabled?, config}` | Replaces the named row. A given `config` replaces the whole row config. |
| `set` | `{id, config: {...}}` | Changes the named config keys and keeps the rest; a `null` value removes a key. |
| `insert` | list of rows | Appends new rows. |
| `remove` | row id | Removes the row. |

`replace` and `set` fail if the row does not exist, and an unknown operation fails composition. The isolated-memory patch used for local checks is a small `replace` patch:

```yaml
- replace:
    id: persistence.mysql
    disabled: true
- replace:
    id: persistence.memory
    disabled: false
```

### Interpolation and validation

`${VAR}` inserts the variable's value and `${VAR:-default}` supplies a fallback. If neither the profile `vars` nor the process environment defines the variable and no default is given, composition fails and names the file, row and variable. A profile's own `vars` take precedence over environment variables of the same name, and disabled rows are not interpolated.

Configuration binding maps `snake_case` keys to the plugin's typed options. Unknown keys, wrong types, out-of-range numbers and failed option validation are all reported together as one `Invalid configuration for row '<id>': ...` error.

## Where files live

| Path | What it holds |
| --- | --- |
| `profiles/<id>/profile.yaml` | Profile identity, client build, `parent`, bundle list, `vars`, patch list. |
| `profiles/<id>/patch.yaml` | The profile's own patch, listed in `profile.yaml`. |
| `profiles/<id>/opcodes.yaml` | Opcode table resolved by name for that client build. |
| `profiles/<id>/local.patch.yaml` | Optional, git-ignored developer overrides, applied automatically when present. |
| `profiles/bundles/*.yaml` | Shared row sets referenced by profiles. |
| `packets/*.yaml` | Packet layouts and `builds.yaml` build inheritance; read at startup, not compiled in. |
| `.env` | Deployment variables; git-ignored. |

The host project copies `bootstrap/`, `profiles/` (without `local.patch.yaml`), `packets/` and the two `data/` files (`data/level-experience.csv` and `data/locales/*.json`) into the build and publish output. Plugin assemblies are published under `plugins/<ProjectName>/`, which is the default plugin root next to the host. A release is therefore self-contained except for the runtime, client data and, when the MySQL provider is enabled, its database.

Paths inside rows resolve against the process working directory. Run from the repository root with `dotnet run`, or from the release directory with the published host. If you start the host from elsewhere, pass `--profiles-dir` and be aware that `packets` and `client-data` paths still resolve against the working directory.

## Inspect before you run

List the profiles the current checkout provides and the plugin implementations it discovers:

```sh
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- profiles list
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- plugins list
```

`profiles list` prints each profile id, a tab and its description; at this release there are 19 profiles (the client-build profiles, `all`, `management` and `setup`). `plugins list` prints each plugin id, its version, the client builds it supports and its origin; use `--plugin-root <dir>` (repeatable) to point discovery at another directory.

Print the effective configuration without starting the server. Always add the isolated-memory patch so a developer `.env` or `local.patch.yaml` that points at a real database cannot be contacted:

```sh
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- run --profile kr-812 --patch tools/local-checks/isolated-memory.patch.yaml --dump-config
```

The dump lists every row with its `id`, `plugin`, the `source` file that supplied it, `disabled` when set, lifecycle budgets and the interpolated `config`. Read it before changing ports or storage: it is the exact composition the server would run. If composition fails, the host prints `error: <message>` on stderr and exits with code 1; the message names the file, row and key that failed.

## Environment variables

### Loading rules

- For every command except `start`, the host looks for a repository root by walking upward from the working directory and then from the host directory, matching a directory that contains `BubbleFighter.Fantasy.sln` or a `.git` entry. When `.env` exists there, its assignments are applied to variables that are not already set.
- Existing process environment variables win. The `.env` file fills gaps; it never overrides your shell or service manager.
- `start` never reads the repository `.env`. The first-run setup profile receives its own variables from the process environment and from `--state-dir`.
- The file is read as UTF-8 and limited to 64 KiB. Lines are `KEY=VALUE`, `export KEY=VALUE` is accepted, values may be quoted, an unquoted `#` after whitespace starts a comment, and a malformed line fails with the file path and line number. Values are never logged. When a file is loaded, the console prints `Loaded unset variables from <path>.`
- A release directory has no solution file or `.git` entry, so a `.env` copied beside a published host is **not** read. In a deployment, set the variables in the process environment, the service manager or the container environment instead.

### Variables you are likely to set

| Variable | Purpose | Default |
| --- | --- | --- |
| `BFF_BIND_HOST` | Address the game and relay listeners bind to. | `0.0.0.0` |
| `BFF_PUBLIC_HOST` | Address advertised to clients for connect and relay endpoints. | `127.0.0.1` |
| `BFF_ADMIN_HOST` / `BFF_ADMIN_PORT` | Administration HTTP bind address. | `127.0.0.1` / `8080` |
| `BFF_LOG_LEVEL` | Console log level: Trace, Debug, Information, Warning, Error, Critical, None. | `Information` |
| `BFF_CLIENT_DATA` | Root directory of per-build client data. | `client-data` |
| `BFF_PACKET_CAPTURE` | Enables the optional full packet recorder. | `false` |
| `BFF_STATE_DIR` | State directory for the first-run setup flow (`start`). | `state` beside the executable |
| `BFF_SQLITE_DATABASE_PATH` | Absolute save-file path for the SQLite provider when a patch enables it. | none |
| `BFF_LEVEL_EXP_TABLE` | Cumulative experience table used by round rewards. | `data/level-experience.csv` |

The MySQL variables are required by the default composition:

| Variable | Purpose | Default |
| --- | --- | --- |
| `BFF_MYSQL_HOST` | Database host. Docker Compose overrides it to its `mysql` service. | `127.0.0.1` |
| `BFF_MYSQL_PORT` | Database port. | `3306` |
| `BFF_MYSQL_USER` | Database account. | `bff` |
| `BFF_MYSQL_PASSWORD` | Password. Must be nonempty because interpolation runs before a disabling patch. | empty, must be set |
| `BFF_MYSQL_DATABASE` | Database name. Any existing database works. | `bf_game_db` |

Ports and gameplay values are documented in the sections below. `.env.example` is the full commented list; copy it rather than inventing variable names.

## Ports and addresses

`BFF_BIND_HOST` is the interface the server listens on. `BFF_PUBLIC_HOST` is the address the server tells clients to use. They may differ behind NAT: bind to the interface that reaches the clients, and advertise the public address the clients can actually reach. Leaving `BFF_PUBLIC_HOST` at its loopback default works only when clients run on the same machine.

Listener ports come from the profile, usually through per-build variables and `vars`. The two baselines at this release use:

| Endpoint | kr-1256 (modern) | kr-812 (legacy) |
| --- | --- | --- |
| Game TCP | `7100` (`KR_1256_GAME_PORT`) | `7110` (`KR_812_GAME_PORT`) |
| Relay TCP | `7101` (`KR_1256_RELAY_PORT`) | not opened by the legacy bundle |
| Administration HTTP | `8080` (`BFF_ADMIN_PORT`) | `8081`, moved by the profile patch |
| Optional native selector | not used | `7112` when `center-selector.yaml` is mounted |

The `all` profile composes the brought-up client builds into one process, each on its own game listener. Its default game ports run from `7100` in steps of ten (`kr-1256` 7100, `kr-812` 7110, `id-400` 7120, and so on through `cn-408` 7250). Use `--dump-config` to read the exact `listen` strings for the profile you selected before opening firewall rules.

Keep the administration port on loopback or a controlled network. Open only the game and relay ports that the selected profiles serve, and never expose MySQL on a public interface.

## Persistence

Three provider rows ship in the core bundle:

| Row | Durability | Use when |
| --- | --- | --- |
| `persistence.memory` | Process lifetime only; reset on restart. | Disposable local checks and tests. |
| `persistence.sqlite` | One local save file. | A single host that needs durable data without a database server. |
| `persistence.mysql` | External MySQL database. | A real deployment, especially with more than one process or host. |

Only one provider may be active. The registry refuses a second registration of the same repository interfaces, so enable exactly one and disable the other two in the same patch. The MySQL row is enabled and the other two are disabled in the core bundle; a patch switches that.

### Switch providers with a patch

SQLite, replacing the default MySQL configuration:

```yaml
- replace:
    id: persistence.memory
    disabled: true
- replace:
    id: persistence.sqlite
    disabled: false
    config:
      database_path: "${BFF_SQLITE_DATABASE_PATH}"
```

To return to MySQL, replace `persistence.mysql` with `disabled: false` and disable the provider you enabled.

### MySQL

The default connection string interpolates the `BFF_MYSQL_*` variables and pins `SslMode=none`. Deployments that require TLS override the whole `connection_string` in a patch instead of editing the bundle.

When the row activates, it opens a connection and runs `SELECT 1`. A missing password, malformed connection string or unreachable server fails the row, and startup reports the failure instead of running without storage. With `auto_migrate` at its default `true`, the plugin then applies its embedded migrations:

- Migration scripts are embedded resources named `NNNN_description.sql`, applied in filename order. This release ships versions up to `0032`.
- `schema_migrations` is created first if it does not exist, then each pending version runs in its own transaction and is recorded. The console prints `Applied migration <version> (<name>).` for each one.
- A misnamed, duplicate-version or empty script fails the run instead of being skipped.
- Migrations are **forward-only**. There is no downgrade path, so an older server binary may not be able to run against a database that a newer release has migrated. See [Deployment](deployment.md) for the backup and rollback procedure.

### SQLite

The SQLite row needs an absolute `database_path`; the option rejects relative paths. Keep the file outside the release and log directories so replacing a release does not remove it, and do not share one save file between two running servers. The row also has bounded player, account, database-size and lock-wait limits that can be set in the same config mapping.

The memory provider is a bounded in-process store with the same repository interfaces. Its state disappears at shutdown, and its row deliberately publishes no database factory. Memory and MySQL are mutually exclusive.

### Isolated local checks

`tools/local-checks/isolated-memory.patch.yaml` disables `persistence.mysql` and enables `persistence.memory`. Because CLI patches apply last, it wins over a developer `.env` or `local.patch.yaml` that points at a real database. Add it to every local `run`, including `--dump-config` checks; the [isolated local check](#isolated-local-check) recipe shows the full command. Memory data is lost when the process stops, and the patch is never a substitute for the production persistence configuration.

## Client data

Game profiles read the selected client build's own data; the repository does not contain the game client or its files. The data root defaults to `client-data` and can be moved with `BFF_CLIENT_DATA`. Its layout is:

```text
client-data/
  <build>/
    data/      client archives at their original client paths, for example Packs/ and Config/
    dataraw/   loose unpacked files at the same client paths, read first
```

`<build>` is the profile id, for example `kr-812` or `kr-1256`. Legacy profiles read `${BFF_CLIENT_DATA:-client-data}/${BFF_PROFILE_ID}/data` and `.../dataraw`; the modern `kr-1256` profile uses the same shape for `kr-1256`.

If neither directory exists, the `clientdata` row fails at activation with a message naming both paths it looked for, and the server does not converge. Provision the data before starting a game profile. Files are read-only input: the server never writes into `client-data`, which is why the Docker setups mount it read-only.

`management` and `setup` do not mount the client-data rows. The first-run setup flow can import prepared data into its state directory; see [installation](installation.md).

## Common configurations

The first recipe is an isolated check. The others are deliberate durable configurations: they point at storage you own, and they do **not** include the isolated patch because it would disable the provider being configured. Never aim a durable recipe at a database you do not intend to change.

### Isolated local check

```sh
cp .env.example .env
# Edit .env and set BFF_MYSQL_PASSWORD to a disposable nonempty value.
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- run --profile management --patch tools/local-checks/isolated-memory.patch.yaml
```

The console reports the activated rows and, once the host is ready, `Server started with <n> active row(s).` Check `GET /health` on `http://127.0.0.1:8080/`: it returns JSON with a `status` field. Stop with `Ctrl+C`.

### SQLite server

Create `sqlite.patch.yaml` in the repository root:

```yaml
- replace:
    id: persistence.mysql
    disabled: true
- replace:
    id: persistence.memory
    disabled: true
- replace:
    id: persistence.sqlite
    disabled: false
    config:
      database_path: "${BFF_SQLITE_DATABASE_PATH}"
```

Set `BFF_SQLITE_DATABASE_PATH` to an absolute path in `.env`, then start the intended profile (a game build needs its client data):

```sh
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- run --profile kr-1256 --patch tools/local-checks/isolated-memory.patch.yaml --patch sqlite.patch.yaml
```

Look for the SQLite activation line in the console and confirm the save file exists at the configured path. Stop with `Ctrl+C`; the save file remains.

### MySQL server

Set the real connection values in `.env` and leave the default MySQL row enabled:

```sh
BFF_MYSQL_HOST=127.0.0.1
BFF_MYSQL_PORT=3306
BFF_MYSQL_USER=bff
BFF_MYSQL_PASSWORD=<your-database-password>
BFF_MYSQL_DATABASE=bf_game_db
```

For a local configuration check, inspect the profile with isolated in-memory storage instead of connecting to the database:

```sh
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- run --profile kr-1256 --patch tools/local-checks/isolated-memory.patch.yaml --dump-config
```

This dump deliberately disables MySQL and starts no listeners; it does not test the database connection or run migrations. For persistent operation, follow [Deployment](deployment.md) with explicitly reviewed database settings. An actual MySQL activation logs `MySQL persistence connected to <host>:<port>/<database> as <user>.` and applies pending migrations.

### NAT: bind and advertised address

Set both addresses in `.env` so listeners bind to every interface while clients are told the public address:

```sh
BFF_BIND_HOST=0.0.0.0
BFF_PUBLIC_HOST=<public-address-clients-use>
```

Port variables can be changed the same way, for example `KR_1256_GAME_PORT` and `KR_1256_RELAY_PORT`. Open only those ports in the firewall and verify the addresses with `--dump-config` before starting. For a single listener whose address must differ from the profile default, a `set` patch on that listener row is the alternative:

```yaml
- set:
    id: game-listener
    config:
      listen: "0.0.0.0:7100"
```

## Multi-client profiles

Profiles serve different historical clients. A build id is `<region>-<wire version>`, where the wire version is the client's `ClientVersion_Major`, not its file version: `kr-812`, `cn-324`, `id-400`, `tw-454`, `kr-1256`. The profile directory name is the build id used throughout the packet tables.

`kr-812` (Korean 2015) is the **legacy baseline**: `profiles/bundles/legacy.yaml` is its row set, and legacy builds state only their differences (ports, cipher tables, seats, seats-related settings, packet switches) in their own `vars` and `patch.yaml`. `kr-1256` (Korean final build) is the **modern baseline**: it has its own `kr-1256`, `ingame` and `shop` bundles, and its packet layouts are the reference for `cn-774` and `tw-454`.

| Profile | Client build | Role |
| --- | --- | --- |
| `kr-812` | `kr:812:458` | Legacy baseline for the legacy family. |
| `kr-1256` | `kr:1256:1304` | Modern baseline; the default Dockerfile profile. |
| `all` | 16 builds | Every brought-up build in one process, each on its own listener port. |
| `management` | none | Administration and storage only; no game or relay ports. |
| `setup` | none | Database-independent first-run installation and administration. |

The remaining client-build profiles each serve one build. Their packet layouts inherit through `packets/builds.yaml` and the profiles' own `parent:` lineage: `cn-279` is based on `cn-324`, which is based on `kr-812`; `cn-738` and `cn-742` follow `cn-735`'s layouts; `tw-454` is based on `cn-774`, which is based on `kr-1256`. A build that differs only slightly annotates its differences instead of restating the layout.

Builds also differ in capabilities, recorded per build in `packets/builds.yaml` and inherited through the base. For example the category-6 restriction on discarded expired items is on for `kr-812` and off for `cn-324`; the modern `kr-1256` turns many legacy capabilities off until their consumers are audited in that build; legacy rooms carry twelve seats in the baseline and eight in `cn-324`. Capability switches decide what a build's dialect sends and enables; they are data, not guesses.

Choose a profile by the client build you intend to connect: read the client's version, find the matching profile id, use that profile's ports and provide that build's client data. `all` is for running several builds together; it starts each build's listeners and needs every selected build's data.

What a listed profile means: the repository composes these rows and carries this build's packet data and capability records. What it does not mean: that every gameplay feature is implemented, or that a real client has been verified. A passing automated check, an enabled capability or a listed profile does not establish real-client compatibility; capability records describe audited native support, and native acceptance remains a separate test.

## Optional feature bundles

A few bundles are opt-in and are listed only by profiles whose client audit supports them.

**`player-settings`** mounts `player.settings`, the typed storage of players' customized key bindings. The packets themselves are bound by the `customized_keys` option of the `dialect.messages` row, stated by `kr-812`, `kr-1256`, `cn-774` and `tw-454`; other profiles need an audit of their native control names and packet paths first. Each `player.settings` row requires `protocol` and uses a per-profile `scope` so names from different builds are not mixed. The persistence providers store the maps per account; MySQL stores them through migration `0032_customized_keys.sql`. The per-account budgets are configured on each provider with `customized_keys` (`max_scopes` 1–64, `max_mappings` 1–512, `max_name_characters` 1–1024, and their combination must fit the packet body). Exceeding a budget fails instead of evicting another saved map.

The expired-item discard is stated the same way: the `inventory_actions` option of the `dialect.messages` row binds `gcpReqInventoryThrowItem`/`gspRepInventoryThrowItem` (on for every legacy build and `kr-1256`), and the `inventory.throw` row inside the build's own bundle removes the item through the stored equipment services. The behavior is deliberately narrow: one authenticated player's expired item is removed from both bags, active entitlements refuse the whole removal, and the build's category-6 exclusion is honored. Requests from matches are refused. Real-client acceptance remains pending; table coverage alone is not enough to state the option on another build.

**`launcher.http`** is the optional public launcher API. It is disabled by default and separate from administration. It accepts `minimum_launcher_version` (empty disables the check) and `plugin_recommendations`, which contain store identities and optional exact `builds`, never executable paths or download URLs. An older launcher receives HTTP 426 when a minimum is configured. Every lobby row's `authentication` option (`BFF_LOGIN_AUTHENTICATION`, default `ticket`) admits only logins that redeem a launcher ticket and then needs this row active; `open` admits any account name and suits only a trusted LAN. The [Launcher](launcher.md) page covers the API, its client presets, the account and ticket flow, and the operator-facing configuration.

Launcher request and sign-in limits are configured on the `launcher.http` row:

| Key | Default | Behavior |
|---|---|---|
| `requests_per_minute` | `120` | Requests per transport peer IP per one-minute window; forwarding headers do not change the peer. |
| `max_rate_limit_addresses` | `4096` | Retained IP windows (1–100000); the oldest window is evicted when full. |
| `max_sessions_per_account` | `8` | Live durable sign-ins per account (1–1024); a new sign-in revokes the oldest by creation time. |
| `max_sessions` | `1024` | Existing process-wide bound on access bearers and pending tickets. |

Revoking an old sign-in also removes its local access bearer and pending game tickets. Expired refresh sessions are deleted before the listener starts, then during sign-in at most once per hour. The account limit survives a server restart because it is enforced by the session store.

## Validation and safety

- Unknown configuration keys and invalid values fail composition or activation; they are not ignored. Rows that fail activation are reported with their state and diagnostic instead of being silently skipped.
- `--dump-config` prints interpolated values, including credentials. Sanitize it before sharing it in a bug report.
- Never commit `.env` or `local.patch.yaml`; both are git-ignored, and both can hold secrets or machine-specific paths.
- Add `--patch tools/local-checks/isolated-memory.patch.yaml` to every local `run`. It is the only reliable way to keep a developer's `.env` from reaching a real database.
- A profile or patch file change takes effect on the next start: the profile is composed once at startup. The admin API can reload an individual row, but that is a plugin-lifecycle operation, not a way to apply a new profile. See [plugins](plugins.md) for reload behavior.

## If something goes wrong

- **Composition fails with a missing environment variable.** The message names the variable; add it to `.env` (or the process environment) or use a `${VAR:-default}` form where an empty value is valid.
- **A patch names an unknown row.** The profile and patch list disagree; check the row id with `--dump-config` on the unpatched profile or in the bundle that supplied it.
- **A game profile fails at activation with a client-data message.** The named `data`/`dataraw` directories do not exist; provision them for that build id or start `management` instead.
- **The listener cannot bind.** Another process holds the port; change the per-profile port variable or stop the other process.

See [troubleshooting](troubleshooting.md) for the ordered investigation checklist.

## Next

Continue with [deployment](deployment.md) to build and run a release, or read [plugins](plugins.md) to understand what the rows themselves do.
