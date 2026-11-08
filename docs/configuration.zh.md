---
title: "服务器配置"
description: "组合插件 profile、设置环境变量、端口与持久化。"
updated: 2026-11-07
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
| 中继 TCP | `7101`（`KR_1256_RELAY_PORT`） | `7111`（`KR_812_RELAY_PORT`） |
| 管理 HTTP | `8080`（`BFF_ADMIN_PORT`） | `8081`，由 profile 补丁移动 |
| 可选原生选择器 | 不使用 | 挂载 `center-selector.yaml` 时为 `7112` |

`all` profile 把已接入的客户端构建组合进一个进程，每个构建使用自己的游戏监听器。其默认游戏端口从 `7100` 开始以十递增（`kr-1256` 7100、`kr-812` 7110、`id-400` 7120，直到 `cn-408` 7250），每个构建的中继监听在游戏端口加一（`<BUILD>_RELAY_PORT`，对客户端公布 `<BUILD>_PUBLIC_RELAY_PORT`）。旧版构建在中继连上时通过中继进行回合，中继失败时回退到游戏连接；`cn-706` 和 `cn-705` 的中继封包未经审计，回合仍走游戏连接。开放防火墙规则前，请用 `--dump-config` 读取所选 profile 的实际 `listen` 字符串。

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

### 战绩记录

`ingame` 的 `record_battle_statistics` 默认为 `false`。启用时需要明确支持战绩的进度存储、等级表及共用结算发送器。它按游戏模式记录已接受的泡封、击爆、营救、死亡与分数，并保存最终结果。对局中退出记录为逃跑，不发完成奖励；完成记录在展示结算前提交，退出结算画面不会抹掉已完成对局。加载失败与被取消的对局不伪造结果。

Memory、MySQL 和 SQLite 均支持此选项。MySQL 迁移 `0039` 新增 `player_battle_records`；SQLite schema `9` 在玩家快照中加入各模式统计。两种持久化仓库把统计、进度、钱包奖励与原有对局凭据放进同一事务，重新打开数据库后记录保留，重复提交同一对局不会再次累计。旧凭据不会因为重试附带统计而被补录。升级前备份存储；旧 SQLite 读取器拒绝 schema `9`。不会重建此前未记录的历史。所有仓库提供 `max_battle_modes_per_player`（默认 `128`），MySQL 与 SQLite 允许 `1..4096`。模式容量满时拒绝整笔新结算，不淘汰历史。Memory 仍属临时存储，另提供 `max_battle_settlements_per_player`（默认 `4096`）；持久化仓库保留原有数据库结算凭据。

爆破系列在下一回合加载期间保留同一个对局身份及累计计数。已有回合打完后，若对手在局间退出，剩余终局成员会在下一次离场可能取消展示之前完成记录；只有首次加载不算已打过对局。结算重试次数有限，最终失败会记录日志；没有用于恢复任意持续存储故障的持久化 outbox。

### 人物外观与已记录战绩

`lobby.fighter_info` 读取被查询在线账号角色背包中当前穿戴的物品，过滤过期和寿命耗尽项，并在人物资料响应前发布该目标的装备缓存。角色缺失、角色选择不唯一或登录会话已被替换时，不返回成功快照。查询只读，范围是同一 build、同一数据域的在线玩家；不会预览独立预设页或伪造逐实例属性。

独立选项 `lobby.fighter_battle_statistics` 默认为 `false`。开启时需要 `fighter_info` 及同一数据域的战绩仓储。原生主页面“无道具／道具”只接收模式 `1`、`3` 的已记录总数：胜、负、平、逃、泡封、击爆与营救。其它模式、服务器个人分数及竞技场等级不投影。客户端根据各项计数自行计算场次和胜率，保留其自身对平局的处理规则。

没有已知主模式记录，或真实数值会使客户端的有符号计算溢出时，返回原生失败响应；不会截断、缩放或代入全零生涯。关闭该选项时，外观查询保留原生构造器的统计默认值，这些默认值不代表真实历史。Memory 只覆盖当前进程实际记录过的对局，不重建原服生涯或排位资格规则。

已审计主页面结构覆盖 `kr-1256`（KR2026）、`kr-812`、`id-400`、`id-1809`、`cn-324`、`cn-717`、`cn-774`、`tw-454`、`cn-279`、`cn-406`、`cn-706`、`cn-404`、`cn-408`。TW454 完整人物资料交换另由 `BFF_TW_454_FIGHTER_INFO=false` 控制；设为 `true` 才同时开启方言与大厅绑定。结构覆盖不代表原生客户端验收通过。

在隔离的单 build profile 中，将以下 patch 与 `tools/local-checks/isolated-memory.patch.yaml` 一起应用。`set` 保留原有协议、等级与结果配置；缺少能力时启动失败，不生成假数据。

```yaml
- set:
    id: ingame
    config:
      record_battle_statistics: true
- set:
    id: lobby
    config:
      online_players: true
      fighter_info: true
      fighter_battle_statistics: true
```
### 情侣关系存储

内存行还提供作为基础准备的 `ICoupleStore` 关系契约。其 `couples` 映射具有独立上限：`max_relationships`（默认 4096，范围 1–65536）、`max_pending_invitations`（1024，1–65536）、`max_pending_per_account`（16，1–1024）、`max_completed_invitations`（1024，1–65536）、`max_message_length`（512 个 UTF-16 代码单元，1–4096）和 `max_authority_length`（128 个 UTF-16 代码单元，1–1024）。接受邀请与分手会在同一进程内原子更新双方关系。已完成回执有容量限制，可能被淘汰；调用方必须始终使用服务端生成的全新邀请标识。此契约尚无 MySQL 或 SQLite 提供程序，不消耗戒指，也不启用原生情侣请求、登录恢复或跨节点通知。仅有包结构表覆盖不代表这些玩法已经启用。

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

已安装客户端的上传流程与 `tools/client-data/bfdata.py` 使用同一套生成规则。地图 `.wb` 资源（包括嵌套的 `Maps/<Directory>/track.wb`）按原客户端路径解包到 `dataraw`，并在 `sources.tsv` 记录来源和 SHA-256；准备好的 `data`/`dataraw` 导入也会保留这些文件。旧数据树若缺少地图 WB，请先从对应版本客户端重新生成到新目录，再启用读取它的功能。共享的 `WorldBlockFile` helper 读取器保留名称、位置和旋转，供不同模式使用；服务器不硬编码地图坐标。

CN2011（`cn-279`）配置 `ingame.item_mode_fallback_point_count: 4`，在普通道具模式房主地图报告没有随机箱点时启用服务器补充策略：从当前地图的 `#sp_star_` helper 选取最多四个互异且空间分散的位置，没有星点时使用 `#ic_` helper，以 `supply_default_min_ms` / `supply_default_max_ms`（默认 15–30 秒）刷新中立 kind-0 箱。周期刷新属于服务器策略，不代表原版客户端存在固定随机箱标记。原生已报告的随机点继续保留，其他 profile 默认值为零。`item_mode_fallback_maximum_file_bytes` 默认 8388608，`item_mode_fallback_maximum_candidates` 默认 4096；地图资源缺失、不支持或无效时会明确拒绝该报告，不猜测坐标。

CN279 保留年级权重表中的十七种道具和一个原生持有槽。碰撞时由客户端抽取道具，服务器校验结果。CN279 还启用 `item_mode_resolve_invalid_random_pickups`：对存在且符合拾取条件的随机箱，若请求种类为零或不在允许池中，服务器从允许池均匀抽取一次；有效客户端结果继续保留，固定种类箱与不合格对象不能使用该策略。关闭时，未解析的零种类请求被拒绝且不消耗箱子。兜底分布属于服务器策略，不等同于原生年级权重。原生行动计量掉落保留初始速度；空出持有槽后，仍存在且曾因满槽被拒绝的箱会换用新对象 ID，掉落箱重建时静止。使用道具会广播给包括使用者在内的所有参与者，团队医疗包 129 也走原生效果路径；服务器不为此另造 HP 账本。这些自动化路径仍需原生客户端验收。

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

**`player-settings`** 挂载 `player.settings`，即玩家自定义按键的类型化存储。封包本身由 `dialect.messages` 行的 `customized_keys` 选项绑定，由 `kr-812`、`kr-1256`、`cn-774` 和 `tw-454` 声明；其他 profile 需先审计其原生控制名称和封包路径。每个 `player.settings` 行都需要 `protocol`，并使用按 profile 的 `scope`，避免混淆不同构建的名称。持久化提供程序按账号保存映射；MySQL 通过迁移 `0032_customized_keys.sql` 存储。每个提供程序用 `customized_keys` 配置每账号预算（`max_scopes` 1–64、`max_mappings` 1–512、`max_name_characters` 1–1024，三者组合必须能放入封包体）。超出预算会直接失败，而不会驱逐其他已保存映射。

`dialect.messages.config.science_lab` 默认 `false`。`kr-1256`、`cn-774` 和 `tw-454` 已验证的操作行允许此选项对八种实验室操作统一返回客户端的实验室关闭结果（KR 为 5，CN/TW 为 4）。激活要求每组请求与回复均已验证且布局齐全，表不完整会直接失败。这不是完整实验室：尚无配方、费用、成功概率、奖励或物品变更，也尚未建立可信的实验室状态开启请求链。启用此选项不会发布实验室状态或开启界面。KR 状态的 34 字节、CN 状态的 36 字节和 KR 空物品列表的 26 字节固定测试仅代表数据覆盖。

