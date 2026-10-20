---
title: "SDK 协议"
description: "将可选的其他语言 SDK 插件连接到 Host 进程，或用 .NET 编写原生插件。"
updated: 2026-10-18
---

服务器的主要实现是 .NET 10 上的 C#。`sdk/` 中的 SDK 是面向 Python、TypeScript、Go 和 Rust 的可选插件客户端：它们适配相同的 Host 契约，使插件可以用其他语言编写，而无需重新实现服务器。进程插件通过标准输入输出上有界的、按行分隔的 JSON 协议连接 Host。Go 和 Rust 模块也可以在 Host 内以 WebAssembly 运行。

本页先说明 SDK 是什么、不是什么，给出每种语言一个可运行的快速开始，并说明如何测试插件和诊断常见故障；然后详细记录 loader、激活、取消、消息、服务、任务、资源和事件契约。SDK 从本仓库直接使用，不从包仓库安装。

## 本页涵盖的内容

- 两个 loader：用于子进程的 `loader.process` 和用于沙箱 WebAssembly 模块的 `loader.wasm`。
- SDK 插件目前能做什么：类型化消息、显式声明的替换与 middleware、显式声明的服务调用、提供者与装饰器、有序集合、共享事件、有所有权的任务与资源。
- 如何在每种随附语言中构建、运行、测试和挂载插件。
- 协议版本协商、生成的契约清单与一致性测试如何工作。
- 协议与平台的故障模式。

**本页适合：** 已阅读[插件](plugins.md)、希望用 C# 之外的语言添加行为的开发者，或需要精确 loader 与协议契约的读者。

## 开始之前

- 已完成[安装](installation.md)并构建了检出，终端位于仓库根目录。
- 所选语言的 SDK 工具链：当前 Python 3 解释器（一致性测试运行器调用 `python`）、Node.js 20 或更高版本与 npm（`sdk/typescript/package.json` 声明 `node >=20`）、Go 1.22 或更高版本（`sdk/go/go.mod`），或支持 edition 2021 的 Rust 工具链与 cargo（`sdk/rust/Cargo.toml`）。只需要一种，不需要四种都装。WebAssembly 模块还需 Rust 的 `wasm32-wasip1` 或 Go 的 `wasip1` GOOS。
- 运行步骤需要：一份 `.env` 文件，其中 `BFF_MYSQL_PASSWORD` 为非空的临时值；以及本地检查补丁 `tools/local-checks/isolated-memory.patch.yaml`，详见[快速开始](quickstart.md)。
- 已了解[插件](plugins.md)中的插件模型：插件 id、行 id、声明与 effect。

## 快速开始

四个快速开始用各自语言构建同一个小插件。它只在激活时记录一行日志，不处理任何消息，因此在仅管理用途的 `management` profile 上运行，不需要客户端数据。Python 与 Go SDK 没有第三方运行库依赖；TypeScript 与 Rust SDK 分别用 `npm ci` 和 `cargo` 获取构建依赖。

| SDK | 位置 | 协议 |
| --- | --- | --- |
| Python | `sdk/python` | 基于 stdio 的 JSON-RPC，协议 v1 与 v2 |
| TypeScript | `sdk/typescript` | 基于 stdio 的 JSON-RPC，协议 v1 与 v2 |
| Go | `sdk/go` | 基于 stdio 的 JSON-RPC，协议 v1 与 v2；可用 `wasip1` 构建供 `loader.wasm` |
| Rust | `sdk/rust` | 基于 stdio 的 JSON-RPC，协议 v1 与 v2；可用 `wasm32-wasip1` 构建供 `loader.wasm` |

### Python

Python SDK 是 `sdk/python` 中仅依赖标准库的 `bff_plugin` 包。在仓库根目录运行其单元测试：

```sh
python -m unittest discover -s sdk/python/tests -p "test_*.py"
```

用共享协议 fixture 运行 Python 一致性插件：

```sh
python sdk/python/tests/run_conformance.py
```

创建 `sdk/python/examples/hello.py`：

```python
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from bff_plugin import Context, plugin, run


@plugin("example.hello", version="1.0.0", description="Logs one message.")
class Hello:
    async def activate(self, ctx: Context) -> None:
        ctx.logger.info("hello from the Python SDK")


if __name__ == "__main__":
    run()
```

loader 条目从 `sdk/python/examples` 启动该模块，因此 `bff_plugin` 可被导入。`@plugin` 声明中的 `config` schema 是可选的；本插件没有声明。将此补丁保存为 `artifacts/sdk-hello.patch.yaml`：

