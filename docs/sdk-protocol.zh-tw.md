---
title: "SDK 通訊協定"
description: "將選用的其他語言 SDK 模組連線至 Host 處理程序，或用 .NET 撰寫原生模組。"
updated: 2026-10-18
---

伺服器的主要實作是 .NET 10 上的 C#。`sdk/` 中的 SDK 是給 Python、TypeScript、Go 與 Rust 的選用模組用戶端：它們介接相同的 Host 契約，讓模組可以用其他語言撰寫，而不必重新實作伺服器。程序模組透過標準輸入輸出上有界、逐行分隔的 JSON 協定連線至 Host。Go 與 Rust 程式模組也可以在 Host 內以 WebAssembly 執行。

本頁先說明 SDK 是什麼、不是什麼，為每種語言提供一個可執行的快速入門，並說明如何測試模組與診斷常見故障；接著詳細記錄 loader、啟用、取消、訊息、服務、工作、資源與事件契約。SDK 直接從本儲存庫使用，不從套件登錄安裝。

## 本頁涵蓋的內容

- 兩個 loader：用於子處理程序的 `loader.process`，以及用於沙箱 WebAssembly 程式模組的 `loader.wasm`。
- SDK 模組目前能做什麼：型別化訊息、明確宣告的替換與 middleware、明確宣告的服務呼叫、提供者與裝飾器、有序集合、共用事件、具所有權的工作與資源。
- 如何在每種隨附語言中建置、執行、測試與掛載模組。
- 協定版本協商、產生的契約清單與一致性測試如何運作。
- 協定與平台的故障模式。

**本頁適合：** 已閱讀[模組](plugins.md)、希望用 C# 以外的語言加入行為的開發者，或需要精確 loader 與協定契約的讀者。

## 開始之前

- 已完成[安裝](installation.md)並建置取出，終端機位於儲存庫根目錄。
- 所選語言的 SDK 工具鏈：目前的 Python 3 解譯器（一致性測試執行器呼叫 `python`）、Node.js 20 或更新版本與 npm（`sdk/typescript/package.json` 宣告 `node >=20`）、Go 1.22 或更新版本（`sdk/go/go.mod`），或支援 edition 2021 的 Rust 工具鏈與 cargo（`sdk/rust/Cargo.toml`）。只需要一種，不需要四種都裝。WebAssembly 程式模組還需要 Rust 的 `wasm32-wasip1` 或 Go 的 `wasip1` GOOS。
- 執行步驟需要：一份 `.env` 檔案，其中 `BFF_MYSQL_PASSWORD` 為非空的臨時值；以及本機檢查修補檔 `tools/local-checks/isolated-memory.patch.yaml`，詳見[快速入門](quickstart.md)。
- 已了解[模組](plugins.md)中的模組模型：模組 id、資料列 id、宣告與 effect。

## 快速入門

四個快速入門用各自語言建置同一個小模組。它只在啟用時記錄一行日誌，不處理任何訊息，因此在僅管理用途的 `management` profile 上執行，不需要用戶端資料。Python 與 Go SDK 沒有第三方執行階段相依套件；TypeScript 與 Rust SDK 分別用 `npm ci` 與 `cargo` 取得建置相依套件。

| SDK | 位置 | 協定 |
| --- | --- | --- |
| Python | `sdk/python` | 以 stdio 為基礎的 JSON-RPC，協定 v1 與 v2 |
| TypeScript | `sdk/typescript` | 以 stdio 為基礎的 JSON-RPC，協定 v1 與 v2 |
| Go | `sdk/go` | 以 stdio 為基礎的 JSON-RPC，協定 v1 與 v2；可用 `wasip1` 建置供 `loader.wasm` |
| Rust | `sdk/rust` | 以 stdio 為基礎的 JSON-RPC，協定 v1 與 v2；可用 `wasm32-wasip1` 建置供 `loader.wasm` |

### Python

Python SDK 是 `sdk/python` 中僅依賴標準函式庫的 `bff_plugin` 套件。在儲存庫根目錄執行其單元測試：

```sh
python -m unittest discover -s sdk/python/tests -p "test_*.py"
```

用共用協定 fixture 執行 Python 一致性模組：

