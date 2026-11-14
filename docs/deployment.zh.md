---
title: "部署"
description: "使用发布构建、Docker Compose、备份与回滚可复现地运行服务器。"
updated: 2026-11-09
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
| 管理 / WebUI | 以 `start` 启动的服务器默认可从其他机器访问，并使用管理员账号登录；`BFF_ADMIN_HOST=127.0.0.1` 可只在本机监听。`run --profile` 服务器没有账号，只在回环地址监听。 | `0.0.0.0:7300`（`start`），`127.0.0.1:7300`（`run`）。 |
| MySQL | 只允许服务器访问，绝不公开。 | `127.0.0.1:3306`，数据库 `bf_game_db`。 |

请把客户端数据放在本地存储上，并尽可能以只读方式挂载或复制。服务器只读取它。

## 可选的宠物共享装备槽

`equipment` 行的 `pet_shared_slot` 选项默认为 `false`。启用后只允许穿戴一只宠物，宠物身份取自该构建自身 `Config/ShopItem2.xml` 中 function 为 `10001` 的条目。装备第二只宠物会失败，必须先卸下当前宠物。规则也会在库存事务内检查最终目标，并在读取装备展示时归一化历史上的多宠物穿戴状态。已有物品、数量和无关标志位保持不变。

| `all` profile 中的构建 | 宠物槽开关 |
| --- | --- |
| `kr-812`、`kr-1256`、`cn-406`、`cn-404`、`cn-408`、`cn-706`、`cn-717`、`id-400`、`id-1809`、`cn-774`、`tw-454` | 提供该构建的客户端数据和持久化装备服务后可启用。 |
| `cn-279`、`cn-324` | 激活时拒绝：当前目录没有宠物条目，也尚未建立其原生宠物选择路径的证据。 |

例如，在 `profiles/kr-1256/local.patch.yaml` 中加入以下内容，为该构建启用功能。`replace` 会替换整行配置，因此要保留部署中已有的其他覆盖值。其他受支持构建应使用自己的 profile 目录，并复制有效配置中的 `protocol`、`client_profile` 和 `version_major` 值。

```yaml
- replace:
    id: equipment
    config:
      protocol: kr-1256/game
      client_profile: kr-1256
      version_major: 1256
      pet_shared_slot: true
```

保持 `persist_worn_state: true` 和库存展示服务启用。缺少客户端数据、构建不受支持或装备组重叠时会明确报错。此选项涵盖已拥有宠物的装备和从库存存储恢复；不实现喂食、成长、进化或能力变更，这些操作需要持久化的逐宠物状态及各自经验证的规则。部署此选项前，请使用目标原生客户端验证装备、拒绝第二只宠物、卸下与重新登录恢复。

## 可选的宠物成长与技能

独立的宠物组合补丁提供喂养、成长、技能替换与回退，以及可选的对局奖励和战斗效果。所有开关默认关闭，仍需原生客户端验收。宠物所有权跨版本共享，成长与技能按版本分别保存。现代版 Article 身份由持久化存储分配；两个背包引用同一所有权，移除后重新发放会产生新身份。

`kr-812`、`kr-1256`、`cn-406`、`cn-404`、`cn-408`、`cn-706`、`cn-717`、`id-400` 和 `id-1809` 的配置档已包含这些补丁，喂养、成长与技能默认开启；奖励、战斗效果与里程仍保持关闭。设置 `BFF_PETS_ENABLED=false` 可同时关闭成长、技能与单宠物装备槽。每个配置档以 `PET_CLIENT_UTC_OFFSET_MINUTES` 声明客户端所在地区的民用时区（韩国 540，中国与台湾 480，印尼 420）。`cn-774` 和 `tw-454` 暂未包含，因为它们的配置档尚未挂载喂养所需的原生物品使用处理器。

在目标版本的常规补丁后加入 `profiles/bundles/pets.patch.yaml`。`kr-1256`、`cn-774` 和 `tw-454` 还需加入 `pets-modern.patch.yaml`；韩服或台服最后分别加入 `pets-kr-1256.patch.yaml` 或 `pets-tw-454.patch.yaml`。其他已核对的宠物版本使用基础宠物补丁。这些补丁挂载 `pets.entitlements`、`pets` 和 `pets.combat-effects` 行。`BFF_PETS_ENABLED` 也会启用 `equipment.pet_shared_slot`，确保同一时间只能佩戴一只宠物。自定义成长补丁时应保留此装备规则。宠物行的版本键必须与库存行过滤后的目录一致。