过期物品丢弃也以同样方式声明：`dialect.messages` 行的 `inventory_actions` 选项绑定 `gcpReqInventoryThrowItem`/`gspRepInventoryThrowItem`（每个 legacy 构建和 `kr-1256` 均开启），构建自己的 bundle 内的 `inventory.throw` 行通过存储装备服务移除该物品。行为被有意限制得很窄：只移除一个已认证玩家两个背包中的该过期物品；若有匹配权益仍有效，整个移除会被拒绝；构建的类别 6 排除规则会被遵守。比赛中的请求会被拒绝。真实客户端验收仍待进行；仅凭封包表覆盖不足以在另一个构建上声明该选项。

**`launcher.http`** 是可选的公共启动器 API。它默认禁用，与管理工作分离。它接受 `minimum_launcher_version`（留空则关闭检查）与 `client_policy`，即启动器执行的按插件分类的类型化规则；规则只引用商店标识和已声明的商店来源，绝不包含可执行文件路径。配置最低版本后，较旧的启动器会收到 HTTP 426。每个大厅行的 `authentication` 选项（`BFF_LOGIN_AUTHENTICATION`，默认 `ticket`）只接受能兑换启动器票据的登录，此时需要本行处于激活状态；`open` 接受任何账号名，只适用于可信的局域网。[启动器](launcher.md)页面介绍该 API、客户端预设、账号与票据流程以及面向运营者的配置。

启动器请求与登录会话限制在 `launcher.http` 行配置：

| 配置项 | 默认值 | 行为 |
|---|---|---|
| `requests_per_minute` | `120` | 每个连接来源 IP 每分钟可接收的请求数；转发头不会改变来源 IP。 |
| `max_rate_limit_addresses` | `4096` | 最多保留的 IP 窗口数（1–100000）；满时淘汰最早的窗口。 |
| `max_sessions_per_account` | `8` | 每账号的有效持久登录会话数（1–1024）；新登录会撤销创建时间最早的会话。 |
| `max_sessions` | `1024` | 已有的进程内访问凭据及待用游戏票据容量上限。 |

撤销旧会话时也会清除它在本进程中的访问凭据和待用游戏票据。监听器启动前删除过期刷新会话，之后在登录时最多每小时清理一次。账号会话上限由存储层执行，因此服务端重启后仍然有效。

## 可选 UDP 传输

UDP 由 `net.udp` 插件提供。游戏登录、房间准入和对局身份认证继续使用 TCP；UDP 承载已审计的原生移动与发现流量。所有现有配置默认都不会挂载 UDP 监听器。

| 可选补丁 | 已审计配置 | 传输方式 |
| --- | --- | --- |
| `profiles/bundles/udp-relay.patch.yaml` | `kr-812`、`cn-717`、`id-1809`、`kr-1256`、`cn-774`、`tw-454` | 通过服务端 UDP 端点传递已认证的移动；接收者未绑定端点时回退到 TCP。 |
| `profiles/bundles/udp-peers.patch.yaml` | `cn-279`、`cn-324`、`cn-406`、`id-400`、`cn-404`、`cn-408`、`cn-705`、`cn-706` | 服务端协调端点发现及玩家之间的直接移动传输；失败路径保留对局 TCP 投递。 |

将其中一种补丁应用到对应的单版本配置。两种补丁互为替代，其不带版本前缀的行标识不适合直接用于 `all`。`BFF_UDP_PORT` 指定空闲 UDP 端口，`BFF_BIND_HOST` 指定监听地址，`BFF_UDP_PUBLIC_PORT` 可选填外部映射的 UDP 端口（零表示通告实际绑定端口）。UDP 端口转发独立于游戏与中继 TCP 端口。玩家端点发现使用已认证游戏服务器的 IPv4 地址，中继旁路使用中继服务器的 IPv4 地址，因此外部 UDP 映射必须使用对应公网地址。

例如，先将普通 TCP/管理端口改到本地空闲端口并准备该版本客户端数据，再执行：

```powershell
$env:BFF_MYSQL_PASSWORD = "isolated-unused"
$env:BFF_MYSQL_HOST = "127.0.0.1"
$env:BFF_MYSQL_PORT = "1"
$env:BFF_LOGIN_AUTHENTICATION = "open"
$env:BFF_BIND_HOST = "127.0.0.1"
$env:BFF_UDP_PORT = "7603"
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- run --profile cn-324 --patch tools/local-checks/isolated-memory.patch.yaml --patch profiles/bundles/udp-peers.patch.yaml
```

挂载任意可选 UDP 补丁后，`BFF_UDP_ENABLED` 默认为 `true`。设为 `false` 可保留 TCP 运行，同时不打开 UDP 监听器、不启动 UDP 协商。单人房间默认使用 TCP。只有对局开始、所有 relay 初始化且端点验证完成后，才允许使用 UDP 移动。

UDP 设置仅限正式游戏房间。CN279、CN324、CN406 和 ID400 的原生游戏场景不发送发现探测，因此在等待房间中准备端点并确认 peer 表。完整房间安装会使已准备的表失效；控制消息与房间操作共用串行队列，不能越过房间安装或场景切换。打洞和直接移动仍须等待对局开始且全部 relay 初始化。其他已审计版本在这个屏障后发起协商。登录及自动场景、主菜单、大厅、广场和单人训练均不协商 UDP；房间强制开关也不能绕过这个限制。

房主可以用 `!udp on` 强制协商 UDP（包括单人房间），用 `!udp off` 保持 TCP，或用 `!udp` 切换当前房间偏好。房间命令无法开启部署中不可用或已全局关闭的 UDP。未验证或不可达的路径保留 TCP 回退。所有插件命令都等价接受 `/`、`!`、`！`，这些字符仅用于触发命令解析；游戏内提示使用 `!`，因为 `/` 可能被客户端原生指令截获。管理后台和控制台还支持直接输入 `udp on` 等无前缀命令，其用法提示使用 `/`。

房间内任何成员都可以输入 `!udp status` 查看是否在使用 UDP：传输方式（经服务器的 `relay` 或直连的 `peer`）、房间偏好，以及每名成员的 `UDP`、`NEGOTIATING` 或 `TCP` 状态和原因，例如等待对局开始、已发出邀请但未收到 UDP 探测、已验证，或客户端回退到 TCP。服务器关闭 UDP 或该客户端版本没有 UDP 设置时也会说明。`!plaza` 立即离开当前房间（对局中也可以）并进入广场：座位按主动退出的方式释放，因此对局会结算离开者，其他人继续对局，最后一名玩家离开时房间会被删除。回退到 TCP 时会说明原因：没有 UDP 探测到达服务器、挑战应答在返回途中丢失、按座位列出的发现或节点表超时、比赛连接已关闭，或玩家之间没有直连路径。

`match-udp-directory.max_sessions` 限制旁路认证租约数量。`match-udp-peers` 的 `max_rounds`/`max_peers` 限制保留的对局与玩家数量；发现、端点表确认、打洞及控制消息的截止时间均可配置。私网端点候选只会在观测到相同公网 IPv4 地址的玩家之间共享。换房、换局、掉线、消费者卸载及中继替换会撤销对应路径。目前仅支持 IPv4，不能保证任意 NAT 或防火墙都允许玩家直接互通。

包表还审计了 `cn-735`、`cn-738`、`cn-742` 的旁路封包：四字节 cookie 与浮点移动坐标。其传输适配器已有隔离线路测试，但研究配置尚未挂载中继补丁所需的完整房间/对局组件。这表示封包覆盖，不代表可用的端到端配置。自动化套接字场景模拟原生客户端协议，真实游戏客户端验收仍须另外进行。

多版本服务端应将对应补丁的操作放入各个目标版本的可选 `local.patch.yaml`，使用不带版本前缀的插件行 ID。共享 profile 的补丁先于各版本插件行执行，因此不能在 `all` 的补丁里修改 `kr-812.dialect.rooms` 等带前缀的行。每个版本应使用独立 UDP 端口。peer 补丁同时启用必需的原生 relay；`cn-705` 和 `cn-706` 默认保持这些行停用，且该补丁不会启用场景种子表权限。

UDP 套接字订阅、租约注册、房间队列回调、使用 Span/out 参数的查询和 IP 端点记录目前需要原生插件；远程 SDK 尚无这些操作的适配器。可序列化的房间快照和中继控制操作仍通过契约 schema 提供。

`match-udp-peers` 支持 `punch_retry_ms`（默认 250；0 关闭重试），在 `punch_window_ms` 内重复发送原生开始打洞指令，不延长窗口、不更换节点标识。直连路径仍要求当前已认证对局连接报告对应玩家的成员关系。`!udp status` 区分未收到原生结果和客户端报告成功路径为零；仅凭这些结果不能认定是防火墙或 NAT 故障。

对只在等待房间运行探测的版本，`discovery_refresh_ms`（默认 1000；0 关闭刷新）会在房间等待阶段使用相同玩家身份重复发送原生单次探测请求。进入加载后停止重发，因为这些客户端在加载阶段不再发送探测。公网端点或本地候选地址变化时，重新发送列表并再次等待确认。在初始化完成的回合开始打洞前停止探测。

## 可选服务器发现

schema-1 服务器目录新增响应字段 `generated_at_utc` 和目标字段 `observed_at_utc`、`expires_at_utc`、`status_revision`（Contracts 4.5.0）。时间戳采用 UTC RFC3339；首次接受上报前，三个目标字段均为 null。目标字段描述同一次由来源维护的上报。重复读取或重新注册同一租约不会续期；过期的手动入口保留过期元数据，撤销租约或时钟变化可缩短到期时间。到期时间不超过来源剩余租期映射到 UTC 的截止点。修订值只能作不透明的相等比较，不是可排序版本或租约凭据。用于分配时必须拒绝缺失、格式错误或过期的元数据；生成新响应不会为目标续期。这些公开的新鲜度字段不授予账号准入，也不将票据绑定到后端成员；自动启动仍须取得明确准入的本次候选集合，其有效期同时受上报和认证授权的到期时间约束。