```yaml
- insert:
    - id: hello.python
      plugin: loader.process
      lifecycle:
        definition_source_reserve: 1
        definition_reserve: 16
      config:
        plugins:
          - command: python
            args: ["-m", "hello"]
            cwd: sdk/python/examples
            env:
              PYTHONUNBUFFERED: "1"
            source_id: hello-python
    - id: hello.binding
      plugin: example.hello
```

### TypeScript

TypeScript SDK 是 `sdk/typescript` 中的本地 npm 包。在该目录安装构建依赖、构建并运行其自带测试脚本：

```sh
cd sdk/typescript
npm ci
npm test
```

`npm test` 会构建包，并运行线格式、生命周期和一致性测试套件。创建 `sdk/typescript/examples/hello.ts`：

```ts
import { Context, plugin, run } from "../src/index.js";

@plugin("example.hello", { version: "1.0.0", description: "Logs one message." })
class Hello {
  async activate(ctx: Context): Promise<void> {
    ctx.logger.info("hello from the TypeScript SDK");
  }
}

run();
```

`npm run build` 把它编译到 `dist/examples/hello.js`。将此补丁保存为 `artifacts/sdk-hello.patch.yaml`，覆盖 Python 补丁：

```yaml
- insert:
    - id: hello.typescript
      plugin: loader.process
      lifecycle:
        definition_source_reserve: 1
        definition_reserve: 16
      config:
        plugins:
          - command: node
            args: ["dist/examples/hello.js"]
            cwd: sdk/typescript
            source_id: hello-typescript
    - id: hello.binding
      plugin: example.hello
```

### Go

Go SDK 是 `sdk/go` 中的 `bffplugin` 包。在该目录运行其测试：

```sh
cd sdk/go
go test ./...
```

创建 `sdk/go/examples/hello/main.go`：

```go
package main

import (
	"bffplugin/bffplugin"
	"os"
)

type hello struct{}

func (*hello) Activate(ctx *bffplugin.Context) error {
	ctx.Logger.Info("hello from the Go SDK", nil)
	return nil
}

func main() {
	bffplugin.Register(bffplugin.Definition{ID: "example.hello", Version: "1.0.0"}, func() bffplugin.Plugin { return &hello{} })
	os.Exit(bffplugin.Run())
}
```

把二进制构建到 `sdk/go`，并将此补丁保存为 `artifacts/sdk-hello.patch.yaml`，覆盖 TypeScript 补丁：

```sh
cd sdk/go
go build -o hello ./examples/hello
```

```yaml
- insert:
    - id: hello.go
      plugin: loader.process
      lifecycle:
        definition_source_reserve: 1
        definition_reserve: 16
      config:
        plugins:
          - command: sdk/go/hello
            cwd: .
            source_id: hello-go
    - id: hello.binding
      plugin: example.hello
```

在 Windows 上输出文件是 `hello.exe`；使用 `go build -o hello.exe ./examples/hello`，并把 `command` 设为 `sdk/go/hello.exe`。

### Rust

Rust SDK 是 `sdk/rust` 中的 `bff-plugin` crate。在该目录运行其测试：

```sh
cd sdk/rust
cargo test --release
```

创建 `sdk/rust/examples/hello.rs`：

```rust
use bff_plugin::{Context, Definition, Error, Plugin, Server};

struct Hello;

impl Plugin for Hello {
    fn activate(&mut self, ctx: &Context) -> Result<(), Error> {
        ctx.info("hello from the Rust SDK");
        Ok(())
    }
}

fn main() {
    std::process::exit(
        Server::new()
            .register(
                Definition {
                    id: "example.hello".into(),
                    version: "1.0.0".into(),
                    ..Default::default()
                },
                || Hello,
            )
            .run(),
    );
}
```

构建它，并将此补丁保存为 `artifacts/sdk-hello.patch.yaml`，覆盖 Go 补丁：

```sh
cd sdk/rust
cargo build --release --example hello
```

```yaml
- insert:
    - id: hello.rust
      plugin: loader.process
      lifecycle:
        definition_source_reserve: 1
        definition_reserve: 16
      config:
        plugins:
          - command: sdk/rust/target/release/examples/hello
            cwd: .
            source_id: hello-rust
    - id: hello.binding
      plugin: example.hello
```

在 Windows 上，二进制是同一目录下的 `hello.exe`。

