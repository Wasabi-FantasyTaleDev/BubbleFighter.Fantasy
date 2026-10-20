---
title: "Launcher"
description: "The companion launcher for Bubble Fighter Fantasy: client builds, sessions, login tickets and the server launcher API."
updated: 2026-10-18
---

Bubble Fighter Fantasy has a companion desktop application, **Fantasy Tale Launcher**, being developed
to install and start game clients and connect them to the server the player chooses. It is a separate
project: [FantasyTale.Launcher](https://github.com/Wasabi-FantasyTaleDev/FantasyTale.Launcher),
distributed under the MIT license. This server repository does not include it, and neither project
distributes the game client or its data.

This page documents the launcher at its current development checkpoint and the server-side launcher
API that operators can already enable. The launcher is in **early development**: most player-facing
flows exist as definitions and contracts, not as working screens yet. Every section below states
what is implemented and what is planned, so that neither players nor operators rely on behaviour
that does not exist at this commit.

## Current development status

| Area | State at this checkpoint |
| --- | --- |
| Window, game catalog and settings shell | Implemented: the host loads the game and client-build definitions from its `games` folder and sends them to the interface; the shell renders the game, instance, server, download, plugin, account and settings pages, and stores appearance and language preferences locally. |
| Bubble Fighter build definitions | Implemented as data: nine client builds are defined, each in its own build file. |
| Session file (`session.toml`) | The writer is implemented as a library helper; no production launch flow calls it yet. |
| Native hook runtime and messenger host | Implemented and covered by native tests, but **not yet verified in a real game client**. |
| Three built-in hook plugins | Defined as data plugins and covered by the same native tests; also not yet verified in a real game client. |
| Backend downloads, sources, plugin manager, instances | Not implemented: the download engine, manifest apply, plugin manager and instance model are still open roadmap work. The source resolver for one download-source kind exists. |
| Player-facing lists and actions | The Instances, Servers, Downloads, Plugins and Accounts pages currently render placeholder data; the interface is explicitly a preview build until the host bridge methods land. There is no working "create instance", "add server", "sign in" or "launch" action yet. |
| Server launcher API (in this repository) | Implemented and enableable by operators: accounts, sessions, tickets, catalog, client presets, version gate and plugin recommendations. |

Do not expect the current launcher checkout to install or start a game client yet. The sections
below are written against the source so that the guide stays correct once the remaining flows land.

## Getting the launcher

The launcher is its own project with its own repository and release process:

- Repository: [FantasyTale.Launcher](https://github.com/Wasabi-FantasyTaleDev/FantasyTale.Launcher).
- License: MIT.
- Status: early development, as stated in its README.

The repository describes building from source (a .NET solution build, a Visual Studio build of the
x86 native hook host, and an npm build of the frontend). It does not describe a published installer,
release asset or download page, so this guide does not provide one. Wait for a launcher release
instead of treating a developer source build as a player installation.

## Client builds

A client build is identified as `<region>-<wire version>`, where the wire version is the client's
`ClientVersion_Major`. It is **not** the release year and not the executable's file version. The
server profiles and client records currently documented here are:

| Build | Branch | Display name | Client date | Client version | Status |
| --- | --- | --- | --- | --- | --- |
| `kr-1256` | Korea | KR 2026 | 2026-05-08 | 1256 | Experimental |
| `kr-812` | Korea | KR 2015 | 2015-04-22 | 812 | Supported |
| `cn-279` | China | CN 2011 | 2011-04-08 | 279 | Supported |
| `cn-324` | China | CN 2012 | 2012-09-23 | 324 | Supported |
| `cn-406` | China | CN 2013 | 2013-06-28 | 406 | Early development |
| `id-400` | Indonesia | ID 2014 | 2014-01-23 | 400 | Supported |
| `cn-706` | China | CN 2014 | 2014-09-23 | 706 | Supported |
| `id-1809` | Indonesia | ID 2015 | 2015-01-29 | 1809 | Supported |
| `cn-717` | China | CN 2015 | 2015-06-18 | 717 | Supported |
| `cn-735` | China | CN 2016 | 2016-06-20 | 735 | Preparing |
| `tw-454` | Taiwan | TW 2019 | 2018-12-11 | 454 | Experimental |
| `cn-774` | China | CN 2019 | 2019-05-30 | 774 | Experimental |
The executable is `CrazyShooter.exe` for the Indonesian builds (`id-400` and `id-1809`); all other listed builds use `BubbleFighter.exe`.

The launcher currently ships definitions for the builds it can start; the additional server
profiles above are documented for compatibility planning and are not automatically launchable until
their launcher definition is added and verified.

### Messenger and login mode

Each build also declares how the client's messenger calls are answered and how it logs in:

| Build | Messenger mode | Login mode | Default port |
| --- | --- | --- | --- |
| `kr-1256` | forward | hook | 7100 |
| `kr-812` | stand-in | nmco | 7110 |
| `cn-279` | forward | gamewidget | 7210 |
| `cn-324` | forward | gamewidget | 7140 |
| `cn-706` | forward | unverified | — |
| `cn-717` | forward | unverified | — |
| `cn-735` | forward | unverified | — |
| `id-400` | stand-in | nmco | 7120 |
| `id-1809` | stand-in | nmco | 7130 |

Notes on the tables:

- The branch column is the region group from the game definition. The messenger table below only
  covers builds with launcher definitions; the additional profiles in the client table are not yet
  launchable through the launcher.
- "Messenger mode" and "login mode" are definition fields. **Stand-in** means the launcher's
  messenger host answers the client's messenger calls itself; **forward** means it passes them to the
  original library, which the installation keeps under a renamed file. See
  [Login and authentication](#login-and-authentication).
- The three builds marked `unverified` have no verified login provider or launch arguments yet; the
  launcher module refuses to build a launch plan for them and reports that the build has no verified
  launch arguments. They are listed by the definitions but cannot be started at this checkpoint.
- A build without a default port needs a selected server endpoint that supplies one.
- A build's display name and year are presentation data. They do not participate in identity: the id
  (`<region>-<wire version>`) is what the server profiles and packet tables use.
- The launcher currently sends the UI each build's id, branch, year and localized name. Executable,
  messenger mode, login mode, fingerprint and default port are definition data used by the launch
  and session contracts, not data the player edits.

## Planned player flow

The launcher's design describes the following flow. Every step is listed here with its status at
this checkpoint, because the screens are not wired to real data yet.

| Step | What the repository defines | Status |
| --- | --- | --- |
| 1. Start the launcher | A native window with the game catalog loaded from the `games` folder. | Implemented |
| 2. Select Bubble Fighter | Bubble Fighter is the only enabled game; other game entries would be placeholders that cannot be selected. | Implemented |
| 3. Create a client instance | An instance is one installed client copy with its own folder, server binding, plugin set and options, so several copies of the same build can coexist. | Planned; the Instances page currently uses placeholder rows |
| 4. Choose the build | The build is a property of the instance. Each build definition supplies the executable, launch arguments, messenger mode and default plugins. | Planned; definitions exist, no build chooser yet |
| 5. Add a server | A server is added by its launcher API base URL; a server without the API is intended to be addable manually by address, with no verified login and no automatic install. | Planned; the Servers page currently uses placeholder rows |
| 6. Sign in to the server | Server-side account registration, login, refresh and logout already exist (see [Server launcher API](#server-launcher-api)). | Server side implemented; launcher client side planned |
| 7. Install or update the client | The server is designed to publish a per-build distribution manifest with file sizes and hashes; the launcher verifies every file and applies updates per file. | Planned; no download engine or update check yet |
| 8. Launch | The launcher writes `session.toml` into the client folder, starts the executable with the instance folder as the working directory, and deletes the session file when the client exits. | Contract and writer implemented; the UI has no launch action yet |

Until these flows land, the launcher is useful for developing and testing the definitions, the
session contract, the native hook host and the server API — not as a player installation.

## Instances and launching

The design and the implemented code define the shape of a launch even though the UI cannot start one
yet:

- An **instance** is one client copy in its own folder. It carries an `instance.toml` with the build,
  the server binding, the enabled plugins and options; an instance may be duplicated so the same
  build can serve different servers or plugin sets.
- The executable named by the build definition must resolve inside the instance folder; a path that
  escapes the folder is refused.
- Launch arguments come from the build definition as a template with `{account}`, `{host}` and
  `{port}` placeholders. A placeholder with no value fails the launch plan instead of starting the
  client with a broken argument.
- The launcher's launch plan uses the server endpoint when one is selected, and otherwise the
  build's default port. Builds without a default port need a server endpoint that supplies one.
- Immediately before each start the launcher writes `session.toml` into the client folder as a
  temporary file and atomically replaces the previous one. The hook host reads it once at client
  load and applies no plugins if it is missing.

The session file is covered in [Session file and local data](#session-file-and-local-data).

## Login and authentication

Login has two independent layers, and they must not be confused:

1. **The launcher account** lives on the server and is verified by the server's launcher API. It
   provides an access token, a refresh credential and a profile summary. This is what an operator's
   server controls.
2. **The game login identity** is what the game client sends in its login request. On stand-in
   messenger builds, the launcher's messenger host answers the client's passport query and supplies
   that identity.

### How a client receives its identity

The messenger host resolves the passport in this order (first non-empty wins):

1. the launch ticket from `session.toml`,
2. the account typed into the client's own login dialog,
3. the launcher account from `session.toml`,
4. an account supplied through the process environment by older launcher scripts,
5. `guest`.

The client sends that passport as its login id. When the client uses its own login dialog, the
stand-in records the typed id and returns it, so the game logs in with the player's own input.

### Launcher-ticket login and in-game login

The two cases the guide distinguishes:

- **Launcher-ticket login.** The player signs in to the launcher (server API), the launcher asks the
  API for a one-use launch ticket for the selected build, and writes it to `session.toml`. The
  messenger stand-in returns the ticket as the passport, and the game server redeems it at login
  instead of treating it as an account name. This path applies to the builds that run the
  **stand-in** messenger mode and are started with an account argument: `kr-812`, `id-400` and
  `id-1809` at this checkpoint.
- **In-game login.** When no launcher account or ticket is supplied, the client can use its own login
  flow. On a stand-in build the stand-in accepts the id typed into the client's login screen (the
  client's dialog variant of the login call) and returns it as the passport, so the server sees an
  ordinary account name. On a forward build the launcher's stand-in is not involved at all, because
  the original library answers the client; the build definition's `gamewidget` (`cn-279`, `cn-324`)
  or `hook` (`kr-1256`) login modes describe that case, but the launcher does not implement those
  providers at this checkpoint.

The `login` value in a build definition is data: the launcher module currently enforces only
`unverified` (it refuses those builds) and does not otherwise branch on the provider. The mapping
above follows the declared messenger mode and the host code; it has not been observed with a real
client at this checkpoint.

### Where the ticket is redeemed

Each lobby row's `authentication` option decides how a game login proves its account. The shipped
profiles feed it from `BFF_LOGIN_AUTHENTICATION` (`${BFF_LOGIN_AUTHENTICATION:-ticket}`), so one
variable sets every build.

| `authentication` | Who can log in | Needs |
|---|---|---|
| `ticket` (default) | Only a login that redeems a launch ticket issued for that build. Any other account string, including an empty one and a name typed into an in-game login box, closes the connection with `launcher ticket required`; a spent, unknown or wrong-build ticket closes it with `invalid launcher ticket`. | An active `launcher.http` row. Without it the lobby fails to start with `authentication: ticket requires an active launcher.http row, which publishes ILauncherTicketService.` |
| `open` | Any account name the client sends; an unknown name creates a player when `auto_create_players` is on. Every start logs a warning. | Nothing. Use it only on a trusted LAN or for local testing. |

In `open` mode the older `launcher_tickets` switch (`BFF_LAUNCHER_TICKETS`) still lets ticket-shaped
logins be redeemed next to plain names; in `ticket` mode redemption is always on.

At this checkpoint the launcher delivers tickets only to the **stand-in** builds (`kr-812`, `id-400`,
`id-1809`). The **forward** builds receive no ticket from the launcher yet, so their players cannot
log in under `ticket`; run those builds with `open` on a trusted network until the launcher supplies
their tickets.

### What a ticket does and does not prove

A valid launch ticket proves that this server issued it to a password/refresh-authenticated launcher
account for that exact build, that it has not been used, and that it is still within its short
lifetime. It is single-use, bound to the build, kept only in the server process, and it expires no
later than the access token that authorized it.

It does **not** prove that the client binary is unmodified, that the launcher version was acceptable
at game-login time, that recommended plugins are installed, or that the connection used TLS. The
per-instance `launcher_login` policy (`required`, `optional`, `none`) is advertised in the server
catalog, but the game server does not enforce it; enforcement is the lobby's `authentication`
option. Under `ticket` a direct game login without a launcher account is refused; under `open` the
game login is not protected at all.

## Session file and local data

The launcher and the server keep different data, and only one launcher file is implemented at this
checkpoint.

### `session.toml` (per launch)

- Location: in the client folder, next to the game's messenger host library (`nmcogame.dll` for
  Bubble Fighter). It is written immediately before each start.
- Schema: version 1. It carries the game and build id, branch, instance id, messenger mode, the hook
  log path, an optional build fingerprint, an optional `[login]` section with the account and
  ticket, an optional `[endpoint]` with the server host and port, and the ordered `[[plugins]]`
  entries with their options.
- The hook runtime refuses plugins on a build-fingerprint mismatch and treats unknown keys as errors.
  These checks validate the session format and compatibility; they do not prevent a local user from
  changing or removing the file or omitting plugin entries.
- The file is a secret for its lifetime: it can carry the account name and the launch ticket. The
  runtime never logs those values, and the launcher deletes the file when the client exits. Do not
  copy it, edit it, share it or package it with a client.
- If the file is missing, the client still starts: it loads no plugins and chooses the messenger mode
  from the presence of the renamed original library.
- The hook runtime writes its log to the path from the session, or to its default under
  `%LOCALAPPDATA%\FantasyTale\BubbleFighter\logs\fthook.log` when the session does not set one. The
  log is bounded: the file is truncated when opened, its size cap is clamped between 16 KiB and
  64 MiB (the session default is 1024 KiB), long lines are cut, and a final marker line is written
  when the cap is reached. It records one result line per plugin and never the account or ticket.

Deletion when the client exits is part of the session writer's contract. The production launch flow
that calls the writer and the cleanup is still open roadmap work, so treat this as the designed
lifecycle rather than an observed one.

### Launcher preferences and definitions

- The launcher reads its game and client-build definitions from the `games` folder next to its
  executable at startup.
- The interface ships in six languages and stores the selected theme, accent source and language as
  local preferences of the launcher window. These are presentation preferences, not accounts.
- The launcher currently has no log of its own; the only log written is the native hook log inside a
  client, described above.
- No instance, server, account or download file format is implemented yet; those belong to the same
  open roadmap work as the corresponding screens. This guide does not document file paths for them.

### Server-side launcher data

- Launcher accounts are stored in the server's accounts store, with PBKDF2-SHA256 password hashes;
  the launcher never verifies passwords itself.
- A durable sign-in stores only the SHA-256 hash of the current refresh credential, so a session can
  be renewed without keeping the password and survives a server restart through the persistence
  provider. Access tokens and launch tickets are in-memory only and are dropped by a restart.
- The default access-token lifetime is 1800 seconds, the refresh session 30 days and a launch ticket
  60 seconds; operators can tune these within the validated ranges.

## Hook plugins

Bubble Fighter client patches are delivered as hook plugins. A plugin is a folder with a
`plugin.toml` that declares its identity, kind, compatibility and options; no compiler is needed for
a data patch. The launcher currently ships three data plugins:

| Plugin | Purpose | Builds it applies to | Mandatory |
| --- | --- | --- | --- |
| `bubblefighter.fantasy.font-weight` | Applies the Chinese build's font weight to every language group, so Chinese text on other regional clients renders like the Chinese build. | `kr-812`, `cn-706`, `cn-717`, `id-400`, `id-1809` | No |
| `bubblefighter.fantasy.no-file-verify` | Makes the client's config-table integrity comparer report a match so modified configuration tables load; the pack's per-member hashes still apply, so edited tables must still be repacked correctly. | `kr-1256`, and `kr-710` which has no launcher build definition yet | No |
| `bubblefighter.fantasy.kr1256-shop-gates` | Re-enables the taskbar shop button and the shared feature entry that the final Korean client disables. | `kr-1256` | Yes, `required_on = kr-1256` |

How hooks are meant to work:

- A plugin declares build compatibility as build ids, branch selectors or all builds, and may exclude
  builds. A plugin that does not list the current build is skipped as unavailable, and the runtime
  refuses it independently of the launcher.
- A plugin marked required cannot be disabled for that build; a failed required plugin produces one
  warning message box in the client ("Required client plugins could not be applied; the game may not
  work correctly.") and the hook log records a result line per plugin.
- Plugins are enabled per instance and written to `session.toml` as `[[plugins]]` entries with their
  options. A new instance starts with the build's default plugin list, which is empty in all nine
  definitions at this checkpoint.
- The plugin manager that installs, toggles and configures plugins in the launcher is not
  implemented yet; the Plugins page is still a preview. Installing a plugin by hand-editing the
  client or its executable is not a supported path. The runtime at this checkpoint applies data
  plugins only and refuses a native-kind plugin with a message that native plugins are not supported
  yet.

As with the rest of the launcher, the native runtime and these plugins are covered by native tests
but have not been verified in a real game client at this checkpoint. All three declare a minimum
hook-runtime version of 3.0.0 and are data-only patch plugins; none defines configurable options.

## Server launcher API

The server can expose a launcher API through the optional **`launcher.http`** plugin row. It is a
separate public HTTP listener, not part of the admin API: it has its own address, its own routes under
`/v1`, and no administrator routes or bearer-token administration. It serves launcher accounts,
durable sign-ins, launch tickets, a catalog of client presets, and the compatibility policy a
launcher is expected to enforce.

### Enabling the row

- The core bundle ships the row **disabled**. At this checkpoint only the `all` profile enables it.
  Its `replace` patch drops the core row's `listen`, `host_policy` and `region` configuration, so the
  `BFF_LAUNCHER_*` environment variables do **not** apply to that profile unless a later `set` patch
  adds them back; the listener then uses the option defaults.
- The plugin requires an active persistence provider that publishes the launcher account repository
  and the launcher session store. All three providers do: memory, SQLite and MySQL. The `all` profile
  uses the process-local memory provider, so accounts and sign-ins reset when it restarts; a durable
  deployment should use SQLite or MySQL.
- The row is independent from `admin.http`; enabling or disabling one does not change the other.

### Options

The row's configuration is validated as one unit; unknown keys fail binding. The most important keys:

| Key | Meaning | Default / limits |
| --- | --- | --- |
| `listen` | IP address and port of the API listener. Plain HTTP; remote deployments are expected to terminate HTTPS in front of it. | `127.0.0.1:8090` |
| `name` | Public server name shown in the catalog. | `Bubble Fighter Fantasy`, at most 128 characters |
| `host_policy` | How the launcher should reach the game endpoint. | `auto`, `ip` or `domain` |
| `region` | Optional region shown to players. | empty, or two uppercase letters |
| `supports_instances` / `supports_plugins` | Advertise whether the server allows choosing instances and client plugins. | `true` / `true` |
| `registration_enabled` | Whether launcher accounts may register. | `true` |
| `create_account_on_login` | Whether signing in with an unknown name creates that account with the given password, so players need not register. A mistyped name creates a new account. | `false` |
| `minimum_launcher_version` | Minimum launcher SemVer for registration, login, refresh and ticket issuance. Empty disables the gate. | empty |
| `plugin_recommendations` | Up to 64 plugin identities, each with optional exact builds and a required flag; identities only, never URLs or paths. | empty |
| `session_seconds` / `refresh_session_seconds` | Access-token and renewable-session lifetimes in seconds. | 1800 / 2592000 |
| `ticket_seconds` | Launch-ticket lifetime in seconds. | 60 |
| `requests_per_minute` / `max_concurrent_requests` | Global request budget and concurrent API work. | 120 / 8 |
| `max_body_bytes` / `max_connections` | Request body and connection bounds. | 16384 / 128 |
| `instances` | Client presets advertised in the catalog, at most 64. | empty |

`plugin_recommendations` entries contain an `id`, an optional `builds` list (empty means every
build) and `required`. The catalog and every sign-in response include the minimum launcher version
and the merged recommendations.

### Client presets (`instances`)

Each preset advertises one selectable client instance:

| Field | Meaning |
| --- | --- |
| `id` | Stable preset identifier, letters, digits and hyphen. |
| `build` | The client build id, for example `kr-1256`. |
| `name` | Localized display names; an `en` entry is required. |
| `release_version` | Optional release label shown under the name. |
| `client_languages` | Optional authoritative list of client language choices. |
| `host` / `port` | Public game endpoint the launcher connects to; the port is mandatory. |
| `launcher_login` | `required`, `optional` or `none`: the server's advertised preference for launcher authentication. This is catalogue data; the game server's actual enforcement is the lobby's `authentication` option. |
| `download_source` | Optional download source for the client package: `kind` (`http` or `alist`), `base_url`, `catalog_path` and `package_id`. No credentials, queries or fragments are accepted in the URL. |

A preset is advertised data; the launcher does not consume presets yet. The server does not check a
preset's `build` against its own composed profiles, so a typo produces a preset whose tickets can
never be redeemed. This portable preset is not the local instance file of the design: the local
`instance.toml` that would hold a player's server binding and plugin selection does not exist yet.

### Routes

All routes are under `/v1` and are served by the launcher listener only.

| Route | Method | Authentication | Purpose |
| --- | --- | --- | --- |
| `/v1/catalog` | GET | none | Server name, region, host policy, support flags, minimum launcher version, plugin recommendations and client presets. Not version-gated, so an old launcher can read the requirement. |
| `/v1/instances/{id}/instance.toml` | GET | none | Portable instance metadata for one preset: schema, id, name, game and build. Deliberately contains no server address or credentials. |
| `/v1/auth/register` | POST | none | Create a launcher account when registration is enabled. |
| `/v1/auth/login` | POST | none | Verify the password and return an access token, a refresh credential, the account profile, the minimum version and the recommendations. |
| `/v1/auth/refresh` | POST | refresh credential | Exchange the single-use refresh credential for a new pair. |
| `/v1/auth/logout` | POST | refresh credential or bearer | End the session. Not version-gated. |
| `/v1/account` | GET | bearer | The signed-in account's profile: display name and level, experience, Lucci and cash when the corresponding stores are available. |
| `/v1/launch` | POST | bearer | Issue a one-use launch ticket for a build the server advertises. |

The launcher declares its version in the `X-FantasyTale-Launcher-Version` header or a
`launcherVersion` JSON field. If both are present they must agree.

### Version gate and HTTP 426

When `minimum_launcher_version` is set, registration, login, refresh and ticket issuance require a
launcher at or above it. A request with no version, an unparsable one, or an older one receives
**HTTP 426 Upgrade Required** with this JSON body:

```json
{ "error": "launcher_update_required", "minimumLauncherVersion": "<configured version>" }
```

The catalog endpoint itself is not gated, so a launcher can always read the requirement before it
signs in. The launcher at this checkpoint does not parse this response or send a version yet; the
gate is therefore effective as a server-side refusal, not as an in-app update prompt.

### JSON compatibility

The launcher API is deliberately tolerant of new clients and strict about what it consumes:

- Unknown **top-level** JSON fields are ignored, so a newer launcher may send extra fields to an older
  server without failing.
- The request must still be a JSON object. Duplicate top-level keys, wrong types, missing required
  values, invalid usernames, a version header that disagrees with the body, too large bodies or too
  deep nesting are rejected as **HTTP 400** with `{"error":"invalid_request"}`.
- Known fields are validated by the handler that uses them; a build the server does not advertise is
  rejected as `unsupported_build`.
- This tolerance is only for the JSON API. Plugin row configuration stays strict: an unknown YAML key
  fails binding at startup.

- The launcher API has no password-change, password-reset or account-deletion route at this
  checkpoint. An operator can disable registration and can ban an account server-side; the launcher
  has no screen for either action.

### Errors and limits

| Response | Meaning |
| --- | --- |
| `400 invalid_request` | Malformed JSON, duplicate top-level key, wrong field type, invalid username or version, or an unadvertised build. |
| `401 unauthorized` | Missing or expired bearer token; the session is no longer valid. |
| `403` | Registration is disabled. |
| `409 account_unavailable` | The username is already registered. |
| `426 launcher_update_required` | The launcher version gate described above. |
| `429` | The per-minute request budget or the concurrency gate was exceeded. |
| `503` | Authentication capacity is reached (too many retained sessions/tickets). |

Responses carry `Cache-Control: no-store`. The listener is plain HTTP by design; run it behind your
own TLS termination when launchers connect over a network, and keep it on loopback when they do not.
The API never serves the game client, its files or updates: a download source is advertised as data
for the launcher's future client installation, not proxied by the server.

### Example configuration

The row is normally enabled in a profile patch. This example uses placeholders and synthetic values
only:

```yaml
- replace:
    id: launcher.http
    disabled: false
    config:
      listen: "127.0.0.1:8090"
      name: "Example Community Server"
      region: "KR"
      minimum_launcher_version: "1.4.0"
      plugin_recommendations:
        - id: "bubblefighter.fantasy.kr1256-shop-gates"
          builds: ["kr-1256"]
          required: true
      instances:
        - id: "kr-1256-main"
          build: "kr-1256"
          name:
            - language: "en"
              text: "KR 2026"
            - language: "zh-CN"
              text: "KR 2026"
          release_version: "1.0.0"
          host: "game.example.com"
          port: 7100
          launcher_login: "required"
```

For a profile whose launcher row was already replaced and whose listener settings were dropped, add
them back explicitly with a `set` patch:

```yaml
- set:
    id: launcher.http
    config:
      listen: "${BFF_LAUNCHER_LISTEN:-127.0.0.1:8090}"
      host_policy: "${BFF_LAUNCHER_HOST_POLICY:-auto}"
      region: "${BFF_LAUNCHER_REGION:-}"
```

## Troubleshooting

These are the failure paths that exist in the current code. There is no in-app repair action in the
launcher at this checkpoint; recovery means reading the message or log and correcting the input.

| Symptom | Check | Likely cause and fix |
| --- | --- | --- |
| The `launcher.http` row stays `Pending` or fails | `/api/plugins` in the admin API and the startup log | No persistence provider is publishing the launcher account and session stores. Enable exactly one persistence provider. |
| The lobby fails to start with `authentication: ticket requires an active launcher.http row` | The startup error | Ticket authentication (the default) or `launcher_tickets` is on, but no active `launcher.http` row publishes the ticket service. Enable the launcher row, or set `BFF_LOGIN_AUTHENTICATION=open` on a trusted LAN. |
| Players are disconnected right after login with `launcher ticket required` | The server log | The lobby runs with `authentication: ticket` and the client sent a plain account name: it was started without the launcher, or its build does not receive tickets from the launcher yet. Start it from the launcher, or use `open` on a trusted LAN. |
| The game closes the connection with `invalid launcher ticket` | Whether the ticket was issued by this server for this exact build and is unused | The ticket was disabled, spent, expired, issued for another build, or the launcher row restarted since it was issued. Restart the launcher, sign in again and relaunch. |
| An old launcher gets HTTP 426 | `minimum_launcher_version` on the row and the launcher's version | The launcher is below the configured minimum. Update the launcher or clear the minimum for the test. The current launcher does not yet show an update prompt. |
| HTTP 400 `invalid_request` | The request body and version fields | Malformed JSON, a duplicate top-level key, a wrong type, an invalid username, or a header/body version mismatch. |
| HTTP 401 `unauthorized` | The access token lifetime and the server restart | The bearer expired or the server restarted and dropped in-memory bearers. Sign in again; the refresh credential should renew the session. |
| HTTP 429 or 503 | Request rate, concurrent requests, retained sessions | The server's bounded authentication limits were hit. Retry later or raise the limits deliberately. |
| A build reports "has no verified launch arguments yet" | The build's `login` value | The build is defined with `login = "unverified"`. Use a build with a verified login mode. |
| The client shows "The launcher session file is invalid; no client plugins were loaded." | The message box text and the client folder | `session.toml` is stale, hand-edited or malformed. Start the client through the launcher so a fresh session is written; do not edit the session by hand. |
| A required plugin warning appears in the client | The hook log result lines | A plugin marked required failed or is unavailable for the build. Read the log line for that plugin id and fix its options or compatibility. |
| The launcher preview cannot start a client | — | Launch is not implemented at this checkpoint; the launch button in the preview has no action. |
| The client starts without the expected plugins | The hook log at the session's log path (default `%LOCALAPPDATA%\FantasyTale\BubbleFighter\logs\fthook.log`) | The session was not written, the plugin is unavailable for the build, or a patch did not match. Read the result line for the plugin id. The launcher has no log viewer and no repair action. |

For server-side failures outside the launcher API, see [Troubleshooting](troubleshooting.md).

## Distribution boundary

- Neither this server repository nor the launcher repository distributes the game client, unpacked
  client data, proprietary native files or other game-client material. The launcher repository
  contains launcher code, definitions and data hook plugins; the game client remains the player's
  own copy.
- Per-build native parameters, `login.ini` differences and hook selection are **data**: build
  definitions, plugin definitions and session entries. They are not code branches for individual
  builds.
- The native hook runtime and messenger host are built from the launcher repository's own source.
  Hook plugins are applied by that runtime from plugin definitions; editing the client executable or
  its libraries by hand is not a supported installation method.
- The server advertises download sources and client presets as data. It does not proxy, host or
  distribute client packages.

## Next

- [Server configuration](configuration.md) for the profile row and environment variables behind the
  launcher API.
- [Deployment](deployment.md) for running the server, persistence, backups and network security.
- [Troubleshooting](troubleshooting.md) for server-side failures that are not specific to the
  launcher.
