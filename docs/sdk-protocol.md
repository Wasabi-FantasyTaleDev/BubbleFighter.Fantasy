---
title: "SDK protocol"
description: "Connect an optional language SDK plugin to the host process, or write a native plugin in .NET."
updated: 2026-10-18
---

The server's main implementation is C# on .NET 10. The SDKs in `sdk/` are optional plugin clients for Python, TypeScript, Go and Rust: they adapt the same host contracts so a plugin can be written in another language without reimplementing the server. A process plugin connects to the Host through a bounded, newline-delimited JSON protocol on standard input and output. Go and Rust modules can alternatively run inside the Host as WebAssembly.

This page first explains what the SDKs are and are not, gives one runnable quick start per language, and shows how to test a plugin and diagnose common failures. It then documents the loader, activation, cancellation, message, service, task, resource and event contracts in detail. The SDKs are consumed from this repository; they are not installed from a package registry.

## What this page covers

- The two loaders: `loader.process` for a child process and `loader.wasm` for a sandboxed WebAssembly module.
- What an SDK plugin can do today: typed messages, declared overrides and middleware, declared service calls, providers and decorators, ordered collections, shared events, owned tasks and resources.
- How to build, run, test and mount a plugin in each bundled language.
- How protocol version negotiation, the generated contract manifest and conformance testing work.
- The failure modes of the protocol and the platform.

**Who this is for:** a developer who has read [Plugins](plugins.md) and wants to add behaviour in a language other than C#, or who needs the exact loader and protocol contract.

## Before you start

- A built checkout per [Installation](installation.md), with a terminal at the repository root.
- The SDK toolchain for the language you picked: a current Python 3 interpreter (the conformance runner invokes `python`), Node.js 20 or later with npm (`sdk/typescript/package.json` declares `node >=20`), Go 1.22 or later (`sdk/go/go.mod`), or Rust with edition 2021 support and cargo (`sdk/rust/Cargo.toml`). You need one, not all four. WebAssembly modules additionally need `wasm32-wasip1` for Rust or the `wasip1` GOOS for Go.
- For the run steps: an `.env` file with a disposable nonempty `BFF_MYSQL_PASSWORD` and the local-check patch `tools/local-checks/isolated-memory.patch.yaml`, as explained in [Quickstart](quickstart.md).
- The plugin model from [Plugins](plugins.md): plugin ids, row ids, declarations and effects.

## Quick start

The four quick starts build the same tiny plugin in each language. It logs one line when activated and handles no messages, so it runs on the administration-only `management` profile and needs no client data. The Python and Go SDKs have no third-party runtime dependencies; the TypeScript and Rust SDKs fetch their build dependencies with `npm ci` and `cargo` respectively.

| SDK | Location | Protocol |
| --- | --- | --- |
| Python | `sdk/python` | JSON-RPC over stdio, protocol v1 and v2 |
| TypeScript | `sdk/typescript` | JSON-RPC over stdio, protocol v1 and v2 |
| Go | `sdk/go` | JSON-RPC over stdio, protocol v1 and v2; `wasip1` build for `loader.wasm` |
| Rust | `sdk/rust` | JSON-RPC over stdio, protocol v1 and v2; `wasm32-wasip1` build for `loader.wasm` |

### Python

The Python SDK is the pure-standard-library package `bff_plugin` in `sdk/python`. Run its unit tests from the repository root:

```sh
python -m unittest discover -s sdk/python/tests -p "test_*.py"
```

Run the shared protocol fixtures against the Python conformance plugin:

```sh
python sdk/python/tests/run_conformance.py
```

Create `sdk/python/examples/hello.py`:

```python
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from bff_plugin import Context, plugin, run


@plugin("example.hello", version="1.0.0", description="Logs one message.")
class Hello:
    async def activate(self, ctx: Context) -> None:
        ctx.logger.info("hello from the Python SDK")


if __name__ == "__main__":
    run()
```

The loader entry starts the module from `sdk/python/examples` so that `bff_plugin` is importable. The `config` schema in the `@plugin` declaration is optional; this plugin declares none. Save this patch as `artifacts/sdk-hello.patch.yaml`:

```yaml
- insert:
    - id: hello.python
      plugin: loader.process
      lifecycle:
        definition_source_reserve: 1
        definition_reserve: 16
      config:
        plugins:
          - command: python
            args: ["-m", "hello"]
            cwd: sdk/python/examples
            env:
              PYTHONUNBUFFERED: "1"
            source_id: hello-python
    - id: hello.binding
      plugin: example.hello
```

### TypeScript

The TypeScript SDK is the local npm package in `sdk/typescript`. Install its build dependencies, build it, and run its own test script from that directory:

```sh
cd sdk/typescript
npm ci
npm test
```

`npm test` builds the package and runs the wire-format, lifetime and conformance suites. Create `sdk/typescript/examples/hello.ts`:

```ts
import { Context, plugin, run } from "../src/index.js";

@plugin("example.hello", { version: "1.0.0", description: "Logs one message." })
class Hello {
  async activate(ctx: Context): Promise<void> {
    ctx.logger.info("hello from the TypeScript SDK");
  }
}

run();
```

`npm run build` compiles it to `dist/examples/hello.js`. Save this patch as `artifacts/sdk-hello.patch.yaml`, replacing the Python patch:

```yaml
- insert:
    - id: hello.typescript
      plugin: loader.process
      lifecycle:
        definition_source_reserve: 1
        definition_reserve: 16
      config:
        plugins:
          - command: node
            args: ["dist/examples/hello.js"]
            cwd: sdk/typescript
            source_id: hello-typescript
    - id: hello.binding
      plugin: example.hello
```

### Go

The Go SDK is the `bffplugin` package in `sdk/go`. Run its tests from that directory:

```sh
cd sdk/go
go test ./...
```

Create `sdk/go/examples/hello/main.go`:

```go
package main

import (
	"bffplugin/bffplugin"
	"os"
)

type hello struct{}

func (*hello) Activate(ctx *bffplugin.Context) error {
	ctx.Logger.Info("hello from the Go SDK", nil)
	return nil
}

func main() {
	bffplugin.Register(bffplugin.Definition{ID: "example.hello", Version: "1.0.0"}, func() bffplugin.Plugin { return &hello{} })
	os.Exit(bffplugin.Run())
}
```

Build the binary into `sdk/go` and save this patch as `artifacts/sdk-hello.patch.yaml`, replacing the TypeScript patch:

```sh
cd sdk/go
go build -o hello ./examples/hello
```

```yaml
- insert:
    - id: hello.go
      plugin: loader.process
      lifecycle:
        definition_source_reserve: 1
        definition_reserve: 16
      config:
        plugins:
          - command: sdk/go/hello
            cwd: .
            source_id: hello-go
    - id: hello.binding
      plugin: example.hello
```

On Windows the output file is `hello.exe`; use `go build -o hello.exe ./examples/hello` and set `command: sdk/go/hello.exe`.

### Rust

The Rust SDK is the `bff-plugin` crate in `sdk/rust`. Run its tests from that directory:

```sh
cd sdk/rust
cargo test --release
```

Create `sdk/rust/examples/hello.rs`:

```rust
use bff_plugin::{Context, Definition, Error, Plugin, Server};

struct Hello;

impl Plugin for Hello {
    fn activate(&mut self, ctx: &Context) -> Result<(), Error> {
        ctx.info("hello from the Rust SDK");
        Ok(())
    }
}

fn main() {
    std::process::exit(
        Server::new()
            .register(
                Definition {
                    id: "example.hello".into(),
                    version: "1.0.0".into(),
                    ..Default::default()
                },
                || Hello,
            )
            .run(),
    );
}
```

Build it and save this patch as `artifacts/sdk-hello.patch.yaml`, replacing the Go patch:

```sh
cd sdk/rust
cargo build --release --example hello
```

```yaml
- insert:
    - id: hello.rust
      plugin: loader.process
      lifecycle:
        definition_source_reserve: 1
        definition_reserve: 16
      config:
        plugins:
          - command: sdk/rust/target/release/examples/hello
            cwd: .
            source_id: hello-rust
    - id: hello.binding
      plugin: example.hello
```

