---
title: "故障排查"
description: "按顺序诊断构建、配置、启动、网络、持久化与 SDK 故障。"
updated: 2026-10-20
---

## 首次安装拒绝代理请求 {#setup-proxy-refused}

**现象。** 尚未创建管理员时，访问 `/api/setup/*` 返回 HTTP 403 和 `setup_proxy_refused`。**原因。** 请求包含 `X-Forwarded-For`、`X-Forwarded-Host`、`X-Forwarded-Proto` 或 `Forwarded`，反向代理可能让远程请求看起来来自本机。**处理。** 直接在服务器上打开安装页面，不经过代理。创建管理员后，此首次安装限制不再适用，正常身份验证仍然必需。

管理后台的登录失败按连接来源 IP 分别计数，沿用 `installation.login_attempts` 和 `login_window_seconds`。`login_address_capacity` 默认保留 1024 个地址；满额时替换最早的时间窗口。转发头不能改变登录计数所用的地址。

## 本页内容

本页把"服务器不工作"变成一次有顺序的排查：先检查什么、证据在哪里、改哪一处能解决问题。每个症状都遵循同一结构：症状、检查、可能原因、修复、确认恢复。它同时适用于源码检出和发布目录，但不能代替用真实游戏客户端进行的测试。

**适用对象。** 首次启动服务器的运维者，以及构建或验证门禁失败的贡献者。

**开始之前。**

- 源码检出在仓库根目录操作，发布构建在发布目录操作。
- 本地检查时，把 `.env.example` 复制为被 git 忽略的 `.env`，并把 `BFF_MYSQL_PASSWORD` 设为非空的临时值。即使禁用 MySQL，profile 插值也需要这个变量。
- 本页所有本地 Host 命令都带 `--patch tools/local-checks/isolated-memory.patch.yaml`。该补丁禁用 `persistence.mysql` 并启用 `persistence.memory`，因此开发者自己的 `.env` 或本地补丁不会把服务器指向真实数据库。
- 先读第一个错误，每次只改一处，并保留失败输出。

## 首先检查

| 症状 | 首先检查 | 可能区域 | 参见 |
| --- | --- | --- | --- |
| 构建没有完成 | 重新执行 `dotnet build BubbleFighter.Fantasy.sln -c Release`，读第一个错误 | 工具链、包还原、警告即错误 | 工具链与构建 |
| `error:` 提到环境变量或 profile | 带隔离补丁执行 `--dump-config` | profile、补丁、`.env` 插值 | 配置 |
| `error: Server startup did not converge` | 读取其中每个行 id 与状态 | 激活、依赖、客户端数据 | 启动与激活 |
| 某个行一直是 `Pending` | `/api/plugins`，读取 `state` 与 `error` | 缺少服务、提供者或定义来源 | 启动与激活 |
| 某个行是 `Failed`、`CleanupIncomplete` 或 `CleanupFailed` | 同一份 JSON，再看控制台日志 | 激活或清理所有权 | 启动与激活 |
| 客户端无法连接 | 监听端口与对外公布地址 | 绑定、NAT、防火墙、relay | 端口与网络 |
| 管理 API 或 WebUI 无法访问或返回 401 | `/health`、`BFF_ADMIN_HOST` | 管理监听与登录 | 端口与网络 |
| 数据库错误，或重启后数据消失 | `--dump-config`；持久化行 | MySQL、SQLite、迁移、内存存储 | 持久化 |
| `clientdata` 激活失败 | 错误中给出的两个路径 | 客户端数据文件与 `BFF_CLIENT_DATA` | 客户端数据 |
| SDK 插件失败或超时 | 插件 stderr 与 loader 行配置 | JSON-RPC stdout、协议、限额 | SDK 插件 |

如果都不匹配，直接查看"报告问题"一节。

## 工具链与构建问题

### .NET SDK 缺失或不是版本 10

**症状。** 找不到 `dotnet`，或构建以 `net10.0` 为目标时报错。**检查。** 运行 `dotnet --info`；它会列出已安装的 SDK 与运行时，而所有项目都以 `net10.0` 为目标。**可能原因。** 没有安装 .NET 10 SDK，或 `PATH` 仍指向旧版本。**修复。** 安装 .NET 10 SDK 并打开新的终端。**确认恢复。** `dotnet --version` 显示 10.x，且 `dotnet build BubbleFighter.Fantasy.sln -c Release` 以 `0 Warning(s)` 和 `0 Error(s)` 结束。