```sh
python sdk/python/tests/run_conformance.py
```

建立 `sdk/python/examples/hello.py`：

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

loader 項目從 `sdk/python/examples` 啟動該程式模組，因此 `bff_plugin` 可被匯入。`@plugin` 宣告中的 `config` schema 是選用的；本模組沒有宣告。將此修補檔儲存為 `artifacts/sdk-hello.patch.yaml`：

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

TypeScript SDK 是 `sdk/typescript` 中的本機 npm 套件。在該目錄安裝建置相依套件、建置並執行其自帶測試指令碼：

```sh
cd sdk/typescript
npm ci
npm test
```

`npm test` 會建置套件，並執行線格式、生命週期與一致性測試套件。建立 `sdk/typescript/examples/hello.ts`：

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

`npm run build` 將它編譯到 `dist/examples/hello.js`。將此修補檔儲存為 `artifacts/sdk-hello.patch.yaml`，覆寫 Python 修補檔：

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

Go SDK 是 `sdk/go` 中的 `bffplugin` 套件。在該目錄執行其測試：

```sh
cd sdk/go
go test ./...
```

建立 `sdk/go/examples/hello/main.go`：

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

將二進位檔建置到 `sdk/go`，並將此修補檔儲存為 `artifacts/sdk-hello.patch.yaml`，覆寫 TypeScript 修補檔：

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

在 Windows 上輸出檔案是 `hello.exe`；使用 `go build -o hello.exe ./examples/hello`，並把 `command` 設為 `sdk/go/hello.exe`。

### Rust

Rust SDK 是 `sdk/rust` 中的 `bff-plugin` crate。在該目錄執行其測試：

```sh
cd sdk/rust
cargo test --release
```

建立 `sdk/rust/examples/hello.rs`：

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

建置它，並將此修補檔儲存為 `artifacts/sdk-hello.patch.yaml`，覆寫 Go 修補檔：

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

在 Windows 上，二進位檔是同一目錄下的 `hello.exe`。

### 掛載模組

上面每種語言的章節都儲存同一種修補檔：一個 `insert` 操作，加入一個 `loader.process` 資料列與一個繫結 `example.hello` 的一般資料列。用第二個 `--patch` 套用你所選語言的修補檔；隔離記憶體修補檔必須放在前面，這樣不會接觸真實資料庫：

```powershell
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- run --profile management --patch tools/local-checks/isolated-memory.patch.yaml --patch artifacts/sdk-hello.patch.yaml
```

loader 資料列上的兩項啟動額度是必要的。`definition_source_reserve: 1` 為該處理程序項目預留一個定義來源；沒有它，啟動會以 `references unknown plugin 'example.hello'` 拒絕繫結資料列。`definition_reserve: 16` 為該來源將要發佈的定義預留空間；引導 loader 資料列也是用同樣方式預留容量。資料列 id 可以任意取名，而 `source_id` 是定義來源的身分，在一次 loader 啟用內必須唯一；未命名的處理程序項目預設為 `process-<index>`。相對的 `command`、`args` 路徑與 `cwd` 相對 `base_dir` 解析，後者預設為 Host 的工作目錄（上述命令中的儲存庫根目錄）；不含目錄的命令名稱從 `PATH` 尋找。

loader 資料列必須處於作用中狀態，定義才會發佈：在此之前繫結 `example.hello` 的資料列會保持 `Pending`。若要在封裝 profile 中長期掛載該模組，可把這些資料列複製到該 profile 自己的修補檔，而不必在命令列上傳入 `--patch`。

啟動日誌會依序顯示 loader 資料列啟用、SDK 來源自我回報、繫結資料列啟用：

```text
Row 'hello.python' activated (plugin 'loader.process' version 1.0.0+<commit>; contracts ...; builds independent).
SDK plugin source 'hello-python' runs SDK {"name":"...","version":"..."}.
Row 'hello.binding' activated (plugin 'example.hello' version 1.0.0; contracts ...; builds independent).
hello from the Python SDK
```