On Windows the binary is `hello.exe` under the same directory.

### Mount the plugin

Each language section above saved the same kind of patch: an `insert` operation that adds a `loader.process` row and an ordinary row bound to `example.hello`. Apply your language's patch with a second `--patch`; the isolated-memory patch must come first so no real database is involved:

```powershell
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- run --profile management --patch tools/local-checks/isolated-memory.patch.yaml --patch artifacts/sdk-hello.patch.yaml
```

The two startup grants on the loader row are required. `definition_source_reserve: 1` reserves one definition source for the process entry; without it, startup refuses the binding row with `references unknown plugin 'example.hello'`. `definition_reserve: 16` reserves space for the definitions that source publishes; the bootstrap loader row reserves capacity the same way. The row id is arbitrary, while `source_id` is the definition source identity and must be unique inside one loader activation; an unnamed process entry defaults to `process-<index>`. Relative `command` and `args` paths and `cwd` resolve against `base_dir`, which defaults to the host's working directory (the repository root for the command above); a bare command name is looked up on `PATH`.

The loader row must be active before the definition is published: rows bound to `example.hello` stay `Pending` until then. A packaged profile that mounts the plugin permanently can copy the same rows into its own patch file instead of passing `--patch` on the command line.

The startup log shows the loader row activating, then the SDK source reporting itself, then the binding row activating:

```text
Row 'hello.python' activated (plugin 'loader.process' version 1.0.0+<commit>; contracts ...; builds independent).
SDK plugin source 'hello-python' runs SDK {"name":"...","version":"..."}.
Row 'hello.binding' activated (plugin 'example.hello' version 1.0.0; contracts ...; builds independent).
hello from the Python SDK
```

**Check it worked:** the loader row and the binding row both appear, the plugin's message is logged, and `GET /api/plugins` shows `hello.binding` in state `Active` while the loader row is `Active` too. If the binding row is `Failed` or stays `Pending`, its `error` field carries the SDK or configuration error. Press Ctrl+C to stop; the loader stops the child process after every row it serves has drained.

This quick start passes a relative `command` path and `cwd` so it works from the repository root. A WebAssembly plugin uses a `loader.wasm` row instead, with a `module` entry: the module is a WASI command exporting `_start`, it imports `bff.recv`, `bff.send` and `bff.log`, and it receives no arguments, environment, files or stdio. Its linear memory is capped by `max_memory_bytes` (default 64 MiB). Build the Go module with `GOOS=wasip1 GOARCH=wasm` and the Rust module for the `wasm32-wasip1` target.

A plugin that registers message handlers additionally needs a game profile whose typed dialect provides the inbound binding for that protocol and message type. The bundled echo examples declare `handles: ["LoginRequested"]` and `supported_builds: ["kr:812"]`, so mount them on a `kr-812` profile that has client data rather than on `management`.

## Capability negotiation and compatibility

The Host opens the conversation with one `initialize` request and the SDK answers with the protocol version it selected. The parameters carry everything a plugin needs to decide what it may declare:

| Initialize field | Meaning |
| --- | --- |
| `protocol_version`, `protocol_versions` | Legacy single version and the full offered list; the Host offers `1` and `2` |
| `features` | Host notifications and callbacks implemented by this build, including `lifecycle.cancel` |
| `max_in_flight` | Bound on outstanding requests in one direction |
| `max_reentry_depth` | Bound on nested host-request frames for a single-threaded module |
| `host_version`, `contracts` | Host release and the shared contract assembly versions it serves |

Protocol v1 covers the original surface: `initialize`, `describe`, `activate`, `deactivate`, `deliver`, `messages.on`, `messages.override`, `messages.send`, `effect.dispose` and `log`. Protocol v2 adds service calls and providers, ordered collections, events, tasks, resources, message middleware and the early `lifecycle.cancel` notification with `lifecycle.ready`. A declaration outside the negotiated version fails instead of being ignored: the host reports `provides, provides_many and decorates require protocol v2.`, `requires_many and optional_many require protocol v2.`, `event declarations require protocol v2.` or the SDK rejects the unknown method.

