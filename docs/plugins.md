---
title: "Plugins"
description: "Understand the plugin model and build, mount, configure and reload a native .NET plugin."
updated: 2026-10-23
---

Everything the server does is implemented by a plugin. A profile composes plugin rows, each row binds one plugin implementation and one configuration to a stable instance id, and the host activates those rows in the order their declared service requirements allow. This page explains that model, shows how to see what the host discovered and loaded, then walks through a first native .NET plugin from project file to a row that runs. It ends with configuration, lifecycle and reload, the boundary between native plugins and the optional language SDKs, the rules plugin authors must follow, and the failures you are most likely to hit.

**Who this is for:** a developer who can build the solution and read C# and YAML, and who wants to add behaviour without changing the core server.

## Before you start

- [Installation](installation.md) is complete: `dotnet build BubbleFighter.Fantasy.sln -c Release` reports zero warnings and zero errors.
- A terminal is open at the repository root; the commands on this page run there unless a step says otherwise.
- The .NET 10 SDK is available: `dotnet --version` reports a 10.x version.
- For the run steps, an `.env` file with a disposable, nonempty `BFF_MYSQL_PASSWORD` exists. See [Quickstart](quickstart.md) for why profile composition needs that variable even when MySQL is disabled.
- The local-check patch `tools/local-checks/isolated-memory.patch.yaml` exists. Every local run on this page passes it so no real database is touched.
- The `management` profile used for the first run serves administration only and needs no client data. Game profiles such as `kr-812` also need that build's client data.

## The model

A plugin is a class marked with `[Plugin("plugin.id")]` that implements `IPlugin` from `BubbleFighter.Fantasy.Abstractions`. The attribute id is the **plugin id** referenced by rows. A profile contributes **plugin rows**; each row's `id` is the **row id**, the unique instance identity inside that composed profile. One implementation can be mounted many times: the `kr-1256` bundle mounts the `net.tcp` plugin twice as `game-listener` and `relay-listener`, and the `all` profile mounts every build's rows with a `<build>.<row id>` prefix.

Plugins never see each other's classes. They publish and consume services by shared interface type. The interfaces live in two assemblies the host loads once and shares into every plugin: `BubbleFighter.Fantasy.Abstractions` (kernel contracts such as `IPlugin`, `IServiceRegistry`, `IEventBus`, `IMessageRouter`) and `BubbleFighter.Fantasy.Contracts` (game contracts such as message records and domain services). A row declares what it needs and what it publishes, and the host activates rows only when their requirements are registered, so activation order comes from service availability, not from the order of rows in YAML.

| Declaration | Meaning |
| --- | --- |
| `Requires` | Singleton services that must be registered before this row activates |
| `Optional` | Services that may be absent; selected providers take part in unload and reload order |
| `Provides`, `ConditionalProvides` | Services this row must, or may, publish during activation |
| `ProvidesMany`, `RequiresMany`, `OptionalMany` | Ordered extension points: publish, require at least one, or accept an empty list |
| `Decorates` | Wrapper layers around a singleton that opted in with `RegisterDecoratable` |
| `Overrides`, `Middleware` | Typed message handler replacement and wrapping, declared as `rowId:fully.qualified.MessageType` |
| `SupportedBuilds`, `SupportedFamilies` | Client build compatibility checks for the row's profile |

Registration is not complete until it is owned. `IPluginContext.Effect(IDisposable)` and `Effect(Action)` register a disposer that the host runs during teardown in reverse order; `AcquireEffectAsync` reserves a slot before an asynchronous factory runs; `CreateResourceScopeAsync` creates a child resource lifetime; `RunTaskAsync` starts background work whose completion and cancellation the host supervises. Service registrations, event subscriptions and message routes made through the context's `Services` and `Events` views are tracked automatically, but a resource you obtain elsewhere still needs an explicit effect. Each row gets a bounded effect capacity (`lifecycle.effect_capacity`, default 4096) shared by the activation and its resource scopes, so a plugin that leaks registrations eventually fails instead of exhausting the process.

## See what is loaded

### 1. List the plugin definitions

```powershell
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- plugins list --log-level Error
```