可选的 `cluster-launch-selection.patch.yaml` 在票据签发端挂载 `cluster.launch-selection`。通过 `BFF_CLUSTER_NODE_ID`、`BFF_CLUSTER_AUTHORITY_ID`、`BFF_CLUSTER_AUTHORITY_URL`、`BFF_CLUSTER_DATA_SCOPE`、`BFF_CLUSTER_AUTOMATIC_SELECTOR_ID` 明确配置本地节点、认证权威标识及准确 API 源、持久化范围和自动入口标识。已认证的 `POST /v1/launch` 携带 `serverSelection: true` 后返回与票据绑定的 `serverSelection`，包含 `schema`、`grantId`、`build`、`authorityId`、`policyRevision`、`dataScope`、`expiresAtUtc`、`generated_at_utc`、`servers`。已授权成员池可以包含离线或尚未上报的配置节点；这表示可认证范围，不代表当前可分配。快照固定本次启动的原生入口标识、物理节点标识和端点绑定。票据保留原有有效期并受授权 bearer 限制，不会因为短心跳租约而缩短慢启动登录时间。`POST /v1/launch/servers` 使用同一认证会话及 `{ticket,build,grantId}`，只刷新原有绑定内的上报状态，不消费或替换票据、不增加成员、不更换端点、不延长来源到期时间。票据已消费、授权已过期或无效时返回 HTTP 409 `admission_closed`，刷新循环可在正常登录后结束。可信工作节点兑换时，在一次性消费的同一个锁内检查已认证节点身份是否属于票据成员；本地兑换使用明确配置的本地节点身份。普通启动保持原有行为。此 API 不代表原生重定向、共享存储或跨节点房间准入已完成。 只有明确启用 Center 的 gateway 模式且目录存在自动入口时，挂载签发端选服行后，所有已认证票据才受范围限制，省略标记也不能绕过；明确传入 `serverSelection: false` 返回 HTTP 409 `server_selection_required`。`launcher.http` 的 `max_selection_targets` 限制每个授权保留的入口端点总次数，默认 128。 每个已授权工作节点必须对应唯一的规范手动入口，且端点绑定一致；签发端在发布服务前检查所有已配置版本，缺失或存在歧义的原生映射会使激活失败。即使通过自动入口刷新，也不能更换该规范入口标识。

单服 profile 默认不挂载 `cluster.directory` 和 `cluster.node`。`profiles/bundles/cluster-directory.patch.yaml` 示例为 `kr-1256` 同时挂载目录和上报插件；`cluster-node.patch.yaml` 仅挂载上报插件。明确配置上报端的 `builds` 与各节点的 `endpoints`，并通过部署密钥为每个节点提供不同的随机 `BFF_CLUSTER_NODE_KEY`。私有监听器默认使用 `127.0.0.1:8093`；上报地址的 HTTP 或 HTTPS 由管理员明确选择，不自动升级。

可选的 `profiles/bundles/cluster-room-directory.patch.yaml` 挂载 `cluster.room-directory`，提供 `IFederatedRoomDiscovery`。必须设置 `node_id`、`directory_url`、`access_key` 和明确的 `builds` 白名单。每次调用都经过认证读取当前房间列表；请求失败时不会返回旧缓存。`max_rooms`、`max_response_bytes`、`max_concurrent_requests`、`request_timeout_seconds` 分别限制房间数、缓冲字节数、并发请求数和整个读取过程的期限。超出并发预算会直接报错，不排队。重定向、无效响应和未配置的版本也会明确失败。房间目录成员关系独立于账号数据，仅控制可见范围；发现服务不授予入房权限、不预留座位，也不修改原生房间列表。单服 profile 不挂载该行。Contracts 4.6.0 增加发现接口。

`cluster.directory.room_preparation_permits` 可选开启仅供已认证节点使用的准备许可接口，默认 false。来源节点向 `/v1/nodes/{source}/room-preparations` 提交当前租约令牌、代次及目标节点、目标代次、客户端版本、原生房间号、房间创建标识和账号编号。签发要求节点租约新鲜且版本就绪、非空数据与入房范围相同、共享房间可见范围、明确的同一 master-issued 认证权威及同一已上报权威数据库标识。目标节点使用自身节点密钥和当前进程租约，在 `/v1/nodes/{destination}/room-preparations/claim` 一次性兑换。兑换时再次核验租约、端点权威及准确的等待中房间；已释放节点和复用房号不能继承许可。来源节点可在签发路径以已认证 DELETE 取消自身许可。`max_room_preparation_permits` 限制保存数量，默认 512；`room_preparation_permit_lifetime_ms` 设置单调时钟有效期，默认 10000，允许 100..60000。目录只保存令牌哈希，不接收房间密码。此许可仅授权目标节点准备，不预留座位、不认证原生游戏连接、不转移账号占用，也不启用跨节点房间点击。友好服务器与独立目录仍仅共享可见信息。

已发布的真实游戏房间通过 `IPublishedRoomDirectory`（Contracts 4.7.0）提供分离的原生列表快照。每次创建都有独立的 `instanceId`，不依赖可复用的房间编号；心跳保留原生列表需要的房主昵称、模式及设置，不包含密码、账号记录或会话前缀。目录及发现客户端均检查原生元数据与摘要的一致性，并使用 `max_room_settings_bytes`（默认 65536）限制所有设置块与回合选择的总字节数。隐藏房间、训练及教程房间不会发布。这些元数据暂不把远端条目投射到原生列表，也不授权跨节点入房。

可选补丁 `profiles/bundles/cluster-room-browser.patch.yaml` 为每个 `client_build` 挂载一个 `cluster.room-browser`，消费认证目录并提供 `IRoomBrowser`（Contracts 4.8.0）。`node_id` 必须与目录客户端一致。浏览状态与其游戏连接同生共灭，连接数已由监听器限制。`max_aliases_per_connection` 限制单连接整个生命周期的房间身份数量（默认值和上限均为 32767，即原生有符号 16 位别名）。本节点使用注册表实时快照替换心跳中的旧条目，其他节点的完整原生元数据使用独立正数别名；重排不改变别名，连接内不回收旧身份。点击远端房间时重新请求目录并核对节点代次与房间创建身份，加入提示不代表认证。目标消失、重建或不可用时拒绝加入，绝不回退到恰好同号的本地房间。刷新失败清空可见别名，连接关闭或插件卸载释放状态。仅经过认证的游戏主连接允许浏览；本地加入仍检查密码、容量及阶段，并额外核对创建身份。只有挂载下述迁移补丁、且目录在两个节点的 `native_migration_builds` 中都列出该版本时，点击远端房间才会发起已登录迁移，否则拒绝。单服配置不挂载此行。

可选的 `profiles/bundles/cluster-plaza-federation.patch.yaml` 挂载 `cluster.plaza-federation`，在节点之间共享广场：同一客户端版本的玩家无论登录哪个节点，都能在广场中看到彼此。在目录行设置 `plaza_federation: true`，并在每个节点列出共享的 `builds`，`node_id`、`directory_url`、`access_key` 与心跳行相同。只有 `data_scope` 相同且非空、心跳证明了该范围数据库的节点才共享广场；拥有独立数据的友好服务器互相不可见。每个节点提交本地广场的进入、移动和离开（`flush_interval_ms`，默认 100），并用一个请求为所有版本长轮询其他节点的变化（`poll_wait_seconds`）。远端玩家在本地广场占一个座位，使用与本地玩家相同的座位和位置数据包显示，客户端看不出区别。租约失效的节点会带走其玩家；重连或目录重启后，节点会收到快照并重新发送自己的广场。`max_queued_events`、`max_plaza_events` 和 `max_plaza_members` 限制队列大小；客户端广场仍为 36 个座位。Contracts 4.25.0 增加 `IPlazaFederationEndpoint`。

可选补丁 `profiles/bundles/cluster-migration.patch.yaml` 包含节点级迁移行（broker、arrivals、destination），`cluster-migration-build.patch.yaml` 包含按版本的行（浏览、来源、准入，以及 `lobby`、`dialect.messages`、`ingame` 开关）。先挂载 node、room-directory、account-store、account-sessions 与 write-fencing 补丁，再挂载这两个，并在目录上设置 `migrations: true` 与 `native_migration_builds`。目标端点的 `listener_id` 指游戏监听行：单版本配置为 `game-listener`，多版本配置为 `<build>.game-listener`。须在来源行设置 `channel`、在准入行设置 `channels`，取该版本经核查的值；补丁本身不填写，未核查的版本会启动失败，而不会继承其他版本的值。客户端只接受其允许频道的迁移回复：kr-812 无条件允许 50、60、80，cn-324 允许 50、60 而拒绝 80。每个被持有的账号占用一个数据库连接，数据库连接上限必须覆盖所有节点的在线玩家。

该按版本补丁还会设置 `protocol.config.accept_retained_moves: true`，使迁移客户端的 `gcpReqMoveInto` 可以在补丁请求之前打开目标连接；其处理程序仍须授权继续握手。启动列表只写数据包名称，不写数字：`pre_handshake_opcodes` 和 `alternate_handshake_sequence` 接受客户端到服务器的数据包名称，由各版本通过自己的操作码表解析；数字、未知名称、服务器到客户端的名称或仅限中继连接的名称都会在启动时终止服务器。客户端在每条辅助连接上先发送空标记的版本，在其配置档中以 `auxiliary_prefix` 声明一次（cn-279、cn-324 和 cn-406 发送 `gcpStartGPK`）。`answer_center_status: true` 将原生选服的状态查询识别为该前缀加 `gcpReqCenterUserCnt`；显式配置的 `alternate_handshake_sequence` 仍然优先。目标端点公布配置档自身的版本及其 `GAME_PUBLIC_PORT` 配置档变量（取自 `<BUILD>_PUBLIC_PORT`）；多版本配置档需用每个版本一项的端点列表替换它。

