---
title: "部署"
description: "使用发布构建、Docker Compose、备份与回滚可复现地运行服务器。"
updated: 2026-10-18
---

本页面向为其他人运行 Bubble Fighter Fantasy 的场景。内容涵盖发布构建、本仓库的 Docker Compose 配置、Linux 与 Windows 手动安装、持久数据与备份、日志、网络安全，以及如何安全升级与回滚。

**本页面向：** 部署并维护服务器进程的运维人员，而不只是在笔记本上启动一次。

**开始之前：**

- 阅读[配置](configuration.md)。部署就是选择 profile 再加上环境变量；本页不重复组合机制。
- 构建并校验你准备部署的确切版本。发布目录来自一次零警告的 `dotnet build BubbleFighter.Fantasy.sln -c Release`。
- 规划主机：受支持的操作系统与架构、依赖框架构建所需的 ASP.NET Core Runtime 10（或 Docker）、专用服务账号，以及你自己拥有的数据库。
- 为将要服务的每个构建准备客户端数据。缺少数据时游戏 profile 会在激活阶段失败。
- 确定备份存放位置。备份不能放在发布目录内。

## 部署之前

这是一个预发布项目。它尚未针对所有受支持客户端验证，列出的 profile 或通过的自动化检查都不构成真实客户端兼容性。请把它当作预发布服务运行，用真实客户端测试，并保留恢复路径。

本次发布的受支持目标为 Windows x64、Windows ARM64、Linux x64 与 Linux ARM64。已知的部署环境是 Debian 13；其他 Linux 发行版未声明经过测试。发布脚本覆盖 Windows x64 与 Linux x64/ARM64；Windows ARM64 是列出的目标，使用相同的 Host 发布命令并指定 `-r win-arm64`。

公开的 Windows 包、Linux 安装脚本与一键 Docker 安装尚未发布。本仓库现有的是源码、`tools/` 中的两个发布脚本、`deploy/docker/` 中的 Docker 文件以及 Compose 配置。不要用其他来源的安装器或镜像仓库替代它们。

部署需要规划四类网络与存储事项：

| 事项 | 需要规划的内容 | 本次发布的默认值 |
| --- | --- | --- |
| 游戏端口 | 每个提供服务的构建一个 TCP 监听器。 | `kr-1256` 使用 7100；旧版 `kr-812` 使用 7110。 |
| 中继 | 只有 profile 打开中继监听器的构建才需要。 | `kr-1256` 使用 7101；旧版构建不打开。 |
| 管理 / WebUI | 保持在回环或受控网络；远程管理需要以 `start` 启动的服务器，并使用管理员账号登录。 | `127.0.0.1:8080`。 |
| MySQL | 只允许服务器访问，绝不公开。 | `127.0.0.1:3306`，数据库 `bf_game_db`。 |

请把客户端数据放在本地存储上，并尽可能以只读方式挂载或复制。服务器只读取它。

## 构建发布目录

`tools/` 中的两个脚本把 Host 和所有插件发布到一个可复制到目标机器的目录。两者都产生**依赖框架**的构建：不捆绑 .NET 运行时，因此目标机器需要匹配的 ASP.NET Core Runtime 10。

| 脚本 | 目标 | 说明 |
| --- | --- | --- |
| `tools/publish-windows-x64.ps1` | `win-x64` | 校验 apphost 是 Windows x64，且不包含捆绑运行时。 |
| `tools/publish-linux.ps1` | `linux-x64`（默认）或 `linux-arm64` | 校验 apphost 是指定架构的 ELF 文件。 |

两个脚本都需要 PowerShell 会话，并接受必填的 `-OutputPath`。目录可以不存在；若已存在则必须为**空**：向包含旧发布版本的目录发布会保留那个版本的运行时文件。脚本会失败而不是覆盖。

### Windows

在仓库根目录的 PowerShell 提示符下：

```powershell
.\tools\publish-windows-x64.ps1 -OutputPath C:\BFF\release-2026-10-18
```

成功运行会打印 `Windows x64 framework-dependent publish ready: <path>`，并提示目标机器需要 x64 ASP.NET Core Runtime 10.0。

### Linux

在任意机器的 PowerShell 7 会话中发布；输出为 Linux 二进制文件：

```powershell
pwsh -File tools/publish-linux.ps1 -OutputPath /srv/bff-release/server
```

为 ARM64 目标添加 `-Runtime linux-arm64`。成功运行会打印 `Linux framework-dependent publish ready: <path> (<runtime>)`，并检查 InGame 插件已发布。PowerShell 只是脚本宿主；目标机器不需要 PowerShell。