### 挂载插件

上面每种语言的章节都保存同一种补丁：一个 `insert` 操作，加入一个 `loader.process` 行和一个绑定 `example.hello` 的普通行。用第二个 `--patch` 应用你所选语言的补丁；隔离内存补丁必须放在前面，这样不会接触真实数据库：

```powershell
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- run --profile management --patch tools/local-checks/isolated-memory.patch.yaml --patch artifacts/sdk-hello.patch.yaml
```

loader 行上的两项启动额度是必需的。`definition_source_reserve: 1` 为该进程条目预留一个定义来源；没有它，启动会以 `references unknown plugin 'example.hello'` 拒绝绑定行。`definition_reserve: 16` 为该来源将要发布的定义预留空间；引导 loader 行也是用同样方式预留容量。行 id 可以任意取名，而 `source_id` 是定义来源的身份，在一次 loader 激活内必须唯一；未命名的进程条目默认为 `process-<index>`。相对的 `command`、`args` 路径和 `cwd` 相对 `base_dir` 解析，后者默认为 Host 的工作目录（上面命令中的仓库根目录）；不带目录的命令名从 `PATH` 查找。

loader 行必须处于活动状态，定义才会发布：在此之前绑定 `example.hello` 的行会保持 `Pending`。若要在打包 profile 中长期挂载该插件，可把这些行复制进该 profile 自己的补丁文件，而不必在命令行上传入 `--patch`。

启动日志会依次显示 loader 行激活、SDK 来源自报信息、绑定行激活：

```text
Row 'hello.python' activated (plugin 'loader.process' version 1.0.0+<commit>; contracts ...; builds independent).
SDK plugin source 'hello-python' runs SDK {"name":"...","version":"..."}.
Row 'hello.binding' activated (plugin 'example.hello' version 1.0.0; contracts ...; builds independent).
hello from the Python SDK
```

**检查是否成功：** loader 行和绑定行都出现，插件消息被记录，且 `GET /api/plugins` 显示 `hello.binding` 状态为 `Active`，loader 行同样为 `Active`。如果绑定行为 `Failed` 或一直 `Pending`，其 `error` 字段会携带 SDK 或配置错误。按 Ctrl+C 停止；在该 loader 服务的每个行都排空后，loader 才会停止子进程。

该快速开始使用相对的 `command` 路径和 `cwd`，因此在仓库根目录即可运行。WebAssembly 插件改用 `loader.wasm` 行和 `module` 条目：模块是导出 `_start` 的 WASI 命令，导入 `bff.recv`、`bff.send` 和 `bff.log`，不接收参数、环境变量、文件或 stdio。其线性内存受 `max_memory_bytes` 限制（默认 64 MiB）。Go 模块用 `GOOS=wasip1 GOARCH=wasm` 构建，Rust 模块用 `wasm32-wasip1` 目标构建。

注册消息处理器的插件还需要一个游戏 profile，其类型化 dialect 为该协议和消息类型提供入站绑定。随附的 echo 示例声明了 `handles: ["LoginRequested"]` 和 `supported_builds: ["kr:812"]`，因此应挂载到有客户端数据的 `kr-812` profile，而不是 `management`。

## 能力协商与兼容性

Host 通过一次 `initialize` 请求开始对话，SDK 回答它选择的协议版本。参数携带插件决定自己可以声明什么所需的全部信息：

| initialize 字段 | 含义 |
| --- | --- |
| `protocol_version`、`protocol_versions` | 旧的单版本字段与完整的提供列表；Host 提供 `1` 和 `2` |
| `features` | 本构建实现的 Host 通知与回调，包括 `lifecycle.cancel` |
| `max_in_flight` | 单个方向上未完成请求的上限 |
| `max_reentry_depth` | 单线程模块嵌套 Host 请求帧的上限 |
| `host_version`、`contracts` | Host 发行版及其提供的共享契约程序集版本 |

协议 v1 覆盖最初的接口面：`initialize`、`describe`、`activate`、`deactivate`、`deliver`、`messages.on`、`messages.override`、`messages.send`、`effect.dispose` 和 `log`。协议 v2 增加了服务调用与提供者、有序集合、事件、任务、资源、消息 middleware，以及提前的 `lifecycle.cancel` 通知与 `lifecycle.ready`。超出协商版本的声明会失败而不是被忽略：Host 会报告 `provides, provides_many and decorates require protocol v2.`、`requires_many and optional_many require protocol v2.`、`event declarations require protocol v2.`，或由 SDK 拒绝未知方法。

