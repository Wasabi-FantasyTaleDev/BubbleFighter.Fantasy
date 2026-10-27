---
title: "自建商店來源"
description: "在明確工具限制的前提下準備與發布啟動器用戶端、Profile 及模組。"
updated: 2026-10-22
---

# 自建商店來源 {#self-hosted-stores}

## 範圍與準備 {#prerequisites}

下文範例、欄位名稱與限制對應這份 [Launcher 原始碼快照](https://github.com/Wasabi-FantasyTaleDev/FantasyTale.Launcher/tree/5f1d4267d0ddd68390dc27fe12f678b06b846e6e)。工具、用戶端定義與驗證器應使用同一版本。若採用較新的 Launcher 修訂，請遵循該修訂隨附的格式文件，並使用同一修訂的驗證器檢查後再發布。

商店提供一份 UTF-8 TOML 總目錄，預設路徑為 `FantasyTale/source.toml`。schema 2 同時包含用戶端資源、模組資源與用戶端 Profile，檔案清單和內容分開儲存、依需要讀取。帳號驗證和登入票證由[伺服器啟動器 API](launcher.md)負責。新增商店不會授權它更新 Launcher 本體：[Launcher 更新來源](https://github.com/Wasabi-FantasyTaleDev/FantasyTale.Launcher/blob/main/docs/launcher-updates.md)另行設定。

下文所有 `tools/publish-store/`、`tools/publish-launcher/`、`games/` 與 `plugin.toml` 路徑皆屬於獨立的 [Fantasy Tale Launcher 儲存庫](https://github.com/Wasabi-FantasyTaleDev/FantasyTale.Launcher)，並非本伺服器儲存庫。取得其原始碼後，以根目錄 `D:/Launcher` 作為範例位置。需要 Python 3.11 或更新版本；上傳工具另需 `requests`，目錄驗證需要 .NET 10 SDK。這些管理者工具不包含在伺服器發行套件內。

以下 PowerShell 準備步驟假設已安裝 Python，且 `D:/Store` 為獨立工作目錄。後續 Python 指令使用此環境；每段可執行指令均明確切換到 Launcher 儲存庫根目錄。

```powershell
Set-Location 'D:/Launcher'
python -m venv D:/Store/.venv
& D:/Store/.venv/Scripts/python.exe -m pip install requests
```

## 託管方式與要求 {#hosting}

| 類型 | 來源根網址 | 總目錄要求 |
| --- | --- | --- |
| `http` | `https://files.example.org/` | `GET /FantasyTale/source.toml` |
| `alist` | `https://archive.example.org/` | `POST /api/fs/get`，再 GET 解析出的網址 |

總目錄路徑可設定。目錄與清單內的路徑相對於來源根目錄，而非引用檔案。HTTP 靜態網站必須回傳 TOML 原始位元組，不能是 HTML 預覽或 JSON 包裝。不需要 HEAD，正確的長度及 HTTP Range 支援可讓下載續傳。

AList 接收第一段 JSON，並回傳第二段所示結構。`size` 必須換成實際位元組數。

```json
{"path":"/FantasyTale/source.toml","password":""}
```

```json
{"code":200,"message":"success","data":{"is_dir":false,"size":1234,"raw_url":"https://storage.example.org/source.toml","sign":""}}
```

缺少 `raw_url` 時，轉接器使用 `/d/<path>?sign=<sign>`。解析中繼資料上限為 64 KiB，下載回應必須回傳原始內容。暫時網址依需要解析，不作為資源識別資訊儲存。桌面來源編輯器用於公開來源，瀏覽不應要求管理員憑證。

## 總目錄欄位 {#catalog-fields}

```toml
schema = 2

[catalog]
id = "community"
name = { en = "Community store", zh-CN = "社区商店" }
```

追加 `[[packages]]` 與 `[[profiles]]`，可省略空陣列。未知欄位會被拒絕。Launcher 讀取器支援最大 4 MiB 的 schema 2、256 個資源發行項目及 512 個 Profile；schema 1 上限為 256 KiB，不含 Profile 或內嵌模組定義。單一 Profile 與內嵌模組定義各以 256 KiB 為限。

| 資源欄位 | 意義 |
| --- | --- |
| `id`、`kind`、`game` | 資源識別碼、類型與遊戲；本文使用 `client` 或 `plugin` |
| `builds` | 用戶端恰好一個版本；商店模組列出明確支援的版本 ID |
| `version` | 用戶端選用更新標籤；模組使用 SemVer |
| `name`、`description` | 本地化文字，名稱須包含英文；說明為選用 |
| `publisher` | 必填顯示名稱，不代表身分驗證 |
| `size` | 清單內需要下載的總位元組數 |
| `manifest`、`manifest_sha256` | 清單相對路徑及原始位元組的 SHA-256 |
| `update_mode` | 用戶端目前快照使用 `files`；版本化發行使用 `release` |
| `revision` | 選用排序值；正數修訂編號在同一資源內必須唯一 |
| `authors`、`uploader` | 選用作者清單與上傳者顯示名稱 |
| `plugin_definition` | 僅 schema 2：內嵌完整模組定義 |
| `minimum_launcher_version` | 僅 schema 2：選用最低應用程式 SemVer |

資源身分綁定其設定來源。其他來源不能僅以相同 ID 取代已安裝資源。相對路徑禁止 URL、目錄穿越、反斜線、百分比編碼、查詢參數與片段。大小及雜湊須依實際內容產生。

## 檔案清單 {#file-manifests}

以下僅示意檔案同步用戶端的清單，不能作為實際發行中繼資料：範例大小及兩處 `<file-sha256>` 必須換成產生的值。總目錄的 `manifest_sha256` 對整份 UTF-8 清單計算雜湊，每項 `[[files]]` 則對自身內容計算雜湊。

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

`path` 為相對於安裝目錄的目標路徑，`source_path` 則相對於來源根目錄。模組清單使用 `[release] version` 與 `builds`，取代用戶端的單數 `build`。清單上限為 256 KiB。請以工具產生完整輸出，封存檔清單的額外欄位見 [Launcher 商店格式](https://github.com/Wasabi-FantasyTaleDev/FantasyTale.Launcher/blob/main/docs/store-sources.md)。

## 本機產生用戶端 {#clients}

滿足上述相依需求後，將範例存成 `D:/Store/publish.toml`。輸入為乾淨且可散布的目錄，輸出必須位於所有輸入目錄之外。發布設定中的相對路徑以設定檔所在目錄為基準。

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

工具產生 `D:/Store/generated/FantasyTale/source.toml`、不可變的雜湊清單與以內容定址的檔案。它輸出 **schema 1**，以設定中的用戶端／伺服器項目取代自身總目錄，不會合併既有模組或 Profile。請勿以使用中的統一目錄作為輸出。`--watch 30` 會重複取代；`--full-check` 重新計算輸入雜湊，不信任中繼資料快取。產生目錄應與發布樹分開。僅新增目錄項目無法實作新遊戲，其模組與有效版本定義必須已支援它。

## 組合 Profile 並驗證 {#profiles}

從 Launcher 儲存庫內已支援的 `games/<game>/builds/<build>.toml` 開始。`[build]` 包含識別碼、執行檔、顯示名稱、分支、`year`、`client_version` 與選用 `release_version`，選用 `region` 為兩個大寫字母。在上方連結的快照中，`client_version` 是整數，選用 `wire_version` 是通訊協定識別字串。請保留該 Launcher 修訂隨附的欄位名稱與型別：此剖析器不接受 `build_date`、`protocol_version` 或 `patch_version`。`[launch]` 使用 `login`、`arguments`、`launcher_login`（`required`、`optional` 或 `none`）與 `requires_elevation` 定義模組專用的啟動與登入原則。`[plugins]` 的 ID 陣列中，`required` 強制啟用，`recommended` 提供建議，`default` 提供預設選擇。請勿猜測指紋或登入行為。

將設定存成 `D:/Store/profiles.toml`，引用的定義位於 `D:/Store/profiles/cn-279.toml`。先備妥包含所有要保留資源的 `D:/Store/package-catalog.toml`。指令從 Launcher 根目錄執行，需要 Python 3.11+ 與 .NET 10 SDK。

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

**組合設定**的 `definition` 是檔案路徑，**輸出總目錄**則內嵌完整文字，並包含 `game`、`build` 及正數 `revision`。定義變動時須增加修訂編號。組合器接受 schema 1／2，保留輸入資源項目與既有 Profile，在不存在的新輸出路徑產生 schema 2。它合併設定中的 Profile，不會合併多份資源目錄。組合前須整合新用戶端快照和所有要保留的模組／資源項目；只使用剛產生的用戶端目錄會遺失其他項目。選用 `--legacy-output <new-path>` 會另行輸出不含 Profile 與內嵌模組中繼資料的 schema 1 投影。

## 模組作者與發布 {#plugins}

Launcher 模組與 .NET 伺服器模組不同。散布目錄根部包含 `plugin.toml` 與執行檔案。`[plugin]` 宣告 ID、遊戲、SemVer、本地化名稱與選用 `authors`；`[compatibility]` 宣告版本支援。請參閱 [Launcher hook 模組格式](https://github.com/Wasabi-FantasyTaleDev/FantasyTale.Launcher/blob/main/docs/hook/plugin-format.md)及[檔案替換模組](https://github.com/Wasabi-FantasyTaleDev/FantasyTale.Launcher/blob/main/docs/file-plugins.md)。同一模組 ID 可支援多個版本；即使模組定義允許選擇器，商店項目的 `builds` 仍須列出明確版本 ID。

`authors` 表示作者，`uploader` 為選用上傳者名稱，空值時省略。兩者均不驗證身分。內容變動須使用新 SemVer，請勿為補上中繼資料而覆寫已發布版本。

下方指令會**實際遠端發布**，沒有 CLI 預演模式。請使用獨立準備、已有 schema 1 總目錄的 AList 暫存來源、可寫入驅動程式，並滿足上述相依需求。僅透過處理程序環境提供 `FT_ALIST_USERNAME` 與 `FT_ALIST_PASSWORD`。審核目標後再取代 `staging.example.org`。

```powershell
Set-Location 'D:/Launcher'
& D:/Store/.venv/Scripts/python.exe tools/publish-store/publish_plugins.py --package D:/Store/plugin-package --output D:/Store/plugin-output --base-url https://staging.example.org/
```

發布器先上傳內容、清單，再上傳總目錄。它將產生的資源文字追加到遠端目錄，既有 `(id, version)` 的中繼資料改變時會拒絕發布。即使輸入宣告 schema 2，輸出仍受 **256 KiB、256 個發行項目**限制。每個模組套件上限為 256 個檔案、128 MiB。它保留既有文字，但不會完整驗證 schema 2，也不會產生總目錄的 `plugin_definition`／`minimum_launcher_version` 欄位。它不是通用 schema 2 編輯器或純本機準備指令。

統一組合前，保留完整暫存目錄及所有引用物件。內嵌中繼資料須在 schema 2 候選中明確加入並驗證：內嵌 ID／遊戲／版本須與資源一致，另填的最低版本須與內嵌 `[plugin] minimum_launcher_version` 一致。組合器保留既有資源欄位，不會自動產生。下載中繼資料不會執行模組程式碼。

## 工具相容性 {#tool-compatibility}

| 工具或讀取器 | 實際行為 |
| --- | --- |
| Launcher 總目錄讀取器 | schema 1：256 KiB；schema 2：4 MiB；最多 256 個資源／512 個 Profile |
| `publish_store.py` | 本機 schema 1 產生器；取代自身總目錄，不合併資源／Profile |
| `publish_plugins.py` | AList 遠端發布器；文字追加，256 KiB／256 個發行項目；不產生內嵌中繼資料 |
| `compose_catalog.py` | schema 1／2 輸入，最大 4 MiB schema 2 輸出；保留資源、合併 Profile |
| `upload_alist.py` | 完整引用圖；schema 2 總目錄最大 4 MiB，清單最大 256 KiB，最多 4096 個物件 |

## 安全發布順序 {#publication}

1. 保留現有總目錄與物件，協調由單一寫入者操作。
2. 在獨立目錄產生用戶端。使用模組 CLI 時先發布至獨立暫存來源。組裝完整目標資源目錄，保留無關項目與既有 Profile；用戶端產生器無法合併它們。
3. 組合新統一目錄並執行上述驗證器。在獨立發布根目錄（例如 `D:/Store/release`）備妥其**全部**清單與內容，將驗證過的目錄放在根部的 `FantasyTale/source.toml`。AList 上傳器也要求先前引用的物件在本機存在。
4. HTTP 先複製內容，再複製不可變清單，最後以不可分割的操作取代總目錄。保留舊物件供已綁定舊清單的下載使用。
5. AList 僅在上述準備完成後執行下方發布指令，並非預演。Python 環境需要 `requests`，處理程序環境需要 `FT_ALIST_USERNAME`／`FT_ALIST_PASSWORD`。

```powershell
Set-Location 'D:/Launcher'
& D:/Store/.venv/Scripts/python.exe tools/publish-store/upload_alist.py --input D:/Store/release --base-url https://archive.example.org/ --workers 2 --verify-timeout-seconds 900
```

上傳器檢查本機雜湊、驗證匿名遠端內容，先內容後清單，檢查舊總目錄的公開摘要後才最後取代。此樂觀檢查不等於不可分割的多寫入者鎖定。`--workers` 為 1–4（預設 2），`--verify-timeout-seconds` 為每物件 30–3600 秒（預設 900）。`--catalog` 修改目錄路徑，`--no-proxy` 僅影響本次上傳處理程序。AList 驅動程式須可寫入，最終檔案須能匿名讀取。其他選項見 [Launcher 上傳文件](https://github.com/Wasabi-FantasyTaleDev/FantasyTale.Launcher/blob/main/docs/store-sources.md#uploading-a-generated-catalog-to-alist)。

## 驗收與界線 {#verification}

以匿名使用者取得並驗證總目錄，讀取引用清單，核對其原始位元組 SHA-256，再安裝至獨立測試目錄。檢查 Profile 身分、必要／建議／預設模組選擇及多版本相容性。雜湊證明內容符合所選目錄，不證明發布者身分。請勿散布帳號資料、憑證、票證、本機覆寫設定、診斷或私有研究資料。本文描述工具契約，不代表特定商店、桌面流程或真實用戶端啟動已通過驗收。

## 伺服器商店下載 {#server-store}

伺服器 WebUI 的**商店**透過一般外掛列 `distribution.sources`、`distribution.plans`、`distribution.packages` 和 `distribution.jobs` 提供 HTTP、AList、GitHub 目錄瀏覽、來源選擇與驗證下載。這與上文桌面啟動器的流程不同：檔案只會進入隔離暫存區，不會安裝、啟用、更新或回復正在執行的伺服器及外掛。

1. 開啟**商店 → 管理來源**，填入唯一來源識別、顯示名稱、類型、根網址及目錄檔案路徑。網址不能包含認證資訊。儲存後的來源順序決定同一套件的預設來源；失敗的來源會顯示錯誤。
2. 瀏覽目錄並選擇下載來源。GitHub Release 還需要選擇具有發布者 SHA-256 的附件。透過儲存庫網址查詢 Release 只是預覽，不會直接建立可安裝套件；用戶端套件在此也僅供預覽。
3. 點選**下載至暫存區**。選擇會繫結目前來源修訂與確切的發布資訊，下載會核對清單及檔案雜湊並記錄進度。關閉瀏覽器不會取消已受理工作；請使用工作的取消操作。
4. **已暫存**表示未解開的檔案已通過驗證，不表示外掛已安裝。可以重新驗證檔案、查看歷史工作或明確刪除暫存內容。失敗或取消會清理不完整檔案並保留結果；重新啟動後未完成的工作顯示為中斷。

來源預設儲存至 `data/distribution-sources.json`，下載存放於 `data/distribution-staging`。可設定的列選項包括 `maximum_document_bytes`（4194304）、`maximum_sources`（512）、中繼資料逾時 `request_timeout_ms`（30000）及下載逾時 `transfer_timeout_ms`（1800000）。`maximum_payload_bytes` 為選用；下載仍受宣告的確切大小及可用磁碟空間限制。`free_space_reserve_bytes` 預設 0，歷史分頁 `history_page_size` 預設 20。這些是部署選項，並非套件格式要求。已完成的下載不會自動刪除。

伺服器應用程式清單使用既有 schema 1 格式。伺服器外掛清單需要明確啟用實驗讀取器 `distribution.packages.experimental_release_format: true`（預設 false）；該提案尚非啟動器的正式共用規格。GitHub 附件的發布者雜湊不證明平台相容性。發布附件可透過 [GitHub 鏡像](store.zh-tw.md#github-mirror)下載，並仍以該雜湊核對。安裝後的商店 API 使用管理員工作階段與既有要求保護，尚無逐項操作的商店權限節點；命令模式管理仍僅監聽回環位址。[用戶端外掛啟動原則](plugins.md#client-plugin-policy)有獨立的權限要求。