对于预置的 13 版本 `all` 配置，使用 `profiles/all/migration.patch.yaml` 替代单版本的节点级迁移补丁。该补丁逐一指定 `<build>.game-listener`，并通过各版本的 `<BUILD>_PUBLIC_PORT` 公布端口，默认使用该版本的预置端口。将 `cluster-migration-build.patch.yaml` 的操作追加到各版本可选的 `local.patch.yaml`，随后明确设置源端 `channel: 60` 和准入端 `channels: [60]`。这些操作使用不带前缀的行 ID；组合时，源端与准入行会映射到该版本自己的 `inventoryview` 子树。节点、房间目录、账号所有权、会话及写入保护行只在共享配置层挂载一次。将节点报告器及房间目录的版本列表设为选定版本。在目录权威端启用迁移，逐个列出参与节点支持的版本，并配置相符的数据、加入及房间目录范围。独立数据库必须保持独立范围，不能仅凭目录发现获得共享账号迁移权限。单服 `all` 配置保持不变。

`home_unavailable_notice_enabled` 默认 `true`，要求存在原生通知发送器。迁移补丁通过版本变量 `MIGRATION_HOME_NOTICE`，仅为没有已确认通知处理能力的 cn-406、cn-774、tw-454 关闭它。返服被拒绝时始终记录警告，并让玩家携带账号权限留在访问节点。

保留身份迁入服务按各版本的 `inventoryview` 子树隔离，同一版本的大厅与迁入处理仅共享本版本的登录服务。

`ingame` 行可通过 `room_admission_reservations: true` 启用目标房间名额预留，为每个版本发布可卸载的 `IRoomAdmissionDirectory`（Contracts 4.9.0）。`room_admission_lifetime_ms` 默认 10000，范围 100–60000。预留数量受房间座位数限制，使用单调时钟过期，并固定房间创建身份与账号。普通加入及机器人不能占用预留名额；取消、超时、房主关闭座位、房间开始或关闭及卸载都会释放未用名额。消耗时再次核对房间密码，要求同账号、同版本且独立完成认证的游戏主连接，并保持原菜单或广场场景，再复用正常入房及成员通知。预留不等于认证或账号所有权；尚未连接远端房间点击、目标原生套接字认证和源节点所有权迁移。单服默认不启用。

`launcher.http` 提供明确的 `center: { enabled: false, mode: gateway }` 配置，默认关闭。模式仅允许 `native`（由服务端承担原生 Center 选服）和 `gateway`（由 Launcher 启动本地原生协议 Gateway）。`GET /v1/catalog` 与 `GET /v1/servers/{build}` 返回相同的顶层 `center: {enabled,mode}` 对象；旧目录没有声明时视为关闭。Launcher 先读 catalog；关闭或 native 模式保留 catalog 游戏端点，无须请求服务器目录或启动 Gateway。只有明确启用 gateway 模式时才读取 schema-1 目录，并要求两处 Center 声明一致。Launcher 中仍为一个入口，自动、手动和独立服务器选项在游戏原生选服界面显示。只有服务端返回 `automatic: true` 的入口才启用自动分配，其 `targets` 限定候选池；没有自动入口的 Gateway 目录仅支持手动选服，不签发选服授权。关闭或 native 模式即使挂载选服服务也不签发授权；明确请求会返回 HTTP 409 `server_selection_unavailable`。选项身份、名称与物理节点身份、地址保持独立。这些 HTTP 设置本身不实现选服后的认证或跨节点入房。 只指定 mode 不会启用 Center；明确设置 enabled=true 后，省略 mode 默认 gateway，只有明确指定 native 才走服务端 Center。

手动入口允许空 `nodes` 列表，用于保留不可选项而不猜测目标地址；自动入口仍须配置候选。启动器接口保留空入口，读取方必须保持其不可选，不解析或连接虚构地址。

入口支持可选的 `short_name`：长度为 1..128 个 UTF-16 代码单元，不能全为空白、不能含控制字符，代理项必须成对。目录和启动授权均以 `short_name` 返回；未配置时仍回退到完整 `name`。健康刷新保留授权时的两个名称。简短地区名称须明确配置，不从完整名称推导；名称不会改变端点或认证绑定。

`selectors[].nodes` 决定可见节点，手动选项有零个或一个节点。`selectors[].automatic` 仅返回租约有效、已就绪、未排空且未满员的候选，并要求成员属于同一非空 `data_scope`。`nodes[].data_scope` 和 `nodes[].join_scope` 是不透明的关系声明：空值不授予共享或加入权限，同名也不自动实现这些能力。`nodes[].room_directories` 独立控制房间摘要共享，允许友好服务器仅共享房间目录而保持账号隔离。

自动选项还必须设置 `persistent_domain_id`，通过 `SELECT REPLACE(domain_id, '-', '') FROM cluster_data_domain WHERE singleton_id = 1` 读取权威数据库标识。MySQL 迁移 0035 只初始化一次。先在权威数据库执行迁移，再以 `auto_migrate: false` 启动工作节点；共享数据的工作节点必须连接同一权威数据库。每次心跳在请求时限内重新读取标识，只有匹配的候选才进入自动分配池。配置错误的节点不会移除健康且匹配的候选。同一已固定数据域的手动入口仍保留显示，但上报标识不匹配时标记为离线。恢复的数据库副本会保留标识，因此此校验用于发现配置错误，不能证明数据库身份或实时复制；独立运行的副本须在启用分配前使用不同标识。没有标识服务的节点只能以空 `data_scope` 上报，不能进入自动分配池。

注册建立带代际隔离的租约，递增序号的心跳负责续期。过期节点从自动候选和房间目录中移除，手动选项保留离线状态。就绪要求宿主就绪且游戏监听服务已注册。上报只包含已认证登录人数和正式游戏房间摘要，不包含密码和玩家身份；跨节点房间在实际路由实现前始终为 `joinAllowed: false`。

目录限制节点、选项、房间数量、请求大小、连接数与时限。`lease_seconds` 必须大于 `heartbeat_seconds + request_timeout_seconds`，不合法的组合会使上报失败。`draining` 排除自动分配，但保留手动状态和房间摘要。服务器发现不提供共享持久化、全局会话权威、跨服加入、队伍互通或中继迁移；多节点接纳正式玩家前须分别实现并验证。

可选行 `persistence.mysql-account-ownership` 提供账号占用原语 `IAccountOwnershipStore`。完成迁移 0036 后才能挂载 `profiles/bundles/cluster-account-store.patch.yaml`，并设置 `BFF_CLUSTER_ACCOUNT_CONNECTION_STRING` 和权威数据库标识；根据数据库内存设置 `max_owners`。每个占用使用独立、禁用连接池的连接立即尝试获取命名锁。校验同时检查原始锁和固定的数据库标识，失去占用后不会自动重新获取。卸载会等待进行中的获取操作结束并关闭已有占用。该原语尚未接入游戏登录或隔离仓储写入，不能单独保证共享节点登录安全；单服配置不加载它。 获取占用返回持有原生连接的生命周期句柄，远程 SDK 尚无对应结果适配器，契约清单明确标记该获取操作不受支持。

可选行 `cluster.account-sessions` 将账号占用接入游戏登录。先挂载账号存储补丁，再挂载 `profiles/bundles/cluster-account-sessions.patch.yaml`，并对每个大厅设置 `require_account_ownership: true` 和 `duplicate_login: reject_new`。缺少接入服务时激活失败。会话只为打开的游戏连接创建，其数量由监听器的连接上限决定；`verification_seconds` 和 `verification_concurrency` 限制后台检查。失去权威会关闭连接；卸载会取消等待中的接入、关闭已接入连接并等待清理完成。`IAccountSession` 特征允许验证占用，但不转移其生命周期。该层尚未隔离进行中的仓储写入和角色创建；完成这些写入路径的隔离与排空前，不要启用共享生产数据库。随包提供的单服配置均不挂载这两个补丁。

账号交接还要求迁移 0037，并在 `persistence.mysql-account-ownership` 上显式设置与目录身份一致的 `node_id`。默认空值保留普通占用功能，不发布 `IAccountHandoffStore`。源节点只能登记自己的交接；数据库仅允许记录中的目标节点认领、源节点撤回。`cluster.account-sessions` 只有在该存储存在时才发布 `IAccountSessionHandoff`。远程 SDK 无法传输原生占用句柄、迁移 IP 地址或代理回调注册，这些方法在提供适配器前明确不受支持。此设置不会在单服配置中启用原生迁移。

可选的 `cluster.migration-broker` 要求与 `cluster.node` 使用相同的 `node_id`、`directory_url`，`access_key` 为该节点的私有密钥。目录必须开启 `migrations`（默认 false）。请求携带当前租约凭据，使用 snake_case JSON，经 TLS 或安全隧道传输；诊断日志不包含密码。共享账号迁移要求加入域、数据域、认证权威和实际持久域标识一致；访客要求明确的加入信任及不同数据域。仅启用传输不会启用原生迁移或预留席位。收件箱满返回 503，重复标识或失效租约返回 409，未收到答复返回 504。