**檢查是否成功：** loader 資料列與繫結資料列都出現，模組訊息被記錄，且 `GET /api/plugins` 顯示 `hello.binding` 狀態為 `Active`，loader 資料列同樣為 `Active`。如果繫結資料列為 `Failed` 或一直 `Pending`，其 `error` 欄位會帶有 SDK 或設定錯誤。按 Ctrl+C 停止；在該 loader 服務的每個資料列都排空後，loader 才會停止子處理程序。

該快速入門使用相對的 `command` 路徑與 `cwd`，因此在儲存庫根目錄即可執行。WebAssembly 模組改用 `loader.wasm` 資料列與 `module` 項目：程式模組是匯出 `_start` 的 WASI 命令，匯入 `bff.recv`、`bff.send` 與 `bff.log`，不接收參數、環境變數、檔案或 stdio。其線性記憶體受 `max_memory_bytes` 限制（預設 64 MiB）。Go 程式模組用 `GOOS=wasip1 GOARCH=wasm` 建置，Rust 程式模組用 `wasm32-wasip1` 目標建置。

註冊訊息處理常式的模組還需要一個遊戲 profile，其型別化 dialect 為該協定與訊息型別提供輸入繫結。隨附的 echo 範例宣告了 `handles: ["LoginRequested"]` 與 `supported_builds: ["kr:812"]`，因此應掛載到有用戶端資料的 `kr-812` profile，而不是 `management`。

## 能力協商與相容性

Host 透過一次 `initialize` 要求開始對話，SDK 回答它選擇的協定版本。參數帶有模組決定自己可以宣告什麼所需的全部資訊：

| initialize 欄位 | 意義 |
| --- | --- |
| `protocol_version`、`protocol_versions` | 舊的單版本欄位與完整的提供清單；Host 提供 `1` 與 `2` |
| `features` | 本建置實作的 Host 通知與回呼，包括 `lifecycle.cancel` |
| `max_in_flight` | 單一方向上未完成要求的上限 |
| `max_reentry_depth` | 單執行緒程式模組巢狀 Host 要求框架的上限 |
| `host_version`、`contracts` | Host 發行版及其提供的共用契約組件版本 |

協定 v1 涵蓋最初的介面：`initialize`、`describe`、`activate`、`deactivate`、`deliver`、`messages.on`、`messages.override`、`messages.send`、`effect.dispose` 與 `log`。協定 v2 增加了服務呼叫與提供者、有序集合、事件、工作、資源、訊息 middleware，以及提前的 `lifecycle.cancel` 通知與 `lifecycle.ready`。超出協商版本的宣告會失敗而不是被忽略：Host 會回報 `provides, provides_many and decorates require protocol v2.`、`requires_many and optional_many require protocol v2.`、`event declarations require protocol v2.`，或由 SDK 拒絕未知方法。

Host 缺少某項能力本身不是錯誤。相依於某項能力前先檢查文件提供的能力旗標，例如 Python 的 `ctx.supports_lifetime_cancellation`、TypeScript 的 `ctx.supportsLifetimeCancellation`、Go 的 `ctx.SupportsLifetimeCancellation()` 與 Rust 的 `ctx.supports_lifetime_cancellation()`。舊 Host 仍可透過本機復原、停用與傳輸中斷路徑運作，但無法在停用前送達取消。

`describe` 的回答會與共用契約驗證。宣告中的名稱必須解析為共用契約組件裡唯一的訊息記錄、共用抽象或契約中唯一的服務介面，family 必須是 `legacy` 或 `modern`。未知名稱會使整個來源失敗，錯誤會列出允許的契約，因此拼字錯誤在資料列啟用時就會暴露，而不是等到訊息到達。

### 查看契約清單

為目前取出產生清單：

```sh
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- contracts schema --out artifacts/contracts.json
```

不帶 `--out` 時清單寫入標準輸出；`contracts` 命令的日誌寫入標準錯誤，使文件保持乾淨。清單描述 SDK 模組可以宣告與呼叫的內容：

| 清單欄位 | 內容 |
| --- | --- |
| `schema_version` | 清單格式版本 |
| `runtime_protocol_versions`、`runtime_features` | Host 宣告的協定版本與能力清單 |
| `messages`、`events`、`event_result_types` | 型別化訊息記錄、事件承載與事件結果型別及其內容形狀 |
| `services` | 服務介面及其操作、參數與結果形狀 |
| `types` | 被參照的記錄與列舉形狀 |