`plugins list` starts the host's definition bootstrap, discovers native plugin modules under the plugin roots, and prints one tab-separated line per accepted definition:

```text
<plugin id>	<version>	<comma-separated supported builds>	<origin>
```

The third column is empty when the plugin is build-independent (for example `loader.native`), and shows ranges such as `kr:1256` when the declaration restricts builds. The fourth column is the definition origin: the assembly name for a definition carried by the host itself, or the plugin directory for a module discovered under a root. `--log-level Error` keeps activation log lines off the listing; without it the activation messages and the listing share standard output. `--plugin-root <dir>` is repeatable and, when present, replaces the default root list; the default is the `plugins` directory next to the host executable (for a `dotnet run` build that is the build output under `src/BubbleFighter.Fantasy.Host/bin/Release/net10.0/`).

**Check it worked:** the command exits with code 0 and your plugin id appears with the expected version and build range. This lists what the host can mount; it does not activate any row.

### 2. Read runtime state

A running server exposes its row instances at `GET /api/plugins`:

```powershell
Invoke-RestMethod http://127.0.0.1:8080/api/plugins | Select-Object rowId, plugin, state, error
```

```sh
curl -s http://127.0.0.1:8080/api/plugins
```

Each item carries `rowId`, `plugin`, `source`, `state`, `error`, `codeRefreshPolicy`, `codeLifetime`, `restartRequiredForReclamation`, `isRetiring`, `overrides`, `version`, `contractVersions` and `supportedBuilds`. State names are written as strings and come from the `PluginState` enumeration. On a server started with `start`, `/api/plugins` needs a signed-in administrator session; a `run --profile` server answers it on loopback without sign-in, as described in [Quickstart](quickstart.md).

### 3. Know where plugin assemblies live

A built host discovers one native module per subdirectory of a plugin root. The rule is fixed: for a root `<root>`, the directory `<root>/<Name>` is one module and its main assembly must be `<root>/<Name>/<Name>.dll`; other dependencies of that module sit beside it. The repository build produces exactly that layout: each plugin project's build output is mirrored to `artifacts/plugins/<AssemblyName>/`, and the Host build replaces its output `plugins` directory with those mirrored folders. The publish scripts produce `<publish dir>/plugins/<ProjectName>/` with the same shape.

The host supplies `BubbleFighter.Fantasy.Abstractions`, `BubbleFighter.Fantasy.Contracts` and the logging abstractions from its own default context, so those assemblies must not be copied into a plugin directory; the repository's plugin build targets remove them automatically. Everything else a plugin needs at run time has to be inside its own module directory.

## Write a first native plugin

### 1. Create the project

Create `src/Plugins/BubbleFighter.Fantasy.Heartbeat/BubbleFighter.Fantasy.Heartbeat.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <Description>Logs one heartbeat line per activation.</Description>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\BubbleFighter.Fantasy.Abstractions\BubbleFighter.Fantasy.Abstractions.csproj" />
    <ProjectReference Include="..\..\BubbleFighter.Fantasy.Contracts\BubbleFighter.Fantasy.Contracts.csproj" />
  </ItemGroup>
</Project>
```

The repository root `Directory.Build.props` supplies `net10.0`, nullable references and warnings-as-errors, and `src/Plugins/Directory.Build.targets` adds the ASP.NET Core framework reference, strips the host-shared assemblies and mirrors the build to `artifacts/plugins/`. Place an out-of-repository plugin in a folder `<root>/<Name>/` with its DLL at `<root>/<Name>/<Name>.dll` and point the host at `<root>` with `--plugin-root`.

### 2. Write the plugin class

Create `src/Plugins/BubbleFighter.Fantasy.Heartbeat/HeartbeatPlugin.cs`:

```csharp
using System.ComponentModel.DataAnnotations;
using BubbleFighter.Fantasy.Abstractions;
using Microsoft.Extensions.Logging;

namespace Example.Heartbeat;

[Plugin("example.heartbeat", Description = "Logs one heartbeat line per activation.")]
public sealed class HeartbeatPlugin : IPlugin
{
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<HeartbeatOptions>();
        context.Logger.LogInformation("Heartbeat every {IntervalSeconds} s.", options.IntervalSeconds);
        context.Effect(() => context.Logger.LogInformation("Heartbeat stopped."));
        return ValueTask.CompletedTask;
    }
}

public sealed class HeartbeatOptions
{
    [Range(1, 3600)]
    public int IntervalSeconds { get; set; } = 30;
}
```

