---
title: "Server configuration"
description: "Compose plugin profiles, set environment variables, ports and persistence."
updated: 2026-11-07
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
| Relay TCP | `7101` (`KR_1256_RELAY_PORT`) | `7111` (`KR_812_RELAY_PORT`) |
| Administration HTTP | `8080` (`BFF_ADMIN_PORT`) | `8081`, moved by the profile patch |
| Optional native selector | not used | `7112` when `center-selector.yaml` is mounted |

The `all` profile composes the brought-up client builds into one process, each on its own game listener. Its default game ports run from `7100` in steps of ten (`kr-1256` 7100, `kr-812` 7110, `id-400` 7120, and so on through `cn-408` 7250), and each build's relay listens one above its game port (`<BUILD>_RELAY_PORT`, advertised as `<BUILD>_PUBLIC_RELAY_PORT`). The legacy builds play a round over their relay when it connects and fall back to the game connection when it fails; `cn-706` and `cn-705` keep their rounds on the game connection because their relay packets are not audited. Use `--dump-config` to read the exact `listen` strings for the profile you selected before opening firewall rules.

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

### Battle records

The `ingame` option `record_battle_statistics` defaults to `false`. Enabling it requires a progress provider that explicitly supports battle records, the level table and the shared result senders. It records accepted bubble, finishing-blow, rescue, death and score counters by game mode, together with the terminal result. An active departure records an escape without awarding completion rewards. Result records are settled before presentation, so leaving the result screen does not erase a completed match. Loading failures and canceled matches do not fabricate results.

Memory, MySQL and SQLite support this option. MySQL migration `0039` adds `player_battle_records`; SQLite schema `9` adds per-mode aggregates to the player snapshot. Both durable providers commit counters, progress, wallet credits and the existing match receipt in one transaction. Reopening the database retains history and repeating a match does not add it again. An older receipt is not backfilled with newly supplied counters. Back up storage before upgrading; older SQLite readers refuse schema `9`. No earlier unrecorded history is reconstructed. All providers expose `max_battle_modes_per_player` (default `128`); MySQL and SQLite accept `1..4096`. A full mode budget rejects the whole new settlement without evicting records. Memory remains temporary and also exposes `max_battle_settlements_per_player` (default `4096`); durable stores retain their existing database-backed receipts.

A bomb series retains one match identity and cumulative counters through continuation loading. If an opponent leaves between played rounds, the remaining terminal roster is recorded before another leave can cancel the presentation. Initial loading alone is not a played match. Settlement retries are finite and final failures are logged; there is no durable outbox for recovery from arbitrary persistent storage failures.

### Fighter appearance and recorded statistics

`lobby.fighter_info` reads the requested online account's current character-bag worn items, with expiry and exhausted-life filtering. It publishes that target's equipment cache before the fighter dialog. Missing or ambiguous characters and replaced login sessions receive no successful snapshot. This inspection is read-only and is limited to online players of the same build and data domain; it does not preview independent preset pages or invent per-instance attributes.

The separate `lobby.fighter_battle_statistics` option defaults to `false`. When enabled, it requires `fighter_info` and a battle-record repository in the same data domain. The native primary No Item/Item page receives only the recorded totals for modes `1` and `3`: wins, losses, draws, escapes, bubbles, finishing blows and rescues. Other modes, server personal score and arena ratings are not projected. Each client computes its own games-played and win-rate display from the component counts, including its own draw rule.

If no primary-mode history is known, or exact counts would overflow the client's signed calculations, the request receives the native failure reply. Counts are never clamped, rescaled or substituted with a zero career. With this option disabled, the appearance query keeps the native constructor's statistical defaults; those defaults are not recorded history. Memory history covers only matches recorded in the current process, and no original-server lifetime or ranked eligibility rules are reconstructed.

The audited primary layouts cover `kr-1256` (KR2026), `kr-812`, `id-400`, `id-1809`, `cn-324`, `cn-717`, `cn-774`, `tw-454`, `cn-279`, `cn-406`, `cn-706`, `cn-404` and `cn-408`. TW454's complete fighter-info exchange is separately gated by `BFF_TW_454_FIGHTER_INFO=false`; set it to `true` to opt in both dialect and lobby bindings. Layout coverage does not establish native-client acceptance.

For an isolated single-build profile, apply the following patch together with `tools/local-checks/isolated-memory.patch.yaml`. The `set` operations preserve the existing protocol, level and result configuration. A missing capability fails activation rather than returning fabricated data.

```yaml
- set:
    id: ingame
    config:
      record_battle_statistics: true
- set:
    id: lobby
    config:
      online_players: true
      fighter_info: true
      fighter_battle_statistics: true
```
### Relationship storage

The memory row also provides the preparatory `ICoupleStore` relationship contract. Its `couples` mapping has independent limits: `max_relationships` (default 4096, range 1–65536), `max_pending_invitations` (1024, 1–65536), `max_pending_per_account` (16, 1–1024), `max_completed_invitations` (1024, 1–65536), `max_message_length` (512 UTF-16 code units, 1–4096), and `max_authority_length` (128 UTF-16 code units, 1–1024). Accepting and breaking up update both memberships atomically within this process. Completed receipts are bounded and may be evicted; callers must always use fresh server-generated invitation tokens. This contract has no MySQL or SQLite provider, does not consume rings, and does not activate native couple requests, login restoration or cross-node notifications. Packet-table coverage alone does not enable those game features.

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

The installed-client upload and `tools/client-data/bfdata.py` use the same generation rules. Map `.wb` resources, including nested `Maps/<Directory>/track.wb`, are extracted into `dataraw` with their original client paths and SHA-256 provenance in `sources.tsv`. Prepared `data`/`dataraw` imports preserve these files too. If an older generated tree lacks a map's WB, regenerate into a new data directory from that build's client before enabling a feature that reads it. The shared `WorldBlockFile` helper reader preserves each helper's name, position and rotation for mode-specific consumers; map coordinates are not compiled into the server.

For CN2011 (`cn-279`), `ingame.item_mode_fallback_point_count: 4` enables a server policy for ordinary Item Mode when the master's map report contains no random-box point. It selects up to four distinct, spatially spread positions from that map's `#sp_star_` helpers, or `#ic_` helpers when there are no star helpers, and schedules neutral kind-0 boxes using `supply_default_min_ms` / `supply_default_max_ms` (15–30 seconds by default). This periodic supply is server policy, not a claim that the original client produced fixed random-box markers. Existing reported random points are preserved. Other profiles keep the option at zero. `item_mode_fallback_maximum_file_bytes` defaults to 8388608 and `item_mode_fallback_maximum_candidates` to 4096; missing, unsupported or invalid map resources fail the report instead of creating guessed coordinates.

CN279 keeps its seventeen weighted grade-sheet kinds and one native held slot. The client chooses the item at collision; the server validates that result. CN279 also enables `item_mode_resolve_invalid_random_pickups`: a live, eligible random box whose requested kind is zero or outside the permitted pool receives one uniformly selected permitted kind. Valid client results are retained; fixed-kind boxes and ineligible objects cannot use this policy. With the option disabled, unresolved kind zero is rejected without consuming the box. This fallback distribution is server policy, not the native grade weights. Native action-gauge drops retain their initial velocity. Freeing a full slot renews still-live refused boxes with a fresh identity; a renewed dropped box starts at rest. Item use is broadcast to every participant, including team medipack kind 129, so the native item-effect path can run. The server does not introduce a separate hit-point ledger for this effect. These automated paths still require native-client acceptance.

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

`dialect.messages.config.science_lab` defaults to `false`. The verified operation rows for `kr-1256`, `cn-774` and `tw-454` allow this option to answer all eight laboratory operations with the client's closed-laboratory result (5 on KR, 4 on CN/TW). Activation requires every verified request/reply pair and the request/reply layouts; incomplete tables fail. This is not a complete laboratory: recipes, costs, success probabilities, rewards and item mutations are absent, and no reliable request chain for opening the laboratory state has been established. Enabling this option does not publish laboratory state or enable its UI. The 34-byte KR state, 36-byte CN state and 26-byte KR empty item-list pins cover data only.

The expired-item discard is stated the same way: the `inventory_actions` option of the `dialect.messages` row binds `gcpReqInventoryThrowItem`/`gspRepInventoryThrowItem` (on for every legacy build and `kr-1256`), and the `inventory.throw` row inside the build's own bundle removes the item through the stored equipment services. The behavior is deliberately narrow: one authenticated player's expired item is removed from both bags, active entitlements refuse the whole removal, and the build's category-6 exclusion is honored. Requests from matches are refused. Real-client acceptance remains pending; table coverage alone is not enough to state the option on another build.