每個服務操作都帶有 `rpc_shape` 與 `provider_rpc_shape`。SDK 模組可以呼叫該操作時 `rpc_shape` 為 `supported`，SDK 模組可以實作它時 `provider_rpc_shape` 為 `supported`；每個不支援項目帶有 `unsupported_reason` 或 `provider_unsupported_reason`。請使用清單而不是猜測：提供者必須實作整個介面，無歧義時使用方法名稱，多載使用完整 schema method id，屬性存取子使用 `get_ReplyAfterInventory` 這類名稱。

## 測試模組

儲存庫提供一個共用一致性執行器與一套所有 SDK 共用的 fixture 格式。在儲存庫根目錄執行：

```sh
python sdk/run-conformance.py
```

執行器會建置並單元測試每個工具鏈存在的 SDK，然後用共用 fixture（位於 `tests/fixtures/sdk-protocol`）與 protocol-v2 fixture 測試該 SDK 的模組。每個套件輸出 `PASS`、`FAIL` 或 `SKIP` 與一行摘要；任一失敗時結束碼非零；因工具鏈缺少而跳過的語言不算通過。這是目前協定的相容性參考。

fixture 是 JSON-lines 檔案。第一行是標頭，含 `ignore` 清單，其中的點分路徑在比較前從兩側移除。之後每一行是 `{"dir": "h2p", ...}`（Host 傳給模組）、`{"dir": "p2h", ...}`（模組預期回覆）或 `{"expect_exit": N}`。執行器為每個 fixture 啟動新的模組處理程序，把每個 Host 訊息寫入其標準輸入，套用 ignore 清單後把模組輸出的每一行與預期回覆精確比較，並要求乾淨結束。識別與控制代碼按精確值比較。用共用 Python 執行器讓自有模組跑同一套 fixture：

```sh
python sdk/python/tests/run_conformance.py --fixtures <fixture-dir> --command <plugin-executable> [args...]
```

**檢查是否成功：** 執行器以 `<passed>/<total> fixtures passed` 結束並結束碼 0；不符時會列印第一個失敗行的預期與實際 JSON，以及模組的標準錯誤。

## 常見故障

| 症狀 | 檢查 | 可能原因與修正 |
| --- | --- | --- |
| 來源失敗並回報 `answered initialize without a supported protocol_version (1 or 2).` | 你的執行階段對 `protocol_versions` 的回答 | 手寫執行階段必須從提供的清單中選擇 `1` 或 `2`。隨附 SDK 總是回答受支援的版本。 |
| `stdout carried a line that is not JSON ...; stdout is reserved for the protocol` | 處理程序寫入標準輸出的任何內容 | 標準輸出是協定通道。Python 會把 `print`、TypeScript 會把 `console.*` 重新導向到標準錯誤；Go 與 Rust 應把診斷寫入標準錯誤或透過 SDK logger，絕不能寫標準輸出。 |
| 啟動失敗並回報 `SDK plugin process '<command>' could not start in '<cwd>'` | `command`、`args`、`cwd` 與 `PATH` | 可執行檔或工作目錄不對。含目錄分隔符的路徑相對 `base_dir` 解析；裸命令名稱需要 `PATH`。 |
| 資料列失敗並回報 `requires_many and optional_many require protocol v2.` 或類似的 v2 訊息 | 協商的協定版本與宣告欄位 | 該宣告需要協定 v2。協商 v2 或移除宣告；錯誤會指明欄位類別。 |
| 訊息處理常式失敗並回報 `protocol '<p>' has no inbound binding for '<type>'` | profile 的 dialect 資料列 | 該協定與訊息型別的型別化 dialect 必須在同一 profile 中啟用；SDK 自己不能解碼 packet。 |
| 回呼逾時：`did not answer '<method>' within <n> ms (request_timeout_ms).` | 模組是否阻塞，以及變更是否可能已提交 | 逾時會使啟用失敗，且要求結果未知。重試前先用讀取操作核對狀態；不要盲目重試變更。 |
| `.wasm` 程式模組失敗並回報 `exports no '_start'; build it as a WASI command` | 建置目標與匯出 | 用 `wasm32-wasip1`（Rust）或 `GOOS=wasip1 GOARCH=wasm`（Go）建置；程式模組必須是 WASI 命令。 |
| 資料列失敗並回報 `Invalid configuration for row '<row>': unknown key '<key>'` | 資料列的 `config` 與宣告的 schema | Host 會套用預設值並驗證型別；未宣告的鍵會被拒絕。更新宣告或資料列。 |