### 发布内容

- `BubbleFighter.Fantasy.Host`（apphost）与 `BubbleFighter.Fantasy.Host.dll`，以及 Host 的依赖和 `runtimeconfig.json`。
- `plugins/<ProjectName>/`，每个插件项目一个目录，也是 Host 旁的默认插件根目录。
- `profiles/`，每个 profile 一个目录，不包含任何 `local.patch.yaml`。
- `packets/`，包含封包表与 `builds.yaml`。
- `bootstrap/`，包含 loader profile；以及 `data/level-experience.csv` 与 `data/locales/*.json`。

脚本的替代做法是 `dotnet publish src/BubbleFighter.Fantasy.Host -c Release -o <directory>`，它执行相同的 Host 发布，包括插件发布目标和内容复制。它跳过脚本的空目录与 apphost 检查，因此请使用新目录并自行校验结果。Windows ARM64 请使用这种形式并加 `-r win-arm64`。

复制前先检查结果：目录中包含 Host 可执行文件、每个插件一个目录的 `plugins/`，以及 `profiles/`、`packets/`、`bootstrap/` 和 `data/` 目录。不应出现 `coreclr.dll`、`libcoreclr.so` 或同类运行时文件；若发现这些文件，说明发布捆绑了运行时，不是你想要的发布版本。

## 从本仓库使用 Docker Compose

`deploy/docker/compose.yaml` 使用本仓库源码启动服务器和 MySQL 数据库，是获得可运行容器配置的最快途径。

1. 复制环境文件，设置非空数据库密码和安装令牌。Compose 文件要求 `BFF_MYSQL_PASSWORD` 与 `BFF_SETUP_TOKEN`（至少 32 个随机字符），缺少时拒绝启动：

   ```sh
   cp .env.example .env
   ```

2. 在 Compose 文件查找的位置准备客户端数据：为将要选择的每个版本在仓库旁准备 `client-data/<build>/data` 与 `client-data/<build>/dataraw`，或用 `BFF_CLIENT_DATA_HOST` 指向其他位置。它会挂载到容器内的 `/app/client-data`。

3. 在仓库根目录构建并启动两个服务：

   ```sh
   docker compose -f deploy/docker/compose.yaml up --build
   ```

这会启动：

- `bff` 服务从 `deploy/docker/Dockerfile` 构建：基于 .NET SDK 与 ASP.NET 10 镜像的多阶段构建，以非特权用户 `bff`（uid 1001）运行。它不带命令运行宿主，即首次安装：打开 `http://<docker-host>:8080`，页面要求时输入 `BFF_SETUP_TOKEN`，创建管理员账号，选择 MySQL，主机填 `mysql`，数据库名、用户与密码取自 `.env`，并以数据目录 `/app/client-data` 选择客户端。之后的启动会运行已保存的安装，管理员使用该账号登录。它发布 7100（游戏）、7101（中继）与 8080（管理与 WebUI）；Compose 设置了 `BFF_ADMIN_HOST=0.0.0.0`，使发布的管理端口能到达容器。
- `mysql` 服务运行 MySQL 8.4，根据 `BFF_MYSQL_DATABASE`、`BFF_MYSQL_USER` 与 `BFF_MYSQL_PASSWORD` 创建数据库和账号，在主机上发布 `${BFF_MYSQL_PORT:-3306}`，并把数据保存在 `mysql-data` 数据卷中。服务器会等待其健康检查通过后启动，并在容器网络内收到 `BFF_MYSQL_HOST=mysql`。
- 客户端数据以只读方式挂载到 `/app/client-data`；已保存的安装位于 `bff-state` 卷，数据库状态位于 `mysql-data` 卷。服务器自身的日志写在容器内，此 Compose 文件不会把它们保存到主机。

确认是否正常：在 Docker 主机上执行 `curl http://127.0.0.1:8080/health` 会返回带 `status` 字段的 JSON，控制台会显示安装或运行进度。可用以下命令跟踪控制台：

```sh
docker compose -f deploy/docker/compose.yaml logs -f bff
```

用 `docker compose -f deploy/docker/compose.yaml down` 停止两个服务。这会保留数据库卷。`down -v` 还会删除它，因此除非确实要丢弃数据库，否则不要使用 `-v`。

对外暴露前请审查该 Compose 文件：它把 MySQL 发布到主机，保留 Docker 的默认日志处理（未配置大小限制），并运行默认游戏 profile。它属于开发风格配置，不是加固过的生产模板。