Host 缺少某项能力本身不是错误。依赖某项能力前先检查文档给出的能力标志，例如 Python 的 `ctx.supports_lifetime_cancellation`、TypeScript 的 `ctx.supportsLifetimeCancellation`、Go 的 `ctx.SupportsLifetimeCancellation()` 和 Rust 的 `ctx.supports_lifetime_cancellation()`。旧 Host 仍可通过本地回滚、停用和传输中断路径工作，但无法在停用前送达取消。

`describe` 的回答会与共享契约做校验。声明中的名称必须解析为共享契约程序集里唯一的消息记录、共享抽象或契约中唯一的服务接口，family 必须是 `legacy` 或 `modern`。未知名称会使整个来源失败，错误会列出允许的契约，因此拼写错误在行激活时就会暴露，而不是等到消息到达。

### 查看契约清单

为当前 checkout 生成清单：

```sh
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- contracts schema --out artifacts/contracts.json
```

不带 `--out` 时清单写入标准输出；`contracts` 命令的日志写入标准错误，使文档保持干净。清单描述 SDK 插件可以声明和调用的内容：

| 清单字段 | 内容 |
| --- | --- |
| `schema_version` | 清单格式版本 |
| `runtime_protocol_versions`、`runtime_features` | Host 声明的协议版本与能力列表 |
| `messages`、`events`、`event_result_types` | 类型化消息记录、事件负载与事件结果类型及其正文形状 |
| `services` | 服务接口及其操作、参数和结果形状 |
| `types` | 被引用的记录与枚举形状 |

每个服务操作都带有 `rpc_shape` 和 `provider_rpc_shape`。SDK 插件可以调用该操作时 `rpc_shape` 为 `supported`，SDK 插件可以实现它时 `provider_rpc_shape` 为 `supported`；每个不支持项带有 `unsupported_reason` 或 `provider_unsupported_reason`。请使用清单而不是猜测：提供者必须实现整个接口，无歧义时使用方法名，重载使用完整 schema method id，属性访问器使用 `get_ReplyAfterInventory` 这类名称。

## 测试插件

仓库提供一个共享一致性运行器和一套所有 SDK 共用的 fixture 格式。在仓库根目录运行：

```sh
python sdk/run-conformance.py
```

运行器会构建并单元测试每个工具链存在的 SDK，然后用共享 fixture（位于 `tests/fixtures/sdk-protocol`）和 protocol-v2 fixture 测试该 SDK 的插件。每个套件输出 `PASS`、`FAIL` 或 `SKIP` 与一行摘要；任一失败时退出码非零；因工具链缺失而跳过的语言不算通过。这是当前协议的兼容性参考。

fixture 是 JSON-lines 文件。第一行是头部，含 `ignore` 列表，其中的点分路径在比较前从两侧移除。之后每一行是 `{"dir": "h2p", ...}`（Host 发给插件）、`{"dir": "p2h", ...}`（插件预期回复）或 `{"expect_exit": N}`。运行器为每个 fixture 启动新的插件进程，把每个 Host 消息写入其标准输入，应用 ignore 列表后把插件输出的每一行与预期回复精确比较，并要求干净退出。标识与句柄按精确值比较。用共享 Python 运行器让自有插件跑同一套 fixture：

```sh
python sdk/python/tests/run_conformance.py --fixtures <fixture-dir> --command <plugin-executable> [args...]
```

**检查是否成功：** 运行器以 `<passed>/<total> fixtures passed` 结束并退出 0；不匹配时会打印第一个失败行的预期与实际 JSON，以及插件的标准错误。

## 常见故障

