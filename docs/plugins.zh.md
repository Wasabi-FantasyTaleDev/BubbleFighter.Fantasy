---
title: "插件"
description: "理解插件模型，并构建、挂载、配置和重新加载原生 .NET 插件。"
updated: 2026-10-23
---

服务器的一切行为都由插件实现。profile 组合插件行，每个行把一个插件实现和一份配置绑定到一个稳定的实例 id 上，Host 按照各行声明的服务依赖能够满足的顺序激活这些行。本页解释这一模型，说明如何查看 Host 发现并加载了什么，然后从项目文件到可运行的行完整演示第一个原生 .NET 插件。最后介绍配置、生命周期与重新加载、原生插件与可选语言 SDK 的边界、插件作者必须遵守的规则，以及最可能遇到的故障。

**本页适合：** 能够构建解决方案、阅读 C# 与 YAML，并希望在不改动服务器核心的前提下添加行为的开发者。

## 开始之前

- 已完成[安装](installation.md)：`dotnet build BubbleFighter.Fantasy.sln -c Release` 报告零警告、零错误。
- 终端位于仓库根目录；除步骤另有说明外，本页命令都在此执行。
- .NET 10 SDK 可用：`dotnet --version` 输出 10.x 版本。
- 运行步骤需要一份 `.env` 文件，其中 `BFF_MYSQL_PASSWORD` 为非空的临时值。为什么即使停用 MySQL 也需要该变量，见[快速开始](quickstart.md)。
- 本地检查补丁 `tools/local-checks/isolated-memory.patch.yaml` 存在。本页所有本地运行都会应用它，因此不会接触真实数据库。
- 首次运行使用的 `management` profile 只提供管理功能，不需要客户端数据。`kr-812` 这类游戏 profile 还需要该版本对应的客户端数据。

## 插件模型

插件是标记了 `[Plugin("plugin.id")]` 并实现 `BubbleFighter.Fantasy.Abstractions` 中 `IPlugin` 的类。属性中的 id 就是行引用的**插件 id**。profile 提供**插件行**，每个行的 `id` 是**行 id**，即组合后 profile 内唯一的实例标识。同一个实现可以挂载多次：`kr-1256` bundle 把 `net.tcp` 插件挂载为 `game-listener` 和 `relay-listener` 两行，`all` profile 则为每个版本的行加上 `<build>.<行 id>` 前缀。

插件之间看不到彼此的类，它们只通过共享接口类型发布和获取服务。这些接口位于 Host 只加载一次、并共享给所有插件的两个程序集：`BubbleFighter.Fantasy.Abstractions`（`IPlugin`、`IServiceRegistry`、`IEventBus`、`IMessageRouter` 等内核契约）和 `BubbleFighter.Fantasy.Contracts`（消息记录、领域服务等游戏契约）。行声明自己需要什么、发布什么，Host 只在其依赖已注册后激活对应行，因此激活顺序由服务可用性决定，而不是 YAML 中行的先后。

| 声明 | 含义 |
| --- | --- |
| `Requires` | 本行激活前必须已注册的单例服务 |
| `Optional` | 可以缺失的服务；已选中的提供者参与卸载与重新加载顺序 |
| `Provides`、`ConditionalProvides` | 本行在激活时必须发布、或在某些配置下可以发布的服务 |
| `ProvidesMany`、`RequiresMany`、`OptionalMany` | 有序扩展点：发布、要求至少一个、或允许空列表 |
| `Decorates` | 包裹通过 `RegisterDecoratable` 明确允许装饰的单例的装饰层 |
| `Overrides`、`Middleware` | 类型化消息处理器的替换与包装，写作 `rowId:完全限定消息类型` |
| `SupportedBuilds`、`SupportedFamilies` | 针对行所在 profile 的客户端版本兼容性检查 |

