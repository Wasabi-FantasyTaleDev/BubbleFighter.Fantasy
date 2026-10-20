---
title: "快速开始"
description: "从已构建的检出启动隔离的本地服务器，并确认它正在运行。"
updated: 2026-10-18
---

本页使用你在[安装](installation.md)中构建好的检出，启动一个使用内存持久化的本地隔离服务器。它先以无游戏监听的管理 profile `management` 完成第一次成功检查，然后说明 `kr-812` 这类游戏 profile 有何不同，以及这些检查能证明什么、不能证明什么。

**本页适合：** 已完成源码构建、希望在几分钟内跑起本地服务器的读者。

## 你将得到什么

一个可以安全启动和停止的本地服务器进程，使用内存持久化，不连接任何真实数据库。进程退出后账号与进度都会重置。它不是可完整游玩的服务器，也不能证明游戏客户端可用。

`management` profile 只启动管理 API 与 WebUI，没有游戏或中继监听，因此不需要客户端数据即可运行。`kr-812` 这类游戏 profile 还需要该版本对应的本地客户端数据，而仓库不包含这些数据。

## 开始之前

- 已完成[安装](installation.md)：解决方案以零警告构建成功，且构建输出存在。
- 终端位于仓库根目录；本页所有命令都在此执行。
- .NET 10 SDK 可用：`dotnet --version` 输出 10.x 版本。
- 本地检查补丁存在。在 PowerShell 中用 `Test-Path tools/local-checks/isolated-memory.patch.yaml` 检查，或在 POSIX shell 中用 `test -f tools/local-checks/isolated-memory.patch.yaml` 检查。
- 下一步会创建 `.env` 文件。
- 可选：如果本次要启动 `kr-812`，匹配的客户端数据已在 `client-data/kr-812/data` 或 `client-data/kr-812/dataraw` 下。

## 准备本地环境变量文件

profile 在组合时就会插入环境变量，此时任何补丁都还没有停用服务。因此即使是内存检查，也必须存在本地的 MySQL 密码变量。

### 1. 复制示例文件

PowerShell：

```powershell
Copy-Item .env.example .env
```

Linux shell：

```sh
cp .env.example .env
```

`.env` 被 Git 忽略，绝不要提交。

### 2. 设置临时密码

打开 `.env`，把 `BFF_MYSQL_PASSWORD` 设为非空的临时值，例如 `isolated-check-only`。`persistence.mysql` 行读取没有默认值的 `${BFF_MYSQL_PASSWORD}`，变量缺失时组合会失败；隔离内存补丁只在组合之后才停用该行。本地检查不要使用真实数据库凭据，真实凭据只用于刻意配置的部署。

**检查是否成功：** `.env` 存在（`Test-Path .env` 输出 `True`），且 `BFF_MYSQL_PASSWORD` 行非空。不要把该文件或其值粘贴到聊天或问题中。

## 找到 profile

profile 指定一个客户端版本，并组合该版本使用的插件行。它的 id 是 `<region>-<wire version>`，其中数字是客户端的 `ClientVersion_Major`，不是可执行文件的文件版本。`management` 是仅管理用途的 profile；`setup` 是首次运行向导。

### 1. 列出打包的 profile

```powershell
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- profiles list
```

命令为每个 profile 输出一行：id 与描述。本版本列出 19 个。其中两行比较有用：`management`（“Administration and durable storage only; no game or relay ports.”）和 `kr-812`（“Korean 2015 client build, ClientVersion_Major 812, resource patch 458.”）。

列出某个 profile 不代表该版本的全部玩法都已实现；不同 profile 可用的功能与所需数据不同。

### 2. 列出发现的插件

```powershell
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- plugins list
```

命令为每个插件定义输出一行：插件 id、版本、支持的客户端版本以及加载来源。这就是 Host 可以挂载到 profile 中的实现集合。

**检查是否成功：** 两条命令都成功退出并输出内容，没有 profile 或插件扫描错误。

## 检查生效的配置

用本地检查补丁组合 `kr-812`，在不启动任何东西的情况下打印结果：

```powershell
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- run --profile kr-812 --patch tools/local-checks/isolated-memory.patch.yaml --dump-config
```

命令以 YAML 输出生效的行列表，并以退出码 0 结束。请在输出中查找以下行：

- `id: persistence.mysql` 后面跟着 `disabled: true`；
- `id: persistence.memory` 没有 `disabled: true`，即已启用；
- `id: clientdata` 显示 `kr-812` 解析后的 `data_directory` 与 `raw_directory`。

隔离补丁最后应用，因此会覆盖开发者 `.env` 或 `local.patch.yaml` 中把 MySQL 指向真实数据库的设置。转储会在任何插件激活之前结束，因此只能证明组合与校验，不能证明 profile 能启动。

::: warning
当补丁启用 MySQL 时，转储可能包含插值后的值，包括凭据。请把它视为敏感内容。
:::

## 启动服务器

### 1. 启动无游戏的本地服务器

在仓库根目录执行：

```powershell
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- run --profile management --patch tools/local-checks/isolated-memory.patch.yaml
```

如果仓库根目录存在 `.env`，第一行是 `Loaded unset variables from <path>.`。随后服务器会打印管理监听地址、WebUI 地址和活动行数：

