---
title: "商店管理"
description: "通过管理界面浏览商店目录、管理来源并把服务器包下载到暂存区。"
updated: 2026-10-25
---

# 商店管理 {#store-management}

商店是用于分发服务器包的管理界面。它浏览已配置的目录来源、预览 GitHub 发布元数据，并把服务器包下载到经过校验的暂存区。本页的一切都受管理员会话保护：通过 WebUI（或 `POST /api/auth/login`）登录后，每个商店请求都需要该会话 Cookie。商店**只做暂存，不做其他任何事**：任何商店操作都不会解压档案、加载插件或替换应用程序路径。一次完成的下载是经过校验的文件清单，而不是已安装的插件。

商店随核心捆绑包发布（`distribution.sources`、`distribution.plans`、`distribution.packages` 与 `distribution.jobs` 四行），`kr-812`、`management` 与 `all` profile 均包含它们。暂存默认落在 `data/distribution-staging`，由独占的持有者锁保护；历史页默认 20 条；空闲空间预留默认为零。来源保存在 `data/distribution-sources.json`——把 `state_path` 留空会使来源列表变为只读。

## 来源 {#sources}

来源卡片按运营者顺序列出已配置的目录来源，顺序很重要：默认选中第一个能提供目录的来源。每行有 ID、显示名、种类（`http`、`alist` 或 `github`）、根 URL 与目录路径。URL 不携带凭据、查询或片段；根路径必须以 `/` 结尾。可以上下移动、增删行，然后一次性保存整个列表。

保存使用乐观修订：编辑器在打开时记录设置修订，保存时一并送回。如果期间另一位管理员保存过，保存会以 `source_revision_conflict` 被拒绝，对话框保持打开，供你核对后重新应用。

## GitHub 镜像 {#github-mirror}

在 GitHub 访问缓慢或受限的地区，商店可以通过公共的 URL 前缀镜像下载 GitHub 发布附件和压缩包。打开**管理来源**并开启 **GitHub 镜像加速**；选择**自动**时，每次下载前都会测量所有镜像并优先尝试最快的可用镜像，也可以在**测速**后指定某个镜像。GitHub 本身始终是最后一条线路。只有文件内容会经过镜像：发布信息和目录始终直接从 GitHub 读取，经镜像收到的每个字节都会与 GitHub 公布的大小和 SHA-256 核对，因此镜像只能让下载失败，无法改变安装的内容。

服务器会检测一次自身地址。若位于中国大陆，第一位打开 WebUI 的管理员会被询问是否开启镜像；该对话框只显示一次，确认之前不会做任何修改。镜像列表、测速文件和测量上限位于 `data/github-mirrors.json`（`distribution.sources` 行的 `github_mirrors_path`，留空即关闭此功能），开关、所选镜像和位置检测结果保存在 `github_mirror_state_path`。

## 浏览目录 {#browsing}

目录页把所有来源的包合并为一张按包标识去重的卡片列表。过滤器可以选择全部来源或单个来源；刷新会重新读取每个目录（或单个来源的）。某个来源失败时绝不会静默消失——它的失败原因（`source_timeout`、`source_unavailable` 或 `catalog_invalid`）会列在卡片上方，健康的来源照常显示。

同一目录中出现同一包的多个发布时，以发布者最高的 `revision` 为准；并列——或多发布组的修订全部未设置——是配置错误，而不是猜测。面向客户端的包可见，但只有服务器目标可以下载：选择它会显示「客户端插件仅提供预览」。

## GitHub 仓库 {#github-repositories}

GitHub 来源从仓库读取目录文档。仓库发布表单预览一个仓库的最新稳定发布——或显式指定的标签——及其附件：草稿与预发布会被拒绝，每个附件 URL 必须属于所请求的仓库与标签，只有当 GitHub 为附件发布了 `sha256:` 摘要时，附件才携带发布者 SHA-256。预览只提供元数据；它不会下载任何内容。