`Config.Bind<T>()` reads the row's `config` mapping, converts snake_case keys to PascalCase properties, applies DataAnnotations validation and throws one `ConfigException` that lists every failure. The lambda passed to `Effect` runs once at teardown, after the row's lifetime token is cancelled; in a real plugin acquire the resource first and register its disposer immediately.

### 3. Build it

```powershell
dotnet build BubbleFighter.Fantasy.sln -c Release
```

A successful build prints `0 Warning(s)` and `0 Error(s)`. The plugin lands in `artifacts/plugins/BubbleFighter.Fantasy.Heartbeat/` and, because the Host build copies the artifact mirror, in the Host's output `plugins/BubbleFighter.Fantasy.Heartbeat/`.

**Check it worked:** the definition is now discoverable. You can confirm without rebuilding the Host by pointing the listing at the artifact mirror:

```powershell
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- plugins list --plugin-root artifacts/plugins --log-level Error
```

The output contains a line whose first column is `example.heartbeat`.

### 4. Mount it with a row

A row is added with a patch file, so the packaged profiles stay untouched. Create `artifacts/heartbeat.patch.yaml`:

```yaml
- insert:
    - id: example.heartbeat
      plugin: example.heartbeat
      config:
        interval_seconds: 15
```

`insert` appends rows; `replace`, `set` and `remove` are the other patch operations. Start the administration-only profile with the isolated-memory patch applied first and the heartbeat patch last:

```powershell
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- run --profile management --patch tools/local-checks/isolated-memory.patch.yaml --patch artifacts/heartbeat.patch.yaml
```

The host mounts and activates the row. Expected output includes a line in this shape, followed by the plugin's own message and the active-row count:

```text
Row 'example.heartbeat' activated (plugin 'example.heartbeat' version 1.0.0+<commit>; contracts ...; builds independent).
Heartbeat every 15 s.
```

**Check it worked:** the activation line names your row and plugin, the plugin log line appears, and no row reports `Failed` or `Pending`. `GET /api/plugins` shows `example.heartbeat` with state `Active`. Press Ctrl+C to stop; the console prints `Shutdown complete.` and the heartbeat effect runs on the way out.

### 5. Undo it

Delete `artifacts/heartbeat.patch.yaml` and stop passing it. Removing the patch only removes the instance; the definition stays discoverable until you delete its module directory. If you also delete the plugin project, remove `artifacts/plugins/BubbleFighter.Fantasy.Heartbeat/` before rebuilding the Host, because the Host copies whatever folders are present in the artifact mirror.

## Configure a row

| Row key | Purpose |
| --- | --- |
| `id` | Required unique instance id within the composed profile |
| `plugin` | Required plugin id from the `[Plugin]` declaration |
| `config` | Mapping bound by the plugin; keys are validated when the plugin binds them |
| `disabled` | `true` lists the row without activating it; a disabled row is not interpolated |
| `version` | Release constraint such as `>=1.2 <2`, checked against the definition version |
| `parent_id` | Runtime parent row: the parent becomes active first and outlives the children's cleanup |
| `isolated_services` | Interface names that get a fresh service namespace for this row's subtree |
| `lifecycle` | Resource capacity, teardown limits and code-refresh policy; see the next section |

Values in an enabled row's `config` support environment interpolation at composition time: `${NAME}` requires the variable, `${NAME:-default}` supplies a fallback, and a missing variable without a default fails composition with `environment variable '<NAME>' is not set and has no default.` A `disabled: true` row never activates, so its configuration is not interpolated at all. A row `version` constraint is checked when the row activates and fails with `Plugin '<id>' version <version> does not satisfy row '<row>' constraint '<constraint>'.`

Activation order is determined by declared service availability. A row whose requirement is not registered stays `Pending`, and its `error` lists what it waits for, for example `Waiting for services: <interface>; provider rows: <rows>; override rows: <rows>.` The server reports a stalled startup with `Plugin activation stalled; the following rows have unsatisfiable service dependencies:` followed by one line per stuck row. When your plugin needs a service, declare it and read it only through the context; undeclared access throws instead of silently observing another plugin's implementation.

