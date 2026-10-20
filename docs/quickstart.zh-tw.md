---
title: "快速入門"
description: "從已建置的取出啟動隔離的本機伺服器，並確認它正在執行。"
updated: 2026-10-18
---

本頁使用你在[安裝](installation.md)中建置好的取出，啟動一個使用記憶體持續性的本機隔離伺服器。它先以無遊戲監聽的管理 profile `management` 完成第一次成功檢查，然後說明 `kr-812` 這類遊戲 profile 有何不同，以及這些檢查能證明什麼、不能證明什麼。

**本頁適合：** 已完成原始碼建置、希望在幾分鐘內跑起本機伺服器的讀者。

## 你將得到什麼

一個可以安全啟動與停止的本機伺服器處理程序，使用記憶體持續性，不連接任何真實資料庫。處理程序結束後帳號與進度都會重置。它不是可完整遊玩的伺服器，也不能證明遊戲用戶端可用。

`management` profile 只啟動管理 API 與 WebUI，沒有遊戲或中繼監聽，因此不需要用戶端資料即可執行。`kr-812` 這類遊戲 profile 還需要該版本對應的本機用戶端資料，而儲存庫不包含這些資料。

## 開始之前

- 已完成[安裝](installation.md)：解決方案以零警告建置成功，且建置輸出存在。
- 終端機位於儲存庫根目錄；本頁所有命令都在此執行。
- .NET 10 SDK 可用：`dotnet --version` 輸出 10.x 版本。
- 本機檢查修補檔存在。在 PowerShell 中用 `Test-Path tools/local-checks/isolated-memory.patch.yaml` 檢查，或在 POSIX shell 中用 `test -f tools/local-checks/isolated-memory.patch.yaml` 檢查。
- 下一步會建立 `.env` 檔案。
- 選用：如果本次要啟動 `kr-812`，相符的用戶端資料已在 `client-data/kr-812/data` 或 `client-data/kr-812/dataraw` 下。

## 準備本機環境變數檔案

profile 在組合時就會插入環境變數，此時任何修補檔都還沒有停用服務。因此即使是記憶體檢查，也必須存在本機的 MySQL 密碼變數。

### 1. 複製範例檔案

PowerShell：

```powershell
Copy-Item .env.example .env
```

Linux shell：

```sh
cp .env.example .env
```

`.env` 被 Git 忽略，絕不要提交。

### 2. 設定臨時密碼

開啟 `.env`，把 `BFF_MYSQL_PASSWORD` 設為非空的臨時值，例如 `isolated-check-only`。`persistence.mysql` 列讀取沒有預設值的 `${BFF_MYSQL_PASSWORD}`，變數缺少時組合會失敗；隔離記憶體修補檔只在組合之後才停用該列。本機檢查不要使用真實資料庫憑證，真實憑證只用於刻意設定的部署。

**檢查是否成功：** `.env` 存在（`Test-Path .env` 輸出 `True`），且 `BFF_MYSQL_PASSWORD` 列非空。不要把該檔案或其值貼到聊天或問題中。

## 找到 profile

profile 指定一個用戶端版本，並組合該版本使用的模組列。它的 id 是 `<region>-<wire version>`，其中數字是用戶端的 `ClientVersion_Major`，不是執行檔的檔案版本。`management` 是僅管理用途的 profile；`setup` 是首次執行助理。

### 1. 列出隨附的 profile

```powershell
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- profiles list
```

命令為每個 profile 輸出一行：id 與描述。本版本列出 19 個。其中兩行比較有用：`management`（“Administration and durable storage only; no game or relay ports.”）與 `kr-812`（“Korean 2015 client build, ClientVersion_Major 812, resource patch 458.”）。

列出某個 profile 不代表該版本的全部玩法都已實作；不同 profile 可用的功能與所需資料不同。

### 2. 列出找到的模組

```powershell
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- plugins list
```

命令為每個模組定義輸出一行：模組 id、版本、支援的用戶端版本以及載入來源。這就是 Host 可以掛載到 profile 中的實作集合。

**檢查是否成功：** 兩條命令都成功結束並輸出內容，沒有 profile 或模組掃描錯誤。

## 檢查生效的設定

用本機檢查修補檔組合 `kr-812`，在不啟動任何東西的情況下列印結果：

```powershell
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- run --profile kr-812 --patch tools/local-checks/isolated-memory.patch.yaml --dump-config
```

命令以 YAML 輸出生效的列清單，並以結束碼 0 結束。請在輸出中尋找以下列：

- `id: persistence.mysql` 後面跟著 `disabled: true`；
- `id: persistence.memory` 沒有 `disabled: true`，即已啟用；
- `id: clientdata` 顯示 `kr-812` 解析後的 `data_directory` 與 `raw_directory`。

隔離修補檔最後套用，因此會覆寫開發者 `.env` 或 `local.patch.yaml` 中把 MySQL 指向真實資料庫的設定。匯出會在任何模組啟用之前結束，因此只能證明組合與驗證，不能證明 profile 能啟動。

::: warning
當修補檔啟用 MySQL 時，匯出可能包含插值後的值，包括憑證。請把它視為敏感內容。
:::

## 啟動伺服器

### 1. 啟動無遊戲的本機伺服器

在儲存庫根目錄執行：

```powershell
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- run --profile management --patch tools/local-checks/isolated-memory.patch.yaml
```

如果儲存庫根目錄存在 `.env`，第一行是 `Loaded unset variables from <path>.`。隨後伺服器會列印管理監聽位址、WebUI 位址與作用中的列數：