| 症状 | 检查 | 可能原因与修复 |
| --- | --- | --- |
| 来源失败并报 `answered initialize without a supported protocol_version (1 or 2).` | 你的运行时对 `protocol_versions` 的回答 | 手写运行时必须从提供的列表中选择 `1` 或 `2`。随附 SDK 总是回答受支持的版本。 |
| `stdout carried a line that is not JSON ...; stdout is reserved for the protocol` | 进程写入标准输出的任何内容 | 标准输出是协议通道。Python 会把 `print`、TypeScript 会把 `console.*` 重定向到标准错误；Go 与 Rust 应把诊断写入标准错误或通过 SDK logger，绝不能写标准输出。 |
| 启动失败并报 `SDK plugin process '<command>' could not start in '<cwd>'` | `command`、`args`、`cwd` 与 `PATH` | 可执行文件或工作目录不对。含目录分隔符的路径相对 `base_dir` 解析；裸命令名需要 `PATH`。 |
| 行失败并报 `requires_many and optional_many require protocol v2.` 或类似 v2 消息 | 协商的协议版本与声明字段 | 该声明需要协议 v2。协商 v2 或移除声明；错误会指明字段类别。 |
| 消息处理器失败并报 `protocol '<p>' has no inbound binding for '<type>'` | profile 的 dialect 行 | 该协议与消息类型的类型化 dialect 必须在同一 profile 中激活；SDK 自己不能解码 packet。 |
| 回调超时：`did not answer '<method>' within <n> ms (request_timeout_ms).` | 插件是否阻塞，以及变更是否可能已提交 | 超时会使激活失败，且请求结果未知。重试前先用读取操作核对状态；不要盲目重试变更。 |
| `.wasm` 模块失败并报 `exports no '_start'; build it as a WASI command` | 构建目标与导出 | 用 `wasm32-wasip1`（Rust）或 `GOOS=wasip1 GOARCH=wasm`（Go）构建；模块必须是 WASI 命令。 |
| 行失败并报 `Invalid configuration for row '<row>': unknown key '<key>'` | 行的 `config` 与声明的 schema | Host 会应用默认值并校验类型；未声明的键会被拒绝。更新声明或行。 |

## 选择 loader

可执行文件或解释器入口使用 `loader.process`。Host 启动进程，通过 `initialize` 协商协议版本 1 或 2，并负责进程生命周期。SDK WebAssembly 模块使用 `loader.wasm`，通过 `bff.recv`、`bff.send` 和 `bff.log` 导入交换相同的 JSON 协议。先配置 loader 的 source，再通过单独的 profile 行挂载发现的插件定义。

两个 loader 都把定义发布为有所有权的定义来源，因此绑定 SDK 插件的行与原生行一样按声明的服务顺序激活。一个进程条目最多可描述多个插件，数量受其 `max_definitions` 预留限制；只有该 loader 服务的所有行都排空后，loader 才会停止进程；重新加载 SDK 行只是向同一进程请求一次新的激活，而不会重启它。

## 激活与资源所有权

Host 通过 `activate` 发送不透明的 activation id、行 id、插件 id、已校验的配置和 reload 标志。contract 声明在此前的 `describe` 阶段读取。激活回调返回前，本次激活就已可寻址，因此回调内可以注册消息处理器和调用 Host 服务。请求等待受 Host 侧超时限制。订阅等自有资源必须在停用时释放，清理不完整会被报告；native 代码必须配合取消。

## 激活生命周期取消

每个激活实例都有只读的生命周期信号。插件行开始停止时，信号会先于已接收回调的排空发出；SDK 激活失败回滚、执行停用钩子或处理连接断开前也会触发它。停止一个插件行不会取消同进程的其他行，重新加载会创建新信号。

| SDK | 观察取消 | 检查是否支持提前通知 |
| --- | --- | --- |
| Python | `await ctx.lifetime.wait()` / `ctx.lifetime.is_set()` | `ctx.supports_lifetime_cancellation` |
| TypeScript | `ctx.lifetime`（`AbortSignal`） | `ctx.supportsLifetimeCancellation` |
| Go | `ctx.Lifetime.Done()` / `ctx.Lifetime.Err()` | `ctx.SupportsLifetimeCancellation()` |
| Rust | `ctx.lifetime().is_cancelled()` | `ctx.supports_lifetime_cancellation()` |

Host 在协议 v2 的 `initialize.features` 中声明 `lifecycle.cancel`。SDK 建立实例状态后、进入插件激活钩子前，以 `{ "activation": "opaque-id" }` 调用 `lifecycle.ready`。Host 通过原生任务管理器和共享 effect 容量拥有一个取消观察任务，停止时发送带相同激活 ID 的 `lifecycle.cancel` 通知。重复就绪和取消不会重复注册；未知或已移除实例的通知直接忽略，不缓存。

旧 Host 仍兼容，但无法提前通知。工作若依赖在停用前收到取消，必须检查上表的能力标志；本地回滚、停用和连接断开仍会取消信号。Go/Rust WebAssembly 在读取 Host 通信时处理通知；阻塞其单执行线程等待本地信号，会导致通知无法被处理。需要独立运行的工作时，使用协作式 Host 调用或进程加载器。