## 在 Linux 发布根目录使用外部数据库的 Docker

`deploy/docker/compose.external-db.yaml` 是另一种产物：面向已在运行数据库的 Linux 主机上的发布根目录模板。它期望以下布局，由你在目标机器上创建：

```text
<release-root>/
  compose.yaml     deploy/docker/compose.external-db.yaml 的副本
  Dockerfile       deploy/docker/Dockerfile.published 的副本
  .env             部署变量，包括 BFF_RELEASE
  server/          依赖框架的 Linux 发布输出
  logs/            由容器写入
  client-data/     可选；或用 BFF_CLIENT_DATA_HOST 指向其他位置
```

1. 使用 `tools/publish-linux.ps1` 把发布输出发布到 `server/`。发布目录必须为空，因此在复制其他内容前先把发布输出写入 `server/`。
2. 把 `deploy/docker/compose.external-db.yaml` 复制为 `<release-root>/compose.yaml`，把 `deploy/docker/Dockerfile.published` 复制为 `<release-root>/Dockerfile`。
3. 创建 `<release-root>/.env`。它必须设置 `BFF_RELEASE`（镜像标签）以及本地数据库的 MySQL 变量。请审查地址和令牌：模板会强制设置 `BFF_BIND_HOST`、`BFF_MYSQL_HOST` 和管理地址。
4. 在 `<release-root>` 中启动：

   ```sh
   docker compose up -d --build
   ```

该模板提供：从 `server/` 构建的 `bf-fantasy:<release>` 镜像；主机网络，因此 `.env` 中配置的游戏、中继和管理端口直接使用，且可访问 `127.0.0.1` 上的数据库；只读容器文件系统加上小型可写 `/tmp`；丢弃全部 capability；`no-new-privileges`；30 秒停止宽限期；除非停止否则重启；把 `./logs` 挂载到 `/app/logs` 以保存封包日志文件；客户端数据以只读方式挂载到 `/app/client-data`；json-file 日志限制为 5 个 10 MB 文件。

该 Dockerfile 变体以 `run --profile all` 启动镜像，因此发布版本会在各自端口上服务所有已接入构建，并需要这些构建的客户端数据。若只服务一个构建，请修改你那份 Dockerfile 中的 `CMD`，该文件由运维人员管理。

`docker compose down` 停止服务并保留 `logs/` 目录。数据库不由该模板管理：请用其自身的工具备份和升级。`deploy/docker/Dockerfile.published` 是该布局使用的仓库文件；没有任何镜像发布到镜像仓库，因此镜像始终在目标机器上构建。

## Linux 手动部署

不想使用容器时可采用手动方式：把 Linux 发布输出复制到目标机器并直接运行 Host。

1. 使用 `tools/publish-linux.ps1` 发布，并把输出复制到 `<release-root>`。保持发布的目录结构不变。
2. 创建专用服务账号（例如 `bff`），拥有发布目录但没有交互式登录。Host 只需读取 `profiles/`、`packets/`、`bootstrap/`、`plugins/` 和 `data/`；它需要可写的工作目录来写 `logs/`，使用 SQLite 时还需要可写位置存放数据库文件。
3. 如果复制过程丢失了执行权限，请让 apphost 可执行：`chmod +x BubbleFighter.Fantasy.Host`。
4. 在进程环境中提供部署变量。发布目录不包含仓库标记，因此放在旁边的 `.env` **不会**被 Host 读取；`.env` 加载规则只在检出目录内生效。请使用由服务管理器或 shell 加载的环境文件，例如：

   ```sh
   set -a
   . <release-root>/deployment.env
   set +a
   ```

   该文件包含与 `.env.example` 相同的 `KEY=VALUE` 项，填入真实的 MySQL 密码、公共地址以及管理令牌。

5. 进入发布根目录并以前台方式启动 Host：

   ```sh
   cd <release-root>
   ./BubbleFighter.Fantasy.Host run --profile kr-1256
   ```

   工作目录很重要：`profiles`、`packets`、`client-data` 和 `logs` 都相对于它解析。`Ctrl+C` 或 SIGTERM 请求正常关闭，控制台最后输出 `Shutdown complete.`。

6. 仓库不提供服务单元。长期运行时，请把 Host 放在你管理的服务管理器（例如 systemd 服务）下，把工作目录设为 `<release-root>`，并通过其环境文件提供变量。保持服务账号非特权，只授予它发布目录、日志目录以及（若使用）SQLite 文件的访问权限。

在主机上执行 `curl http://127.0.0.1:8080/health` 检查，并用下面的监听端口检查。用服务管理器的 SIGTERM 或前台的 `Ctrl+C` 停止。