## 選擇 loader

可執行檔或直譯器入口使用 `loader.process`。Host 啟動處理程序，透過 `initialize` 協商協定版本 1 或 2，並負責處理程序生命週期。SDK WebAssembly 程式模組使用 `loader.wasm`，透過 `bff.recv`、`bff.send` 與 `bff.log` 匯入交換相同的 JSON 協定。先設定 loader 的 source，再透過獨立的 profile 資料列掛載探索到的模組定義。

兩個 loader 都把定義發佈為有所有權的定義來源，因此繫結 SDK 模組的資料列與原生資料列一樣依宣告的服務順序啟用。一個處理程序項目最多可描述多個模組，數量受其 `max_definitions` 預留限制；只有該 loader 服務的所有資料列都排空後，loader 才會停止處理程序；重新載入 SDK 資料列只是向同一處理程序要求一次新的啟用，而不會重新啟動它。

## 啟用與資源所有權

Host 透過 `activate` 傳送不透明的 activation id、資料列 id、模組 id、已驗證的設定與 reload 旗標。contract 宣告在先前的 `describe` 階段讀取。啟用回呼傳回前，本次啟用就已可定址，因此回呼內可以註冊訊息處理常式與呼叫 Host 服務。要求的等待時間受 Host 端逾時限制。訂閱等自有資源必須在停用時釋放；清理不完整會回報；native 程式碼必須配合取消。

## 啟用執行個體的取消訊號

每個啟用執行個體都有唯讀的生命週期訊號。模組資料列開始停止時，訊號會先於已接受回呼的排空發出；SDK 啟用失敗復原、執行停用掛鉤或處理連線中斷前也會觸發它。停止一個模組資料列不會取消同處理程序中的其他資料列；重新載入會建立新的訊號。

| SDK | 觀察取消 | 檢查是否支援提前通知 |
| --- | --- | --- |
| Python | `await ctx.lifetime.wait()` / `ctx.lifetime.is_set()` | `ctx.supports_lifetime_cancellation` |
| TypeScript | `ctx.lifetime`（`AbortSignal`） | `ctx.supportsLifetimeCancellation` |
| Go | `ctx.Lifetime.Done()` / `ctx.Lifetime.Err()` | `ctx.SupportsLifetimeCancellation()` |
| Rust | `ctx.lifetime().is_cancelled()` | `ctx.supports_lifetime_cancellation()` |

Host 在協定 v2 的 `initialize.features` 宣告 `lifecycle.cancel`。SDK 建立執行個體狀態後、進入模組啟用掛鉤前，以 `{ "activation": "opaque-id" }` 呼叫 `lifecycle.ready`。Host 透過原生工作管理器與共用 effect 容量管理一個取消觀察工作；停止時傳送帶有相同啟用 ID 的 `lifecycle.cancel` 通知。重複就緒與取消不會重複註冊；未知或已移除執行個體的通知會直接忽略，不會暫存。

舊版 Host 仍相容，但無法提前通知。工作若相依於在停用前收到取消，必須檢查上表的能力旗標；本機復原、停用及連線中斷仍會取消訊號。Go/Rust WebAssembly 在讀取 Host 通訊時處理通知；若阻塞其單一執行緒等待本機訊號，通知就無法處理。需要獨立執行的工作時，請使用協作式 Host 呼叫或處理程序載入器。

取消代表要求停止，不代表工作已經完成。仍須管理註冊控制代碼，並等待實際清理完成。

## 型別化訊息處理常式與明確替換

在 `handles` 宣告輸入訊息型別，再使用 `ctx.messages.on` 註冊處理常式。若模組需要替換指定的既有處理常式，必須在 `overrides` 宣告精確的 `rowId:fully.qualified.MessageType` 目標，然後呼叫 SDK 的 `messages.override`。Host 會驗證宣告、協定繫結與目前選取的目標，驗證通過後才發佈替換。釋放回傳的控制代碼會恢復前一個處理常式，並遵循啟用執行個體的反向 effect 順序。目標不存在或替換沒有實際安裝都會使啟用失敗，不會靜默改變分派順序。

