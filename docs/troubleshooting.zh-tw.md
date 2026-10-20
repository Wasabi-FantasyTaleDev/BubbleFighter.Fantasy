---
title: "疑難排解"
description: "依序診斷建置、設定、啟動、網路、持久化與 SDK 故障。"
updated: 2026-10-20
---

## 首次安裝拒絕代理請求 {#setup-proxy-refused}

**現象。** 尚未建立管理員時，存取 `/api/setup/*` 傳回 HTTP 403 和 `setup_proxy_refused`。**原因。** 請求包含 `X-Forwarded-For`、`X-Forwarded-Host`、`X-Forwarded-Proto` 或 `Forwarded`，反向代理可能讓遠端請求看起來來自本機。**處理。** 直接在伺服器上開啟安裝頁面，不經過代理。建立管理員後，此首次安裝限制不再適用，正常身分驗證仍然必需。

管理後台的登入失敗依連線來源 IP 分別計數，沿用 `installation.login_attempts` 和 `login_window_seconds`。`login_address_capacity` 預設保留 1024 個位址；滿額時替換最早的時間視窗。轉發標頭不能改變登入計數使用的位址。

## 本頁內容

本頁把「伺服器不運作」化為一次有順序的排查：先檢查什麼、證據在哪裡、改哪一處能解決問題。每個症狀都遵循同一結構：症狀、檢查、可能原因、修正、確認已復原。它同時適用於原始碼檢出與發行目錄，但不能取代以真實遊戲用戶端進行的測試。

**適用對象。** 首次啟動伺服器的維運人員，以及建置或驗證關卡失敗的貢獻者。

**開始之前。**

- 原始碼檢出在儲存庫根目錄操作，發行建置在發行目錄操作。
- 本機檢查時，將 `.env.example` 複製為被 git 忽略的 `.env`，並把 `BFF_MYSQL_PASSWORD` 設為非空的臨時值。即使停用 MySQL，profile 插值也需要這個變數。
- 本頁所有本機 Host 命令都帶 `--patch tools/local-checks/isolated-memory.patch.yaml`。此修補檔會停用 `persistence.mysql` 並啟用 `persistence.memory`，因此開發者自己的 `.env` 或本機修補檔不會把伺服器指向真實資料庫。
- 先讀第一個錯誤，每次只改一處，並保留失敗輸出。

## 首先檢查

| 症狀 | 首先檢查 | 可能區域 | 參見 |
| --- | --- | --- | --- |
| 建置沒有完成 | 重新執行 `dotnet build BubbleFighter.Fantasy.sln -c Release`，讀第一個錯誤 | 工具鏈、套件還原、警告視為錯誤 | 建置與工具鏈 |
| `error:` 提到環境變數或 profile | 帶隔離修補檔執行 `--dump-config` | profile、修補檔、`.env` 插值 | 設定 |
| `error: Server startup did not converge` | 讀取其中每個資料列 id 與狀態 | 啟用、相依套件、用戶端資料 | 啟動與啟用 |
| 某個資料列一直是 `Pending` | `/api/plugins`，讀取 `state` 與 `error` | 缺少服務、提供者或定義來源 | 啟動與啟用 |
| 某個資料列是 `Failed`、`CleanupIncomplete` 或 `CleanupFailed` | 同一份 JSON，再看主控台記錄 | 啟用或清理所有權 | 啟動與啟用 |
| 用戶端無法連線 | 監聽連接埠與對外公布位址 | 繫結、NAT、防火牆、relay | 連接埠與網路 |
| 管理 API 或 WebUI 無法存取或傳回 401 | `/health`、`BFF_ADMIN_HOST` | 管理監聽與登入 | 連接埠與網路 |
| 資料庫錯誤，或重新啟動後資料消失 | `--dump-config`；持久化資料列 | MySQL、SQLite、移轉、記憶體儲存 | 持久化 |
| `clientdata` 啟用失敗 | 錯誤中提供的兩個路徑 | 用戶端資料檔案與 `BFF_CLIENT_DATA` | 用戶端資料 |
| SDK 模組失敗或逾時 | 模組 stderr 與 loader 資料列設定 | JSON-RPC stdout、協定、限制 | SDK 模組 |

如果都不符合，直接查看「回報問題」一節。

## 建置與工具鏈問題

### .NET SDK 缺少或不是版本 10

