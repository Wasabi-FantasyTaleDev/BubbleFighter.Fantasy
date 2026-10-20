# Agent development guide

The binding project rules are in [AGENTS.md](AGENTS.md). Read them before changing code.

@AGENTS.md

## Working safely

Use a separate branch and worktree, preserve other contributors' changes, and review changes before merging. Never commit credentials, local overrides, proprietary assets or private materials. Local Host runs, including configuration dumps, must always include `--patch tools/local-checks/isolated-memory.patch.yaml`.

```sh
dotnet build BubbleFighter.Fantasy.sln -c Release
python tools/ci/serial-gates.py --public --results-directory artifacts/validation/public-01
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- run --profile kr-812 --patch tools/local-checks/isolated-memory.patch.yaml --dump-config
```

The public gate excludes local-data cases; [CONTRIBUTING.md](CONTRIBUTING.md) documents the complete gate and its fixtures. Use unique result directories. Never terminate unrelated processes. Automated checks do not establish native-client acceptance.

## Implementation map

- `Abstractions` and `Contracts` define shared plugin contracts.
- `Kernel` composes profiles and owns activation, service visibility, effects and teardown.
- `Host` exposes the `bff` command line.
- `src/Plugins/` implements listeners, dialects, game features, persistence, administration and execution loaders.
- `packets/` contains packet layout data and build inheritance; `profiles/` contains rows, configuration differences and opcode tables.
- `sdk/` contains the optional Python, TypeScript, Go and Rust plugin SDKs. Current SDK limitations are documented; do not claim full native parity.

Before changing wire behavior, read [packet tables](packets/README.md) and [opcode policy](profiles/opcode-policy.md). Establish layouts and semantics from build-qualified client functions; keep unknowns unknown and add byte-pinning tests. Select behavior through capabilities or build data rather than scattered version checks.

## Public content

Canonical guides live in `docs/`; the Vue 3 site publishes only its explicit page list. Update English and Chinese together, plus Traditional Chinese and Korean for site pages. See [docs-site/README.md](docs-site/README.md). Add dated bilingual changelog entries. Private investigation notes and archived documentation are excluded from publication; never copy their contents into public files.