`pets` 行需要同协议的房间策略服务。宠物装备、卸下、喂养、升级和技能请求会暂缓手动与自动开局，直到提交后的库存及外观发布结束。已经开始的房间会拒绝这些请求，同一会话的第二个并发宠物请求也会被拒绝。`max_in_flight_sessions` 限制活动请求数量，`max_held_rooms` 限制保留的开局记录；两者默认 128，范围为 1–65536。恢复任务最多额外持有一条已领取记录。容量耗尽会拒绝新的修改或报告开局记录超限错误，应按该协议的预期并发量配置。恢复开局前仍会重新评估其他房间策略。

开启 `BFF_PETS_ENABLED` 前，须在两个宠物行明确设置 `client_utc_offset_minutes`：原生喂养日期使用客户端本地时钟。两个行的喂养策略按下表设置：

| 客户端策略 | 必要配置 |
| --- | --- |
| 自动成长 | `client_configuration_source: None`；不设置非零喂养冷却。 |
| KR1256 与 CN774 | `client_configuration_source: DynamicConfig`，明确的 `feed_cooldown_seconds` 和正数 `mileage.display_maximum`。即使关闭里程累积，已挂载的方言也必须能接收并发布这两个值。 |
| TW454 | `client_configuration_source: NativePetConfig`、`native_pet_config_path: Config/PetConfig.xml`，以及与该文件 `PetSystem.FeedCoolTime` 分钟值一致的明确冷却时间。已核对文件为 420 分钟，即 25200 秒。台服不接收韩服、国服的配置字段。 |

`skill_resets_enabled` 单独开启。存在原生里程余额的版本必须配置完整且启用的 `mileage` 策略：为每种重置消耗品明确指定收益，包括主动选择零收益，并指定显示上限。可选兑换还须明确阈值、`Single` 或 `All` 模式、`Preserve` 或 `Discard` 余数策略、有界兑换次数，以及明确物品种类和数量的奖励组合。不假定原服的收益、阈值或奖励数值。已核对的台服目录没有里程余额。消耗品、成长、里程与奖励在同一事务提交，拒绝时不消耗物品。

奖励和战斗效果分别开启，并明确填写 `rewards.mode_ids` 与 `dialect.scene-seeds.pet_combat_mode_ids`。奖励叠加与取整由运营策略决定。抵消种子仅在已核实原生消费者的版本可用。上线前逐个客户端验证喂养、普通及高级成长、技能变化、重新登录恢复、房间/队伍/广场外观和对局效果。MySQL 增量迁移文件单独审核，并协调共享数据库的所有节点后再应用。

MySQL 迁移 `0042_inventory_slot_key_watermark.sql` 会在物品消耗后保留普通背包编号的最高水位。开启宠物里程奖励前，必须把共享数据库中所有发放物品的进程更新为使用该水位的购买分配器。旧版购买或礼物写入程序无法看到已消耗里程奖励的退役编号，可能再次分配它。读取购买历史只能保护新版写入程序兼容旧版购买，不能让旧版程序遵守新水位。所有发放程序更新前保持里程关闭；里程已经分配编号后，回滚程序也必须保留该分配行为。

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

1. 复制环境文件，设置非空数据库密码。Compose 文件要求 `BFF_MYSQL_PASSWORD`，缺少时拒绝启动；`BFF_SETUP_TOKEN` 为可选项，设置后（至少 32 个随机字符）远程首次安装会要求输入：

   ```sh
   cp .env.example .env
   ```

2. 在 Compose 文件查找的位置准备客户端数据：为将要选择的每个版本在仓库旁准备 `client-data/<build>/data` 与 `client-data/<build>/dataraw`，或用 `BFF_CLIENT_DATA_HOST` 指向其他位置。它会挂载到容器内的 `/app/client-data`。

3. 在仓库根目录构建并启动两个服务：

   ```sh
   docker compose -f deploy/docker/compose.yaml up --build
   ```

这会启动：