取消表示请求停止，不表示工作已经完成。仍须管理注册句柄并等待实际清理。

## 类型化消息处理器与显式替换

在 `handles` 中声明入站消息类型，再使用 `ctx.messages.on` 注册处理器。若插件需要替换指定的现有处理器，必须在 `overrides` 中声明精确的 `rowId:fully.qualified.MessageType` 目标，然后调用 SDK 的 `messages.override`。Host 会验证声明、协议绑定和当前选中的目标，验证通过后才发布替换。释放返回的句柄会恢复前一个处理器，并遵循激活实例的反向 effect 顺序。目标缺失或替换未实际安装都会使激活失败，不会静默改变分发顺序。

| SDK | 基础注册 | 显式替换 |
| --- | --- | --- |
| Python | `await ctx.messages.on(protocol, type, handler)` | `await ctx.messages.override(protocol, type, target_row_id, handler)` |
| TypeScript | `ctx.messages.on(protocol, type, handler)` | `ctx.messages.override(protocol, type, targetRowId, handler)` |
| Go | `ctx.Messages.On(protocol, type, handler)` | `ctx.Messages.Override(protocol, type, targetRowID, handler)` |
| Rust | `ctx.on(protocol, type, handler)` | `ctx.override_handler(protocol, type, targetRowId, handler)` |

回调收到的 session 和已解码消息体与基础处理器相同。线协议仍由 Host 的类型化 dialect 层负责；SDK 处理器不能直接读取或写入 opcode。

## 消息 middleware 与单次续调

协议 v2 的 SDK 插件可以用与原生消息 middleware 相同的顺序和生命周期规则包装类型化消息管线。在 `middleware` 中列出消息短名称，再调用 `messages.use`。优先级高的 middleware 在外层；优先级相同则保持注册顺序。回调会收到 session、已解码消息体、只能调用一次的 `next` 续调，以及本次调用的取消信号。不调用 `next` 就会短路后续处理器。消息进入 middleware 管线前必须已经选出一个下游类型化处理器。

| SDK | 注册方式 |
| --- | --- |
| Python | `await ctx.messages.use(protocol, type, middleware, priority=0)` |
| TypeScript | `ctx.messages.use(protocol, type, middleware, priority)` |
| Go | `ctx.Messages.Use(protocol, type, priority, middleware)` |
| Rust | `ctx.use_middleware(protocol, type, priority, middleware)` |

Host 拥有 `messages.use` 返回的注册句柄；必须把它交给激活实例的 effect 作用域。每次分发都作为一个有界的 `messages.invoke` 回调进入 SDK。SDK 调用 `next` 时发送带调用 ID 的 `messages.next`，Host 会保留原生续调，直到下游管线完成。重复调用 `next`、回调返回后调用，或使用其他激活实例的调用 ID，都会返回 invalid-params 错误；续调只能使用一次，不能逃出本次调用。调用或激活实例停止时，Host 发送 `messages.cancel`；取消是协作式停止请求，不会丢弃已经进入的下游工作。

middleware 注册和续调都要求协议 v2。这样原生路由器、类型化 dialect 与 SDK loader 共用一条消息管线：封包和字段含义仍由 Host 管理，策略和游戏逻辑可以由任意受支持的插件语言实现。

## 原生所有的任务

协议 v2 的 SDK 可以通过激活实例的原生任务所有者启动后台工作：

```python
task = await ctx.tasks.run("refresh-cache", work)
ctx.effect(task)
```

`ctx.tasks.run` 使用与原生 `RunTaskAsync` 相同的激活容量限制。Host 通过 `tasks.invoke` 调用已注册的回调，在任务停止时发送 `tasks.cancel`。`TaskHandle.wait` 只观察回调结果，不释放所有权；`TaskHandle.dispose` 请求取消、等待回调和原生清理，并释放任务所有者。如果回调自身请求释放，Host 会延迟父作用域释放，避免自己等待自己的死锁。正常完成、取消和故障保持不同状态；故障会继续占有资源，直到停用时报告。

四种 SDK 的入口分别为 Python 和 TypeScript 的 `ctx.tasks.run` / `wait` / `dispose`，Go 的 `ctx.Tasks.Run` / `Wait` / `Dispose`，以及 Rust 的 `Context::run_task` / `TaskHandle::wait` / `dispose`。每个句柄都应登记为 effect。Go 和 Rust WebAssembly 仍采用协作式取消；模块必须返回 Host 通信循环才能收到取消通知。

