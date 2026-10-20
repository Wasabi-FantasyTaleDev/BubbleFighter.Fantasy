---
title: "Contributing"
description: "Set up the toolchain, build and test, follow the project rules, and submit a reviewable change."
updated: 2026-10-18
---

## What this page covers

This page takes a new contributor from a clean machine to a change that can be reviewed and merged: the rules to read first, the toolchain, the build and validation commands, the repository map, the obligations a change carries, and the review process. Bubble Fighter Fantasy is a prerelease community server maintained by Fantasy Tale Studio; it is written in C# on .NET 10 and composed from plugins.

**Who this is for.** First-time contributors, and anyone who needs to reproduce or review another contributor's change.

**Before you start.**

- Read `AGENTS.md` and `CONTRIBUTING.md` at the repository root. `AGENTS.md` is binding: it holds the architecture, packet-data, evidence, naming and language rules.
- Install the .NET 10 SDK and Git; you need both to build and run the server.
- A real game client and its data are needed only for native-client testing. The repository ships neither.

## Read the rules first

| Rule | What it means in practice |
| --- | --- |
| Everything is a plugin | New capability ships as a plugin row in a profile, not as a special case in the kernel or host. |
| Context is the service registry | Consume services by declared interface type; never reach into another plugin's classes. |
| Registrations are effects | Every `Register*`/`On*` returns a disposer; hand it to `context.Effect(...)` so unload unwinds in reverse order. |
| No hard-coded tunables | Deployment-varying values are validated config fields on a plugin row, not constants. |
| Fail loud | Misconfiguration, missing services, malformed packets and connection failures are errors, never silent defaults. |
| Bounded resources | No unbounded queues, buffers or file sinks. Overflow is reported and never stalls gameplay. |
| Packet layouts are data | Structures live in `packets/`, written once for the baseline and annotated per build; code supplies values by field name. |
| Evidence comes from the client | Layouts and ordering are established by reading the client's own code; every claim keeps its build-qualified function address. |
| English in the code | Code, comments, XML docs, commit messages, `opcodes.yaml` and `profiles/opcode-policy.md` are English. |
| Bilingual user documentation | Update the English guide and its Chinese translation together. Site pages also need Traditional Chinese and Korean. |

No emoji anywhere. Do not cite or name third-party projects, other servers or reference trees; describe designs in this project's own terms.

## Set up the toolchain

| Tool | Needed for | Check |
| --- | --- | --- |
| .NET 10 SDK | building and running the server | `dotnet --version` reports 10.x |
| Git | cloning, branches, the validation gate | `git --version` |
| Python 3 | CI gate scripts and their unit tests | `python --version` |
| Node.js and npm | documentation site, TypeScript SDK conformance | `node --version`, `npm --version` |
| Go | Go SDK conformance and tests | `go version` |
| Rust with cargo and rustup, plus `wasm32-wasip1` | Rust SDK conformance and WebAssembly tests | `rustc --version`; `rustup target add wasm32-wasip1` |

Only .NET and Git are required to build and run the server. The other toolchains are needed by the validation gates, the documentation site and the SDK suites; the gate reports them as missing rather than skipping silently.

In PowerShell or a shell, from the directory that should hold the checkout:

```sh
git clone https://github.com/Wasabi-FantasyTaleDev/BubbleFighter.Fantasy.git
cd BubbleFighter.Fantasy
dotnet build BubbleFighter.Fantasy.sln -c Release
```

**Check it worked.** The clone directory contains `BubbleFighter.Fantasy.sln`, and the build ends with `0 Warning(s)` and `0 Error(s)`.

## Build and test locally

### Build the solution

```sh
dotnet build BubbleFighter.Fantasy.sln -c Release
```

Warnings are errors (`TreatWarningsAsErrors` is on), and `GenerateDocumentationFile` requires a meaningful XML summary on every public type and member. Fix the cause; never suppress a warning to get a green build.

### Run the test projects

```sh
dotnet test BubbleFighter.Fantasy.sln -c Release
```

This is the fast loop while developing. The release gate does not use it directly: it discovers every tracked test project with `git ls-files tests/*/*.csproj` — including projects outside the solution — and runs them serially with one TRX per project. Give every attempt its own results directory or file name so a rerun cannot overwrite an earlier failure.

### Run the gate self-tests

```sh
python -m unittest discover -s tools/ci -p "test_*.py"
```

These tests guard the gate runner itself against false success from empty, missing or skipped reports.

### Run the public gate

```sh
python tools/ci/serial-gates.py --public --results-directory artifacts/validation/public-01
```