**症狀。** 找不到 `dotnet`，或建置以 `net10.0` 為目標時報錯。**檢查。** 執行 `dotnet --info`；它會列出已安裝的 SDK 與執行階段，而所有專案都以 `net10.0` 為目標。**可能原因。** 未安裝 .NET 10 SDK，或 `PATH` 仍指向舊版本。**修正。** 安裝 .NET 10 SDK 並開啟新的終端機。**確認已復原。** `dotnet --version` 顯示 10.x，且 `dotnet build BubbleFighter.Fantasy.sln -c Release` 以 `0 Warning(s)` 和 `0 Error(s)` 結束。

### 套件還原或編譯失敗

**症狀。** 建置在 NuGet 還原階段因網路或來源錯誤停止，或編譯器報告警告並導致失敗。**檢查。** 讀第一個還原錯誤；再判斷訊息是否為編譯器警告。所有專案都啟用 `TreatWarningsAsErrors`，且 `GenerateDocumentationFile` 要求每個公開型別與成員都有真實的 XML 摘要。**可能原因。** 無法存取套件來源、代理伺服器問題、套件快取損毀、缺少 XML 註解、未使用的變數或可空性錯誤。**修正。** 以 `dotnet restore` 重試，修正網路或代理設定，並修正警告的根本原因——絕不要為了讓建置變綠而停用警告。**確認已復原。** 建置回報零警告、零錯誤，且沒有套件或還原錯誤。

有一種還原錯誤的原因不同。`NU1900`（`Error occurred while getting package vulnerability data`）表示 NuGet 的套件稽核無法連線到 nuget.org；每次還原都會執行稽核，而在本專案中它的警告視為錯誤。若機器無法連線到 nuget.org，但所需套件已在本機快取中，可使用 `dotnet build BubbleFighter.Fantasy.sln -c Release -p:NuGetAudit=false` 建置。這只會略過線上弱點查詢，不會略過任何編譯器警告。發布版本之前，請在啟用稽核的情況下正常建置一次。

### 發行建置在另一台機器上無法啟動

**症狀。** 編譯通過的建置在目標機器上立即結束。**檢查。** 在該機器上執行 `dotnet --list-runtimes`，確認存在 `Microsoft.AspNetCore.App 10.x`；Host 使用 ASP.NET Core 共用架構。**可能原因。** 機器上沒有 ASP.NET Core Runtime 10——相依於框架的發行不會自帶執行階段。**修正。** 為目標平台安裝 ASP.NET Core Runtime 10；發行指令碼的說明是 `The deployment server requires the x64 ASP.NET Core Runtime 10.0, which includes the .NET runtime.` **確認已復原。** 從發行目錄啟動，主控台會回報 `Server started with <N> active row(s).`

### 驗證工具鏈不完整

**症狀。** `python tools/ci/serial-gates.py` 在測試開始前停止，或某個 SDK 一致性關卡被回報為略過。**檢查。** 該關卡需要 `PATH` 中有 `dotnet`、`node`、`npm`、`go`、`cargo` 和 `rustup`；缺少時會印出 `Missing validation toolchains: <names>`。**可能原因。** 開發機器只安裝了 .NET 建置所需的部分。**修正。** 安裝缺少的工具鏈；對 Rust SDK 及其 WebAssembly 測試，加入失敗訊息提供的目標：`rustup target add wasm32-wasip1`。**確認已復原。** 重跑不再印出 `An SDK conformance gate was skipped.`，並以一行 `PASS public gates:` 或 `PASS complete gates:` 結束。

## 設定與插值問題

### 環境變數沒有值

**症狀。** `error: File '<file>', row '<row id>': environment variable '<NAME>' is not set and has no default.` **檢查。** 在錯誤所指的 profile、bundle 或修補檔中尋找沒有 `:-default` 的 `${NAME}`，然後檢查 `.env` 與處理程序環境。除 `start` 外，每個命令都會在組合 profile 之前，把儲存庫根目錄的 `.env` 套用到未設定的處理程序變數；已存在的環境變數優先。**可能原因。** 變數既沒有匯出也不在 `.env` 中，而 `BFF_MYSQL_PASSWORD` 沒有預設值，因此任何遊戲或管理 profile 都無法在缺少它時完成組合。**修正。** 為該命令設定變數，或寫入被 git 忽略的 `.env`；絕不提交真實憑證。**確認已復原。** 組合成功，`--dump-config` 印出生效的資料列。

### MySQL 密碼為空