## 原生所有的资源作用域

协议 v2 的 SDK 可以创建有界的原生资源作用域，并在 SDK 进程中保留资源释放回调：

```python
scope = await ctx.resources.scope("cache")
ctx.effect(scope)
resource = await scope.acquire("cache-entry", release)
ctx.effect(resource)
```

`create` 创建子作用域，`acquire` 占用一个原生 effect 槽位。作用域和资源句柄都归激活实例所有，应登记为 effect。停用时，如果原生父作用域正在退出，Host 可能返回 `{ "deferred": true }`；SDK 会继续保留回调，直到 Host 调用并完成它。释放回调会收到取消信号，Host 会一直保留原生所有权直到回调返回；回调不能同步释放同一个资源。Python、TypeScript、Go 和 Rust 提供相同的作用域、获取和释放形状；Go 与 Rust WebAssembly 的回调必须回到 Host 通讯循环才能接收取消通知。

## 服务调用

当前协议提供类型化消息、日志、effects，以及明确列出的 Host 服务调用。使用 contract 前必须在插件声明中获得授权。针对当前 checkout 生成清单：

```sh
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- contracts schema --out artifacts/contracts.json
```

通过 `rpc_shape` 检查可调用操作，通过 `provider_rpc_shape` 检查可实现操作。提供者必须实现全部方法；无歧义时使用方法名，重载使用完整 schema method id。属性访问器采用 `get_ReplyAfterInventory` 这样的名称。不支持的形状会明确失败。委托、引用参数、通用资源适配、数据包编解码器和接收 `IPluginContext` 的提供者方法尚不在这套服务桥接的范围内。

## 提供与装饰服务

以下注册需要协议 v2，并参与原生依赖图：

| 声明 | 注册模式 | 行为 |
| --- | --- | --- |
| `provides` | `singleton` | 单个实现，不接受装饰层 |
| `provides` | `decoratable` | 单个实现，允许已声明的装饰层 |
| `provides_many` | `many` | 多个有序实现；优先级高者在前，同级按注册顺序 |
| `decorates` | `decorate` | 包装选定的内层服务，基础服务必须允许装饰 |

例如，Python 行可以装饰原生策略。将其与允许装饰的基础提供者一同挂载：

```python
from bff_plugin import Context, ServiceCall, plugin, run

@plugin("example.inventory-policy", version="1.0.0",
        decorates=["IEquipmentReplyPolicy"])
class InventoryPolicy:
    async def activate(self, ctx: Context) -> None:
        async def reply_policy(call: ServiceCall):
            return await call.inner()

        ctx.effect(await ctx.services.decorate("IEquipmentReplyPolicy", {
            "get_ReplyAfterInventory": reply_policy,
        }, priority=10))

if __name__ == "__main__":
    run()
```

示例保留内层结果；策略也可以返回自己的合法结果。内层服务仅属于本次调用，并保留已选定的装饰链，不能保存到后续调用中使用。Host 会拒绝跨激活或已结束的调用令牌。注销注册不会让消费者已经持有的服务失效，因此回调目标会保留到提供者激活结束。

| SDK | 注册 / 装饰 / 选择全部实现 |
| --- | --- |
| Python | `ctx.services.provide` / `decorate` / `get_all` |
| TypeScript | `ctx.services.provide` / `decorate` / `getAll` |
| Go | `ctx.Services.Provide` / `Decorate` / `GetAll` |
| Rust | `ctx.provide_service` / `decorate_service` / `get_all_services` |

## 有序集合与取消

声明 `requires_many` 要求至少一个实现；`optional_many` 允许空集合。TypeScript 选项写作 `requiresMany`、`optionalMany`，Go 写作 `RequiresMany`、`OptionalMany`。集合提供数量，并按从零开始的索引调用成员；所选实现及顺序在释放前保持稳定。集合句柄仅属于一次激活，与其他句柄共用容量限制。把释放操作登记为 effect；Rust 集合使用 `effect_fn`。

提供者回调接收协作取消信号：Python 为 `call.cancellation`，TypeScript 为 `call.signal`，Go 为 `call.Context`，Rust 为 `call.is_cancelled()`。收到取消后应及时返回。WebAssembly 在处理 Host 调用时接收通知，忙计算不能假定存在后台取消线程。超时会使激活失败，但已进入的工作仍受管理，直到实际响应、原生操作完成或传输终止。回调返回后，Host 仍等待此前已进入的内层调用结束。