注册只有在被拥有之后才算完成。`IPluginContext.Effect(IDisposable)` 和 `Effect(Action)` 注册一个清理器，Host 在清理时按逆序执行；`AcquireEffectAsync` 在异步工厂运行前预留槽位；`CreateResourceScopeAsync` 创建子资源生命周期；`RunTaskAsync` 启动由 Host 监督完成与取消的后台工作。通过 context 的 `Services` 和 `Events` 视图完成的服务注册、事件订阅和消息路由会被自动跟踪，但通过其他途径获得的资源仍必须显式交给 effect。每个行拥有有界的 effect 容量（`lifecycle.effect_capacity`，默认 4096），由本次激活及其资源作用域共享，因此泄漏注册的插件最终会失败，而不是耗尽整个进程。

## 查看已加载的内容

### 1. 列出插件定义

```powershell
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- plugins list --log-level Error
```

`plugins list` 启动 Host 的定义引导流程，在插件根目录下发现原生插件模块，并为每个被接受的定义输出一行制表符分隔的内容：

```text
<插件 id>	<版本>	<逗号分隔的受支持版本>	<来源>
```

第三个列为空表示插件与版本无关（例如 `loader.native`）；声明限制版本时会显示 `kr:1256` 这类区间。第四列是定义来源：由 Host 自身携带的定义显示程序集名，在根目录下发现的模块显示插件目录。`--log-level Error` 可让激活日志不进入列表；否则激活消息与列表共用标准输出。`--plugin-root <dir>` 可重复指定，一旦出现就替换默认根目录列表；默认根目录是 Host 可执行文件旁的 `plugins` 目录（对于 `dotnet run` 构建，即 `src/BubbleFighter.Fantasy.Host/bin/Release/net10.0/` 下的构建输出）。

**检查是否成功：** 命令以代码 0 退出，且你的插件 id 以预期的版本和版本区间出现。该命令只列出 Host 可以挂载的定义，不激活任何行。

### 2. 读取运行时状态

运行中的服务器在 `GET /api/plugins` 暴露行实例：

```powershell
Invoke-RestMethod http://127.0.0.1:8080/api/plugins | Select-Object rowId, plugin, state, error
```

```sh
curl -s http://127.0.0.1:8080/api/plugins
```

每个条目包含 `rowId`、`plugin`、`source`、`state`、`error`、`codeRefreshPolicy`、`codeLifetime`、`restartRequiredForReclamation`、`isRetiring`、`overrides`、`version`、`contractVersions` 和 `supportedBuilds`。状态以字符串写出，取值来自 `PluginState` 枚举。在以 `start` 启动的服务器上，`/api/plugins` 需要已登录的管理员会话；`run --profile` 服务器在回环上无需登录即可应答，详见[快速开始](quickstart.md)。

### 3. 了解插件程序集的位置

构建后的 Host 把插件根目录下的每个子目录当作一个原生模块。规则固定：对于根目录 `<root>`，目录 `<root>/<Name>` 是一个模块，其主程序集必须是 `<root>/<Name>/<Name>.dll`，该模块的其他依赖放在同一目录。仓库构建正好产生这种布局：每个插件项目的构建输出镜像到 `artifacts/plugins/<AssemblyName>/`，Host 构建再用这些镜像文件夹替换输出的 `plugins` 目录。发布脚本产生 `<发布目录>/plugins/<项目名>/`，结构相同。

Host 从自己的默认上下文提供 `BubbleFighter.Fantasy.Abstractions`、`BubbleFighter.Fantasy.Contracts` 和日志抽象程序集，因此这些程序集不能复制到插件目录；仓库的插件构建目标会自动移除它们。插件运行时需要的其他所有内容都必须位于自己的模块目录内。

## 编写第一个原生插件

### 1. 创建项目

创建 `src/Plugins/BubbleFighter.Fantasy.Heartbeat/BubbleFighter.Fantasy.Heartbeat.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <Description>Logs one heartbeat line per activation.</Description>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\BubbleFighter.Fantasy.Abstractions\BubbleFighter.Fantasy.Abstractions.csproj" />
    <ProjectReference Include="..\..\BubbleFighter.Fantasy.Contracts\BubbleFighter.Fantasy.Contracts.csproj" />
  </ItemGroup>
</Project>
```

