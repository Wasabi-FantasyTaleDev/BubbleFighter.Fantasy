---
title: "Self-hosted stores"
description: "Prepare and publish Launcher clients, profiles, and plugins within the actual tool limits."
updated: 2026-10-22
---

# Self-hosted stores {#self-hosted-stores}

## Scope and prerequisites {#prerequisites}

The examples, field names, and limits below match this [Launcher source snapshot](https://github.com/Wasabi-FantasyTaleDev/FantasyTale.Launcher/tree/5f1d4267d0ddd68390dc27fe12f678b06b846e6e). Use its tools, client definitions, and validator together. When using a newer Launcher revision, follow that revision's bundled format documentation and validate with the same revision before publishing.

A store exposes one UTF-8 TOML catalog, normally `FantasyTale/source.toml`. Schema 2 combines client packages, plugin packages, and client Profiles; manifests and payloads remain separate and are fetched when needed. Account authentication and login tickets belong to the [server Launcher API](launcher.md). Adding a store does not authorize it to update the Launcher application: [Launcher update sources](https://github.com/Wasabi-FantasyTaleDev/FantasyTale.Launcher/blob/main/docs/launcher-updates.md) are configured separately.

All `tools/publish-store/`, `tools/publish-launcher/`, `games/`, and `plugin.toml` paths below belong to the separate [Fantasy Tale Launcher repository](https://github.com/Wasabi-FantasyTaleDev/FantasyTale.Launcher), not this server repository. Obtain its source and use its root as `D:/Launcher` (a placeholder). Use Python 3.11 or newer; upload tools also need `requests`. Catalog validation needs the .NET 10 SDK. These operator tools are not included in the server package.

The PowerShell preparation below assumes Python is installed and `D:/Store` is your separate working directory. Later Python commands use this environment; each runnable sequence explicitly selects the Launcher checkout.

```powershell
Set-Location 'D:/Launcher'
python -m venv D:/Store/.venv
& D:/Store/.venv/Scripts/python.exe -m pip install requests
```

## Hosting and requests {#hosting}

| Kind | Source root | Catalog request |
| --- | --- | --- |
| `http` | `https://files.example.org/` | `GET /FantasyTale/source.toml` |
| `alist` | `https://archive.example.org/` | `POST /api/fs/get`, then GET the resolved URL |

The catalog path is configurable. Catalog and manifest paths are relative to the source root, not the referring document. Static HTTP hosting must return original TOML bytes, not an HTML preview or JSON wrapper. HEAD is unnecessary; correct lengths and HTTP Range support allow resumable transfers.

AList receives the first JSON document and returns the second shape. Replace `size` with the actual byte count.

```json
{"path":"/FantasyTale/source.toml","password":""}
```

```json
{"code":200,"message":"success","data":{"is_dir":false,"size":1234,"raw_url":"https://storage.example.org/source.toml","sign":""}}
```

Without `raw_url`, the adapter uses `/d/<path>?sign=<sign>`. Resolution metadata is bounded to 64 KiB; the resolved download must return original bytes. Temporary URLs are resolved when needed, not stored as package identities. The desktop source editor configures public sources; browsing should not require administrator credentials.

## Catalog fields {#catalog-fields}

```toml
schema = 2

[catalog]
id = "community"
name = { en = "Community store", zh-CN = "社区商店" }
```

Append `[[packages]]` and `[[profiles]]`; empty arrays may be omitted. Unknown keys are rejected. The Launcher reader accepts schema 2 up to 4 MiB, 256 package releases, and 512 profiles. Schema 1 is limited to 256 KiB and excludes profiles and inline plugin definitions. Each profile or inline plugin definition is bounded to 256 KiB.

| Package field | Meaning |
| --- | --- |
| `id`, `kind`, `game` | Package identity, kind, and game; use `client` or `plugin` here |
| `builds` | Exactly one build for a client; explicit supported build IDs for store plugins |
| `version` | Optional client update label; SemVer for plugins |
| `name`, `description` | Localized text including an English name; description is optional |
| `publisher` | Required display name, not authenticated identity |
| `size` | Total download bytes in the manifest |
| `manifest`, `manifest_sha256` | Relative manifest path and SHA-256 of its exact bytes |
| `update_mode` | `files` for the client's current snapshot; `release` for versioned releases |
| `revision` | Optional ordering value; positive revisions must be unique per package |
| `authors`, `uploader` | Optional author list and uploader display name |
| `plugin_definition` | Schema-2-only inline complete plugin definition |
| `minimum_launcher_version` | Schema-2-only optional minimum application SemVer |

Identity is scoped to the configured source. Another source cannot replace an installed package merely by copying its ID. Relative paths reject URLs, traversal, backslashes, percent escapes, queries, and fragments. Generate real sizes and hashes.

## File manifests {#file-manifests}

This file-sync client outline is not valid release metadata: replace its example size and both `<file-sha256>` placeholders. The catalog's `manifest_sha256` hashes the entire UTF-8 manifest. Each `[[files]]` entry separately hashes its payload.

```toml
schema = 1

[release]
id = "bubblefighter.community.client"
game = "bubblefighter"
build = "cn-279"

[[files]]
path = "BubbleFighter.exe"
source_path = "FantasyTale/files/<file-sha256>"
size = 1234
sha256 = "<file-sha256>"
```

`path` is the installation-relative destination; `source_path` is relative to the source root. Plugin manifests use `[release] version` and `builds` instead of the client's singular `build`. Manifests are bounded to 256 KiB. Generate complete output with the publisher; archive manifests have extra fields described in the [Launcher store format](https://github.com/Wasabi-FantasyTaleDev/FantasyTale.Launcher/blob/main/docs/store-sources.md).

## Generate clients locally {#clients}

With the prerequisites above, save this example as `D:/Store/publish.toml`. Use a clean distributable input directory and keep output outside all inputs. Relative publisher paths are resolved from the configuration directory.

```toml
[store]
id = "community"
name = "Community store"
output = "./generated"
storage_gib = 200

[[clients]]
game = "bubblefighter"
build = "cn-279"
name = "China 2011"
directory = "./clients/cn-279"
exclude = ["logs/*", "*.log", "*.dmp"]
```

```powershell
Set-Location 'D:/Launcher'
& D:/Store/.venv/Scripts/python.exe tools/publish-store/publish_store.py --config D:/Store/publish.toml
```

The tool creates `D:/Store/generated/FantasyTale/source.toml`, immutable hashed manifests, and content-addressed files. It emits **schema 1**, replacing its catalog with the configured client/server rows. It does not merge existing plugin rows or profiles. Never target a live unified catalog. `--watch 30` repeats this replacement; `--full-check` rehashes inputs instead of trusting its metadata cache. Keep generation separate from the publication tree. A catalog row cannot implement a new game: its module and valid build definition must already support it.

## Compose profiles and validate {#profiles}

Start from a supported `games/<game>/builds/<build>.toml` in the Launcher checkout. `[build]` provides identity, executable, display names, branch, `year`, `client_version`, and optional `release_version`; optional `region` is a two-letter uppercase code. In the linked snapshot, `client_version` is an integer and optional `wire_version` is a protocol identifier string. Keep the names and types supplied with that Launcher revision: this parser rejects `build_date`, `protocol_version`, and `patch_version`. `[launch]` uses `login`, `arguments`, `launcher_login` (`required`, `optional`, or `none`), and `requires_elevation` for module-specific startup and login policy. `[plugins]` has ID arrays: `required` mandates activation, `recommended` suggests plugins, and `default` provides the default selection. Do not guess fingerprints or login behavior.

Save this configuration as `D:/Store/profiles.toml`, with its definition at `D:/Store/profiles/cn-279.toml`. Prepare `D:/Store/package-catalog.toml` containing the complete package set to retain. Run from the Launcher root with Python 3.11+ and .NET 10 SDK.

```toml
[[profiles]]
definition = "./profiles/cn-279.toml"
revision = 1
```

```powershell
Set-Location 'D:/Launcher'
& D:/Store/.venv/Scripts/python.exe tools/publish-store/compose_catalog.py --catalog D:/Store/package-catalog.toml --config D:/Store/profiles.toml --output D:/Store/unified-source.toml
dotnet run --project tools/publish-launcher -c Release -- --validate-catalog D:/Store/unified-source.toml
```

The **configuration** uses a definition file path; the **resulting catalog** embeds its complete text alongside `game`, `build`, and positive `revision`. Advance the revision whenever the definition changes. The composer accepts schema 1/2, retains input package rows and existing profiles, and emits schema 2 at a new, nonexistent output path. It merges configured profiles, not separate package catalogs. Reconcile fresh client snapshots with all retained plugin/package rows before composing; starting only from a fresh client catalog would lose those rows. Optional `--legacy-output <new-path>` emits a separate schema 1 projection without profiles or inline plugin metadata.

## Plugin authors and publication {#plugins}

Launcher plugins differ from .NET server plugins. A distribution folder contains root `plugin.toml` and runtime files. `[plugin]` declares ID, game, SemVer, localized names, and optional `authors`; `[compatibility]` declares build support. See the [Launcher hook plugin format](https://github.com/Wasabi-FantasyTaleDev/FantasyTale.Launcher/blob/main/docs/hook/plugin-format.md) and [file replacement plugins](https://github.com/Wasabi-FantasyTaleDev/FantasyTale.Launcher/blob/main/docs/file-plugins.md). One plugin ID can support several builds; store package `builds` must list explicit build IDs even when a plugin definition permits selectors.

`authors` identifies creators; `uploader` optionally labels the uploader and is omitted when empty. Neither authenticates identity. Changed bytes need a new SemVer; do not overwrite a published version to add metadata.

The command below **publishes remotely**, with no dry-run CLI mode. Use an independently prepared AList staging source with an existing schema 1 catalog, a writable driver, and the prerequisites above. Supply `FT_ALIST_USERNAME` and `FT_ALIST_PASSWORD` only through the process environment. Review the destination before replacing `staging.example.org`.

```powershell
Set-Location 'D:/Launcher'
& D:/Store/.venv/Scripts/python.exe tools/publish-store/publish_plugins.py --package D:/Store/plugin-package --output D:/Store/plugin-output --base-url https://staging.example.org/
```

The publisher uploads payloads, manifests, then the catalog. It appends generated package text to the remote catalog and refuses changed metadata for an existing `(id, version)`. Its output is limited to **256 KiB and 256 releases**, even if input declares schema 2. Each plugin package is limited to 256 files and 128 MiB. It preserves existing text but does not perform full schema-2 validation or generate `plugin_definition` / `minimum_launcher_version` catalog fields. It is not a general schema-2 editor or local preparation command.

Before unified composition, preserve the complete staged catalog and all referenced objects. Inline metadata must be explicitly added to the schema-2 candidate and validated: embedded ID/game/version must match the package; a separately supplied minimum version must match the embedded `[plugin] minimum_launcher_version`. The composer retains existing package fields but does not generate them. Metadata download does not execute plugin code.

## Tool compatibility {#tool-compatibility}

| Tool or reader | Actual behavior |
| --- | --- |
| Launcher catalog reader | Schema 1: 256 KiB; schema 2: 4 MiB; at most 256 packages / 512 profiles |
| `publish_store.py` | Local schema-1 generator; overwrites its catalog, no package/profile merge |
| `publish_plugins.py` | Remote AList publisher; text append, 256 KiB / 256 releases; no inline metadata generation |
| `compose_catalog.py` | Schema 1/2 input, schema-2 output up to 4 MiB; retains packages, merges profiles |
| `upload_alist.py` | Complete referenced graph; schema-2 catalog up to 4 MiB, manifests up to 256 KiB, at most 4096 objects |

## Safe publication order {#publication}

1. Preserve the current catalog and objects. Coordinate a single writer.
2. Generate clients separately. If using the plugin CLI, publish to an isolated staging source first. Assemble the full desired package catalog, preserving unrelated rows and existing profiles; the client generator cannot merge them.
3. Compose a new unified catalog and run the validator above. Materialize **all** its manifests and payloads beneath a separate release root, such as `D:/Store/release`, and put the validated catalog at its `FantasyTale/source.toml`. The AList uploader requires earlier referenced objects locally too.
4. For HTTP, copy payloads, then immutable manifests, then atomically replace the catalog. Retain old objects for downloads that pinned earlier manifests.
5. For AList, the following command publishes only after that preparation; it is not a dry run. The Python environment needs `requests` and the process needs `FT_ALIST_USERNAME` / `FT_ALIST_PASSWORD`.

```powershell
Set-Location 'D:/Launcher'
& D:/Store/.venv/Scripts/python.exe tools/publish-store/upload_alist.py --input D:/Store/release --base-url https://archive.example.org/ --workers 2 --verify-timeout-seconds 900
```

The uploader checks local hashes, verifies anonymous remote bytes, uploads content before manifests, and replaces the catalog last after checking its previous public digest. This optimistic check is not an atomic multi-writer lock. `--workers` accepts 1–4 (default 2); `--verify-timeout-seconds` is 30–3600 (default 900) per object. `--catalog` changes the catalog path; `--no-proxy` affects only this upload process. A writable AList driver and anonymously readable final files are required. See [Launcher upload documentation](https://github.com/Wasabi-FantasyTaleDev/FantasyTale.Launcher/blob/main/docs/store-sources.md#uploading-a-generated-catalog-to-alist) for more options.

## Acceptance and boundaries {#verification}

From an anonymous consumer, fetch and validate the catalog, fetch a referenced manifest, compare its exact-byte SHA-256, then install into a separate test directory. Check profile identity, required/recommended/default plugin selection, and multi-build compatibility. Hashes establish integrity against the selected catalog, not publisher authenticity. Exclude account data, credentials, tickets, local overrides, diagnostics, and private research. This guide records tool contracts; it does not certify a particular store, desktop flow, or real-client launch.

## Server Store downloads {#server-store}

The server WebUI's **Store** page uses ordinary `distribution.sources`, `distribution.plans`, `distribution.packages` and `distribution.jobs` plugin rows. It can browse public HTTP, AList and GitHub catalogs, select a source and release attachment, and download server packages to isolated staging. This is separate from the desktop Launcher workflows above. It does not install, activate, update or roll back a running server or plugin.

1. Open **Store → Manage sources** and add a unique source ID, display name, transport kind, base URL and catalog path. URLs must not embed credentials. Save the list; source order determines the default offer when multiple sources publish the same package. A failed source remains visible as an error.
2. Browse the catalog and explicitly choose the source. For GitHub releases, choose an attachment with a publisher SHA-256 digest. Release lookup by repository URL is a preview; it does not itself create an installable package. Client packages are previews only in this server interface.
3. Choose **Download to staging**. The server binds the selection to the current source revision and exact release metadata, verifies manifest and payload hashes, and records progress. Closing the browser does not cancel an admitted job. Use its cancel action to stop it.
4. A **staged** job has verified opaque files, not an installed plugin. You can recheck their bytes, browse older jobs or explicitly delete staging. Failed or cancelled transfers clean up partial payloads and retain their status. Restarted unfinished jobs appear as interrupted.

Source settings persist in `data/distribution-sources.json`; downloads use `data/distribution-staging` by default. `maximum_document_bytes` defaults to 4194304; `maximum_sources` to 512; metadata and payload deadlines are `request_timeout_ms` (30000) and `transfer_timeout_ms` (1800000). `maximum_payload_bytes` is optional; declared exact sizes and available disk space still bound downloads. `free_space_reserve_bytes` defaults to 0. These are configurable row options, not package format requirements. History is paged using `history_page_size` (20). There is no automatic deletion of completed downloads.

Server application manifests use their existing schema-1 format. Server plugin release manifests require the experimental reader (`distribution.packages.experimental_release_format: true`, default false); that proposed format is not a finalized Launcher contract. GitHub assets require a publisher digest but do not prove platform compatibility. Release attachments may be downloaded through the [GitHub mirror](store.md#github-mirror), still against that digest. Signed-in administrator sessions and the existing request guard protect the installed server's Store API; per-operation Store permission nodes are not yet implemented. Local command-mode administration remains loopback-only. See [client plugin launch policy](plugins.md#client-plugin-policy) for its separate permission requirements.
