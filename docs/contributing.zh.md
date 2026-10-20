---
title: "参与贡献"
description: "配置工具链、构建与测试、遵循项目规则，并提交可审查的改动。"
updated: 2026-10-18
---

## 本页内容

本页带新贡献者从一台干净的机器走到一个可审查、可合并的改动：先读哪些规则、需要什么工具链、构建与验证命令、仓库结构、改动附带的责任，以及审查流程。Bubble Fighter Fantasy 是由 Fantasy Tale Studio 维护的预发布社区服务器，使用 C# 与 .NET 10 编写，并由插件组合而成。

**适用对象。** 首次贡献的人，以及需要复现或审查他人改动的贡献者。

**开始之前。**

- 阅读仓库根目录的 `AGENTS.md` 和 `CONTRIBUTING.md`。`AGENTS.md` 具有约束力，包含架构、packet 数据、证据、命名和语言规则。
- 安装 .NET 10 SDK 和 Git；构建并运行服务器需要这两者。
- 真实游戏客户端及其数据只在原生客户端测试时需要。仓库本身不包含它们。

## 先读规则

| 规则 | 实际含义 |
| --- | --- |
| 一切都是插件 | 新能力以 profile 中的插件行形式交付，而不是内核或 Host 中的特例。 |
| Context 是服务注册表 | 只通过声明的接口类型消费服务，不要访问其他插件的具体类。 |
| 注册即 effect | 每个 `Register*`/`On*` 都返回可释放句柄，交给 `context.Effect(...)`，卸载时按相反顺序回退。 |
| 不允许硬编码可调参数 | 随部署变化的值是插件行上经过校验的配置字段，而不是常量。 |
| 失败要响亮 | 错误配置、缺少服务、畸形 packet 和连接失败都是错误，绝不静默使用默认值。 |
| 资源必须有界 | 不允许无界队列、缓冲区或文件写入；溢出要报告，且不能拖住游戏。 |
| packet 布局是数据 | 结构写在 `packets/` 中，以基线写一次并按构建注解；代码按字段名提供值。 |
| 证据来自客户端 | 布局与顺序通过阅读客户端自身代码确定，每项结论保留带构建前缀的函数地址。 |
| 代码使用英语 | 代码、注释、XML 文档、提交消息、`opcodes.yaml` 和 `profiles/opcode-policy.md` 使用英语。 |
| 用户文档双语 | 英文指南与其中文翻译在同一次提交中更新。站点页面还需要繁体中文和韩语。 |

任何地方都不要使用 emoji。不要引用或提及第三方项目、其他服务器或参考树；用本项目自己的术语描述设计。

## 配置工具链

| 工具 | 用途 | 检查 |
| --- | --- | --- |
| .NET 10 SDK | 构建与运行服务器 | `dotnet --version` 显示 10.x |
| Git | 克隆、分支、验证门禁 | `git --version` |
| Python 3 | CI 门禁脚本及其单元测试 | `python --version` |
| Node.js 与 npm | 文档站、TypeScript SDK 一致性 | `node --version`、`npm --version` |
| Go | Go SDK 一致性检查与测试 | `go version` |
| Rust 与 cargo、rustup，以及 `wasm32-wasip1` | Rust SDK 一致性与 WebAssembly 测试 | `rustc --version`；`rustup target add wasm32-wasip1` |

构建并运行服务器只需要 .NET 和 Git。其余工具链供验证门禁、文档站和 SDK 测试套件使用；缺少时门禁会报告，而不是静默跳过。

在 PowerShell 或 shell 中，从应放置检出的目录执行：

```sh
git clone https://github.com/Wasabi-FantasyTaleDev/BubbleFighter.Fantasy.git
cd BubbleFighter.Fantasy
dotnet build BubbleFighter.Fantasy.sln -c Release
```

**确认成功。** 克隆目录包含 `BubbleFighter.Fantasy.sln`，构建以 `0 Warning(s)` 和 `0 Error(s)` 结束。

## 本地构建与测试