```text
Row 'admin.http' admin API listening on 127.0.0.1:8080.
WebUI: http://127.0.0.1:8080/
Server started with <count> active row(s).
```

`<count>` 是 Host 列印的作用中列數量，不是固定數字。`management` 沒有遊戲或中繼連接埠。它的管理 API 基礎位址是 `http://127.0.0.1:8080`，來自 `profiles/bundles/core.yaml` 中的 `admin.http` 列（`${BFF_ADMIN_HOST:-127.0.0.1}:${BFF_ADMIN_PORT:-8080}`）。處理程序會一直保持在前景，直到你停止它。

### 2. 改為啟動遊戲 profile（需要用戶端資料）

```powershell
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- run --profile kr-812 --patch tools/local-checks/isolated-memory.patch.yaml
```

遊戲 profile 需要相符的用戶端資料。缺少資料時，`clientdata` 列會失敗，啟動會以下列訊息停止：

```text
clientdata: neither data_directory '<working-directory>/client-data/kr-812/data' nor raw_directory '<working-directory>/client-data/kr-812/dataraw' exists. Provision the configured client-data directories before enabling this row.
```

請提供資料，或依[安裝](installation.md)所述透過安裝助理匯入，然後重新啟動該 profile。資料就位後，`kr-812` 會在 `${BFF_BIND_HOST:-0.0.0.0}:${LEGACY_PORT}` 上綁定遊戲監聽，因為 `profiles/kr-812/profile.yaml` 把 `LEGACY_PORT` 設為 `${KR_812_GAME_PORT:-7110}`，所以實際是 `0.0.0.0:7110`。舊版（legacy）版本沒有中繼監聽。管理 API 仍是 `127.0.0.1:8080`。

**檢查是否成功：** 處理程序保持在前景，列印上面三行啟動資訊，且沒有以啟用錯誤結束。

## 確認它正在執行

### 1. 檢查健康檢查端點

PowerShell：

```powershell
Invoke-RestMethod http://127.0.0.1:8080/health
```

Linux shell：

```sh
curl -s http://127.0.0.1:8080/health
```

回應包含 `status` = `ok` 以及 UTC 時間戳。`/health` 始終不需登入。

### 2. 檢查執行階段實例

PowerShell：

```powershell
Invoke-RestMethod http://127.0.0.1:8080/api/plugins | Format-Table rowId, state
```

Linux shell：

```sh
curl -s http://127.0.0.1:8080/api/plugins
```

`/api/plugins` 傳回正在執行的模組列。每一項包含 `rowId`、`plugin`、`source`、`state` 與 `error`。請尋找 `state` = `Active` 且 `error` 為空的列；`Failed` 或 `Pending` 列會在 `error` 中提供診斷資訊。

以 `run --profile` 啟動的伺服器存取 `/api` 不需登入，並且只在回送位址上監聽。不帶參數啟動的伺服器（首次安裝）會要求你建立的管理員帳號；其工作階段是瀏覽器 Cookie，因此請透過 WebUI 進行這些操作。

### 3. 開啟 WebUI

在瀏覽器開啟 `http://127.0.0.1:8080/`。management profile 會提供管理介面。

**檢查是否成功：** 健康檢查傳回 `ok`，實例清單顯示作用中的列，且 WebUI 可以載入。這些檢查證明 Host 及其管理 API 正在執行；它們不能證明遊戲用戶端可以連線。

## 停止伺服器

在執行伺服器的終端機按 Ctrl+C。主控台會列印 `Shutdown complete.`，隨後處理程序結束。記憶體中的帳號與進度隨處理程序一起捨棄；對本機檢查來說停止總是安全的，因為沒有任何持續性資料被更動。

如果希望資料在重新啟動後保留，請依[伺服器設定](configuration.md)把持續性切換到 SQLite 或 MySQL。隔離記憶體修補檔只用於本機檢查。

## 這不能證明什麼

- 沒有遊戲用戶端登入，也沒有驗證任何玩法或封包相容性。
- management profile 沒有遊戲或中繼監聽，因此完全沒有用戶端流量。
- 設定檢查或自動檢查通過不等於真實用戶端驗收；請另行用真實用戶端驗證目標登入與遊戲流程。

## 發生問題時

- **`environment variable 'BFF_MYSQL_PASSWORD' is not set and has no default`：** `.env` 檔案缺少、沒有在儲存庫根目錄找到，或沒有設定該變數。請複製 `.env.example` 並設定一個臨時值。
- **管理連接埠已被占用：** 啟動會在監聽列失敗。請在 `.env` 中設定 `BFF_ADMIN_PORT`，或停止占用 8080 的處理程序。
- **遊戲連接埠已被占用：** 請在 `.env` 中設定 `KR_812_GAME_PORT`，或停止占用 7110 的處理程序。
- **`kr-812` 因 `clientdata` 失敗：** 參見上面的用戶端資料訊息；該版本的資料必須存在，profile 才能啟動。
- **`Server startup did not converge`：** 訊息會列出每個未啟用的列及其狀態與診斷。先閱讀第一個 `Failed` 或 `Pending` 列並修正其原因。

更多情況見[疑難排解](troubleshooting.md)。

## 下一步

繼續閱讀[伺服器設定](configuration.md)，了解持續性、連接埠、profile 與環境變數；然後閱讀[模組](plugins.md)，了解模組列的運作方式以及如何掛載自己的模組。配套啟動器如何安裝用戶端並連線伺服器，見[啟動器](launcher.md)。
