---
title: "服务器配置"
description: "组合插件 profile、设置环境变量、端口与持久化。"
updated: 2026-10-18
---

Bubble Fighter Fantasy 没有固定的一组服务器组件。`profiles/` 下的一个 **profile** 决定运行哪些插件行、服务哪个客户端构建、打开哪些监听端口以及使用哪种存储。本页说明 profile 如何变成运行中的服务器、配置文件的位置、环境变量与端口的工作方式，以及如何切换持久化与客户端数据。

**本页面向：** 需要改变服务器运行内容的运维人员，以及在启动前需要检查实际配置的开发者。

**开始之前：**

- 先构建一次服务器：`dotnet build BubbleFighter.Fantasy.sln -c Release` 必须以零警告完成。参见[安装](installation.md)。
- 在仓库根目录打开终端。本页使用 `dotnet run` 的命令都假定当前目录是仓库根目录，因为 profile、packets 和客户端数据都相对于工作目录解析。
- 将 `.env.example` 复制为 `.env`，并设置非空的 `BFF_MYSQL_PASSWORD`。即使某次检查会禁用 MySQL，profile 插值也要求该变量有值。不要把真实凭证写入准备提交的文件。
- 如果打算启动游戏构建，请准备好客户端数据。仓库不包含客户端数据；客户端数据见本页“客户端数据”一节。

## 配置的工作方式

启动服务器有两种方式：

- `bff run --profile <id>` 从 YAML 文件组合 profile 并启动其插件行。以下各节都描述这条路径。
- `bff start`（可执行文件不带参数启动时也会使用）运行打包的首次安装 profile，包含安装助手和管理行。它有意不读取仓库 `.env`；环境变量见本页“环境变量”一节。

### 组合顺序

profile 按层组合，后一层修改前一层的组合结果：

1. `profiles/<id>/profile.yaml` 声明 profile 身份、服务的客户端构建、使用的 bundle、自己的 `vars` 以及要应用的补丁文件。
2. `profiles/bundles/<name>.yaml` 中每个 bundle 按文件顺序提供插件行。
3. 按 `patches:` 列出的顺序应用 profile 的补丁文件。构建 profile 通常先应用自己的 `patch.yaml`，再应用 `profiles/bundles/shop-supplement-lucci.yaml` 之类的共享补充补丁。
4. 当 profile 列出 `profiles/<id>/local.patch.yaml` 时，随后应用该文件。它被 Git 忽略，缺失时静默跳过，是放置开发者覆盖配置的位置。
5. 命令行上的每个 `--patch <file>` 在所有 profile 补丁之后按给定顺序应用。
6. 字符串值按 profile 的 `vars` 与进程环境进行插值。`--dump-config` 显示最终结果。

由于命令行补丁最后应用，本地覆盖文件可以把 `persistence.mysql` 指向某个数据库，而命令行补丁仍能在某次运行中关掉它。

### 插件行

实际运行的一切都是**插件行**。一行指定插件实现并向它传入配置：

```yaml
- id: admin.http
  plugin: admin.http
  config:
    listen: "${BFF_ADMIN_HOST:-127.0.0.1}:${BFF_ADMIN_PORT:-8080}"
```

| 键 | 必需 | 含义 |
| --- | --- | --- |
| `id` | 是 | 稳定的行标识，供补丁和管理 API 使用。在同一 profile 内唯一。 |
| `plugin` | 是 | 从插件目录发现的插件实现 id。 |
| `config` | 否 | 绑定到插件选项类型的映射。未知键会绑定失败。 |
| `disabled` | 否 | `true` 保留该行的组合结果但阻止激活；默认 `false`。 |

高级行还可以声明 `parent_id`、`isolated_services`、`version` 和 `lifecycle`。这些键控制嵌套、按构建隔离的服务以及资源预算；仓库提供的 profile 已在需要处设置它们。应把它们视为 profile 定义的一部分，而不是部署配置。