### 构建解决方案

```sh
dotnet build BubbleFighter.Fantasy.sln -c Release
```

警告即错误（`TreatWarningsAsErrors` 已启用），且 `GenerateDocumentationFile` 要求每个公开类型和成员都有有意义的 XML 摘要。修复原因，绝不要为了让构建变绿而屏蔽警告。

### 运行测试项目

```sh
dotnet test BubbleFighter.Fantasy.sln -c Release
```

这是开发时的快速循环。发布门禁并不直接使用它：门禁用 `git ls-files tests/*/*.csproj` 发现每个被跟踪的测试项目（包括解决方案之外的），并逐个串行运行，每个项目一份 TRX。每次尝试使用独立的结果目录或文件名，避免重跑覆盖此前的失败。

### 运行门禁自测

```sh
python -m unittest discover -s tools/ci -p "test_*.py"
```

这些测试保护门禁运行器本身，防止空报告、缺失报告或被跳过的报告被误报为成功。

### 运行公开门禁

```sh
python tools/ci/serial-gates.py --public --results-directory artifacts/validation/public-01
```

每次尝试都使用新的、不存在的结果目录——运行器拒绝已存在的目录。它会构建 Release、运行 SDK 一致性检查、逐个串行运行测试项目、检查 packet 表，并转储带隔离补丁的 `kr-1256` 配置。最后输出 `PASS public gates: <n> populated test reports, <n> tests, no skips`，并把排除范围记录到 `scope.json`。公开模式运行每个被跟踪的测试项目，只排除 `Category=LocalClientData` 用例；它不是完整发布门禁。如果设置了 `CI=true`，运行器还会拒绝含有 `.env` 或 `profiles/*/local.patch.yaml` 的检出。

**确认成功。** 退出码为 0，最后一行是上面的 `PASS` 行。非零退出会打印 `FAIL <reason>`；失败步骤的日志在结果目录中。

### 运行本地 Host 检查

遵循[快速开始](quickstart.md)：把 `.env.example` 复制为 `.env`，将 `BFF_MYSQL_PASSWORD` 设为非空的临时值，并且始终附加隔离补丁——`--dump-config` 也一样：

```sh
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- run --profile management --patch tools/local-checks/isolated-memory.patch.yaml
```

该补丁停用 `persistence.mysql` 并启用内存持久化，因此开发者的 `.env` 或本地覆盖不会连接真实数据库。绝不要把测试或本地检查指向生产数据库。**确认成功：** 另一个终端从 `http://127.0.0.1:8080/health` 得到 `{"status":"ok", ...}`；用 Ctrl+C 停止。

## 仓库结构

| 路径 | 内容 |
| --- | --- |
| `src/BubbleFighter.Fantasy.Abstractions`、`src/BubbleFighter.Fantasy.Contracts` | 共享插件契约；由 Host 加载一次并共享给每个插件 |
| `src/BubbleFighter.Fantasy.Kernel` | 插件运行时：发现、激活、服务注册表、effect、profile 组合 |
| `src/BubbleFighter.Fantasy.Host` | `bff` 控制台 Host 及其 bootstrap profile |
| `src/Plugins/` | 监听器、方言、游戏功能、持久化、管理与 SDK loader |
| `profiles/` | profile 行、bundle、补丁和逐构建 opcode 表 |
| `packets/` | packet 布局数据与逐构建继承 |
| `sdk/` | 可选的 Python、TypeScript、Go 和 Rust 插件 SDK |
| `tests/` | .NET 测试项目与共享 fixture |
| `tools/` | 验证门禁（`tools/ci`）、本地检查补丁（`tools/local-checks`）与维护脚本 |
| `docs/` | 已发布指南的来源 |
| `docs-site/` | VitePress 站点、页面清单与内容同步 |
| `deploy/` | Docker 文件与 Compose 模板 |

## 进行改动

### 分支与拆分工作

在自己的分支上工作，一次提交只做一个逻辑改动；改动经审查后才进入 `main`。提交消息使用英语：改了什么、为什么改、有什么证据支持。