仓库根目录的 `Directory.Build.props` 提供 `net10.0`、可空引用和警告即错误，`src/Plugins/Directory.Build.targets` 添加 ASP.NET Core 框架引用、剔除 Host 共享程序集并把构建镜像到 `artifacts/plugins/`。仓库外的插件应放在 `<root>/<Name>/` 文件夹中，DLL 位于 `<root>/<Name>/<Name>.dll`，并用 `--plugin-root` 把 Host 指向 `<root>`。

### 2. 编写插件类

创建 `src/Plugins/BubbleFighter.Fantasy.Heartbeat/HeartbeatPlugin.cs`：

```csharp
using System.ComponentModel.DataAnnotations;
using BubbleFighter.Fantasy.Abstractions;
using Microsoft.Extensions.Logging;

namespace Example.Heartbeat;

[Plugin("example.heartbeat", Description = "Logs one heartbeat line per activation.")]
public sealed class HeartbeatPlugin : IPlugin
{
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<HeartbeatOptions>();
        context.Logger.LogInformation("Heartbeat every {IntervalSeconds} s.", options.IntervalSeconds);
        context.Effect(() => context.Logger.LogInformation("Heartbeat stopped."));
        return ValueTask.CompletedTask;
    }
}

public sealed class HeartbeatOptions
{
    [Range(1, 3600)]
    public int IntervalSeconds { get; set; } = 30;
}
```

`Config.Bind<T>()` 读取行的 `config` 映射，把 snake_case 键转换为 PascalCase 属性，执行 DataAnnotations 校验，并抛出一个列出全部失败的 `ConfigException`。传给 `Effect` 的 lambda 在清理时执行一次，此时行的生命周期令牌已经取消；在真实插件中应先获取资源，然后立即注册其清理器。

### 3. 构建

```powershell
dotnet build BubbleFighter.Fantasy.sln -c Release
```

构建成功会输出 `0 Warning(s)` 和 `0 Error(s)`。插件生成到 `artifacts/plugins/BubbleFighter.Fantasy.Heartbeat/`，并且由于 Host 构建会复制该镜像，也出现在 Host 输出的 `plugins/BubbleFighter.Fantasy.Heartbeat/` 中。

**检查是否成功：** 定义现在可以被发现。无需重新构建 Host，只需把列表指向产物镜像：

```powershell
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- plugins list --plugin-root artifacts/plugins --log-level Error
```

输出中应有一行第一列为 `example.heartbeat`。

### 4. 用行挂载

用补丁文件添加行，从而不改动随仓库发布的 profile。创建 `artifacts/heartbeat.patch.yaml`：

```yaml
- insert:
    - id: example.heartbeat
      plugin: example.heartbeat
      config:
        interval_seconds: 15
```

`insert` 追加行；其他补丁操作是 `replace`、`set` 和 `remove`。先应用隔离内存补丁，最后应用 heartbeat 补丁，启动仅管理用途的 profile：

```powershell
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- run --profile management --patch tools/local-checks/isolated-memory.patch.yaml --patch artifacts/heartbeat.patch.yaml
```

Host 挂载并激活该行。预期输出包含下面这种形状的行，随后是插件自己的消息和活动行数：

```text
Row 'example.heartbeat' activated (plugin 'example.heartbeat' version 1.0.0+<commit>; contracts ...; builds independent).
Heartbeat every 15 s.
```

**检查是否成功：** 激活行写明了你的行和插件，插件日志行出现，且没有任何行报告 `Failed` 或 `Pending`。`GET /api/plugins` 显示 `example.heartbeat` 状态为 `Active`。按 Ctrl+C 停止；控制台输出 `Shutdown complete.`，heartbeat 的 effect 会在退出时执行。

### 5. 撤销

删除 `artifacts/heartbeat.patch.yaml` 并停止传入它。删除补丁只移除实例；在你删除模块目录之前，定义仍然可被发现。如果同时删除插件项目，请在重新构建 Host 前删除 `artifacts/plugins/BubbleFighter.Fantasy.Heartbeat/`，因为 Host 会复制产物镜像中存在的所有文件夹。

## 配置行