**`launcher.http`** is the optional public launcher API. It is disabled by default and separate from administration. It accepts `minimum_launcher_version` (empty disables the check) and `client_policy`, the typed per-plugin rules the Launcher enforces; rules name store identities and declared store sources, never executable paths. An older launcher receives HTTP 426 when a minimum is configured. Every lobby row's `authentication` option (`BFF_LOGIN_AUTHENTICATION`, default `ticket`) admits only logins that redeem a launcher ticket and then needs this row active; `open` admits any account name and suits only a trusted LAN. The [Launcher](launcher.md) page covers the API, its client presets, the account and ticket flow, and the operator-facing configuration.

Launcher request and sign-in limits are configured on the `launcher.http` row:

| Key | Default | Behavior |
|---|---|---|
| `requests_per_minute` | `120` | Requests per transport peer IP per one-minute window; forwarding headers do not change the peer. |
| `max_rate_limit_addresses` | `4096` | Retained IP windows (1–100000); the oldest window is evicted when full. |
| `max_sessions_per_account` | `8` | Live durable sign-ins per account (1–1024); a new sign-in revokes the oldest by creation time. |
| `max_sessions` | `1024` | Existing process-wide bound on access bearers and pending tickets. |

Revoking an old sign-in also removes its local access bearer and pending game tickets. Expired refresh sessions are deleted before the listener starts, then during sign-in at most once per hour. The account limit survives a server restart because it is enforced by the session store.

## Optional UDP transport

UDP is provided by the `net.udp` plugin. Game login, room admission and match authentication continue over TCP; UDP carries the audited native movement and discovery traffic. No shipped profile mounts a UDP listener by default.

| Optional patch | Audited profiles | Transport |
| --- | --- | --- |
| `profiles/bundles/udp-relay.patch.yaml` | `kr-812`, `cn-717`, `id-1809`, `kr-1256`, `cn-774`, `tw-454` | Authenticated movement through the server's UDP endpoint, with TCP fallback for recipients without a bound endpoint. |
| `profiles/bundles/udp-peers.patch.yaml` | `cn-279`, `cn-324`, `cn-406`, `id-400`, `cn-404`, `cn-408`, `cn-705`, `cn-706` | Server-coordinated discovery and direct peer movement; unsuccessful peer paths retain match TCP delivery. |

Apply one patch to one matching profile. The patches are alternatives, and their unprefixed row ids are not intended to be applied directly to `all`. Set `BFF_UDP_PORT` to a free UDP port, `BFF_BIND_HOST` to the listener address, and optionally `BFF_UDP_PUBLIC_PORT` to the externally forwarded UDP port (zero advertises the actual bound port). Forward UDP separately from the existing game and relay TCP ports. Peer discovery uses the authenticated game server's IPv4 address; relay bypass uses the relay server's IPv4 address, so an external UDP mapping must use the corresponding public address.

For example, after assigning the profile's ordinary TCP/admin ports to free local ports and provisioning its client data:

```powershell
$env:BFF_MYSQL_PASSWORD = "isolated-unused"
$env:BFF_MYSQL_HOST = "127.0.0.1"
$env:BFF_MYSQL_PORT = "1"
$env:BFF_LOGIN_AUTHENTICATION = "open"
$env:BFF_BIND_HOST = "127.0.0.1"
$env:BFF_UDP_PORT = "7603"
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- run --profile cn-324 --patch tools/local-checks/isolated-memory.patch.yaml --patch profiles/bundles/udp-peers.patch.yaml
```

`BFF_UDP_ENABLED` defaults to `true` once either optional UDP patch is mounted. Set it to `false` to keep TCP operation and avoid opening the UDP listener or starting UDP negotiation. Single-player rooms use TCP by default. UDP movement is authorized only after the round starts, all relays initialize and endpoint verification completes.

UDP setup is limited to published gameplay rooms. CN279, CN324, CN406 and ID400 prepare discovery and acknowledge the peer table in the waiting room because their native scene update does not send discovery in the game scene. Full room installations invalidate that preparation; control messages share the room operation queue so they cannot overtake room installation or stage entry. Punching and direct movement still wait for the initialized playing round. Other audited builds start negotiation after that barrier. Login and automatic scenes, menus, lobbies, plazas and solo training never negotiate UDP; a forced room override cannot bypass this restriction.

The room host can use `!udp on` to force UDP negotiation, including in a single-player room, `!udp off` to keep TCP, or `!udp` to toggle the current room preference. An unavailable or globally disabled UDP transport cannot be enabled by a room command. Unverified or unreachable paths keep TCP fallback. All plugin commands accept `/`, `!` and `！` equivalently; these characters only trigger command parsing. Game-chat hints use `!` because `/` may be intercepted by native client commands. Administration and console input also accept bare commands such as `udp on`, and their usage hints use `/`.

Any room member can type `!udp status` to see whether UDP is in use: the transport kind (`relay` through the server or direct `peer`), the room preference, and for every member `UDP`, `NEGOTIATING` or `TCP` with the reason, such as waiting for the round, offered but no UDP probe received, verified, or the client's fallback to TCP. It reports when the server has UDP off or the client version has no UDP setup. `!plaza` leaves the current room at once, even during a round, and goes to the plaza: the seat is released as on a voluntary leave, so the round settles the leaver, the others play on and the last player's departure deletes the room. A TCP fallback names its cause: no UDP probe reaching the server, challenge replies lost on the way back, discovery or peer-table timeouts by slot, a closed match connection, or no direct path between players.

`match-udp-directory.max_sessions` bounds authenticated bypass leases. `match-udp-peers` bounds retained rounds and peers with `max_rounds`/`max_peers`; its discovery, table acknowledgment, punch and control deadlines are configurable. Private endpoint candidates are shared only between peers observed at the same public IPv4 address. Room changes, round changes, disconnects, consumer unload and relay replacement revoke the applicable paths. These are IPv4 transports; an arbitrary NAT or firewall is not guaranteed to permit direct peer traffic.

The tables also audit `cn-735`, `cn-738` and `cn-742` bypass envelopes: four-byte cookies with floating-point movement. Their transport adapters have isolated wire tests, but their research profiles do not yet mount the complete room/match stack needed by the relay patch. This is packet coverage, not a supported end-to-end profile configuration. Automated socket scenarios simulate the client's native protocol; real game-client acceptance remains separate.

For a multi-build server, add the matching patch operations to each selected build's optional `local.patch.yaml`, using unprefixed row IDs. Shared-profile patches run before build rows are appended, so a patch on `all` cannot target `kr-812.dialect.rooms` or other namespaced build rows. Give each build a distinct UDP port. The peer patch also enables its native relay prerequisites; `cn-705` and `cn-706` keep those rows dormant without the patch, and the patch does not enable scene-seed authority.

UDP socket subscriptions, lease registration, room-queue callbacks, span/out-parameter lookups and IP endpoint records currently require native plugins; the remote SDK has no adapters for those operations. Serializable room snapshots and relay control operations remain available through the contract schema.

Native peer setup accepts `punch_retry_ms` on `match-udp-peers` (default 250; 0 disables retries). It resends the native punch-start control inside `punch_window_ms`, without extending that window or replacing the peer identities. A direct path still requires the current authenticated match connection's per-peer membership report. `!udp status` distinguishes a missing native result from a client-reported zero path count; neither alone proves a firewall or NAT fault.

For builds whose discovery runs in the waiting room, `discovery_refresh_ms` (default 1000; 0 disables refresh) repeats the native one-shot offer with the same peer identity while the room is waiting. Loading stops these offers because those clients no longer pump discovery there. A changed public endpoint or local candidate republishes the table and requires acknowledgments again. Discovery is stopped before the initialized round starts punching.

## Optional server discovery

The schema-1 server directory includes response `generated_at_utc` and target `observed_at_utc`, `expires_at_utc` and `status_revision` (Contracts 4.5.0). Timestamps are UTC RFC3339 values; all three target fields are null before the first accepted report. The target fields describe one source-owned report. Re-reading or re-registering the same lease never renews its expiry; expired manual targets retain their expired metadata, and revocation or clock changes can shorten the expiry. Expiry never exceeds the remaining source lease projected into UTC. Revisions are opaque equality tokens, not sortable versions or lease credentials. Consumers must reject missing, malformed or expired metadata when using it for placement. Response generation does not renew a target. These public freshness fields do not admit an account or bind a ticket to backend membership; automatic launch still needs an explicitly admitted per-launch candidate set bounded by both observation and authentication-grant expiry.