服务器按“所需服务何时可用”激活行，而不是按列表顺序。两个激活的行不能发布同一个服务接口：注册表会拒绝第二次注册。这正是同一时间只能启用一个持久化提供程序、切换存储属于组合变更而非运行时开关的原因。

### 补丁操作

补丁文件是单键操作组成的 YAML 列表：

| 操作 | 形式 | 作用 |
| --- | --- | --- |
| `replace` | `{id, plugin?, disabled?, config}` | 替换指定行。给出的 `config` 会替换整行配置。 |
| `set` | `{id, config: {...}}` | 只修改指定的配置键并保留其余键；`null` 值删除该键。 |
| `insert` | 行列表 | 追加新行。 |
| `remove` | 行 id | 删除该行。 |

行不存在时 `replace` 与 `set` 会失败，未知操作会使组合失败。本地检查使用的内存隔离补丁就是一个很小的 `replace` 补丁：

```yaml
- replace:
    id: persistence.mysql
    disabled: true
- replace:
    id: persistence.memory
    disabled: false
```

### 插值与校验

`${VAR}` 插入变量值，`${VAR:-default}` 提供默认值。如果 profile 的 `vars` 与进程环境都没有定义该变量且没有默认值，组合会失败并指出文件、行和变量。profile 自己的 `vars` 优先于同名环境变量，禁用行不参与插值。

配置绑定将 `snake_case` 键映射到插件的类型化选项。未知键、错误类型、越界数字和选项校验失败会合并报告为一条 `Invalid configuration for row '<id>': ...` 错误。

## 文件位置

| 路径 | 内容 |
| --- | --- |
| `profiles/<id>/profile.yaml` | profile 身份、客户端构建、`parent`、bundle 列表、`vars`、补丁列表。 |
| `profiles/<id>/patch.yaml` | profile 自己的补丁，在 `profile.yaml` 中列出。 |
| `profiles/<id>/opcodes.yaml` | 该客户端构建按名称解析的 opcode 表。 |
| `profiles/<id>/local.patch.yaml` | 可选的、被 Git 忽略的开发者覆盖配置，存在时自动应用。 |
| `profiles/bundles/*.yaml` | profile 引用的共享行集合。 |
| `packets/*.yaml` | 封包布局与 `builds.yaml` 的构建继承关系；启动时读取，不编译进程序。 |
| `.env` | 部署变量；被 Git 忽略。 |

Host 项目会把 `bootstrap/`、`profiles/`（不含 `local.patch.yaml`）、`packets/` 以及两个 `data/` 文件（`data/level-experience.csv` 与 `data/locales/*.json`）复制到构建和发布输出中。插件程序集发布在 `plugins/<ProjectName>/` 下，这也是 Host 旁默认的插件根目录。因此除运行时、客户端数据以及启用 MySQL 时所需的数据库外，发布目录是自包含的。

行内的路径相对于进程工作目录解析。使用 `dotnet run` 时从仓库根目录运行；使用已发布的 Host 时从发布目录运行。若从其他位置启动，请传入 `--profiles-dir`，并注意 `packets` 与 `client-data` 路径仍按工作目录解析。

## 运行前检查

列出当前检出提供的 profile 和发现的插件实现：

```sh
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- profiles list
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- plugins list
```

`profiles list` 每行输出 profile id、制表符和描述；本次发布共有 19 个 profile（各客户端构建 profile，以及 `all`、`management`、`setup`）。`plugins list` 输出插件 id、版本、支持的客户端构建和来源；使用 `--plugin-root <dir>`（可重复）可把发现指向其他目录。

不启动服务器而打印实际配置。务必附加内存隔离补丁，使指向真实数据库的开发者 `.env` 或 `local.patch.yaml` 不会被连接：

```sh
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- run --profile kr-812 --patch tools/local-checks/isolated-memory.patch.yaml --dump-config
```

转储会列出每一行的 `id`、`plugin`、提供该行的 `source` 文件、设置时的 `disabled`、生命周期预算以及插值后的 `config`。在修改端口或存储前阅读它：这就是服务器实际会运行的组合。组合失败时，Host 会在 stderr 打印 `error: <message>` 并以退出码 1 结束；消息会指出失败的文件、行和键。

