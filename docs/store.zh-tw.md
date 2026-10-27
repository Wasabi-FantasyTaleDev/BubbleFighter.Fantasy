---
title: "商店管理"
description: "透過管理介面瀏覽商店總目錄、管理來源並把伺服器套件下載到暫存區。"
updated: 2026-10-25
---

# 商店管理 {#store-management}

商店是用於分發伺服器套件的管理介面。它瀏覽已設定的目錄來源、預覽 GitHub 發行中繼資料，並把伺服器套件下載到經過驗證的暫存區。本頁的一切都受系統管理員工作階段保護：透過 WebUI（或 `POST /api/auth/login`）登入後，每個商店請求都需要該工作階段 Cookie。商店**只做暫存，不做其他任何事**：任何商店動作都不會解壓縮封存檔、載入模組或替換應用程式路徑。一次完成的下載是經過驗證的檔案清單，而不是已安裝的模組。

商店隨核心捆綁包發布（`distribution.sources`、`distribution.plans`、`distribution.packages` 與 `distribution.jobs` 四列），`kr-812`、`management` 與 `all` profile 均包含它們。暫存預設落在 `data/distribution-staging`，由獨佔的持有者鎖保護；歷史頁預設 20 筆；可用空間保留預設為零。來源儲存在 `data/distribution-sources.json`——把 `state_path` 留空會使來源清單變為唯讀。

## 來源 {#sources}

來源卡片按營運者順序列出已設定的目錄來源，順序很重要：預設選中第一個能提供目錄的來源。每列有 ID、顯示名稱、種類（`http`、`alist` 或 `github`）、根 URL 與目錄路徑。URL 不攜帶憑證、查詢或片段；根路徑必須以 `/` 結尾。可以上下移動、新增與刪除列，然後一次儲存整個清單。

儲存使用樂觀修訂：編輯器在開啟時記錄設定修訂，儲存時一併送回。如果期間另一位系統管理員儲存過，儲存會以 `source_revision_conflict` 被拒絕，對話方塊保持開啟，供你核對後重新套用。

## GitHub 鏡像 {#github-mirror}

在 GitHub 存取緩慢或受限的地區，商店可以透過公開的 URL 前綴鏡像下載 GitHub 發布附件與壓縮檔。開啟**管理來源**並啟用 **GitHub 鏡像加速**；選擇**自動**時，每次下載前都會測量所有鏡像，並優先嘗試最快的可用鏡像，也可以在**測速**後指定某個鏡像。GitHub 本身一律是最後一條線路。只有檔案內容會經過鏡像：發布資訊與目錄一律直接從 GitHub 讀取，經鏡像收到的每個位元組都會與 GitHub 公布的大小及 SHA-256 核對，因此鏡像只能讓下載失敗，無法改變安裝的內容。

伺服器會檢測一次自身位址。若位於中國大陸，第一位開啟 WebUI 的管理員會被詢問是否啟用鏡像；該對話方塊只顯示一次，確認之前不會做任何變更。鏡像清單、測速檔案與測量上限位於 `data/github-mirrors.json`（`distribution.sources` 列的 `github_mirrors_path`，留空即關閉此功能），開關、所選鏡像與位置檢測結果儲存在 `github_mirror_state_path`。

## 瀏覽總目錄 {#browsing}

總目錄頁把所有來源的套件合併為一張按套件識別去重的卡片清單。篩選器可以選擇全部來源或單一來源；重新整理會重新讀取每個總目錄（或單一來源的）。某個來源失敗時絕不會靜默消失——它的失敗原因（`source_timeout`、`source_unavailable` 或 `catalog_invalid`）會列在卡片上方，健康的來源照常顯示。

同一總目錄中出現同一套件的多個發行時，以發行者最高的 `revision` 為準；並列——或多發行群的修訂全部未設定——是設定錯誤，而不是猜測。面向用戶端的套件可見，但只有伺服器目標可以下載：選取它會顯示「用戶端模組僅提供預覽」。

## GitHub 儲存庫 {#github-repositories}

GitHub 來源從儲存庫讀取目錄文件。儲存庫發行表單預覽一個儲存庫的最新穩定發行——或明確指定的標籤——及其附件：草稿與預發行會被拒絕，每個附件 URL 必須屬於所請求的儲存庫與標籤，只有當 GitHub 為附件發布了 `sha256:` 摘要時，附件才攜帶發行者 SHA-256。預覽只提供中繼資料；它不會下載任何內容。