| 行键 | 用途 |
| --- | --- |
| `id` | 必填，组合后 profile 内唯一的实例 id |
| `plugin` | 必填，`[Plugin]` 声明中的插件 id |
| `config` | 由插件绑定的映射；插件绑定键时执行校验 |
| `disabled` | 为 `true` 时列出但不激活该行；禁用的行不做变量插值 |
| `version` | 发布版本约束，例如 `>=1.2 <2`，与定义版本比较 |
| `parent_id` | 运行时父行：父行先激活，并在子行清理完成后才释放 |
| `isolated_services` | 为该行子树分配全新服务命名空间的接口名 |
| `lifecycle` | 资源容量、清理时限与代码刷新策略；见下一节 |

启用的行中，`config` 的值在组合时支持环境变量插值：`${NAME}` 要求变量存在，`${NAME:-default}` 提供默认值；变量缺失且没有默认值时组合失败，报 `environment variable '<NAME>' is not set and has no default.`。`disabled: true` 的行永不激活，因此其配置完全不做插值。行的 `version` 约束在激活时检查，失败时报 `Plugin '<id>' version <version> does not satisfy row '<row>' constraint '<constraint>'.`。

激活顺序由声明服务的可用性决定。依赖尚未注册的行保持 `Pending`，其 `error` 会列出等待内容，例如 `Waiting for services: <interface>; provider rows: <rows>; override rows: <rows>.`。启动无法推进时服务器报告 `Plugin activation stalled; the following rows have unsatisfiable service dependencies:`，随后每个卡住的行各一行。插件需要服务时应先声明，并且只能通过 context 读取；未声明的访问会抛错，而不会悄悄观察到其他插件的实现。

## 生命周期与重新加载

| 状态 | 含义 |
| --- | --- |
| `Active` | 行已激活，其 effect 生效中 |
| `Pending` | 行是期望状态，但所需服务或选中的提供者尚不可用 |
| `Unloading` | 已关闭 effect 准入，行正在排空 |
| `Unloaded` | 行已停止，没有生效的 effect |
| `Failed` | 激活或拥有的工作失败，行不持有 effect |
| `WorkFailed` | 拥有的后台工作失败；资源在清理完成前仍被持有 |
| `CleanupIncomplete` | 清理超过观察时限；所有权与依赖被保留 |
| `CleanupFailed` | 清理报告错误；拒绝重新启动 |

卸载会关闭 effect 准入，取消本次激活的 `Lifetime` 令牌，等待已接收的事件回调结束，然后按逆序清理 effect。只要仍有活动行依赖某行发布的服务，该行就不能卸载：Host 会抛 `Row '<id>' cannot unload; active row(s) depend on its services: ...`。

`POST /api/plugins/{rowId}/reload` 通过 `IPluginHost.ReloadAsync` 重新加载一行。依赖该行服务的其他行先按激活逆序卸载，然后目标行重新加载，依赖行再按原顺序重新激活；响应包含重新加载的行 id。对从隔离模块加载的原生插件，重新加载还会从磁盘重新加载模块程序集。契约中有两个限制：

- `lifecycle.code_refresh_policy: restart_required` 拒绝原地刷新代码，报 `Row '<id>' requires a process restart for code refresh (lifecycle.code_refresh_policy=restart_required).`。对无法安全刷新的代码应使用该策略。
- 重新加载无法回收进程生命周期的代码。普通原生模块的 `codeLifetime` 为 `ProcessLifetime`，只有后端支持回收时才是 `Collectible`；需要重启才能回收时 `restartRequiredForReclamation` 为 true。重新加载只在后端支持时刷新代码。

失败或超时的清理绝不假装完成。超时使行保持 `CleanupIncomplete`；错误使行保持 `CleanupFailed`，该行保留其所有权与依赖租约，在清理落定前拒绝重新启动。在 `lifecycle` 下设置每行的限制：插件作者通常只涉及 `effect_capacity`（默认 4096）、`teardown_timeout_ms`（默认 10000）和 `code_refresh_policy`（默认 `allow`）。发布定义的行还要声明启动额度（`definition_source_reserve`、`definition_reserve`）；这些额度应保留在发布定义的行上，启动后不要改动。