| SDK | 基礎註冊 | 明確替換 |
| --- | --- | --- |
| Python | `await ctx.messages.on(protocol, type, handler)` | `await ctx.messages.override(protocol, type, target_row_id, handler)` |
| TypeScript | `ctx.messages.on(protocol, type, handler)` | `ctx.messages.override(protocol, type, targetRowId, handler)` |
| Go | `ctx.Messages.On(protocol, type, handler)` | `ctx.Messages.Override(protocol, type, targetRowID, handler)` |
| Rust | `ctx.on(protocol, type, handler)` | `ctx.override_handler(protocol, type, targetRowId, handler)` |

回呼收到的 session 與已解碼訊息內容與基礎處理常式相同。線協定仍由 Host 的型別化 dialect 層負責；SDK 處理常式不能直接讀取或寫入 opcode。

## 訊息 middleware 與單次續接

協定 v2 的 SDK 模組可以用與原生訊息 middleware 相同的順序與生命週期規則包裝型別化訊息管線。在 `middleware` 列出訊息短名稱，再呼叫 `messages.use`。優先權高的 middleware 在外層；優先權相同則保持註冊順序。回呼會收到 session、已解碼訊息內容、只能呼叫一次的 `next` 續接，以及本次呼叫的取消訊號。不呼叫 `next` 就會短路後續處理常式。訊息進入 middleware 管線前必須已經選出一個下游型別化處理常式。

| SDK | 註冊方式 |
| --- | --- |
| Python | `await ctx.messages.use(protocol, type, middleware, priority=0)` |
| TypeScript | `ctx.messages.use(protocol, type, middleware, priority)` |
| Go | `ctx.Messages.Use(protocol, type, priority, middleware)` |
| Rust | `ctx.use_middleware(protocol, type, priority, middleware)` |

Host 擁有 `messages.use` 回傳的註冊控制代碼；必須把它交給啟用執行個體的 effect 範圍。每次分派都作為一個有界的 `messages.invoke` 回呼進入 SDK。SDK 呼叫 `next` 時傳送帶呼叫 ID 的 `messages.next`，Host 會保留原生續接，直到下游管線完成。重複呼叫 `next`、回呼傳回後呼叫，或使用其他啟用執行個體的呼叫 ID，都會傳回 invalid-params 錯誤；續接只能使用一次，不能逃出本次呼叫。呼叫或啟用執行個體停止時，Host 傳送 `messages.cancel`；取消是協作式停止要求，不會丟棄已經進入的下游工作。

middleware 註冊與續接都要求協定 v2。如此原生路由器、型別化 dialect 與 SDK loader 共用一條訊息管線：封包與欄位意義仍由 Host 管理，原則與遊戲邏輯可以由任何受支援的模組語言實作。

## 原生所有的工作

協定 v2 的 SDK 可以透過啟用執行個體的原生工作所有者啟動背景工作：

```python
task = await ctx.tasks.run("refresh-cache", work)
ctx.effect(task)
```

`ctx.tasks.run` 使用與原生 `RunTaskAsync` 相同的啟用容量限制。Host 透過 `tasks.invoke` 呼叫已註冊的回呼，在工作停止時傳送 `tasks.cancel`。`TaskHandle.wait` 只觀察回呼結果，不釋放所有權；`TaskHandle.dispose` 要求取消、等待回呼與原生清理，並釋放工作所有者。如果回呼自身要求釋放，Host 會延遲父範圍釋放，避免自己等待自己的死鎖。正常完成、取消與故障保持不同狀態；故障會繼續占有資源，直到停用時回報。

四種 SDK 的入口分別為 Python 與 TypeScript 的 `ctx.tasks.run` / `wait` / `dispose`，Go 的 `ctx.Tasks.Run` / `Wait` / `Dispose`，以及 Rust 的 `Context::run_task` / `TaskHandle::wait` / `dispose`。每個控制代碼都應登記為 effect。Go 與 Rust WebAssembly 仍採協作式取消；程式模組必須回到 Host 通訊迴圈才能收到取消通知。