```text
Row 'admin.http' admin API listening on 127.0.0.1:8080.
WebUI: http://127.0.0.1:8080/
Server started with <count> active row(s).
```

`<count>` 是 Host 打印的活动行数量，不是固定数字。`management` 没有游戏或中继端口。它的管理 API 基础地址是 `http://127.0.0.1:8080`，来自 `profiles/bundles/core.yaml` 中的 `admin.http` 行（`${BFF_ADMIN_HOST:-127.0.0.1}:${BFF_ADMIN_PORT:-8080}`）。进程会一直保持在前台，直到你停止它。

### 2. 改为启动游戏 profile（需要客户端数据）

```powershell
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- run --profile kr-812 --patch tools/local-checks/isolated-memory.patch.yaml
```

游戏 profile 需要匹配的客户端数据。缺少数据时，`clientdata` 行会失败，启动会以类似下面的消息停止：

```text
clientdata: neither data_directory '<working-directory>/client-data/kr-812/data' nor raw_directory '<working-directory>/client-data/kr-812/dataraw' exists. Provision the configured client-data directories before enabling this row.
```

请提供数据，或按[安装](installation.md)所述通过安装向导导入，然后重新启动该 profile。数据就位后，`kr-812` 会在 `${BFF_BIND_HOST:-0.0.0.0}:${LEGACY_PORT}` 上绑定游戏监听，因为 `profiles/kr-812/profile.yaml` 把 `LEGACY_PORT` 设为 `${KR_812_GAME_PORT:-7110}`，所以实际是 `0.0.0.0:7110`。旧版（legacy）版本没有中继监听。管理 API 仍是 `127.0.0.1:8080`。

**检查是否成功：** 进程保持在前台，打印上面三行启动信息，且没有以激活错误退出。

## 确认它正在运行

### 1. 检查健康检查端点

PowerShell：

```powershell
Invoke-RestMethod http://127.0.0.1:8080/health
```

Linux shell：

```sh
curl -s http://127.0.0.1:8080/health
```

响应包含 `status` = `ok` 以及 UTC 时间戳。`/health` 始终无需登录。

### 2. 检查运行时实例

PowerShell：

```powershell
Invoke-RestMethod http://127.0.0.1:8080/api/plugins | Format-Table rowId, state
```

Linux shell：

```sh
curl -s http://127.0.0.1:8080/api/plugins
```

`/api/plugins` 返回正在运行的插件行。每一项包含 `rowId`、`plugin`、`source`、`state` 和 `error`。请查找 `state` = `Active` 且 `error` 为空的行；`Failed` 或 `Pending` 行会在 `error` 中给出诊断信息。

以 `run --profile` 启动的服务器访问 `/api` 无需登录，并且只在回环地址上监听。不带参数启动的服务器（首次安装）会要求你创建的管理员账号；其会话是浏览器 Cookie，因此请通过 WebUI 进行这些操作。

### 3. 打开 WebUI

在浏览器中打开 `http://127.0.0.1:8080/`。management profile 会提供管理界面。

**检查是否成功：** 健康检查返回 `ok`，实例列表显示活动行，且 WebUI 可以加载。这些检查证明 Host 及其管理 API 正在运行；它们不能证明游戏客户端可以连接。

## 停止服务器

在运行服务器的终端按 Ctrl+C。控制台会打印 `Shutdown complete.`，随后进程退出。内存中的账号与进度随进程一起丢弃；对本地检查来说停止总是安全的，因为没有任何持久数据被改动。

如果希望数据在重启后保留，请按[服务器配置](configuration.md)把持久化切换到 SQLite 或 MySQL。隔离内存补丁只用于本地检查。

## 这不能证明什么

- 没有游戏客户端登录，也没有验证任何玩法或数据包兼容性。
- management profile 没有游戏或中继监听，因此完全没有客户端流量。
- 配置检查或自动检查通过不等于真实客户端验收；请另行用真实客户端验证目标登录与游戏流程。

## 出现问题时

- **`environment variable 'BFF_MYSQL_PASSWORD' is not set and has no default`：** `.env` 文件缺失、没有在仓库根目录找到，或没有设置该变量。复制 `.env.example` 并设置一个临时值。
- **管理端口已被占用：** 启动会在监听行失败。请在 `.env` 中设置 `BFF_ADMIN_PORT`，或停止占用 8080 的进程。
- **游戏端口已被占用：** 请在 `.env` 中设置 `KR_812_GAME_PORT`，或停止占用 7110 的进程。
- **`kr-812` 因 `clientdata` 失败：** 参见上面的客户端数据消息；该版本的数据必须存在，profile 才能启动。
- **`Server startup did not converge`：** 消息会列出每个未激活的行及其状态与诊断。先阅读第一个 `Failed` 或 `Pending` 行并修复其原因。

更多情况见[疑难排查](troubleshooting.md)。

## 下一步

继续阅读[服务器配置](configuration.md)，了解持久化、端口、profile 与环境变量；然后阅读[插件](plugins.md)，了解插件行的工作方式以及如何挂载自己的插件。配套启动器如何安装客户端并连接服务器，见[启动器](launcher.md)。