## 选择原生 .NET 还是语言 SDK

| 能力 | 原生 .NET 插件 | 可选语言 SDK 插件 |
| --- | --- | --- |
| 扩展面 | 所有内核与游戏契约 | 类型化消息、已声明服务、装饰器、有序集合、事件、任务、资源 |
| 加载方式 | 作为原生模块被发现 | `loader.process` 子进程或 `loader.wasm` 模块 |
| 契约访问 | 直接使用接口与类型 | 声明的名称按生成的契约清单解析 |
| 代码刷新 | 按配置的后端重新加载模块 | 进程或模块运行到行或进程结束 |
| 工具链 | 仅 .NET 10 | Python、Node.js、Go 或 Rust，外加对应 loader |

.NET 仍是主要平台：SDK 适配相同的 Host 契约，也不重复实现游戏逻辑，但只覆盖其中一个子集。完整原生对等尚未实现。packet 编解码器、原生连接与类型标识、委托、引用参数、共享契约程序集之外的契约发现，以及接收原生调用方上下文的提供者方法仍需专用桥接。契约清单把每个操作标记为可调用（`rpc_shape`）或可由 SDK 提供者实现（`provider_rpc_shape`），不支持的形式会明确失败，而不会静默降级。

需要编解码器、解码层消息 middleware 或桥接未建模的契约时，使用原生插件。希望用其他语言实现策略、游戏逻辑或服务且能力落在声明范围内时，使用 SDK 插件。[SDK 协议](sdk-protocol.md)记录协议、各语言快速开始、契约清单与一致性测试运行器。

## 插件作者规则

- 不要硬编码可调参数。把限制、间隔和标识作为带边界与校验的行配置暴露；运维无法修改的值就是缺陷。
- 每个资源都要有界。队列、缓存、会话、处理器表与后台工作都需要明确上限；在获取前预留 effect 容量，而不是事后才发现耗尽。
- 明确失败。配置或所需状态不对时在激活中抛错，绝不要吞掉失败并以部分状态继续。Host 会把异常转换为带消息的 `Failed` 行。
- 拥有你注册的东西。获取资源后立即注册 effect，绝不清理不属于你的句柄；清理失败要上报而不是隐藏，未完成的清理按设计保留所有权。
- 异步工作使用有所有权的 API。`AcquireEffectAsync` 与 `RunTaskAsync` 提供取消与完成跟踪；裸的 fire-and-forget 任务会脱离行的生命周期，属于泄漏。
- 保持生命周期回调简短。激活、停用和清理器不得阻塞在无关工作上，也不得等待本行自身排空。
- 为公开类型和成员编写英文 XML 文档。解决方案以警告即错误的方式为所有项目生成文档，未写文档的公开成员会导致构建失败。
- 绝不随插件分发共享契约程序集的私有副本，插件代码也不得引用第三方项目。
- 把消息与事件负载当作值。修改收到的对象不会发布变更，还可能破坏其他插件的视图。

## 故障排查