## 共享事件

协议 v2 通过本次激活的原生事件总线处理 SDK 事件。原生与 SDK 监听器参与同一个优先级顺序和分发快照。分别声明 `subscribes` 与 `publishes`，指定事件 `type`、明确的 `mode`，并为返回结果的模式指定 `result_type`。从生成清单的 `events` 和 `event_result_types` 查询事件与结果类型；短名称有歧义时使用全限定名称。包含原生连接、委托或其他不支持值的类型需要专用适配器，会在声明时被拒绝。

| 发布模式 | 监听器与行为 |
| --- | --- |
| `emit` | 仅同步监听器；记录监听器错误后继续分发 |
| `serial` | 同步及异步监听器，按顺序等待 |
| `parallel` | 同步及异步监听器，一起等待异步工作 |
| `waterfall` | 拦截器包裹末端回调，可以返回替代结果 |
| `bail` | 首个非 null 的引用类型结果胜出；null 表示继续，处理器错误会记录 |

订阅模式为 `sync`、`async`、`waterfall`、`bail`。高优先级先执行，同级保持注册顺序。在 `serial` 和 `parallel` 中，同步监听器错误仍按原生规则记录后继续；异步错误向上传播，但因移除监听器引起的取消除外。必须选择原生发布方实际使用的模式。通知监听器不返回值；waterfall 返回声明类型的非 null 结果；只有 bail 允许通过 null 继续。

下面的 Python 策略观察已有的装备决策流水线，保留原结果。将它挂载到会发布装备选择事件的 profile：

```python
from bff_plugin import Context, EventSpec, plugin, run

selection = EventSpec("EquipmentSelectionApplying", "waterfall",
                      "EquipmentSelectionDecision")

@plugin("example.selection-policy", version="1.0.0", subscribes=[selection])
class SelectionPolicy:
    async def activate(self, ctx: Context) -> None:
        async def inspect(call):
            decision = await call.next()
            ctx.logger.info(f"Selection allowed: {decision['allowed']}")
            return decision

        ctx.effect(await ctx.events.on(selection, inspect, priority=10))

if __name__ == "__main__":
    run()
```

`call.next()` 调用已经选定的下游流水线。在本次回调仍有效时可以重复调用，与原生行为一致；每次都会重新执行下游工作。直接返回合法决策即可短路。事件数据跨边界时按值传输，修改本地 body 不会修改发布者的对象；策略变化应通过事件/结果契约表达。

| SDK | 订阅 / 发布 | 结果声明 |
| --- | --- | --- |
| Python | `ctx.events.on` / `ctx.events.publish` | `EventSpec(type, mode, result_type)` |
| TypeScript | `ctx.events.on` / `ctx.events.publish` | `{ type, mode, resultType }` |
| Go | `ctx.Events.On` / `ctx.Events.Publish` | `EventSpec{Type, Mode, ResultType}` |
| Rust | `ctx.on_event` / `ctx.publish_event` | `EventSpec::new(type, mode).with_result(result_type)` |

Waterfall 发布必须提供返回声明结果的末端回调，其他模式拒绝末端回调。每个订阅句柄都交给 effect 管理。在监听器内部使用 `call.dispose(handle)`（Go：`call.Dispose(handle)`）移除自身订阅，以恢复原生回调上下文，避免等待自己结束；外部移除仍等待已进入的工作完成。续调和自身移除令牌仅属于一次有效调用，跨激活或已经结束的令牌会被拒绝。

事件回调提供与服务回调相同的协作取消 API。取消只请求停止，实际完成后才释放所有权。超时会使行失败，已进入的原生工作仍受管理直到结束。发布会把末端回调保留到原生分发实际完成，即使 Python 调用方取消了自己的 await。同步及 bail 监听器会在发布方执行路径上进行阻塞 RPC，应保持短小。提供者注册、保留的事件回调及临时末端回调共用本次激活的有界回调预算（`max_provider_callbacks`，由行的 `effect_capacity` 授予）。移除监听器后，其回调保留至激活清理；注册失败的预留和已完成的临时末端回调会释放。

## 可靠性

Host 提交变更后请求仍可能超时。超时代表结果未知，应先通过读取操作核对状态，再决定是否重试。日志中保留 request id，停用后停止发送，并把 Host 断开视为本次激活失败。

`sdk/` 下的示例是当前协议的兼容性参考，不能替代 profile 中的 capability 和权限声明。