| 插件行 | 配置项 | 默认值 | 范围 |
|---|---|---|---|
| `cluster.directory` | `max_pending_migrations` | 256 | 1..4096 每个目标 |
| `cluster.directory` | `migration_request_timeout_ms` | 5000 | 500..30000 |
| `cluster.migration-broker` | `migration_request_timeout_ms` | 5000 | 500..30000 |
| `cluster.migration-broker` | `max_concurrent_requests` | 4 | 1..32 |
| `cluster.migration-broker` | `max_body_bytes` | 65536 | 1024..1048576 |
| `cluster.migration-broker` | `poll_wait_seconds` | 20 | 1..25 |
| `cluster.migration-broker` | `http_timeout_ms` | 35000 | 1000..90000 |
| `cluster.migration-broker` | `retry_delay_ms` | 1000 | 100..30000 |

HTTP 超时必须大于轮询等待和回调时限。目标收件箱由一个受管任务轮询；出站并发容量耗尽时立即拒绝新请求。

可选行 `cluster.migration-destination` 需要 broker、arrivals、心跳身份及账号会话交接服务。`node_id` 必须与心跳身份一致；`endpoints` 包含 1..64 个不同的 `{ client_build, listener_id, host, port }` 项。host 必须是规范 DNS 名称，目标端逐次请求解析并要求得到 IPv4 地址。房间请求还需要对应版本的 `IRoomAdmissionDirectory`。缺少端点、目标租约代次失效或 DNS 解析失败时拒绝准备；回原服请求不预留房间席位。此行尚不认证原生游戏连接，也不启用远端房间点击。

必填的 `listener_id` 是目标传输行的完整 ID，包含配置前缀。激活时必须恰好找到一个具有该 ID、且使用对应版本游戏协议的可见活动监听器。待迁入记录保留此 ID，消费时核对 `ConnectionAcceptedEvent` 实际观测到的连接；未被观测、已关闭、非 IP 或来自其他监听器的连接，即使版本、账号编号和地址一致，也不能取走记录。拒绝的尝试保留记录供正确连接使用。此检查不授予账号权限，原生迁入处理器仍须先认领交接，再加载玩家状态。

`arrival_lifetime_ms` 默认 10000（100..60000），`cleanup_interval_ms` 默认 1000（100..10000），`operation_timeout_ms` 默认 5000（100..30000）。房间的 `room_admission_lifetime_ms` 应覆盖到达窗口及准备耗时。挂载 `cluster.migration-arrivals` 并配置 `max_arrivals`（默认 64，1..4096）：等待到达和已过期但仍待清理的记录共用此容量。过期到达不能被消耗；取消失败会记录错误并重试，期间不释放容量。卸载在操作截止时间内取消剩余预留并报告失败。成功取走到达记录的消费者负责后续入房或取消该预留。

`INodeLeaseSource` 与 `IMigrationArrivalCleanup` 是共享 Contracts 4.12.0 服务，使独立加载的插件保持相同接口身份。租约记录可通过 SDK 序列化，但其持有令牌是私密信息，诊断文本不包含令牌。到达清理记录包含 IP 地址，且完成操作保留对象身份约束，因此仍需原生适配器。

可选 `profiles/bundles/cluster-account-write-fencing.patch.yaml` 要求先挂载账号存储和会话补丁、执行迁移 0036，并在大厅启用账号占用。它设置 `core.packet-router.decoratable: true`、启用 `persistence.mysql.require_account_write_authority` 并挂载 `cluster.account-mutations`；未明确开启装饰的普通路由仍直接调用。`IAccountMutationScopes` 将不可变占用句柄绑定到原始游戏会话，包分发选择该会话，对局逐人结算和奖励卡发放则选择收款玩家的原始会话；延迟旧任务不能借用后来会话的权限。受保护的 MySQL 写入在修改玩家状态前，使用同一连接和事务锁定并校验权威行；占用接管等待已经准入的事务结束。缺失或已替换的权限直接失败，不重新获取。新账号角色创建在事务内初始化，修改已有账号需要其占用。Contracts 4.4.0 提供这些原生生命周期和事务接口，远程 SDK 明确拒绝调用。此功能不配置共享存储、不自动保护无关插件，也不实现跨节点入房；启用共享生产数据前仍须审核其余全部写入者。单服配置不挂载此补丁。

## 校验与安全

`nodes[].authentication` 明确配置 `authority_id`、`api_url`、`mode`（`master-issued` 或 `own-authority`）及 `policy_mode: authority`。同一认证身份只能对应一个由管理员配置的 HTTP 或 HTTPS 地址，使用该认证服务的目录及所选客户端策略，不跨独立认证服务隐式继承或合并策略。缺少此元数据不授予认证信任。

可选 `cluster-ticket-authority.patch.yaml` 在本地 Launcher 票据签发器旁挂载 `cluster.ticket-authority`。每个可信节点须有不同的兑换密钥和明确的客户端版本白名单。私有 `POST /v1/nodes/{node}/tickets/redeem` 单次消费 `{ticket,build}`，返回账号、Launcher 语言及启动身份。`cluster-ticket-client.patch.yaml` 在工作节点挂载远程提供者，替代本地 Launcher 票据提供者；工作节点游戏登录策略必须要求票据。配置 `BFF_CLUSTER_TICKET_URL`、`BFF_CLUSTER_NODE_ID`、`BFF_CLUSTER_REDEMPTION_KEY`，认证地址的 HTTP 或 HTTPS 由管理员明确选择。兑换请求异步且有界，不重定向、重试或回退为账号名。超时前票据可能已消费，应重新取得启动票据。独立认证服务需要自身已登录的 Launcher 会话和启动票据。此功能尚不等于全局会话归属或原生选服认证集成。

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

## 对局取消与开局规则

以下 `ingame` 行选项适用于所有版本。对局加载或进行中有玩家掉线时，对局会被取消：不显示结算，其余所有玩家都被送回广场，方式与等待房间中退出的玩家相同。`cancel_round_on_disconnect`（默认 `true`）可关闭此行为，此时掉线的玩家只是单独离开。玩家主动退出房间或输入 `!plaza` 通常让其他人继续对局。启用僵尸逃生后，Lua 房主离开会取消本局且不发奖励。`load_timeout_ms`（默认 `60000`，`0` 表示不限，最大 `600000`）在届时仍有玩家未完成加载时以同样方式取消对局；该值应大于最慢客户端的地图加载与比赛连接建立时间。

在僵尸模式中，只有所有在场玩家都被感染时僵尸才获胜；连接已经关闭的玩家不算作幸存者。

两条开局规则默认关闭。`start_min_players`（0–32，座位与 AI 玩家合计）在人数不足时拒绝房主开局。`start_requires_team_balance` 在红蓝队伍模式中，按 `assign_spawn_teams_on_start` 为无队伍玩家分队之后，两队人数相差超过一人时拒绝房主开局；否则无队伍的玩家计入红队。客户端没有这类规则的拒绝原因，因此服务器只记录日志，房间继续等待。原生自动开局不受检查。

## 下一步

继续阅读[部署](deployment.md)以构建并运行发布版本，或阅读[插件](plugins.md)了解这些行的实际作用。

## 现代版本等待房间踢人

`dialect.rooms.config.room_kicking` 默认为 `false`。在 kr-1256、cn-774 和 tw-454 上启用后，已审计的昵称请求和移除通知接入共享的房主权限逻辑。只有等待房间的房主能移除另一位在房玩家；通知先于座位移除发送，被移除玩家返回广场。此选项要求 `modern_rooms: true`，不能与 `plaza_only` 同用；缺少任一原生包名或布局时，激活失败。

`dialect.rooms.config.npc_rounds` 用于应答现代版对局房主的原生波次管理器，海盗模式依赖它推进。kr-1256 配置包默认开启。在海盗模式房间中，房主的波次开始、波次结束和刷怪表应答发送给所有成员，全部波次结束的应答只发给房主；其他模式忽略这些请求。普通对局中的刷怪请求会校验格式但暂不转发，因为其传输方式尚未确认。初始教程使用同一绑定，仍只应答其单人房主。此选项要求 `modern_rooms: true` 且挂载对局绑定。

单版本隔离试验时，在 `--patch tools/local-checks/isolated-memory.patch.yaml` 之外增加 `--patch profiles/bundles/modern-room-kicking.patch.yaml`。使用 `all` 部署时，将补丁中的 `set` 操作复制到各目标版本被忽略的 `local.patch.yaml`，行 ID 使用不带前缀的 `dialect.rooms`。真实客户端验收前保持关闭。此选项不改变旧版现有的踢人绑定。

## 带类型的快速匹配

对于已审计此能力的构建，`dialect.rooms.config.quick_matching` 挂载原生匹配请求。`quick_match_default_mode_id` 选择共享匹配模式（0–65535，默认 0 拒绝请求）。类型 1 开始匹配，类型 2 刷新匹配；各自不同的原生尾部均完整校验。组队加入和列表条目请求在流程实现前返回原生拒绝。随附现代版本配置保持关闭，等待真实客户端验收。

## 原生选服登录衔接

可选项 `dialect.messages.config.center_migration_prelogin` 默认为 `false`，且要求同时启用普通登录。对 kr-1256，同时配置 `protocol.config.pre_handshake_opcodes: [gcpReqMoveInto]`，才能在补丁请求之前接收全新的原生选服连接。适配器严格校验双字字段长度、零保留身份及值为 0 或 1 的阶段标志，拒绝已登录或重复请求，并发送原生补丁回复以继续普通登录。仅在当前报文分发期间可用的 `IInitialHandshakeContinuation` 在分发结束时失效，且仅在处理器成功返回后生效。它不会标记版本已验证、兑换票据、安装玩家身份或授权迁移。大厅仍须要求有效的一次性 Launcher 票据，账号名不能替代票据。单服默认配置保持不变。服务端报文测试通过不代表真实客户端已验收。