**症狀。** `error: Activation of row 'persistence.mysql' (plugin 'persistence.mysql') failed: Invalid configuration for row 'persistence.mysql': connection_string is missing Password (BFF_MYSQL_PASSWORD is required and must not be empty).` **檢查。** 複製來的 `.env.example` 含有 `BFF_MYSQL_PASSWORD=`，因此插值會以空字串成功，失敗只會在该資料列啟用時出現。**可能原因。** `.env` 中留下了空佔位值。**修正。** 本機檢查使用非空的臨時值，並加上隔離修補檔以免連線資料庫；真實憑證只用於刻意設定的持久化部署。**確認已復原。** 加上隔離修補檔後，`persistence.memory` 啟用，記錄回報記憶體持久化已啟用。

### profile、修補檔或選項名稱錯誤

**症狀。** 出現下列之一：`error: Profile '<id>' not found: expected '<path>'.`、`error: Patch file '<path>' does not exist.`、`error: Unknown option '<option>'.`、`error: Unknown command '<command>'.`、`error: Option '<option>' requires a value.` **檢查。** 執行 `bff profiles list` 與 `bff --help`；profile id 是 `profiles/` 下的目錄名稱，說明文字列出可接受的選項。**可能原因。** 拼字錯誤、profile 不存在，或把選項放在不接受它的命令之後。**修正。** 改正 id 或選項；本機執行時保留 `--patch tools/local-checks/isolated-memory.patch.yaml`。**確認已復原。** `bff profiles list` 印出該 profile 與描述，改正後的命令完成組合。

### 設定鍵未知或無效

**症狀。** `error: Invalid configuration for row '<row id>': unknown key '<key>'`，或同一資料列的型別轉換、驗證失敗。**檢查。** 模組選項是對應到 PascalCase 屬性的 snake_case YAML 鍵；模組的選項類別列出它接受的鍵。檢查訊息中的資料列 id 及其對應的 `plugin` 欄位。**可能原因。** 鍵的拼字錯誤、使用舊指南中的鍵，或值的型別、範圍不正確。**修正。** 改正鍵或值；一個資料列的所有失敗會合併在一則訊息中，請全部修正。**確認已復原。** `--dump-config` 成功並顯示改正後的值。

### 生效設定與預期不符

**症狀。** 伺服器表現得像某資料列被啟用，或使用了與你修改不同的連接埠。**檢查。** 各層依序套用：bundle、profile 修補檔，最後是命令列修補檔。用不啟動 Host 的方式印出結果：

```sh
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- run --profile kr-812 --patch tools/local-checks/isolated-memory.patch.yaml --dump-config
```

**可能原因。** 後面的層取代了該資料列或它的 `disabled` 旗標；隔離修補檔是最後一層，會啟用記憶體持久化。**修正。** 修改應該擁有該值的層；不要把同一個覆寫加到兩層。**確認已復原。** 傾印顯示預期的旗標與值。傾印可能包含插值後的機密（例如 MySQL 連線字串），分享前請去識別化。

## 啟動、啟用與模組狀態

每次啟動嘗試後，每個資料列都會發布狀態。從主控台記錄或管理 API 讀取：

```powershell
Invoke-RestMethod http://127.0.0.1:8080/api/plugins | Select-Object rowId, plugin, state, error
```

```sh
curl -s http://127.0.0.1:8080/api/plugins
```

`/api/plugins` 為每個執行階段執行個體傳回一個物件，欄位包括 `rowId`、`plugin`、`source`、`state`、`error`、`codeRefreshPolicy`、`codeLifetime`、`restartRequiredForReclamation`、`isRetiring`、`overrides`、`version`、`contractVersions` 和 `supportedBuilds`。只有 `Active` 是健康的執行狀態。

| 狀態 | 意義 | 處理方式 |
| --- | --- | --- |
| `Active` | 已啟用，effect 生效 | 無需處理 |
| `Pending` | 已被期望，但缺少服務、提供者或定義來源 | 讀 `error`，修正相依性 |
| `Failed` | 啟用或自有工作失敗 | 讀 `error`，修正後重新載入或重新啟動 |
| `Unloading` | effect 已關閉，清理進行中 | 等待；不要再次啟動該資料列 |
| `Unloaded` | 已停用或已卸載，沒有生效的 effect | 停用資料列的正常狀態 |
| `CleanupIncomplete` | 清理超過時限仍在進行或被取消 | 所有權仍被持有；重試前先調查 |
| `CleanupFailed` | 清理回報了錯誤 | 不能假定資源已釋放；重新啟動被拒絕 |
| `WorkFailed` | 自有背景工作失敗 | 資源在清理完成前仍被持有 |

### 伺服器啟動未收斂