### 包还原或编译失败

**症状。** 构建在 NuGet 还原阶段因网络或源错误停止，或编译器报告警告并导致失败。**检查。** 读第一个还原错误；然后判断消息是否是编译器警告。所有项目都启用 `TreatWarningsAsErrors`，且 `GenerateDocumentationFile` 要求每个公开类型和成员都有真实的 XML 摘要。**可能原因。** 无法访问包源、代理问题、包缓存损坏、缺少 XML 注释、未使用的变量或可空性错误。**修复。** 用 `dotnet restore` 重试，修正网络或代理设置，并修复警告的根因——绝不要为了让构建变绿而屏蔽警告。**确认恢复。** 构建报告零警告、零错误，且没有包或还原错误。

有一种还原错误的原因不同。`NU1900`（`Error occurred while getting package vulnerability data`）表示 NuGet 的包审计无法连接 nuget.org；每次还原都会运行审计，而在本项目中它的警告按错误处理。如果机器无法连接 nuget.org，但所需的包已在本地缓存中，可使用 `dotnet build BubbleFighter.Fantasy.sln -c Release -p:NuGetAudit=false` 构建。这只跳过在线漏洞查询，不会跳过任何编译器警告。发布版本之前，请在启用审计的情况下正常构建一次。

### 发布构建在另一台机器上无法启动

**症状。** 编译通过的构建在目标机器上立即退出。**检查。** 在那台机器上运行 `dotnet --list-runtimes`，确认存在 `Microsoft.AspNetCore.App 10.x`；Host 使用 ASP.NET Core 共享框架。**可能原因。** 机器上没有 ASP.NET Core Runtime 10——框架依赖发布不会自带运行时。**修复。** 为目标平台安装 ASP.NET Core Runtime 10；发布脚本中的说明是 `The deployment server requires the x64 ASP.NET Core Runtime 10.0, which includes the .NET runtime.` **确认恢复。** 从发布目录启动，控制台会报告 `Server started with <N> active row(s).`

### 验证工具链不完整

**症状。** `python tools/ci/serial-gates.py` 在测试开始前停止，或某个 SDK 一致性门禁被报告为跳过。**检查。** 该门禁需要 `PATH` 中有 `dotnet`、`node`、`npm`、`go`、`cargo` 和 `rustup`；缺少时会打印 `Missing validation toolchains: <names>`。**可能原因。** 开发机只安装了 .NET 构建所需的部分。**修复。** 安装缺少的工具链；对 Rust SDK 及其 WebAssembly 测试，添加失败信息中给出的目标：`rustup target add wasm32-wasip1`。**确认恢复。** 重跑不再打印 `An SDK conformance gate was skipped.`，并以一行 `PASS public gates:` 或 `PASS complete gates:` 结束。

## 配置与插值问题

### 环境变量没有值

**症状。** `error: File '<file>', row '<row id>': environment variable '<NAME>' is not set and has no default.` **检查。** 在错误所指的 profile、bundle 或补丁中查找没有 `:-default` 的 `${NAME}`，然后检查 `.env` 和进程环境。除 `start` 外，每个命令都会在组合 profile 之前把仓库根目录的 `.env` 应用到未设置的进程变量；已存在的环境变量优先。**可能原因。** 变量既没有导出也不在 `.env` 中，而 `BFF_MYSQL_PASSWORD` 没有默认值，因此任何游戏或管理 profile 都无法在缺少它时完成组合。**修复。** 为该命令设置变量，或写入被 git 忽略的 `.env`；绝不提交真实凭证。**确认恢复。** 组合成功，`--dump-config` 打印生效的行。

### MySQL 密码为空