A missing host feature is not an error by itself. Check the documented capability flag before depending on it, for example `ctx.supports_lifetime_cancellation` in Python, `ctx.supportsLifetimeCancellation` in TypeScript, `ctx.SupportsLifetimeCancellation()` in Go and `ctx.supports_lifetime_cancellation()` in Rust. Older hosts keep working through the local rollback, deactivation and transport-loss paths, but they cannot deliver cancellation before deactivation.

The `describe` reply is validated against the shared contracts. Declaration names must resolve to exactly one message record in the shared contract assembly, service names to exactly one interface from the shared abstractions or contracts, and families to `legacy` or `modern`. An unknown name fails the whole source with an error that lists the allowed contracts, so a typo is caught when the row activates, not when a message arrives.

### Inspect the contract manifest

Generate the manifest for the exact checkout:

```sh
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- contracts schema --out artifacts/contracts.json
```

Without `--out` the manifest is written to standard output; for the `contracts` command, logging goes to standard error so the document stays clean. The manifest describes what an SDK plugin may declare and call:

| Manifest field | Content |
| --- | --- |
| `schema_version` | Manifest format version |
| `runtime_protocol_versions`, `runtime_features` | Protocol versions and the feature list the Host advertises |
| `messages`, `events`, `event_result_types` | Typed message records, event payloads and event result types with body shapes |
| `services` | Service interfaces with their operations, parameters and result shapes |
| `types` | Referenced record and enum shapes |

Every service operation carries `rpc_shape` and `provider_rpc_shape`. `rpc_shape` is `supported` when an SDK plugin may call the operation, and `provider_rpc_shape` is `supported` when an SDK plugin may implement it; each unsupported entry carries `unsupported_reason` or `provider_unsupported_reason`. Use the manifest instead of guessing: a provider must implement its whole interface, using unambiguous method names or the full schema method id for overloads, and property accessors use names such as `get_ReplyAfterInventory`.

## Testing your plugin

The repository ships one shared conformance runner and one fixture format used by every SDK. From the repository root:

```sh
python sdk/run-conformance.py
```

The runner builds and unit-tests each SDK whose toolchain is present, then plays the shared fixtures in `tests/fixtures/sdk-protocol` and the protocol-v2 fixtures against that SDK's plugin. It prints `PASS`, `FAIL` or `SKIP` plus a one-line summary per suite, and exits nonzero if any suite fails; a skipped language because its toolchain is missing is not a pass. This is the compatibility reference for the current protocol.

A fixture is a JSON-lines file. The first line is a header with an `ignore` list of dotted paths removed from both sides before comparison. Every later line is `{"dir": "h2p", ...}` for a host-to-plugin message, `{"dir": "p2h", ...}` for the plugin's expected reply, or `{"expect_exit": N}`. The runner starts a fresh plugin process, writes each host message to its standard input, compares each plugin line exactly with the expected reply after applying the ignore list, and requires a clean exit. Identities and handles are compared exactly. Run your own plugin through the same fixtures with the shared Python runner:

```sh
python sdk/python/tests/run_conformance.py --fixtures <fixture-dir> --command <plugin-executable> [args...]
```

**Check it worked:** the runner ends with a `<passed>/<total> fixtures passed` line and exits 0; a mismatch prints the expected and actual JSON for the first failing line plus the plugin's standard error.

## Common failures