## 环境变量

### 加载规则

- 除 `start` 外，所有命令都会先从工作目录、再从 Host 目录向上查找包含 `BubbleFighter.Fantasy.sln` 或 `.git` 条目的仓库根目录。若该目录存在 `.env`，其中的赋值会应用到尚未设置的变量。
- 已存在的进程环境变量优先。`.env` 只补齐空缺，从不覆盖你的 shell 或服务管理器。
- `start` 从不读取仓库 `.env`。首次安装 profile 从进程环境和 `--state-dir` 获取自己的变量。
- 文件按 UTF-8 读取，大小上限为 64 KiB。每行是 `KEY=VALUE`，也接受 `export KEY=VALUE`；值可以加引号；空白后的未加引号 `#` 开始注释；格式错误的行会失败并报告文件路径与行号。变量值从不写入日志。加载文件时控制台会打印 `Loaded unset variables from <path>.`。
- 发布目录没有解决方案文件或 `.git` 条目，因此复制到已发布 Host 旁的 `.env` **不会**被读取。部署时应通过进程环境、服务管理器或容器环境设置变量。

### 常用变量

| 变量 | 用途 | 默认值 |
| --- | --- | --- |
| `BFF_BIND_HOST` | 游戏与中继监听器绑定的地址。 | `0.0.0.0` |
| `BFF_PUBLIC_HOST` | 告知客户端用于连接和中继的地址。 | `127.0.0.1` |
| `BFF_ADMIN_HOST` / `BFF_ADMIN_PORT` | 管理 HTTP 绑定地址。 | `127.0.0.1` / `8080` |
| `BFF_LOG_LEVEL` | 控制台日志级别：Trace、Debug、Information、Warning、Error、Critical、None。 | `Information` |
| `BFF_CLIENT_DATA` | 各构建客户端数据的根目录。 | `client-data` |
| `BFF_PACKET_CAPTURE` | 启用可选的完整数据包记录器。 | `false` |
| `BFF_STATE_DIR` | 首次安装流程（`start`）的状态目录。 | 可执行文件旁的 `state` |
| `BFF_SQLITE_DATABASE_PATH` | 补丁启用 SQLite 提供程序时使用的绝对存档路径。 | 无 |
| `BFF_LEVEL_EXP_TABLE` | 回合奖励使用的累计经验表。 | `data/level-experience.csv` |

默认组合还要求以下 MySQL 变量：

| 变量 | 用途 | 默认值 |
| --- | --- | --- |
| `BFF_MYSQL_HOST` | 数据库主机。Docker Compose 会把它改为其 `mysql` 服务。 | `127.0.0.1` |
| `BFF_MYSQL_PORT` | 数据库端口。 | `3306` |
| `BFF_MYSQL_USER` | 数据库账号。 | `bff` |
| `BFF_MYSQL_PASSWORD` | 密码。插值发生在禁用补丁之前，因此必须非空。 | 空，必须设置 |
| `BFF_MYSQL_DATABASE` | 数据库名。可以使用任何已有的数据库。 | `bf_game_db` |

端口和游戏数值在下面的章节说明。`.env.example` 是带注释的完整列表；请复制它，不要自行发明变量名。

## 端口与地址

`BFF_BIND_HOST` 是服务器监听的接口，`BFF_PUBLIC_HOST` 是服务器告知客户端使用的地址。位于 NAT 之后时两者可以不同：绑定到能到达客户端的接口，并公布客户端实际可访问的公共地址。保持 `BFF_PUBLIC_HOST` 为回环默认值只在客户端与本机同机时有效。

监听端口来自 profile，通常经由按构建的变量和 `vars` 设置。本次发布的两个基线使用：

| 端点 | kr-1256（现代） | kr-812（旧版） |
| --- | --- | --- |
| 游戏 TCP | `7100`（`KR_1256_GAME_PORT`） | `7110`（`KR_812_GAME_PORT`） |
| 中继 TCP | `7101`（`KR_1256_RELAY_PORT`） | 旧版 bundle 不打开 |
| 管理 HTTP | `8080`（`BFF_ADMIN_PORT`） | `8081`，由 profile 补丁移动 |
| 可选原生选择器 | 不使用 | 挂载 `center-selector.yaml` 时为 `7112` |