**症状。** `error: Activation of row 'persistence.mysql' (plugin 'persistence.mysql') failed: Invalid configuration for row 'persistence.mysql': connection_string is missing Password (BFF_MYSQL_PASSWORD is required and must not be empty).` **检查。** 复制来的 `.env.example` 含有 `BFF_MYSQL_PASSWORD=`，因此插值会以空字符串成功，失败只会在该行激活时出现。**可能原因。** `.env` 中留下了空占位值。**修复。** 本地检查使用非空的临时值，并加上隔离补丁以免连接数据库；真实凭证只用于刻意配置的持久化部署。**确认恢复。** 加上隔离补丁后，`persistence.memory` 激活，日志报告内存持久化已启用。

### profile、补丁或选项名写错

**症状。** 出现以下之一：`error: Profile '<id>' not found: expected '<path>'.`、`error: Patch file '<path>' does not exist.`、`error: Unknown option '<option>'.`、`error: Unknown command '<command>'.`、`error: Option '<option>' requires a value.` **检查。** 运行 `bff profiles list` 和 `bff --help`；profile id 是 `profiles/` 下的目录名，帮助文本列出可接受的选项。**可能原因。** 拼写错误、profile 不存在，或把选项放在不接受它的命令后面。**修复。** 改正 id 或选项；本地运行时保留 `--patch tools/local-checks/isolated-memory.patch.yaml`。**确认恢复。** `bff profiles list` 打印该 profile 及描述，改正后的命令完成组合。

### 配置键未知或无效

**症状。** `error: Invalid configuration for row '<row id>': unknown key '<key>'`，或同一行的类型转换、校验失败。**检查。** 插件选项是映射到 PascalCase 属性的 snake_case YAML 键；插件的选项类列出它接受的键。检查消息中的行 id 及其对应的 `plugin` 字段。**可能原因。** 键拼写错误、使用了旧指南中的键，或值的类型、范围不对。**修复。** 改正键或值；一行的所有失败会合并到一条消息里，请全部修正。**确认恢复。** `--dump-config` 成功并显示改正后的值。

### 生效配置与预期不符

**症状。** 服务器表现得像某行被启用，或使用了与你修改不同的端口。**检查。** 层按顺序应用：bundle、profile 补丁，最后是命令行补丁。用不启动 Host 的方式打印结果：

```sh
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- run --profile kr-812 --patch tools/local-checks/isolated-memory.patch.yaml --dump-config
```

**可能原因。** 后面的层替换了该行或它的 `disabled` 标志；隔离补丁是最后一层，会启用内存持久化。**修复。** 修改应当拥有该值的层；不要把同一覆盖加到两层。**确认恢复。** 转储显示预期的标志和值。转储可能包含插值后的机密（例如 MySQL 连接字符串），分享前请脱敏。

## 启动、激活与插件状态

每次启动尝试后，每个行都会发布状态。从控制台日志或管理 API 读取：

```powershell
Invoke-RestMethod http://127.0.0.1:8080/api/plugins | Select-Object rowId, plugin, state, error
```

```sh
curl -s http://127.0.0.1:8080/api/plugins
```

`/api/plugins` 为每个运行时实例返回一个对象，字段包括 `rowId`、`plugin`、`source`、`state`、`error`、`codeRefreshPolicy`、`codeLifetime`、`restartRequiredForReclamation`、`isRetiring`、`overrides`、`version`、`contractVersions` 和 `supportedBuilds`。只有 `Active` 是健康的运行状态。

| 状态 | 含义 | 处理方式 |
| --- | --- | --- |
| `Active` | 已激活，effect 生效 | 无需处理 |
| `Pending` | 已被期望，但缺少服务、提供者或定义来源 | 读 `error`，修复依赖 |
| `Failed` | 激活或自有工作失败 | 读 `error`，修复后重新加载或重启 |
| `Unloading` | effect 已关闭，清理进行中 | 等待；不要再次启动该行 |
| `Unloaded` | 已禁用或已卸载，没有生效的 effect | 禁用行的正常状态 |
| `CleanupIncomplete` | 清理超过时限仍在进行或被取消 | 所有权仍被持有；重试前先调查 |
| `CleanupFailed` | 清理报告了错误 | 不能假定资源已释放；重启被拒绝 |
| `WorkFailed` | 自有后台工作失败 | 资源在清理完成前仍被持有 |

### 服务器启动未收敛