## Windows 手动部署

1. 使用 `tools/publish-windows-x64.ps1` 发布，并把输出复制到部署目录。
2. 在启动前为进程设置部署变量，例如在将拥有该进程的 PowerShell 会话中：

   ```powershell
   $env:BFF_MYSQL_HOST = '127.0.0.1'
   $env:BFF_MYSQL_PASSWORD = '<your-database-password>'
   $env:BFF_PUBLIC_HOST = '<public-address-clients-use>'
   ```

   与 Linux 一样，复制到发布目录中的 `.env` 不会被读取，因为 Host 只有在能找到仓库根目录时才加载仓库 `.env`。

3. 从发布目录启动 Host，使相对的 `profiles`、`packets`、`client-data` 和 `logs` 路径在此解析：

   ```powershell
   Set-Location C:\BFF\<release>
   .\BubbleFighter.Fantasy.Host.exe run --profile kr-1256
   ```

4. 只允许计划中的游戏、中继和管理端口通过防火墙。管理端口只对可信网络可达，并让 MySQL 远离公共接口。
5. 本仓库没有 Windows 服务包装器。若必须随机器启动，请在你自己管理的服务管理器下运行可执行文件；保持服务账号非特权。

用 `Invoke-RestMethod http://127.0.0.1:8080/health` 检查，它返回带 `status` 字段的 JSON。用 `Ctrl+C` 停止；在平台提供相应信号时，Host 也处理 SIGTERM 与 SIGINT。

## 持久数据与备份

| 数据 | 位置 | 升级前是否备份 |
| --- | --- | --- |
| 游戏与账号数据 | MySQL 数据库 `bf_game_db`，或一个 SQLite 存档文件。 | 是；每次升级都要。 |
| 客户端数据 | `client-data/<build>/` 下的只读文件。 | 一次即可，或更换客户端副本时。 |
| 配置与机密 | Compose 的 `.env` 或服务管理器的环境文件。 | 是；妥善保管。 |
| 日志与封包日志 | 工作目录下的 `logs/<date>/`，或容器内路径。 | 可选；按策略轮转与保留。 |
| 旧发布版本 | 独立目录。 | 保留到回滚窗口关闭。 |

请把数据库、其备份以及 SQLite 存档文件放在发布目录之外，因为发布目录会被替换。MySQL 请在服务器停止或无写入时使用数据库自身的备份工具，例如逻辑转储（`mysqldump`）或一致性存储快照。SQLite 请先停止服务器，再复制单个存档文件。服务器正在写入时绝不复制运行中的 SQLite 文件。

在依赖备份前先在独立实例上测试恢复：恢复数据库或复制存档文件，用它启动发布版本，并确认 `/health`、插件行状态和客户端流程。从未恢复过的备份不算经过验证的备份。

客户端数据是输入而非状态：服务器只读取它，Docker 配置以只读方式挂载它，因此替换发布版本不会改变它。

## 日志

服务器向控制台输出日志。每行是单行并带 UTC 时间戳；级别来自 `BFF_LOG_LEVEL` 或 `--log-level`，默认 `Information`。正常启动会报告每个已激活的行、每个监听器的监听地址，就绪后输出 `Server started with <n> active row(s).`。正常停止以 `Shutdown complete.` 结束。

诊断行还会写封包日志文件。它们与控制台分开，每个封包和连接事件一行：

- 默认路径是相对于工作目录的 `logs/{date}/packets_{timestamp}.log`。profile 会覆盖文件名：`kr-812` 写 `packets-kr-812_{timestamp}.log`，`all` profile 写 `packets-all_{timestamp}.log`。
- 每个文件达到 10 MiB 时轮转为 `.1`；`{date}` 变化时开始新文件。默认按配置的文件名模式保留最多 100 个文件，写入器启动时会删除更旧的文件。文件名时区默认为 China Standard Time。
- 写入经过有界队列（默认 32768 条），关闭时最多等待 10 秒刷新。溢出会以 `packet-log-overflow` 报告，绝不丢弃游戏封包。
- 完整数据包记录默认关闭。设置 `BFF_PACKET_CAPTURE=true` 可启用，并在同一 `logs/` 目录树下写出独立的 JSONL 数据包文件。

封包日志行和捕获可能包含玩家与连接数据；请把 `logs` 目录视为敏感数据并套用你的保留策略。在 Docker 配置中，发布根目录模板挂载 `./logs`，使封包文件保存在主机上，而控制台输出进入容器的 json-file 日志，限制为 5 个 10 MB 文件。