`all` profile 把已接入的客户端构建组合进一个进程，每个构建使用自己的游戏监听器。其默认游戏端口从 `7100` 开始以十递增（`kr-1256` 7100、`kr-812` 7110、`id-400` 7120，直到 `cn-408` 7250）。开放防火墙规则前，请用 `--dump-config` 读取所选 profile 的实际 `listen` 字符串。

管理端口应保持在回环或受控网络。只开放所选 profile 提供服务的游戏与中继端口，绝不要把 MySQL 暴露在公共接口上。

## 持久化

核心 bundle 提供三个持久化行：

| 行 | 持久性 | 适用场景 |
| --- | --- | --- |
| `persistence.memory` | 仅进程生命周期；重启即重置。 | 一次性本地检查与测试。 |
| `persistence.sqlite` | 一个本地存档文件。 | 需要持久数据但不想运行数据库服务器的单机部署。 |
| `persistence.mysql` | 外部 MySQL 数据库。 | 真实部署，尤其是在多进程或多主机时。 |

同一时间只能有一个提供程序生效。注册表会拒绝同一仓库接口的第二次注册，因此必须在同一个补丁中只启用一个并禁用另外两个。核心 bundle 中 MySQL 行已启用、另外两个已禁用；补丁负责切换。

### 用补丁切换提供程序

从默认 MySQL 配置切换到 SQLite：

```yaml
- replace:
    id: persistence.memory
    disabled: true
- replace:
    id: persistence.sqlite
    disabled: false
    config:
      database_path: "${BFF_SQLITE_DATABASE_PATH}"
```

要回到 MySQL，把 `persistence.mysql` 替换为 `disabled: false`，并禁用已启用的提供程序。

### MySQL

默认连接字符串会插入 `BFF_MYSQL_*` 变量并固定 `SslMode=none`。需要 TLS 的部署应在补丁中覆盖整个 `connection_string`，而不是修改 bundle。

该行激活时会打开连接并执行 `SELECT 1`。密码缺失、连接字符串格式错误或服务器不可达都会使该行失败，启动会报告失败而不是在没有存储的情况下继续运行。`auto_migrate` 默认为 `true`，此时插件会应用内嵌迁移：

- 迁移脚本是名为 `NNNN_description.sql` 的内嵌资源，按文件名顺序应用。本次发布最高版本为 `0032`。
- 先创建 `schema_migrations`（若不存在），随后每个待应用版本在自己的事务中执行并记录。控制台会打印 `Applied migration <version> (<name>).`。
- 命名错误、版本重复或内容为空的脚本会使本次运行失败，而不是被跳过。
- 迁移**仅向前**。没有降级路径，因此较旧的服务器程序可能无法在较新版本迁移过的数据库上运行。备份与回滚流程见[部署](deployment.md)。

### SQLite

SQLite 行要求绝对 `database_path`，该选项拒绝相对路径。请把文件放在发布目录和日志目录之外，以免替换发布版本时丢失；也不要让两个运行中的服务器共用同一个存档文件。该行还有玩家数、账号数、数据库大小和锁等待的上限，可在同一配置映射中设置。

内存提供程序是基于进程内字典的有界存储，提供相同的仓库接口。状态在关闭时消失，其行有意不发布数据库工厂。内存与 MySQL 互斥。

### 本地隔离检查

`tools/local-checks/isolated-memory.patch.yaml` 禁用 `persistence.mysql` 并启用 `persistence.memory`。由于命令行补丁最后应用，它会覆盖指向真实数据库的开发者 `.env` 或 `local.patch.yaml`。每次本地 `run`（包括 `--dump-config` 检查）都要附加它；下文“本地隔离检查”配方给出了完整命令。进程停止后内存数据即丢失，该补丁绝不能替代正式环境的持久化配置。