**症状。** 进程以退出码 1 结束，并输出 `error: Server startup did not converge (WaitingForDependencies): <row id>: Pending (<diagnostic>); <row id>: Failed (<diagnostic>).`；控制台还会记录 `Lifecycle revision <N> for '<row id>' did not converge.` **检查。** 读取列出的每一行，然后把 `/api/plugins` 与 `--dump-config` 的转储对照。**可能原因。** 至少一个未禁用的行没有达到 `Active`：缺少提供者、缺少客户端数据、端口错误，或某个值无法绑定。**修复。** 修复列表中的第一行，而不是最后一行；如果某行是刻意禁用的，确认生效转储确实禁用了它。**确认恢复。** 控制台报告 `Server started with <N> active row(s).`，且 `GET /health` 返回 `{"status":"ok", ...}`。

### 某个行一直是 Pending

**症状。** 该行始终不激活，`error` 说明原因，通常是 `Waiting for services: <service types>; provider rows: <rows>; override rows: <rows>.` 或 `Definition source for plugin '<plugin>' is unavailable.` **检查。** 找到消息中给出的提供者或定义来源，检查其行状态。**可能原因。** 必需服务、可选提供者或处理器替换目标被禁用、失败或不在 profile 中；或发布该插件的 SDK loader 行尚未激活。**修复。** 启用或修复消息中的提供者行，或在不想要该能力时移除消费者。**确认恢复。** 重新加载或重启后该行变为 `Active`，启动收敛。

### 某个行是 Failed

**症状。** `error: Activation of row '<row id>' (plugin '<plugin>') failed: <cause>.` **检查。** 读取 `error` 字段和控制台中的完整原因；外层消息给出运行器和插件，内层消息给出实际问题——客户端数据、MySQL、SQLite、端口、未知键或未知插件。**可能原因。** 插件在激活时校验的任何内容。**修复。** 修复内层原因。若出现 `Automatic activation attempt limit <N> reached for row '<row id>'.`，该行会停止自动重试，修复后需要显式重新加载。**确认恢复。** 该行变为 `Active`。

### CleanupIncomplete 或 CleanupFailed

**症状。** 正在卸载的行在 `/api/plugins` 中显示 `CleanupIncomplete` 或 `CleanupFailed`，其 `error` 字段与控制台日志给出清理失败原因。触发清理的卸载或重新加载调用也可能失败，并报告 `Row '<row id>' teardown is <state>; ownership and dependencies are retained.`，其中 `<state>` 是该行到达的状态。**检查。** 读取该行的 `error` 和控制台日志；`CleanupIncomplete` 表示清理超过时限仍在进行或调用方已离开，`CleanupFailed` 表示自有的清理器抛出异常。**可能原因。** 释放时阻塞的资源、不会结束的自有后台任务，或失败的清理器。**修复。** 不要强制该行重启：所有权仍在时重启会被拒绝（`Activation is refused while failed work or cleanup ownership remains: <row ids>.`）。让清理自行完成或停止服务器；若无法完成，重启进程并修复插件。**确认恢复。** 状态变为 `Unloaded`，服务器干净关闭。

### 插件未知或行 id 重复

**症状。** `error: Row '<row id>' references unknown plugin '<plugin>'.` 或 `error: Duplicate row id '<row id>' in profile '<profile id>'.` **检查。** 运行 `bff plugins list` 查找插件 id；确认其程序集位于插件根目录下（Host 旁的 `plugins/`，或用 `--plugin-root` 指定的目录）。对于重复 id，找出贡献第二行的 bundle 或补丁。**可能原因。** 插件构建过期或缺失、插件目录未复制、`plugin` 字段拼写错误，或补丁重新插入了已存在的 id。**修复。** 重新构建解决方案（Host 构建会用 `artifacts/plugins` 替换自己的 `plugins` 目录）；怀疑副本过期时使用 `--no-incremental`；删除或重命名重复行。**确认恢复。** `bff plugins list` 打印 id、版本、支持的构建和来源，`--dump-config` 只列出该行一次。

### 激活时缺少必需服务