| 症状 | 检查 | 可能原因与修复 |
| --- | --- | --- |
| `plugins list` 看不到插件 | 传入的根目录；文件夹与 DLL 名称 | 模块目录名必须等于程序集文件名主干，且程序集必须包含 `[Plugin]` 类型。坏模块会报 `Plugin assembly '<path>' defines no [Plugin] type.` 或重复 id 错误。 |
| 行持续 `Pending` | `/api/plugins` 的 `error` 字段与启动日志 | 缺少必需服务。阅读 `Waiting for services: ...; provider rows: ...` 并启用或修复提供者行；override 目标行也必须处于活动状态。 |
| 启动失败并报 `references unknown plugin '<id>'` | 定义来源行是否已挂载并激活 | 原生定义需要包含该模块的插件根目录。SDK 定义需要在同一 profile 中有 `loader.process` 或 `loader.wasm` 行，且该 loader 行必须预留定义来源（`lifecycle.definition_source_reserve`）。 |
| `Declared service(s) were not published: ...` | 本应调用 `Services.Register` 的激活代码 | `Provides` 声明是承诺。在激活中注册服务，并把注册交给 `Effect`。 |
| `Invalid configuration for row '<row>': unknown key '<key>'` | 行的 `config` 键与绑定选项类型 | 绑定拒绝未知键。重命名键或在选项类型中接受它；消息会在一个异常中列出全部失败。 |
| `Duplicate row id '<id>' in profile '<profile>'` | `--dump-config` 组合出的行列表 | 两行共用同一 id，或 `all` profile 中版本作用域的行发生冲突。重命名实例 id。 |
| 版本或版本族不匹配 | 行的 `version`、profile 的 `client_build`、插件支持的区间 | 错误会写明约束：`does not satisfy row ... constraint`、`does not support client build ...` 或 `Plugin requires BubbleFighter.Fantasy.Abstractions ... host provides ...`。让插件与部署的契约保持一致。 |
| 行为 `Failed` 或清理未完成 | `error` 文本与插件的清理器 | 先读第一个失败；`CleanupIncomplete` 和 `CleanupFailed` 保留所有权并拒绝重启，因此应修复失败的清理器或取消路径，而不是盲目重试。 |

如果整个服务器在启动时退出，消息 `Server startup did not converge (<outcome>): <row>: <state> (<error>)` 会列出最先未激活的行。先修复第一个 `Failed` 或 `Pending` 条目，其余通常是连锁结果。更多按症状组织的指引见[故障排查](troubleshooting.md)。

## 后续

继续阅读 [SDK 协议](sdk-protocol.md)，挂载用 Python、TypeScript、Go 或 Rust 编写的插件；然后阅读[参与贡献](contributing.md)了解验证门禁与审查规则。[服务器配置](configuration.md)完整说明 profile、bundle、补丁与环境变量。
## 注册指令与权限 {#register-commands}

需要先通知再断开连接时，`ISession.SendAsync` 只保证消息入队。若会话实现 `ISessionOutputDrain`，应在关闭前使用该操作配置的取消期限等待 `DrainOutputAsync`。此屏障只等待之前的传输写入完成，不确认客户端收到或显示消息。取消只停止等待，不撤回已入队的消息。队列满、会话停止或写入失败都会使等待失败；不要用固定延时或具体协议类代替共享接口。

核心组合中的 `permissions`、`commands` 插件行提供原生插件指令基础。插件在 `Requires` 中声明 `BubbleFighter.Fantasy.Contracts.Administration` 下的 `IPermissionRegistry`、`ICommandRegistry`，先注册权限节点，再注册指令；两份返回句柄都交给 `context.Effect(...)`：

```csharp
context.Effect(context.Services.Get<IPermissionRegistry>().Register(
    new PermissionDefinition("example.ping", "Check whether the example plugin is ready.")));
context.Effect(context.Services.Get<ICommandRegistry>().Register(context,
    new CommandDefinition("ping", ["ready"], "example.ping", "Check plugin readiness.", []),
    (invocation, token) => ValueTask.FromResult(
        new CommandResult(CommandStatus.Success, "Ready."))));
```

指令名称和别名全局唯一，注册时保存元数据快照。注销会移除全部别名并通知正在执行的任务取消；处理器通过注册者上下文的受监管任务执行，插件卸载会取消并等待任务退出。处理器必须响应传入的取消令牌。正常的状态限制返回 `InvalidState`，意外异常则向调用方传播并报告给所属插件。`ICommandRegistry.List(principal)` 返回经过权限过滤的帮助和面板元数据。

`ICommandExecutor.ExecuteAsync` 接收名称和结构化位置参数。`ExecuteTextAsync` 识别 `/`、`!`、`！`，支持双引号参数、有符号整数和末尾的 `RemainingText`；结构化调用将末尾正文作为一个参数传入。正文没有单独的单词数量限制。控制台适配器可明确允许无前缀输入。不会执行 shell 文本。

