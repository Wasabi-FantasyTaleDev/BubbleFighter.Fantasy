---
title: "Quickstart"
description: "Start an isolated local server from a built checkout and check that it is alive."
updated: 2026-10-18
---

This page takes a checkout that you built in [Installation](installation.md) and starts a local, isolated server with in-memory persistence. It uses the game-less `management` profile for the first success check, then explains how a game profile such as `kr-812` differs, and what the checks prove and do not prove.

**Who this is for:** someone who has finished the source build and wants a running local server within a few minutes.

## What this gets you

A local server process you can start and stop safely, with in-memory persistence and no connection to a real database. Accounts and progress reset when the process exits. It is not a playable complete server, and it is not evidence that a game client works.

The `management` profile starts the administration API and WebUI with no game or relay listener, so it works without client data. A game profile such as `kr-812` additionally requires local client data for that build, which the repository does not contain.

## Before you start

- [Installation](installation.md) is complete: the solution built with zero warnings and the build output exists.
- A terminal is open at the repository root; every command on this page runs there.
- The .NET 10 SDK is available: `dotnet --version` reports a 10.x version.
- The local-check patch exists. Check it in PowerShell with `Test-Path tools/local-checks/isolated-memory.patch.yaml`, or in a POSIX shell with `test -f tools/local-checks/isolated-memory.patch.yaml`.
- You create the `.env` file in the next step.
- Optional: if you want to start `kr-812` in this session, the matching client data exists under `client-data/kr-812/data` or `client-data/kr-812/dataraw`.

## Prepare the local environment file

The profiles interpolate environment variables while they are composed, before any patch disables a service. That is why the local MySQL password variable must be present even for an in-memory check.

### 1. Copy the example file

PowerShell:

```powershell
Copy-Item .env.example .env
```

Linux shell:

```sh
cp .env.example .env
```

`.env` is git-ignored. Never commit it.

### 2. Set a disposable password

Open `.env` and set `BFF_MYSQL_PASSWORD` to a nonempty disposable value, for example `isolated-check-only`. The `persistence.mysql` row reads `${BFF_MYSQL_PASSWORD}` with no default, so composition fails when the variable is missing; the isolated-memory patch disables that row only after composition. Keep real database credentials out of local checks and use them only in a deliberately configured deployment.

**Check it worked:** `.env` exists (`Test-Path .env` prints `True`) and its `BFF_MYSQL_PASSWORD` line is nonempty. Do not paste the file or its values into a chat or an issue.

## Find a profile

A profile names a client build and composes the plugin rows that build uses. Its id is `<region>-<wire version>`, and the number is the client's `ClientVersion_Major`, not the executable's file version. `management` is the administration-only profile; `setup` is the first-run assistant.

### 1. List the packaged profiles

```powershell
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- profiles list
```

The command prints one line per profile: the id and a description. This release lists 19. Two useful lines are `management` ("Administration and durable storage only; no game or relay ports.") and `kr-812` ("Korean 2015 client build, ClientVersion_Major 812, resource patch 458.").

A listed profile does not mean every gameplay feature for that build is implemented; available features and required data differ between profiles.

### 2. List the discovered plugins

```powershell
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- plugins list
```

The command prints one line per plugin definition: the plugin id, its version, the client builds it supports and where it was loaded from. This is the set of implementations the host can mount into a profile.

**Check it worked:** both commands exit successfully and print lines; no profile or plugin scan error appears.

## Check the effective configuration

Compose the `kr-812` profile with the local-check patch and print the result without starting anything:

```powershell
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- run --profile kr-812 --patch tools/local-checks/isolated-memory.patch.yaml --dump-config
```

The command prints the effective row list as YAML and exits with code 0. Search the output for these rows:

- `id: persistence.mysql` followed by `disabled: true`;
- `id: persistence.memory` with no `disabled: true` line, so it is enabled;
- `id: clientdata` with its resolved `data_directory` and `raw_directory` for `kr-812`.

The isolated patch is applied last, so it overrides a developer `.env` or `local.patch.yaml` that points MySQL at a real database. The dump stops before any plugin activates, so it proves composition and validation only, not that the profile will start.

::: warning
A dump can contain interpolated values, including credentials, when a patch enables MySQL. Treat it as sensitive.
:::

## Start the server

### 1. Start the game-less local server

From the repository root:

```powershell
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- run --profile management --patch tools/local-checks/isolated-memory.patch.yaml
```

When a repository-root `.env` exists, the first line says `Loaded unset variables from <path>.`. The server then prints its administration listener, the WebUI address and the active row count:

```text
Row 'admin.http' admin API listening on 127.0.0.1:8080.
WebUI: http://127.0.0.1:8080/
Server started with <count> active row(s).
```

`<count>` is the number of active rows printed by the host; it is not a fixed number. `management` has no game or relay ports. Its admin API base address is `http://127.0.0.1:8080`, which comes from the `admin.http` row in `profiles/bundles/core.yaml` (`${BFF_ADMIN_HOST:-127.0.0.1}:${BFF_ADMIN_PORT:-8080}`). The process stays in the foreground until you stop it.

### 2. Start a game profile instead (needs client data)

```powershell
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- run --profile kr-812 --patch tools/local-checks/isolated-memory.patch.yaml
```

A game profile needs the matching client data. Without it, the `clientdata` row fails and startup stops with a message like:

```text
clientdata: neither data_directory '<working-directory>/client-data/kr-812/data' nor raw_directory '<working-directory>/client-data/kr-812/dataraw' exists. Provision the configured client-data directories before enabling this row.
```

Supply the data, or import it through the setup assistant described in [Installation](installation.md), then start the profile again. With the data in place, `kr-812` binds its game listener on `${BFF_BIND_HOST:-0.0.0.0}:${LEGACY_PORT}`, which is `0.0.0.0:7110` because `profiles/kr-812/profile.yaml` sets `LEGACY_PORT` to `${KR_812_GAME_PORT:-7110}`. Legacy builds have no relay listener. The admin API stays on `127.0.0.1:8080`.

**Check it worked:** the process stays in the foreground, prints the three startup lines above, and does not exit with an activation error.

## Check it is alive

### 1. Check the health endpoint

PowerShell:

```powershell
Invoke-RestMethod http://127.0.0.1:8080/health
```

Linux shell:

```sh
curl -s http://127.0.0.1:8080/health
```

The response contains `status` = `ok` and a UTC timestamp. `/health` never needs a sign-in.

### 2. Check the runtime instances

PowerShell:

```powershell
Invoke-RestMethod http://127.0.0.1:8080/api/plugins | Format-Table rowId, state
```

Linux shell:

```sh
curl -s http://127.0.0.1:8080/api/plugins
```

`/api/plugins` returns the running plugin rows. Each item has `rowId`, `plugin`, `source`, `state` and `error`. Look for rows with `state` = `Active` and an empty `error`; a `Failed` or `Pending` row carries its diagnostic in `error`.

A server started with `run --profile` needs no sign-in for `/api` and listens on loopback only. A server started without arguments (first-run setup) asks for the administrator account you created; its session is a browser cookie, so use the WebUI for those calls.

### 3. Open the WebUI

Open `http://127.0.0.1:8080/` in a browser. The management profile serves the administration interface.

**Check it worked:** the health response reports `ok`, the instance list shows active rows, and the WebUI loads. These checks prove that the host and its administration API run; they do not prove that a game client can connect.

## Stop the server

Press Ctrl+C in the terminal that runs the server. The console prints `Shutdown complete.` and the process exits. In-memory accounts and progress are discarded with the process; stopping is always safe for local checks because nothing durable was touched.

To keep data across restarts, switch persistence to SQLite or MySQL as described in [Server configuration](configuration.md). The isolated-memory patch is for local checks only.

## What this does not prove

- No game client logged in, and no gameplay or packet compatibility was exercised.
- The management profile has no game or relay listener, so there was no client traffic at all.
- A passing configuration or automated check is not native-client acceptance; test the intended login and gameplay flows with a real client, separately.

## If something goes wrong

- **`environment variable 'BFF_MYSQL_PASSWORD' is not set and has no default`:** the `.env` file is missing, was not found at the repository root, or does not set the variable. Copy `.env.example` and set a disposable value.
- **The administration port is in use:** startup fails on the listener row. Set `BFF_ADMIN_PORT` in `.env`, or stop the process that owns port 8080.
- **The game port is in use:** set `KR_812_GAME_PORT` in `.env`, or stop the process that owns port 7110.
- **`kr-812` fails on `clientdata`:** see the client-data message above; the build's data must exist before the profile can start.
- **`Server startup did not converge`:** the message lists each non-active row with its state and diagnostic. Read the first `Failed` or `Pending` row and fix its cause.

More cases are covered in [Troubleshooting](troubleshooting.md).

## Next

Continue with [Server configuration](configuration.md) for persistence, ports, profiles and environment variables, then [Plugins](plugins.md) to learn how the plugin rows work and how to mount your own. For the companion launcher that installs clients and connects them to servers, see [Launcher](launcher.md).