**症状。** 失败行报告 `Row '<row id>' cannot <verb>; missing service(s): <service types>.` **检查。** 找出应当发布该服务的行；依赖是声明的接口，而不是行 id。**可能原因。** 提供者行被禁用、失败或不存在，或通过 `isolated_services` 位于独立的服务 realm。**修复。** 启用提供者，或从消费者的声明中移除该服务；当提供者与消费者应位于不同树时，检查 `parent_id` 与 `isolated_services`。**确认恢复。** 提供者先变为 `Active`，消费者在下一次协调时激活。

## 端口、绑定地址、NAT 与管理认证

### 端口已被占用

**症状。** 监听行激活失败，例如 `error: Activation of row 'game-listener' (plugin 'net.tcp') failed: <operating system socket error>`。**检查。** 改动前先找出占用者：Windows 上运行 `Get-NetTCPConnection -LocalPort 7110 -State Listen | Select-Object LocalAddress, LocalPort, OwningProcess`，再运行 `Get-Process -Id <owningProcess>`；Linux 上运行 `sudo ss -ltnp | grep :7110`。**可能原因。** 另一个服务器实例、同一 profile 的第二个终端，或无关进程。**修复。** 只停止已识别的进程，或用该 profile 的变量改端口（例如 `KR_812_GAME_PORT`）。不要结束机器上所有 .NET 进程。**确认恢复。** 日志出现 `Row 'game-listener' listening on <address> (protocol <profile>/game).`，端口处于监听状态。

### 客户端无法通过 NAT 或防火墙连接

**症状。** 服务器在监听，但另一台机器上的客户端无法连接。**检查。** 监听绑定 `${BFF_BIND_HOST:-0.0.0.0}`，而告知客户端的是 center gate 地址和 relay 公布中的 `${BFF_PUBLIC_HOST:-127.0.0.1}`。对外地址默认是回环，只在本机可用。在 `--dump-config` 中检查两个值，再测试连通性：Windows 用 `Test-NetConnection <public-host> -Port 7110`，Linux 用 `nc -vz <public-host> 7110`。**可能原因。** `BFF_PUBLIC_HOST` 保留默认值、防火墙拦截端口，或端口转发与公布端口不一致。**修复。** 把 `BFF_BIND_HOST` 与 `BFF_PUBLIC_HOST` 设为客户端可达的地址，只开放所选 profile 需要的端口，并转发公布的端口号。**确认恢复。** 客户端到达登录或 center 连接，连接期间 `/api/sessions` 显示该会话。

### relay 连接失败

**症状。** modern 构建可以登录，但比赛连接失败，或 relay 端口始终不监听。**检查。** `kr-1256` profile 有 `${KR_1256_GAME_PORT:-7100}` 和 `${KR_1256_RELAY_PORT:-7101}` 两个监听，并公布 `relay_advertise_ipv4` 与 `relay_advertise_port`（默认 `${KR_1256_RELAY_PUBLIC_PORT:-7101}`）。对照监听与公布值。**可能原因。** NAT 后公布端口与监听端口不一致、补丁禁用了 relay 行，或客户端是 legacy 构建。**修复。** 让公布的 relay 地址与端口可达，并保持两个 relay 值一致。legacy 构建目前没有 relay 监听（legacy bundle 中的 `No relay listener on the legacy builds yet.`），因此不要在那里期待 relay 连接。**确认恢复。** relay 端口监听，比赛连接出现在 `/api/sessions` 且 `matchBound` 已设置。

### 管理 API 或 WebUI 无法访问或未授权

**症状。** 浏览器打不开 WebUI，或 `/api/...` 返回 401 和 `{"error":"unauthorized"}`。**检查。** 管理行绑定 `${BFF_ADMIN_HOST:-127.0.0.1}:${BFF_ADMIN_PORT:-8080}`，默认为回环。以 `start` 启动的服务器对每个受保护的 `/api` 请求都要求已登录的管理员会话；`/health` 和 WebUI 文件保持开放。首次安装期间，在管理员账号建立之前 `/api/plugins` 会返回 401——这是认证模型，而非服务器故障；`/health` 和 `/api/setup/status` 仍可访问。**可能原因。** 监听器在回环上而浏览器在别处、会话已过期、给 `run --profile` 服务器配置了非回环地址（它没有账号，会拒绝该地址），或非回环的安装监听器未配置引导令牌。**修复。** 重新登录；在以 `start` 启动的服务器上，把 `BFF_ADMIN_HOST` 设为所需接口，并为非回环的安装监听器将 `bootstrap_token`（`BFF_SETUP_TOKEN`）设为至少 32 个字符。否则该行会以 `A non-loopback installation listener requires an explicit bootstrap_token of at least 32 characters.` 失败。**确认恢复。** `/health` 返回 `{"status":"ok", ...}`，且登录后的 WebUI 列出插件。