## Lifecycle and reload

| State | Meaning |
| --- | --- |
| `Active` | The row activated and its effects are live |
| `Pending` | The row is desired but required services or selected providers are not available yet |
| `Unloading` | Effect admission is closed and the row is draining |
| `Unloaded` | The row stopped and has no live effects |
| `Failed` | Activation or owned work failed and the row holds no effects |
| `WorkFailed` | Owned background work failed; resources stay owned until teardown settles |
| `CleanupIncomplete` | Teardown exceeded its observation limit; ownership and dependencies are retained |
| `CleanupFailed` | Teardown reported an error; restart is refused |

Unloading closes effect admission, cancels the activation's `Lifetime` token, waits for admitted event callbacks to finish, then disposes effects in reverse order. A row cannot unload while an active row still depends on a service it published: the host throws `Row '<id>' cannot unload; active row(s) depend on its services: ...`.

`POST /api/plugins/{rowId}/reload` reloads one row through `IPluginHost.ReloadAsync`. Dependents that consume the row's services are unloaded first in reverse activation order, the target reloads, then they re-activate in their original order; the response contains the reloaded row ids. For a native plugin loaded from an isolated module, reload also reloads the module assembly from disk. Two limits are part of the contract:

- `lifecycle.code_refresh_policy: restart_required` refuses an in-place code refresh with `Row '<id>' requires a process restart for code refresh (lifecycle.code_refresh_policy=restart_required).` Use it for code that cannot be refreshed safely.
- Reload cannot reclaim process-lifetime code. `codeLifetime` reports `ProcessLifetime` for ordinary native modules and `Collectible` only when the backend supports collection; `restartRequiredForReclamation` is true when a restart is needed for reclamation. Reload refreshes code only where the backend supports it.

Teardown that fails or times out never pretends to be complete. A timeout leaves the row in `CleanupIncomplete`; an error leaves it in `CleanupFailed`, the row keeps its ownership and dependency leases, and restart is refused until cleanup settles. Set per-row limits under `lifecycle`: `effect_capacity` (default 4096), `teardown_timeout_ms` (default 10000) and `code_refresh_policy` (`allow` by default) are the ones plugin authors normally touch. Definition-publishing rows also declare startup grants (`definition_source_reserve`, `definition_reserve`); keep those on the row that publishes definitions and do not change them after startup.

## Choose native .NET or a language SDK

| Capability | Native .NET plugin | Optional language SDK plugin |
| --- | --- | --- |
| Extension surface | Every kernel and game contract | Typed messages, declared services, decorators, ordered collections, events, tasks, resources |
| Loader | Discovered as a native module | `loader.process` child process or `loader.wasm` module |
| Contract access | Direct interfaces and types | Declared names resolved against the generated contract manifest |
| Code refresh | Module reload through the configured backend | The process or module runs until the row or process ends |
| Toolchain | .NET 10 only | Python, Node.js, Go or Rust, plus the matching loader |

.NET remains the primary platform: the SDKs adapt the same host contracts and do not duplicate game logic, but they cover a subset. Full native parity is not implemented. Packet codecs, native connection and type identities, delegates, reference parameters, contract discovery outside the shared contract assemblies, and provider methods that receive a native caller context still need dedicated bridges. The contract manifest marks every operation as callable (`rpc_shape`) or implementable by an SDK provider (`provider_rpc_shape`), and unsupported shapes fail explicitly rather than degrading silently.

Use a native plugin when you need codecs, message middleware at the decoding layer, or any contract the bridge does not model. Use an SDK plugin when you want policy, game logic, or a service implementation in another language and the capability fits the declared surface. [SDK protocol](sdk-protocol.md) documents the protocol, per-language quick starts, the contract manifest and the conformance runner.

## Rules for plugin authors