## 管理与网络安全

- 管理监听器默认绑定 `127.0.0.1:8080`。以 `start` 启动的服务器（可执行文件不带参数时即如此）要求管理员使用账号和密码登录；`/health` 和 WebUI 文件保持开放，以便浏览器加载登录页。以 `run --profile` 启动的服务器没有账号，无需登录，并且只在回环地址上监听。
- 在把管理端口暴露到回环之外前，请限制来源网络。若无法限制，就让它保持在回环，并使用你自己管理的隧道或反向代理。
- 防火墙只开放所选 profile 提供服务的游戏与中继端口。不要把管理端口或 MySQL 发布到公共接口。
- 使用专用数据库账号，把 MySQL 端口保持在回环或私有网络，绝不要把数据库管理员账号复用于服务器。
- 机密保存在部署本地的 `.env`（Compose）或服务管理器环境文件（手动）中。限制其权限，绝不提交。`--dump-config` 会显示插值后的机密，不要把它的输出附到公开报告中。
- 不要公开客户端数据、封包日志或捕获文件；它们可能包含专有客户端文件和玩家数据。

## 升级与回滚

升级采用并排方式：把新发布版本安装到新目录，保留旧版本，通过停止一个进程再启动另一个来完成切换。

1. 公布维护窗口并停止接收新工作。让比赛结束或有意终止；然后用 SIGTERM 或 `Ctrl+C` 正常停止服务器，等待 `Shutdown complete.`。
2. 按上述方式备份数据库，并记录正在运行的准确发布目录与配置。
3. 把新发布版本发布到全新的空目录。不要覆盖旧发布版本。
4. 复制或沿用部署变量与客户端数据。除非更新客户端，客户端数据在版本之间不变。
5. 从新目录启动新发布版本并校验：`/health`、插件行状态、监听端口、存储连接行，以及控制台中无错误。
6. 保留旧发布目录和升级前备份，直到不再需要回滚。

回滚时，停止新发布版本，用相同配置启动旧发布目录；若新版本改动了数据库结构，则恢复升级前的备份。仅回滚二进制并不保证可行：MySQL 迁移在持久化行激活时自动应用，且仅向前。服务器没有降级路径，因此较旧的二进制可能无法在较新版本已迁移的数据库上运行。安全的回滚组合是旧发布版本加上升级前所做的备份。

## 部署后检查

- [ ] `GET /health` 返回 `status` 为 `ok` 的 JSON。
- [ ] WebUI 插件列表（或已登录会话中的 `GET /api/plugins`）显示每个预期行，且没有失败状态。
- [ ] 主机上的监听端口符合计划；Linux 用 `ss -ltn`，PowerShell 用 `Get-NetTCPConnection -State Listen` 检查。
- [ ] 控制台显示存储连接行：MySQL 已连接，或 SQLite 已激活。
- [ ] 每个提供服务的游戏构建的 `clientdata` 行处于激活状态，而非失败。
- [ ] 日志级别适合生产；若预期有封包日志，封包日志目录可写且在增长。
- [ ] 已取得备份，并至少在一个测试实例上恢复过一次。
- [ ] 由所有者用真实客户端测试过。自动化检查与列出的 profile 都不等于客户端验收。

## 遇到问题时

- **容器立即退出。** 默认 profile 是游戏 profile，其客户端数据缺失。添加 `client-data/<build>/data` 或 `dataraw`，或运行不需要客户端数据的 profile。
- **服务器无法连接 MySQL。** 检查地址、端口、账号和数据库名。发布目录不会读取本地 `.env`，因此请在进程环境中核对变量。
- **`/api` 返回 401。** 请求没有已登录的管理员会话；请先在 WebUI 中登录。
- **端口已被占用。** 停止冲突进程或修改按 profile 的端口变量，然后重启。

按顺序排查的清单见[故障排查](troubleshooting.md)。

## 下一步

阅读[配置](configuration.md)了解上面用到的 profile、端口和存储键，以及[插件](plugins.md)了解行的激活与重载方式。

## 安装凭据

`state/administration/installation.json` 会以明文保存 MySQL 密码，以便服务器重启后重新连接。请只允许所有者访问状态目录：Windows 使用文件安全属性仅授予服务账号权限；Linux 使用 `chown` 设置服务用户，并执行 `chmod 700 state/administration` 和 `chmod 600 state/administration/installation.json`。远程 MySQL 使用 `SslMode=None` 时服务器会发出警告；数据库不在本机时应启用 TLS。