Use a new, non-existent results directory for every attempt; the runner refuses an existing one. It builds Release, runs SDK conformance, runs each test project serially, checks the packet tables and dumps the isolated `kr-1256` configuration. It ends with `PASS public gates: <n> populated test reports, <n> tests, no skips` and records the exclusion in `scope.json`. Public mode runs every tracked test project but excludes only `Category=LocalClientData` cases; it is not the complete release gate. If `CI=true` is set, the runner also refuses a checkout that contains `.env` or `profiles/*/local.patch.yaml`.

**Check it worked.** The exit code is 0 and the final line is the `PASS` line above. A nonzero exit prints `FAIL <reason>`; the log of the failing step is in the results directory.

### Run a local Host check

Follow [Quickstart](quickstart.md): copy `.env.example` to `.env`, set `BFF_MYSQL_PASSWORD` to a nonempty disposable value, and always add the isolated patch — including for `--dump-config`:

```sh
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- run --profile management --patch tools/local-checks/isolated-memory.patch.yaml
```

The patch disables `persistence.mysql` and enables in-memory persistence so a developer `.env` or local override cannot contact a live database. Never point tests or local checks at a production database. **Check it worked:** another terminal gets `{"status":"ok", ...}` from `http://127.0.0.1:8080/health`; stop with Ctrl+C.

## Repository map

| Path | What belongs there |
| --- | --- |
| `src/BubbleFighter.Fantasy.Abstractions`, `src/BubbleFighter.Fantasy.Contracts` | shared plugin contracts; loaded once by the host and shared with every plugin |
| `src/BubbleFighter.Fantasy.Kernel` | plugin runtime: discovery, activation, service registry, effects, profile composition |
| `src/BubbleFighter.Fantasy.Host` | the `bff` console host and its bootstrap profiles |
| `src/Plugins/` | listeners, dialects, game features, persistence, administration and SDK loaders |
| `profiles/` | profile rows, bundles, patches and per-build opcode tables |
| `packets/` | packet layout data and per-build inheritance |
| `sdk/` | optional Python, TypeScript, Go and Rust plugin SDKs |
| `tests/` | .NET test projects and shared fixtures |
| `tools/` | validation gates (`tools/ci`), local-check patches (`tools/local-checks`) and maintenance scripts |
| `docs/` | source of the published guides |
| `docs-site/` | VitePress site, page allowlist and content sync |
| `deploy/` | Docker files and Compose templates |

## Making a change

### Branch and split the work

Work on your own branch and keep one logical change per commit; the change lands on `main` only after review. Write commit messages in English: what changed, why, and what evidence supports it.

```sh
git switch -c fix/short-topic
```

### Provide evidence

- Add or update tests with the change. For packet work, add byte-pinning tests that fail before the change and pass after it.
- Run the targeted tests, then the full build and test run. Keep the failing output of any earlier attempt; do not overwrite it with a rerun.
- Cite evidence the way the code does: a build-qualified client function address for a wire claim (`kr-812 sub_810C20`). An opcode row with the observed-session evidence grade names its session in the row, not in code comments.
- In the change description, state what you ran and what it proves; automated tests do not establish real-client acceptance.

### Follow the plugin and resource rules

- Add capability as a plugin row: declare required and provided interfaces in `[Plugin(...)]`, consume them through `IPluginContext`, and register only through it.
- Own every registration and external resource with an effect so unloading unwinds it; never leave a background task or queue unbounded.
- Put deployment-varying values in a validated option class on the row (`[Required]`, `[Range]`, `IValidatableOptions`), not in constants.
- Build against the shared contract assemblies; do not ship private copies of `Abstractions` and `Contracts` with a plugin.
- Keep public types and members documented with a real XML summary. Write comments that explain why and point at the evidence.

### Change packet or opcode data

Read `packets/README.md` and `profiles/opcode-policy.md` before touching a layout. In short:

- Write each structure once as the `kr-812` baseline has it, one named field per line with its client offset; a build that differs annotates the row (`except`, `only`, `also`, or its own replacement). Never add a per-build field table or `if (build ...)` to code for something the tables can say.
- A field without a proven purpose is `unknown_<offset>`; do not invent a meaning. Every `sub_` address is prefixed with its client build.
- Opcode rows record `verified`, `inferred`, `unverified` or `observed-session` evidence and a `name_source`; unknown inbound opcodes are logged and never parsed optimistically. C2S and S2C, and game and relay connections, are separate spaces.
- Every codec needs a byte-length-pinning test, and a wire change is checked against the client's consuming path before it is committed.
- Confirm the tables with `bff packets show <build> <structure>`, `bff packets diff <build> <build>` and `bff packets check`.

## Documentation changes

`docs/` is the source of the published guides; `docs-site/` builds them through the explicit page list in `docs-site/pages.json`. A published page exists in four files — `X.md` (English, canonical), `X.zh.md`, `X.zh-tw.md` and `X.ko.md` — and every file needs front matter with `title`, `description` and an `updated` date.