- No hard-coded tunables. Expose limits, intervals and identifiers as row configuration with documented bounds and validation; a value an operator cannot change is a defect.
- Keep every resource bounded. Queues, caches, sessions, handler tables and background work need explicit limits; reserve effect capacity before acquisition instead of discovering exhaustion later.
- Fail loud. Throw from activation when configuration or required state is wrong; never swallow a failure and continue with partial state. The host converts the exception into a `Failed` row with the message.
- Own what you register. Register an effect immediately after acquiring a resource, never dispose a handle you did not own, and report cleanup failures instead of hiding them; incomplete teardown retains ownership by design.
- Use the owned APIs for asynchronous work. `AcquireEffectAsync` and `RunTaskAsync` give cancellation and completion tracking; a raw fire-and-forget task escapes the row's lifetime and is a leak.
- Keep lifecycle callbacks short. Activation, deactivation and disposers must not block on unrelated work or wait for the row's own drain.
- Write English XML documentation on public types and members. The solution documents all projects with warnings-as-errors, so an undocumented public member fails the build.
- Never ship private copies of the shared contract assemblies and never reference third-party projects from plugin code.
- Treat message and event payloads as values. Mutating a received object does not publish a change and can corrupt another plugin's view.

## Troubleshooting

| Symptom | Check | Likely cause and fix |
| --- | --- | --- |
| `plugins list` does not show the plugin | The roots you passed; the folder and DLL names | The module directory name must equal the assembly file name stem, and the assembly must contain a `[Plugin]` type. A bad module fails with `Plugin assembly '<path>' defines no [Plugin] type.` or a duplicate-id error. |
| Row stays `Pending` | `error` field of `/api/plugins` and the startup log | A required service is missing. Read `Waiting for services: ...; provider rows: ...` and enable or fix the provider row; an override target row must also be active. |
| Startup stops with `references unknown plugin '<id>'` | Whether the definition source row is mounted and active | Native definitions need a plugin root that contains the module. SDK definitions need the `loader.process` or `loader.wasm` row in the same profile, and that loader row must reserve a definition source (`lifecycle.definition_source_reserve`). |
| `Declared service(s) were not published: ...` | The activation code that should call `Services.Register` | A `Provides` declaration is a promise. Register the service during activation and give the registration to `Effect`. |
| `Invalid configuration for row '<row>': unknown key '<key>'` | The row's `config` keys against the bound options type | Binding rejects unknown keys. Rename the key or accept it in the options type; the message lists every failure in one exception. |
| `Duplicate row id '<id>' in profile '<profile>'` | The composed row list from `--dump-config` | Two rows share an id, or a build-scoped row collides in the `all` profile. Rename the instance id. |
| Version or build mismatch | The row's `version`, the profile's `client_build`, the plugin's supported ranges | The quote names the constraint: `does not satisfy row ... constraint`, `does not support client build ...`, or `Plugin requires BubbleFighter.Fantasy.Abstractions ... host provides ...`. Align the plugin with the deployed contracts. |
| Row is `Failed` or cleanup is incomplete | The `error` text and the plugin's disposers | Read the first failure; `CleanupIncomplete` and `CleanupFailed` retain ownership and refuse restart, so fix the failing disposer or cancellation path instead of retrying blindly. |

If the whole server exits during startup, the message `Server startup did not converge (<outcome>): <row>: <state> (<error>)` names the first rows that did not activate. Fix the first `Failed` or `Pending` entry; the rest are often consequences. More symptom-first guidance lives in [Troubleshooting](troubleshooting.md).

## Next

Continue with [SDK protocol](sdk-protocol.md) to mount a plugin written in Python, TypeScript, Go or Rust, then read [Contributing](contributing.md) for the validation gate and review rules. [Server configuration](configuration.md) explains profiles, bundles, patches and environment variables in full.

## Register commands and permissions {#register-commands}

For an explicit notice-before-disconnect operation, `ISession.SendAsync` only queues the message. If the session implements `ISessionOutputDrain`, await `DrainOutputAsync` with the operation's configured cancellation deadline before closing. The fence waits for preceding transport writes; it does not confirm client receipt or rendering. Cancellation stops waiting without retracting queued frames. A full queue, stopped session or failed write fails the drain. Do not use fixed delays or concrete protocol classes as a replacement.