```sh
git switch -c fix/short-topic
```

### 提供证据

- 随改动添加或更新测试。packet 改动要加字节钉住测试，做到改动前失败、改动后通过。
- 先运行定向测试，再运行完整构建与测试。保留此前失败尝试的输出，不要被重跑覆盖。
- 按代码中的方式引用证据：wire 结论使用带构建前缀的客户端函数地址（`kr-812 sub_810C20`）。采用测试会话证据等级的 opcode 行在该行中注明会话，不写进代码注释。
- 在改动说明中写明运行了什么、能证明什么；自动化测试不能证明真实客户端兼容性。

### 遵循插件与资源规则

- 以插件行添加能力：在 `[Plugin(...)]` 中声明所需与提供的接口，通过 `IPluginContext` 消费，并且只通过它注册。
- 每个注册和外部资源都用 effect 持有，使卸载时可以回退；不要留下无界后台任务或队列。
- 把随部署变化的值放进该行经过校验的选项类（`[Required]`、`[Range]`、`IValidatableOptions`），而不是常量。
- 基于共享契约程序集构建；不要把 `Abstractions` 和 `Contracts` 的私有副本随插件发布。
- 公开类型和成员都要有真实的 XML 摘要。注释说明原因，并指向证据。

### 修改 packet 或 opcode 数据

改动布局前先读 `packets/README.md` 和 `profiles/opcode-policy.md`。简要规则：

- 每个结构以 `kr-812` 基线写一次，一行一个命名字段并标注客户端偏移；有差异的构建在该行上加注解（`except`、`only`、`also`，或自己的替换）。表能表达的内容，绝不要在代码里加逐构建字段表或 `if (build ...)`。
- 用途未经证实的字段保持 `unknown_<offset>`；不要臆造含义。每个 `sub_` 地址都带客户端构建前缀。
- opcode 行记录 `verified`、`inferred`、`unverified` 或 `observed-session` 证据等级与 `name_source`；未知入站 opcode 只记录日志，绝不乐观解析。C2S 与 S2C、游戏与 relay 连接是各自独立的空间。
- 每个 codec 都需要字节长度钉住测试；wire 改动在提交前要对照客户端消费路径检查。
- 用 `bff packets show <build> <structure>`、`bff packets diff <build> <build>` 和 `bff packets check` 核对表。

## 文档改动

`docs/` 是已发布指南的来源；`docs-site/` 通过 `docs-site/pages.json` 中的显式页面清单构建站点。每个已发布页面有四个文件——`X.md`（英文，权威版本）、`X.zh.md`、`X.zh-tw.md` 和 `X.ko.md`——每个文件都需要包含 `title`、`description` 和 `updated` 日期的 front matter。

- 本地链接必须指向已发布的页面源，例如[配置服务器](configuration.md)。外部 URL 和锚点可以保留。
- 不要编辑 `docs-site/content/` 下生成的内容，也不要提交构建产物；两者都被忽略。
- 英文与中文指南要一起更新，并保持站点页面四种翻译同步。
- 在同一次提交中给两份根更新日志（`CHANGELOG.md` 与 `CHANGELOG.zh.md`）添加带日期的条目；日期格式为 `YYYY-MM-DD`，注明时间时使用 Asia/Shanghai。新条目放在旧条目上方。

在站点目录中构建：

```sh
cd docs-site
npm ci
npm test
npm run build
```

同步步骤在构建前运行，校验元数据与链接。front matter 不完整时以 `Missing guide metadata: <path>` 失败，缺少四个文件之一时以 `Missing public guide: <path>` 失败，本地链接未指向已发布页面时以 `Unpublished local link: <target>` 失败。可以参考其他已发布指南，例如[配置](configuration.md)和[插件](plugins.md)。

## 验证等级

