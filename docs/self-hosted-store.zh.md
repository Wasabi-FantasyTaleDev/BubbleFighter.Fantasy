---
title: "自建商店源"
description: "在明确工具限制的前提下准备和发布启动器客户端、Profile 与插件。"
updated: 2026-10-22
---

# 自建商店源 {#self-hosted-stores}

## 范围与准备 {#prerequisites}

下文示例、字段名和限制对应这份 [Launcher 源码快照](https://github.com/Wasabi-FantasyTaleDev/FantasyTale.Launcher/tree/5f1d4267d0ddd68390dc27fe12f678b06b846e6e)。工具、客户端定义与验证器应使用同一版本。如果使用较新的 Launcher 修订，请遵循该修订随附的格式文档，并用同一修订的验证器检查后再发布。

商店提供一份 UTF-8 TOML 总目录，默认路径为 `FantasyTale/source.toml`。schema 2 同时包含客户端资源、插件资源和客户端 Profile，文件清单与内容独立保存、按需读取。账号认证和登录票据由[服务端启动器 API](launcher.md)负责。添加商店不会授权它更新 Launcher 本体：[Launcher 更新源](https://github.com/Wasabi-FantasyTaleDev/FantasyTale.Launcher/blob/main/docs/launcher-updates.md)单独配置。

下文所有 `tools/publish-store/`、`tools/publish-launcher/`、`games/` 和 `plugin.toml` 路径均属于独立的 [Fantasy Tale Launcher 仓库](https://github.com/Wasabi-FantasyTaleDev/FantasyTale.Launcher)，不属于本服务端仓库。取得其源码后，以根目录 `D:/Launcher` 为占位示例。需要 Python 3.11 或更新版本，上传工具另需 `requests`，目录验证需要 .NET 10 SDK。这些运营者工具不包含在服务端发行包内。

以下 PowerShell 准备步骤假定已安装 Python，并将 `D:/Store` 作为独立工作目录。后续 Python 命令使用此环境；每段可运行命令均明确切换到 Launcher 仓库根目录。

```powershell
Set-Location 'D:/Launcher'
python -m venv D:/Store/.venv
& D:/Store/.venv/Scripts/python.exe -m pip install requests
```

## 托管方式与请求 {#hosting}

| 类型 | 来源根地址 | 总目录请求 |
| --- | --- | --- |
| `http` | `https://files.example.org/` | `GET /FantasyTale/source.toml` |
| `alist` | `https://archive.example.org/` | `POST /api/fs/get`，再 GET 解析出的地址 |

总目录路径可以配置。目录和清单内的路径相对于来源根目录，而非引用文件。HTTP 静态网站必须返回 TOML 原始字节，不是 HTML 预览或 JSON 包装；无需 HEAD，正确的长度和 HTTP Range 支持可实现断点续传。

AList 接收第一段 JSON，并返回第二段所示结构。`size` 必须替换为真实字节数。

```json
{"path":"/FantasyTale/source.toml","password":""}
```

```json
{"code":200,"message":"success","data":{"is_dir":false,"size":1234,"raw_url":"https://storage.example.org/source.toml","sign":""}}
```

缺少 `raw_url` 时，适配器使用 `/d/<path>?sign=<sign>`。解析元数据上限为 64 KiB，下载响应必须返回原始内容。临时地址按需解析，不作为资源身份保存。桌面来源编辑器面向公开源，浏览不应要求管理员凭据。

## 总目录字段 {#catalog-fields}

```toml
schema = 2

[catalog]
id = "community"
name = { en = "Community store", zh-CN = "社区商店" }
```

追加 `[[packages]]` 和 `[[profiles]]`，空数组可省略。未知字段会被拒绝。Launcher 读取器支持最大 4 MiB 的 schema 2、256 个资源发行条目及 512 个 Profile；schema 1 上限 256 KiB，不含 Profile 或内联插件定义。单个 Profile 和内联插件定义各不超过 256 KiB。

| 资源字段 | 含义 |
| --- | --- |
| `id`、`kind`、`game` | 资源标识、类型和游戏；本文使用 `client` 或 `plugin` |
| `builds` | 客户端恰好一个版本；商店插件列出明确支持的版本 ID |
| `version` | 客户端可选更新标签；插件使用 SemVer |
| `name`、`description` | 本地化文本，名称必须包含英文；描述可选 |
| `publisher` | 必填展示名称，不代表身份认证 |
| `size` | 清单内需下载的总字节数 |
| `manifest`、`manifest_sha256` | 清单相对路径及其原始字节的 SHA-256 |
| `update_mode` | 客户端当前快照使用 `files`；版本化发行使用 `release` |
| `revision` | 可选排序值；正数修订号在同一资源内唯一 |
| `authors`、`uploader` | 可选作者列表和上传者展示名称 |
| `plugin_definition` | 仅 schema 2：内联完整插件定义 |
| `minimum_launcher_version` | 仅 schema 2：可选最低应用 SemVer |

资源身份绑定其配置来源。其他来源不能仅凭相同 ID 替换已安装资源。相对路径禁止 URL、目录穿越、反斜杠、百分号转义、查询参数及片段。大小和哈希须根据真实内容生成。

## 文件清单 {#file-manifests}

下面仅示意文件同步客户端的清单，不能作为真实发行元数据：示例大小和两处 `<file-sha256>` 必须换成生成值。总目录的 `manifest_sha256` 对整份 UTF-8 清单求哈希，每项 `[[files]]` 则对自身内容求哈希。

```toml
schema = 1

[release]
id = "bubblefighter.community.client"
game = "bubblefighter"
build = "cn-279"

[[files]]
path = "BubbleFighter.exe"
source_path = "FantasyTale/files/<file-sha256>"
size = 1234
sha256 = "<file-sha256>"
```

`path` 是相对于安装目录的目标路径，`source_path` 相对于来源根目录。插件清单使用 `[release] version` 和 `builds`，替代客户端的单数 `build`。清单上限为 256 KiB。完整输出应由工具生成，压缩包清单的额外字段见 [Launcher 商店格式](https://github.com/Wasabi-FantasyTaleDev/FantasyTale.Launcher/blob/main/docs/store-sources.md)。

## 本地生成客户端 {#clients}

满足上述依赖后，将示例保存为 `D:/Store/publish.toml`。输入为干净的可分发目录，输出必须位于所有输入目录之外。发布配置中的相对路径从配置文件所在目录解析。

```toml
[store]
id = "community"
name = "Community store"
output = "./generated"
storage_gib = 200

[[clients]]
game = "bubblefighter"
build = "cn-279"
name = "China 2011"
directory = "./clients/cn-279"
exclude = ["logs/*", "*.log", "*.dmp"]
```

```powershell
Set-Location 'D:/Launcher'
& D:/Store/.venv/Scripts/python.exe tools/publish-store/publish_store.py --config D:/Store/publish.toml
```

工具生成 `D:/Store/generated/FantasyTale/source.toml`、不可变哈希清单和按内容寻址的文件。它输出 **schema 1**，用配置中的客户端／服务端条目替换自身总目录，不会合并已有插件或 Profile。不要将正在使用的统一目录设为输出。`--watch 30` 会反复替换；`--full-check` 重新计算输入哈希而不信任元数据缓存。生成目录应与发布树分开。仅增加目录条目无法实现新游戏，其模块与有效版本定义必须已支持它。

## 组合 Profile 并验证 {#profiles}

从 Launcher 仓库内受支持的 `games/<game>/builds/<build>.toml` 开始。`[build]` 包含标识、可执行文件、显示名称、分支、`year`、`client_version` 及可选 `release_version`，可选 `region` 为两个大写字母。在上方链接的快照中，`client_version` 是整数，可选 `wire_version` 是协议标识字符串。请保留该 Launcher 修订随附的字段名与类型：此解析器不接受 `build_date`、`protocol_version` 或 `patch_version`。`[launch]` 使用 `login`、`arguments`、`launcher_login`（`required`、`optional` 或 `none`）及 `requires_elevation` 定义模块专用的启动和登录策略。`[plugins]` 的 ID 数组中，`required` 强制启用，`recommended` 提供建议，`default` 提供默认选择。不要猜测指纹或登录行为。

将配置保存为 `D:/Store/profiles.toml`，被引用的定义位于 `D:/Store/profiles/cn-279.toml`。预先准备含全部要保留资源的 `D:/Store/package-catalog.toml`。命令从 Launcher 根目录运行，需要 Python 3.11+ 和 .NET 10 SDK。

```toml
[[profiles]]
definition = "./profiles/cn-279.toml"
revision = 1
```

```powershell
Set-Location 'D:/Launcher'
& D:/Store/.venv/Scripts/python.exe tools/publish-store/compose_catalog.py --catalog D:/Store/package-catalog.toml --config D:/Store/profiles.toml --output D:/Store/unified-source.toml
dotnet run --project tools/publish-launcher -c Release -- --validate-catalog D:/Store/unified-source.toml
```

**组合配置**中的 `definition` 为文件路径，**输出总目录**中则内嵌完整文本，同时包含 `game`、`build` 和正数 `revision`。定义有变必须增加修订号。组合器接受 schema 1／2，保留输入资源条目及已有 Profile，在不存在的新输出路径生成 schema 2。它合并配置中的 Profile，不会合并多个资源目录。组合前必须整合新客户端快照和所有要保留的插件／资源条目；单用刚生成的客户端目录会丢失其他条目。可选 `--legacy-output <new-path>` 另外输出不含 Profile 和内联插件元数据的 schema 1 投影。

## 插件作者与发布 {#plugins}

Launcher 插件与 .NET 服务端插件不同。分发目录的根部包含 `plugin.toml` 和运行文件。`[plugin]` 声明 ID、游戏、SemVer、本地化名称及可选 `authors`；`[compatibility]` 声明版本支持。参见 [Launcher hook 插件格式](https://github.com/Wasabi-FantasyTaleDev/FantasyTale.Launcher/blob/main/docs/hook/plugin-format.md)和[文件替换插件](https://github.com/Wasabi-FantasyTaleDev/FantasyTale.Launcher/blob/main/docs/file-plugins.md)。同一插件 ID 可以支持多个版本；即使插件定义允许选择器，商店条目的 `builds` 仍须写明确版本 ID。

`authors` 表示作者，`uploader` 为可选上传者名称，空值省略。两者都不认证身份。内容有变须使用新 SemVer，不要为了补元数据覆盖已发布版本。

下方命令会**实际远端发布**，没有 CLI 预演模式。请使用独立准备、已有 schema 1 总目录的 AList 暂存来源、可写驱动，并满足上述依赖。仅通过进程环境提供 `FT_ALIST_USERNAME` 和 `FT_ALIST_PASSWORD`。审核目标后再替换 `staging.example.org`。

```powershell
Set-Location 'D:/Launcher'
& D:/Store/.venv/Scripts/python.exe tools/publish-store/publish_plugins.py --package D:/Store/plugin-package --output D:/Store/plugin-output --base-url https://staging.example.org/
```

发布器先上传内容、清单，再上传总目录。它将生成的资源文本追加到远端目录，已有 `(id, version)` 元数据改变时拒绝发布。即使输入声明 schema 2，输出仍受 **256 KiB、256 个发行条目**限制。每个插件包上限为 256 个文件、128 MiB。它保留原有文本，但不会完整验证 schema 2，也不会生成总目录的 `plugin_definition`／`minimum_launcher_version` 字段。它不是通用 schema 2 编辑器或纯本地准备命令。

统一组合前，保留完整暂存目录及所有引用对象。内联元数据必须在 schema 2 候选中明确加入并验证：内嵌 ID／游戏／版本须与资源一致，单独填写的最低版本须与内嵌 `[plugin] minimum_launcher_version` 一致。组合器保留已有资源字段，不会自动生成。下载元数据不会执行插件代码。

## 工具兼容性 {#tool-compatibility}

| 工具或读取器 | 实际行为 |
| --- | --- |
| Launcher 总目录读取器 | schema 1：256 KiB；schema 2：4 MiB；最多 256 个资源／512 个 Profile |
| `publish_store.py` | 本地 schema 1 生成器；替换自身总目录，不合并资源／Profile |
| `publish_plugins.py` | AList 远端发布器；文本追加，256 KiB／256 个发行条目；不生成内联元数据 |
| `compose_catalog.py` | schema 1／2 输入，最大 4 MiB schema 2 输出；保留资源、合并 Profile |
| `upload_alist.py` | 完整引用图；schema 2 总目录最大 4 MiB，清单最大 256 KiB，最多 4096 个对象 |

## 安全发布顺序 {#publication}

1. 保留现有总目录及对象，协调为单一写入者。
2. 在独立目录生成客户端。使用插件 CLI 时先发布到独立暂存来源。组装完整目标资源目录，保留无关条目和已有 Profile；客户端生成器无法合并它们。
3. 组合新统一目录并运行上述验证器。在独立发布根目录（如 `D:/Store/release`）准备其**全部**清单和内容，将已验证目录放到根部的 `FantasyTale/source.toml`。AList 上传器也要求此前引用的对象在本地存在。
4. HTTP 先复制内容，再复制不可变清单，最后原子替换总目录。保留旧对象，供已绑定旧清单的下载使用。
5. AList 仅在上述准备完成后运行下方发布命令，它不是预演。Python 环境需要 `requests`，进程环境需要 `FT_ALIST_USERNAME`／`FT_ALIST_PASSWORD`。

```powershell
Set-Location 'D:/Launcher'
& D:/Store/.venv/Scripts/python.exe tools/publish-store/upload_alist.py --input D:/Store/release --base-url https://archive.example.org/ --workers 2 --verify-timeout-seconds 900
```

上传器检查本地哈希、验证匿名远端内容，先内容后清单，检查旧总目录的公开摘要后再最后替换。该乐观检查不等于原子的多写入者锁。`--workers` 为 1–4（默认 2），`--verify-timeout-seconds` 为每对象 30–3600 秒（默认 900）。`--catalog` 修改目录路径，`--no-proxy` 仅影响本次上传进程。AList 驱动必须可写，最终文件必须能匿名读取。其他选项见 [Launcher 上传文档](https://github.com/Wasabi-FantasyTaleDev/FantasyTale.Launcher/blob/main/docs/store-sources.md#uploading-a-generated-catalog-to-alist)。

## 验收与边界 {#verification}

使用匿名消费者获取并验证总目录，读取一份引用清单，对照其原始字节 SHA-256，再安装到独立测试目录。检查 Profile 身份、必装／推荐／默认插件选择和多版本兼容性。哈希证明内容与所选目录一致，不证明发布者身份。不要分发账号数据、凭据、票据、本地覆盖配置、诊断或私有研究资料。本文描述工具契约，不代表特定商店、桌面流程或真实客户端启动已验收。

## 服务端商店下载 {#server-store}

服务端 WebUI 的**商店**通过普通插件行 `distribution.sources`、`distribution.plans`、`distribution.packages` 和 `distribution.jobs` 提供 HTTP、AList、GitHub 目录浏览、来源选择及验证下载。这与上文桌面启动器的流程不同：文件只进入隔离暂存区，不会安装、激活、更新或回滚正在运行的服务端及插件。

1. 打开**商店 → 管理来源**，填写唯一来源标识、显示名称、类型、根网址及目录文件路径。网址不能包含凭据。保存后的来源顺序决定同一软件包的默认来源；失败的来源会显示错误。
2. 浏览目录并选择下载来源。GitHub Release 还需要选择具有发布者 SHA-256 的附件。通过仓库网址查询 Release 只是预览，不会直接创建可安装软件包；客户端软件包在此也仅供预览。
3. 点击**下载到暂存区**。选择绑定当前来源修订和准确的发布信息，下载会核对清单及文件哈希并记录进度。关闭浏览器不会取消已受理任务；请使用任务的取消操作。
4. **已暂存**表示不透明文件已经验证，不表示插件已安装。可以重新校验文件、查看历史任务或明确删除暂存内容。失败或取消会清理不完整文件并保留结果；重启后未结束的任务显示为中断。

来源默认保存到 `data/distribution-sources.json`，下载保存在 `data/distribution-staging`。可配置的行选项包括 `maximum_document_bytes`（4194304）、`maximum_sources`（512）、元数据超时 `request_timeout_ms`（30000）和下载超时 `transfer_timeout_ms`（1800000）。`maximum_payload_bytes` 可选；下载仍受声明的准确大小及可用磁盘空间约束。`free_space_reserve_bytes` 默认 0，历史分页 `history_page_size` 默认 20。这些是部署选项，不是包格式要求。已完成下载不会自动删除。

服务端应用清单使用既有 schema 1 格式。服务端插件清单需要显式启用实验读取器 `distribution.packages.experimental_release_format: true`（默认 false）；该提案尚非启动器的正式共享规范。GitHub 附件的发布者哈希不证明平台兼容性。GitHub 镜像尚未实现，启用 `use_github_mirror` 会明确报错。安装后的商店 API 使用管理员会话及既有请求保护，尚无逐操作的商店权限节点；命令模式管理仍仅监听回环地址。[客户端插件启动策略](plugins.md#client-plugin-policy)有独立的权限要求。