## 客户端数据

游戏 profile 读取所选客户端构建自己的数据；仓库不包含游戏客户端及其文件。数据根目录默认为 `client-data`，可用 `BFF_CLIENT_DATA` 移动。其布局为：

```text
client-data/
  <build>/
    data/      保留客户端原始路径的客户端封包，例如 Packs/ 和 Config/
    dataraw/   同一客户端路径下的解包散文件，优先读取
```

`<build>` 是 profile id，例如 `kr-812` 或 `kr-1256`。旧版 profile 读取 `${BFF_CLIENT_DATA:-client-data}/${BFF_PROFILE_ID}/data` 和 `.../dataraw`；现代 `kr-1256` profile 对 `kr-1256` 使用相同结构。

若两个目录都不存在，`clientdata` 行会在激活时失败，错误消息会指出它查找的两个路径，服务器无法完成启动。启动游戏 profile 前请准备好数据。这些文件是只读输入：服务器不会写入 `client-data`，因此 Docker 配置以只读方式挂载它。

`management` 与 `setup` 不挂载客户端数据行。首次安装流程可以把准备好的数据导入其状态目录；参见[安装](installation.md)。

## 常用配置

第一个配方是隔离检查。其余是明确指向持久存储的配置：它们指向你自己拥有的存储，且**不**包含隔离补丁，因为那会禁用正在配置的提供程序。不要把持久化配方指向你无意修改的数据库。

### 本地隔离检查

```sh
cp .env.example .env
# 编辑 .env，将 BFF_MYSQL_PASSWORD 设为一次性的非空值。
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- run --profile management --patch tools/local-checks/isolated-memory.patch.yaml
```

控制台会报告已激活的行，Host 就绪后输出 `Server started with <n> active row(s).`。访问 `http://127.0.0.1:8080/` 的 `GET /health`：它返回含 `status` 字段的 JSON。用 `Ctrl+C` 停止。

### SQLite 服务器

在仓库根目录创建 `sqlite.patch.yaml`：

```yaml
- replace:
    id: persistence.mysql
    disabled: true
- replace:
    id: persistence.memory
    disabled: true
- replace:
    id: persistence.sqlite
    disabled: false
    config:
      database_path: "${BFF_SQLITE_DATABASE_PATH}"
```

在 `.env` 中把 `BFF_SQLITE_DATABASE_PATH` 设为绝对路径，然后启动目标 profile（游戏构建需要其客户端数据）：

```sh
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- run --profile kr-1256 --patch tools/local-checks/isolated-memory.patch.yaml --patch sqlite.patch.yaml
```

在控制台中查找 SQLite 激活行，并确认存档文件已出现在配置的路径。用 `Ctrl+C` 停止；存档文件会保留。

### MySQL 服务器

在 `.env` 中设置真实连接值，并保持默认 MySQL 行启用：

```sh
BFF_MYSQL_HOST=127.0.0.1
BFF_MYSQL_PORT=3306
BFF_MYSQL_USER=bff
BFF_MYSQL_PASSWORD=<your-database-password>
BFF_MYSQL_DATABASE=bf_game_db
```

本地核查配置时，使用隔离的内存存储检查 profile，不连接此数据库：

```sh
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- run --profile kr-1256 --patch tools/local-checks/isolated-memory.patch.yaml --dump-config
```

此转储明确禁用 MySQL，也不启动监听器；它不验证数据库连接或执行迁移。持久化运行请按[部署](deployment.md)操作，并明确核查数据库设置。实际激活 MySQL 时会记录 `MySQL persistence connected to <host>:<port>/<database> as <user>.` 并执行待处理的迁移。

### NAT：绑定地址与公布地址

在 `.env` 中设置两个地址，使监听器绑定所有接口，同时向客户端公布公共地址：

```sh
BFF_BIND_HOST=0.0.0.0
BFF_PUBLIC_HOST=<public-address-clients-use>
```