- Local links must point at a published page source, for example [Configuration](configuration.md). External URLs and anchors are fine.
- Do not edit generated content under `docs-site/content/` or commit build output; both are ignored.
- Update the English and Chinese guides together, and keep all four translations of a site page current.
- Add a dated entry to both root changelogs (`CHANGELOG.md` and `CHANGELOG.zh.md`) in the same commit; dates are `YYYY-MM-DD` and supplied times use Asia/Shanghai. New entries go above older ones.

Build the site in its own directory:

```sh
cd docs-site
npm ci
npm test
npm run build
```

The sync step runs before the build and validates metadata and links. It fails with `Missing guide metadata: <path>` when front matter is incomplete, `Missing public guide: <path>` when one of the four files is absent, and `Unpublished local link: <target>` when a local link does not name a published page. Use the other published guides, such as [Configuration](configuration.md) and [Plugins](plugins.md), as examples.

## Validation levels

| Level | What runs | Needs | What it proves |
| --- | --- | --- | --- |
| Quick | `dotnet build` and `dotnet test` | .NET | the change compiles and the .NET suites pass |
| Public gate | `tools/ci/serial-gates.py --public` | .NET, Python, Node.js/npm, Go, Rust with `wasm32-wasip1` | every tracked test project except local-data cases, SDK conformance, packet tables, isolated configuration |
| Complete gate | the same command without `--public` | the locally provisioned client workbooks below | every test, no skips; this is the release gate |
| Release review | isolated startup and affected real-client flows | a real game client | client acceptance; no automated check replaces it |

The complete gate needs these locally obtained client files; they are not distributed with the repository:

| Setting or path | Required file |
| --- | --- |
| `client-data/cn-324/` | `Config/ShopItem2.xml` |
| `client-data/kr-1256/data/` | `Packs/Config_00000.pkn` (the shop and fighter workbooks) and `Packs/ConfigSh_00000.pkn` (the pet overlay) |
| `artifacts/shop-tables-survey/id400/xml/` | `Config/ShopItem2.xml` |

If the files are absent, the complete gate prints the missing prerequisites and stops with `Local game data is missing for <tests>; provision it or pass --public to run the publicly reproducible gates only.`

Report validation honestly:

- Every attempt uses a new results directory; a populated TRX must match passing counters with no failed, empty or skipped results. A skipped test is not a pass.
- Report the mode you ran. The public gate records its exclusion in `scope.json` and is not the complete release gate.
- Do not delete or rewrite a failing result. Keep the console log and the TRX of a failed attempt.
- Say whether a result came from an automated check or a real game client.

The public repository workflow runs the publicly reproducible gate on a GitHub-hosted Windows runner for pushes and pull requests to `main`; it does not run local-data cases. Run the complete gate locally when you have the private client-data prerequisites, and keep `.env` and local profile patches out of CI checkouts.

## What a reviewable change contains

- A focused diff that does one thing, with tests for the behaviour it changes.
- Validation evidence: the commands, the results, and the failing output before the fix when there was one.
- Data and documentation updates that the change requires: packet tables for wire layouts, opcode rows with evidence, English and Chinese guide updates, and changelog entries.
- No unrelated reformatting, generated files, build output, logs or local overrides.

## What is rejected

- Credentials, tokens, `.env` files, local profile patches, or any secret in code, docs or logs.
- Private investigation material, packet-recording details, client exports or client assets.
- Third-party code, copied layouts or named references to other projects and servers.
- Guessed packet names or field meanings, or handlers enabled on `unverified` evidence.
- Unbounded queues, buffers or file sinks, and deployment values hard-coded instead of validated configuration.
- A build with suppressed warnings, or a skipped test reported as a pass.
- Generated documentation content, build output, or a feature from an unmerged branch described as available.

## Submitting

1. Push your branch and open a pull request against `main`.
2. Describe the behaviour change, the evidence and the validation mode you ran; link the failing output if the change fixes a bug.
3. Make sure the diff contains no secrets, private material or generated content.
4. Answer review comments with another commit or a clear explanation; keep the history reviewable.
5. After review, the change is merged into `main`. Watch the next serial validation run, because the workflow runs there.

## Next

- The project's GitHub repository is the entry point the README documents: https://github.com/Wasabi-FantasyTaleDev/BubbleFighter.Fantasy. The repository documents no chat server or forum.
- [Configuration](configuration.md) and [Plugins](plugins.md) explain the structures a change usually touches.
- [SDK protocol](sdk-protocol.md) covers the optional language SDKs, and [Troubleshooting](troubleshooting.md) helps when a build or gate fails.