The optional `cluster-launch-selection.patch.yaml` mounts `cluster.launch-selection` at the ticket issuer. Configure its local node, authority identity and exact API origin, persistent scope, and automatic selector identity through `BFF_CLUSTER_NODE_ID`, `BFF_CLUSTER_AUTHORITY_ID`, `BFF_CLUSTER_AUTHORITY_URL`, `BFF_CLUSTER_DATA_SCOPE` and `BFF_CLUSTER_AUTOMATIC_SELECTOR_ID`. Authenticated `POST /v1/launch` with `serverSelection: true` returns a ticket-bound `serverSelection` object: `schema`, `grantId`, `build`, `authorityId`, `policyRevision`, `dataScope`, `expiresAtUtc`, `generated_at_utc` and `servers`. Its configured member pool may contain offline or never-observed nodes; that is permission to authenticate, not placement health. The snapshot fixes native selector identities, physical node identities and endpoint bindings for this launch. The ticket keeps its ordinary lifetime, bounded by the authorizing bearer; a short heartbeat never shortens slow-start login time. `POST /v1/launch/servers` requires the same authenticated session and `{ticket,build,grantId}` and refreshes observations only within those original bindings. It never spends or replaces the ticket, adds members, changes endpoints or extends source expiry. A consumed, expired or invalid grant returns HTTP 409 `admission_closed`, allowing a refresh loop to finish after successful login. Trusted-worker redemption checks its authenticated node identity against the ticket membership atomically with one-use consumption; local redemption uses the configured local node identity. Ordinary launches remain unchanged. This API does not establish native redirect acceptance, shared storage or cross-node room admission. When Center is explicitly enabled in gateway mode and the directory has an automatic row, mounting the issuer selection row scopes every authenticated ticket, including requests that omit the flag; explicit `serverSelection: false` returns HTTP 409 `server_selection_required`. The row cannot be bypassed by choosing an older request shape. `launcher.http.max_selection_targets` bounds all retained selector/endpoint appearances per grant (default 128). Each admitted worker requires exactly one canonical manual selector with the same endpoint binding; the issuer validates configured builds before publishing this service. Missing or ambiguous native mappings fail activation. Refresh cannot change that canonical selector identity even through the automatic entry.

Standalone profiles do not mount `cluster.directory` or `cluster.node`. The optional `profiles/bundles/cluster-directory.patch.yaml` example mounts both for `kr-1256`; `cluster-node.patch.yaml` mounts only a reporter. Configure the reporter's `builds` and every node's `endpoints` explicitly. Supply a distinct random `BFF_CLUSTER_NODE_KEY` for each node through deployment secrets. The private listener defaults to `127.0.0.1:8093`; the operator explicitly selects HTTP or HTTPS for reporters, without automatic upgrades.

The optional `profiles/bundles/cluster-room-directory.patch.yaml` mounts `cluster.room-directory` and provides `IFederatedRoomDiscovery`. Set `node_id`, `directory_url`, `access_key` and an explicit `builds` allowlist. Each call obtains a fresh authenticated room list; it does not cache a previous list after a failure. `max_rooms`, `max_response_bytes`, `max_concurrent_requests` and `request_timeout_seconds` bound room counts, buffered bytes, simultaneous requests and the whole fetch deadline. Excess callers are refused without queuing. Redirects, malformed bodies and unconfigured builds fail explicitly. Directory room membership controls visibility independently of account data; discovery does not grant room admission, reserve a seat or change the native room list. Standalone profiles do not mount this row. Contracts 4.6.0 adds the discovery interface.

`cluster.directory.room_preparation_permits` optionally exposes authenticated node-only preparation permit routes; it defaults to false. The source posts to `/v1/nodes/{source}/room-preparations` with its current lease token/epoch and the destination node/epoch, build, native room number, room creation identity and account number. Issuance requires fresh ready nodes, matching nonempty data/join scopes, shared room visibility, one explicit master-issued authority and the same reported authoritative database identity. The destination claims once at `/v1/nodes/{destination}/room-preparations/claim`, authenticated with its own node key and current process lease. Claiming rechecks node leases, endpoint authority and the exact live waiting room; a released node or recycled room number cannot inherit a permit. The source may cancel its own permit with authenticated DELETE on its issue route. `max_room_preparation_permits` bounds retained permits (default 512); `room_preparation_permit_lifetime_ms` sets monotonic expiry (default 10000, allowed 100..60000). Directory storage retains only a token hash, never a room password. A permit authorizes destination preparation only: it does not reserve a seat, authenticate a native game connection, transfer account ownership or enable remote room clicks. Friendly and independent room directories remain discovery-only.

Published gameplay rooms now expose detached native list entries through `IPublishedRoomDirectory` (Contracts 4.7.0). A new `instanceId` identifies every creation independently of recyclable room numbers; the heartbeat retains the native master nickname, mode and settings without passwords, account records or session prefixes. Both the directory and discovery client validate native metadata against its summary and enforce `max_room_settings_bytes` (default 65536) across all settings blocks and round selections. Hidden rooms, training scenes and tutorial rooms are not published. This metadata does not yet project remote rows into the native browser or authorize cross-node joining.

The optional `profiles/bundles/cluster-room-browser.patch.yaml` mounts one `cluster.room-browser` per `client_build`, consuming authenticated discovery and providing `IRoomBrowser` (Contracts 4.8.0). Configure the same `node_id` as the directory client. Browser state lives exactly as long as its game connection, which the listener already bounds. `max_aliases_per_connection` bounds lifetime identities (default and maximum 32767, the native signed 16-bit alias). Local registry snapshots replace the local heartbeat rows. Other nodes with complete native metadata receive distinct positive aliases, stable across reordering and never recycled during that connection. Every remote click rechecks the node epoch and room creation identity against a new directory response; a join hint grants no authentication. Missing, recreated and unavailable destinations are refused without falling back to a coincident local number. Failed refreshes clear visible aliases; a closed connection or plugin unload releases its state. Only the authenticated owning game connection may browse. Local clicks use ordinary password, capacity and phase checks with an additional creation-identity check. A remote click starts a logged-in migration only when the migration bundles below are mounted and the directory lists the build in both nodes' `native_migration_builds`; otherwise it is refused. Standalone profiles do not mount this row.

The optional `profiles/bundles/cluster-plaza-federation.patch.yaml` mounts `cluster.plaza-federation`, which shares the plaza between nodes: players of the same client build see each other in the plaza whichever node they logged in to. Set `plaza_federation: true` on the directory row and list the shared `builds` on each node, with the same `node_id`, `directory_url` and `access_key` as the heartbeat row. Only nodes with the same non-empty `data_scope` whose heartbeat proves the scope's database share a plaza; friendly servers with their own data never see each other. Each node posts its plaza entries, moves and leaves (`flush_interval_ms`, default 100) and long-polls the other nodes' changes in one request for all builds (`poll_wait_seconds`). A remote player takes a seat in the local plaza and is shown with the same seat and position packets as a local one, so no client difference exists. A node whose lease lapses takes its players with it; after a reconnect or a directory restart a node receives a snapshot and resends its own plaza. `max_queued_events`, `max_plaza_events` and `max_plaza_members` bound the queues; the client's plaza still holds 36 seats. Contracts 4.25.0 adds `IPlazaFederationEndpoint`.

The optional `profiles/bundles/cluster-migration.patch.yaml` holds the node-wide migration rows (broker, arrivals, destination) and `cluster-migration-build.patch.yaml` the per-build rows (browser, source, admission, and the `lobby`, `dialect.messages` and `ingame` switches). Mount them after the node, room-directory, account-store, account-sessions and write-fencing bundles, and set `migrations: true` and `native_migration_builds` at the directory. A destination endpoint's `listener_id` names the game listener row: `game-listener` in a single-build profile, `<build>.game-listener` in a multi-build one. Set `channel` on the source row and `channels` on the admission row to the build's own audited values; the bundle leaves them empty so an unaudited build fails to start instead of inheriting another build's set. A move reply is accepted only for channels the client admits: kr-812 admits 50, 60 and 80 unconditionally, cn-324 admits 50 and 60 and refuses 80. Each held account keeps one database connection open, so the database connection limit must cover every node's online players.

The build bundle also sets `protocol.config.accept_retained_moves: true`, so the moved client's `gcpReqMoveInto` may open the destination connection before the patch request; its handler must still authorize the continuation. Bootstrap lists name packets, never numbers: `pre_handshake_opcodes` and `alternate_handshake_sequence` take client-to-server packet names that each build resolves through its own opcode table, and a number, an unknown name, a server-to-client name or a relay-only name stops the server at startup. A build whose client sends an empty marker first on every auxiliary connection states it once in its profile as `auxiliary_prefix` (cn-279, cn-324 and cn-406 send `gcpStartGPK`). `answer_center_status: true` admits the native chooser's status query as that prefix followed by `gcpReqCenterUserCnt`; an explicit `alternate_handshake_sequence` still takes precedence. The destination endpoint advertises the profile's own build and its `GAME_PUBLIC_PORT` profile variable, read from `<BUILD>_PUBLIC_PORT`; a multi-build profile replaces the endpoint list with one entry per build.

