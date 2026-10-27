---
title: "Changelog"
description: "Dated changes for the public release series."
updated: 2026-10-25
---

## Unreleased — 2026-10-25 (UTC+08:00)

- 2026-10-25: Restored all 22 configured kr-1256 AI room presets with their roster levels, characters, colors, clothing, weapons and native item modifiers. AI nicknames follow each recipient's Launcher language across room and round views; Korean names keep their preset spelling and other languages use localized difficulty titles and character names. Invalid modifier targets, missing localization and oversized appearance data fail during activation. Actual in-game appearance acceptance remains pending.
- 2026-10-25: Added opt-in kr-1256 AI room seats through native add/remove requests, separate slot appearance records, controller migration and actor-specific movement, scoring and life/reset state. Bots reserve seats without acquiring network sessions or blocking the human loading barrier. The optional `profiles/kr-1256/ai-rooms.patch.yaml` supplies configurable server loadouts for the native preset choices; complete official outfits and real-client gameplay acceptance remain unverified. The initial tutorial route is unchanged.
- 2026-10-25: Removed the unused `match-relay` plugin and the `InGame.Wire` assembly. Shared room bindings read and write round results through the packet tables; room session models live in the room library and relay session keys in the contracts assembly. Existing typed gameplay paths and profile configuration are preserved.
- 2026-10-24: Folded the last compatibility plugins into the shared rows. Deleted plugin ids: `dialect.modern.messages`, `dialect.inventory-actions` and `dialect.customized-keys`; their packets are now options of `dialect.messages` (`inventory_actions`, `customized_keys`). No wire bytes changed; operator patches listing any deleted row must drop it or the profile fails at composition naming the unknown plugin.

## Unreleased — 2026-10-20 (UTC+08:00)

- 2026-10-22: Added the self-hosted Launcher store guide covering catalogs, client profiles, plugin metadata, tool prerequisites and limits, and catalog-last publication.
- Prebuilt packages for Windows x64, Windows ARM64, Linux x64 and Linux arm64 are published with each release. On Windows, extract the ZIP and double-click `BubbleFighter.Fantasy.Host.exe`: the setup assistant starts and opens in the browser, and a startup error stays on screen until a key is pressed. On Linux, `curl -fsSL https://wasabi-fantasytaledev.github.io/install.sh | bash` installs the server, and `--docker` runs it in a container, with a bundled MySQL only when `--with-mysql` is added.
- KR2026 shop purchases follow the item terms in the client's own shop sheet: timed items expire, permanent items stay permanent, and a purchase the terms refuse gets a failed reply and charges nothing. Six item sets with endurance-counted items cannot be bought until that rule is established for this build.
- The KR2026 shop now looks up every packet by name from the build's opcode table. Opening the gift box's sent list no longer triggers a purchase, and a refund request is no longer answered with an item card.
- A new KR2026 account now goes through the client's own character-creation screen, as the older builds do. KR2026 also shows its native notice when items expired while the player was away.
- Fixed logins on older builds for accounts holding an item without a recorded lifetime, and the first login on cn-404, cn-406, cn-408 and cn-706.
- KR2026, cn-774 and tw-454 log in through the shared login adapter. Operators with custom patches: the removed `dialect.modern.messages` login options are listed in the repository's `CHANGELOG.md`; startup fails on a removed key until it is deleted.
- Startup checks now verify the type of every packet field a binding reads or writes, so a packet table that changes a field's type fails when the server starts instead of on the first packet.
- Troubleshooting explains the `NU1900` package-audit error and how to build when nuget.org is unreachable.

## Unreleased — 2026-10-18 (UTC+08:00)

- Expanded the four-language guides with verified steps: installation and quickstart distinguish source builds from packages that are not published yet; configuration documents profile composition, environment variables, ports, persistence and client data; deployment covers Docker Compose, backups, upgrades and rollback; troubleshooting follows symptom → check → cause → fix.
- Added a practical first-plugin walkthrough to the plugins guide and language quick starts, capability negotiation and conformance testing to the SDK protocol reference.
- Rewrote the project README in English and Chinese with requirements, Windows and Linux quick starts, Docker Compose, the client-data boundary and documentation entry points.
- Corrected statements that did not match the current code. Unpublished download and installer endpoints remain marked as coming soon. Documentation only: runtime behaviour is unchanged.
- Added a Launcher guide in all four site languages: the companion Fantasy Tale Launcher's client builds, session and login-ticket model, hook plugins, distribution boundary, and the server's `launcher.http` API for operators, including the HTTP 426 version gate, JSON compatibility rules and the ticket security boundary. Documentation only.

## Unreleased — 2026-10-16 (UTC+08:00)

- Added declared SDK events on the native event bus, including serial/parallel notifications, waterfall interception and bail decisions across languages.
- Preserved event continuation ownership and Rust callback cleanup; fixed a race that could hide a recorded activation timeout during shutdown.
- Added SDK service providers, decorator chains and ordered service collections across Python, TypeScript, Go and Rust, including Go/Rust WebAssembly.
- Preserved ownership of admitted work through cancellation and timeouts, and fixed startup selection after new definitions are published.
- Preserved SDK cleanup after a row timeout and released retired activation contexts while their shared process remains alive.
- Updated plugin and SDK guides with registration examples, lifetime rules and explicit remaining native-parity limits.

## Unreleased — 2026-10-15 (UTC+08:00)

- Prepared a new public documentation set and a Vue 3 documentation site in four languages.
- Added Windows x64/ARM64 download entries and Linux installer/Docker choices; download endpoints remain coming soon.
- Corrected private-file exclusion rules and removed internal tooling from the public source index.
- Made public validation retain self-contained Equipment and Shop tests while explicitly excluding local-data cases.
- Preserved existing bootstrap defaults and made missing local catalog data fail instead of reporting a test pass.

This is preparation for the first public release, not an announcement that packages or a documentation site have been published. Detailed changes are maintained in the repository's root changelogs.