**症狀。** 處理程序以結束碼 1 結束，並輸出 `error: Server startup did not converge (WaitingForDependencies): <row id>: Pending (<diagnostic>); <row id>: Failed (<diagnostic>).`；主控台還會記錄 `Lifecycle revision <N> for '<row id>' did not converge.` **檢查。** 讀取列出的每個資料列，然後把 `/api/plugins` 與 `--dump-config` 的傾印對照。**可能原因。** 至少一個未停用的資料列沒有達到 `Active`：缺少提供者、缺少用戶端資料、連接埠錯誤，或某個值無法繫結。**修正。** 修正清單中的第一個資料列，而不是最後一個；如果某資料列是刻意停用的，確認生效傾印確實停用了它。**確認已復原。** 主控台回報 `Server started with <N> active row(s).`，且 `GET /health` 傳回 `{"status":"ok", ...}`。

### 某個資料列一直是 Pending

**症狀。** 該資料列始終不啟用，`error` 說明原因，通常是 `Waiting for services: <service types>; provider rows: <rows>; override rows: <rows>.` 或 `Definition source for plugin '<plugin>' is unavailable.` **檢查。** 找出訊息中提供的提供者或定義來源，檢查其資料列狀態。**可能原因。** 必要服務、選用提供者或處理常式置換目標被停用、失敗或不在 profile 中；或發布該模組的 SDK loader 資料列尚未啟用。**修正。** 啟用或修正訊息中的提供者資料列，或在不想要該能力時移除消費者。**確認已復原。** 重新載入或重新啟動後該資料列變為 `Active`，啟動收斂。

### 某個資料列是 Failed

**症狀。** `error: Activation of row '<row id>' (plugin '<plugin>') failed: <cause>.` **檢查。** 讀取 `error` 欄位與主控台中的完整原因；外層訊息提供資料列與模組，內層訊息提供實際問題——用戶端資料、MySQL、SQLite、連接埠、未知鍵或未知模組。**可能原因。** 模組在啟用時驗證的任何內容。**修正。** 修正內層原因。若出現 `Automatic activation attempt limit <N> reached for row '<row id>'.`，該資料列會停止自動重試，修正後需要明確重新載入。**確認已復原。** 該資料列變為 `Active`。

### CleanupIncomplete 或 CleanupFailed

**症狀。** 正在卸載的資料列在 `/api/plugins` 中顯示 `CleanupIncomplete` 或 `CleanupFailed`，其 `error` 欄位與主控台記錄提供清理失敗原因。觸發清理的卸載或重新載入呼叫也可能失敗，並回報 `Row '<row id>' teardown is <state>; ownership and dependencies are retained.`，其中 `<state>` 是該資料列到達的狀態。**檢查。** 讀取該資料列的 `error` 與主控台記錄；`CleanupIncomplete` 表示清理超過時限仍在進行或呼叫端已離開，`CleanupFailed` 表示自有的清理器擲出例外。**可能原因。** 釋放時阻塞的資源、不會結束的自有背景工作，或失敗的清理器。**修正。** 不要強制該資料列重新啟動：所有權仍在時重新啟動會被拒絕（`Activation is refused while failed work or cleanup ownership remains: <row ids>.`）。讓清理自行完成或停止伺服器；若無法完成，重新啟動處理程序並修正模組。**確認已復原。** 狀態變為 `Unloaded`，伺服器乾淨關閉。

### 模組未知或資料列 id 重複

**症狀。** `error: Row '<row id>' references unknown plugin '<plugin>'.` 或 `error: Duplicate row id '<row id>' in profile '<profile id>'.` **檢查。** 執行 `bff plugins list` 尋找模組 id；確認其組件位於模組根目錄下（Host 旁的 `plugins/`，或以 `--plugin-root` 指定的目錄）。對於重複 id，找出貢獻第二個資料列的 bundle 或修補檔。**可能原因。** 模組建置過期或缺少、模組目錄未複製、`plugin` 欄位拼字錯誤，或修補檔重新插入了已存在的 id。**修正。** 重新建置方案（Host 建置會以 `artifacts/plugins` 取代自己的 `plugins` 目錄）；懷疑副本過期時使用 `--no-incremental`；刪除或重新命名重複資料列。**確認已復原。** `bff plugins list` 印出 id、版本、支援的建置與來源，`--dump-config` 只列出該資料列一次。

### 啟用時缺少必要服務