`github_repository` 與 `github_tag` 欄位屬於總目錄 schema 3。它們在伺服器與啟動器的嚴格總目錄讀取器上都可用，但這是暫行的協調格式，不是已發布的共享合約——在與啟動器發行版本就格式達成共識之前，不要把正式總目錄改發為 schema 3。儲存庫附件只有攜帶該發行者摘要才能下載；否則下載按鈕保持停用，因為下載執行的逐位元組驗證沒有可比對的對象。

## 下載到暫存區 {#staging}

下載伺服器套件是三步流程，每一步都釘住所顯示的內容：

1. **選取**卡片上的一個下載供給。每個供給都攜帶選取指紋——對已設定來源與該供給精確內容的 SHA-256——以及它讀取自的總目錄位元組的 SHA-256。
2. **規劃**下載。計畫會針對這些釘住重新驗證來源、總目錄、發行與附件；來源或供給發生變化時，以 `distribution_source_changed` 或 `distribution_selection_changed` 拒絕，而不是下載別的東西。
3. **受理**工作。工作在串流傳輸時驗證每個檔案的大小與 SHA-256，把資料區塊以不透明形式保存在暫存根下，並報告階段（`preparing`、`transferring`、`staged`）。下載面板輪詢進度、按需對已暫存工作做完整回讀驗證、取消進行中的工作，並刪除終態工作及其暫存內容。

通過驗證只說明當時暫存卷裡有什麼。它不授予任何安裝授權；磁碟上的後續變化要靠再次驗證發現，而不是被預設可信。

## 用戶端策略 {#client-plugin-policy}

決定啟動器必須、可以或不得使用哪些模組的用戶端策略，在已安裝伺服器的 WebUI 中編輯。儲存需要系統管理員工作階段之外的 `webui.settings.client_policy` 權限，儲存後會重新執行設定套用。這些規則設定的是相容的啟動器——用戶端二進位檔不會安裝在伺服器上，這些規則也不能證明用戶端完整性。管理 API 見[用戶端策略](plugins.zh-tw.md#client-plugin-policy)，規則類型見[啟動器文件](launcher.zh-tw.md#client-policy)。

## 管理 API {#administration-api}

WebUI 呼叫的端點與營運者指令碼可用的相同。以下所有路徑都需要系統管理員工作階段 Cookie；非 GET 請求還需要 `X-BFF-Request: 1` 標頭與同來源的 `Origin`。

| 方式與路徑 | 用途 |
| --- | --- |
| `GET/PUT /api/distribution/v1/sources` | 讀取或攜帶修訂替換來源清單。 |
| `POST /api/distribution/v1/repository-release` | 預覽一個 GitHub 發行（僅中繼資料）。 |
| `GET /api/distribution/v1/catalog?sourceId=` | 合併後的總目錄快照，或單一來源的。 |
| `POST /api/distribution/v1/plans` · `/validate` · `DELETE` | 釘住、複查與釋放一個選取。 |
| `POST /api/distribution/v1/plans/{id}/files` | 驗證資訊清單並列出裝載檔案。 |
| `GET/POST /api/distribution/v1/jobs` · `GET /{id}` | 歷史分頁與受理（202，帶 `Location` 標頭）。 |
| `POST /api/distribution/v1/jobs/{id}/cancel` · `/verify` · `DELETE` | 取消、驗證暫存回讀，或移除終態工作。 |

穩定的錯誤碼（`source_revision_conflict`、`distribution_selection_changed`、`distribution_job_not_staged`、`repository_rate_limited` 等）說明失敗原因；HTTP 狀態區分錯誤請求（400）、過期的釘住（409）、上游問題（502/504）與儲存或設定問題（503）。

## 商店不做什麼 {#limits}

- 不安裝、不啟用：沒有任何路由會解壓、載入或重新啟動任何東西。下載不是安裝，持久的意圖也不是執行中的功能。
- GitHub 鏡像按設計直接失敗，而不是假裝可用。
- 跨來源的資訊清單參照會被拒絕（「尚不支援」）。
- 啟動器目前不消費任何這些內容：它不讀取商店總目錄；在其下載引擎發布之前，用戶端預設只是對外展示的資料。