- `bff` 服务从 `deploy/docker/Dockerfile` 构建：基于 .NET SDK 与 ASP.NET 10 镜像的多阶段构建，以非特权用户 `bff`（uid 1001）运行。它不带命令运行宿主，即首次安装：打开 `http://<docker-host>:7300`，页面要求时输入 `BFF_SETUP_TOKEN`，创建管理员账号，选择 MySQL，主机填 `mysql`，数据库名、用户与密码取自 `.env`，并以数据目录 `/app/client-data` 选择客户端。之后的启动会运行已保存的安装，管理员使用该账号登录。它发布 7100（游戏）、7101（中继）与 7300（管理与 WebUI）；Compose 设置了 `BFF_ADMIN_HOST=0.0.0.0`，使发布的管理端口能到达容器。
- `mysql` 服务运行 `BFF_DB_IMAGE` 指定的镜像（从 `.env.example` 复制的 `.env` 为 MariaDB 11.4；没有这一行时为 MySQL 8.4，已有的 `mysql-data` 卷因此继续由创建它的数据库使用），根据 `BFF_MYSQL_DATABASE`、`BFF_MYSQL_USER` 与 `BFF_MYSQL_PASSWORD` 创建数据库和账号，在主机上发布 `${BFF_MYSQL_PORT:-3306}`，并把数据保存在 `mysql-data` 数据卷中。服务器会等待其健康检查通过后启动，并在容器网络内收到 `BFF_MYSQL_HOST=mysql`。
- 客户端数据以只读方式挂载到 `/app/client-data`；已保存的安装位于 `bff-state` 卷，数据库状态位于 `mysql-data` 卷。服务器自身的日志写在容器内，此 Compose 文件不会把它们保存到主机。

确认是否正常：在 Docker 主机上执行 `curl http://127.0.0.1:7300/health` 会返回带 `status` 字段的 JSON，控制台会显示安装或运行进度。可用以下命令跟踪控制台：

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

在主机上执行 `curl http://127.0.0.1:7300/health` 检查，并用下面的监听端口检查。用服务管理器的 SIGTERM 或前台的 `Ctrl+C` 停止。

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

用 `Invoke-RestMethod http://127.0.0.1:7300/health` 检查，它返回带 `status` 字段的 JSON。用 `Ctrl+C` 停止；在平台提供相应信号时，Host 也处理 SIGTERM 与 SIGINT。

## 持久数据与备份

| 数据 | 位置 | 升级前是否备份 |
| --- | --- | --- |
| 游戏与账号数据 | MySQL 数据库 `bf_game_db`，或一个 SQLite 存档文件。 | 是；每次升级都要。 |
| 客户端数据 | `client-data/<build>/` 下的只读文件。 | 一次即可，或更换客户端副本时。 |
| 配置与机密 | Compose 的 `.env` 或服务管理器的环境文件。 | 是；妥善保管。 |
| 编译后的客户端数据 | 工作目录下的 `state/compiled-data/<build>.bffdata`，或 `BFF_COMPILED_DATA`。 | 不需要；它是服务器会自行重建的缓存。 |
| 日志与封包日志 | 工作目录下的 `logs/<date>/`，或容器内路径。 | 可选；按策略轮转与保留。 |
| 旧发布版本 | 独立目录。 | 保留到回滚窗口关闭。 |

请把数据库、其备份以及 SQLite 存档文件放在发布目录之外，因为发布目录会被替换。MySQL 请在服务器停止或无写入时使用数据库自身的备份工具，例如逻辑转储（`mysqldump`）或一致性存储快照。SQLite 请先停止服务器，再复制单个存档文件。服务器正在写入时绝不复制运行中的 SQLite 文件。

在依赖备份前先在独立实例上测试恢复：恢复数据库或复制存档文件，用它启动发布版本，并确认 `/health`、插件行状态和客户端流程。从未恢复过的备份不算经过验证的备份。

客户端数据是输入而非状态：服务器只读取它，Docker 配置以只读方式挂载它，因此替换发布版本不会改变它。

## 内存

Host 的内存设置随 `BubbleFighter.Fantasy.Host.runtimeconfig.json` 一起发布：工作站回收器、并发回收，以及设为 9 的 `System.GC.ConserveMemory`（让回收器更积极地压缩）。所有行都激活后，Host 会压缩一次堆并记录 `Startup allocated N MB in C collection(s); the managed heap was compacted from A MB to B MB committed`。B 接近已加载配置档实际保留的内存；进程本身还要加上运行时自身的代码和映射的程序文件，因此比 B 大。