## 原生所有的資源範圍

協定 v2 的 SDK 可以建立有界的原生資源範圍，並在 SDK 處理程序中保留資源釋放回呼：

```python
scope = await ctx.resources.scope("cache")
ctx.effect(scope)
resource = await scope.acquire("cache-entry", release)
ctx.effect(resource)
```

`create` 建立子範圍，`acquire` 佔用一個原生 effect 名額。範圍與資源控制代碼都歸啟用執行個體所有，應登記為 effect。停用時，如果原生父範圍正在結束，Host 可能傳回 `{ "deferred": true }`；SDK 會繼續保留回呼，直到 Host 呼叫並完成它。釋放回呼會收到取消訊號，Host 會一直保留原生所有權直到回呼傳回；回呼不能同步釋放同一個資源。Python、TypeScript、Go 與 Rust 提供相同的範圍、取得與釋放形狀；Go 與 Rust WebAssembly 的回呼必須回到 Host 通訊迴圈才能接收取消通知。

## 服務呼叫

目前協定提供型別化訊息、日誌、effects，以及明確列出的 Host 服務呼叫。使用 contract 前必須在模組宣告中獲得授權。針對目前取出產生清單：

```sh
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- contracts schema --out artifacts/contracts.json
```

透過 `rpc_shape` 檢查可呼叫操作，透過 `provider_rpc_shape` 檢查可實作操作。提供者必須實作全部方法；無歧義時使用方法名稱，多載使用完整 schema method id。屬性存取子採用 `get_ReplyAfterInventory` 這樣的名稱。不支援的形狀會明確失敗。委派、參考參數、通用資源介接、封包編解碼器與接收 `IPluginContext` 的提供者方法尚不在這套服務橋接的範圍內。

## 提供與裝飾服務

以下註冊需要協定 v2，並參與原生相依圖：

| 宣告 | 註冊模式 | 行為 |
| --- | --- | --- |
| `provides` | `singleton` | 單一實作，不接受裝飾層 |
| `provides` | `decoratable` | 單一實作，允許已宣告的裝飾層 |
| `provides_many` | `many` | 多個有序實作；優先權高者在前，同級依註冊順序 |
| `decorates` | `decorate` | 包裝選取的內層服務，基礎服務必須允許裝飾 |

例如，Python 資料列可以裝飾原生原則。將其與允許裝飾的基礎提供者一同掛載：

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

範例保留內層結果；原則也可以傳回自己的合法結果。內層服務僅屬於本次呼叫，並保留已選取的裝飾鏈，不能保存到後續呼叫中使用。Host 會拒絕跨啟用或已結束的呼叫權杖。註銷註冊不會讓使用端已經持有的服務失效，因此回呼目標會保留到提供者啟用結束。

| SDK | 註冊 / 裝飾 / 選取全部實作 |
| --- | --- |
| Python | `ctx.services.provide` / `decorate` / `get_all` |
| TypeScript | `ctx.services.provide` / `decorate` / `getAll` |
| Go | `ctx.Services.Provide` / `Decorate` / `GetAll` |
| Rust | `ctx.provide_service` / `decorate_service` / `get_all_services` |

## 有序集合與取消

宣告 `requires_many` 要求至少一個實作；`optional_many` 允許空集合。TypeScript 選項寫作 `requiresMany`、`optionalMany`，Go 寫作 `RequiresMany`、`OptionalMany`。集合提供數量，並按從零開始的索引呼叫成員；所選實作及順序在釋放前保持穩定。集合控制代碼僅屬於一次啟用，與其他控制代碼共用容量限制。把釋放操作登記為 effect；Rust 集合使用 `effect_fn`。

提供者回呼接收協作取消訊號：Python 為 `call.cancellation`，TypeScript 為 `call.signal`，Go 為 `call.Context`，Rust 為 `call.is_cancelled()`。收到取消後應及時傳回。WebAssembly 在處理 Host 呼叫時接收通知，忙碌運算不能假定存在背景取消執行緒。逾時會使啟用失敗，但已進入的工作仍受管理，直到實際回應、原生操作完成或傳輸終止。回呼傳回後，Host 仍等待先前已進入的內層呼叫結束。