| 等级 | 运行内容 | 需要 | 证明什么 |
| --- | --- | --- | --- |
| 快速 | `dotnet build` 与 `dotnet test` | .NET | 改动可编译，.NET 测试套件通过 |
| 公开门禁 | `tools/ci/serial-gates.py --public` | .NET、Python、Node.js/npm、Go、Rust 与 `wasm32-wasip1` | 除本地数据用例外每个被跟踪测试项目、SDK 一致性、packet 表、隔离配置 |
| 完整门禁 | 同一命令去掉 `--public` | 下面列出的本地客户端工作簿 | 全部测试、无跳过；这是发布门禁 |
| 发布审查 | 隔离启动与受影响的真实客户端流程 | 真实游戏客户端 | 客户端验收；任何自动检查都无法替代 |

完整门禁需要以下本地获取的客户端文件；它们不随仓库分发：

| 设置或路径 | 必需文件 |
| --- | --- |
| `client-data/cn-324/` | `Config/ShopItem2.xml` |
| `client-data/kr-1256/data/` | `Packs/Config_00000.pkn`（商店与角色工作簿）与 `Packs/ConfigSh_00000.pkn`（宠物叠加表） |
| `artifacts/shop-tables-survey/id400/xml/` | `Config/ShopItem2.xml` |

如果文件缺失，完整门禁会列出缺少的前置条件并停止：`Local game data is missing for <tests>; provision it or pass --public to run the publicly reproducible gates only.`

如实报告验证结果：

- 每次尝试使用新的结果目录；有内容的 TRX 必须与全部通过的计数一致，没有失败、空结果或跳过结果。被跳过的测试不算通过。
- 报告你实际运行的模式。公开门禁把排除范围写入 `scope.json`，它不是完整发布门禁。
- 不要删除或改写失败结果。保留失败尝试的控制台日志与 TRX。
- 说明结果来自自动检查还是真实游戏客户端。

仓库的串行验证工作流在自托管的 Windows x64 运行器上为 `main` 推送和手动触发运行完整门禁；它不是 pull request 检查，因此提交前要在本地运行门禁。该工作流的检出不得含有 `.env` 或本地 profile 补丁。

## 可审查改动包含什么

- 只做一件事的聚焦 diff，并带有覆盖所改行为的测试。
- 验证证据：命令、结果，以及修复前曾有的失败输出。
- 改动所必需的数据与文档更新：wire 布局的 packet 表、带证据的 opcode 行、英文与中文指南更新、更新日志条目。
- 不包含无关的重格式化、生成文件、构建产物、日志或本地覆盖。

## 哪些会被拒绝

- 凭证、令牌、`.env` 文件、本地 profile 补丁，或出现在代码、文档、日志中的任何机密。
- 私有调查材料、数据包记录、反编译客户端导出或客户端资源。
- 第三方代码、抄来的布局，或提及其他项目和服务器名称的引用。
- 猜测的 packet 名称或字段含义，或基于 `unverified` 证据启用的处理器。
- 无界队列、缓冲区或文件写入，以及用硬编码代替经过校验配置的部署值。
- 屏蔽警告的构建，或被跳过却报告为通过的测试。
- 提交生成的文档内容、构建产物，或把未合并分支中的功能描述为可用。

## 提交流程

1. 推送你的分支并向 `main` 发起 pull request。
2. 说明行为改动、证据以及所运行的验证模式；如果改动修复缺陷，附上失败输出。
3. 确认 diff 不含机密、私有材料或生成内容。
4. 用新的提交或清楚的解释回应审查意见；保持历史可审查。
5. 审查通过后合入 `main`。注意随后一次串行验证运行，因为工作流在 `main` 上运行。

## 后续

- 项目的 GitHub 仓库是 README 记录的入口：https://github.com/Wasabi-FantasyTaleDev/BubbleFighter.Fantasy 。仓库没有记录聊天服务器或论坛。
- [配置](configuration.md) 与[插件](plugins.md) 介绍改动通常涉及的结构。
- [SDK 协议](sdk-protocol.md) 介绍可选语言 SDK，[故障排查](troubleshooting.md) 帮助解决构建或门禁失败。