`github_repository` 与 `github_tag` 字段属于目录 schema 3。它们在服务器与启动器的严格目录读取器上都可用，但这是临时的协调格式，不是已发布的共享契约——在与启动器发布版本就格式达成一致之前，不要把生产目录改发为 schema 3。仓库附件只有携带该发布者摘要才能下载；否则下载按钮保持禁用，因为下载执行的逐字节校验没有可比对的对象。

## 下载到暂存区 {#staging}

下载服务器包是三步流程，每一步都钉住所显示的内容：

1. **选择**卡片上的一个下载供给。每个供给都携带选择指纹——对已配置来源与该供给精确内容的 SHA-256——以及它读取自的目录字节的 SHA-256。
2. **规划**下载。计划会针对这些钉住重新校验来源、目录、发布与附件；来源或供给发生变化时，以 `distribution_source_changed` 或 `distribution_selection_changed` 拒绝，而不是下载别的东西。
3. **受理**作业。作业在流式传输时校验每个文件的大小与 SHA-256，把数据块以不透明形式保存在暂存根下，并报告阶段（`preparing`、`transferring`、`staged`）。下载面板轮询进度、按需对已暂存作业做完整回读校验、取消进行中的工作，并删除终态作业及其暂存内容。

通过校验只说明当时暂存卷里有什么。它不授予任何安装权限；磁盘上的后续变化要靠再次校验发现，而不是被默认可信。

## 客户端策略 {#client-plugin-policy}

决定启动器必须、可以或不得使用哪些插件的客户端策略，在已安装服务器的 WebUI 中编辑。保存需要管理员会话之外的 `webui.settings.client_policy` 权限，保存后会重新执行配置应用。这些规则配置的是兼容的启动器——客户端二进制不会安装在服务器上，这些规则也不能证明客户端完整性。管理 API 见[客户端策略](plugins.zh.md#client-plugin-policy)，规则类型见[启动器文档](launcher.zh.md#client-policy)。

## 管理 API {#administration-api}

WebUI 调用的端点与运营者脚本可用的相同。以下所有路径都需要管理员会话 Cookie；非 GET 请求还需要 `X-BFF-Request: 1` 头与同源的 `Origin`。

| 方式与路径 | 用途 |
| --- | --- |
| `GET/PUT /api/distribution/v1/sources` | 读取或携带修订替换来源列表。 |
| `POST /api/distribution/v1/repository-release` | 预览一个 GitHub 发布（仅元数据）。 |
| `GET /api/distribution/v1/catalog?sourceId=` | 合并后的目录快照，或单个来源的。 |
| `POST /api/distribution/v1/plans` · `/validate` · `DELETE` | 钉住、复查与释放一个选择。 |
| `POST /api/distribution/v1/plans/{id}/files` | 校验清单并列出载荷文件。 |
| `GET/POST /api/distribution/v1/jobs` · `GET /{id}` | 历史分页与受理（202，带 `Location` 头）。 |
| `POST /api/distribution/v1/jobs/{id}/cancel` · `/verify` · `DELETE` | 取消、校验暂存回读，或移除终态作业。 |

稳定的错误码（`source_revision_conflict`、`distribution_selection_changed`、`distribution_job_not_staged`、`repository_rate_limited` 等）说明失败原因；HTTP 状态区分错误请求（400）、过期的钉住（409）、上游问题（502/504）与存储或配置问题（503）。

## 商店不做什么 {#limits}

- 不安装、不激活：没有任何路由会解压、加载或重启任何东西。下载不是安装，持久的意图也不是运行中的功能。
- GitHub 镜像按设计直接失败，而不是假装可用。
- 跨来源的清单引用会被拒绝（「暂不支持」）。
- 启动器目前不消费任何这些内容：它不读取商店目录；在其下载引擎发布之前，客户端预设只是对外展示的数据。