**症狀。** 失敗資料列回報 `Row '<row id>' cannot <verb>; missing service(s): <service types>.` **檢查。** 找出應發布該服務的資料列；相依性是宣告的介面，而不是資料列 id。**可能原因。** 提供者資料列被停用、失敗或不存在，或透過 `isolated_services` 位於獨立的服務 realm。**修正。** 啟用提供者，或從消費者的宣告中移除該服務；當提供者與消費者應位於不同樹時，檢查 `parent_id` 與 `isolated_services`。**確認已復原。** 提供者先變為 `Active`，消費者在下次協調時啟用。

## 連接埠、繫結位址、NAT 與管理驗證

### 連接埠已被占用

**症狀。** 監聽資料列啟用失敗，例如 `error: Activation of row 'game-listener' (plugin 'net.tcp') failed: <operating system socket error>`。**檢查。** 變更前先找出占用者：Windows 上執行 `Get-NetTCPConnection -LocalPort 7110 -State Listen | Select-Object LocalAddress, LocalPort, OwningProcess`，再執行 `Get-Process -Id <owningProcess>`；Linux 上執行 `sudo ss -ltnp | grep :7110`。**可能原因。** 另一個伺服器執行個體、同一 profile 的第二個終端機，或無關的處理程序。**修正。** 只停止已辨識的處理程序，或以該 profile 的變數變更連接埠（例如 `KR_812_GAME_PORT`）。不要結束機器上所有 .NET 處理程序。**確認已復原。** 記錄出現 `Row 'game-listener' listening on <address> (protocol <profile>/game).`，連接埠處於監聽狀態。

### 用戶端無法通過 NAT 或防火牆連線

**症狀。** 伺服器在監聽，但另一台機器上的用戶端無法連線。**檢查。** 監聽繫結 `${BFF_BIND_HOST:-0.0.0.0}`，而告知用戶端的是 center gate 位址與 relay 公布中的 `${BFF_PUBLIC_HOST:-127.0.0.1}`。對外位址預設是回送，只在本機可用。在 `--dump-config` 中檢查兩個值，再測試連通性：Windows 用 `Test-NetConnection <public-host> -Port 7110`，Linux 用 `nc -vz <public-host> 7110`。**可能原因。** `BFF_PUBLIC_HOST` 保留預設值、防火牆攔截連接埠，或連接埠轉送與公布連接埠不一致。**修正。** 把 `BFF_BIND_HOST` 與 `BFF_PUBLIC_HOST` 設為用戶端可達的位址，只開放所選 profile 需要的連接埠，並轉送公布的連接埠號。**確認已復原。** 用戶端到達登入或 center 連線，連線期間 `/api/sessions` 顯示該工作階段。

### relay 連線失敗

**症狀。** modern 建置可以登入，但對戰連線失敗，或 relay 連接埠始終不監聽。**檢查。** `kr-1256` profile 有 `${KR_1256_GAME_PORT:-7100}` 和 `${KR_1256_RELAY_PORT:-7101}` 兩個監聽，並公布 `relay_advertise_ipv4` 與 `relay_advertise_port`（預設 `${KR_1256_RELAY_PUBLIC_PORT:-7101}`）。對照監聽與公布值。**可能原因。** NAT 後公布連接埠與監聽連接埠不一致、修補檔停用了 relay 資料列，或用戶端是 legacy 建置。**修正。** 讓公布的 relay 位址與連接埠可達，並保持兩個 relay 值一致。legacy 建置目前沒有 relay 監聽（legacy bundle 中的 `No relay listener on the legacy builds yet.`），因此不要在那裡期待 relay 連線。**確認已復原。** relay 連接埠監聽，對戰連線出現在 `/api/sessions` 且 `matchBound` 已設定。

### 管理 API 或 WebUI 無法存取或未授權

**症狀。** 瀏覽器打不開 WebUI，或 `/api/...` 傳回 401 和 `{"error":"unauthorized"}`。**檢查。** 管理資料列繫結 `${BFF_ADMIN_HOST:-127.0.0.1}:${BFF_ADMIN_PORT:-8080}`，預設為回送。以 `start` 啟動的伺服器對每個受保護的 `/api` 要求都需要已登入的管理員工作階段；`/health` 與 WebUI 檔案保持開放。首次安裝期間，在管理員帳號建立之前 `/api/plugins` 會傳回 401——這是驗證模型，而非伺服器故障；`/health` 與 `/api/setup/status` 仍可存取。**可能原因。** 監聽器在回送上而瀏覽器在別處、工作階段已過期、給 `run --profile` 伺服器設定了非回送位址（它沒有帳號，會拒絕該位址），或非回送的安裝監聽器未設定啟動權杖。**修正。** 重新登入；在以 `start` 啟動的伺服器上，把 `BFF_ADMIN_HOST` 設為所需介面，並為非回送的安裝監聽器將 `bootstrap_token`（`BFF_SETUP_TOKEN`）設為至少 32 個字元。否則該列會以 `A non-loopback installation listener requires an explicit bootstrap_token of at least 32 characters.` 失敗。**確認復原。** `/health` 傳回 `{"status":"ok", ...}`，且登入後的 WebUI 列出模組。