启动所需的内存远多于运行时。在我们的测量中，单个客户端版本在解析客户端数据时峰值接近 140 MB，稳定后私有内存约 40 MB；全部版本一起启动时峰值接近 300 MB（见[小型服务器](#小型服务器)）。小内存机器应为启动阶段提供交换空间，而不是设置低于峰值的限制。

环境变量会覆盖该文件。有两点需要注意：

- 运行时把数值型的 `DOTNET_GC*` 变量按**十六进制**读取。`DOTNET_GCHeapHardLimit=20000000` 是 512 MB，而不是 20 MB。请写成 `0x20000000`，让进制一目了然。
- 启动过程一旦触及堆上限，服务器会因 `OutOfMemoryException` 停止。启动峰值包含解析每个版本客户端数据的开销，因此一个容得下空闲服务器的上限仍可能让启动失败。在设置了内存限制的容器中，运行时会自动把堆上限设为该限制的 75%。除非已实测你的配置档能在某个上限内启动，否则不要设置堆上限。

`python tools/perf/measure.py --profile <id>` 可在你自己的机器上测量一次启动；各项数值的含义见 `tools/perf/README.md`。

连接从被接受起就占用一个套接字、一个会话和一个读取缓冲。每个受支持的客户端在收到问候后都会立即发送第一帧，因此在 `first_frame_seconds`（每个 `protocol` 行的配置项，默认 30）内没有发出完整帧的连接会被关闭。只有在实测某个客户端需要更长时间时才应调高。

停止读取的客户端会让服务器为它排队待发送的数据。每个会话在达到 `send_queue_bytes`（4 MB）时被关闭；`total_send_queue_bytes`（每个 `protocol` 行的配置项，默认 64 MB）限制该行所有会话合计排队的字节数，因此多个客户端同时停止读取也不会累积起来。某一帧将越过该上限时，排队字节最多的会话会被关闭，该帧照常发送；任何发送方都不会等待。小内存机器上可将其调低到机器能够承受的数值；会话结束时会在 Debug 级别记录其排队字节数的峰值。

### 小型服务器

服务器需要多少资源，取决于它服务的客户端版本、使用的数据库以及同时启动的内容。下表数值由 `tools/perf/measure.py` 在使用内存持久化的发布构建上测得；可据此选择配置，再用同样的方法测量你自己的配置档。

| | 单个版本（`kr-812`） | 全部版本（`all`） |
| --- | --- | --- |
| 启动时峰值 | 约 140 MB | 约 300 MB（首次启动最高约 370 MB） |
| 空闲时私有内存 | 约 40 MB | 约 95 MB |
| 首次启动所需 CPU（单个快速核心） | 约 5 秒 | 约 27 秒 |
| 之后每次启动所需 CPU | 约 2 秒 | 约 16 秒 |

较慢的 VPS 核心需要两到四倍的启动 CPU 时间。峰值只出现在解析客户端数据期间，之后服务器会归还这部分内存。

**编译后的客户端数据。** 一个版本第一次启动时会解析客户端工作簿，并把结果保存到工作目录下的 `state/compiled-data/<build>.bffdata`（设置 `BFF_COMPILED_DATA` 可更换目录）。之后的启动直接读取该文件，因此所需 CPU 少得多。该文件只是缓存：只有客户端数据和服务器版本都与写入时一致时才会使用，否则重新生成；没有它服务器的行为完全相同，所以可以随时删除，也不需要备份。升级之后的第一次启动又是一次首次启动。`bff data compile <profile>` 执行这次首次启动后即停止，部署时可以在放玩家进入之前先完成它。在容器中请把该目录挂载为卷，否则每个新容器都要从头开始。

**512 MB。** 服务一到两个版本，使用 SQLite（通过补丁启用 `persistence.sqlite` 行，见 [SQLite](configuration.md#sqlite)），不要使用 MySQL：单是一个数据库服务器就会占用这类机器的大部分内存。添加 1 GB 交换空间或 zram，避免启动峰值触发 OOM。保持 `BFF_PACKET_LOG` 关闭。

**1 GB。** 使用 SQLite 服务数个版本，或按下文配置 MariaDB 服务一到两个版本。保留 512 MB 交换空间。

**2 GB 及以上。** 使用 MariaDB 或 MySQL 服务全部版本（`profiles/all`）。

**小型服务器补丁。** `profiles/bundles/small-vps.patch.yaml` 关闭管理网页以及客户端与插件分发相关的行，只为玩家提供服务的服务器不需要它们。管理 API 保持开启。在配置档自身的补丁之后，为 `run` 或 `start` 加上 `--patch profiles/bundles/small-vps.patch.yaml`。每个版本的监听器默认接受 512 个连接（监听器行的 `max_connections`）；机器容纳不了这么多玩家时，在你自己的补丁中调低。

**休眠的版本。** 在一个进程里服务多个版本的服务器（`profiles/all`，或你自己的、列出了 `builds` 的服务器配置档）可以让某个版本保持未加载，直到有玩家需要它。设置 `BFF_BUILD_ACTIVATION=on_demand`；在你自己的服务器配置档中写 `build_activation: on_demand`。每个版本的监听器和线路方言随服务器一起启动，所以各端口从第一秒起就接受连接。版本的其余部分在第一个客户端连上它时加载，并在连续 `BFF_BUILD_IDLE_MINUTES`（默认 30）分钟没有连接也没有房间后卸载。在我们的测量中，`all` 约 2 秒就绪、私有内存 30 MB，而不是约 16 秒 CPU 和 95 MB；加载两个版本时峰值约 155 MB，而不是 300 MB，两个版本再次休眠后回到 32 MB。代价由休眠版本的第一位玩家承担：他的连接要等该版本加载完成，在测试机上，服务器启动后的第一个版本约 11 秒，之后的版本约 2 秒，慢的核心上更久。等待会超过 `wake_timeout_seconds`（30，位于 `core.activation-groups` 行）的连接会被关闭，版本就绪后客户端可以重新连接。一个版本的加载或卸载不会打扰其他版本的玩家、管理 API 或启动器通道。必须一直运行的行可以在补丁中写 `activation: always`。以上是在单节点上测得的；挂载了集群行的配置档尚未在版本休眠的情况下检查过，那里请保持 `always`。

在小内存机器上，MariaDB 的 `[mysqld]` 配置：

```ini
innodb_buffer_pool_size = 64M    # 游戏数据表很小；数据库增长超过此值时再调高
performance_schema      = OFF
max_connections         = 50
innodb_log_buffer_size  = 8M
key_buffer_size         = 8M
aria_pagecache_buffer_size = 16M
tmp_table_size          = 16M
max_heap_table_size     = 16M
```

游戏服务器与数据库在同一台机器上时，将其绑定到 `127.0.0.1`。在我们的测量中，MariaDB 与运行中的服务器并存时常驻内存约 32 MB；MySQL 8.4 按默认配置会预留数百 MB，因此新的 Docker 安装改用 MariaDB。

**Docker。** 新安装时，`deploy/docker/compose.yaml` 与 `install.sh --docker --with-mysql` 启动 MariaDB 11.4（`BFF_DB_IMAGE=mariadb:11.4`）。数据库由 MySQL 8.4 创建的安装没有 `BFF_DB_IMAGE` 这一行，会继续使用 MySQL，因为 MariaDB 无法打开该数据卷；如需迁移，请导出数据库再导入新数据库。如需限制容器内存，可添加 Compose 覆盖文件，例如 1 GB 机器可使用：

```yaml
services:
  bff:
    mem_limit: 512m
    memswap_limit: 1g
  mysql:
    mem_limit: 192m
    memswap_limit: 384m
    command: ["--character-set-server=utf8mb4", "--collation-server=utf8mb4_unicode_ci",
              "--innodb-buffer-pool-size=64M", "--performance-schema=OFF", "--max-connections=50"]
```

运行时会把堆上限设为容器内存上限的 75%，因此服务器容器的上限应明显高于上表中的启动峰值。

不要把堆上限设得低于启动峰值（见上文关于十六进制的说明），否则服务器会在启动过程中停止。

## 日志

设置 `BFF_LOG_FILE` 或 `--log-file` 可在控制台输出之外保留 Host `ILogger` 记录。未设置路径时关闭文件日志。文件阈值默认 `Warning`，由 `BFF_LOG_FILE_LEVEL` 或 `--log-file-level` 指定，与 `BFF_LOG_LEVEL` 独立。日志工厂的类别与提供程序筛选规则仍然适用。每条接纳的记录包含 UTC 时间戳、级别、类别、事件编号和完整异常文本，包括内部异常与堆栈。

请选择现有持久卷内的可写路径。对于 `deploy/docker/compose.yaml`，`/app/state/logs/host.log` 位于 `bff-state:/app/state` 挂载内；还需把变量加入容器环境。原生服务应使用自己的持久数据目录。写入器会创建父目录，并在运行期间独占 `host.log.lock`。其他进程不能同时写入同一日志。无效路径、权限问题或目标被锁定都会导致启动失败。

文件按固定槽位轮转：`host.log`、`host.1.log` 等。默认保留五个文件，包含当前文件，新写入受每个文件 16 MiB 的上限限制。重启或调低数量后仍执行数量限制。启动时已有的超限当前文件会轮转到第一个备份；历史备份可能超过新调低的大小上限，直到正常保留轮转替换它们。只保留一个槽位时，轮转会替换当前文件。单个写入任务同时受记录槽数与 UTF-8 字节预算限制。调用方不等待磁盘：队列满、字节预算用尽、记录过大或格式化失败时，丢弃最新的整条记录并写入带计数的 `dropped_entries=N` 行。异常不会被截短来适应上限。写入、刷新或轮转发生 I/O 错误时，写入器会向 stderr 输出一条包含异常类型的有界短行，关闭文件流，并将不可用期间的记录计为丢弃。它按指数退避重试打开文件，默认从 1 秒增加到最多 60 秒。恢复后先写入待报告的丢弃计数，再写新记录。轮转失败会从上次完成的文件移动位置继续。Warning 及更高级别的记录和丢弃报告会在下一次写入前刷新至操作系统；关闭时只在限定时间内排空，无法完成时报告失败。I/O 错误可能留下部分记录，这也不保证断电后恢复。所有上限见[配置](configuration.zh.md)。完整异常可能包含账号或连接数据；请限制访问并执行正常的保留策略。

服务器向控制台输出日志。每行是单行并带 UTC 时间戳；级别来自 `BFF_LOG_LEVEL` 或 `--log-level`，默认 `Information`。正常启动会报告每个已激活的行、每个监听器的监听地址，就绪后输出 `Server started with <n> active row(s).`。正常停止以 `Shutdown complete.` 结束。

诊断行还可以写封包日志文件。它们默认关闭，因为开启后每个封包都要格式化一行并写入文件；调试时设置 `BFF_PACKET_LOG=true` 即可开启。连接事件无论如何都会输出到控制台。这些文件与控制台分开，每个封包和连接事件一行：

- 默认路径是相对于工作目录的 `logs/{date}/packets_{timestamp}.log`。profile 会覆盖文件名：`kr-812` 写 `packets-kr-812_{timestamp}.log`，`all` profile 写 `packets-all_{timestamp}.log`。
- 每个文件达到 10 MiB 时轮转为 `.1`；`{date}` 变化时开始新文件。默认按配置的文件名模式保留最多 100 个文件，写入器启动时会删除更旧的文件。文件名时区默认为 China Standard Time。
- 写入经过有界队列（默认 32768 条），关闭时最多等待 10 秒刷新。溢出会以 `packet-log-overflow` 报告，绝不丢弃游戏封包。
- 完整数据包记录默认关闭。设置 `BFF_PACKET_CAPTURE=true` 可启用，并在同一 `logs/` 目录树下写出独立的 JSONL 数据包文件。

封包日志行和捕获可能包含玩家与连接数据；请把 `logs` 目录视为敏感数据并套用你的保留策略。在 Docker 配置中，发布根目录模板挂载 `./logs`，使封包文件保存在主机上，而控制台输出进入容器的 json-file 日志，限制为 5 个 10 MB 文件。

## 管理与网络安全

- 以 `start` 启动的服务器（可执行文件不带参数时即如此）默认将管理监听绑定到 `0.0.0.0:7300`，管理员使用账号和密码登录；`/health` 和 WebUI 文件保持开放，以便浏览器加载登录页。在第一个管理员创建之前，任何能连到该端口的人都可以完成安装，因此请在首次启动后立即完成，或设置 `BFF_SETUP_TOKEN`。绑定到可访问地址时接受任何主机名，除非 `BFF_ADMIN_ALLOWED_HOSTS` 列出了允许的名称。以 `run --profile` 启动的服务器没有账号，无需登录，并且只在回环地址上监听。
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