For the shipped 13-build `all` profile, use `profiles/all/migration.patch.yaml` in place of the single-build node-wide migration patch. It names each `<build>.game-listener` and advertises that build's `<BUILD>_PUBLIC_PORT` (with its shipped port as the default). Append the operations from `cluster-migration-build.patch.yaml` to each selected build's optional `local.patch.yaml`, followed by explicit source `channel: 60` and admission `channels: [60]`. These operations use unprefixed row IDs; composition maps the source and admission to that build's own `inventoryview` subtree. Mount the node, room-directory, account-store, account-sessions and fencing rows once at the shared profile level. Set the reporter and room-directory build lists to the selected builds. At the authority, enable migrations and list those builds on each participating node, with matching data, join and room-directory scopes. Independent databases must keep distinct scopes and cannot acquire shared-account migration permission from discovery alone. The standalone `all` profile remains unchanged.

`home_unavailable_notice_enabled` defaults to `true` and requires a native notice sender. The migration bundle uses the build variable `MIGRATION_HOME_NOTICE` to disable this only for builds without an audited notice consumer (cn-406, cn-774 and tw-454). Refused home returns always log a warning and retain the player on the visiting node with account authority.

Retained-player admission is isolated by each build's `inventoryview` subtree, so its lobby and migration admission share only that build's login provider.

The `ingame` row may opt into destination seat preparation with `room_admission_reservations: true`, publishing one removable `IRoomAdmissionDirectory` per build (Contracts 4.9.0). `room_admission_lifetime_ms` defaults to 10000 and accepts 100–60000. Holds are bounded by the room seat count, use monotonic expiry, and retain the exact room creation and account. Ordinary joins and bots cannot take a held seat. Cancellation, expiry, host slot closure, room start/closure and unload release unused holds. A changed password is checked again. Consumption requires an independently authenticated owning game connection for that same account and build, with an unchanged menu/plaza scene, then uses ordinary room entry and peer announcements. A reservation is not authentication or account ownership; this does not yet connect remote browser clicks, authenticate a pending native socket or transfer source ownership. The option is disabled in standalone profiles.

`launcher.http` has an explicit `center: { enabled: false, mode: gateway }` configuration, disabled by default. Valid modes are `native` (the server owns Center selection) and `gateway` (the Launcher starts a local native-protocol Gateway). Both `GET /v1/catalog` and `GET /v1/servers/{build}` return the same top-level `center: {enabled,mode}` object. Missing Center metadata on an older catalog means disabled. The Launcher reads the catalog first; disabled or native mode preserves the catalog game endpoint and needs no directory request or Gateway. Only explicit enabled gateway mode reads the schema-1 directory and requires matching Center settings. It keeps one Launcher entry; automatic, manual and independent choices appear in the native game selector. Only a server-provided `automatic: true` row enables automatic placement, and its `targets` bound the candidate pool. Gateway directories without an automatic row are manual only and receive no selection grant. Disabled/native mode never issues a selection grant even if selection services are mounted; an explicit attempt returns HTTP 409 `server_selection_unavailable`. Selector identity/name and physical node identity/endpoint remain separate. These HTTP settings alone do not implement post-selection authentication or cross-node room admission. Center remains disabled when only mode is specified. With explicit enabled=true, an omitted mode defaults to gateway; native requires an explicit mode.

Manual selectors may have an empty `nodes` list to reserve a disabled choice without guessing an endpoint. Automatic selectors still require configured candidates. The Launcher API preserves empty entries; clients must keep them disabled and must not resolve or connect to an invented address.

Selectors accept an optional `short_name` of 1..128 UTF-16 code units with nonblank text, no control characters and only paired surrogates. The directory and launch grants expose it as `short_name`; omission retains the full `name` fallback. Grants retain both labels across health refreshes. Set a concise region label explicitly rather than deriving it from the full name; labels never change endpoint or authentication bindings.

`selectors[].nodes` controls visibility; a manual entry has zero or one node. `selectors[].automatic` returns only fresh, ready, non-draining candidates below capacity and requires one nonempty persistent `data_scope` across its members. `nodes[].data_scope` and `nodes[].join_scope` are opaque declarations; empty values grant no sharing or joining, and matching values do not implement either capability. `nodes[].room_directories` independently controls room-summary sharing, including friendly servers whose accounts remain isolated.

Automatic selectors also require `persistent_domain_id`, the authoritative database marker read with `SELECT REPLACE(domain_id, '-', '') FROM cluster_data_domain WHERE singleton_id = 1`. MySQL migration 0035 creates it once. Run migrations on the authority before starting workers with `auto_migrate: false`; point shared workers at the same authoritative database. Each heartbeat rereads the marker within its request deadline, and only matching candidates enter the automatic pool. A misconfigured worker cannot remove healthy matching candidates. Manual entries in that pinned data scope remain visible but offline when their reported marker differs. A restored database copy preserves the marker, so this check detects configuration mistakes; it does not authenticate a database or prove live replication. Independently operated copies need distinct markers before placement is enabled. Nodes without an identity provider can report only under an empty `data_scope` and cannot enter an automatic pool.

Registration creates a generation-fenced lease; ordered heartbeats refresh it. Expiry removes stale automatic candidates and rooms while retaining manual entries as offline. Readiness requires host readiness and a registered game listener. Reports include authenticated login counts and published gameplay-room summaries, excluding passwords and player identities. Remote rooms have `joinAllowed: false` until actual routing exists.

The directory bounds node/selector/room counts, body sizes, connections and deadlines. The directory's `lease_seconds` must exceed `heartbeat_seconds + request_timeout_seconds`; invalid combinations fail reporting. `draining` excludes automatic placements but retains manual status and room summaries. Discovery does not provide shared persistence, global session authority, remote joining, party federation or relay migration; verify those separately before admitting players to a multi-node persistent service.

The optional `persistence.mysql-account-ownership` row provides the account-occupancy primitive `IAccountOwnershipStore`. Mount `profiles/bundles/cluster-account-store.patch.yaml` only after migration 0036, set `BFF_CLUSTER_ACCOUNT_CONNECTION_STRING` and the authority marker, and budget `max_owners` against database memory. Each hold uses a dedicated unpooled connection and a non-waiting named lock. Verification checks both the original lock and the pinned marker; a lost hold never reacquires itself. Unload drains pending acquisitions and closes active holds. This primitive does not yet gate game logins or fence repository writes, so it does not make shared-node admission safe by itself. Standalone profiles do not mount it. Account acquisition returns an owned native connection handle; the remote SDK has no adapter for that result and marks acquisition unsupported in its schema.

The optional `cluster.account-sessions` row connects occupancy to game logins. Mount `profiles/bundles/cluster-account-sessions.patch.yaml` after the account-store patch and opt in each lobby with `require_account_ownership: true` and `duplicate_login: reject_new`. Missing admission services fail activation. A session exists only for an open game connection, so the listeners' connection limits bound their number; `verification_seconds` and `verification_concurrency` bound background checks. Lost authority closes the connection, and unload cancels pending admission, closes admitted connections and joins cleanup. The `IAccountSession` feature exposes verification without transferring the retained hold. This layer does not fence in-flight repository writes or character creation. Do not activate shared production databases until those mutation paths are fenced and drained. No shipped standalone profile mounts either patch.

Account handoff additionally requires migration 0037 and an explicit `node_id` on `persistence.mysql-account-ownership`, matching its directory identity. The default empty value preserves ordinary occupancy and does not publish `IAccountHandoffStore`. A configured source can record only its own handoffs; the database permits claims only by the recorded destination and withdrawals only by the recorded source. `cluster.account-sessions` publishes `IAccountSessionHandoff` only with that store. Remote SDK calls cannot transport native ownership handles, migration IP addresses or broker callback registrations; those methods are explicitly unsupported until adapters exist. This does not enable native migration in standalone profiles.

The optional `cluster.migration-broker` requires `cluster.node` with the same `node_id` and `directory_url`; `access_key` is its private node key. The directory must enable `migrations` (default false). Requests carry current lease credentials and snake_case JSON over a TLS-protected origin or secure tunnel. Passwords are omitted from diagnostics. Shared-account moves require matching join/data scopes, authentication authority and actual persistent-domain identity. Visitors require join trust and different data scopes. Transport alone does not enable native migration or reserve seats. A full inbox returns 503, a duplicate identity or stale lease 409, and an unanswered request 504.

| Row | Option | Default | Range |
|---|---|---|---|
| `cluster.directory` | `max_pending_migrations` | 256 | 1..4096 per destination |
| `cluster.directory` | `migration_request_timeout_ms` | 5000 | 500..30000 |
| `cluster.migration-broker` | `migration_request_timeout_ms` | 5000 | 500..30000 |
| `cluster.migration-broker` | `max_concurrent_requests` | 4 | 1..32 |
| `cluster.migration-broker` | `max_body_bytes` | 65536 | 1024..1048576 |
| `cluster.migration-broker` | `poll_wait_seconds` | 20 | 1..25 |
| `cluster.migration-broker` | `http_timeout_ms` | 35000 | 1000..90000 |
| `cluster.migration-broker` | `retry_delay_ms` | 1000 | 100..30000 |