## 持久化與移轉

### MySQL 連線或驗證失敗

**症狀。** `error: Activation of row 'persistence.mysql' (plugin 'persistence.mysql') failed: Failed to open MySQL at <host>:<port> database '<database>' as '<user>'.` **檢查。** 檢查 `BFF_MYSQL_HOST`、`BFF_MYSQL_PORT`、`BFF_MYSQL_USER`、`BFF_MYSQL_PASSWORD` 和 `BFF_MYSQL_DATABASE`，並確認伺服器可達。**可能原因。** 主機、連接埠、使用者或密碼錯誤，伺服器不可達，或資料庫不存在。**修正。** 為刻意設定的持久化部署修正這些變數；絕不要把本機檢查指向生產資料庫。**確認已復原。** 記錄回報 `MySQL persistence connected to <host>:<port>/<database> as <user>.`

### 移轉失敗

**症狀。** 讀取或套用內嵌移轉時啟用失敗，訊息為 `Embedded SQL resource '<resource>' is not named NNNN_description.sql.`、`Duplicate embedded migration version <version>.`、`No embedded SQL migrations were found.` 或 `Embedded migration '<resource>' is empty.` 之一。**檢查。** 移轉是內嵌資源，在模組以 `auto_migrate: true` 啟用時依檔案名稱順序套用。**可能原因。** 模組建置損毀，或移轉資源被改名、清空。**修正。** 不要手動修改執行中的 `schema_migrations` 資料表；從乾淨檢出重新建置模組。沒有降級移轉路徑，如需回到舊建置，請還原 schema 變更前的備份。**確認已復原。** 啟用時為每個待套用移轉記錄一行 `Applied migration <version> (<name>).`，隨後是連線成功行。

### 同時啟用了兩個持久化提供者

**症狀。** 持久化資料列失敗並回報 `Service '<contract>' is already registered in realm <id>.` **檢查。** `persistence.memory`、`persistence.sqlite` 和 `persistence.mysql` 中只能啟用一個。檢查 `--dump-config` 中的 `disabled` 旗標；隔離修補檔啟用 memory 並停用 MySQL，但保持 SQLite 停用。**可能原因。** 修補檔啟用了第二個提供者卻沒有停用第一個。**修正。** 在同一修補檔中停用不用的提供者。**確認已復原。** 恰好一個持久化提供者為 `Active`，其餘為 `Unloaded`。

### SQLite 路徑或鎖定問題

**症狀。** SQLite 資料列啟用失敗，或寫入以 `SQLite transaction committed but WAL truncation failed; further operations are refused.` 失敗。**檢查。** `database_path` 必須是包含檔案名稱的絕對路徑；相對路徑會以 `database_path must be an absolute file path.` 失敗。把存檔放在發行目錄與記錄目錄之外，每個存檔只執行一個伺服器。鎖定等待為 `busy_timeout_ms`，預設 5000。**可能原因。** 相對路徑、唯讀目錄，或兩個處理程序開啟同一存檔。**修正。** 修正路徑與權限，停止第二個處理程序。**確認已復原。** 記錄回報 `SQLite save active; capacity <players> players, <accounts> accounts, <bytes> main-file bytes.`

### 重新啟動後資料為空

**症狀。** 停止並重新啟動伺服器後，帳號、物品或進度消失。**檢查。** `--dump-config` 顯示啟用的持久化資料列。使用隔離修補檔或任何啟用 `persistence.memory` 的設定時，狀態只存在於處理程序內；啟用記錄會說明：`In-memory persistence active for up to <players> players and <movement actors> movement actor(s); state is process-local and resets on restart.` **可能原因。** 誤以為記憶體持久化是持久的。**修正。** 需要持久狀態時啟用 `persistence.sqlite` 或 `persistence.mysql` 並停用其他提供者；本機實驗可保留隔離修補檔並接受重置。**確認已復原。** 使用持久化提供者重新啟動後，同一帳號可以登入。

### 還原備份