## 持久化与迁移

### MySQL 连接或认证失败

**症状。** `error: Activation of row 'persistence.mysql' (plugin 'persistence.mysql') failed: Failed to open MySQL at <host>:<port> database '<database>' as '<user>'.` **检查。** 检查 `BFF_MYSQL_HOST`、`BFF_MYSQL_PORT`、`BFF_MYSQL_USER`、`BFF_MYSQL_PASSWORD` 和 `BFF_MYSQL_DATABASE`，并确认服务器可达。**可能原因。** 主机、端口、用户或密码错误，服务器不可达，或数据库不存在。**修复。** 为刻意配置的持久化部署修正这些变量；绝不要把本地检查指向生产数据库。**确认恢复。** 日志报告 `MySQL persistence connected to <host>:<port>/<database> as <user>.`

### 迁移失败

**症状。** 读取或应用内嵌迁移时激活失败，消息为 `Embedded SQL resource '<resource>' is not named NNNN_description.sql.`、`Duplicate embedded migration version <version>.`、`No embedded SQL migrations were found.` 或 `Embedded migration '<resource>' is empty.` 之一。**检查。** 迁移是内嵌资源，在插件以 `auto_migrate: true` 激活时按文件名顺序应用。**可能原因。** 插件构建损坏，或迁移资源被改名、清空。**修复。** 不要手工修改运行中的 `schema_migrations` 表；从干净检出重新构建插件。没有降级迁移路径，如需回到旧构建，请恢复 schema 变更前的备份。**确认恢复。** 激活时为每个待应用迁移记录一行 `Applied migration <version> (<name>).`，随后是连接成功行。

### 同时启用了两个持久化提供者

**症状。** 持久化行失败并报告 `Service '<contract>' is already registered in realm <id>.` **检查。** `persistence.memory`、`persistence.sqlite` 和 `persistence.mysql` 中只能启用一个。检查 `--dump-config` 中的 `disabled` 标志；隔离补丁启用 memory 并禁用 MySQL，但保持 SQLite 禁用。**可能原因。** 补丁启用了第二个提供者却没有禁用第一个。**修复。** 在同一补丁中禁用不用的提供者。**确认恢复。** 恰好一个持久化提供者为 `Active`，其余为 `Unloaded`。

### SQLite 路径或锁定问题

**症状。** SQLite 行激活失败，或写入以 `SQLite transaction committed but WAL truncation failed; further operations are refused.` 失败。**检查。** `database_path` 必须是包含文件名的绝对路径；相对路径会以 `database_path must be an absolute file path.` 失败。把存档放在发布目录和日志目录之外，每个存档只运行一个服务器。锁等待为 `busy_timeout_ms`，默认 5000。**可能原因。** 相对路径、只读目录，或两个进程打开同一存档。**修复。** 修正路径与权限，停止第二个进程。**确认恢复。** 日志报告 `SQLite save active; capacity <players> players, <accounts> accounts, <bytes> main-file bytes.`

### 重启后数据为空

**症状。** 停止并重新启动服务器后，账号、物品或进度消失。**检查。** `--dump-config` 显示启用的持久化行。使用隔离补丁或任何启用 `persistence.memory` 的配置时，状态只存在于进程内；激活日志会说明：`In-memory persistence active for up to <players> players and <movement actors> movement actor(s); state is process-local and resets on restart.` **可能原因。** 误以为内存持久化是持久的。**修复。** 需要持久状态时启用 `persistence.sqlite` 或 `persistence.mysql` 并禁用其他提供者；本地实验可保留隔离补丁并接受重置。**确认恢复。** 使用持久化提供者重启后，同一账号可以登录。

### 恢复备份