| Symptom | Check | Likely cause and fix |
| --- | --- | --- |
| The source fails with `answered initialize without a supported protocol_version (1 or 2).` | The `protocol_versions` answer of your runtime | A hand-written runtime must choose `1` or `2` from the offered list. The bundled SDKs always answer a supported version. |
| `stdout carried a line that is not JSON ...; stdout is reserved for the protocol` | Anything the process writes to standard output | Standard output is the protocol channel. Python redirects `print` and TypeScript redirects `console.*` to standard error; in Go and Rust write diagnostics to standard error or through the SDK logger, never to standard output. |
| Start fails with `SDK plugin process '<command>' could not start in '<cwd>'` | `command`, `args`, `cwd` and `PATH` | The executable or working directory is wrong. A path with a directory separator resolves against `base_dir`; a bare name needs `PATH`. |
| The row fails with `requires_many and optional_many require protocol v2.` or a similar v2 message | The negotiated protocol version and declared fields | The declaration needs protocol v2. Negotiate v2 or remove the declaration; the error names the field family. |
| A message handler fails with `protocol '<p>' has no inbound binding for '<type>'` | The profile's dialect rows | The typed dialect for that protocol and message type must be active in the same profile; the SDK cannot decode packets itself. |
| A callback times out: `did not answer '<method>' within <n> ms (request_timeout_ms).` | Whether the plugin blocks, and whether the change may have committed | A timeout faults the activation and the request outcome is unknown. Reconcile from a read before retrying; do not retry a mutation blindly. |
| A `.wasm` module fails with `exports no '_start'; build it as a WASI command` | The build target and exports | Build for `wasm32-wasip1` (Rust) or `GOOS=wasip1 GOARCH=wasm` (Go); the module must be a WASI command. |
| The row fails with `Invalid configuration for row '<row>': unknown key '<key>'` | The row's `config` against the declared schema | The host applies defaults and validates types; an undeclared key is rejected. Update the declaration or the row. |

## Choose a loader

Use `loader.process` when the plugin is an executable or an interpreter entry point. The Host starts it, negotiates protocol version 1 or 2 through `initialize`, and owns the process lifetime. Use `loader.wasm` for an SDK WebAssembly module; it exchanges the same JSON protocol through the `bff.recv`, `bff.send` and `bff.log` imports. Configure the loader source and mount the discovered plugin definition as a separate profile row.

Both loaders publish their definitions as owned definition sources, so a row bound to an SDK plugin activates exactly like a native row, ordered by declared services. One process entry can describe several plugins, up to its `max_definitions` reservation; the loader stops the process only after every row it serves has drained, and a reload of an SDK row asks the same process for a fresh activation rather than restarting it.

## Activation and ownership

The Host sends `activate` with an opaque activation id, the row id, plugin id, validated configuration and a reload flag. Contract declarations are read earlier through `describe`. The activation becomes addressable before the activation callback returns, allowing the callback to register handlers and call host services. Host-side timeouts bound request waits. Subscriptions and other owned resources must be released during deactivation. Incomplete cleanup is reported; native code must cooperate with cancellation.

## Activation cancellation

Each activation exposes a read-only lifetime signal. It is cancelled when that row starts stopping, before its admitted callbacks have drained. It is also cancelled before the SDK rolls back a failed activation, runs a deactivation hook, or stops on transport loss. Stopping one row does not cancel other rows sharing its process. A reload creates a new signal.

| SDK | Observe cancellation | Check early notification support |
| --- | --- | --- |
| Python | `await ctx.lifetime.wait()` / `ctx.lifetime.is_set()` | `ctx.supports_lifetime_cancellation` |
| TypeScript | `ctx.lifetime` (`AbortSignal`) | `ctx.supportsLifetimeCancellation` |
| Go | `ctx.Lifetime.Done()` / `ctx.Lifetime.Err()` | `ctx.SupportsLifetimeCancellation()` |
| Rust | `ctx.lifetime().is_cancelled()` | `ctx.supports_lifetime_cancellation()` |

The host advertises `lifecycle.cancel` in the protocol-v2 `initialize.features` list. After installing its activation state, the SDK calls `lifecycle.ready` with `{ "activation": "opaque-id" }` before entering the plugin's activation hook. The host then owns one cancellation observer through its native task manager and shared effect capacity. At stop it sends the `lifecycle.cancel` notification with the same activation id. Repeated readiness/cancellation is idempotent; notifications for unknown or removed activations are ignored rather than retained.

Older hosts remain compatible, but cannot provide the early notification. Check the capability above if work depends on cancellation arriving before deactivation. Local rollback, deactivation and transport loss still cancel the SDK signal. Go/Rust WebAssembly processes notifications while reading host traffic; blocking its single execution thread on a local wait prevents incoming notifications from being processed. Use cooperative host calls or the process loader for independently running work.

Cancellation asks work to stop; it does not prove that work has finished. Continue owning registration handles and await actual cleanup.

## Typed message handlers and declared overrides