The `permissions` and `commands` rows in the core bundle provide the native command foundation. A plugin declares `IPermissionRegistry` and `ICommandRegistry` from `BubbleFighter.Fantasy.Contracts.Administration` in its `Requires` list, then registers a permission before its command:

```csharp
context.Effect(context.Services.Get<IPermissionRegistry>().Register(
    new PermissionDefinition("example.ping", "Check whether the example plugin is ready.")));
context.Effect(context.Services.Get<ICommandRegistry>().Register(context,
    new CommandDefinition("ping", ["ready"], "example.ping", "Check plugin readiness.", []),
    (invocation, token) => ValueTask.FromResult(
        new CommandResult(CommandStatus.Success, "Ready."))));
```

Names and aliases are globally unique. Registration snapshots metadata. Disposal removes every alias and signals active invocations; handlers run through the registering context's supervised task API, so plugin unload cancels and drains them. Handlers must observe the supplied token. Expected application-state errors return `InvalidState`; unexpected exceptions propagate and are reported to the owning plugin. `ICommandRegistry.List(principal)` supplies permission-filtered metadata for help and panels.

`ICommandExecutor.ExecuteAsync` accepts a command name and structured positional arguments. `ExecuteTextAsync` recognizes `/`, `!` and `！`, double-quoted tokens, invariant signed integers and a final `RemainingText` argument. Structured callers supply remaining text as one value. There is no separate word-count limit on that text. Console adapters can explicitly allow input without a prefix. No input is evaluated as shell code.

Only trusted adapters construct `CommandCaller`: resolve stable account IDs through authentication and preserve the original `Actor` when acting for a different `Subject`. Never deserialize this identity from request arguments. `Language` carries the user's UI language; an empty value requests the deployment fallback. Audit events contain identity and outcome, without raw argument values. Native registration is available; remote SDK registration still needs a callback/ownership adapter. Console input and WebUI are not connected to this dispatcher yet.

Set `shared_chat_commands: true` in an `ingame` row's config to connect that gameplay scope to shared chat dispatch. This option defaults to `false` pending native-client acceptance; disabled scopes retain their original host-only `!start` and `!set` handlers. Enabling it requires both shared command services, otherwise activation fails.

The core bundle's `commands.game` row connects enabled scopes to the same dispatcher, including native plugin commands. It registers `/start` and `/mode <id>` (alias `/set`) once across gameplay scopes; `!` and `！` also work. Their `game.room.start` and `game.room.mode` permission nodes allow ordinary players by default, acting only on their own waiting room. A configured mode table still rejects unsupported IDs. Command start bypasses ready marks; the native start button retains its room-master check. Prefix input that fails permissions is consumed, with no fallback to the local command path.

Gameplay owners register `IGameCommandEndpoint` through `IGameCommandEndpoints` and own the returned handle. Routing verifies the authenticated game account against the current seat, rejects ambiguous session ownership, and runs under the gameplay owner's lifetime. `commands.game.max_endpoints` defaults to 512 and bounds live scope registrations. This row does not implement map, management or room-transfer commands.

Removing or replacing a gameplay scope invalidates earlier routing snapshots. An invocation that has not begun execution cannot call the withdrawn endpoint or silently follow its replacement.

Permission configuration is on the `permissions` row:

```yaml
config:
  groups:
    - name: players
      allow: [example.ping]
    - name: helpers
      inherits: [players]
      deny: [example.restricted]
  subjects:
    - kind: game
      id: "42"
      groups: [helpers]
    - kind: game
      id: "7"
      operator: true
    - kind: webui
      id: "admin-account-id"
      allow: [webui.access, webui.admin]
```

The IDs refer to accounts already authenticated in their respective namespace. These settings do not create accounts or grant login credentials. Ordinary denials override all ordinary grants, including inherited and wildcard grants. Rules accept exact nodes, `prefix.*` or `*`. Unknown groups and inheritance cycles fail activation. Unregistered nodes fail closed. A game OP and the local console have all registered permissions; game OP does not grant WebUI login. `webui.access` grants entry only; `webui.admin` is the WebUI superuser permission. Permissions never bypass argument or application-state checks. These WebUI nodes are provided for adapter integration; they do not yet replace existing HTTP authorization.