`dialect.messages.native_migration` 默认关闭。它与 `center_migration_prelogin` 共用原生移动请求的单一路由：零编号仍要求显式启用预登录选项，非零编号分发为 `MoveIntoRequested`。分发本身不解除首包握手限制，也不授予账号占用。此选项还提供由数据表校验的迁入回复与连接切换消息，只有 opcode 表声明了确认及完成报文时才挂载这对消息。目标准入处理器必须先校验绑定监听器的待迁入记录、认领账号权限并安装玩家状态，然后才能请求握手续接。目标准入由下述可选 `cluster.migration-admission` 行提供；来源编排由 `cluster.migration-source` 提供，部署接线仍需显式启用。


可选的 `cluster.migration-admission` 行在目标预留完成后协调共享账号入房迁移，应放在能看到对应版本大厅和房间服务的位置。启用 `lobby.native_migration`（默认 false）与 `dialect.messages.native_migration`；大厅还要求账号所有权、持久化保留身份查询、存储型背包与钱包，且不允许角色创建预览。独立协调插件避免大厅与房间服务形成循环依赖。Contracts 4.16.0 新增 `IRetainedPlayerAdmission`；安装结果是原生玩家状态，远程 SDK 尚无适配器。

`channels` 必须显式列出已证明适用于该版本的原生频道值，来源频道不在列表中时拒绝迁入。kr-812 的 50、60、80 才会通过 kr-812 sub_A4A440 的无条件分支，其余命名频道依赖客户端权限。不能直接把现代版登录频道用于旧版。`max_migrations` 默认 64（1..4096），`timeout_ms` 默认 10000（100..60000），`cleanup_interval_ms` 默认 1000（100..10000）。等待确认与清理失败共用容量；过期、断线或租约变化会释放预留和账号占用，清理失败保留容量等待重试，卸载报告未完成的清理。

准入消费监听器和地址绑定的一次性记录，检查目标租约，拒绝超出玩家模型正有符号整数范围的身份，并在读取保留身份、解析背包和钱包前认领精确的账号交接。不会创建身份或重复发放每日登录奖励。只有玩家状态安装且连接提升发送成功后才继续初始握手。需要提升确认的版本保持认证阶段，收到确认后完成提升并走普通房间准入；其他版本在立即提升后入房。返服迁入使用下述可选主菜单准入，访客仍被拒绝。访客和部署接线尚未完成；发布的配置未挂载此行，仍需真实客户端验收。

可选的 `cluster.migration-source` 行把重新验证后的远程房间点击接到原生迁出流程。它要求迁移代理、节点租约、账号会话交接、在线玩家目录，以及与 `client_build` 匹配的唯一 `ChannelMoveOffered` 发送器。必须显式设置 `channel`（1..255），且目标须允许该频道。`max_migrations` 默认 64（1..4096），`operation_timeout_ms` 与 `migration_timeout_ms` 默认 10000（100..60000），`cleanup_interval_ms` 默认 1000（100..10000）。发布配置没有挂载此行。

只有当前持有登录的大菜单或广场游戏连接能够迁出。房间流程持有场景锁；挂载的队伍目录必须支持原子单人预留：撤销既有邀请，并在清理前拒绝涉及该玩家的新邀请和入队。单人预留以已配置的待处理邀请容量为上限。准备前玩家进入 `Migrating` 阶段，目标先预留精确房间实例，来源再释放账号写权限，最后发送数据表定义的原生转服通知。超时、回包丢失、发送失败或连接失效时，取消精确目标请求并仅撤回本次交接；目标已经认领时关闭来源连接，不重新普通抢占账号。清理失败保留容量和单人预留以便重试。Contracts 4.17.0 新增来源接口和原生通知；需要生命周期所有权的队伍预留尚无远程 SDK 适配器。

共享账号迁移在连续访问多个房间时保留首次登录节点和原生频道（Contracts 4.18.0）。来源插件还要求 `IMigrationTargetDirectory`，并在 `home_unavailable_notice_enabled` 为 true 时要求唯一的原生 `ServerNotice` 发送器。代理通过认证查询重新解析原服租约，复核就绪状态、容量、加入范围、持久域和账号签发方；查询本身不授予待迁入记录或账号权限。原服拒绝或不可用时，玩家留在当前节点，并在启用通知时收到 `home_unavailable_notice`（1..512 字符）。

InGame 的 `return_on_leave` 默认 `home`，设为 `stay` 则留在访问节点。`return_delay_ms` 默认 1500（0..60000）。先完成正常离房清理和对局离房结算，再等待返服。等待期间不占用场景锁；断线、连接被替换或任何场景代数变化都会取消旧返服请求，之后可通过退出广场重试。原服的 InGame 须启用 `home_migration_admission: true`，同时要求 `room_admission_reservations` 和原生主菜单发送器。返服准备不预留房间席位。完成同样的账号认领和连接提升后，目标使用保留装备恢复来源节点的空闲场景，不重复提示教程。返服请求携带 `home_scene`（`MainMenu` 或 `Plaza`），入房请求始终使用 `MainMenu`。从广场返服时复用原生广场安装流程，避免额外跳回主菜单；未提供广场编码器的版本退回主菜单。Contracts 4.21.0 为 `MigrationRequest` 和 `IMigrationHomeAdmission.EnterAsync` 增加场景参数。挂载队伍目录时，目标在等待提升确认及场景安装期间持有原子单人预留；清理失败则继续保留，直到资源释放。发布配置仍保持关闭，等待部署与真实客户端验收。

队长会带着队伍一起移动。队长前往远端房间或返回原服时，同一版本、位于主菜单或广场的每名在线队员会在队长的迁移邀请发出后立即收到同一目的地的邀请：两个客户端系列都会在收到任何已接受的迁移邀请时打开待定连接，无需自己先请求。队员迁移不使用单人预留；返服只带走原服与队长相同的队员。在房间或对局中的队员，或被目的地拒绝的队员（例如房间已满），留在原节点并保留本地队伍；队长的迁移不会撤销。非队长队员不能单独迁出，也不会自行发起返服。每个请求在 `party_leader` 中写明队长，单人迁移时节点传输省略该字段；目的地通过 `IPartyDirectory.RejoinAsync` 把每名到达者重新加入已到达队长的队伍，到达顺序不限，未匹配的到达者在其到达时限过后丢弃。Contracts 4.23.0 增加这两项。尚未经原生客户端确认。

目录的 `nodes[].native_migration_builds` 默认是空列表。仅列出已部署原生迁出、监听器绑定目标准入和连接提升处理器，并已核对客户端路径的版本；条目必须唯一且对应已配置的端点。目录启用 `migrations: true` 后，两端都列出该版本，才会把共享账号远端房间标为可加入或接受迁移请求，返服同样受此检查。加入提示还要求两端租约有效、版本就绪、目标有容量、加入范围和房间目录相符、持久数据库身份及账号签发方完全一致。列表不授予席位预留或账号所有权，准备阶段仍会重新校验路径；访客准入完成前，独立数据域只提供发现。

预留房间在玩家迁入前关闭、开局或更改密码时，准入协调器会释放席位并安装保留玩家状态的主菜单，同时继续持有目标节点的账号写入权。`cluster.migration-admission` 要求每个版本恰有一个 `IMigrationRoomRecovery` 服务；InGame 在 `home_migration_admission: true` 时发布此服务。恢复操作等到原握手消息处理结束后，再应用 `return_on_leave` 和 `return_delay_ms`。期间切换场景会取消返回，`stay` 则保留玩家在目标节点。恢复任务占用 `max_migrations` 容量，在准入锁外运行。如果主菜单安装失败，仍会关闭连接并释放已认领的资源。Contracts 4.19.0 新增此恢复服务，原生主菜单消息格式不变。

房间服务发布 `IRemoteRoomMigrationEndpoint`（Contracts 4.20.0），`cluster.migration-source` 为其 `client_build` 绑定恰好一个端点。房间发布、节点心跳和迁移因此能一起启动：房间服务不再等待本身依赖心跳的迁移源。重复迁移源或缺少匹配房间端点会导致激活失败。卸载迁移源时拒绝新的远端进房，并等待正在执行的进房和回原服调用结束后再释放；重载迁移源或心跳不会重载房间服务。无需增加配置行，也不会默认启用迁移。

旧版房间适配器分别控制连接就绪与自动教学。`dialect.rooms.config.tutorial_move_in` 启用原生主菜单移入应答；`automatic_tutorial`（默认 `true`）独立控制随后发送的自动教学通知。需要跳过首次教学的测试部署可设置 `automatic_tutorial: false`，同时保留房间操作所需的就绪应答。单次登录的 `OfferTutorial: false` 同样只抑制教学通知。

## 可选房间物件经济

现代版房间方言与旧版复用相同的可选物件经济绑定：`supply_items`、`lucci_drops`、`star_items`、`horde_lottery`、`instant_revival`、`pve_points`、`rescue_missions`、`counted_pve_items`。每项绑定仍要求自身已确认的包能力，不支持的选项会在激活时失败。kr-1256、cn-774、tw-454 配置启用补给、合作模式计数道具及 PvE 实时计分。模式 3 使用自身启用的道具池和两个普通持有槽。模式 16 初始化三个计数槽；槽内道具选择及其余即时生效的合作模式拾取道具，均为来自各版本自身已启用 ItemSpec 数据的可配置服务端策略。现代版地图上报有十四个向量，队伍元素为单字节；生成、丢弃消息分别为 37、33 字节。KR 切槽回复有三个字段，CN、TW 额外携带第四个结果字段。原生客户端验收仍待完成。


