---
title: "安装"
description: "准备工具链，从源码构建服务器，或用 Docker 运行服务器。"
updated: 2026-10-20
---

Bubble Fighter Fantasy 是 Fantasy Tale Studio 维护的游戏「泡泡战士」（Bubble Fighter）社区服务器，使用 C# 与 .NET 10 编写。服务器能力由 YAML profile 中的插件行组合而成：profile 选择某个客户端版本需要的插件行，Host 负责激活它们。本页带领你从一台空白机器完成到确认可用的安装：先决条件、当前已有的安装方式、在 Windows 和 Linux 上从源码构建、运行 Docker Compose 组合、首次运行安装向导，以及用来验证每一步的检查。

**本页适合：** 第一次部署服务器、希望完成安装并确认可以运行的读者。不需要事先了解本项目。

## 安装的是什么

你安装的是服务器 Host 以及它启动时读取的数据：

- Host 可执行文件（`BubbleFighter.Fantasy.Host`）、插件程序集，以及随包提供的 `profiles`、`packets`、`data` 和 `bootstrap` 目录；
- 首次运行向导会在可执行文件旁创建的安装状态目录；
- 可选：你为要运行的客户端版本自行准备的客户端数据。

仓库不包含游戏客户端，也不包含客户端的专有数据。部分 profile 行需要匹配版本的本地文件，缺少这些文件时对应行会在激活阶段失败。项目目前是预发布版本（prerelease）：不同 profile 可用的玩法与所需数据不同，列出某个 profile 或通过自动检查并不代表真实客户端的兼容性已经验证。

每个版本都附带 Windows x64、Windows ARM64、Linux x64 与 Linux arm64 的预编译包。在 Linux 上可以用安装脚本以原生方式或 Docker 方式安装，见下文“安装预编译包”。源码构建与本仓库中的 Docker 文件仍然可用。

## 开始之前

| 要求 | 用于 | 如何检查 |
| --- | --- | --- |
| Windows x64、Windows ARM64、Linux x64 或 Linux arm64 | 运行服务器 | 系统设置；Linux 上执行 `uname -m` |
| .NET 10 SDK | 从源码构建 | `dotnet --version` 显示 10.x |
| Node.js 20.19 或更高版本 | 从源码构建（构建时会编译网页界面） | `node --version` 显示 20.19 或更高 |
| ASP.NET Core Runtime 10 | 运行依赖框架的构建输出 | `dotnet --list-runtimes` 显示 `Microsoft.AspNetCore.App 10.x` |
| Git | 从源码构建 | `git --version` |
| Docker Engine 与 Compose | 仅 Docker 方式 | `docker compose version` |
| 运行版本对应的客户端数据 | 游戏 profile | `client-data/<build>/data` 存在 |
| 位于仓库根目录的终端 | 本页除容器内部之外的每条命令 | `Get-Location` 或 `pwd` |

仓库没有规定最低 Windows 或 Linux 版本；请使用 .NET 10 SDK 支持的系统。可选的插件 SDK（Python、TypeScript、Go 和 Rust）在安装或运行服务器时都不需要。

## 选择安装方式

| 方式 | 状态 | 适用场景 |
| --- | --- | --- |
| Windows x64 构建包 | 可用 | Windows x64，希望使用现成构建 |
| Windows ARM64 构建包 | 可用 | Windows on ARM，希望使用现成构建 |
| Linux 安装脚本 | 可用 | 不使用 Docker 的 Linux 发行方式 |
| Docker 一键安装 | 可用 | 无需检出仓库，在容器中运行服务器，可选附带 MySQL |
| 从本仓库构建 Docker | 可用 | 已有检出与 Docker，在容器中运行服务器和 MySQL |
| 从源码构建 | 可用 | 希望在本机得到构建输出和首次运行向导 |

## 安装预编译包

每个版本附带四个程序包和一份 `SHA256SUMS` 校验清单。这些程序包依赖框架：运行它们的机器需要 ASP.NET Core Runtime 10。缺少运行时时，Linux 安装脚本会自动安装。

