---
title: "启动器"
description: "Bubble Fighter Fantasy 的配套启动器：客户端版本、会话、登录票据与服务端启动器 API。"
updated: 2026-10-23
---

Bubble Fighter Fantasy 的配套桌面应用 **Fantasy Tale Launcher** 正在开发中，目标是安装并启动游戏客户端，并把它们连接到玩家选择的服务器。它是一个独立项目：[FantasyTale.Launcher](https://github.com/Wasabi-FantasyTaleDev/FantasyTale.Launcher)，以 MIT 许可证分发。本服务器仓库不包含它，两个项目都不分发游戏客户端或其数据。

本页记录启动器在当前开发检查点的状态，以及运营者已经可以启用的服务端启动器 API。启动器处于**早期开发阶段**：大多数面向玩家的流程仍以定义与契约的形式存在，而不是可用的界面。下文每一节都说明哪些已实现、哪些已计划，以免玩家或运营者依赖在此提交中并不存在的行为。

## 当前开发状态

| 领域 | 当前检查点的状态 |
| --- | --- |
| 窗口、游戏目录与设置外壳 | 已实现：宿主从 `games` 文件夹加载游戏与客户端版本定义并发送给界面；外壳渲染游戏、实例、服务器、下载、插件、账号与设置页面，并在本地保存外观与语言偏好。 |
| Bubble Fighter 版本定义 | 已实现为数据：共定义九个客户端版本，每个版本各自一个版本文件。 |
| 会话文件（`session.toml`） | 写入器已作为库辅助类实现；尚无生产启动流程调用它。 |
| 原生 hook 运行时与 messenger 宿主 | 已实现并由原生测试覆盖，但**尚未在真实游戏客户端中验证**。 |
| 三个内置 hook 插件 | 已定义为数据插件，并由同一批原生测试覆盖；同样尚未在真实游戏客户端中验证。 |
| 后端下载、下载源、插件管理器、实例 | 未实现：下载引擎、清单应用、插件管理器与实例模型仍是待办的路线图工作。一种下载源类型的解析器已经存在。 |
| 面向玩家的列表与操作 | 实例、服务器、下载、插件与账号页面目前渲染占位数据；在宿主桥接方法就位之前，界面明确是预览版本。目前没有可用的“创建实例”“添加服务器”“登录”或“启动”操作。 |
| 服务端启动器 API（位于本仓库） | 已实现且运营者可启用：账号、会话、票据、目录、客户端预设、版本门禁与插件推荐。 |

不要期望当前检出的启动器已经能够安装或启动游戏客户端。下文各节依据源码编写，以便在其余流程就绪后本指南仍然正确。

## 获取启动器

启动器是一个独立项目，拥有自己的仓库和发布流程：

- 仓库：[FantasyTale.Launcher](https://github.com/Wasabi-FantasyTaleDev/FantasyTale.Launcher)。
- 许可证：MIT。
- 状态：早期开发阶段，如其 README 所述。

该仓库说明如何从源码构建（构建 .NET 解决方案、用 Visual Studio 构建 x86 原生 hook 宿主，以及用 npm 构建前端）。它没有说明已发布的安装程序、发行文件或下载页面，因此本指南也不提供这些内容。请等待启动器正式发布，而不要把开发者源码构建当作玩家安装。

## 客户端版本

客户端版本的标识为 `<region>-<wire version>`，其中 wire version 是客户端的 `ClientVersion_Major`。它**不是**发行年份，也不是可执行文件的版本。当前文档记录的服务器 profile 与客户端版本如下：

| 版本 | 分支 | 显示名称 | 客户端日期 | 客户端版本 | 状态 |
| --- | --- | --- | --- | --- | --- |
| `kr-1256` | 韩国 | KR 2026 | 2026-05-08 | 1256 | 实验性 |
| `kr-812` | 韩国 | KR 2015 | 2015-04-22 | 812 | 已支持 |
| `cn-279` | 中国 | CN 2011 | 2011-04-08 | 279 | 已支持 |
| `cn-324` | 中国 | CN 2012 | 2012-09-23 | 324 | 已支持 |
| `cn-406` | 中国 | CN 2013 | 2013-06-28 | 406 | 开发中的早期阶段 |
| `id-400` | 印尼 | ID 2014 | 2014-01-23 | 400 | 已支持 |
| `cn-706` | 中国 | CN 2014 | 2014-09-23 | 706 | 已支持 |
| `id-1809` | 印尼 | ID 2015 | 2015-01-29 | 1809 | 已支持 |
| `cn-717` | 中国 | CN 2015 | 2015-06-18 | 717 | 已支持 |
| `cn-735` | 中国 | CN 2016 | 2016-06-20 | 735 | 准备中 |
| `tw-454` | 台湾 | TW 2019 | 2018-12-11 | 454 | 实验性 |
| `cn-774` | 中国 | CN 2019 | 2019-05-30 | 774 | 实验性 |
可执行文件方面，印尼版本（`id-400` 和 `id-1809`）使用 `CrazyShooter.exe`；其他列出的版本使用 `BubbleFighter.exe`。

启动器目前只随附可以实际启动的版本定义；上表中的其他服务器 profile 用于记录兼容性规划，在补充并验证对应的启动器定义之前不会自动启动。

### messenger 与登录模式

每个版本还声明客户端的 messenger 调用由谁应答，以及客户端如何登录：

| 版本 | messenger 模式 | 登录模式 | 默认端口 |
| --- | --- | --- | --- |
| `kr-1256` | forward | hook | 7100 |
| `kr-812` | stand-in | nmco | 7110 |
| `cn-279` | forward | gamewidget | 7210 |
| `cn-324` | forward | gamewidget | 7140 |
| `cn-404` | forward | gamewidget-ticket | 7240 |
| `cn-406` | forward | gamewidget-ticket | 7230 |
| `tw-454` | stand-in | nmco | 7200 |
| `cn-706` | forward | unverified | — |
| `cn-717` | forward | unverified | — |
| `cn-735` | forward | unverified | — |
| `cn-774` | forward | gamewidget-ticket | 7190 |
| `id-400` | stand-in | nmco | 7120 |
| `id-1809` | stand-in | nmco | 7130 |

关于这些表格的说明：

- 分支列是游戏定义中的区域分组。下面的 messenger 表只列出已有启动器定义的版本；客户端表中的其他 profile 目前还不能通过启动器启动。
- “messenger 模式”和“登录模式”是定义字段。**替身（stand-in）**表示启动器的 messenger 宿主自行应答客户端的 messenger 调用；**转发（forward）**表示把这些调用传给原始函数库，安装内容会将该库保存为改名后的文件。参见[登录与身份验证](#登录与身份验证)。
- 标记为 `unverified` 的三个版本尚无经过验证的登录提供程序或启动参数；启动器模块会拒绝为它们构建启动计划，并报告该版本没有经过验证的启动参数。它们在定义中列出，但在当前检查点无法启动。
- 没有默认端口的版本需要一个能提供端口的已选服务器端点。
- 版本的显示名称与年份是展示数据。它们不参与身份识别：服务器 profile 与 packet 表使用的是 id（`<region>-<wire version>`）。
- 启动器目前向界面发送每个版本的 id、分支、年份和本地化名称。可执行文件、messenger 模式、登录模式、指纹与默认端口是启动和会话契约使用的定义数据，不是玩家编辑的数据。

## 计划的玩家流程

启动器的设计描述了以下流程。由于界面尚未连接到真实数据，这里列出每个步骤及其在当前检查点的状态。

| 步骤 | 仓库定义的内容 | 状态 |
| --- | --- | --- |
| 1. 启动启动器 | 一个原生窗口，游戏目录从 `games` 文件夹加载。 | 已实现 |
| 2. 选择 Bubble Fighter | Bubble Fighter 是唯一已启用的游戏；其他游戏条目会是无法选择的占位项。 | 已实现 |
| 3. 创建客户端实例 | 实例是一份已安装的客户端副本，拥有自己的文件夹、服务器绑定、插件集与选项，因此同一版本的多个副本可以共存。 | 计划中；实例页面目前使用占位行 |
| 4. 选择版本 | 版本是实例的属性。每个版本定义提供可执行文件、启动参数、messenger 模式与默认插件。 | 计划中；定义已存在，尚无版本选择器 |
| 5. 添加服务器 | 服务器通过其启动器 API 基础 URL 添加；没有该 API 的服务器计划支持按地址手动添加，但没有经过验证的登录，也没有自动安装。 | 计划中；服务器页面目前使用占位行 |
| 6. 登录服务器 | 服务端账号注册、登录、刷新与登出已经存在（参见[服务端启动器 API](#服务端启动器-api)）。 | 服务端已实现；启动器客户端侧为计划中 |
| 7. 安装或更新客户端 | 服务器计划为每个版本发布带文件大小与哈希的分发清单；启动器会验证每个文件，并按文件应用更新。 | 计划中；尚无下载引擎或更新检查 |
| 8. 启动 | 启动器把 `session.toml` 写入客户端文件夹，以实例文件夹为工作目录启动可执行文件，并在客户端退出时删除会话文件。 | 契约与写入器已实现；界面尚无启动操作 |

在这些流程就绪之前，启动器可用于开发和测试定义、会话契约、原生 hook 宿主以及服务端 API——而不是作为玩家安装使用。

## 实例与启动

尽管界面尚不能启动客户端，设计与已实现的代码仍然定义了一次启动的形态：

- **实例**是位于自己文件夹中的一份客户端副本。它携带一个 `instance.toml`，其中记录版本、服务器绑定、启用的插件与选项；实例可以复制，因此同一版本可以服务于不同的服务器或插件集。
- 版本定义指定的可执行文件必须解析到实例文件夹内；逃逸出该文件夹的路径会被拒绝。
- 启动参数来自版本定义，是一个带 `{account}`、`{host}` 和 `{port}` 占位符的模板。没有值的占位符会使启动计划失败，而不是用一个损坏的参数启动客户端。
- 启动器的启动计划在选择了服务器端点时使用该端点，否则使用版本的默认端口。没有默认端口的版本需要一个能提供端口的服务器端点。
- 每次启动前，启动器会立即把 `session.toml` 作为临时文件写入客户端文件夹，并以原子方式替换上一个文件。hook 宿主在客户端加载时读取它一次；如果文件缺失，则不应用任何插件。

会话文件见[会话文件与本地数据](#会话文件与本地数据)。

## 登录与身份验证

登录分为两个独立的层，不应混淆：

1. **启动器账号**位于服务器上，并由服务器的启动器 API 验证。它提供访问令牌、刷新凭据和账户资料摘要。这是运营者的服务器所控制的部分。
2. **游戏登录身份**是游戏客户端在其登录请求中发送的内容。在以替身 messenger 模式运行的版本上，启动器的 messenger 宿主应答客户端的通行证查询并提供该身份。

### 客户端如何获得身份

messenger 宿主按以下顺序解析通行证（第一个非空值生效）：

1. 来自 `session.toml` 的启动票据，
2. 在客户端自己的登录对话框中输入的账号，
3. 来自 `session.toml` 的启动器账号，
4. 由旧版启动器脚本通过进程环境提供的账号，
5. `guest`。

客户端把该通行证作为登录 id 发送。当客户端使用自己的登录对话框时，替身会记录输入的 id 并将其返回，因此游戏使用玩家自己的输入登录。

### 启动器票据登录与游戏内登录

本指南区分两种情形：

- **启动器票据登录。** 玩家登录启动器（服务器 API），启动器向该 API 为所选版本申请一次性启动票据，并把它写入 `session.toml`。messenger 替身把票据作为通行证返回，游戏服务器在登录时兑换它，而不是把它当作账号名。票据按版本定义的 `login` 设置分发到两条路径：**替身**版本（`kr-812`、`id-400`、`id-1809`、`tw-454`，其 nmcogame 替身把票据作为登录通行证交给客户端）与**登录桥**版本（`cn-404`、`cn-406`、`cn-774`，其登录模块把票据带进 SDO 登录入口）。
- **游戏内登录。** 当没有提供启动器账号或票据时，客户端可以使用自己的登录流程。在替身版本上，替身接受在客户端登录界面中输入的 id（即登录调用的客户端对话框变体），并将其作为通行证返回，因此服务器看到的是普通账号名。在转发版本上完全不涉及启动器的替身，因为由原始函数库应答客户端；版本定义中的 `gamewidget`（`cn-279`、`cn-324`）或 `hook`（`kr-1256`）登录模式描述的就是这种情形，但启动器在当前检查点并未实现这些提供程序。

版本定义中的 `login` 值是数据：启动器模块目前只对 `unverified` 强制生效（拒绝这些版本），不会针对提供程序做其他分支处理。上述映射依据声明的 messenger 模式和宿主代码；在当前检查点尚未用真实客户端观察过。

### 票据在哪里兑换

每个大厅行的 `authentication` 选项决定游戏登录如何证明账号身份。内置 profile 从 `BFF_LOGIN_AUTHENTICATION` 读取它（`${BFF_LOGIN_AUTHENTICATION:-ticket}`），因此一个变量即可设置所有版本。

| `authentication` | 谁可以登录 | 需要 |
|---|---|---|
| `ticket`（默认） | 只有能兑换为该版本签发的启动票据的登录。其他任何账号字符串（包括空字符串和在游戏内登录框中输入的名称）都会以 `launcher ticket required` 关闭连接；已用过、未知或版本不符的票据会以 `invalid launcher ticket` 关闭连接。 | 一个处于激活状态的 `launcher.http` 行。没有它时大厅无法启动，并报出 `authentication: ticket requires an active launcher.http row, which publishes ILauncherTicketService.` |
| `open` | 客户端发送的任何账号名；开启 `auto_create_players` 时未知名称会创建新玩家。每次启动都会记录警告。 | 无。只应在可信的局域网或本地测试中使用。 |

在 `open` 模式下，旧的 `launcher_tickets` 开关（`BFF_LAUNCHER_TICKETS`）仍可让票据形式的登录与普通名称并存兑换；在 `ticket` 模式下兑换始终开启。

哪些版本能从启动器拿到票据，取决于各版本定义的 `login` 设置。当前定义经两条路径提供票据：**替身**版本（`kr-812`、`id-400`、`id-1809`、`tw-454`）与**登录桥**版本（`cn-404`、`cn-406`、`cn-774`）。其余版本目前拿不到票据——`kr-1256` 直接钩住客户端，`cn-279` 与 `cn-324` 使用普通组件，`cn-706`、`cn-717` 与 `cn-735` 的登录未经验证——因此在 `ticket` 下这些版本的玩家无法登录；在它们的定义提供票据之前，请在可信网络中以 `open` 运行这些版本。

### 票据能证明什么、不能证明什么

有效的启动票据能证明：它由本服务器签发给经过密码或刷新凭据认证的启动器账号、针对的正是该版本、尚未被使用，并且仍在其短暂有效期内。它是一次性的、绑定版本、只保存在服务器进程中，并且最迟与授权它的访问令牌同时过期。

它**不能**证明客户端二进制文件未被修改，不能证明游戏登录时启动器版本是可接受的，不能证明已安装推荐的插件，也不能证明连接使用了 TLS。每个实例的 `launcher_login` 策略（`required`、`optional`、`none`）会在服务器目录中公布，但游戏服务器并不强制它；实际强制手段是大厅的 `authentication` 选项。在 `ticket` 下，没有启动器账号的直接游戏登录会被拒绝；在 `open` 下，游戏登录完全不受保护。

## 会话文件与本地数据

启动器与服务器保存不同的数据，并且在当前检查点只实现了一个启动器文件。

### `session.toml`（每次启动）

- 位置：客户端文件夹中，游戏 messenger 宿主库旁边（Bubble Fighter 为 `nmcogame.dll`）。它在每次启动前立即写入。
- 结构：版本 1。它包含游戏与版本 id、分支、实例 id、messenger 模式、hook 日志路径、可选的版本指纹、可选的 `[login]` 段（含账号与票据）、可选的 `[endpoint]`（含服务器主机与端口），以及按顺序排列的 `[[plugins]]` 条目及其选项。
- hook 运行时在版本指纹不匹配时拒绝加载插件，并把未知键视为错误。这些检查验证会话格式与兼容性，不能阻止本地用户修改或删除文件，也不能阻止其省略插件条目。
- 该文件在其生命周期内都是机密：它可能携带账号名与启动票据。运行时从不记录这些值，启动器会在客户端退出时删除该文件。不要复制、编辑、共享该文件，也不要将其与客户端一起打包。
- 如果文件缺失，客户端仍会启动：它不加载任何插件，并根据改名后的原始库是否存在来选择 messenger 模式。
- hook 运行时把日志写入会话中的路径；当会话未指定路径时，写入 `%LOCALAPPDATA%\FantasyTale\BubbleFighter\logs\fthook.log` 下的默认位置。日志是有界的：文件在打开时被截断，其大小上限被限制在 16 KiB 到 64 MiB 之间（会话默认值为 1024 KiB），过长的行会被截断，达到上限时会写入一行最终标记。它每个插件记录一行结果，且绝不记录账号或票据。

客户端退出时删除文件是会话写入器契约的一部分。调用写入器与清理逻辑的生产启动流程仍是待办的路线图工作，因此请把它视为设计中的生命周期，而非已观察到的行为。

### 启动器偏好设置与定义

- 启动器在启动时从可执行文件旁的 `games` 文件夹读取游戏与客户端版本定义。
- 界面提供六种语言，并把所选主题、强调色来源和语言作为启动器窗口的本地偏好设置存储。这些是外观偏好，不是账号。
- 启动器自身没有日志；唯一写入的日志是客户端内的原生 hook 日志，如上所述。
- 尚未实现任何实例、服务器、账号或下载的文件格式；它们与对应界面属于同一批未完成的路线图工作。本指南不记录这些文件路径。

### 服务端启动器数据

- 启动器账号存储在服务器的账号存储中，使用 PBKDF2-SHA256 密码哈希；启动器自身从不验证密码。
- 持久登录只存储当前刷新凭据的 SHA-256 哈希，因此会话无需保留密码即可续期，并能通过持久化提供程序在服务器重启后存续。访问令牌与启动票据仅存在于内存中，重启即丢失。
- 访问令牌默认有效期为 1800 秒，刷新会话为 30 天，启动票据为 60 秒；运营者可在已验证的范围内调整。

## hook 插件

Bubble Fighter 的客户端补丁以 hook 插件形式提供。插件是一个包含 `plugin.toml` 的文件夹，其中声明身份、类型、兼容性和选项；数据补丁无需编译器。启动器目前随附三个数据插件：

| 插件 | 用途 | 适用版本 | 是否必需 |
| --- | --- | --- | --- |
| `bubblefighter.fantasy.font-weight` | 把国服字重应用到每个语言组，使其他地区客户端上的中文文本渲染得与国服版本一致。 | `kr-812`、`cn-706`、`cn-717`、`id-400`、`id-1809` | 否 |
| `bubblefighter.fantasy.no-file-verify` | 让客户端的配置表完整性比较器报告一致，从而加载修改过的配置表；包内每个成员的哈希仍然生效，因此修改过的表仍须正确重新打包。 | `kr-1256`，以及尚无启动器版本定义的 `kr-710` | 否 |
| `bubblefighter.fantasy.kr1256-shop-gates` | 重新启用最终韩服客户端所禁用的任务栏商店按钮与共享功能入口。 | `kr-1256` | 是，`required_on = kr-1256` |

hook 的预期工作方式：

- 插件以版本 id、分支选择器或所有版本来声明版本兼容性，并可以排除某些版本。未列出当前版本的插件会因不可用而被跳过，运行时也会独立于启动器拒绝它。
- 标记为必需的插件在该版本上无法禁用；必需插件失败时会在客户端产生一个警告消息框（“Required client plugins could not be applied; the game may not work correctly.”），hook 日志会为每个插件记录一行结果。
- 插件按实例启用，并以带选项的 `[[plugins]]` 条目写入 `session.toml`。新实例以该版本的默认插件列表启动；在当前检查点，九个定义中的该列表均为空。
- 启动器中用于安装、开关和配置插件的插件管理器尚未实现；插件页面仍是预览。通过手动编辑客户端或其可执行文件来安装插件不是受支持的路径。当前检查点的运行时只应用数据插件，并会以尚不支持原生插件的消息拒绝原生类型插件。

与启动器的其余部分一样，原生运行时和这些插件由原生测试覆盖，但在当前检查点尚未在真实游戏客户端中验证。三者都声明最低 hook 运行时版本为 3.0.0，且都是纯数据补丁插件；均未定义可配置选项。

## 服务端启动器 API

服务器可以通过可选的 **`launcher.http`** 插件行公开启动器 API。它是一个独立的公开 HTTP 监听器，不属于管理 API：它有自己的地址、自己在 `/v1` 下的路由，没有管理路由或 Bearer 令牌管理。它提供启动器账号、持久登录、启动票据、客户端预设目录，以及启动器应当执行的兼容性策略。

### 启用该行

- 核心 bundle 随附时该行处于**禁用**状态。`all` profile 启用该行，并保留部署变量 `BFF_LAUNCHER_LISTEN`、`BFF_LAUNCHER_TICKET_SECONDS`、`BFF_LAUNCHER_HOST_POLICY` 和 `BFF_LAUNCHER_REGION`，默认值分别为 `127.0.0.1:8090`、`600`、`auto` 和空地区。后续 `set` 补丁可以单独覆盖配置项，同时保留目录配置。
- 该插件需要一个处于激活状态的持久化提供程序，由它发布启动器账号仓库与启动器会话存储。三种提供程序都满足：memory、SQLite 和 MySQL。`all` profile 使用进程本地内存提供程序，因此重启时账号与登录会被重置；持久部署应使用 SQLite 或 MySQL。
- 该行与 `admin.http` 相互独立；启用或禁用其一不会改变另一个。

### 选项

该行的配置作为一个整体验证；未知键会导致绑定失败。最重要的键如下：

| 键 | 含义 | 默认值 / 限制 |
| --- | --- | --- |
| `listen` | API 监听器的 IP 地址与端口。明文 HTTP；远程部署应在其前方终止 HTTPS。 | `127.0.0.1:8090` |
| `name` | 目录中显示的公开服务器名称。 | `Bubble Fighter Fantasy`，最多 128 个字符 |
| `host_policy` | 启动器应如何访问游戏端点。 | `auto`、`ip` 或 `domain` |
| `region` | 向玩家显示的可选区域。 | 空，或两个大写字母 |
| `supports_instances` / `supports_plugins` | 公布服务器是否允许选择实例与客户端插件。 | `true` / `true` |
| `registration_enabled` | 启动器账号是否可以注册。 | `true` |
| `create_account_on_login` | 用未知名称登录时是否以所填密码创建该账号，使玩家无需注册。名称输错会创建新账号。 | `false` |
| `minimum_launcher_version` | 注册、登录、刷新与票据签发所需的最低启动器 SemVer。留空则禁用门禁。 | 空 |
| `client_policy` | 发送给启动器的商店来源和按插件的类型化规则，见[客户端策略](#client-policy)。 | 空 |
| `session_seconds` / `refresh_session_seconds` | 访问令牌与可续期会话的有效期（秒）。 | 1800 / 2592000 |
| `ticket_seconds` | 启动票据有效期（秒）。 | 600 |
| `requests_per_minute` / `max_concurrent_requests` | 全局请求预算与并发 API 工作数。 | 120 / 8 |
| `max_body_bytes` / `max_connections` | 请求体与连接上限。 | 16384 / 128 |
| `instances` | 目录中公布的客户端预设，最多 64 个。 | 空 |

### 客户端策略（`client_policy`） {#client-policy}

客户端策略告诉启动器：连接本服务器启动时，哪些插件必须使用、可以使用或不得使用。它包含两个列表，条目数量不设上限：

- `sources`：商店目录，每个包含小写的 `name`、`kind`（`http` 或 `alist`）、`base_url`（HTTP(S)，不含凭据、查询或片段）和 `catalog_path`。
- `plugins`：每个插件一个条目，包含商店 `id`、可选的 `name` 和 `description`（只在 WebUI 中显示，不会发送给启动器）以及它的 `rules`。

每条规则有一个在所属插件内唯一的 `id`、一个 `type`，以及可选的 `builds` 列表（为空表示所有版本）。规则类型如下：

| 类型 | 字段 | 启动器的行为 |
|---|---|---|
| `require` | 无 | 插件安装并启用之前拒绝启动，并提供安装。 |
| `disable` | 无 | 连接本服务器启动时关闭该插件，不改变已保存的选择。版本本身要求的插件不能被关闭，此时拒绝启动。 |
| `recommend` | `sources` | 每个客户端、每条规则推荐一次，从指定来源安装（列表为空时允许任何已声明的来源）。 |
| `min_version` | `version` | 已启用的插件低于此版本时，启动前必须更新。 |
| `max_version` | `version` | 已启用的插件高于此版本时，改为安装来源中允许的最高版本；没有可用版本时拒绝启动。 |
| `latest` | 无 | 启动前把已启用的插件更新到最新版本。 |

版本使用严格的 SemVer。同一插件的两条规则在版本范围重叠且满足以下任一条件时冲突：类型相同；其中一条是 `disable`；一条是 `latest`、另一条是 `max_version`；或 `min_version` 高于 `max_version`（两者相等即锁定一个版本）。存在冲突、格式错误或未知类型的规则时，该行无法启动。不认识某个规则类型的启动器会拒绝连接本服务器启动，并提示玩家更新启动器。

目录和每个登录响应都携带最低启动器版本，以及名为 `clientPolicy` 的策略（`version`、`sources`、`plugins`）；规则的 `version` 或 `sources` 只在其类型需要时出现。玩家登录时收到的策略用于启动；目录中的副本只在登录前显示。该策略配置的是兼容的启动器，不证明客户端完整性，也不表示游戏登录会检查已加载的模组。管理 API 参见 [WebUI 客户端策略](plugins.zh.md#client-plugin-policy)。

### 客户端预设（`instances`）

每个预设公布一个可选择的客户端实例：

| 字段 | 含义 |
| --- | --- |
| `id` | 稳定的预设标识符，由字母、数字和连字符组成。 |
| `build` | 客户端版本 id，例如 `kr-1256`。 |
| `name` | 本地化显示名称；必须包含 `en` 条目。 |
| `release_version` | 显示在名称下方的可选发行标签。 |
| `client_languages` | 可选的客户端语言选择权威列表。 |
| `host` / `port` | 启动器连接的公开游戏端点；端口为必填。 |
| `launcher_login` | `required`、`optional` 或 `none`：服务器公布的启动器身份验证偏好。这是目录数据；游戏服务器的实际强制手段是大厅的 `authentication` 选项。 |
| `download_source` | 客户端包的可选下载源：`kind`（`http` 或 `alist`）、`base_url`、`catalog_path` 与 `package_id`。URL 中不接受凭据、查询或片段。 |

预设是公布的数据；启动器尚不消费预设。服务器不会把预设的 `build` 与自身组合出的 profile 对照检查，因此一个拼写错误会产生票据永远无法兑换的预设。这种可移植预设不是设计中本地的实例文件：用于保存玩家服务器绑定与插件选择的本地 `instance.toml` 尚不存在。

### 路由

所有路由都位于 `/v1` 下，且仅由启动器监听器提供。

| 路由 | 方法 | 身份验证 | 用途 |
| --- | --- | --- | --- |
| `/v1/catalog` | GET | 无 | 服务器名称、区域、host 策略、支持标志、最低启动器版本、插件推荐与客户端预设。不受版本门禁限制，因此旧启动器可以读取该要求。 |
| `/v1/instances/{id}/instance.toml` | GET | 无 | 单个预设的可移植实例元数据：结构、id、名称、游戏与版本。有意不包含服务器地址或凭据。 |
| `/v1/auth/register` | POST | 无 | 在启用注册时创建启动器账号。 |
| `/v1/auth/login` | POST | 无 | 验证密码并返回访问令牌、刷新凭据、账户资料、最低版本与推荐。 |
| `/v1/auth/refresh` | POST | 刷新凭据 | 用一次性刷新凭据交换一对新凭据。 |
| `/v1/auth/logout` | POST | 刷新凭据或 Bearer 令牌 | 结束会话。不受版本门禁限制。 |
| `/v1/account` | GET | Bearer 令牌 | 已登录账号的资料：显示名称与等级、经验、Lucci 与现金（当对应的存储可用时）。 |
| `/v1/launch` | POST | Bearer 令牌 | 为服务器公布的版本签发一次性启动票据。 |
| `/v1/companion/tickets` | POST | Bearer 令牌 | 为账号已持有的启动重新签发一次性 Nirinsou Link 票据（`{"launchId":"..."}`）。 |

启动器通过 `X-FantasyTale-Launcher-Version` 请求头或 `launcherVersion` JSON 字段声明其版本。两者都提供时必须一致。

### 版本门禁与 HTTP 426

当设置了 `minimum_launcher_version` 时，注册、登录、刷新与票据签发都要求启动器版本达到或高于该值。没有提供版本、版本无法解析或版本较旧的请求会收到 **HTTP 426 Upgrade Required**，正文为以下 JSON：

```json
{ "error": "launcher_update_required", "minimumLauncherVersion": "<configured version>" }
```

目录端点本身不受门禁限制，因此启动器在登录前总能读取该要求。当前检查点的启动器尚不解析此响应，也不发送版本；因此该门禁实际是服务端拒绝，而不是应用内更新提示。

### JSON 兼容性

启动器 API 有意对新客户端宽容，同时对自身消费的内容严格：

- 未知的**顶层** JSON 字段会被忽略，因此较新的启动器可以向较旧的服务器发送额外字段而不会失败。
- 请求仍必须是 JSON 对象。重复的顶层键、错误的类型、缺少必需值、无效的用户名、与正文不一致的版本请求头、过大的请求体或过深的嵌套都会被拒绝为 **HTTP 400**，并返回 `{"error":"invalid_request"}`。
- 已知字段由使用它们的处理程序验证；服务器未公布的版本会被拒绝为 `unsupported_build`。
- 这种宽容仅适用于 JSON API。插件行配置仍然严格：未知 YAML 键会在启动时导致绑定失败。

- 启动器 API 在当前检查点没有修改密码、重置密码或删除账号的路由。运营者可以禁用注册，也可以在服务端封禁账号；启动器没有这两种操作的界面。

### 错误与限制

| 响应 | 含义 |
| --- | --- |
| `400 invalid_request` | JSON 格式错误、重复的顶层键、字段类型错误、用户名或版本无效，或版本未公布。 |
| `401 unauthorized` | 缺少或已过期的 Bearer 令牌；会话已不再有效。 |
| `403` | 注册已禁用。 |
| `409 account_unavailable` | 用户名已被注册。 |
| `426 launcher_update_required` | 上文所述的启动器版本门禁。 |
| `429` | 超出每分钟请求预算或并发门禁。 |
| `503` | 达到身份验证容量上限（保留的会话/票据过多）。 |
| `503 server_not_ready` | 所有行激活、主机日志打印 `Server is ready` 之前，登录、注册和启动都会被拒绝。 |
| `404 companion_unavailable` / `404 launch_not_found` | 未启用 `companion` 行时续签票据，或续签的启动已结束、属于其他账号。 |

响应携带 `Cache-Control: no-store`。该监听器按设计为明文 HTTP；启动器通过网络连接时，请将其置于自己的 TLS 终止之后；不通过网络连接时，请保持在回环地址上。该 API 从不提供游戏客户端、其文件或更新：下载源作为数据公布，供启动器将来安装客户端使用，服务器不代理这些内容。

### 示例配置

该行通常通过 profile 补丁启用。此示例只使用占位符和合成值：

```yaml
- replace:
    id: launcher.http
    disabled: false
    config:
      listen: "127.0.0.1:8090"
      name: "Example Community Server"
      region: "KR"
      minimum_launcher_version: "1.4.0"
      client_policy:
        sources:
          - name: "community"
            kind: "http"
            base_url: "https://store.example.com"
            catalog_path: "FantasyTale/source.toml"
        plugins:
          - id: "bubblefighter.fantasy.kr1256-shop-gates"
            rules:
              - { id: "needed", type: "require", builds: ["kr-1256"] }
              - { id: "floor", type: "min_version", builds: ["kr-1256"], version: "1.2.0" }
          - id: "example.optional-overlay"
            rules:
              - { id: "offer", type: "recommend", sources: ["community"] }
      instances:
        - id: "kr-1256-main"
          build: "kr-1256"
          name:
            - language: "en"
              text: "KR 2026"
            - language: "zh-CN"
              text: "KR 2026"
          release_version: "1.0.0"
          host: "game.example.com"
          port: 7100
          launcher_login: "required"
```

对于启动器行已被替换、监听器设置已被丢弃的 profile，请使用 `set` 补丁显式加回：

```yaml
- set:
    id: launcher.http
    config:
      listen: "${BFF_LAUNCHER_LISTEN:-127.0.0.1:8090}"
      host_policy: "${BFF_LAUNCHER_HOST_POLICY:-auto}"
      region: "${BFF_LAUNCHER_REGION:-}"
```

## Nirinsou Link {#nirinsou-link}

Fantasy Tale Nirinsou Link（简称 Nirinsou）是运行中的启动器与其启动游戏所在服务器之间的可选 WebSocket 通道，
用来承载原版游戏客户端没有对应封包的功能，例如显示在游戏上方的启动器弹窗。游戏连接本身不变；没有 Nirinsou
的启动与以前完全相同。

### 插件行

| 行 | 作用 |
| --- | --- |
| `companion` | 监听器与启动表。启用后，每个 `/v1/launch` 回复都带有 `companion` 对象：`url`、一次性 `ticket`、`launchId` 和 `expiresAt`。 |
| `companion.notices` | 启动器弹窗（`ui.notification.show`）与 `popup <文字>` 管理命令，权限为 `game.management.popup`。 |

两行在 core 包中默认禁用，由 `all` 配置启用。`companion` 的选项：

| 选项 | 默认值 | 含义 |
| --- | --- | --- |
| `listen` | `127.0.0.1:8092`（`BFF_COMPANION_LISTEN`） | WebSocket 监听地址。 |
| `public_url` | 空（`BFF_COMPANION_PUBLIC_URL`） | 交给启动器的 URL，路径为 `/companion/v1`；`{host}` 会替换为启动器访问时使用的主机。为空时为 `ws://{host}:<监听端口>/companion/v1`。 |
| `ticket_seconds` | `120` | 连接票据的有效时间。 |
| `pending_seconds` | `900` | 启动等待游戏登录的最长时间，超时即丢弃。 |
| `max_launches`、`max_connections` | `4096`、`2048` | 启动表与同时连接数的上限。 |
| `message_bytes`、`queue_messages`、`queue_bytes`、`messages_per_second` | `65536`、`128`、`1048576`、`128` | 每个连接的消息、队列与速率上限，与启动器一致。 |
| `idle_seconds` | `60` | 连接静默多久后关闭；启动器每 20 秒发送一次心跳。 |

### 启动如何绑定

1. `/v1/launch` 打开一个启动，返回游戏票据和 Nirinsou 授权；游戏票据记录该启动。
2. 启动器以子协议 `fantasytale.companion.v1` 升级 `GET /companion/v1`，并以 `Authorization: Bearer` 携带票据。
   票据只能使用一次；重连时通过 `/v1/companion/tickets` 获取新票据，新连接会替换旧连接。
3. 启动器发送 `companion.hello`，包含版本、启动 ID、语言及其支持的消息类型；服务器回复 `companion.welcome`，
   给出双方都支持的类型和状态 `pending_game`。
4. 游戏登录兑换游戏票据后，启动绑定到该游戏连接，启动器收到状态 `connected`。服务器消息只在此之后发送。
5. 游戏连接关闭时，启动结束，Nirinsou 连接随之关闭。

游戏始终没有登录的启动会在 `pending_seconds` 后丢弃。每条消息都是 JSON，包含 kind、id、type、架构版本和
payload；格式错误、重复、未协商或过快的消息会以原因代码关闭连接，启动器再用新票据重连。

监听器使用明文 `ws`。通过网络访问时，请像启动器 API 一样放在 TLS 终止之后，并把 `public_url` 设为 `wss`。

## 故障排查

以下是当前代码中存在的失败路径。当前检查点的启动器没有应用内修复操作；恢复意味着阅读消息或日志并修正输入。

| 症状 | 检查 | 可能原因与修复 |
| --- | --- | --- |
| `launcher.http` 行保持 `Pending` 或失败 | 管理 API 中的 `/api/plugins` 与启动日志 | 没有持久化提供程序发布启动器账号与会话存储。请只启用一个持久化提供程序。 |
| 大厅启动失败并报出 `authentication: ticket requires an active launcher.http row` | 启动错误 | 已启用票据认证（默认）或 `launcher_tickets`，但没有处于激活状态的 `launcher.http` 行发布票据服务。请启用启动器行，或在可信局域网中设置 `BFF_LOGIN_AUTHENTICATION=open`。 |
| 玩家登录后立即以 `launcher ticket required` 断开 | 服务器日志 | 大厅以 `authentication: ticket` 运行，而客户端发送的是普通账号名：它不是由启动器启动的，或其版本目前还拿不到启动器票据。请通过启动器启动，或在可信局域网中使用 `open`。 |
| 游戏以 `invalid launcher ticket` 关闭连接 | 票据是否由本服务器针对该确切版本签发且尚未使用 | 票据已被禁用、已使用、已过期、为另一版本签发，或自其签发以来启动器行已重启。请重启启动器、重新登录并再次启动客户端。 |
| 旧启动器收到 HTTP 426 | 该行的 `minimum_launcher_version` 与启动器版本 | 启动器低于配置的最低版本。请更新启动器，或为测试清除最低版本。当前启动器尚不显示更新提示。 |
| HTTP 400 `invalid_request` | 请求体与版本字段 | JSON 格式错误、重复的顶层键、类型错误、用户名无效，或请求头/正文版本不一致。 |
| HTTP 401 `unauthorized` | 访问令牌有效期与服务器重启 | Bearer 令牌已过期，或服务器重启后丢弃了内存中的 Bearer 令牌。请重新登录；刷新凭据应能续期会话。 |
| HTTP 429 或 503 | 请求速率、并发请求、保留的会话 | 触达服务器有界的身份验证限制。请稍后重试，或有意识地提高限制。 |
| 某个版本报告 “has no verified launch arguments yet” | 该版本的 `login` 值 | 该版本定义为 `login = "unverified"`。请使用具有已验证登录模式的版本。 |
| 客户端显示 “The launcher session file is invalid; no client plugins were loaded.” | 消息框文本与客户端文件夹 | `session.toml` 已过期、被手动编辑或格式错误。请通过启动器启动客户端，以便写入全新的会话；不要手动编辑会话。 |
| 客户端出现必需插件警告 | hook 日志的结果行 | 标记为必需的插件失败或对该版本不可用。请阅读该插件 id 对应的日志行，并修正其选项或兼容性。 |
| 启动器预览无法启动客户端 | — | 当前检查点尚未实现启动；预览中的启动按钮没有动作。 |
| 客户端启动后缺少预期的插件 | 会话日志路径下的 hook 日志（默认 `%LOCALAPPDATA%\FantasyTale\BubbleFighter\logs\fthook.log`） | 会话未写入、插件对该版本不可用，或补丁未匹配。请阅读该插件 id 的结果行。启动器没有日志查看器，也没有修复操作。 |

对于启动器 API 之外的服务端故障，请参见[故障排查](troubleshooting.md)。

## 分发边界

- 本服务器仓库与启动器仓库都不分发游戏客户端、解包后的客户端数据、专有原生文件或其他游戏客户端材料。启动器仓库包含启动器代码、定义与数据 hook 插件；游戏客户端始终是玩家自己的副本。
- 每版本的原生参数、`login.ini` 差异与 hook 选择都是**数据**：版本定义、插件定义与会话条目。它们不是针对个别版本的代码分支。
- 原生 hook 运行时与 messenger 宿主由启动器仓库自己的源码构建。hook 插件由该运行时根据插件定义应用；手动编辑客户端可执行文件或其函数库不是受支持的安装方法。
- 服务器把下载源与客户端预设作为数据公布。它不代理、托管或分发客户端包。

## 下一步

- [服务器配置](configuration.md)介绍启动器 API 背后的 profile 行与环境变量。
- [部署](deployment.md)介绍如何运行服务器、持久化、备份与网络安全。
- [故障排查](troubleshooting.md)介绍与启动器无关的服务端故障。

## 服务器公开展示与准入 {#server-presentation-and-admission}

`GET /v1/catalog` 新增 `presentation`，包含 `motd`、`iconUrl`、`images`、`onlinePlayers`、
`playerLimit`、`state`、`whitelist` 和 `rosterEnabled`。在 `launcher.http` 行中通过
`motd`、`icon_url`、`images`、`show_online_players`、`player_limit`、`maintenance`、
`whitelist_enabled` 和 `roster_enabled` 配置。默认不限制准入和人数，公开名单默认开启。
会话服务不可用时人数为 null，`show_online_players: false` 时为 -1。人数只统计已认证的
游戏玩家，按账号去重，排除已关闭连接及已识别的对局连接。尚未接入持久化峰值存储，因此不提供历史最高人数。

`GET /v1/players` 无需 Bearer，返回 `schema: 1`、`enabled`、`players` 和 `truncated`。
每个玩家只包含 `name` 和 `level`。`roster_enabled: false` 返回空名单且不读取会话注册表；
名单开启但注册表不可用时返回 HTTP 503 `player_state_unavailable`。
`max_roster_players` 限制单次返回条数，默认 1000，范围 1–10000；`truncated: true` 表示
实际在线玩家更多。人数与名单公开开关相互独立。HTTP 插件读取 `ISessionRegistry`，通过
`IPlayerSession.LoginConnectionId` 识别实际登录连接。共享玩家状态的对局连接，以及未提供
该身份的插件会话，不计入公开在线名单。这样票据签发无需等待使用票据的大厅和对局服务启动。

客户端预设支持可选的 `protocol_version`（1–65535）和 `patch_version`（最多 64 字符），
分别输出为 `protocolVersion` 和 `patchVersion`，与已有的 `build`、`releaseVersion`、
`clientLanguages`、`downloadSource` 并列。未配置的值保持未知；这些展示字段尚未用于校验启动请求中的客户端版本。

`maintenance: true` 阻止登录及签发新票据，返回 HTTP 503 `server_maintenance`。
可选的 `maintenance_start`、`maintenance_end`、`maintenance_description` 分别输出为
`maintenanceStart`、`maintenanceEnd`、`maintenanceDescription`；时间仅作说明，不会自动排期。
`whitelist_enabled: true` 按 `whitelist` 校验账号名，不区分大小写，最多 4096 个不重复账号；
不在名单内时返回 HTTP 403 `not_whitelisted`。正数 `player_limit` 要求启动时存在会话服务；
达到上限时新玩家收到 HTTP 503 `server_full`，已在线账号仍可取得替换票据。
这些是启动器准入检查，不是原子座位预留；直接游戏连接和票据兑换尚未重新检查这些选项。

认证失败通过 JSON `error` 返回 HTTP 401 `account_not_found`、`wrong_password` 或
`account_banned`，只有密码正确后才披露封禁状态，不编造封禁原因。Bearer 失败仍为 `unauthorized`。
关闭注册返回 HTTP 403 `registration_disabled`，请求限制返回 HTTP 429 `rate_limited`，
会话容量不足返回 HTTP 503 `authentication_capacity`。维护期间仍可刷新会话和退出登录；
网络连接失败由调用端处理，不伪装为服务端认证错误。

同一个 `POST /v1/launch` 请求还可携带 `release_version`、`patch_version`、
`launcher_language`、`native_language` 和 `client_language`。版本标签如有提供，必须是非空白、
不含控制字符且不超过 64 个字符的字符串；它们描述分发版本，不会作为协议版本解析，也不与公告的标签比较。
语言如有提供，必须精确为 `zh-CN`、`zh-TW`、`ko`、`en`、`id` 或 `vi`。
已知字段不合法时，在占用票据名额之前返回 HTTP 400 `invalid_request`。旧请求可以省略全部五个字段；
未知顶层字段仍会忽略，重复键仍会拒绝。成功签发票据时，插件日志记录 build 和这些描述信息，
不记录账号名、Bearer 或票据。这些信息不能证明已安装客户端的版本、语言或完整性。

游戏连接兑换启动票据后，`launcher_language` 决定该连接收到的服务端提示语言，包括房间指令和自动开局提示。
客户端所属地区、`native_language` 和 `client_language` 均不决定这些提示的语言。
未提供语言或缺少译文时，使用本地化插件的 `default_language`。
`launcher_languages` 将界面语言标签映射到资源名称；默认配置将 `zh-CN` 映射到 `zh-Hans`，
将 `zh-TW` 映射到 `zh-Hant`，其他标签直接匹配资源名称。目前没有越南语词典，`vi` 使用部署默认语言。
没有启动票据语言信息的连接同样使用部署默认语言。语言设置持续到连接关闭，并保留至首次创建角色完成。
