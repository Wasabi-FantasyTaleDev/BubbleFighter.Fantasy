# Contributing

Read [AGENTS.md](AGENTS.md) before implementation. Make a focused change on a separate branch, explain the behavior and provide validation evidence. Update English and Chinese user documentation and changelogs together. The published site also requires its Traditional Chinese and Korean translations to stay current.

## Build and checks

Install the .NET 10 SDK. Builds must produce zero warnings; do not suppress warnings. Public types and members need meaningful XML summaries. Use declared interfaces, owned effects and bounded resources; keep packet variations in data and deployment values in validated configuration.

```sh
dotnet build BubbleFighter.Fantasy.sln -c Release
python -m unittest discover -s tools/ci -p "test_*.py"
python tools/ci/serial-gates.py --public --results-directory artifacts/validation/public-01
```

The gate additionally needs Python, Node.js/npm, Go and Rust/cargo/rustup with `wasm32-wasip1` on PATH. Public mode runs every tracked test project, excluding only `Category=LocalClientData` cases. It records that exclusion and is not the complete release gate. Every attempt needs a new results directory; populated TRX results must match passing counters with no skips.

## Complete validation

Omit `--public` to require all tests. Provision the following locally obtained client workbooks; these assets are not distributed:

| Setting or path | Required file |
| --- | --- |
| `client-data/cn-324/` | `Config/ShopItem2.xml` |
| `client-data/kr-1256/data/` | `Packs/Config_00000.pkn` (the shop and fighter workbooks) and `Packs/ConfigSh_00000.pkn` (the pet overlay) |
| `artifacts/shop-tables-survey/id400/xml/` | `Config/ShopItem2.xml` |

The complete gate builds Release, checks all four SDKs, runs test projects serially, validates packet tables and dumps isolated configuration. Release review additionally requires isolated startup and affected real-client flows. Automated checks do not replace client acceptance.

Self-hosted CI uses `BFF_CI_FIXTURE_ROOT` for the two fixture trees and the kr-1256 client-data tree. Its checkout must have no `.env` or local profile patches. The fixture helper records ownership and removes only junctions it created; it never deletes fixture data.

## Local Host checks

Follow [Quickstart](docs/quickstart.md), including the nonempty local `BFF_MYSQL_PASSWORD`. Every local `run`, including `--dump-config`, must include `--patch tools/local-checks/isolated-memory.patch.yaml`. Do not assume a developer environment points at a disposable database.

## Protocol and documentation changes

For suspected vulnerabilities, follow [the security policy](SECURITY.md) rather than opening a public issue with exploit details.

Read [packet tables](packets/README.md) and [opcode policy](profiles/opcode-policy.md) before changing layouts. Cite build-qualified client addresses, retain unknown fields and honest evidence grades, and pin the bytes in tests. Put implementation history and private material outside the published tree. Do not add credentials, personal identities, proprietary client assets or reference material.

Public guides live in `docs/`; the site uses an explicit allowlist. Follow [the site instructions](docs-site/README.md), update all four translations and add a dated entry to both changelogs. Commit source rather than generated content or build output.
