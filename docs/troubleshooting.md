---
title: "Troubleshooting"
description: "Diagnose build, configuration, startup, network, persistence and SDK failures in order."
updated: 2026-10-20
---

## First-run setup rejects a proxy {#setup-proxy-refused}

**Symptom.** A request to `/api/setup/*` returns HTTP 403 with `setup_proxy_refused` before an administrator is created. **Cause.** The request contains `X-Forwarded-For`, `X-Forwarded-Host`, `X-Forwarded-Proto` or `Forwarded`. A reverse proxy can make a remote request appear local. **Fix.** Open setup directly on the server, without a proxy. After administrator enrollment, this first-run restriction no longer applies; normal authentication remains required.

Administration login failures are counted per connection source IP, using `installation.login_attempts` and `login_window_seconds`. `login_address_capacity` defaults to 1024 tracked addresses; a full table replaces its oldest window. Forwarding headers do not select the login budget.

## What this page covers

This page turns "the server does not work" into an ordered investigation: what to check first, where the evidence lives, and which change fixes the problem. Each symptom follows one shape: symptom, check, likely cause, fix, confirm recovery. It covers a source checkout and a release directory; it does not replace a test with a real game client.

**Who this is for.** Operators starting a server for the first time, and contributors whose build or validation gate fails.

**Before you start.**

- Work from the repository root for a source checkout, or from the release directory for a published build.
- For local checks, copy `.env.example` to the git-ignored `.env` and set `BFF_MYSQL_PASSWORD` to a nonempty disposable value. Profile interpolation requires it even when MySQL is disabled.
- Every local Host command on this page includes `--patch tools/local-checks/isolated-memory.patch.yaml`. The patch disables `persistence.mysql` and enables `persistence.memory`, so a developer `.env` or local patch cannot point the server at a live database.
- Read the first error, change one thing per attempt, and keep the failing output.

## First checks

| Symptom | First check | Likely area | Section |
| --- | --- | --- | --- |
| Build does not finish | rerun with `dotnet build BubbleFighter.Fantasy.sln -c Release`; read the first error | toolchain, restore, warnings-as-errors | Toolchain and build |
| `error:` names an environment variable or profile | `--dump-config` with the isolated patch | profile, patch, `.env` interpolation | Configuration |
| `error: Server startup did not converge` | read every listed row id and state | activation, dependencies, client data | Startup and activation |
| A row stays `Pending` | `/api/plugins`; read `state` and `error` | missing service, provider or definition source | Startup and activation |
| A row is `Failed`, `CleanupIncomplete` or `CleanupFailed` | same JSON, then the console log | activation or teardown ownership | Startup and activation |
| Client cannot connect | listening port and advertised address | bind, NAT, firewall, relay | Ports and network |
| Admin API or WebUI unreachable or 401 | `/health`, `BFF_ADMIN_HOST` | admin listener and sign-in | Ports and network |
| Database error, or data gone after restart | `--dump-config`; persistence rows | MySQL, SQLite, migrations, memory | Persistence |
| `clientdata` activation failure | the two paths in the error | client-data files and `BFF_CLIENT_DATA` | Client data |
| SDK plugin fails or times out | plugin stderr and loader row config | JSON-RPC stdout, protocol, limits | SDK plugins |