## 共用事件

協定 v2 透過本次啟用的原生事件匯流排處理 SDK 事件。原生與 SDK 監聽器參與同一個優先權順序與分派快照。分別宣告 `subscribes` 與 `publishes`，指定事件 `type`、明確的 `mode`，並為傳回結果的模式指定 `result_type`。從產生清單的 `events` 與 `event_result_types` 查詢事件與結果型別；短名稱有歧義時使用完整限定名稱。包含原生連線、委派或其他不支援值的型別需要專用介接器，會在宣告時被拒絕。

| 發佈模式 | 監聽器與行為 |
| --- | --- |
| `emit` | 僅同步監聽器；記錄監聽器錯誤後繼續分派 |
| `serial` | 同步及非同步監聽器，依序等待 |
| `parallel` | 同步及非同步監聽器，一起等待非同步工作 |
| `waterfall` | 攔截器包裝末端回呼，可以傳回替代結果 |
| `bail` | 首個非 null 的參考型別結果勝出；null 表示繼續，處理常式錯誤會記錄 |

訂閱模式為 `sync`、`async`、`waterfall`、`bail`。高優先權先執行，同級保持註冊順序。在 `serial` 與 `parallel` 中，同步監聽器錯誤仍依原生規則記錄後繼續；非同步錯誤向上傳播，但因移除監聽器引起的取消除外。必須選擇原生發佈方實際使用的模式。通知監聽器不傳回值；waterfall 傳回宣告型別的非 null 結果；只有 bail 允許透過 null 繼續。

下列 Python 原則觀察既有的裝備決策管線，保留原結果。將它掛載到會發佈裝備選擇事件的 profile：

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

`call.next()` 呼叫已經選取的下游管線。在本次回呼仍有效時可以重複呼叫，與原生行為一致；每次都會重新執行下游工作。直接傳回合法決策即可短路。事件資料跨邊界時按值傳輸，修改本機內容不會修改發佈者的物件；原則變化應透過事件/結果契約表達。

| SDK | 訂閱 / 發佈 | 結果宣告 |
| --- | --- | --- |
| Python | `ctx.events.on` / `ctx.events.publish` | `EventSpec(type, mode, result_type)` |
| TypeScript | `ctx.events.on` / `ctx.events.publish` | `{ type, mode, resultType }` |
| Go | `ctx.Events.On` / `ctx.Events.Publish` | `EventSpec{Type, Mode, ResultType}` |
| Rust | `ctx.on_event` / `ctx.publish_event` | `EventSpec::new(type, mode).with_result(result_type)` |

Waterfall 發佈必須提供傳回宣告結果的末端回呼，其他模式拒絕末端回呼。每個訂閱控制代碼都交給 effect 管理。在監聽器內部使用 `call.dispose(handle)`（Go：`call.Dispose(handle)`）移除自身訂閱，以恢復原生回呼內容，避免等待自己結束；外部移除仍等待已進入的工作完成。續接與自身移除權杖僅屬於一次有效呼叫，跨啟用或已經結束的權杖會被拒絕。

事件回呼提供與服務回呼相同的協作取消 API。取消只要求停止，實際完成後才釋放所有權。逾時會使資料列失敗，已進入的原生工作仍受管理直到結束。發佈會把末端回呼保留到原生分派實際完成，即使 Python 呼叫端取消了自己的 await。同步及 bail 監聽器會在發佈方執行路徑上進行封鎖式 RPC，應保持短小。提供者註冊、保留的事件回呼及臨時末端回呼共用本次啟用的有界回呼預算（`max_provider_callbacks`，由資料列的 `effect_capacity` 授予）。移除監聽器後，其回呼保留至啟用清理；註冊失敗的預留與已完成的臨時末端回呼會釋放。

## 可靠性

Host 提交變更後要求仍可能逾時。逾時代表結果未知，應先透過讀取操作核對狀態，再決定是否重試。日誌中保留 request id，停用後停止傳送，並把 Host 中斷視為本次啟用失敗。

`sdk/` 下的範例是目前協定的相容性參考，不能替代 profile 中的 capability 與權限宣告。