**症状。** 需要撤销一次数据库变更。**检查。** 服务器没有恢复数据库的命令；恢复是数据库层面的操作，操作前必须停止服务器。**可能原因。** 错误的迁移、误写或失败的升级。**修复。** 恢复变更前的备份，然后用与恢复的 schema 匹配的配置启动服务器。不要编辑 `schema_migrations` 或删除行来伪造回滚。**确认恢复。** `/health` 正常，持久化行为 `Active`，预期的账号与数据存在。

## 客户端数据

### clientdata 行激活失败

**症状。** `error: Activation of row 'clientdata' (plugin 'clientdata') failed: clientdata: neither data_directory '<base>/client-data/<profile>/data' nor raw_directory '<base>/client-data/<profile>/dataraw' exists. Provision the configured client-data directories before enabling this row.` **检查。** 游戏 profile 解析 `${BFF_CLIENT_DATA:-client-data}/${BFF_PROFILE_ID}/data` 和 `.../dataraw`，相对于工作目录。把错误中的两个路径与磁盘文件和 `--dump-config` 中的 `BFF_CLIENT_DATA` 对照。**可能原因。** 没有为该构建提供客户端数据、`BFF_CLIENT_DATA` 指向别处，或服务器从不同目录启动。**修复。** 提供匹配的本地客户端数据，把 `BFF_CLIENT_DATA` 指向其根目录，或在只需要管理与存储时使用 `management` profile。仓库不包含客户端或其专有数据。**确认恢复。** `clientdata` 行变为 `Active`，游戏 profile 以 `Server started with <N> active row(s).` 启动。

### 客户端文件缺失或版本不匹配

**症状。** 功能以 `clientdata: no client file '<path>' in the archives or the loose files.` 失败，或 `/api/logs` 记录 `Session <id> reported client version <reported>, expected <expected>.` **检查。** 文件缺失时，确认 `data/` 中的相对路径或 `dataraw/` 中的松散文件使用客户端自己的拼写；松散文件优先于归档。版本不匹配时，比较客户端的 `ClientVersion_Major` 与 profile 的 `client_build` 和 profile id。**可能原因。** 客户端数据不完整、归档未复制，或客户端构建与所选 profile 不匹配。**修复。** 复制该构建的完整数据，并启动与客户端构建匹配的 profile；不要为了让 profile 接受代码不支持的构建而修改它。**确认恢复。** 功能能读取文件，会话连接时不再出现版本不匹配警告。

### 安装向导的客户端数据导入失败

**症状。** 安装向导报告导入失败，或 API 以 400、413 或 409 返回 `{"error":"client_data_invalid"}`、`{"error":"client_data_limit"}` 或 `{"error":"client_data_busy"}`。**检查。** 导入接受已整理的文件夹或传统 ZIP。默认限制为一份副本上传 8 GiB、解压 16 GiB、32,768 个文件或 ZIP 条目、保留 64 份副本、保留存储 64 GiB、时限 20 分钟。不支持 ZIP64 和分卷归档；`client_data_busy` 表示已有导入在运行。**可能原因。** 文件过多或过大、导入器无法识别的目录布局，或并发的第二次导入。**修复。** 在限额内导入 `<build>/data` 与 `<build>/dataraw` 布局，部署确有需要时可在 `installation` 行的允许范围内调整限额，并等待正在运行的导入结束后重试。**确认恢复。** 导入返回根路径，匹配 profile 的 `clientdata` 行激活。

## SDK 插件问题

### SDK 插件进程无法启动

**症状。** `error: Activation of row '<row id>' (plugin 'loader.process') failed: SDK plugin process '<command>' could not start in '<directory>': <reason>` 或 `SDK plugin process '<command>' did not start.` **检查。** 检查 loader 行的 `plugins[].command`、`args`、`cwd` 和 `env`；带目录部分的命令相对 `base_dir` 解析，其余在 `PATH` 中查找；缺少 `cwd` 目录会以 `plugins[<n>].cwd: directory '<path>' does not exist` 配置失败。**可能原因。** 解释器或可执行文件未安装、工作目录错误，或 `PATH` 中没有该命令。**修复。** 安装运行时、修正 `command`/`cwd`，或传绝对路径。**确认恢复。** 该行激活，插件 id 以 `state` `Active` 出现在 `/api/plugins`。