If nothing matches, go to [Reporting a problem](#reporting-a-problem).

## Toolchain and build problems

### The .NET SDK is missing or not version 10

**Symptom.** `dotnet` is not found, or the build rejects the `net10.0` target. **Check.** Run `dotnet --info`; it lists installed SDKs and runtimes, and every project targets `net10.0`. **Likely cause.** No .NET 10 SDK, or `PATH` still points at an older one. **Fix.** Install the .NET 10 SDK and open a new terminal. **Confirm recovery.** `dotnet --version` reports 10.x and `dotnet build BubbleFighter.Fantasy.sln -c Release` ends with `0 Warning(s)` and `0 Error(s)`.

### Package restore or compilation fails

**Symptom.** The build stops during NuGet restore with a network or feed error, or the compiler reports a warning and fails. **Check.** Read the first restore error; then check whether the message is a compiler warning. `TreatWarningsAsErrors` is on for every project and `GenerateDocumentationFile` requires a real XML summary on every public type and member. **Likely cause.** No access to the package feed, a proxy, a corrupted package cache, a missing XML comment, an unused variable or a nullability mistake. **Fix.** Restore again with `dotnet restore`, fix network or proxy settings, and fix the warning's cause — never suppress the warning to get a green build. **Confirm recovery.** The build reports zero warnings and zero errors, with no package or restore errors.

One restore error has a different cause. `NU1900` (`Error occurred while getting package vulnerability data`) means NuGet's package audit could not reach nuget.org; the audit runs during every restore and its warning counts as an error here. When the machine cannot reach nuget.org but the packages are already in the local cache, build with `dotnet build BubbleFighter.Fantasy.sln -c Release -p:NuGetAudit=false`. This skips only the online vulnerability lookup, not any compiler warning. Run a normal build with the audit before publishing a release.

### A release build does not start on another machine

**Symptom.** A build that compiled cleanly exits immediately on the target machine. **Check.** Run `dotnet --list-runtimes` there and look for `Microsoft.AspNetCore.App 10.x`; the Host uses the ASP.NET Core shared framework. **Likely cause.** The machine has no ASP.NET Core Runtime 10 — a framework-dependent publish does not bundle it. **Fix.** Install the ASP.NET Core Runtime 10 for the target platform; the publish scripts state the requirement as `The deployment server requires the x64 ASP.NET Core Runtime 10.0, which includes the .NET runtime.` **Confirm recovery.** Start from the release directory; the console reports `Server started with <N> active row(s).`

### The validation toolchain is incomplete

**Symptom.** `python tools/ci/serial-gates.py` stops before the tests, or an SDK conformance gate is reported as skipped. **Check.** The gate needs `dotnet`, `node`, `npm`, `go`, `cargo` and `rustup` on `PATH`, and prints `Missing validation toolchains: <names>` when one is absent. **Likely cause.** A development machine set up only for the .NET build. **Fix.** Install the missing toolchains and, for the Rust SDK and its WebAssembly tests, add the target named in the failure: `rustup target add wasm32-wasip1`. **Confirm recovery.** The rerun does not print `An SDK conformance gate was skipped.` and ends with one `PASS public gates:` or `PASS complete gates:` line.

## Configuration and interpolation problems

### An environment variable has no value

**Symptom.** `error: File '<file>', row '<row id>': environment variable '<NAME>' is not set and has no default.` **Check.** Look for `${NAME}` without a `:-default` in the profile, bundle or patch named in the error, then inspect `.env` and the process environment. A repository-root `.env` is applied to unset process variables before profile composition for every command except `start`; existing environment variables win. **Likely cause.** The variable is neither exported nor in `.env`, and `BFF_MYSQL_PASSWORD` has no default, so any game or management profile fails to compose without it. **Fix.** Set the variable for the command or add it to the git-ignored `.env`; never commit real credentials. **Confirm recovery.** Composition succeeds and `--dump-config` prints the effective rows.

### The MySQL password is empty

**Symptom.** `error: Activation of row 'persistence.mysql' (plugin 'persistence.mysql') failed: Invalid configuration for row 'persistence.mysql': connection_string is missing Password (BFF_MYSQL_PASSWORD is required and must not be empty).` **Check.** A copied `.env.example` contains `BFF_MYSQL_PASSWORD=`, so interpolation succeeds with an empty string and the failure appears only when the row activates. **Likely cause.** An empty placeholder left in `.env`. **Fix.** Set a disposable nonempty value for local checks and add the isolated patch so no database is contacted; use real credentials only for a deliberate persistent deployment. **Confirm recovery.** With the isolated patch, `persistence.memory` activates and the log reports in-memory persistence active.

### A profile, patch or option name is wrong

**Symptom.** One of `error: Profile '<id>' not found: expected '<path>'.`, `error: Patch file '<path>' does not exist.`, `error: Unknown option '<option>'.`, `error: Unknown command '<command>'.`, `error: Option '<option>' requires a value.` **Check.** Run `bff profiles list` and `bff --help`; profile ids are directory names under `profiles/`, and the help text lists the accepted options. **Likely cause.** A typo, a profile that does not exist, or an option placed after a command that does not take it. **Fix.** Correct the id or option; for a local run keep `--patch tools/local-checks/isolated-memory.patch.yaml`. **Confirm recovery.** `bff profiles list` prints the profile with its description and the corrected command composes.

### A configuration key is unknown or invalid

**Symptom.** `error: Invalid configuration for row '<row id>': unknown key '<key>'`, or a conversion or validation failure for the same row. **Check.** Plugin options are snake_case YAML keys mapped to PascalCase properties; the plugin's option class lists what it accepts. Check the row id in the message and the `plugin` field it maps to. **Likely cause.** A misspelled key, a key from an older guide, or a value of the wrong type or range. **Fix.** Correct the key or value; every failure for the row is listed in one message, so fix all of them. **Confirm recovery.** `--dump-config` succeeds and shows the corrected value.

### The effective configuration is not what you expected

**Symptom.** The server behaves as if a row were enabled, or a port different from the one you edited, were in use. **Check.** Layers apply in order: bundles, profile patches, then CLI patches. Print the result without starting the host:

```sh
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- run --profile kr-812 --patch tools/local-checks/isolated-memory.patch.yaml --dump-config
```

**Likely cause.** A later layer replaced the row or its `disabled` flag; the isolated patch is the last layer and enables in-memory persistence. **Fix.** Change the layer that should own the value; do not add the same override to two layers. **Confirm recovery.** The dump shows the expected flags and values. It can contain interpolated secrets such as the MySQL connection string — redact it before sharing.

## Startup, activation and plugin states

Every row publishes a state after a start attempt. Read it from the console log or the admin API:

```powershell
Invoke-RestMethod http://127.0.0.1:8080/api/plugins | Select-Object rowId, plugin, state, error
```

```sh
curl -s http://127.0.0.1:8080/api/plugins
```

`/api/plugins` returns one object per runtime instance with `rowId`, `plugin`, `source`, `state`, `error`, `codeRefreshPolicy`, `codeLifetime`, `restartRequiredForReclamation`, `isRetiring`, `overrides`, `version`, `contractVersions` and `supportedBuilds`. Only `Active` is a healthy running state.

| State | Meaning | What to do |
| --- | --- | --- |
| `Active` | Activated; effects are live | none |
| `Pending` | Desired, but services, providers or a definition source are missing | read `error`, fix the dependency |
| `Failed` | Activation or owned work failed | read `error`, fix the cause, reload or restart |
| `Unloading` | Effects closed, teardown running | wait; do not start the row again |
| `Unloaded` | Disabled or unloaded, no live effects | expected for disabled rows |
| `CleanupIncomplete` | Teardown still running past its limit or cancelled | ownership retained; investigate before retrying |
| `CleanupFailed` | Teardown reported an error | resources cannot be assumed released; restart is refused |
| `WorkFailed` | Owned background work failed | resources stay owned until teardown settles |

### Server startup did not converge

**Symptom.** The process exits with code 1 and `error: Server startup did not converge (WaitingForDependencies): <row id>: Pending (<diagnostic>); <row id>: Failed (<diagnostic>).`; the console also logs `Lifecycle revision <N> for '<row id>' did not converge.` **Check.** Read every listed row, then compare `/api/plugins` with the `--dump-config` dump. **Likely cause.** At least one non-disabled row did not reach `Active`: a missing provider, missing client data, a wrong port, or a value that fails binding. **Fix.** Fix the first listed row, not the last; if a row is disabled on purpose, confirm the effective dump disables it. **Confirm recovery.** The console reports `Server started with <N> active row(s).` and `GET /health` answers `{"status":"ok", ...}`.

### A row stays Pending

**Symptom.** The row never activates and `error` explains why, usually `Waiting for services: <service types>; provider rows: <rows>; override rows: <rows>.` or `Definition source for plugin '<plugin>' is unavailable.` **Check.** Find the provider or definition source named in the message and check its row state. **Likely cause.** A required service, optional provider or handler-override target is disabled, failed or absent from the profile; or an SDK loader row that publishes the plugin has not activated. **Fix.** Enable or fix the named provider row, or remove the consumer if the capability is not wanted. **Confirm recovery.** The row becomes `Active` after a reload or restart, and startup converges.

### A row is Failed

**Symptom.** `error: Activation of row '<row id>' (plugin '<plugin>') failed: <cause>.` **Check.** Read the `error` field and the full cause in the console log; the wrapper names the row and plugin, and the inner message names the actual problem — client data, MySQL, SQLite, a port, an unknown key or an unknown plugin. **Likely cause.** Anything the plugin validates during activation. **Fix.** Fix the inner cause. A row with `Automatic activation attempt limit <N> reached for row '<row id>'.` stops retrying automatically and needs an explicit reload once the cause is fixed. **Confirm recovery.** The row becomes `Active`.

### CleanupIncomplete or CleanupFailed

**Symptom.** A row that was unloading shows `CleanupIncomplete` or `CleanupFailed` in `/api/plugins`, and its `error` field and the console log carry the teardown failure. The unload or reload call that triggered teardown can also fail with `Row '<row id>' teardown is <state>; ownership and dependencies are retained.`, where `<state>` is the state the row reached. **Check.** Read the row's `error` and the console log; `CleanupIncomplete` means teardown is still running past its limit or the caller went away, while `CleanupFailed` means an owned disposer threw. **Likely cause.** A resource that blocks on release, an owned background task that does not finish, or a failing disposer. **Fix.** Do not force the row to restart: a restart is refused while ownership remains (`Activation is refused while failed work or cleanup ownership remains: <row ids>.`). Let cleanup settle or stop the server; if it cannot settle, restart the process and fix the plugin. **Confirm recovery.** The state becomes `Unloaded` and the server shuts down cleanly.

### A plugin is unknown or a row id is duplicated

**Symptom.** `error: Row '<row id>' references unknown plugin '<plugin>'.` or `error: Duplicate row id '<row id>' in profile '<profile id>'.` **Check.** Run `bff plugins list` and look for the plugin id; check that its assembly is under the plugin root (`plugins/` next to the host, or a directory passed with `--plugin-root`). For a duplicate, find the bundle or patch that contributes the second row. **Likely cause.** A stale or missing plugin build, a plugin directory not copied, a typo in the `plugin` field, or a patch reinserting an existing id. **Fix.** Rebuild the solution (the Host build replaces its `plugins` directory from `artifacts/plugins`), use `--no-incremental` if a stale copy is suspected, and remove or rename the duplicate row. **Confirm recovery.** `bff plugins list` prints the id, version, supported builds and origin, and `--dump-config` lists the row once.

### A required service is missing at activation

**Symptom.** A failed row reports `Row '<row id>' cannot <verb>; missing service(s): <service types>.` **Check.** Identify the row that should publish the service; the dependency is the declared interface, not a row id. **Likely cause.** The provider row is disabled, failed or absent, or it lives in a separate service realm through `isolated_services`. **Fix.** Enable the provider or remove the service from the consumer's declaration; check `parent_id` and `isolated_services` when provider and consumer should live in different trees. **Confirm recovery.** The provider is `Active` first, then the consumer activates on the next reconciliation.

## Ports, bind addresses, NAT and admin authentication

### A port is already in use

**Symptom.** A listener row fails with `error: Activation of row 'game-listener' (plugin 'net.tcp') failed: <operating system socket error>`. **Check.** Find the owner before changing anything: `Get-NetTCPConnection -LocalPort 7110 -State Listen | Select-Object LocalAddress, LocalPort, OwningProcess` then `Get-Process -Id <owningProcess>` on Windows, or `sudo ss -ltnp | grep :7110` on Linux. **Likely cause.** Another server instance, the same profile in a second terminal, or an unrelated process. **Fix.** Stop only the process you identified, or move this profile's port with its variable, for example `KR_812_GAME_PORT`. Do not kill every .NET process on the machine. **Confirm recovery.** The log shows `Row 'game-listener' listening on <address> (protocol <profile>/game).` and the port is listening.

### Clients cannot connect through NAT or a firewall

**Symptom.** The server listens, but a client on another machine cannot connect. **Check.** Listeners bind `${BFF_BIND_HOST:-0.0.0.0}`, while the client is told `${BFF_PUBLIC_HOST:-127.0.0.1}` in the center gate address and the relay advertisement. The public default is loopback, which works only on the same machine. Check both values in `--dump-config`, then test the path with `Test-NetConnection <public-host> -Port 7110` on Windows or `nc -vz <public-host> 7110` on Linux. **Likely cause.** `BFF_PUBLIC_HOST` left at its default, a firewall blocking the port, or a port-forward that does not match the advertised port. **Fix.** Set `BFF_BIND_HOST` and `BFF_PUBLIC_HOST` to addresses the client can reach, open only the ports the chosen profile needs, and forward the advertised numbers. **Confirm recovery.** The client reaches the login or center connection and `/api/sessions` shows it while connected.

### The relay connection fails

**Symptom.** Login works on a modern build, but the match connection does not, or the relay port never listens. **Check.** The `kr-1256` profile has `${KR_1256_GAME_PORT:-7100}` and `${KR_1256_RELAY_PORT:-7101}` listeners, and advertises `relay_advertise_ipv4` and `relay_advertise_port` (default `${KR_1256_RELAY_PUBLIC_PORT:-7101}`). Compare the listener with the advertisement. **Likely cause.** Advertised and listening ports differ behind NAT, a patch disabled the relay row, or the client is a legacy build. **Fix.** Make the advertised relay address and port reachable and keep both relay values consistent. Legacy builds have no relay listener yet (`No relay listener on the legacy builds yet.` in the legacy bundle), so do not expect a relay connection there. **Confirm recovery.** The relay port listens and the match connection appears in `/api/sessions` with `matchBound` set.

### The admin API or WebUI is unreachable or unauthorized

**Symptom.** The browser cannot reach the WebUI, or `/api/...` answers 401 with `{"error":"unauthorized"}`. **Check.** The admin row binds `${BFF_ADMIN_HOST:-127.0.0.1}:${BFF_ADMIN_PORT:-8080}`, loopback by default. A server started with `start` needs a signed-in administrator session for every protected `/api` request; `/health` and the WebUI files stay open. During first-run setup, `/api/plugins` answers 401 until an administrator account exists — that is the authentication model, not a server failure; `/health` and `/api/setup/status` stay reachable. **Likely cause.** The listener is on loopback and the browser is elsewhere, the session expired, a `run --profile` server was given a non-loopback address (it has no accounts and refuses one), or a non-loopback installation listener was configured without a bootstrap token. **Fix.** Sign in again; on a server started with `start`, set `BFF_ADMIN_HOST` to the interface you need and set `bootstrap_token` (`BFF_SETUP_TOKEN`) to at least 32 characters for a non-loopback installation listener. The row otherwise fails with `A non-loopback installation listener requires an explicit bootstrap_token of at least 32 characters.` **Confirm recovery.** `/health` returns `{"status":"ok", ...}` and the signed-in WebUI lists the plugins.

## Persistence and migrations

### MySQL connection or authentication fails

**Symptom.** `error: Activation of row 'persistence.mysql' (plugin 'persistence.mysql') failed: Failed to open MySQL at <host>:<port> database '<database>' as '<user>'.` **Check.** Check `BFF_MYSQL_HOST`, `BFF_MYSQL_PORT`, `BFF_MYSQL_USER`, `BFF_MYSQL_PASSWORD` and `BFF_MYSQL_DATABASE`, and that the server is reachable. **Likely cause.** Wrong host, port, user or password, no reachable server, or a database that does not exist. **Fix.** Correct the variables for a deliberate persistent deployment; never point a local check at a production database. **Confirm recovery.** The log reports `MySQL persistence connected to <host>:<port>/<database> as <user>.`

### Migrations fail

**Symptom.** Activation fails while reading or applying embedded migrations, with one of `Embedded SQL resource '<resource>' is not named NNNN_description.sql.`, `Duplicate embedded migration version <version>.`, `No embedded SQL migrations were found.` or `Embedded migration '<resource>' is empty.` **Check.** Migrations are embedded resources applied in filename order when the plugin activates with `auto_migrate: true`. **Likely cause.** A damaged plugin build, or a migration resource that was renamed or emptied. **Fix.** Do not edit a live `schema_migrations` table by hand; rebuild the plugin from a clean checkout. There is no downgrade migration path, so restore the backup taken before the schema changed if you must return to an older build. **Confirm recovery.** Activation logs one `Applied migration <version> (<name>).` line per pending migration, then the connection line.

### Two persistence providers are enabled

**Symptom.** A persistence row fails with `Service '<contract>' is already registered in realm <id>.` **Check.** Only one of `persistence.memory`, `persistence.sqlite` and `persistence.mysql` may be enabled. Check the `disabled` flags in `--dump-config`; the isolated patch enables memory and disables MySQL but leaves SQLite disabled. **Likely cause.** A patch enabled a second provider without disabling the first. **Fix.** Disable the providers you do not use in the same patch. **Confirm recovery.** Exactly one persistence provider is `Active` and the others are `Unloaded`.

### SQLite path or locking problems

**Symptom.** The SQLite row fails to activate, or writes fail with `SQLite transaction committed but WAL truncation failed; further operations are refused.` **Check.** `database_path` must be an absolute file path with a file name; a relative path fails with `database_path must be an absolute file path.` Keep the save outside release and log directories, and run one server per save. The lock wait is `busy_timeout_ms`, default 5000. **Likely cause.** A relative path, a read-only directory, or two processes opening the same save. **Fix.** Fix the path and permissions and stop the second process. **Confirm recovery.** The log reports `SQLite save active; capacity <players> players, <accounts> accounts, <bytes> main-file bytes.`

### Data is empty after a restart

**Symptom.** Accounts, items or progress disappear after stopping and starting the server. **Check.** `--dump-config` shows which persistence row is enabled. With the isolated patch, or any configuration that enables `persistence.memory`, state is process-local; the activation log says `In-memory persistence active for up to <players> players and <movement actors> movement actor(s); state is process-local and resets on restart.` **Likely cause.** In-memory persistence was expected to be durable. **Fix.** For durable state, enable `persistence.sqlite` or `persistence.mysql` and disable the other providers; for local experiments keep the isolated patch and expect the reset. **Confirm recovery.** After a restart with a durable provider, the same account can log in.

### Restore a backup

**Symptom.** A database change needs to be undone. **Check.** The server has no command that restores a database; restoring is a database-level operation, and the server must be stopped before touching the database. **Likely cause.** A bad migration, an accidental write or a failed upgrade. **Fix.** Restore the backup taken before the change, then start the server with the configuration that matches the restored schema. Do not edit `schema_migrations` or delete rows to fake a rollback. **Confirm recovery.** `/health` is fine, the persistence row is `Active`, and the expected accounts and data are present.

## Client data

### The clientdata row fails activation

**Symptom.** `error: Activation of row 'clientdata' (plugin 'clientdata') failed: clientdata: neither data_directory '<base>/client-data/<profile>/data' nor raw_directory '<base>/client-data/<profile>/dataraw' exists. Provision the configured client-data directories before enabling this row.` **Check.** Game profiles resolve `${BFF_CLIENT_DATA:-client-data}/${BFF_PROFILE_ID}/data` and `.../dataraw`, relative to the working directory. Compare both paths in the error with the files on disk and with `BFF_CLIENT_DATA` in `--dump-config`. **Likely cause.** The client data was never supplied for this build, `BFF_CLIENT_DATA` points elsewhere, or the server was started from a different directory. **Fix.** Supply the matching local client data, set `BFF_CLIENT_DATA` to its root, or use the `management` profile when you only need administration and storage. The repository does not include the client or its proprietary data. **Confirm recovery.** The `clientdata` row becomes `Active` and a game profile starts with `Server started with <N> active row(s).`

### A client file is missing or the version does not match

**Symptom.** A feature fails with `clientdata: no client file '<path>' in the archives or the loose files.`, or `/api/logs` records `Session <id> reported client version <reported>, expected <expected>.` **Check.** For a missing file, confirm the relative path in `data/` or a loose file in `dataraw/` with the client's own spelling; loose files win over archives. For a version mismatch, compare the client's `ClientVersion_Major` with the profile's `client_build` and profile id. **Likely cause.** An incomplete client-data copy, an archive that was not copied, or a client build that does not match the selected profile. **Fix.** Copy the complete data for this build, and start the profile that matches the client build; do not edit a profile to accept a build the code does not support. **Confirm recovery.** The feature reads the file, and the session connects without the mismatch warning.

### A setup client-data import fails

**Symptom.** The setup assistant reports a failed import, or the API answers 400, 413 or 409 with `{"error":"client_data_invalid"}`, `{"error":"client_data_limit"}` or `{"error":"client_data_busy"}`. **Check.** The import accepts a prepared folder or a classic ZIP. The defaults bound one copy at 8 GiB uploaded, 16 GiB extracted, 32,768 files or ZIP entries, 64 retained copies, 64 GiB of retained storage and 20 minutes. ZIP64 and split archives are not supported; `client_data_busy` means another import is already running. **Likely cause.** Too many or too large files, a layout the importer does not recognize, or a second concurrent import. **Fix.** Import a prepared `<build>/data` and `<build>/dataraw` layout within the limits, adjust the limits on the `installation` row within their allowed ranges when the deployment needs it, and wait for a running import to finish before retrying. **Confirm recovery.** The import returns a root path and the `clientdata` row for the matching profile activates.

## SDK plugin problems

### The SDK plugin process does not start

**Symptom.** `error: Activation of row '<row id>' (plugin 'loader.process') failed: SDK plugin process '<command>' could not start in '<directory>': <reason>` or `SDK plugin process '<command>' did not start.` **Check.** Check the loader row's `plugins[].command`, `args`, `cwd` and `env`; a command with a directory part resolves against `base_dir`, the rest is looked up on `PATH`, and a missing `cwd` fails configuration with `plugins[<n>].cwd: directory '<path>' does not exist`. **Likely cause.** The interpreter or executable is not installed, the working directory is wrong, or `PATH` does not contain the command. **Fix.** Install the runtime, correct `command`/`cwd`, or pass an absolute path. **Confirm recovery.** The row activates and the plugin id appears in `/api/plugins` with `state` `Active`.

### The plugin writes to stdout

**Symptom.** `error: Activation of row '<row id>' (plugin 'loader.process') failed: SDK plugin process broke the protocol: stdout carried a line that is not JSON (<detail>); stdout is reserved for the protocol, use stderr for diagnostics.` **Check.** Find the print, banner or progress output that writes to stdout. **Likely cause.** A debug print, a library banner or a progress bar on stdout. **Fix.** Send every diagnostic to stderr; keep stdout for JSON-RPC lines only. **Confirm recovery.** The plugin handshakes and activates.

### Protocol or capability mismatch

**Symptom.** A declaration or negotiation failure such as `SDK plugin source '<source>' answered initialize without a supported protocol_version (1 or 2).`, `protocol v1 allows only IMessageRouter in requires and optional.` or `requires_many and optional_many require protocol v2.` **Check.** Compare the plugin manifest with the host's supported protocol versions and with the declarations each version allows; typed messages, events, providers and decorators need protocol v2. **Likely cause.** An old SDK or manifest pinned to protocol v1 while using v2 declarations. **Fix.** Update the SDK or manifest, or restrict the declaration to what v1 supports. **Confirm recovery.** The source publishes its definitions and the dependent rows activate.

### A call times out or is refused

**Symptom.** `SDK plugin <label> did not answer '<method>' within <n> ms (request_timeout_ms).` or `SDK plugin <label> already has <n> requests in flight (max_in_flight <n>).` **Check.** Check the loader row's `request_timeout_ms` and `max_in_flight`, and measure what the plugin does in the call; a blocked event loop shows up here. **Likely cause.** A slow or blocked handler, or a burst above the in-flight bound. **Fix.** Make the handler return promptly, raise the bounds only with a measured reason, and retry only idempotent operations — a timeout does not undo a change the plugin already committed. **Confirm recovery.** Calls complete within the configured timeout and the in-flight count stays below the bound.

### A WebAssembly module fails to load

**Symptom.** `error: Activation of row '<row id>' (plugin 'loader.wasm') failed: WebAssembly plugin '<module>' exports no '_start'; build it as a WASI command (wasm32-wasip1 or GOOS=wasip1).` or `WebAssembly plugin '<module>' could not be loaded: <reason>`. **Check.** Check `plugins[].module` in the `loader.wasm` row and the module's build target. **Likely cause.** A module built for the wrong target, or a path that does not exist. **Fix.** Build a WASI command module with the Rust SDK (`wasm32-wasip1`) or the Go SDK (`GOOS=wasip1`), then correct the module path. **Confirm recovery.** The module is accepted and its definitions activate.

## Reporting a problem

Include enough for another person to reproduce the failure. Use this template, one report per problem:

```text
Source revision: <git rev-parse --short HEAD, or release/package version>
Operating system: <Windows 11 x64 / Debian 13 x64 / container image>
Profile: <profile id or the matching bff profiles list line>
Command: <exact command; include --patch tools/local-checks/isolated-memory.patch.yaml for local checks>
Expected: <what should have happened>
Actual: <what happened, with exit code>
First error: <the first error: line>
Affected rows: <rowId, state and error from /api/plugins>
Evidence: <console excerpt, TRX result, dump path>
Source of the observation: <real game client, automated test, or both>
```

Before attaching anything, remove tokens, passwords, account names, player identities, public addresses and packet payloads that contain personal data. Do not attach client data, proprietary files or private investigation material. Redact `--dump-config` output, which can contain interpolated credentials.

State clearly whether the problem was seen with a real game client or only in automated checks; an automated pass or failure does not establish client compatibility.

## Next

- [Configuration](configuration.md) for profiles, patches, environment variables, ports and persistence.
- [Deployment](deployment.md) for running the server for other people, upgrades and rollback.
- [Plugins](plugins.md) and [SDK protocol](sdk-protocol.md) for activation and lifecycle details.
- [Launcher](launcher.md) for launcher accounts, session tickets, launcher API version gates and the companion launcher's own errors.