端口变量也用同样方式修改，例如 `KR_1256_GAME_PORT` 和 `KR_1256_RELAY_PORT`。只开放这些端口的防火墙规则，并在启动前用 `--dump-config` 核对地址。若某个监听器的地址必须偏离 profile 默认值，也可以对该监听器行使用 `set` 补丁：

```yaml
- set:
    id: game-listener
    config:
      listen: "0.0.0.0:7100"
```

## 多客户端 profile

不同 profile 服务不同的历史客户端。构建 ID 是 `<region>-<wire version>`，其中 wire version 是客户端的 `ClientVersion_Major`，不是文件版本：`kr-812`、`cn-324`、`id-400`、`tw-454`、`kr-1256`。profile 目录名就是封包表中使用的构建 ID。

`kr-812`（韩服 2015）是**旧版基线**：`profiles/bundles/legacy.yaml` 是它的行集合，旧版构建只在各自的 `vars` 和 `patch.yaml` 中声明差异（端口、加密表、座位数、座位相关设置、封包开关）。`kr-1256`（韩服最终构建）是**现代基线**：它有自己的 `kr-1256`、`ingame` 和 `shop` bundle，其封包布局是 `cn-774` 与 `tw-454` 的参照。

| Profile | 客户端构建 | 角色 |
| --- | --- | --- |
| `kr-812` | `kr:812:458` | 旧版家族的基线。 |
| `kr-1256` | `kr:1256:1304` | 现代基线；Dockerfile 默认使用的 profile。 |
| `all` | 16 个构建 | 所有已接入构建在同一进程内运行，各自使用独立监听端口。 |
| `management` | 无 | 仅管理 HTTP 与存储；没有游戏或中继端口。 |
| `setup` | 无 | 与数据库无关的首次安装与管理。 |

其余客户端构建 profile 各自服务一个构建。它们的封包布局通过 `packets/builds.yaml` 和 profile 自己的 `parent:` 继承：`cn-279` 基于 `cn-324`，后者基于 `kr-812`；`cn-738` 与 `cn-742` 沿用 `cn-735` 的布局；`tw-454` 基于 `cn-774`，后者基于 `kr-1256`。差异较小的构建只需标注差异，不必重写布局。

构建之间还存在能力（capability）差异，按构建记录在 `packets/builds.yaml` 中并沿基线继承。例如“丢弃过期物品时排除类别 6”的限制在 `kr-812` 上开启、在 `cn-324` 上关闭；现代 `kr-1256` 关闭了许多旧版能力，直到在该构建中审计完对应消费者；旧版房间在基线中为十二个座位，在 `cn-324` 中为八个。能力开关决定构建的协议层发送和启用什么；它们是数据，不是猜测。

按你准备连接的客户端构建选择 profile：读取客户端版本，找到匹配的 profile id，使用该 profile 的端口并提供该构建的客户端数据。`all` 用于同时运行多个构建；它会启动每个构建的监听器，并需要所有所选构建的数据。

已列出的 profile 意味着：仓库能组合这些行，并带有该构建的封包数据和能力记录。它不意味着：所有游戏功能都已实现，或真实客户端已验证。自动化检查通过、能力启用或 profile 出现在列表中，都不构成真实客户端兼容性证明；能力记录描述的是已审计的原生支持，原生验收仍需单独进行。

## 可选功能 bundle

少数 bundle 需要显式启用，且只被客户端审计支持的 profile 列出。

**`player-settings`** 挂载 `dialect.customized-keys` 与 `player.settings`，让玩家保存自定义按键。它被 `kr-812`、`kr-1256`、`cn-774` 和 `tw-454` 列出；其他 profile 需先审计其原生控制名称和封包路径。每个 `player.settings` 行都需要 `protocol`，并使用按 profile 的 `scope`，避免混淆不同构建的名称。持久化提供程序按账号保存映射；MySQL 通过迁移 `0032_customized_keys.sql` 存储。每个提供程序用 `customized_keys` 配置每账号预算（`max_scopes` 1–64、`max_mappings` 1–512、`max_name_characters` 1–1024，三者组合必须能放入封包体）。超出预算会直接失败，而不会驱逐其他已保存映射。