| 程序包 | 适用于 |
| --- | --- |
| `bubblefighter-fantasy-win-x64.zip` | Intel 或 AMD 64 位处理器上的 Windows |
| `bubblefighter-fantasy-win-arm64.zip` | Windows on ARM |
| `bubblefighter-fantasy-linux-x64.tar.gz` | x86_64 上的 Linux |
| `bubblefighter-fantasy-linux-arm64.tar.gz` | aarch64 上的 Linux |

最新版本的文件始终位于 `https://github.com/Wasabi-FantasyTaleDev/BubbleFighter.Fantasy/releases/latest/download/<package>`。

### Windows

1. 安装与处理器匹配（x64 或 Arm64）的 [ASP.NET Core Runtime 10](https://dotnet.microsoft.com/download/dotnet/10.0)，或安装已包含它的 .NET 10 SDK。
2. 下载与处理器匹配的 ZIP，解压到较短的文件夹，例如 `C:\BubbleFighterFantasy`。完整路径达到约 259 个字符的插件文件无法在 Windows 上加载；从这么深的文件夹启动时，服务器会拒绝启动并指出最长的文件。
3. 双击 `BubbleFighter.Fantasy.Host.exe`。安装向导随即启动，WebUI 会在默认浏览器中打开；接着按下文“首次运行设置”操作。关闭控制台窗口即停止服务器。启动失败时窗口会保持打开并显示错误，按任意键后才关闭。

### Linux 安装脚本

```bash
curl -fsSL https://wasabi-fantasytaledev.github.io/install.sh | bash
```

脚本会识别 x86_64 或 aarch64，从最新版本下载对应的程序包和 `SHA256SUMS` 并校验。系统没有 ASP.NET Core Runtime 10 时，它会用 Microsoft 的 `dotnet-install.sh` 将运行时安装到安装目录中，不改动系统级 .NET。安装目录为 `~/bubblefighter-fantasy`，以 root 运行时为 `/opt/bubblefighter-fantasy`：

| 路径 | 内容 |
| --- | --- |
| `releases/<tag>/` | 每个已安装版本解压后的程序包 |
| `current` | 指向当前版本的链接 |
| `state/` | 安装向导保存的安装状态，升级时保留 |
| `dotnet/` | 运行时，仅在安装脚本需要安装时存在 |
| `bff` | 启动器；不带参数时启动安装向导 |

用 `~/bubblefighter-fantasy/bff` 启动服务器，然后打开 `http://127.0.0.1:8080/`。向导只监听本机回环地址；在远程服务器上，请先在自己的电脑上用 `ssh -L 8080:127.0.0.1:8080 <user>@<server>` 转发端口。

选项写在 `bash -s --` 之后，例如 `curl -fsSL https://wasabi-fantasytaledev.github.io/install.sh | bash -s -- --service`：

| 选项 | 作用 |
| --- | --- |
| `--version <tag>` | 安装指定版本而不是最新版本 |
| `--dir <path>` | 安装目录 |
| `--admin-port <n>` | 安装向导与 WebUI 的端口（默认 8080） |
| `--service` | 以 root 运行时：创建并启动 `bubblefighter-fantasy` systemd 服务，以 `bff` 系统用户运行 |
| `--start` | 安装完成后启动服务器 |
| `--no-runtime` | 缺少运行时时直接失败，而不是安装它 |
| `--from <dir>` | 从存放版本文件的目录安装，不下载 |

升级时再次运行同一条命令即可。新版本会解压到旧版本旁边，并通过列出其 profile 进行检查，检查通过后才成为 `current`。`state/` 不会被改动。

### Docker 一键安装

```bash
curl -fsSL https://wasabi-fantasytaledev.github.io/install.sh | bash -s -- --docker
```

需要 Docker Engine 与 Compose 插件。安装脚本会把 Linux 程序包解压到 `~/bubblefighter-fantasy/docker`，用它构建本地镜像，并通过 Docker Compose 启动服务器容器。容器使用主机网络，因此你选择的客户端所用的游戏端口都可以访问。默认不启动数据库容器：在安装向导中选择 SQLite（数据文件保存在 `bff-state` 卷中），或填写已有的 MySQL 服务器。

如需同时运行内置的 MySQL 8.4，请加上 `--with-mysql`：

```bash
curl -fsSL https://wasabi-fantasytaledev.github.io/install.sh | bash -s -- --docker --with-mysql
```

此时 MySQL 只监听 `127.0.0.1:3306`（可用 `--mysql-port` 修改），生成的 `.env` 保存随机生成的数据库密码，只有其所有者可以读取。在安装向导中选择 MySQL，主机填 `127.0.0.1`，端口填安装脚本输出的端口，数据库 `bf_game_db`，用户 `bff`，密码为 `~/bubblefighter-fantasy/docker/.env` 中 `BFF_MYSQL_PASSWORD` 的值。

再次运行同一条命令即可升级；`.env` 与各个卷都会保留，带 MySQL 创建的部署升级后仍保留 MySQL。在该目录中运行 `docker compose logs -f bff` 可以查看日志。

## 在 Windows 上从源码构建

在 Windows PowerShell 中执行以下步骤。仓库可以克隆到任意目录；后续命令都在仓库根目录运行。

### 1. 克隆仓库

```powershell
git clone <repository-url> BubbleFighter.Fantasy
cd BubbleFighter.Fantasy
```

`<repository-url>` 是项目公布的仓库地址。如果你已经有检出或源码压缩包，请直接在该目录打开 PowerShell。

### 2. 检查 .NET SDK

```powershell
dotnet --version
```

命令必须输出 10.x 版本。如果版本不符或找不到命令，请先安装 .NET 10 SDK。

### 3. 构建所有项目 {#build-all-projects-windows}

```powershell
dotnet build BubbleFighter.Fantasy.sln -c Release
```

首次构建会下载依赖包，可能需要几分钟。本仓库把警告视为错误，因此成功构建以以下输出结束：

```text
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

### 4. 找到构建输出

| 内容 | 位置 |
| --- | --- |
| Host 可执行文件与程序库 | `src/BubbleFighter.Fantasy.Host/bin/Release/net10.0/` |
| 插件构建输出 | `artifacts/plugins/<AssemblyName>/` |
| 运行时使用的副本 | `<host output>/plugins/` |
| Host 旁的打包数据 | `<host output>/profiles/`、`packets/`、`data/`、`bootstrap/` |

Host 构建会把插件目录复制到 `<host output>/plugins/`，并在每次构建时替换该目录，因此已删除的插件不会残留。复制到输出中的 `profiles/` 不包含 `local.patch.yaml`；该文件是被 Git 忽略的开发者覆盖文件，只在检出目录中存在时才会生效。

### 5. 运行构建好的 Host

在仓库根目录执行，这样默认的 `./profiles` 目录才能解析：

```powershell
.\src\BubbleFighter.Fantasy.Host\bin\Release\net10.0\BubbleFighter.Fantasy.Host.exe profiles list
```

命令会为每个打包的 profile 输出一行，说明可执行文件及其 profile 数据已就位。启动安装向导的打包首次运行方式见下文“首次运行设置”。

**检查是否成功：** 构建以 `0 Warning(s)` 和 `0 Error(s)` 结束，且上面的命令输出 profile 行而不是报错。

## 在 Linux 上从源码构建

在 POSIX shell 中执行以下步骤。仓库可以克隆到任意目录；后续命令都在仓库根目录运行。

### 1. 克隆仓库

```sh
git clone <repository-url> BubbleFighter.Fantasy
cd BubbleFighter.Fantasy
```

### 2. 检查 .NET SDK

```sh
dotnet --version
```

命令必须输出 10.x 版本。

### 3. 构建所有项目 {#build-all-projects-linux}

```sh
dotnet build BubbleFighter.Fantasy.sln -c Release
```

成功构建以 `Build succeeded.`、`0 Warning(s)` 和 `0 Error(s)` 结束。

### 4. 运行构建好的 Host

```sh
dotnet src/BubbleFighter.Fantasy.Host/bin/Release/net10.0/BubbleFighter.Fantasy.Host.dll profiles list
```

请在仓库根目录运行，这样默认的 `./profiles` 目录才能解析。DLL 旁的 apphost 可执行文件用法相同。

### 5. 注意运行时要求

构建需要 .NET 10 SDK 和 Node.js 20.19 或更高版本：WebUi 项目会在首次构建时用 npm 编译网页界面，之后只在其源码变化时重新编译。依赖框架的输出（例如 `tools/publish-linux.ps1` 生成的发行目录，或从容器镜像中复制出来的文件）需要运行机器上安装 ASP.NET Core Runtime 10。可用 `dotnet --list-runtimes` 检查，确认存在 `Microsoft.AspNetCore.App 10.x`。下面的 Docker 方式会在镜像内提供运行时。

**检查是否成功：** 构建无警告无错误，且 `profiles list` 输出 profile 行。

## 使用本仓库的 Docker 运行

在仓库根目录、已安装 Docker Engine 与 Compose 插件的前提下执行以下步骤。

### 1. 创建环境变量文件

PowerShell：

```powershell
Copy-Item .env.example .env
```

Linux shell：

```sh
cp .env.example .env
```

启动前把 `.env` 中的 `BFF_MYSQL_PASSWORD` 设为非空的临时值。Compose 会用它配置 MySQL 容器；`.env` 被 Git 忽略，绝不要提交。MySQL 连接变量在 `.env.example` 中有说明。

### 2. 启动服务器与数据库

```sh
docker compose -f deploy/docker/compose.yaml --env-file .env up --build
```

compose 文件会启动两个服务：

- `bff` 使用 `deploy/docker/Dockerfile` 构建，构建上下文为仓库根目录。Dockerfile 先发布不带运行时的 Host，再在 ASP.NET Core Runtime 10 镜像中以非 root 系统用户运行，入口命令为 `dotnet BubbleFighter.Fantasy.Host.dll run --profile kr-1256`。镜像声明端口 7100、7101 和 8080。
- `mysql` 使用 `mysql:8.4` 镜像，数据库名为 `bf_game_db`、用户为 `bff`、字符集为 utf8mb4，数据存放在 `mysql-data` 卷中，并在服务器启动前通过健康检查。

对外发布的端口为服务器的 7100、7101、8080，以及 MySQL 的 `${BFF_MYSQL_PORT:-3306}`。服务器把 `${BFF_CLIENT_DATA_HOST:-../../client-data}`（即仓库中的 `client-data` 目录）以只读方式挂载到 `/app/client-data`；容器需要 `client-data/kr-1256/data` 或 `client-data/kr-1256/dataraw`，否则 clientdata 行会失败。两个服务都使用 `restart: unless-stopped`。

容器方式会刻意启动 MySQL，它不是隔离的本地检查，也不使用本地检查补丁。管理监听容器内绑定在 `127.0.0.1:8080`，如需通过发布的 8080 端口访问 WebUI，请在 `.env` 中加入 `BFF_ADMIN_HOST=0.0.0.0`。

### 3. 跟踪日志

```sh
docker compose -f deploy/docker/compose.yaml logs -f bff
```

启动日志应与下文“首次运行设置”中描述的行一致。按 Ctrl+C 只会停止日志跟踪，`up` 启动的服务仍在运行。

### 4. 停止容器

在运行 compose 命令的终端按 Ctrl+C，或在另一个终端执行：

```sh
docker compose -f deploy/docker/compose.yaml down
```

`down` 会保留 `mysql-data` 卷，之后再次 `up` 会继续使用同一个数据库。`docker compose -f deploy/docker/compose.yaml down -v` 会同时删除该卷及其中的全部游戏数据，请只在数据可以丢弃时使用。

### 手动单容器方式

如果不使用 compose 组合，而是自行运行一个容器，可以在仓库根目录构建镜像，并传入可访问的 MySQL 与客户端数据挂载：

```sh
docker build -f deploy/docker/Dockerfile -t bff-fantasy .
docker run --rm --env-file .env -e BFF_MYSQL_HOST=<mysql-host> -e BFF_ADMIN_HOST=0.0.0.0 -p 7100:7100 -p 7101:7101 -p 8080:8080 -v "<client-data-directory>:/app/client-data:ro" bff-fantasy
```

镜像只复制发布后的 Host 与 `profiles` 目录，不包含数据库，也不包含客户端数据。请把 `BFF_MYSQL_HOST` 指向容器能够访问的地址。

**检查是否成功：** `docker compose -f deploy/docker/compose.yaml ps` 显示各服务正在运行，服务器日志出现下一节所述的启动行。首次构建可能需要几分钟。

## 首次运行设置

打包的首次运行体验会启动内置 `setup` profile：安装向导与管理 WebUI，不监听任何游戏端口。它可以在没有客户端数据、也没有数据库的空白机器上启动；应用前需要选择并测试数据库。

### 1. 不带参数启动 Host

在 Windows 上，打开构建输出目录中的终端并运行可执行文件：

```powershell
.\BubbleFighter.Fantasy.Host.exe
```

请把随包的 `profiles`、`packets` 和 `plugins` 目录放在可执行文件旁；构建输出已经包含它们。进程会把工作目录切换到可执行文件所在目录，因此从哪里启动都不影响。`start` 命令从不读取仓库根目录的 `.env`；向导把设置保存在状态目录中。

状态目录默认是可执行文件旁的 `state/`。`BFF_STATE_DIR` 或 `--state-dir <directory>` 可以覆盖它；相对路径会基于启动进程时所在的目录解析。

### 2. 读取打印的地址

控制台会打印管理监听地址与 WebUI 地址：

```text
Row 'admin.http' admin API listening on 127.0.0.1:8080.
WebUI: http://127.0.0.1:8080/
```

在浏览器中打开 `http://127.0.0.1:8080/`。管理监听默认是 `127.0.0.1:8080`；如需修改，请在进程环境中设置 `BFF_ADMIN_HOST` 或 `BFF_ADMIN_PORT`（`start` 不读取 `.env`）。

### 3. 完成向导

1. 选择界面语言：英语、简体中文、繁体中文或韩语。
2. 为服务器命名（1 到 80 个字符）。
3. 选择一个或多个要启用的客户端版本。每个选中的版本有各自的客户端数据选择；你也可以暂时不提供数据，稍后在服务器设置中导入。
4. 可选：现在导入客户端数据。选择本地文件夹或准备好的 ZIP 包，检查筛选后的文件数量与大小，然后点击“开始导入”。浏览器自身的文件夹确认可能会统计文件夹内的全部文件；向导会先把选择筛选为客户端档案与 `data`/`dataraw` 文件，再显示汇总。导入会在状态目录中生成一份副本；失败或取消的导入只清理它自己的临时文件。
5. 选择数据库。默认选中 MySQL：主机 `127.0.0.1`、端口 `3306`、数据库 `bf_game_db`、用户 `bff`。SQLite 默认把数据库保存在状态目录中。请执行“测试连接”；连接测试成功前向导不会继续。
6. 创建管理员账号。用户名 3 到 64 个字符，密码至少 12 个字符。该账号用于管理服务器，随安装信息一起保存，与游戏账号分开。
7. 检查各项设置并应用。Host 会启动所选 profile 的行；如果激活失败，会显示可重试的错误码。

### 4. 应用成功后磁盘上的内容

在状态目录中会出现：

- `installation.json`：保存的设置与管理员凭据哈希；
- `installation.lock`：防止两个安装进程同时管理同一状态的锁文件；
- `runtime.generated.patch.yaml`：派生出的持久化覆盖文件（只有占位符，不含数据库凭据）；
- `administration/client-imports/<token>/`：每次导入的客户端数据副本各占一个目录；
- 选择 SQLite 时的 `saves/fantasy.db`。

### 5. 导入规则与限制

文件夹导入接受客户端原始相对路径下的档案文件（保留客户端的字母大小写），也接受路径中包含 `data` 或 `dataraw` 的散装文件。ZIP 导入接受一个标准单卷 ZIP，其条目为 `<build>/data` 或 `<build>/dataraw`，也可以位于最多两层包装目录之下，例如 `client-data/<build>/data`。包含多个版本目录的数据包只导入所选版本。不支持 ZIP64 与分卷压缩包；请先在本地解压，再使用文件夹选择。

也可选择已安装的游戏文件夹：服务器会自动生成所需数据，不需要 Python。只包含服务端数据的档案整包复制到 `data/`；混合档案中所需的小文件解到 `dataraw/`，不提取模型和贴图。生成前会检查必需文件并报告缺失的文件名，成功后保存到 `<state-dir>/client-data/<build>`。已有生成数据不会被覆盖；若需更换配置来源，请选择预先整理好的数据。预先整理好的文件夹和 ZIP 仍作为独立副本保存到 `administration/client-imports/<token>/`。生成过程受上传、解包、存储、文件数和超时限制。

导入只是把文件复制到安装状态；所需的数据表与档案内容会在游戏服务启动时检查。导入成功并不代表玩法或客户端兼容性已经验证。

默认限制为每次上传 8 GiB、每次导入解压数据 16 GiB、每次导入 32,768 个文件、保留 64 份副本、导入存储总量 64 GiB、每次导入最长 20 分钟。可在 `installation` 行中用 `client_data_upload_bytes`、`client_data_extracted_bytes`、`client_data_file_limit`、`client_data_copy_limit`、`client_data_storage_bytes` 和 `client_data_import_seconds` 调整。再次导入不会删除先前成功的副本。

**检查是否成功：** 向导报告安装完成，WebUI 切换到管理界面。即使还没有管理员，下文所述的 `/health` 检查在向导运行期间也会正常响应。

## 检查安装

请在源码构建的仓库根目录运行以下命令。它们检查不同层面，建议三条都执行。

### 1. 列出打包的 profile

```powershell
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- profiles list
```

命令会为每个打包的 profile 输出一行：id 与描述。本版本列出 19 个，其中包括 `setup`（“Database-independent first-run installation and administration.”）和 `management`（“Administration and durable storage only; no game or relay ports.”）。这证明 profile 文件可以读取并参与组合；它不能证明任何插件已经激活。

### 2. 列出发现的插件

```powershell
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- plugins list
```

命令会为每个发现的插件定义输出一行：插件 id、版本、支持的客户端版本以及加载来源。这证明 Host 旁的插件目录可以被发现；它不能证明某个 profile 的行会激活。

### 3. 组合 profile 但不启动

```powershell
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- run --profile kr-812 --patch tools/local-checks/isolated-memory.patch.yaml --dump-config
```

这条命令要求已设置 `BFF_MYSQL_PASSWORD`，因为组合 MySQL 行时会插值 `${BFF_MYSQL_PASSWORD}`，之后最后的补丁才会停用该行。如果还没有准备，请按[快速开始](quickstart.md)创建 `.env`。

命令以 YAML 输出生效的行列表，每一行是一个以 `- id:` 开头的块，然后以退出码 0 结束。请确认以下内容：

- `persistence.mysql` 行带有 `disabled: true`；
- `persistence.memory` 行没有 `disabled: true`，即已启用；
- `clientdata` 行显示 `kr-812` 解析后的 `data_directory` 与 `raw_directory`。

该转储证明组合、插值与补丁顺序：命令行补丁最后应用，会覆盖开发者 `.env` 或本地补丁中指向真实数据库的设置。它不会启动监听，因此不能证明激活或客户端数据。

::: warning
当补丁启用 MySQL 时，配置转储可能包含插值后的值，包括凭据。请把转储视为敏感内容，不要粘贴到聊天或问题中。
:::

### 安装向导路径的浏览器检查

向导在默认地址运行时：

| 检查 | 命令 | 应看到的内容 |
| --- | --- | --- |
| 健康检查 | `Invoke-RestMethod http://127.0.0.1:8080/health` | `status` = `ok` 以及 UTC 时间戳 |
| 安装状态 | `Invoke-RestMethod http://127.0.0.1:8080/api/setup/status` | 包含 `phase`、`installed`、`profiles` 与导入限制的 JSON |

在 Linux 上使用 `curl -s http://127.0.0.1:8080/health` 与 `curl -s http://127.0.0.1:8080/api/setup/status`。`/health` 与 WebUI 文件始终开放；在管理员尚未创建时，`/api/plugins` 按设计返回 401，因此请先通过向导创建管理员。

**检查是否成功：** 三条源码构建命令都成功退出，浏览器检查返回上述 JSON。

## 各部分的位置

| 内容 | 位置 |
| --- | --- |
| 仓库检出 | 你克隆到的目录 |
| Host 构建输出 | `src/BubbleFighter.Fantasy.Host/bin/Release/net10.0/` |
| 插件构建输出 | `artifacts/plugins/<AssemblyName>/`（被 Git 忽略） |
| Host 旁的文件 | `<host output>/profiles/`、`packets/`、`data/`、`bootstrap/`、`plugins/` |
| 安装状态 | 可执行文件旁的 `state/`；`BFF_STATE_DIR` 或 `--state-dir` 可覆盖 |
| 环境变量文件 | 仓库根目录的 `.env`；被 Git 忽略；`start` 不读取 |
| 日志 | 控制台输出；数据包日志写入工作目录下的 `logs/{date}/` |
| 客户端数据 | `client-data/<build>/data` 或 `dataraw`；可用 `BFF_CLIENT_DATA` 覆盖 |
| Docker 数据 | `mysql-data` 卷；客户端数据以只读方式挂载 |

除 `start` 之外，每条命令都会先把仓库根目录的 `.env` 应用到尚未设置的进程环境变量；已存在的环境变量优先，且值永远不会写入日志。

## 停止与删除

- **停止源码构建或向导：** 在终端按 Ctrl+C。控制台会打印 `Shutdown complete.`，随后进程退出。
- **停止 Docker：** 在前台 compose 命令中按 Ctrl+C，或执行 `docker compose -f deploy/docker/compose.yaml down`。只有确实要删除数据库卷时才加 `-v`。
- **删除安装状态：** 会移除保存的安装设置、管理员账号、导入的客户端数据副本以及默认 SQLite 数据库。该操作不可恢复，请先停止服务器。
- **删除数据库：** 会移除长期保存的游戏数据。请先备份，绝不要删除玩家正在使用的在线数据库。[部署](deployment.md)页面介绍了备份与升级。
- **删除构建输出：** 安全，重新执行构建命令即可恢复。

## 出现问题时

- **构建失败：** 本仓库把警告视为错误。第一条 `error` 会指出项目与原因，修复后重新构建。参见[疑难排查](troubleshooting.md)。
- **依赖框架的构建无法启动：** 缺少 ASP.NET Core Runtime 10。用 `dotnet --list-runtimes` 确认，然后安装相同架构的运行时。
- **端口已被占用：** 向导使用 8080 端口，游戏 profile 使用自己的游戏端口，例如 `kr-812` 的 7110。请在 `.env` 中设置 `BFF_ADMIN_PORT` 或该版本的端口变量，或停止占用端口的进程。
- **游戏 profile 因客户端数据失败：** `clientdata` 行会报告 `data_directory` 与 `raw_directory` 都不存在。请提供匹配的客户端数据，或通过安装向导导入，然后重新启动该 profile。参见[疑难排查](troubleshooting.md)。

## 下一步

继续阅读[快速开始](quickstart.md)，启动一个隔离的本地服务器并确认它正在运行；然后阅读[服务器配置](configuration.md)，选择持久化方式、端口与 profile 调整。如果需要长期运行的服务、备份与升级，请继续阅读[部署](deployment.md)。客户端数据始终需要单独提供：本仓库不包含游戏客户端及其数据。配套的 Fantasy Tale Launcher 有独立指南：[启动器](launcher.md)。
