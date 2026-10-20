# Bubble Fighter Fantasy

使用现代技术重现回忆中的泡泡村悠闲时光。

Bubble Fighter Fantasy 是由 **Fantasy Tale Studio** 维护的「泡泡战士」3D 射击游戏社区服务器模拟器。服务器主要使用 **C# / .NET 10** 开发。项目没有特权核心：监听、协议帧与加密、封包路由、游戏逻辑、持久化、管理 HTTP API 与网页界面全部由插件实现，并通过 YAML profile 选择与配置。Python、TypeScript、Go 和 Rust 是可选的插件 SDK，运行服务器不需要安装这些语言。

## 项目状态

本项目处于**预发布**阶段，尚未对每个受支持的客户端完成验证；列出某个 profile 或自动化检查通过，都不代表真实客户端兼容性已经成立。

- 仓库不包含游戏客户端及其专有数据。游戏 profile 需要自行准备客户端数据，见[客户端数据](#客户端数据)。
- profile 描述多个历史客户端版本（韩国、中国、印度尼西亚与台湾），各版本已实现的玩法范围不同。
- 每个版本都会发布 Windows x64/ARM64 与 Linux x64/arm64 的预编译包，见[安装预编译包](#安装预编译包)。

## 环境要求

| 要求 | 用途 |
| --- | --- |
| [.NET SDK 10.0](https://dotnet.microsoft.com/) | 从源码构建并运行 |
| [Node.js 20.19 或更高版本](https://nodejs.org/) | 从源码构建（构建时会编译网页界面） |
| ASP.NET Core Runtime 10.0 | 运行依赖框架的发布版本 |
| Git | 获取仓库 |
| Docker 与 Compose 插件 | 容器部署（可选） |
| 所选版本的客户端数据 | 仅游戏 profile 需要（管理与检查不需要） |

本版本支持的平台：Windows x64、Windows ARM64、Linux x64 与 Linux ARM64。已知的部署环境为 Debian 13；其他 Linux 发行版未声明经过实测。下方分别给出 Windows 与 Linux 的源码运行方式。

## 安装预编译包

运行这些程序包的机器需要 ASP.NET Core Runtime 10。

- **Windows：**从[最新版本](https://github.com/Wasabi-FantasyTaleDev/BubbleFighter.Fantasy/releases/latest)下载 `bubblefighter-fantasy-win-x64.zip` 或 `bubblefighter-fantasy-win-arm64.zip`，解压到较短的文件夹（例如 `C:\BubbleFighterFantasy`），然后双击 `BubbleFighter.Fantasy.Host.exe`。安装向导会在浏览器中打开。
- **Linux：**运行 `curl -fsSL https://wasabi-fantasytaledev.github.io/install.sh | bash`，然后运行 `~/bubblefighter-fantasy/bff` 并打开 `http://127.0.0.1:8080/`。缺少运行时时，脚本会自行安装。
- **Docker：**`curl -fsSL https://wasabi-fantasytaledev.github.io/install.sh | bash -s -- --docker` 会在容器中运行服务器；加上 `--with-mysql` 可附带 MySQL 8.4。

全部选项见[安装指南](docs/installation.zh.md#安装预编译包)。

## 从源码快速开始（Windows PowerShell）

```powershell
git clone https://github.com/Wasabi-FantasyTaleDev/BubbleFighter.Fantasy.git
cd BubbleFighter.Fantasy
dotnet --version   # 应报告 .NET 10 SDK
dotnet build BubbleFighter.Fantasy.sln -c Release
Copy-Item .env.example .env
```

构建必须以**零警告、零错误**完成。编辑被忽略的 `.env`，把 `BFF_MYSQL_PASSWORD` 设为非空的临时值；即使本次检查不使用 MySQL，配置插值仍需要这个变量。

启动一个使用内存存储、不需要游戏数据的本地服务器，然后验证：

```powershell
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- run --profile management --patch tools/local-checks/isolated-memory.patch.yaml
```

在另一个终端：

```powershell
Invoke-RestMethod http://127.0.0.1:8080/health
```

返回 `{"status":"ok", ...}` 表示服务器已启动。按 `Ctrl+C` 停止；内存中的数据会丢失。`management` profile 只运行管理与存储，不开放游戏或中继端口。若要运行游戏 profile，请先准备客户端数据并选择匹配的 profile，参见[安装](docs/installation.zh.md)、[快速开始](docs/quickstart.zh.md)与[配置](docs/configuration.zh.md)。

构建输出目录本身就是一个可运行的发布目录：直接启动
`src/BubbleFighter.Fantasy.Host/bin/Release/net10.0/BubbleFighter.Fantasy.Host.exe`（不带参数）会打开首次运行设置向导，并打印网页界面地址（`WebUI: http://127.0.0.1:8080/`）。

## 从源码快速开始（Linux）

```sh
git clone https://github.com/Wasabi-FantasyTaleDev/BubbleFighter.Fantasy.git
cd BubbleFighter.Fantasy
dotnet --version   # 应报告 .NET 10 SDK
dotnet build BubbleFighter.Fantasy.sln -c Release
cp .env.example .env
```

在 `.env` 中把 `BFF_MYSQL_PASSWORD` 设为非空的临时值，然后执行同一本地检查：

```sh
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- run --profile management --patch tools/local-checks/isolated-memory.patch.yaml
```

```sh
curl http://127.0.0.1:8080/health
```

按 `Ctrl+C` 停止。

## Docker Compose

仓库提供同时运行服务器与 MySQL 的 Compose 配置。默认容器使用游戏 profile，因此请先准备客户端数据（见下文），否则容器会在启动时因缺少数据而退出。

```sh
cp .env.example .env      # 设置 BFF_MYSQL_PASSWORD 并检查其他值
docker compose -f deploy/docker/compose.yaml up --build
```

服务器开放 7100（游戏）、7101（中继）与 8080（管理 API 与网页界面）；MySQL 监听 `${BFF_MYSQL_PORT:-3306}`。客户端数据目录以只读方式挂载到 `/app/client-data`，数据库状态保存在 `mysql-data` 卷中。管理监听器在容器内默认绑定 `127.0.0.1`，如需通过已发布的 8080 端口访问，请在 `.env` 中设置 `BFF_ADMIN_HOST=0.0.0.0`。外部数据库模板、备份、升级与回退见[部署](docs/deployment.zh.md)。

## 客户端数据

仓库不包含游戏客户端及其数据文件，本项目也不会分发它们。游戏 profile 从本地准备的 `client-data/<build>/data`（按客户端原始相对路径存放的档案，如 `Packs/...`、`Config/...`）与 `client-data/<build>/dataraw`（相同相对路径的散装解包文件）读取数据。可用 `BFF_CLIENT_DATA` 指定其他根目录。缺少数据的 profile 会在激活时让 `clientdata` 行失败，错误信息会列出查找过的两个路径。首次运行设置向导也可以把准备好的文件夹或 ZIP 导入服务器的状态目录。

本项目不会提供专有客户端文件的下载链接。

## 文档

| 指南 | 内容 |
| --- | --- |
| [安装](docs/installation.zh.md) | 前置条件、源码构建、Docker、首次运行设置 |
| [快速开始](docs/quickstart.zh.md) | 几分钟内启动经过验证的本地服务器 |
| [配置](docs/configuration.zh.md) | profile、补丁、环境变量、端口、持久化、客户端数据 |
| [插件](docs/plugins.zh.md) | 扩展模型与第一个原生插件 |
| [SDK 协议](docs/sdk-protocol.zh.md) | 可选的 Python、TypeScript、Go 与 Rust 插件 |
| [部署](docs/deployment.zh.md) | 发布构建、Docker Compose、备份、升级、安全 |
| [故障排查](docs/troubleshooting.zh.md) | 症状 → 检查 → 原因 → 处理 |
| [参与贡献](CONTRIBUTING.zh.md) | 构建、测试、审查规则与封包数据流程 |
| [更新日志](CHANGELOG.zh.md) | 按日期记录的用户可见变更 |

文档站通过明确的页面清单发布 English、中文（简体）、中文（繁体）与 한국어；构建方法见[站点说明](docs-site/README.zh.md)。英文 README 见 [README.md](README.md)。

## 仓库结构

| 路径 | 内容 |
| --- | --- |
| `src/BubbleFighter.Fantasy.Abstractions`、`src/BubbleFighter.Fantasy.Contracts` | 共享插件契约与边界接口 |
| `src/BubbleFighter.Fantasy.Kernel` | 插件运行时：注册表、激活、effect、profile 组合 |
| `src/BubbleFighter.Fantasy.Host` | `bff` 命令行宿主 |
| `src/Plugins/` | 监听、方言、游戏功能、持久化、管理与加载器插件 |
| `packets/` | 封包结构数据与版本继承 |
| `profiles/` | 插件行、版本差异与 opcode 表 |
| `sdk/` | 可选的 Python、TypeScript、Go 与 Rust 插件 SDK |
| `tests/`、`tools/` | xunit 测试、CI 门禁与本地检查 |
| `docs/`、`docs-site/` | 公开指南与 VitePress 文档站 |

## 开发

修改代码前请阅读 [AGENTS.md](AGENTS.md) 与 [CONTRIBUTING.zh.md](CONTRIBUTING.zh.md)。每项改动放在独立分支上，说明改变了什么行为并附上验证证据。构建必须零警告；公开类型需要真实的 XML 文档；部署参数属于经过校验的配置，封包布局是 `packets/` 下的数据。

```sh
dotnet build BubbleFighter.Fantasy.sln -c Release
dotnet test BubbleFighter.Fantasy.sln -c Release
python tools/ci/serial-gates.py --public --results-directory artifacts/validation/public-01
```

公开门禁只排除 `Category=LocalClientData` 的用例，不等于完整发布门禁；完整门禁需要的本地客户端工作簿见 CONTRIBUTING.zh.md。自动化检查不能替代真实客户端测试。

## 许可证

MIT，见 [LICENSE](LICENSE)。第三方依赖仍遵循各自的许可证。

如需报告安全漏洞，请按[安全政策](SECURITY.zh.md)使用私密渠道。