The HTTP timeout must exceed both the polling wait and callback timeout. One owned task polls the destination inbox; outgoing requests are refused when their concurrency budget is exhausted.

The optional `cluster.migration-destination` row requires the broker, arrivals, heartbeat identity and account-session handoff. Set `node_id` to the heartbeat identity and `endpoints` to 1..64 distinct `{ client_build, listener_id, host, port }` entries. Each host must be a canonical DNS name; the destination resolves it for each request and requires an IPv4 result. Room requests also require the matching build's `IRoomAdmissionDirectory`. Missing endpoints, stale destination epochs and failed DNS resolution refuse preparation. Home requests reserve no room seat. This row does not authenticate native game connections or enable remote room clicks.

The required `listener_id` is the exact transport row id, including its profile prefix. Activation requires exactly one visible active listener with that id and the build's game protocol. Arrivals retain this destination id. Consumption uses the actual connection observed through `ConnectionAcceptedEvent`; an unobserved, closed, non-IP or wrong-listener connection cannot consume an arrival, even when its build, account number and address match. Rejected attempts leave the record available for the intended connection. This check does not authenticate an account; the native migration handler must still claim its handoff before loading player state.

`arrival_lifetime_ms` defaults to 10000 (100..60000), `cleanup_interval_ms` to 1000 (100..10000), and `operation_timeout_ms` to 5000 (100..30000). Configure the room's `room_admission_lifetime_ms` to cover the arrival window and preparation time. Mount `cluster.migration-arrivals` with `max_arrivals` (default 64, 1..4096): pending and expired records awaiting cleanup share that budget. Expired arrivals cannot be consumed. Cancellation failures are logged and retried without freeing their capacity; unload cancels remaining reservations within the operation deadline and reports failures. A consumer that successfully takes an arrival owns its reservation's subsequent join or cancellation.

`INodeLeaseSource` and `IMigrationArrivalCleanup` are shared Contracts 4.12.0 services, preserving interface identity across independent plugin loads. Lease records serialize through the SDK; their bearer token is private and their diagnostic text omits it. Arrival cleanup still requires a native adapter because its records contain IP addresses and completion retains object identity.

The optional `profiles/bundles/cluster-account-write-fencing.patch.yaml` requires the account-store and account-sessions patches, migration 0036, and ownership-enabled lobbies. It sets `core.packet-router.decoratable: true`, enables `persistence.mysql.require_account_write_authority`, and mounts `cluster.account-mutations`. Ordinary packet routers remain direct unless decoration is explicitly enabled. `IAccountMutationScopes` binds an immutable ownership handle to its original game session; packet dispatch selects that session, and per-player round settlement and reward-card grants select the recipient's original session. A delayed task cannot borrow a later session's authority. Fenced MySQL mutations lock and verify the authority row on the same connection and transaction before changing player state; handover waits for an already-admitted transaction. Missing or replaced authority fails without reacquisition. Character creation initializes a new account atomically; changing an existing account requires its hold. Contracts 4.4.0 exposes these native lifetime and transaction interfaces; the remote SDK explicitly rejects them. These controls do not configure shared storage, fence unrelated plugins automatically, or implement cross-node room joining. Audit every additional writer before shared production activation; standalone profiles do not mount this patch.

## Validation and safety

`nodes[].authentication` explicitly names `authority_id`, `api_url`, `mode` (`master-issued` or `own-authority`) and `policy_mode: authority`. One authority identity names one operator-configured HTTP or HTTPS origin. Its catalog and selected-build policy apply; there is no implicit inheritance or policy merging across independent authorities. Missing metadata grants no authentication trust. Automatic pools require identical authentication metadata across their members.

The optional `cluster-ticket-authority.patch.yaml` mounts `cluster.ticket-authority` beside the local Launcher ticket issuer. Each peer has a distinct redemption key and explicit build allowlist. The private `POST /v1/nodes/{node}/tickets/redeem` consumes `{ticket,build}` once and returns the account, Launcher language and launch identity. `cluster-ticket-client.patch.yaml` mounts a worker provider instead of a local Launcher ticket provider; the worker's game-login policy must require tickets. Configure `BFF_CLUSTER_TICKET_URL`, `BFF_CLUSTER_NODE_ID` and `BFF_CLUSTER_REDEMPTION_KEY`; select HTTP or HTTPS explicitly for the configured authority origin. Redemption uses bounded asynchronous requests without redirects, retries or account-name fallback. A timeout may follow a successful consumption: obtain a fresh launch ticket rather than replaying it. Independent authorities require their own authenticated Launcher session and launch ticket. This is not global session ownership or native selector authentication integration.

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

## Round cancellation and start rules

These `ingame` row options apply to every build. A dropped connection while a round is loading or playing cancels the round: no result is shown and every remaining player is sent to the plaza, the same way a waiting-room leaver gets there. `cancel_round_on_disconnect` (default `true`) switches this off, and then the dropped player simply leaves. A voluntary leave or `!plaza` normally lets the others play on. In enabled Zombie Escape, departure of the Lua master cancels the round without rewards. `load_timeout_ms` (default `60000`, `0` for no limit, at most `600000`) cancels a round in the same way when not every player has finished loading by then; keep it above the slowest client's map load and match setup.

In the zombie modes the zombies win only when every present player is infected; a player whose connection has already closed is not counted as a survivor.

Two start rules are off by default. `start_min_players` (0–32, seats and AI players together) refuses a master's start with fewer players. `start_requires_team_balance` refuses a master's start in a red-or-blue team mode when the teams differ by more than one player, after `assign_spawn_teams_on_start` has placed the players without a team; a player without a team otherwise counts as red. The client has no refusal reason for these rules, so the server only logs the refusal and the room keeps waiting. A native automatic start is not checked.

## Next

Continue with [deployment](deployment.md) to build and run a release, or read [plugins](plugins.md) to understand what the rows themselves do.

## Modern waiting-room kicking

`dialect.rooms.config.room_kicking` defaults to `false`. On kr-1256, cn-774 and tw-454, enabling it binds the audited nickname request and removal notification to shared room-master authority. Only the master of a waiting room can remove another present player; the notification precedes seat removal and the removed player returns to the plaza. The option requires `modern_rooms: true`, rejects `plaza_only`, and fails activation when either native packet name or layout is missing.

`dialect.rooms.config.npc_rounds` answers a modern round host's native wave manager, which the pirate battle waits on. The kr-1256 bundle enables it. In a pirate-battle room the host's wave start, wave end and spawn table are answered to every member, and the all-waves reply to the host alone; other modes ignore these requests. Spawn requests are validated but not passed on in ordinary rounds, because their transport is not yet established. The initial tutorial uses the same binding and still answers only its solo host. The option requires `modern_rooms: true` with round bindings.

For an isolated single-build trial, add `--patch profiles/bundles/modern-room-kicking.patch.yaml` alongside `--patch tools/local-checks/isolated-memory.patch.yaml`. On an `all` deployment, copy the patch's `set` operation into each selected build's ignored `local.patch.yaml` using the unprefixed `dialect.rooms` row ID. Keep the option off until real-client acceptance. This option does not change the existing legacy kick binding.

## Typed quick matching

For a build with the audited typed matching capability, `dialect.rooms.config.quick_matching` mounts the native request route. `quick_match_default_mode_id` selects the shared matcher mode (0–65535; default 0 refuses requests). Type 1 starts matching and type 2 refreshes it; their different native tails are fully validated. Party-join and listing-entry requests return the native refusal until those flows are implemented. The shipped modern profiles keep this option off pending native-client acceptance.

## Native chooser prelogin

The optional `dialect.messages.config.center_migration_prelogin` setting defaults to `false` and requires ordinary login. On kr-1256, configure `protocol.config.pre_handshake_opcodes: [gcpReqMoveInto]` together with this setting to accept a fresh native chooser connection before the patch request. The adapter validates the exact two-word body, zero retained identity and a stage flag of 0 or 1, rejects authenticated or repeated requests, and sends the native patch reply to resume ordinary login. The packet-scoped `IInitialHandshakeContinuation` expires when dispatch ends and takes effect only after a successful handler return. It does not mark the client version verified, redeem a ticket, attach a player or authorize migration. The lobby must still require a valid one-use Launcher ticket; an account name cannot replace it. Standalone profiles remain unchanged. Native client acceptance is separate from the server wire tests.

`dialect.messages.native_migration` defaults to `false`. It shares the native move-request router with `center_migration_prelogin`: zero identity remains restricted to the explicit prelogin option, while a nonzero identity is dispatched as `MoveIntoRequested`. This dispatch does not clear the initial handshake gate or grant account ownership. The option also supplies the table-validated move reply and promotion messages, including the acknowledgement/completion pair only when the opcode table declares it. A destination admission handler must validate the listener-bound arrival, claim account authority and install player state before requesting handshake continuation. The destination handler is provided by the optional `cluster.migration-admission` row described below; source orchestration is provided by `cluster.migration-source`; deployment wiring remains opt-in.