只有可信适配器可以构造 `CommandCaller`：从认证结果取得稳定账户 ID，代他人操作时保留原始 `Actor` 与实际 `Subject`，不能从请求参数反序列化这些身份。`Language` 使用用户界面语言，空值表示使用部署默认语言。审计事件包含身份和结果，不记录原始参数。原生插件可以注册指令；远程 SDK 注册仍需要回调和生命周期适配。控制台输入和 WebUI 尚未接入此分派器。

在 `ingame` 行的 config 中设置 `shared_chat_commands: true`，将该游戏作用域接入共享聊天指令。等待原生客户端验收期间，此项默认 `false`；关闭时保留原有仅房主可用的 `!start`、`!set`。启用时必须同时提供两项共享指令服务，否则激活失败。

核心组合的 `commands.game` 行将启用的作用域接入同一分派器，也能调用原生插件注册的指令。它在所有游戏作用域中统一注册 `/start`、`/mode <编号>`（别名 `/set`），也支持 `!`、`！` 前缀。`game.room.start`、`game.room.mode` 权限节点默认允许普通玩家使用，只操作自己所在的等待中房间；已配置的模式表仍会拒绝不支持的编号。指令开始会跳过准备标记，原生开始按钮仍要求房主操作。前缀输入在权限拒绝后不会回退到本地处理器。

游戏插件通过 `IGameCommandEndpoints` 注册 `IGameCommandEndpoint`，并持有返回的注销句柄。路由会核对已认证游戏账户与当前座位，拒绝重复的会话归属，并在游戏插件的生命周期内执行。`commands.game.max_endpoints` 默认 512，用于约束同时注册的作用域存储。本行尚未提供地图、管理或房间传送指令。

注销或替换游戏作用域会使先前的路由快照失效。尚未开始执行的调用不能继续使用已注销的端点，也不能自动改投替换后的端点。

权限配置写在 `permissions` 行：

```yaml
config:
  groups:
    - name: players
      allow: [example.ping]
    - name: helpers
      inherits: [players]
      deny: [example.restricted]
  subjects:
    - kind: game
      id: "42"
      groups: [helpers]
    - kind: game
      id: "7"
      operator: true
    - kind: webui
      id: "admin-account-id"
      allow: [webui.access, webui.admin]
```

ID 属于各自认证空间内已经存在的账户；配置不会创建账户或登录凭据。普通身份的拒绝规则优先于个人、继承及通配符授权。规则支持完整节点、`prefix.*` 和 `*`。不存在的组及继承循环会导致激活失败；未注册节点拒绝访问。游戏 OP 和本地控制台拥有全部已注册权限；OP 不授予 WebUI 登录资格。`webui.access` 只代表入口，`webui.admin` 代表 WebUI 超级管理员。权限不能跳过参数或业务状态校验。这些 WebUI 节点供后续适配使用，尚未替换当前 HTTP 授权。

资源配置保护注册表存储和正在执行的任务，不限制游戏功能。`commands` 支持 `max_commands`（512）、`max_input_characters`（4096，同时作为注册名称及参数定义文本预算）、`max_result_characters`（16384）、`max_concurrent`（64）。执行容量占满时立即返回 `Busy`，不建立等待队列。`permissions` 支持 `max_nodes`（2048）与 `max_policy_entries`（65536，账户、组、继承关系和展开规则的合计存储预算）。这些配置经过校验，可按部署调整。

### 在线管理命令 {#online-management-commands}

Contracts 2.61.0 新增原生在线玩家作用域注册。`commands.management` 统一注册 `/list`、`/notice <文本>`、`/disconnect <昵称> [原因]`；`commands.players` 为各版本接入已认证玩家目录和指定协议的通知编码器。等待原生客户端验收期间，两种插件行默认禁用，启用时须同时启用管理行和所选版本的玩家行。玩家行位于 `lobby` 下，要求 `lobby.online_players: true`，必填 `protocol` 指定真实协议。对应的 `dialect.messages` 还须启用 `server_notices: true`；缺少或重复编码器会使激活失败。组合配置中的完整行 ID 以 `--dump-config` 为准。