`dialect.rooms.config.weapon_objects` 默认为 `false`。仅在版本声明已核对的 `weapon_objects` 能力时挂载原生武器掉落创建和拾取，目前为 cn-324。共用 `ingame` 行通过 `max_weapon_objects_per_round` 限制每局累计创建数量（默认 256，范围 1..65536），对象标识从 `weapon_object_id_base` 之后分配（默认 `0x64000000`）。该区间应与玩家及其他对局对象标识分开。拾取仅接受上报玩家自身角色，同一对象只能消耗一次；对局重置清除状态。自动化检查不等于实机验收。

### 客户端显示等级

每个客户端版本在 `packets/builds.yaml` 中声明 `display_level_max`，数值来自该版本自身 `Images/Icons/LevelMark.smf` 的图标帧数。发送玩家状态、队伍成员、广场／房间座位、玩家资料、等待列表和结算时，超过上限的等级显示为该版本的最高等级。数据库等级、经验和奖励计算保留真实值。例如，194 级在 cn-324、cn-406 显示为 180 级，在 kr-812 仍显示为 194 级。新增版本必须声明其自身资源支持的正整数上限。

### 早期临时房间装备

`dialect.rooms.config.room_inventory_selection` 默认为 `false`，要求同名封包版本能力。CN2011、CN2012 的配置启用此项，并将房间行放在各自隔离的 `clientdata` 行之下。它将早期 `gcpRoomChangeInventory` / `gspRoomChangeInventory` 路径与 `weapon_presets` 分开挂载。CN2011 通过 `room_inventory_selection_count` 声明四个选择值，CN2012 声明五个；`room_inventory_selection_fields` 指定 `UserSlot` 中原生角色与武器短整数的字段。绑定要求该版本的客户端数据，依据 `Config/FighterConfig.xml` 与 `Config/WeaponSpecDetail.xml` 校验角色和武器规格标识，拒绝未知值，尚未确认用途的尾部选择值只接受空值。玩家只能修改自己尚未准备的等待房间席位。房间快照保留该选择，不发放拥有的道具，普通换装或退出房间后清除。原生按钮可见性是另一项问题：CN2012 初始化时隐藏旧按钮，因此服务端挂载此路径不等于已经具备可用的原生入口或通过实机验收。

### 队长对局

`dialect.rooms.config.captain_mode` 默认 `false`，需要该版本独立审核的 `captain_mode` 能力。支持 kr-812、cn-717、id-400、id-1809、cn-404、cn-408、cn-706、kr-1256、cn-774 和 tw-454，使用完整的 legacy 或 modern 对局适配器；仅菜单、浏览器、广场及等待房间适配器拒绝此选项。 模式 20 在对局开始后为每队随机指定一名在线队长，变身、武器和技能由客户端处理。队长本人的死亡上报会把身份交给存活队友；无人可选时等待共用复活流程。普通击杀与队长击杀共用同一次死亡计分，击杀队长只增加一个普通团队分，沿用共用时限、团队总分、平局、结算及奖励规则。队长离开时会在有存活队友后替换。此选项不发送 `gspCaptainEvent`，不授权未经审核的版本，也不代表已通过原生客户端验收。 `ingame.config.captain_ultra_item_kind`（legacy 套件为 104，0 关闭）让当前队长的 ULTRA 请求只回给队长本人，之后由客户端自己把 ULTRA 显示给其他玩家；其他玩家和已倒下队长的请求不会得到回应，也不会重复任命。

### 蕾雅角色技能

`dialect.rooms.config.rhea_character_skill` 默认为 `false`。应用对应版本的 `rhea-character-skill.patch.yaml`，可为 kr-1256、cn-774 或 tw-454 启用已核实的变身协议，需要完整的现代对局适配器及该版本的原生能力。只有当前穿戴角色物品 300029 的玩家，才能为当前回合、当前生命中的自身角色请求变身。接受的变化同步给本人和房内玩家；重复激活会被忽略，已结束状态的重复结束请求仅向本人确认。死亡、复活和转化会清除已存变身状态。充能及结束时机仍由原生客户端控制；此选项不增加服务端计时强制校验，也不代表已通过原生客户端验收。

### 爆破对局

`dialect.rooms.config.bomb_mission` 启用已审核的原生炸弹操作与结果绑定。`ingame.config.bomb_round_series` 进一步启用连续回合，逐局交换攻守，将胜局数与击杀总分分别记录。legacy 格式为三局两胜；kr-1256、cn-774 和 tw-454 为五局三胜。两项默认均为 `false`，完整系列支持 kr-812、cn-717、id-400、id-1809、cn-404、cn-408、cn-706、kr-1256、cn-774 和 tw-454，需要共用结果接口及已审核的连续回合入场发送器。应用对应的 `profiles/<build>/bomb.patch.yaml` 可启用两项并从普通复活流程中排除模式 19，要求具备观察模式支持。modern 显式启用补丁同时将模式 19、20 加入记分板选择。CN2013 cn-406 的模式工厂没有这两个专用分支，因此保持排除；该结论不代表其他 CN2013 修订。

`dialect.rooms.config.extreme_racing` 默认 `false`，需要已审核的 `extreme_racing` 版本能力（目前仅 kr-1256），并需要 `modern_rooms` 及其对局绑定；cn-774 和 tw-454 已有经过审核的计时、回合、记录、终点、金币与检查点表行，但竞速能力仍关闭并拒绝此选项，等待完整对局审核。应用 `profiles/kr-1256/extreme-racing.patch.yaml` 即可启用。模式 24 随后进行 `ingame.config.extreme_racing_rounds` 个竞速回合（1 至 5，默认 3）。每回合开始时启动竞速计时（`extreme_racing_round_ms`，默认 180000）。首位到达终点后启动终点倒计时（`extreme_racing_finish_countdown_ms`，默认 10000）；倒计时或竞速计时结束，或所有在线玩家都完成时，该回合结束。完成者按名次获得 `extreme_racing_place_points` 分数（默认 10、8、6、5、4、3、2、1），竞速记录板显示本回合与累计分数，回合之间暂停 `extreme_racing_intermission_ms`（默认 5000，须大于 1000）。最后一回合后执行一次共用结算：分数较高的队伍获胜；房间不是两队时，分数最高的玩家获胜。共用对局时限不会结束竞速。检查点触发会被转发，使每名玩家的进度标记前进；标记和复活点由客户端自行布置。需要服务器对象列表的地图对象（炮台、金币和石块）不会生成。回合数、计时与分数属于服务器规则，原生客户端验收尚未完成。

服务端依据具体操作人、队伍与当前回合校验安装、取消和拆除。原生房主客户端上报爆炸与目标结果；普通回合时间到期后，已安装炸弹保留自身倒计时。各局沿用共用加载、角色生命与取消流程，整场只结算一次奖励，并保留同一场次标识。一整队主动退出会判该队整场弃权，不增加未进行的胜局；现有掉线取消策略保持生效。此选项不启用未经审核的版本，也不代表已通过原生客户端验收。

### FinalZone

`dialect.rooms.config.final_zone` 默认 `false`。应用 `profiles/kr-1256/final-zone.patch.yaml` 可启用模式 23、25、30、45 共用的 Area32 交互，需要 modern 对局绑定、`final_zone` 版本能力和记分板模式 30，目前仅审核 kr-1256。补丁同时保留记分板模式 1、19、20、24，可以在其他模式补丁之后应用，但不会自行开启这些模式。

通过 `client-data` 导入对应客户端的 `Config/GameSpec/GenItemSetting.yaml`、`Config/GenDroneSpec.csv`、`Config/SurvivalMode.xml`、`ServerShared/MapMeta.xml`，以及所选地图的 `GenClusterTable.csv`、`GenSpawnTable.csv`、`GenSpotList.csv`。使用无人机或金色补给的地图还需要 `GenDroneRewardTable.csv`。公寓地图关闭了无人机，原始资源也没有这张表。资源缺失或格式错误会在倒计时前报错。当前显式物品生成路径支持普通生成物，包括 `rel_Sudden08_Finalzone` 地图；使用独立 `AutoItem` 地形物件类别的地图（包括 `rel_space04_64room`）会被拒绝。可选开局包的原生消耗与发放交互尚未接入，因此装备开局包也会在倒计时前被拒绝。

服务器在对局开始后发布地面物品，每个物件只授予一名领取者，依据已发放数量校验主动丢弃和一次死亡背包，并使用原生用户编号转移背包内容。洪水阶段遵循地图资源时序。无人机使用受限的房间计时器，补给箱与房主去重后的金色补给申请采用原生奖励种子。陷阱每个端点消耗一根已持有的杆，只允许放置者连接已确认的两个端点；必要确认包含发送者。玩家淘汰后不能复活，存活人数只更新一次，有竞争者的对局在只剩一人时进行一次结算，模式 25 则按最后存活的队伍结算。模式 45 接入共用物资和淘汰流程，独有的僵尸机制另行实现。普通分数和时限不会结束 FinalZone。取消、房间销毁与最终结算都会停止模式计时器。

资源与物件限制位于 `ingame.config`：

| 配置键 | 默认值 | 限制 |
|---|---:|---|
| `final_zone_first_object_id` | 65536 | 首个预留世界编号，整个范围必须低于玩家编号。 |
| `final_zone_maximum_objects` | 16384 | 每局不复用的编号总量，由物品、背包、无人机、补给和陷阱杆共用。 |
| `final_zone_maximum_quantity` | 100000 | 每个物品规格可发放或保留的最大数量。 |
| `final_zone_maximum_drones` | 1 | 同时活动的无人机数。 |
| `final_zone_maximum_golden_supplies` | 3 | 每局不同金色补给申请数，零表示不发放。 |
| `final_zone_maximum_file_bytes` | 4194304 | 每个导入资源的字节数。 |
| `final_zone_maximum_rows` | 16384 | 每张地图表的行数。 |
| `final_zone_maximum_item_specs` | 4096 | 物品规格与开局包标识数量。 |
| `final_zone_maximum_alternatives` | 256 | 单个生成字段中的候选数。 |
| `final_zone_maximum_bonus_items` | 64 | 单次生成选择附带的物件数。 |