The optional `cluster.migration-admission` row coordinates shared-account room arrivals after destination preparation. Place it where the build's lobby and room services are visible. Enable `lobby.native_migration` (default false) and `dialect.messages.native_migration`; the lobby additionally requires account ownership, persistent retained-player lookup, stored inventory and wallet sources, and no creation preview. The separate coordinator avoids a dependency cycle between lobby and room services. Contracts 4.16.0 adds `IRetainedPlayerAdmission`; its installation result is native player state and has no remote SDK adapter.

Set `channels` explicitly to the native channel bytes proven for this build; an unlisted source channel is refused. For kr-812, only 50, 60 and 80 pass the unconditional branch of kr-812 sub_A4A440; the other named channels require client entitlements. Do not copy the modern login channel into a legacy deployment. `max_migrations` defaults to 64 (1..4096), `timeout_ms` to 10000 (100..60000), and `cleanup_interval_ms` to 1000 (100..10000). Pending promotions and failed cleanup share the capacity. Expired, disconnected or stale-lease promotions release reservations and account holds; failed cleanup retains capacity for retry and unload reports remaining failures.

Admission consumes a listener/address-bound arrival, checks the destination lease, rejects identities outside the player model's positive signed range, and claims the exact account handoff before reading retained identity or resolving inventory and wallet. It never creates an identity or reapplies daily login grants. The initial handshake continues only after player installation and successful promotion publication. Builds with promotion acknowledgement keep the player in the authenticated phase until that acknowledgement; then the coordinator completes promotion and joins through normal room admission. Other builds join after immediate promotion. Home arrivals use the optional main-menu admission described below; visitor arrivals remain refused. Visitor support and deployment wiring remain unfinished; shipped profiles do not mount this row and native-client acceptance is still required.

The optional `cluster.migration-source` row connects freshly revalidated remote room clicks to native departure. It requires the migration broker, node lease, account-session handoff, online-player directory and exactly one `ChannelMoveOffered` sender for `client_build`. Set `channel` explicitly (1..255); the destination must admit it. `max_migrations` defaults to 64 (1..4096), `operation_timeout_ms` and `migration_timeout_ms` to 10000 (100..60000), and `cleanup_interval_ms` to 1000 (100..10000). No shipped profile mounts this row.

Only the current owning game connection in the main menu or plaza can depart. The room flow holds its scene lock, and a mounted party directory must support atomic solo reservations: existing invitations are revoked and new invitations or joins involving the player are refused until cleanup. Party reservations use the configured pending-invitation capacity as their bound. The player enters the `Migrating` phase before preparation. The destination reserves the exact room instance before the source releases account authority; only then is the table-defined native offer sent. Timeouts, lost replies, send failures and stale connections cancel the exact destination request and withdraw only that migration's handoff. If the destination already claimed it, the source closes instead of reacquiring ordinary account ownership. Cleanup failures retain capacity and the solo guard for retry. Contracts 4.17.0 adds this source interface and the native offer; the owned party reservation has no remote SDK adapter.

Shared-account migration retains the first login node and native channel across further room visits (Contracts 4.18.0). The source also requires `IMigrationTargetDirectory` and, when `home_unavailable_notice_enabled` is true, exactly one native `ServerNotice` sender. The broker resolves a fresh home lease through an authenticated lookup that rechecks readiness, capacity, join scope, persistent domain and account issuer; lookup grants no arrival or account authority. A refused or unavailable home keeps the player on the current node and, when enabled, sends `home_unavailable_notice` (1..512 characters).

InGame `return_on_leave` is `home` by default, or `stay` to remain on the visited node. `return_delay_ms` defaults to 1500 (0..60000). Normal room removal and round-leave settlement finish first. The delay does not hold the scene lock; disconnects, connection replacement or any intervening scene generation cancel the old return. A subsequent plaza exit can retry. Set `home_migration_admission: true` on InGame at the home destination; it requires `room_admission_reservations` and a native main-menu sender. Home preparation reserves no room seat. After the same account claim and promotion sequence, the destination restores the source idle scene from retained equipment without offering the tutorial. A home request carries `home_scene` (`MainMenu` or `Plaza`); room requests always use `MainMenu`. Returning from a plaza uses the existing native plaza installation, avoiding an extra jump to the main menu. Builds without plaza encoders fall back to the main menu. Contracts 4.21.0 adds the scene to `MigrationRequest` and `IMigrationHomeAdmission.EnterAsync`. Where a party directory is mounted, destination admission holds an atomic solo reservation through promotion acknowledgement and scene installation; cleanup failures keep that guard until resources retire. Shipped profiles remain disabled pending deployment and native-client acceptance.

A party leader moves its party with it. When the leader departs for a remote room or returns home, every online member on the same build in the main menu or plaza is offered the same destination right after the leader's offer: both client families open the pending connection on any accepted offer without having asked for it. Members travel without the solo reservation; a home return takes only members whose own home is the leader's. A member in a room or a round, or one the destination refuses (for example a full room), stays on its node and keeps its local party; the leader's move is never undone. A non-leader member cannot depart on its own and does not start its own home return. Each request names the leader in `party_leader`, omitted from the node transport for solo moves; the destination rejoins each arrival to the arrived leader's party through `IPartyDirectory.RejoinAsync`, in either arrival order, and drops an unmatched arrival once its arrival window has passed. Contracts 4.23.0 adds both. Not yet confirmed in the native client.

The directory's `nodes[].native_migration_builds` defaults to an empty list. List only builds whose native migration source, listener-bound destination and promotion handlers are deployed and whose client paths have been verified. Entries must be unique and name configured endpoints. With directory `migrations: true`, both nodes must list the build before the directory advertises a shared-account remote room as joinable or accepts its migration request. The same check applies to home return. Join hints also require fresh source and destination leases, ready build reports, destination capacity, matching join and room-directory scopes, the same persistent database identity and exact account issuer. Listing grants neither a room reservation nor account ownership; preparation revalidates the route. Independent data domains remain discovery-only until visitor admission is implemented.

If a prepared room closes, starts or changes its password before the promoted player enters, admission cancels the seat and installs the retained main menu without releasing the destination account hold. `cluster.migration-admission` requires exactly one `IMigrationRoomRecovery` service for its build; InGame publishes it with `home_migration_admission: true`. Recovery waits until the original handshake dispatch has ended before applying `return_on_leave` and `return_delay_ms`. A changed scene cancels that return, and `stay` keeps the player on the destination. Recovery work shares `max_migrations` capacity and runs outside the admission lock. Failure to install the fallback menu still closes the connection and releases the claimed resources. Contracts 4.19.0 adds this recovery service; the native main-menu message remains unchanged.

The room service publishes `IRemoteRoomMigrationEndpoint` (Contracts 4.20.0), and `cluster.migration-source` binds to exactly one endpoint for its `client_build`. This permits room publication, node heartbeat and migration to start together: the room service no longer waits for a source that itself needs the heartbeat. Duplicate sources or missing matching room endpoints fail activation. Unloading a source rejects new remote joins and drains its in-flight room and home-return calls before releasing it; reloading the source or heartbeat preserves the room service. No additional profile row or default migration enablement is introduced.

The legacy room adapter separates connection readiness from automatic teaching. `dialect.rooms.config.tutorial_move_in` enables the native menu move-in reply; `automatic_tutorial` (default `true`) independently controls the following automatic tutorial notification. Set `automatic_tutorial: false` for test deployments that skip first-run teaching while retaining the readiness reply required by room actions. A per-login `OfferTutorial: false` also suppresses only the notification.

## Optional room object economy

Modern room dialects use the same opt-in object-economy bindings as legacy rooms: `supply_items`, `lucci_drops`, `star_items`, `horde_lottery`, `instant_revival`, `pve_points`, `rescue_missions` and `counted_pve_items`. Each binding still requires its own audited packet capability; unsupported options fail activation. The kr-1256, cn-774 and tw-454 profiles enable supplies, counted cooperative items and live PvE points. Mode 3 uses its own enabled item pool and two ordinary held slots. Mode 16 prepares three counted slots; their selected kinds and the other cooperative pickups applied immediately are configurable server policy, drawn from each build's own enabled ItemSpec data. Modern map reports use fourteen vectors and byte-sized teams. Their creation and drop bodies are 37 and 33 bytes, respectively; the KR slot-switch reply has three words, while CN and TW include a fourth result word. Native-client acceptance remains pending.