Declare inbound message contracts in `handles` and register them with `ctx.messages.on`. A plugin that must replace a specific existing handler declares its exact `rowId:fully.qualified.MessageType` target in `overrides`, then calls the SDK's `messages.override` API. The host validates the declaration, the protocol binding and the currently selected target before publishing the replacement. Removing the returned handle restores the preceding handler and remains part of the activation's reverse effect order. Overrides are explicit dependencies: a missing target or a replacement that does not install fails activation instead of silently changing dispatch order.

| SDK | Base registration | Declared replacement |
| --- | --- | --- |
| Python | `await ctx.messages.on(protocol, type, handler)` | `await ctx.messages.override(protocol, type, target_row_id, handler)` |
| TypeScript | `ctx.messages.on(protocol, type, handler)` | `ctx.messages.override(protocol, type, targetRowId, handler)` |
| Go | `ctx.Messages.On(protocol, type, handler)` | `ctx.Messages.Override(protocol, type, targetRowID, handler)` |
| Rust | `ctx.on(protocol, type, handler)` | `ctx.override_handler(protocol, type, targetRowId, handler)` |

The callback receives the same session and decoded message body as a base handler. The wire protocol remains owned by the host's typed dialect layer; SDK handlers cannot read or write packet opcodes directly.

## Message middleware and one-shot continuations

Protocol v2 SDK plugins can wrap a typed message pipeline with the same ordering and lifetime rules as native message middleware. List the short message type in `middleware`, then call `messages.use`. Higher priorities run outermost; equal priorities retain registration order. The callback receives the session, decoded body, a single-use `next` continuation and the invocation cancellation signal. Returning without calling `next` short-circuits the downstream handler. A downstream typed handler must be selected for the message before the middleware pipeline is entered.

| SDK | Registration |
| --- | --- |
| Python | `await ctx.messages.use(protocol, type, middleware, priority=0)` |
| TypeScript | `ctx.messages.use(protocol, type, middleware, priority)` |
| Go | `ctx.Messages.Use(protocol, type, priority, middleware)` |
| Rust | `ctx.use_middleware(protocol, type, priority, middleware)` |

The host owns the registration returned by `messages.use`; pass it to the activation effect scope. A dispatch is admitted as one bounded `messages.invoke` callback. If the SDK calls `next`, it sends `messages.next` with the invocation id and the host retains the native continuation until the downstream pipeline completes. Calling `next` twice, calling it after the callback returns, or using an invocation id from another activation fails with an invalid-params error; the continuation is single-use and cannot escape its invocation. The host sends `messages.cancel` when the invocation or activation stops; cancellation requests cooperation and does not discard already admitted downstream work.

Middleware registration and continuation calls require protocol v2. The native router, typed dialect and SDK loaders therefore share one message pipeline: packet framing and field meanings stay in the host, while policy and game logic can be implemented in any supported plugin language.

## Native-owned tasks

Protocol-v2 SDKs can start background work through the activation's native task owner:

```python
task = await ctx.tasks.run("refresh-cache", work)
ctx.effect(task)
```

`ctx.tasks.run` reserves the same bounded activation capacity as native `RunTaskAsync`. The host sends `tasks.invoke` to run the registered callback, and sends `tasks.cancel` when the task is being stopped. `TaskHandle.wait` observes the callback result and does not release ownership. `TaskHandle.dispose` requests cancellation, joins the callback and native cleanup, and releases the task owner. If disposal is requested from the callback itself, the host defers the parent-scope release to avoid a self-wait deadlock. Normal completion, cancellation and faults remain distinct outcomes; a fault keeps ownership until teardown reports it.

The equivalent entry points are `ctx.tasks.run`/`wait`/`dispose` in Python and TypeScript, `ctx.Tasks.Run`/`Wait`/`Dispose` in Go, and `Context::run_task`/`TaskHandle::wait`/`dispose` in Rust. Register every handle as an effect. Go and Rust WebAssembly remain cooperative: the module must return to Host communication to receive cancellation.

## Native-owned resource scopes

Protocol-v2 SDKs can create bounded native resource scopes and acquire resources whose release callback remains in the SDK process:

```python
scope = await ctx.resources.scope("cache")
ctx.effect(scope)
resource = await scope.acquire("cache-entry", release)
ctx.effect(resource)
```

`create` makes a child scope, and `acquire` reserves one native effect slot. Scope and resource handles are activation-owned and must be registered as effects. During deactivation, the host may return `{ "deferred": true }` while the parent native scope is unwinding; the SDK keeps the callback registered until the host invokes and completes it. A release callback receives a cancellation signal, and the host retains native ownership until that callback returns. The callback must not synchronously dispose the same resource. Python, TypeScript, Go and Rust expose the same scope/acquire/dispose shape; Go and Rust WebAssembly callbacks must return to the host loop to receive cancellation.

## Service calls

The current protocol exposes typed messages, logging, effects and the explicitly listed host service calls. Contracts are granted by the plugin declaration. Generate the inventory for the exact checkout with:

```sh
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- contracts schema --out artifacts/contracts.json
```

Use `rpc_shape` to check callable operations and `provider_rpc_shape` to check provider operations. A provider must implement every method, using unambiguous method names or full schema method ids for overloads. Property accessors use names such as `get_ReplyAfterInventory`. Unsupported shapes fail explicitly. Delegates, reference parameters, general resource adapters, packet codecs and provider methods receiving `IPluginContext` are not covered by this service bridge.

## Provide and decorate services

These registrations require protocol v2 and participate in the native dependency graph:

| Declaration | Registration mode | Behavior |
| --- | --- | --- |
| `provides` | `singleton` | One implementation, without decorator layers |
| `provides` | `decoratable` | One implementation that accepts declared decorator layers |
| `provides_many` | `many` | Ordered implementations; higher priority first, registration order for ties |
| `decorates` | `decorate` | A layer around the selected inner service; the base must allow decoration |

For example, a Python row can decorate a native policy. Mount it alongside a base provider registered as decoratable:

```python
from bff_plugin import Context, ServiceCall, plugin, run

@plugin("example.inventory-policy", version="1.0.0",
        decorates=["IEquipmentReplyPolicy"])
class InventoryPolicy:
    async def activate(self, ctx: Context) -> None:
        async def reply_policy(call: ServiceCall):
            return await call.inner()

        ctx.effect(await ctx.services.decorate("IEquipmentReplyPolicy", {
            "get_ReplyAfterInventory": reply_policy,
        }, priority=10))

if __name__ == "__main__":
    run()
```

The example preserves the inner result. A policy may instead return its own valid result. The inner service belongs to that invocation and retains its selected decorator chain; do not save it for later calls. The host rejects foreign or completed invocation tokens. Removing a registration does not invalidate projections already held by consumers: callback targets remain owned until the provider activation ends.

| SDK | Register / decorate / select all |
| --- | --- |
| Python | `ctx.services.provide` / `decorate` / `get_all` |
| TypeScript | `ctx.services.provide` / `decorate` / `getAll` |
| Go | `ctx.Services.Provide` / `Decorate` / `GetAll` |
| Rust | `ctx.provide_service` / `decorate_service` / `get_all_services` |

## Ordered collections and cancellation

Declare `requires_many` to require at least one implementation, or `optional_many` to accept an empty collection. TypeScript spells these options `requiresMany` and `optionalMany`; Go uses `RequiresMany` and `OptionalMany`. A collection returns a count and supports calls by zero-based index. The selected identities and order remain stable until disposal. Collection handles belong to one activation and share its bounded handle budget. Register their disposal as an effect; Rust collections use `effect_fn`.

Each provider callback receives cooperative cancellation: Python exposes `call.cancellation`, TypeScript `call.signal`, Go `call.Context`, and Rust `call.is_cancelled()`. Return promptly after cancellation. WebAssembly processes notifications while servicing host calls; a busy computation must not assume a background cancellation thread. A deadline faults the activation, but admitted work remains owned until its actual response, native completion or transport termination. A returned callback still waits for already admitted inner calls to finish.

## Shared events