**`inventory-actions`** 挂载 `dialect.inventory-actions` 与 `inventory.throw`，让玩家丢弃过期物品。它由已审计操作消费者并具备存储装备服务的 profile 列出（`kr-812`、`cn-279`、`cn-324`、`cn-406`、`cn-404`、`cn-408`、`cn-706`、`cn-717`、`id-400`、`id-1809`、`kr-1256`）。行为被有意限制得很窄：只移除一个已认证玩家两个背包中的该过期物品；若有匹配权益仍有效，整个移除会被拒绝；构建的类别 6 排除规则会被遵守。比赛中的请求会被拒绝。真实客户端验收仍待进行；仅凭封包表覆盖不足以在另一个构建上挂载该 bundle。

**`launcher.http`** 是可选的公共启动器 API。它默认禁用，与管理工作分离。它接受 `minimum_launcher_version`（留空则关闭检查）与 `plugin_recommendations`，后者包含商店标识和可选的精确 `builds`，绝不包含可执行文件路径或下载地址。配置最低版本后，较旧的启动器会收到 HTTP 426。每个大厅行的 `authentication` 选项（`BFF_LOGIN_AUTHENTICATION`，默认 `ticket`）只接受能兑换启动器票据的登录，此时需要本行处于激活状态；`open` 接受任何账号名，只适用于可信的局域网。[启动器](launcher.md)页面介绍该 API、客户端预设、账号与票据流程以及面向运营者的配置。

启动器请求与登录会话限制在 `launcher.http` 行配置：

| 配置项 | 默认值 | 行为 |
|---|---|---|
| `requests_per_minute` | `120` | 每个连接来源 IP 每分钟可接收的请求数；转发头不会改变来源 IP。 |
| `max_rate_limit_addresses` | `4096` | 最多保留的 IP 窗口数（1–100000）；满时淘汰最早的窗口。 |
| `max_sessions_per_account` | `8` | 每账号的有效持久登录会话数（1–1024）；新登录会撤销创建时间最早的会话。 |
| `max_sessions` | `1024` | 已有的进程内访问凭据及待用游戏票据容量上限。 |

撤销旧会话时也会清除它在本进程中的访问凭据和待用游戏票据。监听器启动前删除过期刷新会话，之后在登录时最多每小时清理一次。账号会话上限由存储层执行，因此服务端重启后仍然有效。

## 校验与安全

- 未知配置键和无效值会使组合或激活失败，绝不会被忽略。激活失败的行会连同状态和诊断一起报告，而不是被静默跳过。
- `--dump-config` 会打印插值后的值，其中包括凭证。在问题报告中分享前请先脱敏。
- 绝不提交 `.env` 或 `local.patch.yaml`；两者都被 Git 忽略，且都可能包含机密或本机专用路径。
- 每次本地 `run` 都附加 `--patch tools/local-checks/isolated-memory.patch.yaml`。这是防止开发者 `.env` 连到真实数据库的唯一可靠办法。
- profile 或补丁文件的修改在下次启动时生效：profile 只在启动时组合一次。管理 API 可以重载单个行，但那是插件生命周期操作，不是应用新 profile 的方式。重载行为见[插件](plugins.md)。

## 遇到问题时

- **组合因缺少环境变量而失败。** 错误消息会指出变量名；把它加入 `.env`（或进程环境），或在空值合法时改用 `${VAR:-default}` 形式。
- **补丁指向未知行。** profile 与补丁列表不一致；用未打补丁的 profile 或提供该行的 bundle 配合 `--dump-config` 检查行 id。
- **游戏 profile 因客户端数据消息激活失败。** 消息提到的 `data`/`dataraw` 目录不存在；为该构建 ID 准备目录，或改用 `management`。
- **监听器无法绑定。** 其他进程占用了端口；修改按 profile 的端口变量或停止该进程。

按顺序排查的清单见[故障排查](troubleshooting.md)。

## 下一步

继续阅读[部署](deployment.md)以构建并运行发布版本，或阅读[插件](plugins.md)了解这些行的实际作用。