`dialect.rooms.config.weapon_objects` defaults to `false`. It mounts native dropped-weapon creation and pickup only when the build declares the audited `weapon_objects` capability (currently cn-324). The shared `ingame` row limits total creations per round with `max_weapon_objects_per_round` (default 256, range 1..65536), using identities after `weapon_object_id_base` (default `0x64000000`). Keep that interval separate from player and other round-object identities. Pickup requires the reporting player's own character and consumes an object once; round reset clears the state. Automatic checks do not establish native-client acceptance.

### Native displayed levels


Each client build declares `display_level_max` in `packets/builds.yaml`, using the frame count of its own `Images/Icons/LevelMark.smf`. Native rank projections clamp a positive saved level to that limit before writing player state, party membership, plaza/room seats, player information, waiting lists and results. Saved levels, experience and reward calculations keep their original values. For example, level194 displays as180 on cn-324 and cn-406, and as194 on kr-812. A new build must declare a positive limit supported by its own resources.

### Early temporary room inventory

`dialect.rooms.config.room_inventory_selection` defaults to false and requires the packet build capability of the same name. The CN2011/CN2012 profiles enable it and place the room row under their isolated `clientdata` row. It binds the early `gcpRoomChangeInventory` / `gspRoomChangeInventory` path independently of `weapon_presets`. CN2011 declares four selectors and CN2012 five through `room_inventory_selection_count`; `room_inventory_selection_fields` names the native character and weapon shorts in `UserSlot`. The binding requires that build's client data, verifies character and weapon-spec identities against `Config/FighterConfig.xml` and `Config/WeaponSpecDetail.xml`, rejects unknown selectors and only accepts the unaudited trailing selector's empty value. The requesting player can change only their own unready waiting-room seat. The selection survives native room snapshots, grants no owned items and is discarded on ordinary equipment selection or room departure. Native button visibility is separate: CN2012 initializes the old button hidden, so mounting this server path does not establish a usable native entry or real-client acceptance.

### Captain rounds

`dialect.rooms.config.captain_mode` defaults to `false` and requires an independently audited `captain_mode` build capability. The supported builds are kr-812, cn-717, id-400, id-1809, cn-404, cn-408, cn-706, kr-1256, cn-774 and tw-454, using their complete legacy or modern round adapter. Menu, browser, plaza and waiting-room-only adapters reject the option. Mode 20 appoints one random connected Captain per team after the round starts. Transformation, weapons and skills remain native client behavior. The Captain's own death report selects a living teammate; when nobody is available, appointment waits for the common respawn flow. Ordinary and Captain kill reports share one death credit. A Captain kill adds one ordinary team point, and the common time limit, team totals, draw, result and reward rules apply. A departing Captain is replaced when a living teammate is available. The option does not send `gspCaptainEvent`, authorize unaudited builds or establish native-client acceptance. With `ingame.config.captain_ultra_item_kind` (104 in the legacy bundle, 0 to disable), the current Captain's ULTRA request is confirmed to that Captain alone; the client then shows the ULTRA to the other players itself. Other players' requests and fallen Captains are not answered, and no appointment is repeated.

### Rhea character skill

`dialect.rooms.config.rhea_character_skill` defaults to `false`. The matching `rhea-character-skill.patch.yaml` enables the audited exchange for kr-1256, cn-774 or tw-454. It requires complete modern round bindings and that build's native capability. Only the player currently wearing character item 300029 may request a form change for their own actor in the current round and life. Accepted changes reach the owner and peers; duplicate activation is ignored, while an already inactive end is acknowledged only to its owner. Death, respawn and conversion clear the stored form. Charge and expiry remain native client behavior; this option does not add server timing enforcement or establish native-client acceptance.

### Bomb matches

`dialect.rooms.config.bomb_mission` enables the audited native bomb operation and result bindings. `ingame.config.bomb_round_series` additionally enables a native round series, alternating attack roles and keeping round wins separate from kill totals. Legacy formats use two wins in at most three rounds; kr-1256, cn-774 and tw-454 use three wins in at most five rounds. Both options default to `false`; the complete series supports kr-812, cn-717, id-400, id-1809, cn-404, cn-408, cn-706, kr-1256, cn-774 and tw-454 and requires shared results and an audited series-entry sender. Apply the matching `profiles/<build>/bomb.patch.yaml` to enable both and exclude mode 19 from ordinary respawning. Observation support is required for that exclusion. Modern opt-in patches also include modes 19 and 20 in the scoreboard selection. CN2013 cn-406 remains excluded because its own mode factory has neither specialized branch; this does not classify other CN2013 revisions.

`dialect.rooms.config.extreme_racing` defaults to `false` and requires the audited `extreme_racing` build capability, currently kr-1256 only, with `modern_rooms` and its round bindings; cn-774 and tw-454 have audited timer, round, record, goal, coin and checkpoint table rows, but their racing capability remains disabled and refuses the option pending a complete round audit. Apply `profiles/kr-1256/extreme-racing.patch.yaml` to enable it. Mode 24 then plays `ingame.config.extreme_racing_rounds` race rounds (1 to 5, default 3). Each round starts the race clock (`extreme_racing_round_ms`, default 180000). The first goal starts the finish countdown (`extreme_racing_finish_countdown_ms`, default 10000), and the round closes when the countdown or the race clock runs out, or when every connected player has finished. Finishers score `extreme_racing_place_points` by place (default 10, 8, 6, 5, 4, 3, 2, 1), the race boards show round and total points, and a pause of `extreme_racing_intermission_ms` (default 5000, more than 1000) separates rounds. After the last round the shared result runs once: the team with more points wins, or the players with the most points when the room does not have two teams. The common round time limit does not end a race. Checkpoint touches are relayed so every player's progress mark moves; the client places its own markers and respawn points. Map objects that need a server object list (cannons, coins and stones) are not created. Round counts, clocks and points are server policy, and native-client acceptance is pending.

The server validates planting, cancellation and defusing against the exact operation owner, team and active round. The native master reports detonation and objective results; the armed bomb retains its own countdown when the ordinary round time expires. Each subround uses shared loading, participant life and cancellation handling. Rewards settle once after the match, retaining the same match identity. A whole team's voluntary departure forfeits the match without inventing unplayed round wins; the existing disconnect-cancellation policy remains in effect. This option does not enable unaudited builds or establish native-client acceptance.

### FinalZone

`dialect.rooms.config.final_zone` defaults to `false`. Apply `profiles/kr-1256/final-zone.patch.yaml` to enable the audited Area32 exchange for modes 23, 25, 30 and 45. It requires modern round bindings, the `final_zone` build capability and scoreboard mode 30; only kr-1256 is audited. The patch also retains scoreboard modes 1, 19, 20 and 24, so it can be applied after the other mode patches. It does not enable those other modes itself.

Import the matching client's `Config/GameSpec/GenItemSetting.yaml`, `Config/GenDroneSpec.csv`, `Config/SurvivalMode.xml`, `ServerShared/MapMeta.xml` and each selected map's `GenClusterTable.csv`, `GenSpawnTable.csv`, `GenSpotList.csv` through `client-data`. Import `GenDroneRewardTable.csv` for maps that use drone or golden supplies. The apartment maps disable drones and do not have this table. Missing or malformed resources fail before the countdown. The current explicit-item path supports ordinary generated items, including the `rel_Sudden08_Finalzone` map. Maps using the distinct `AutoItem` terrain class, including `rel_space04_64room`, are rejected. Equipped optional starting packs are also rejected before countdown until their native consumption and grant exchange is integrated.

The server publishes explicit ground items after round start, grants each object to one claimant, validates live returns and one terminal backpack against issued quantities, and uses the native user number when transferring backpack contents. Flood phases follow the map resource timings. Drones use bounded room timers; their boxes and the master's deduplicated golden-supply requests share native reward seeds. Trap placement consumes one issued rod per endpoint; only the owner's accepted pair can be linked. Required acknowledgements include the sender. Eliminated players cannot respawn, survivor counts update once, and a contested round settles once when one player remains, or one team remains in mode 25. Mode 45 uses the shared ground-item and elimination flow. Its separate zombie mechanics are not implemented here. Ordinary score/time limits do not finish FinalZone. Cancellation, room disposal and final results stop its clocks.

Resource and object bounds are `ingame.config` fields:

| Key | Default | Bound |
|---|---:|---|
| `final_zone_first_object_id` | 65536 | First reserved world handle; the range must stay below participant handles. |
| `final_zone_maximum_objects` | 16384 | Total nonreusable handles per round, shared by items, backpacks, drones, supplies and rods. |
| `final_zone_maximum_quantity` | 100000 | Maximum issued or retained amount per item specification. |
| `final_zone_maximum_drones` | 1 | Simultaneously active drones. |
| `final_zone_maximum_golden_supplies` | 3 | Distinct golden-supply requests per round; zero disables grants. |
| `final_zone_maximum_file_bytes` | 4194304 | Bytes per imported resource. |
| `final_zone_maximum_rows` | 16384 | Rows per map table. |
| `final_zone_maximum_item_specs` | 4096 | Item specifications and starting-pack identities. |
| `final_zone_maximum_alternatives` | 256 | Candidates in a spawn field. |
| `final_zone_maximum_bonus_items` | 64 | Accompanying objects per spawn choice. |