`/list` 返回在线账号去重后的数量，包含大厅玩家，不公开昵称或网络地址。`game.management.list` 默认允许普通玩家使用。`/notice` 将输入文本发送给各版本的全部已认证游戏连接，报告入队和失败数量，要求 `game.management.notice`。`/disconnect` 要求 `game.management.disconnect`，精确匹配当前昵称，存在多个匹配连接时拒绝执行。游戏 OP 拥有这两项管理权限；游戏调用还会核对调用者的稳定账号是否仍持有当前连接。聊天入口需要 `ingame.shared_chat_commands: true`；控制台和 WebUI 入口尚未接入。

断开通知按目标的 `launcher_language` 显示，并包含可选原因。先入队通知、等待 `ISessionOutputDrain`，再关闭所选连接。通知失败会记录；目标仍有效时继续断开，不会追踪并关闭重新登录的新连接。返回结果会说明通知未完成。这不保证客户端收到或显示消息；公告也只报告入队及部分失败，不代表玩家已阅读。

`commands.management` 支持 `max_scopes`（512 个注册）、`disconnect_notice_timeout_ms`（5000）、`max_notice_characters`（511）。文本预算可调低，上限来自原生通知缓冲区。格式化后的原因过长时，在发送或关闭前拒绝执行。作用域注册由插件效果管理，操作在注册者的受监督生命周期内执行。远程 SDK 尚无原生所有者与回调传输适配，不能宣称指令注册已与原生等效。本批不包含 kick、sudo、OP 持久化编辑、房间传送、WebUI 配置或分发商店。


### 客户端策略 {#client-plugin-policy}

WebUI 可以编辑已安装服务器的[客户端策略](launcher.zh.md#client-policy)：商店来源和按插件的类型化规则。这些规则配置的是兼容的启动器，客户端文件不会安装到服务器。沿用配置文件时，服务器使用所选配置文件中的策略；第一次保存修改后切换为自定义策略，恢复沿用配置文件则回到配置文件自身的策略。

所有路由都要求已登录的 WebUI 账号同时拥有 `webui.access` 与 `webui.settings.client_policy`；`webui.admin` 可授予这两项权限。游戏 OP 不会获得 WebUI 访问权。

| 路由 | 作用 |
|---|---|
| `GET /api/setup/client-policy` | 返回 `mode`（`profile` 或 `custom`）、`revision`、已保存的 `policy`（沿用配置文件时为 null）以及 `active`，即启动器当前收到的策略。 |
| `GET /api/setup/client-policy/rule-types` | 每种规则类型的 `name`、表单 `field`（`none`、`version` 或 `sources`）以及可能冲突的类型 `conflicts`。 |
| `PUT /api/setup/client-policy` | 替换整个策略：`{"policy":{"sources":[],"plugins":[]}}`，或用 `{"policy":null}` 回到配置文件的策略。 |
| `PUT /api/setup/client-policy/sources` | 只替换来源列表。 |
| `PUT /api/setup/client-policy/plugins/{id}` | 替换一个插件的条目 `{"id":"{id}","name":null,"description":null,"rules":[...]}`，其他插件不变。 |
| `DELETE /api/setup/client-policy/plugins/{id}` | 删除一个插件的条目。 |

写入使用现有的同源请求保护，并且必须在 `If-Match` 中发送所编辑的 `revision`。缺少时返回 428；基于旧版本的写入返回 409 并附带当前 `revision`，两个同时打开的编辑器不会互相覆盖。沿用配置文件时的局部写入以当前生效的策略为基础。被拒绝的策略返回 400，包含 `error`（`client_policy_invalid`、`client_policy_unknown_type`、`client_policy_unknown_source`、`client_policy_unknown_build` 或 `client_policy_conflict`），并在可知时给出 `plugin`、`rule` 和冲突的 `other` 规则。规则的版本必须是已配置的、`management` 以外的客户端配置文件。

修改独立于数据库、账号、奖励与客户端数据设置持久保存，再通过现有的受监督配置流程应用。HTTP 202 只表示请求已受理。应用可能使游戏服务重新连接；安装状态会显示进度和失败，可通过现有配置流程修正或重试。