Resource settings protect registry storage and active work, rather than setting gameplay limits. `commands` accepts `max_commands` (512), `max_input_characters` (4096, also the registration-name/schema text budget), `max_result_characters` (16384) and `max_concurrent` (64). Calls receive `Busy` when execution capacity is occupied; no waiting queue is created. `permissions` accepts `max_nodes` (2048) and `max_policy_entries` (65536, the combined storage budget for identities, groups, inheritance links and expanded rules). These validated settings can be changed per deployment.

### Online management commands {#online-management-commands}

Contracts 2.61.0 adds native online-scope registrations. `commands.management` registers `/list`, `/notice <text>` and `/disconnect <nickname> [reason]` once; `commands.players` registers each build's authenticated player directory and configured notice encoder. Both rows are disabled by default pending native-client acceptance. Enable the management row and the selected build's player row together. The player row sits below `lobby`, requires `lobby.online_players: true`, and selects the actual protocol through its required `protocol` option. The corresponding `dialect.messages` row must enable `server_notices: true`; a missing or duplicate encoder fails activation. In a composed profile, use the expanded row IDs shown by `--dump-config`.

`/list` returns the number of distinct online accounts, including main-menu players; it does not disclose names or network addresses. `game.management.list` is allowed to ordinary players by default. `/notice` sends supplied text to every authenticated game connection, including different builds, and reports queued and failed deliveries. It requires `game.management.notice`. `/disconnect` requires `game.management.disconnect`, resolves the current nickname exactly, and rejects ambiguous matches across connections. Game OP has both administrative permissions. Game invocation also verifies that the caller's stable account still owns its connection. Chat entry requires `ingame.shared_chat_commands: true`; console and WebUI adapters remain unavailable.

The disconnect notice uses the target's `launcher_language` and includes the optional reason. It queues the notice, awaits `ISessionOutputDrain`, then closes that selected connection. Notification failure is logged and does not prevent disconnecting a still-current target; a replacement login is never followed. The result reports when notification could not complete. This guarantees neither client receipt nor rendering. Announcements report partial failures without treating a queued message as a confirmed read.

`commands.management` options are `max_scopes` (512 live registrations), `disconnect_notice_timeout_ms` (5000) and `max_notice_characters` (511). The text budget may be lowered; its upper bound follows the native notice buffer. An overlong formatted reason is rejected before any notification or close. Scope registrations are owned effects and calls run under their registering plugin's supervised lifetime. Remote SDK registration remains unsupported because it needs native owner/callback transport. These rows do not implement kick, sudo, persistent OP editing, transfers, WebUI configuration or the distribution store.


### Client plugin launch policy {#client-plugin-policy}

The Store page can select client plugin identities from the merged catalog, or add an identity manually, and edit their recommended, required or forbidden status. These rules configure compatible Launchers; client binaries are not installed on the server. Each rule can target selected exact client builds. Leaving every build unchecked applies it to all builds. Profile mode retains the original configuration, while an empty custom list explicitly clears its recommendations.

`GET` and `PUT /api/setup/client-plugins` require an authenticated WebUI account with both `webui.access` and `webui.settings.client_plugins`; `webui.admin` grants both. Game OP does not grant WebUI access. PUT uses the existing same-origin request guard and accepts `{"policies":[{"id":"example.plugin","builds":[],"required":false,"disabled":true}]}`. Send `{"policies":null}` to restore the profile policy or `{"policies":[]}` to clear it. Only store identities are accepted; rules do not contain download URLs or executable paths.

The Launcher catalog, login and refresh responses advertise `pluginRecommendations` with `id`, `builds`, `required` and `disabled`. Recommended is false/false, required is true/false and forbidden is false/true. Required and disabled rules for the same identity cannot overlap, including all-build rules. Different policies on disjoint builds remain distinct; a requirement on one build is never expanded to another build by a global recommendation. Compatible Launchers enforce launch policy; this metadata does not prove client integrity or make game login validate loaded modules.

Changes persist independently of database, account, reward and client-data settings, then use the existing supervised configuration application. HTTP 202 acknowledges admission only. Application may reconnect game services; progress and failures remain visible in the setup status. Restore or retry failed configuration through the existing setup workflow.