This remains a guarded increment. The ledger bounds issued quantities but does not reproduce all native inventory slots, weapon variants or live ammunition consumption. Alliance and assassination behavior for mode 30 has not been established. Automated checks and resource imports do not establish native-client acceptance or complete map-mechanic coverage.

## Native plaza recruitment

Native plaza recruitment is enabled in the supported thirteen profiles. For a custom profile, set `plaza_recruitment: true` on both `dialect.rooms` and `ingame`, and mount the matching `party` row. Both options default to `false`; missing native bindings, party membership or plaza support fail activation. `ingame.plaza_recruitment_text_length` defaults to 128 UTF-16 characters and accepts 1–1024. Publishing and editing share the chat rate window; cancellation remains available.

Recruitment belongs to the current plaza leader. Native sign flags, message bubbles, party membership and scene following are updated together; cancellation, departure, a new scene and loss of leadership clear the old sign. KR1256 and CN2019 also expose native recruitment documents in pages of ten, using each leader's selected category and capacity. These documents admit local leaders only; sharing a remote player's plaza appearance does not grant cross-node party admission. Native entry positions remain the client's responsibility, and only actual movement reports are replayed.

## Optional equipment defence tables

Equipment-derived ordinary defence is opt-in. On `kr-812`, `cn-324`, `cn-406`, `cn-404`, `cn-408`, `cn-705`, `cn-706`, `id-400`, `id-1809`, `cn-717`, `cn-774`, `tw-454` or `kr-1256`, add `--patch profiles/bundles/defence-seeds.patch.yaml`. This also enables the native TCP relay needed to deliver the tables, including its otherwise dormant rows on `cn-705` and `cn-706`; it does not require UDP. For `all`, put the patch operations in each chosen build's `local.patch.yaml`; shared patches cannot address build rows appended later. `cn-705` is available as a separate profile and is not included in `all`. Builds without the audited capability or authority row reject this patch.

CN2011 (`cn-279`) calculates ordinary equipment defence locally in its native damage path and does not use this seed-table patch.

The authority reads the selected build's `Config/ShopItem2.xml` and `defence_seed` rules. At round start it freezes worn item IDs and gives every recipient the same 1000-byte table for each victim. The native client still applies damage reduction. Critical and cancellation tables remain separate; enabling defence on `cn-705` or `cn-706` does not enable their unaudited critical exchange. With the option off, the existing zero-defence exchange remains unchanged; defence-only rows stay disabled.

The table approximates the local integer-roll distribution with balanced frequencies; it does not reproduce the client's random sequence. The projection covers catalog min/max and the configured RC/KR/BB set effects, including the selected endpoint precision. Both `cn-774` and `tw-454` use their own RC/KR/BB rules and single-precision endpoints. Instance attributes, transformation-specific attributes and independent special modifiers are not projected. CN2019 AI actors use a different seed encoding and are outside this player projection. Native-client acceptance is required before enabling it on a live deployment. `defence_items_client_path` selects the workbook; `defence_workbook_max_bytes` bounds it (default 268435456 bytes).

## Modern zombie mode

For `kr-1256`, `cn-774` and `tw-454`, apply `profiles/<build>/script-zombies.patch.yaml` to enable `script_zombies` on both the `dialect.rooms` and `ingame` rows. Both options are off by default pending native-client acceptance. The patch connects ordinary mode 8: the native master script chooses multiple small zombies with its equipment weights, the server bounds the initial count to one zombie per four active players rounded up, and native owned confirmations apply team 34. Humans start on team 1. Later infection and zombie revival require the owning participant's life or respawn evidence; an ordinary death report does not end the round. All confirmed infections award the zombies the win, while surviving humans win on the server deadline.

`ingame.script_zombie_round_time_limit_ms` defaults to `180000` and accepts `1..3600000`. Native life and respawn must be enabled for mode 8, and `dialect.rooms.result_scoreboard_modes` must include `8`. The two `script_zombies` options must agree. The optional patch composes with the audited Bomb, Captain, Extreme Racing and FinalZone patches and retains their scoreboards. Other modes retain the build's declared peer-feed policy. These three builds disable pass-through, so the shared conversion operation is not forwarded outside the enabled mode. The native script master's departure cancels this round without awards because its selection state cannot be transferred to another client.

This is a single-round integration. The script's ten-round request is not yet a server match series. Ghost mode 28, origin mode 43 and zombie survival mode 44 have different native rules and are not enabled by this option. Native-client acceptance remains outstanding.

## Ghost zombies

For `kr-1256` or `cn-774`, apply `profiles/<build>/ghost-zombies.patch.yaml` to enable `ghost_zombies` on both the room dialect and in-game rows. Mode 28 uses the same conversion binding as ordinary mode 8 and escape mode 32. The native master chooses one king (201 or 203), with weapon 110290; the script's repeated zero-weapon selection does not grant another conversion. The king's confirmed death cleanup awards surviving humans the win, and a king or master departure cancels without rewards. Other humans become small zombies through their own death/respawn path.

Ordinary and ghost zombies also relay the current owner's native ULTRA activation and timer restoration without changing shared HP or life generation. The native gauge remains responsible for charging. These options do not implement the script's ten-round series. TW454 has no audited complete ghost script path, so its ghost option is rejected. The patch preserves other enabled scoreboards; native-client acceptance remains pending.

## Zombie Escape

`dialect.rooms.config.zombie_escape` / `ingame.config.zombie_escape` defaults to `false`. For kr-1256, cn-774 or tw-454, apply `profiles/bundles/zombie-escape.patch.yaml` to a single-build profile; with `all`, put the operation in each selected build's ignored `local.patch.yaml`. Activation requires modern round bindings, the build's audited `zombie_escape` capability and scoreboard mode 32. The patch preserves scoreboard modes 1, 8, 19, 20, 23, 24, 25, 28, 30, 32 and 45 without enabling their separate mode options. Use the matching client's Zombie Escape maps and scripts. Native-client acceptance is still pending.

The room dialect and in-game rows must enable the same zombie modes. Ordinary zombies and Zombie Escape share one typed conversion binding; enabling both does not register the native operation twice. The optional patches preserve all supported survival scoreboards. Returning to the waiting room restores the teams selected before the round.

The client's master script owns the initial zombie draw, separate zombie spawn positions, ready gate, checkpoint buttons, timed doors, infection gauge and escape condition. The server accepts the master's bounded initial selection, permits each player's own later infection or respawn conversion, echoes approved peer conversions to every participant, and uses the shared life, result and settlement flows. The initial selection limit is one zombie per five active players, rounded up. The script can infect a living player through its gauge; button scans do not establish a human-only restriction.

The master reports human escape through the native stage outcome. Once no connected human remains, or the server deadline expires, the zombies win. `ingame.config.zombie_escape_round_time_limit_ms` defaults to 600000 and accepts 1..3600000. Ordinary death-stage reports do not end the mode. Mid-round joins are refused. Departure of the Lua master cancels the round without rewards because its private timer and checkpoint state cannot be replayed; other disconnects follow the configured cancellation policy.

This option settles one stage through the shared result flow. The native script writes a game-count value of three, but a three-stage series has not been established and is not implemented here. Automated tests do not establish complete native map or UI behavior.

## Equipment selections by client build

The `inventory.loadouts` row gives each build its own worn character, clothing, weapons, weapon preset pages and active preset slot. `build_key` is the stable profile identity, such as `kr-1256` or `cn-279`; use the same key on nodes serving the same build. Account inventory ownership, quantities, lifetimes, wallet and progression remain shared. A client sees only inventory identities present in its own `ShopItem2.xml`, with characters also checked against `FighterConfig.xml`. Unsupported items stay owned and become visible again in a compatible build.

On first use, the row imports compatible legacy selections and repairs required equipment positions through the existing selection rules. Later changes affect only that build. Preset initialization preserves previously materialized pages. The row belongs below `clientdata`, isolates `IInventoryRepository` and `IPlayerInventoryState`, and parents the build's equipment consumers; it consumes the shared provider's `IInventoryLoadoutStore`. Custom profiles must keep the lobby, inventory view, equipment, shop, room dialect and gameplay consumers in this subtree.

Memory, MySQL and SQLite providers expose `max_loadouts_per_player` (default `32`, range `1..256`). Exhaustion rejects a new build selection without evicting existing ones. MySQL migration `0038` adds `player_loadouts`; SQLite schema `8` adds loadout snapshots while retaining older account state. Back up durable storage before upgrading. Rolling back MySQL code leaves the additive table intact; older SQLite readers refuse schema `8`, so a rollback needs a compatible reader. Do not restore an old save over newer player progress.