Protocol v2 routes SDK events through the activation's native event bus. Native and SDK listeners participate in the same priority order and dispatch snapshot. Declare `subscribes` and `publishes` separately, using an event `type`, an explicit `mode`, and a `result_type` for result-producing modes. Read event identities from `events` and result identities from `event_result_types` in the generated inventory. Use a fully qualified name if a short name is ambiguous. Types containing native connections, delegates or unsupported values need a dedicated adapter and are rejected during declaration.

| Publication mode | Listeners and behavior |
| --- | --- |
| `emit` | Synchronous listeners only; listener errors are logged and dispatch continues |
| `serial` | Synchronous and asynchronous listeners, awaited in order |
| `parallel` | Synchronous and asynchronous listeners; asynchronous work is awaited together |
| `waterfall` | Interceptors wrap a terminal callback and may return a replacement result |
| `bail` | First non-null reference result wins; null continues and handler errors are logged |

Subscription modes are `sync`, `async`, `waterfall` and `bail`. Higher priorities run first; ties keep registration order. In `serial` and `parallel`, synchronous listener errors still follow the native log-and-continue rule; asynchronous failures propagate, except cancellation caused by removal of that listener. Choose the publication mode the event's native caller actually uses. Notification listeners return no value; waterfall returns the declared non-null result; bail alone permits null to continue.

This Python policy observes the existing equipment decision pipeline without changing its result. Mount it in a profile that publishes the equipment selection event:

```python
from bff_plugin import Context, EventSpec, plugin, run

selection = EventSpec("EquipmentSelectionApplying", "waterfall",
                      "EquipmentSelectionDecision")

@plugin("example.selection-policy", version="1.0.0", subscribes=[selection])
class SelectionPolicy:
    async def activate(self, ctx: Context) -> None:
        async def inspect(call):
            decision = await call.next()
            ctx.logger.info(f"Selection allowed: {decision['allowed']}")
            return decision

        ctx.effect(await ctx.events.on(selection, inspect, priority=10))

if __name__ == "__main__":
    run()
```

`call.next()` invokes the already selected downstream pipeline. It may be called repeatedly while the callback is live, matching native behavior; each call can repeat downstream work. Return your own valid decision to short-circuit. Event data crosses the boundary as a value: mutating the local body does not mutate the publisher's object. The event/result contract, rather than a shared object reference, expresses policy changes.

| SDK | Subscribe / publish | Result declaration |
| --- | --- | --- |
| Python | `ctx.events.on` / `ctx.events.publish` | `EventSpec(type, mode, result_type)` |
| TypeScript | `ctx.events.on` / `ctx.events.publish` | `{ type, mode, resultType }` |
| Go | `ctx.Events.On` / `ctx.Events.Publish` | `EventSpec{Type, Mode, ResultType}` |
| Rust | `ctx.on_event` / `ctx.publish_event` | `EventSpec::new(type, mode).with_result(result_type)` |

Waterfall publication requires a terminal callback returning the declared result; other modes reject a terminal. Register every subscription handle as an effect. Inside a listener, use `call.dispose(handle)` (Go: `call.Dispose(handle)`) to remove its own registration: this restores the native callback context and avoids waiting on itself. An external removal still waits for admitted work. Continuations and self-disposal tokens belong to one live invocation; foreign or completed tokens fail.

Event callbacks expose the same cooperative cancellation APIs as service callbacks. Cancellation requests a stop; actual completion releases ownership. A timeout faults the row, while admitted native work remains owned until it finishes. Publication keeps its terminal alive until native dispatch completes, even if a Python caller cancels its await. Synchronous and bail listeners perform a blocking RPC on the publisher's path, so keep them short. Provider registrations, retained event callbacks and temporary terminals share the activation's bounded callback budget (`max_provider_callbacks`, granted from the row's `effect_capacity`). Removed listener callbacks stay retained until activation teardown; failed registration reservations and completed temporary terminals are released.

## Reliability

Requests can time out after the host has committed a mutation. Treat a timeout as an unknown outcome and reconcile from a read operation before retrying. Include a request id in logs, stop sending after deactivation, and handle a host disconnect as a terminal activation failure.

The SDK examples under `sdk/` are the compatibility reference for the current protocol. They are not a substitute for a profile's capability and permission declarations.