**症狀。** 需要撤銷一次資料庫變更。**檢查。** 伺服器沒有還原資料庫的命令；還原是資料庫層級的操作，操作前必須停止伺服器。**可能原因。** 錯誤的移轉、誤寫或失敗的升級。**修正。** 還原變更前的備份，然後用與還原的 schema 相符的設定啟動伺服器。不要編輯 `schema_migrations` 或刪除資料列來偽造回滾。**確認已復原。** `/health` 正常，持久化資料列為 `Active`，預期的帳號與資料存在。

## 用戶端資料

### clientdata 資料列啟用失敗

**症狀。** `error: Activation of row 'clientdata' (plugin 'clientdata') failed: clientdata: neither data_directory '<base>/client-data/<profile>/data' nor raw_directory '<base>/client-data/<profile>/dataraw' exists. Provision the configured client-data directories before enabling this row.` **檢查。** 遊戲 profile 解析 `${BFF_CLIENT_DATA:-client-data}/${BFF_PROFILE_ID}/data` 和 `.../dataraw`，相對於工作目錄。把錯誤中的兩個路徑與磁碟檔案和 `--dump-config` 中的 `BFF_CLIENT_DATA` 對照。**可能原因。** 沒有為該建置提供用戶端資料、`BFF_CLIENT_DATA` 指向別處，或伺服器從不同目錄啟動。**修正。** 提供相符的本機用戶端資料，把 `BFF_CLIENT_DATA` 指向其根目錄，或在只需要管理與儲存時使用 `management` profile。儲存庫不包含用戶端或其專有資料。**確認已復原。** `clientdata` 資料列變為 `Active`，遊戲 profile 以 `Server started with <N> active row(s).` 啟動。

### 用戶端檔案缺少或版本不相符

**症狀。** 功能以 `clientdata: no client file '<path>' in the archives or the loose files.` 失敗，或 `/api/logs` 記錄 `Session <id> reported client version <reported>, expected <expected>.` **檢查。** 檔案缺少時，確認 `data/` 中的相對路徑或 `dataraw/` 中的鬆散檔案使用用戶端自己的拼字；鬆散檔案優先於封存檔。版本不相符時，比較用戶端的 `ClientVersion_Major` 與 profile 的 `client_build` 及 profile id。**可能原因。** 用戶端資料不完整、封存檔未複製，或用戶端建置與所選 profile 不相符。**修正。** 複製該建置的完整資料，並啟動與用戶端建置相符的 profile；不要為了讓 profile 接受程式碼不支援的建置而修改它。**確認已復原。** 功能能讀取檔案，工作階段連線時不再出現版本不相符警告。

### 安裝精靈的用戶端資料匯入失敗

**症狀。** 安裝精靈回報匯入失敗，或 API 以 400、413 或 409 傳回 `{"error":"client_data_invalid"}`、`{"error":"client_data_limit"}` 或 `{"error":"client_data_busy"}`。**檢查。** 匯入接受已整理的資料夾或傳統 ZIP。預設限制為一份副本上傳 8 GiB、解壓縮 16 GiB、32,768 個檔案或 ZIP 項目、保留 64 份副本、保留儲存 64 GiB、時限 20 分鐘。不支援 ZIP64 與分卷封存檔；`client_data_busy` 表示已有匯入在執行。**可能原因。** 檔案過多或過大、匯入器無法辨識的目錄配置，或並行的第二次匯入。**修正。** 在限制內匯入 `<build>/data` 與 `<build>/dataraw` 配置，部署確有需要時可在 `installation` 資料列的允許範圍內調整限制，並等待執行中的匯入結束後重試。**確認已復原。** 匯入傳回根路徑，相符 profile 的 `clientdata` 資料列啟用。

## SDK 模組問題

### SDK 模組處理程序無法啟動

**症狀。** `error: Activation of row '<row id>' (plugin 'loader.process') failed: SDK plugin process '<command>' could not start in '<directory>': <reason>` 或 `SDK plugin process '<command>' did not start.` **檢查。** 檢查 loader 資料列的 `plugins[].command`、`args`、`cwd` 和 `env`；含目錄部分的命令相對 `base_dir` 解析，其餘在 `PATH` 中尋找；缺少 `cwd` 目錄會以 `plugins[<n>].cwd: directory '<path>' does not exist` 設定失敗。**可能原因。** 解譯器或可執行檔未安裝、工作目錄錯誤，或 `PATH` 中沒有該命令。**修正。** 安裝執行階段、修正 `command`/`cwd`，或傳絕對路徑。**確認已復原。** 該資料列啟用，模組 id 以 `state` `Active` 出現在 `/api/plugins`。

