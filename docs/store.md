---
title: "Store management"
description: "Browse store catalogs, manage sources and stage server packages through the administration interface."
updated: 2026-10-25
---

# Store management {#store-management}

The store is the administration interface for distributing server packages. It browses configured
catalog sources, previews GitHub release metadata, and downloads server packages into a verified
staging area. Everything on this page is protected by an administrator session: sign in through the
WebUI (or `POST /api/auth/login`), and every store request requires that session cookie. The store
**stages files and nothing else**: no archive is extracted, no plugin is loaded, and no application
path is replaced by any store action. A completed download is an inventory of verified files, not an
installed plugin.

The store ships in the core bundle (rows `distribution.sources`, `distribution.plans`,
`distribution.packages` and `distribution.jobs`), which the `kr-812`, `management` and `all`
profiles include. Staging lands under `data/distribution-staging` by default, held by an exclusive
owner lock; history pages default to 20 entries; the free-space reserve defaults to zero. Sources
are stored in `data/distribution-sources.json` — setting `state_path` empty makes the source list
read-only.

## Sources {#sources}

The Sources card lists the configured catalog sources in operator order, and the order matters: the
first source that serves a catalog is selected by default. Each row has an id, a display name, a
kind (`http`, `alist` or `github`), a root URL and a catalog path. URLs carry no credentials, query
or fragment; the root must end in `/`. Move rows up and down, add and remove them, then save the
whole list at once.

Saving uses an optimistic revision: the editor captures the settings revision when it opens and
sends it back with the save. If another administrator saved in between, the save is refused with
`source_revision_conflict` and the dialog stays open for you to review and reapply.

## GitHub mirror {#github-mirror}

Where GitHub is slow or blocked, the store can download GitHub release attachments and archives
through public URL-prefix mirrors. Open **Manage sources** and turn on **GitHub mirror
acceleration**; **Automatic** measures every mirror before each download and tries the fastest
available ones first, or choose one mirror after a **Speed test**. GitHub itself is always the
last route. Only payloads travel through a mirror: release information and catalogs are read from
GitHub directly, and every mirrored byte is checked against the size and SHA-256 that GitHub
published, so a mirror can make a download fail but cannot change what is installed.

The server checks its own address once. When it is in mainland China, the first administrator to
open the WebUI is asked whether to turn the mirror on; the dialog is shown once and nothing changes
until it is confirmed. The mirror list, the probe file and the measurement bounds are in
`data/github-mirrors.json` (`github_mirrors_path` on the `distribution.sources` row; empty turns the
feature off), and the switch, selection and location check are kept in `github_mirror_state_path`.

## Browsing the catalog {#browsing}

The catalog page shows one merged card list across all sources, keyed by package identity. A filter
selects all sources or one; a refresh re-reads every catalog (or one source's). A source that fails
is never dropped silently — its failure (`source_timeout`, `source_unavailable` or
`catalog_invalid`) is listed above the cards while healthy sources still render.

When several releases of the same package appear in one catalog, the highest publisher `revision`
wins; a tie — or a multi-release group whose revisions are unset — is a configuration error rather
than a guess. Client-targeted packages are visible but download-only for server targets: selecting
one shows "Client plugins are available for preview only".

## GitHub repositories {#github-repositories}

A GitHub source reads catalog documents from the repository. The Repository release form previews a
repository's latest stable release — or an explicitly selected tag — with its attachments: drafts
and prereleases are refused, every asset URL must belong to the requested repository and tag, and
an attachment carries a publisher SHA-256 only when GitHub publishes a `sha256:` digest for it. The
preview is metadata only; nothing is downloaded by it.

The `github_repository` and `github_tag` fields are catalog schema 3. They work against the server
and the Launcher's strict catalog readers, but they are a provisional coordination format, not a
published shared contract — do not republish production catalogs to schema 3 without agreeing the
format with Launcher releases first. A repository attachment can be downloaded only when it carries
that publisher digest; the download button stays disabled otherwise, because the byte-for-byte
verification a download performs has nothing to compare against.

## Downloading to staging {#staging}

Downloading a server package is a three-step flow, and each step pins exactly what was shown:

1. **Select** a download offer on the card. Each offer carries a selection fingerprint — a SHA-256
   over the configured source and the exact offer — and the SHA-256 of the exact catalog bytes it
   was read from.
2. **Plan** the download. The plan revalidates the source, catalog, release and attachment against
   those pins; a changed source or offer is refused with `distribution_source_changed` or
   `distribution_selection_changed` instead of downloading something else.
3. **Admit** the job. The job verifies every file's size and SHA-256 as it streams, keeps the blobs
   opaque under the staging root, and reports phases (`preparing`, `transferring`, `staged`). The
   Downloads panel polls progress, verifies a staged job's full readback on demand, cancels active
   work and deletes terminal jobs with their staging.

A passing verification states what the staging volume held at that instant. It grants no
installation authority, and a later change on disk is detected by verifying again, not trusted
forward.

## Client policy {#client-plugin-policy}

The client policy that decides which plugins Launchers must, may or must not use is edited in the WebUI of an installed server. Saving requires the `webui.settings.client_policy` permission on top of the administrator session and reruns the configuration application. These rules configure compatible Launchers; client binaries are not installed on the server, and the rules prove nothing about client integrity. See [Client policy](plugins.md#client-plugin-policy) for the management API and [the rule types](launcher.md#client-policy).

## Administration API {#administration-api}

The WebUI calls the same endpoints an operator script would. All paths below require the
administrator session cookie; non-GET requests also require the `X-BFF-Request: 1` header with a
matching same-origin `Origin`.

| Method and path | Purpose |
| --- | --- |
| `GET/PUT /api/distribution/v1/sources` | Read or replace the source list with its revision. |
| `POST /api/distribution/v1/repository-release` | Preview one GitHub release (metadata only). |
| `GET /api/distribution/v1/catalog?sourceId=` | The merged catalog snapshot, or one source's. |
| `POST /api/distribution/v1/plans` · `/validate` · `DELETE` | Pin, recheck and release a selection. |
| `POST /api/distribution/v1/plans/{id}/files` | Verify the manifest and list the payload files. |
| `GET/POST /api/distribution/v1/jobs` · `GET /{id}` | History page and admission (202 with a `Location` header). |
| `POST /api/distribution/v1/jobs/{id}/cancel` · `/verify` · `DELETE` | Cancel, verify the staged readback, or remove a terminal job. |

Stable error codes (`source_revision_conflict`, `distribution_selection_changed`,
`distribution_job_not_staged`, `repository_rate_limited`, and the rest) name the failure; the HTTP
status distinguishes a bad request (400), a stale pin (409), an upstream problem (502/504) and a
storage or configuration problem (503).

## What the store does not do {#limits}

- No installation or activation: no route extracts, loads or restarts anything. Downloading is not
  installing, and a durable intent is not a running feature.
- GitHub mirrors fail by design instead of pretending to work.
- Cross-origin manifest references are refused ("not supported yet").
- The Launcher consumes none of this yet: it does not read store catalogs, and client presets are
  advertised data until its download engine ships.