### 插件向 stdout 写数据

**症状。** `error: Activation of row '<row id>' (plugin 'loader.process') failed: SDK plugin process broke the protocol: stdout carried a line that is not JSON (<detail>); stdout is reserved for the protocol, use stderr for diagnostics.` **检查。** 找出向 stdout 写数据的打印、横幅或进度输出。**可能原因。** 调试打印、库横幅或 stdout 上的进度条。**修复。** 所有诊断写入 stderr；stdout 只保留 JSON-RPC 行。**确认恢复。** 插件完成握手并激活。

### 协议或能力不匹配

**症状。** 声明或协商失败，例如 `SDK plugin source '<source>' answered initialize without a supported protocol_version (1 or 2).`、`protocol v1 allows only IMessageRouter in requires and optional.` 或 `requires_many and optional_many require protocol v2.` **检查。** 对比插件清单与 Host 支持的协议版本，以及各版本允许的声明；类型化消息、事件、提供者和装饰器需要协议 v2。**可能原因。** 旧 SDK 或清单固定在协议 v1，却使用 v2 声明。**修复。** 更新 SDK 或清单，或把声明限制在 v1 支持的范围。**确认恢复。** 来源发布其定义，依赖行激活。

### 调用超时或被拒绝

**症状。** `SDK plugin <label> did not answer '<method>' within <n> ms (request_timeout_ms).` 或 `SDK plugin <label> already has <n> requests in flight (max_in_flight <n>).` **检查。** 检查 loader 行的 `request_timeout_ms` 与 `max_in_flight`，并观察插件在该调用中做了什么；事件循环被阻塞会在这里显现。**可能原因。** 处理器过慢或阻塞，或突发超过在途上限。**修复。** 让处理器尽快返回，只在有测量依据时调高上限，并且只重试幂等操作——超时不会撤销插件已经提交的变更。**确认恢复。** 调用在配置的超时内完成，在途计数保持在上限以下。

### WebAssembly 模块无法加载

**症状。** `error: Activation of row '<row id>' (plugin 'loader.wasm') failed: WebAssembly plugin '<module>' exports no '_start'; build it as a WASI command (wasm32-wasip1 or GOOS=wasip1).` 或 `WebAssembly plugin '<module>' could not be loaded: <reason>`. **检查。** 检查 `loader.wasm` 行的 `plugins[].module` 和模块的构建目标。**可能原因。** 模块为错误目标构建，或路径不存在。**修复。** 用 Rust SDK（`wasm32-wasip1`）或 Go SDK（`GOOS=wasip1`）构建 WASI 命令模块，然后修正模块路径。**确认恢复。** 模块被接受，其定义激活。

## 报告问题

提供足以让他人复现的信息。每个问题使用一份报告，模板如下：

```text
Source revision: <git rev-parse --short HEAD，或发布/包版本>
Operating system: <Windows 11 x64 / Debian 13 x64 / 容器镜像>
Profile: <profile id，或 bff profiles list 中对应的一行>
Command: <完整命令；本地检查需包含 --patch tools/local-checks/isolated-memory.patch.yaml>
Expected: <本应发生什么>
Actual: <实际发生了什么，含退出码>
First error: <第一行 error: 消息>
Affected rows: <来自 /api/plugins 的 rowId、state 与 error>
Evidence: <控制台摘录、TRX 结果、转储路径>
Source of the observation: <真实游戏客户端、自动测试，或两者>
```

附加任何内容前，移除令牌、密码、账号名、玩家身份、公开地址以及包含个人数据的 packet 载荷。不要附加客户端数据、专有文件或私有调查材料。`--dump-config` 输出可能包含插值后的凭证，请脱敏。

请明确说明问题是在真实游戏客户端中观察到，还是只在自动检查中出现；自动检查通过或失败都不代表客户端兼容性。

## 后续

- [配置](configuration.md)：profile、补丁、环境变量、端口与持久化。
- [部署](deployment.md)：为他人运行服务器，以及升级与回滚。
- [插件](plugins.md) 与 [SDK 协议](sdk-protocol.md)：激活与生命周期细节。
- [启动器](launcher.md)：启动器账号、会话票据、启动器 API 版本门禁以及配套启动器自身的错误。