### 模組向 stdout 寫入資料

**症狀。** `error: Activation of row '<row id>' (plugin 'loader.process') failed: SDK plugin process broke the protocol: stdout carried a line that is not JSON (<detail>); stdout is reserved for the protocol, use stderr for diagnostics.` **檢查。** 找出向 stdout 寫入資料的列印、橫幅或進度輸出。**可能原因。** 偵錯列印、函式庫橫幅或 stdout 上的進度列。**修正。** 所有診斷寫入 stderr；stdout 只保留 JSON-RPC 行。**確認已復原。** 模組完成握手並啟用。

### 協定或能力不相符

**症狀。** 宣告或協商失敗，例如 `SDK plugin source '<source>' answered initialize without a supported protocol_version (1 or 2).`、`protocol v1 allows only IMessageRouter in requires and optional.` 或 `requires_many and optional_many require protocol v2.` **檢查。** 對照模組資訊清單與 Host 支援的協定版本，以及各版本允許的宣告；型別化訊息、事件、提供者與裝飾器需要協定 v2。**可能原因。** 舊 SDK 或資訊清單固定在協定 v1，卻使用 v2 宣告。**修正。** 更新 SDK 或資訊清單，或把宣告限制在 v1 支援的範圍。**確認已復原。** 來源發布其定義，相依資料列啟用。

### 呼叫逾時或被拒絕

**症狀。** `SDK plugin <label> did not answer '<method>' within <n> ms (request_timeout_ms).` 或 `SDK plugin <label> already has <n> requests in flight (max_in_flight <n>).` **檢查。** 檢查 loader 資料列的 `request_timeout_ms` 與 `max_in_flight`，並觀察模組在該呼叫中做了什麼；事件迴圈被阻塞會在這裡顯現。**可能原因。** 處理常式過慢或阻塞，或突發超過在途上限。**修正。** 讓處理常式儘快傳回，只在有量測依據時調高上限，並且只重試冪等操作——逾時不會撤銷模組已提交的變更。**確認已復原。** 呼叫在設定的逾時內完成，在途計數保持在上限以下。

### WebAssembly 程式模組無法載入

**症狀。** `error: Activation of row '<row id>' (plugin 'loader.wasm') failed: WebAssembly plugin '<module>' exports no '_start'; build it as a WASI command (wasm32-wasip1 or GOOS=wasip1).` 或 `WebAssembly plugin '<module>' could not be loaded: <reason>`. **檢查。** 檢查 `loader.wasm` 資料列的 `plugins[].module` 與程式模組的建置目標。**可能原因。** 程式模組為錯誤目標建置，或路徑不存在。**修正。** 用 Rust SDK（`wasm32-wasip1`）或 Go SDK（`GOOS=wasip1`）建置 WASI 命令程式模組，然後修正程式模組路徑。**確認已復原。** 程式模組被接受，其定義啟用。

## 回報問題

提供足以讓他人生產重現的資訊。每個問題使用一份回報，範本如下：

```text
Source revision: <git rev-parse --short HEAD，或發行/套件版本>
Operating system: <Windows 11 x64 / Debian 13 x64 / 容器映像>
Profile: <profile id，或 bff profiles list 中對應的一行>
Command: <完整命令；本機檢查需包含 --patch tools/local-checks/isolated-memory.patch.yaml>
Expected: <原本應該發生什麼>
Actual: <實際發生了什麼，含結束碼>
First error: <第一行 error: 訊息>
Affected rows: <來自 /api/plugins 的 rowId、state 與 error>
Evidence: <主控台摘錄、TRX 結果、傾印路徑>
Source of the observation: <真實遊戲用戶端、自動測試，或兩者>
```

附加任何內容前，移除權杖、密碼、帳號名稱、玩家身分、公開位址以及包含個人資料的 packet 內容。不要附加用戶端資料、專有檔案或私有調查材料。`--dump-config` 輸出可能包含插值後的憑證，請去識別化。

請明確說明問題是在真實遊戲用戶端中觀察到，還是只在自動檢查中出現；自動檢查通過或失敗都不代表用戶端相容性。

## 後續

- [設定](configuration.md)：profile、修補檔、環境變數、連接埠與持久化。
- [部署](deployment.md)：為他人執行伺服器，以及升級與回滾。
- [模組](plugins.md) 與 [SDK 協定](sdk-protocol.md)：啟用與生命週期細節。
- [啟動器](launcher.md)：啟動器帳號、工作階段票據、啟動器 API 版本閘門以及配套啟動器自身的錯誤。