此功能仍是受开关保护的增量。台账限制已发放数量，但尚未完整复现原生背包槽位、武器变体与实时弹药消耗。模式 30 的同盟与背刺行为尚未得到证实。自动检查和资源导入不代表原生客户端验收，也不代表地图机制全部覆盖。

## 原生广场招募

支持的十三个配置已启用原生广场招募。自定义配置需在 `dialect.rooms` 和 `ingame` 两行设置 `plaza_recruitment: true`，并挂载对应的 `party` 行。两个选项默认均为 `false`；缺少原生绑定、队伍目录或广场支持时，激活会失败。`ingame.plaza_recruitment_text_length` 默认限制为 128 个 UTF-16 字符，可设置 1–1024。发布和修改共用聊天限流窗口，取消招募不受该窗口阻止。

只有当前广场队长能招募。原生木牌标志、文字气泡、队伍成员与场景跟随共同更新；取消、离开、切换场景和失去队长身份会清除旧木牌。KR1256 和 CN2019 还提供每页十条的原生招募列表，使用队长选定的分类和人数上限。列表只允许加入本节点队长的队伍；显示远端玩家的广场外观不代表可以跨节点入队。入场位置由原生客户端决定，服务端只重放真实的位置报告。

## 可选的装备防御表

装备普通防御默认关闭。在 `kr-812`、`cn-324`、`cn-406`、`cn-404`、`cn-408`、`cn-705`、`cn-706`、`id-400`、`id-1809`、`cn-717`、`cn-774`、`tw-454` 或 `kr-1256` 上添加 `--patch profiles/bundles/defence-seeds.patch.yaml` 可启用。补丁也会启用发放防御表所需的原生 TCP relay，包括 `cn-705`、`cn-706` 默认停用的相关插件行，无需开启 UDP。使用 `all` 时，将补丁操作放入各选定版本的 `local.patch.yaml`；共用补丁无法修改稍后追加的版本行。`cn-705` 可作为单独配置使用，但不在 `all` 中。缺少已核实能力或授权行的版本会拒绝此补丁。

CN2011（`cn-279`）在原生伤害路径中本地计算普通装备防御，不使用此种子表补丁。

授权插件读取对应版本的 `Config/ShopItem2.xml` 和 `defence_seed` 规则。回合开始时冻结穿戴物品 ID，并向每个接收端发送每名受击者相同的 1000 字节表，减伤仍由原生客户端应用。暴击与抵消表保持独立；启用 `cn-705` 或 `cn-706` 的防御不会启用尚未核实的暴击交换。关闭时保留原来的零防御交换，仅防御插件行保持停用。

表内频数均衡，近似本地整数随机取值分布，不复现客户端随机序列。目前投影涵盖目录中的 min/max、配置的 RC/KR/BB 套装效果和端点精度；`cn-774` 与 `tw-454` 均采用各自的 RC/KR/BB 规则和单精度端点。暂不投影实例属性、变身专属属性及独立特殊修正。CN2019 的 AI 角色使用不同的种子编码，不在这套玩家投影范围内。线上启用前需要原生客户端验收。`defence_items_client_path` 选择工作表文件；`defence_workbook_max_bytes` 限制文件大小，默认 268435456 字节。

## 新版僵尸模式

对 `kr-1256`、`cn-774` 和 `tw-454`，应用 `profiles/<build>/script-zombies.patch.yaml`，同时在 `dialect.rooms` 与 `ingame` 行启用 `script_zombies`，接入普通模式 8。两项默认关闭，等待原生客户端验收。原生房主脚本按装备权重选择多个小僵尸，服务端将初始数量限制为在线人数除以 4 后向上取整，再由角色所属客户端的原生确认完成队伍 34 变身。人类开局为队伍 1。之后的感染与僵尸复活须有角色所有者的死亡或复活依据；普通死亡上报不会结束回合。所有感染确认完成后僵尸获胜，到服务端时限仍有人类存活则人类获胜。

`ingame.script_zombie_round_time_limit_ms` 默认为 `180000`，范围为 `1..3600000`。模式 8 必须启用原生生命与复活，`dialect.rooms.result_scoreboard_modes` 必须包含 `8`。两行的 `script_zombies` 必须一致。此可选补丁可与已审核的炸弹、队长、跑酷和 FinalZone 补丁叠加，保留各自记分板。其他模式保持版本声明的 peer feed 策略。这三个版本均关闭通用转发，因此共用变身操作不会在已启用模式之外被转发。原生脚本房主离开时会取消回合且不结算，因为选择状态尚不能转交给另一客户端。

本次接入为单回合，脚本的十回合请求尚未组成服务端连续比赛。幽灵模式 28、原版模式 43、僵尸生存模式 44 各有不同原生规则，不由此选项启用。原生客户端实机验收仍待完成。

## 幽灵僵尸

为 `kr-1256` 或 `cn-774` 应用 `profiles/<build>/ghost-zombies.patch.yaml`，即可在房间适配器与对局行同时启用 `ghost_zombies`。模式 28 与普通模式 8、逃生模式 32 共用转换绑定。原生房主选择一名僵尸王（201 或 203），使用武器 110290；脚本随后重复的零武器选择不会再次授予转换。已确认僵尸王的死亡清理使存活人类获胜；僵尸王或房主离开则取消对局，不发奖励。其他人类经自身死亡／复活流程变为小僵尸。

普通与幽灵僵尸还会同步当前角色所有者发出的原生 ULTRA 激活与计时结束恢复，保留共享 HP 和生命代次。充能仍由原生计量槽负责。当前未实现脚本的十回合系列。TW454 没有完整幽灵脚本路径的核实证据，因此拒绝启用。补丁保留其他已启用记分板，原生客户端实机验收仍待完成。

## 僵尸逃生

`dialect.rooms.config.zombie_escape` / `ingame.config.zombie_escape` 默认 `false`。kr-1256、cn-774 或 tw-454 的单版本配置可应用 `profiles/bundles/zombie-escape.patch.yaml`；使用 `all` 时，将操作放入各选定版本被忽略的 `local.patch.yaml`。启用需要现代对局绑定、该版本已审核的 `zombie_escape` 能力和记分板模式 32。补丁保留记分板模式 1、8、19、20、23、24、25、30、32、45，但不会自行开启各模式的独立开关。请使用对应客户端的僵尸逃生地图和脚本，原生客户端验收尚未完成。

房间方言与对局插件必须启用相同的僵尸模式。普通僵尸和僵尸逃离共用一个类型化变身绑定，同时启用不会重复注册原生操作。可选补丁保留已支持的所有生存模式结算表；返回等待房间时恢复开局前选择的队伍。

客户端房主脚本负责初始僵尸抽签、分离的僵尸出生点、准备门、检查点按钮、定时门、感染条与撤离条件。服务器校验房主有上限的初始选择，允许每名玩家自身的后续感染或复活变身，向全体参与者回传获准的 peer 变身，并使用共用生命、结果与奖励结算流程。初始名额为当前在线玩家人数除以五后向上取整。脚本可通过感染条直接感染仍存活的玩家；按钮扫描没有证明仅限人类触发。

房主通过原生关卡结果报告人类撤离。没有在线人类剩余，或服务器时限届满时，僵尸获胜。`ingame.config.zombie_escape_round_time_limit_ms` 默认 600000，范围为 1..3600000。普通死亡关卡报告不会结束此模式。对局中拒绝新玩家加入。Lua 房主离开会取消本局且不发奖励，因为无法重放其私有计时和检查点状态；其他掉线遵循已配置的取消策略。

此开关通过共用结果流程结算一个关卡。原生脚本写入游戏次数三，但三关系列语义尚未证实，本次未实现。自动测试不代表原生地图机制与界面行为已全部验收。

## 按客户端版本保存穿戴方案

`inventory.loadouts` 行让每个版本独立保存已穿戴角色、服装、武器、武器预设页及当前预设槽位。`build_key` 使用稳定的配置身份，例如 `kr-1256` 或 `cn-279`；运行同一版本的各节点使用相同键。账号的道具所有权、数量、期限、钱包和进度继续共享。客户端库存视图只显示自身 `ShopItem2.xml` 中存在的物品，角色还会核对 `FighterConfig.xml`。不兼容物品仍归账号所有，切回支持它的版本后可再次看到。

首次使用时，系统从旧记录继承兼容的选择，并通过既有穿戴规则补齐必需装备位置。后续修改只影响当前版本。预设初始化不会覆盖已经保存的预设页。该行位于 `clientdata` 下，隔离 `IInventoryRepository` 与 `IPlayerInventoryState`，通过共享存储提供的 `IInventoryLoadoutStore` 操作数据。自定义配置应让大厅、装备视图、装备、商店、房间协议和游戏逻辑消费者位于此子树中。

Memory、MySQL 和 SQLite 提供者均有 `max_loadouts_per_player`，默认 `32`，范围 `1..256`。达到上限后拒绝新增版本方案，不驱逐已有方案。MySQL 迁移 `0038` 新增 `player_loadouts`；SQLite 模式 `8` 增加方案快照并保留旧账号状态。升级前备份持久存储。回退 MySQL 程序时保留新增表；旧 SQLite 读取器会拒绝模式 `8`，回退需要兼容读取器。不要用旧存档覆盖玩家的新进度。
