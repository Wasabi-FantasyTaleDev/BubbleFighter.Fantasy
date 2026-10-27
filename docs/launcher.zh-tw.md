---
title: "啟動器"
description: "Bubble Fighter Fantasy 的配套啟動器：用戶端版本、工作階段、登入票據與伺服器啟動器 API。"
updated: 2026-10-23
---

Bubble Fighter Fantasy 的配套桌面應用程式 **Fantasy Tale Launcher** 正在開發中，目標是安裝並啟動遊戲用戶端，並將它們連線到玩家選擇的伺服器。它是獨立專案：[FantasyTale.Launcher](https://github.com/Wasabi-FantasyTaleDev/FantasyTale.Launcher)，以 MIT 授權條款發布。本伺服器儲存庫不包含它，兩個專案都不散布遊戲用戶端或其資料。

本頁記錄啟動器在目前開發檢查點的狀態，以及營運者已經可以啟用的伺服器端啟動器 API。啟動器處於**早期開發階段**：大多數面向玩家的流程仍以定義與契約的形式存在，而不是可用的介面。下文每一節都說明哪些已實作、哪些已規劃，以免玩家或營運者依賴在此提交中並不存在的行為。

## 目前開發狀態

| 領域 | 目前檢查點的狀態 |
| --- | --- |
| 視窗、遊戲目錄與設定殼層 | 已實作：Host 從 `games` 資料夾載入遊戲與用戶端版本定義並傳送給介面；殼層呈現遊戲、執行個體、伺服器、下載、模組、帳號與設定頁面，並在本機儲存外觀與語言偏好設定。 |
| Bubble Fighter 版本定義 | 已實作為資料：共定義九個用戶端版本，每個版本各自一個版本檔案。 |
| 工作階段檔案（`session.toml`） | 寫入器已作為函式庫輔助類別實作；尚無正式啟動流程呼叫它。 |
| 原生 hook 執行階段與 messenger 宿主 | 已實作並由原生測試涵蓋，但**尚未在真實遊戲用戶端中驗證**。 |
| 三個內建 hook 模組 | 已定義為資料模組，並由同一批原生測試涵蓋；同樣尚未在真實遊戲用戶端中驗證。 |
| 後端下載、下載來源、模組管理員、執行個體 | 未實作：下載引擎、清單套用、模組管理員與執行個體模型仍是待辦的路線圖工作。一種下載來源類型的解析器已經存在。 |
| 面向玩家的清單與操作 | 執行個體、伺服器、下載、模組與帳號頁面目前呈現預留位置資料；在 Host 橋接方法就緒之前，介面明確是預覽版本。目前沒有可用的「建立執行個體」「新增伺服器」「登入」或「啟動」操作。 |
| 伺服器啟動器 API（位於本儲存庫） | 已實作且營運者可啟用：帳號、工作階段、票據、目錄、用戶端預設、版本閘門與模組建議。 |

不要期望目前取出的啟動器已經能夠安裝或啟動遊戲用戶端。下文各節依據原始碼撰寫，以便在其餘流程就緒後本指南仍然正確。

## 取得啟動器

啟動器是獨立專案，擁有自己的儲存庫和發行流程：

- 儲存庫：[FantasyTale.Launcher](https://github.com/Wasabi-FantasyTaleDev/FantasyTale.Launcher)。
- 授權條款：MIT。
- 狀態：早期開發階段，如其 README 所述。

該儲存庫說明如何從原始碼建置（建置 .NET 方案、用 Visual Studio 建置 x86 原生 hook 宿主，以及用 npm 建置前端）。它沒有說明已發布的安裝程式、發行檔案或下載頁面，因此本指南也不提供這些內容。請等待啟動器正式發行，而不要把開發者原始碼建置當作玩家安裝。

## 用戶端版本

用戶端版本的識別為 `<region>-<wire version>`，其中 wire version 是用戶端的 `ClientVersion_Major`。它**不是**發行年份，也不是執行檔的版本。目前文件記錄的伺服器 profile 與用戶端版本如下：

| 版本 | 分支 | 顯示名稱 | 用戶端日期 | 用戶端版本 | 狀態 |
| --- | --- | --- | --- | --- | --- |
| `kr-1256` | 韓國 | KR 2026 | 2026-05-08 | 1256 | 實驗性 |
| `kr-812` | 韓國 | KR 2015 | 2015-04-22 | 812 | 已支援 |
| `cn-279` | 中國 | CN 2011 | 2011-04-08 | 279 | 已支援 |
| `cn-324` | 中國 | CN 2012 | 2012-09-23 | 324 | 已支援 |
| `cn-406` | 中國 | CN 2013 | 2013-06-28 | 406 | 開發中的早期階段 |
| `id-400` | 印尼 | ID 2014 | 2014-01-23 | 400 | 已支援 |
| `cn-706` | 中國 | CN 2014 | 2014-09-23 | 706 | 已支援 |
| `id-1809` | 印尼 | ID 2015 | 2015-01-29 | 1809 | 已支援 |
| `cn-717` | 中國 | CN 2015 | 2015-06-18 | 717 | 已支援 |
| `cn-735` | 中國 | CN 2016-06-20 | 735 | `BubbleFighter.exe` |
| `tw-454` | 台灣 | TW 2019 | 2018-12-11 | 454 | 實驗性 |
| `cn-774` | 中國 | CN 2019 | 2019-05-30 | 774 | 實驗性 |
可執行檔方面，印尼版本（`id-400` 與 `id-1809`）使用 `CrazyShooter.exe`；其他列出的版本使用 `BubbleFighter.exe`。

啟動器目前只隨附可以實際啟動的版本定義；上表中的其他伺服器 profile 用於記錄相容性規劃，在補充並驗證對應的啟動器定義之前不會自動啟動。

### messenger 與登入模式

每個版本還宣告用戶端的 messenger 呼叫由誰回應，以及用戶端如何登入：

| 版本 | messenger 模式 | 登入模式 | 預設連接埠 |
| --- | --- | --- | --- |
| `kr-1256` | forward | hook | 7100 |
| `kr-812` | stand-in | nmco | 7110 |
| `cn-279` | forward | gamewidget | 7210 |
| `cn-324` | forward | gamewidget | 7140 |
| `cn-404` | forward | gamewidget-ticket | 7240 |
| `cn-406` | forward | gamewidget-ticket | 7230 |
| `tw-454` | stand-in | nmco | 7200 |
| `cn-706` | forward | unverified | — |
| `cn-717` | forward | unverified | — |
| `cn-735` | forward | unverified | — |
| `cn-774` | forward | gamewidget-ticket | 7190 |
| `id-400` | stand-in | nmco | 7120 |
| `id-1809` | stand-in | nmco | 7130 |

關於這些表格的說明：

- 分支欄是遊戲定義中的區域分組。下面的 messenger 表只列出已有啟動器定義的版本；用戶端表中的其他 profile 目前還不能透過啟動器啟動。
- 「messenger 模式」和「登入模式」是定義欄位。**替身（stand-in）**表示啟動器的 messenger 宿主自行回應用戶端的 messenger 呼叫；**轉送（forward）**表示把這些呼叫傳給原始函式庫，安裝內容會將該函式庫保留為重新命名後的檔案。參見[登入與驗證](#登入與驗證)。
- 標記為 `unverified` 的三個版本尚無已驗證的登入提供者或啟動參數；啟動器模組會拒絕為它們建置啟動計畫，並回報該版本沒有已驗證的啟動參數。它們在定義中列出，但在目前檢查點無法啟動。
- 沒有預設連接埠的版本需要一個能提供連接埠的已選伺服器端點。
- 版本的顯示名稱與年份是呈現資料。它們不參與身分識別：伺服器 profile 與 packet 表使用的是 id（`<region>-<wire version>`）。
- 啟動器目前向介面傳送每個版本的 id、分支、年份和本地化名稱。執行檔、messenger 模式、登入模式、指紋與預設連接埠是啟動與工作階段契約使用的定義資料，不是玩家編輯的資料。

## 規劃中的玩家流程

啟動器的設計描述了以下流程。由於介面尚未連線到真實資料，這裡列出每個步驟及其在目前檢查點的狀態。

| 步驟 | 儲存庫定義的內容 | 狀態 |
| --- | --- | --- |
| 1. 啟動啟動器 | 一個原生視窗，遊戲目錄從 `games` 資料夾載入。 | 已實作 |
| 2. 選取 Bubble Fighter | Bubble Fighter 是唯一已啟用的遊戲；其他遊戲項目會是無法選取的預留位置。 | 已實作 |
| 3. 建立用戶端執行個體 | 執行個體是一份已安裝的用戶端副本，擁有自己的資料夾、伺服器繫結、模組集與選項，因此同一版本的多個副本可以共存。 | 規劃中；執行個體頁面目前使用預留位置列 |
| 4. 選擇版本 | 版本是執行個體的屬性。每個版本定義提供執行檔、啟動參數、messenger 模式與預設模組。 | 規劃中；定義已存在，尚無版本選擇器 |
| 5. 新增伺服器 | 伺服器透過其啟動器 API 基礎 URL 新增；沒有該 API 的伺服器規劃支援依位址手動新增，但沒有已驗證的登入，也沒有自動安裝。 | 規劃中；伺服器頁面目前使用預留位置列 |
| 6. 登入伺服器 | 伺服器端帳號註冊、登入、更新與登出已經存在（參見[伺服器啟動器 API](#伺服器啟動器-api)）。 | 伺服器端已實作；啟動器用戶端側為規劃中 |
| 7. 安裝或更新用戶端 | 伺服器規劃為每個版本公布帶檔案大小與雜湊的散布清單；啟動器會驗證每個檔案，並依檔案套用更新。 | 規劃中；尚無下載引擎或更新檢查 |
| 8. 啟動 | 啟動器把 `session.toml` 寫入用戶端資料夾，以執行個體資料夾為工作目錄啟動執行檔，並在用戶端結束時刪除工作階段檔案。 | 契約與寫入器已實作；介面尚無啟動操作 |

在這些流程就緒之前，啟動器可用於開發與測試定義、工作階段契約、原生 hook 宿主以及伺服器端 API——而不是作為玩家安裝使用。

## 執行個體與啟動

儘管介面尚不能啟動用戶端，設計與已實作的程式碼仍然定義了一次啟動的形態：

- **執行個體**是位於自己資料夾中的一份用戶端副本。它攜帶一個 `instance.toml`，其中記錄版本、伺服器繫結、啟用的模組與選項；執行個體可以複製，因此同一版本可以服務於不同的伺服器或模組集。
- 版本定義指定的執行檔必須解析到執行個體資料夾內；逸出該資料夾的路徑會被拒絕。
- 啟動參數來自版本定義，是一個帶 `{account}`、`{host}` 和 `{port}` 預留位置的範本。沒有值的預留位置會使啟動計畫失敗，而不是用一個損壞的參數啟動用戶端。
- 啟動器的啟動計畫在選取了伺服器端點時使用該端點，否則使用版本的預設連接埠。沒有預設連接埠的版本需要一個能提供連接埠的伺服器端點。
- 每次啟動前，啟動器會立即把 `session.toml` 作為暫存檔案寫入用戶端資料夾，並以原子方式取代上一個檔案。hook 宿主在用戶端載入時讀取它一次；如果檔案缺少，則不套用任何模組。

工作階段檔案見[工作階段檔案與本機資料](#工作階段檔案與本機資料)。

## 登入與驗證

登入分為兩個獨立的層次，不應混淆：

1. **啟動器帳號**位於伺服器上，並由伺服器的啟動器 API 驗證。它提供存取權杖、更新憑證和帳號資料摘要。這是營運者的伺服器所控制的部分。
2. **遊戲登入身分**是遊戲用戶端在其登入請求中傳送的內容。在以替身 messenger 模式執行的版本上，啟動器的 messenger 宿主回應用戶端的通行證查詢並提供該身分。

### 用戶端如何取得身分

messenger 宿主依下列順序解析通行證（第一個非空值優先）：

1. 來自 `session.toml` 的啟動票據，
2. 在用戶端自己的登入對話方塊中輸入的帳號，
3. 來自 `session.toml` 的啟動器帳號，
4. 由舊版啟動器指令碼透過處理程序環境提供的帳號，
5. `guest`。

用戶端把該通行證作為登入 id 傳送。當用戶端使用自己的登入對話方塊時，替身會記錄輸入的 id 並將其傳回，因此遊戲使用玩家自己的輸入登入。

### 啟動器票據登入與遊戲內登入

本指南區分兩種情形：

- **啟動器票據登入。** 玩家登入啟動器（伺服器 API），啟動器向該 API 為所選版本申請一次性啟動票據，並把它寫入 `session.toml`。messenger 替身把票據作為通行證傳回，遊戲伺服器在登入時兌換它，而不是把它當作帳號名稱。票據按版本定義的 `login` 設定分發到兩條路徑：**替身**版本（`kr-812`、`id-400`、`id-1809`、`tw-454`，其 nmcogame 替身把票據作為登入通行證交給用戶端）與**登入橋**版本（`cn-404`、`cn-406`、`cn-774`，其登入模組把票據帶進 SDO 登入入口）。
- **遊戲內登入。** 當沒有提供啟動器帳號或票據時，用戶端可以使用自己的登入流程。在替身版本上，替身接受在用戶端登入介面中輸入的 id（即登入呼叫的用戶端對話方塊變體），並將其作為通行證傳回，因此伺服器看到的是一般帳號名稱。在轉送版本上完全不涉及啟動器的替身，因為由原始函式庫回應用戶端；版本定義中的 `gamewidget`（`cn-279`、`cn-324`）或 `hook`（`kr-1256`）登入模式描述的就是這種情形，但啟動器在目前檢查點並未實作這些提供者。

版本定義中的 `login` 值是資料：啟動器模組目前只對 `unverified` 強制生效（拒絕這些版本），不會針對提供者做其他分支處理。上述對應依據宣告的 messenger 模式與宿主程式碼；在目前檢查點尚未用真實用戶端觀察過。

### 票據在哪裡兌換

每個大廳列的 `authentication` 選項決定遊戲登入如何證明帳號身分。內建 profile 從 `BFF_LOGIN_AUTHENTICATION` 讀取它（`${BFF_LOGIN_AUTHENTICATION:-ticket}`），因此一個變數即可設定所有版本。

| `authentication` | 誰可以登入 | 需要 |
|---|---|---|
| `ticket`（預設） | 只有能兌換為該版本簽發之啟動票據的登入。其他任何帳號字串（包括空字串與在遊戲內登入框輸入的名稱）都會以 `launcher ticket required` 關閉連線；已使用、未知或版本不符的票據會以 `invalid launcher ticket` 關閉連線。 | 一個處於啟用狀態的 `launcher.http` 列。沒有它時大廳無法啟動，並回報 `authentication: ticket requires an active launcher.http row, which publishes ILauncherTicketService.` |
| `open` | 用戶端送出的任何帳號名稱；開啟 `auto_create_players` 時未知名稱會建立新玩家。每次啟動都會記錄警告。 | 無。只應在可信任的區域網路或本機測試中使用。 |

在 `open` 模式下，舊的 `launcher_tickets` 開關（`BFF_LAUNCHER_TICKETS`）仍可讓票據形式的登入與一般名稱並存兌換；在 `ticket` 模式下兌換一律開啟。

哪些版本能從啟動器拿到票據，取決於各版本定義的 `login` 設定。目前定義經兩條路徑提供票據：**替身**版本（`kr-812`、`id-400`、`id-1809`、`tw-454`）與**登入橋**版本（`cn-404`、`cn-406`、`cn-774`）。其餘版本目前拿不到票據——`kr-1256` 直接鉤住用戶端，`cn-279` 與 `cn-324` 使用普通元件，`cn-706`、`cn-717` 與 `cn-735` 的登入未經驗證——因此在 `ticket` 下這些版本的玩家無法登入；在它們的定義提供票據之前，請在可信任的網路中以 `open` 執行這些版本。

### 票據能證明什麼、不能證明什麼

有效的啟動票據能證明：它由本伺服器簽發給經過密碼或重新整理憑證驗證的啟動器帳號、針對的正是該版本、尚未被使用，並且仍在其短暫有效期內。它是一次性的、綁定版本、只保存在伺服器處理程序中，並且最遲與授權它的存取權杖同時過期。

它**不能**證明用戶端二進位檔未被修改，不能證明遊戲登入時啟動器版本是可接受的，不能證明已安裝建議的模組，也不能證明連線使用了 TLS。每個執行個體的 `launcher_login` 原則（`required`、`optional`、`none`）會在伺服器目錄中公布，但遊戲伺服器並不強制它；實際強制手段是大廳的 `authentication` 選項。在 `ticket` 下，沒有啟動器帳號的直接遊戲登入會被拒絕；在 `open` 下，遊戲登入完全不受保護。

## 工作階段檔案與本機資料

啟動器與伺服器儲存不同的資料，並且在目前檢查點只實作了一個啟動器檔案。

### `session.toml`（每次啟動）

- 位置：用戶端資料夾中，遊戲 messenger 宿主函式庫旁邊（Bubble Fighter 為 `nmcogame.dll`）。它在每次啟動前立即寫入。
- 結構：版本 1。它包含遊戲與版本 id、分支、執行個體 id、messenger 模式、hook 記錄路徑、可選的版本指紋、可選的 `[login]` 段（含帳號與票據）、可選的 `[endpoint]`（含伺服器主機與連接埠），以及依序排列的 `[[plugins]]` 項目及其選項。
- hook 執行階段在版本指紋不符時拒絕載入模組，並把未知鍵視為錯誤。這些檢查驗證工作階段格式與相容性，不能阻止本機使用者修改或刪除檔案，也不能阻止其省略模組項目。
- 該檔案在其生命週期內都是機密：它可能攜帶帳號名稱與啟動票據。執行階段從不記錄這些值，啟動器會在用戶端結束時刪除該檔案。不要複製、編輯、共用該檔案，也不要將其與用戶端一起封裝。
- 如果檔案缺少，用戶端仍會啟動：它不載入任何模組，並根據重新命名後的原始函式庫是否存在來選擇 messenger 模式。
- hook 執行階段把記錄寫入工作階段中的路徑；當工作階段未指定路徑時，寫入 `%LOCALAPPDATA%\FantasyTale\BubbleFighter\logs\fthook.log` 下的預設位置。記錄是有界的：檔案在開啟時被截斷，其大小上限被限制在 16 KiB 到 64 MiB 之間（工作階段預設值為 1024 KiB），過長的行會被截斷，達到上限時會寫入一行最終標記。它每個模組記錄一行結果，且絕不記錄帳號或票據。

用戶端結束時刪除檔案是工作階段寫入器契約的一部分。呼叫寫入器與清理邏輯的正式啟動流程仍是待辦的路線圖工作，因此請把它視為設計中的生命週期，而非已觀察到的行為。

### 啟動器偏好設定與定義

- 啟動器在啟動時從執行檔旁的 `games` 資料夾讀取遊戲與用戶端版本定義。
- 介面提供六種語言，並把所選主題、強調色來源和語言作為啟動器視窗的本機偏好設定儲存。這些是外觀偏好，不是帳號。
- 啟動器自身沒有記錄；唯一寫入的記錄是遊戲用戶端內的原生 hook 記錄，如上所述。
- 尚未實作任何執行個體、伺服器、帳號或下載的檔案格式；它們與對應介面屬於同一批未完成的路線圖工作。本指南不記錄這些檔案路徑。

### 伺服器端啟動器資料

- 啟動器帳號儲存在伺服器的帳號儲存中，使用 PBKDF2-SHA256 密碼雜湊；啟動器自身從不驗證密碼。
- 持續性登入只儲存目前更新憑證的 SHA-256 雜湊，因此工作階段無需保留密碼即可續期，並能透過持續性提供者在伺服器重新啟動後存續。存取權杖與啟動票據僅存在於記憶體中，重新啟動即遺失。
- 存取權杖預設有效期限為 1800 秒，更新工作階段為 30 天，啟動票據為 60 秒；營運者可在已驗證的範圍內調整。

## hook 模組

Bubble Fighter 的用戶端修補以 hook 模組形式提供。模組是一個包含 `plugin.toml` 的資料夾，其中宣告身分、類型、相容性和選項；資料修補無需編譯器。啟動器目前隨附三個資料模組：

| 模組 | 用途 | 適用版本 | 是否必要 |
| --- | --- | --- | --- |
| `bubblefighter.fantasy.font-weight` | 把中國字重套用到每個語言群組，使其他地區用戶端上的中文文字呈現得與中國版本一致。 | `kr-812`、`cn-706`、`cn-717`、`id-400`、`id-1809` | 否 |
| `bubblefighter.fantasy.no-file-verify` | 讓用戶端的設定表完整性比較器回報一致，從而載入修改過的設定表；封裝內每個成員的雜湊仍然生效，因此修改過的設定表仍須正確重新封裝。 | `kr-1256`，以及尚無啟動器版本定義的 `kr-710` | 否 |
| `bubblefighter.fantasy.kr1256-shop-gates` | 重新啟用最終韓國用戶端所停用的工作列商店按鈕與共用功能入口。 | `kr-1256` | 是，`required_on = kr-1256` |

hook 的預期工作方式：

- 模組以版本 id、分支選擇器或所有版本來宣告版本相容性，並可以排除某些版本。未列出目前版本的模組會因無法使用而被略過，執行階段也會獨立於啟動器拒絕它。
- 標記為必要的模組在該版本上無法停用；必要模組失敗時會在用戶端產生一個警告訊息方塊（「Required client plugins could not be applied; the game may not work correctly.」），hook 記錄會為每個模組記錄一行結果。
- 模組依執行個體啟用，並以帶選項的 `[[plugins]]` 項目寫入 `session.toml`。新執行個體以該版本的預設模組清單啟動；在目前檢查點，九個定義中的該清單均為空。
- 啟動器中用於安裝、切換和設定模組的模組管理員尚未實作；模組頁面仍是預覽。透過手動編輯用戶端或其執行檔來安裝模組不是受支援的路徑。目前檢查點的執行階段只套用資料模組，並會以尚未支援原生模組的訊息拒絕原生類型模組。

與啟動器的其餘部分一樣，原生執行階段和這些模組由原生測試涵蓋，但在目前檢查點尚未在真實遊戲用戶端中驗證。三者都宣告最低 hook 執行階段版本為 3.0.0，且都是純資料修補模組；均未定義可設定選項。

## 伺服器啟動器 API

伺服器可以透過選用的 **`launcher.http`** 模組列公開啟動器 API。它是獨立的公用 HTTP 監聽器，不屬於管理 API：它有自己的位址、自己在 `/v1` 下的路由，沒有管理路由或 Bearer 權杖管理。它提供啟動器帳號、持續性登入、啟動票據、用戶端預設目錄，以及啟動器應當執行的相容性原則。

### 啟用該列

- 核心 bundle 隨附時該列處於**停用**狀態。`all` profile 啟用該列，並保留部署變數 `BFF_LAUNCHER_LISTEN`、`BFF_LAUNCHER_TICKET_SECONDS`、`BFF_LAUNCHER_HOST_POLICY` 和 `BFF_LAUNCHER_REGION`，預設值分別為 `127.0.0.1:8090`、`600`、`auto` 和空白地區。後續 `set` 修補檔可單獨覆寫設定項目，同時保留目錄設定。
- 該模組需要一個處於啟用狀態的持續性提供者，由它公布啟動器帳號存放庫與啟動器工作階段儲存。三種提供者都滿足：memory、SQLite 和 MySQL。`all` profile 使用處理程序本機記憶體提供者，因此重新啟動時帳號與登入會被重設；持續性部署應使用 SQLite 或 MySQL。
- 該列與 `admin.http` 相互獨立；啟用或停用其一不會改變另一個。

### 選項

該列的設定作為一個整體驗證；未知鍵會導致繫結失敗。最重要的鍵如下：

| 鍵 | 含義 | 預設值 / 限制 |
| --- | --- | --- |
| `listen` | API 監聽器的 IP 位址與連接埠。明文 HTTP；遠端部署應在其前方終止 HTTPS。 | `127.0.0.1:8090` |
| `name` | 目錄中顯示的公用伺服器名稱。 | `Bubble Fighter Fantasy`，最多 128 個字元 |
| `host_policy` | 啟動器應如何存取遊戲端點。 | `auto`、`ip` 或 `domain` |
| `region` | 向玩家顯示的選用區域。 | 空，或兩個大寫字母 |
| `supports_instances` / `supports_plugins` | 公布伺服器是否允許選取執行個體與用戶端模組。 | `true` / `true` |
| `registration_enabled` | 啟動器帳號是否可以註冊。 | `true` |
| `create_account_on_login` | 以未知名稱登入時是否以所填密碼建立該帳號，讓玩家不必註冊。名稱輸錯會建立新帳號。 | `false` |
| `minimum_launcher_version` | 註冊、登入、更新與票據簽發所需的最低啟動器 SemVer。留空則停用閘門。 | 空 |
| `client_policy` | 傳送給啟動器的商店來源與依模組的型別化規則，見[用戶端策略](#client-policy)。 | 空 |
| `session_seconds` / `refresh_session_seconds` | 存取權杖與可續期工作階段的有效期限（秒）。 | 1800 / 2592000 |
| `ticket_seconds` | 啟動票據有效期限（秒）。 | 600 |
| `requests_per_minute` / `max_concurrent_requests` | 全域請求額度與並行 API 工作數。 | 120 / 8 |
| `max_body_bytes` / `max_connections` | 請求主體與連線上限。 | 16384 / 128 |
| `instances` | 目錄中公布的用戶端預設，最多 64 個。 | 空 |

### 用戶端策略（`client_policy`） {#client-policy}

用戶端策略告訴啟動器：連線至本伺服器啟動時，哪些模組必須使用、可以使用或不得使用。它包含兩份清單，項目數量不設上限：

- `sources`：商店目錄，每個包含小寫的 `name`、`kind`（`http` 或 `alist`）、`base_url`（HTTP(S)，不含憑證、查詢或片段）與 `catalog_path`。
- `plugins`：每個模組一個項目，包含商店 `id`、選用的 `name` 與 `description`（只在 WebUI 顯示，不會傳送給啟動器）以及它的 `rules`。

每條規則有一個在所屬模組內唯一的 `id`、一個 `type`，以及選用的 `builds` 清單（為空表示所有版本）。規則類型如下：

| 類型 | 欄位 | 啟動器的行為 |
|---|---|---|
| `require` | 無 | 模組安裝並啟用之前拒絕啟動，並提供安裝。 |
| `disable` | 無 | 連線至本伺服器啟動時關閉該模組，不變更已儲存的選擇。版本本身要求的模組不能被關閉，此時拒絕啟動。 |
| `recommend` | `sources` | 每個用戶端、每條規則推薦一次，從指定來源安裝（清單為空時允許任何已宣告的來源）。 |
| `min_version` | `version` | 已啟用的模組低於此版本時，啟動前必須更新。 |
| `max_version` | `version` | 已啟用的模組高於此版本時，改為安裝來源中允許的最高版本；沒有可用版本時拒絕啟動。 |
| `latest` | 無 | 啟動前將已啟用的模組更新至最新版本。 |

版本採用嚴格的 SemVer。同一模組的兩條規則在版本範圍重疊且符合下列任一條件時衝突：類型相同；其中一條是 `disable`；一條是 `latest`、另一條是 `max_version`；或 `min_version` 高於 `max_version`（兩者相等即鎖定單一版本）。存在衝突、格式錯誤或未知類型的規則時，該列無法啟動。不認得某個規則類型的啟動器會拒絕連線至本伺服器啟動，並提示玩家更新啟動器。

目錄與每個登入回應都攜帶最低啟動器版本，以及名為 `clientPolicy` 的策略（`version`、`sources`、`plugins`）；規則的 `version` 或 `sources` 只在其類型需要時出現。玩家登入時收到的策略用於啟動；目錄中的副本只在登入前顯示。此策略設定的是相容的啟動器，不證明用戶端完整性，也不表示遊戲登入會檢查已載入的模組。管理 API 請參閱 [WebUI 用戶端策略](plugins.zh-tw.md#client-plugin-policy)。

### 用戶端預設（`instances`）

每個預設公布一個可選取的用戶端執行個體：

| 欄位 | 含義 |
| --- | --- |
| `id` | 穩定的預設識別，由字母、數字和連字號組成。 |
| `build` | 用戶端版本 id，例如 `kr-1256`。 |
| `name` | 本地化顯示名稱；必須包含 `en` 項目。 |
| `release_version` | 顯示在名稱下方的選用發行標籤。 |
| `client_languages` | 選用的用戶端語言選擇權威清單。 |
| `host` / `port` | 啟動器連線的公用遊戲端點；連接埠為必填。 |
| `launcher_login` | `required`、`optional` 或 `none`：伺服器公布的啟動器驗證偏好。這是目錄資料；遊戲伺服器的實際強制手段是大廳的 `authentication` 選項。 |
| `download_source` | 用戶端套件的選用下載來源：`kind`（`http` 或 `alist`）、`base_url`、`catalog_path` 與 `package_id`。URL 中不接受憑證、查詢或片段。 |

預設是公布的資料；啟動器尚不取用預設。伺服器不會把預設的 `build` 與自身組合出的 profile 對照檢查，因此一個拼寫錯誤會產生票據永遠無法兌換的預設。這種可攜式預設不是設計中本機的執行個體檔案：用於保存玩家伺服器繫結與模組選取的本機 `instance.toml` 尚不存在。

### 路由

所有路由都位於 `/v1` 下，且僅由啟動器監聽器提供。

| 路由 | 方法 | 驗證 | 用途 |
| --- | --- | --- | --- |
| `/v1/catalog` | GET | 無 | 伺服器名稱、區域、host 原則、支援旗標、最低啟動器版本、模組建議與用戶端預設。不受版本閘門限制，因此舊啟動器可以讀取該要求。 |
| `/v1/instances/{id}/instance.toml` | GET | 無 | 單一預設的可攜式執行個體中繼資料：結構、id、名稱、遊戲與版本。刻意不包含伺服器位址或憑證。 |
| `/v1/auth/register` | POST | 無 | 在啟用註冊時建立啟動器帳號。 |
| `/v1/auth/login` | POST | 無 | 驗證密碼並傳回存取權杖、更新憑證、帳號資料、最低版本與建議。 |
| `/v1/auth/refresh` | POST | 更新憑證 | 用一次性更新憑證交換一對新憑證。 |
| `/v1/auth/logout` | POST | 更新憑證或 Bearer 權杖 | 結束工作階段。不受版本閘門限制。 |
| `/v1/account` | GET | Bearer 權杖 | 已登入帳號的資料：顯示名稱與等級、經驗、Lucci 與現金（當對應的儲存可用時）。 |
| `/v1/launch` | POST | Bearer 權杖 | 為伺服器公布的版本簽發一次性啟動票據。 |
| `/v1/companion/tickets` | POST | Bearer 權杖 | 為帳號已持有的啟動重新簽發一次性 Nirinsou Link 票據（`{"launchId":"..."}`）。 |

啟動器透過 `X-FantasyTale-Launcher-Version` 標頭或 `launcherVersion` JSON 欄位宣告其版本。兩者都提供時必須一致。

### 版本閘門與 HTTP 426

設定了 `minimum_launcher_version` 時，註冊、登入、更新與票據簽發都要求啟動器版本達到或高於該值。沒有提供版本、版本無法解析或版本較舊的請求會收到 **HTTP 426 Upgrade Required**，內容為以下 JSON：

```json
{ "error": "launcher_update_required", "minimumLauncherVersion": "<configured version>" }
```

目錄端點本身不受閘門限制，因此啟動器在登入前總能讀取該要求。目前檢查點的啟動器尚不解析此回應，也不傳送版本；因此該閘門實際是伺服器端拒絕，而不是應用程式內的更新提示。

### JSON 相容性

啟動器 API 有意對新用戶端寬容，同時對自身取用的內容嚴格：

- 未知的**頂層** JSON 欄位會被忽略，因此較新的啟動器可以向較舊的伺服器傳送額外欄位而不會失敗。
- 請求仍必須是 JSON 物件。重複的頂層鍵、錯誤的類型、缺少必要值、無效的使用者名稱、與內容不一致的版本標頭、過大的請求主體或過深的巢狀都會被拒絕為 **HTTP 400**，並傳回 `{"error":"invalid_request"}`。
- 已知欄位由使用它們的處理常式驗證；伺服器未公布的版本會被拒絕為 `unsupported_build`。
- 這種寬容僅適用於 JSON API。模組列設定仍然嚴格：未知 YAML 鍵會在啟動時導致繫結失敗。

- 啟動器 API 在目前檢查點沒有變更密碼、重設密碼或刪除帳號的路由。營運者可以停用註冊，也可以在伺服器端封鎖帳號；啟動器沒有這兩種操作的介面。

### 錯誤與限制

| 回應 | 含義 |
| --- | --- |
| `400 invalid_request` | JSON 格式錯誤、重複的頂層鍵、欄位類型錯誤、使用者名稱或版本無效，或版本未公布。 |
| `401 unauthorized` | 缺少或已過期的 Bearer 權杖；工作階段已不再有效。 |
| `403` | 註冊已停用。 |
| `409 account_unavailable` | 使用者名稱已被註冊。 |
| `426 launcher_update_required` | 上文所述的啟動器版本閘門。 |
| `429` | 超出每分鐘請求額度或並行閘門。 |
| `503` | 達到驗證容量上限（保留的工作階段/票據過多）。 |
| `503 server_not_ready` | 所有列啟用、主機日誌輸出 `Server is ready` 之前，登入、註冊和啟動都會被拒絕。 |
| `404 companion_unavailable` / `404 launch_not_found` | 未啟用 `companion` 列時續簽票據，或續簽的啟動已結束、屬於其他帳號。 |

回應攜帶 `Cache-Control: no-store`。該監聽器依設計為明文 HTTP；啟動器透過網路連線時，請將其置於自己的 TLS 終止之後；不透過網路連線時，請保持在回送位址上。該 API 從不提供遊戲用戶端、其檔案或更新：下載來源作為資料公布，供啟動器未來安裝用戶端使用，伺服器不代理這些內容。

### 範例設定

該列通常透過 profile 修補檔啟用。此範例只使用預留位置和合成值：

```yaml
- replace:
    id: launcher.http
    disabled: false
    config:
      listen: "127.0.0.1:8090"
      name: "Example Community Server"
      region: "KR"
      minimum_launcher_version: "1.4.0"
      client_policy:
        sources:
          - name: "community"
            kind: "http"
            base_url: "https://store.example.com"
            catalog_path: "FantasyTale/source.toml"
        plugins:
          - id: "bubblefighter.fantasy.kr1256-shop-gates"
            rules:
              - { id: "needed", type: "require", builds: ["kr-1256"] }
              - { id: "floor", type: "min_version", builds: ["kr-1256"], version: "1.2.0" }
          - id: "example.optional-overlay"
            rules:
              - { id: "offer", type: "recommend", sources: ["community"] }
      instances:
        - id: "kr-1256-main"
          build: "kr-1256"
          name:
            - language: "en"
              text: "KR 2026"
            - language: "zh-CN"
              text: "KR 2026"
          release_version: "1.0.0"
          host: "game.example.com"
          port: 7100
          launcher_login: "required"
```

對於啟動器列已被取代、監聽器設定已被捨棄的 profile，請使用 `set` 修補檔明確加回：

```yaml
- set:
    id: launcher.http
    config:
      listen: "${BFF_LAUNCHER_LISTEN:-127.0.0.1:8090}"
      host_policy: "${BFF_LAUNCHER_HOST_POLICY:-auto}"
      region: "${BFF_LAUNCHER_REGION:-}"
```

## Nirinsou Link {#nirinsou-link}

Fantasy Tale Nirinsou Link（簡稱 Nirinsou）是執行中的啟動器與其啟動遊戲所在伺服器之間的選用 WebSocket 通道，
用來承載原版遊戲用戶端沒有對應封包的功能，例如顯示在遊戲上方的啟動器彈窗。遊戲連線本身不變；沒有 Nirinsou
的啟動與以前完全相同。

### 模組列

| 列 | 作用 |
| --- | --- |
| `companion` | 監聽器與啟動表。啟用後，每個 `/v1/launch` 回覆都帶有 `companion` 物件：`url`、一次性 `ticket`、`launchId` 和 `expiresAt`。 |
| `companion.notices` | 啟動器彈窗（`ui.notification.show`）與 `popup <文字>` 管理指令，權限為 `game.management.popup`。 |

兩列在 core 套件中預設停用，由 `all` 設定啟用。`companion` 的選項：

| 選項 | 預設值 | 含義 |
| --- | --- | --- |
| `listen` | `127.0.0.1:8092`（`BFF_COMPANION_LISTEN`） | WebSocket 監聽位址。 |
| `public_url` | 空（`BFF_COMPANION_PUBLIC_URL`） | 交給啟動器的 URL，路徑為 `/companion/v1`；`{host}` 會替換為啟動器存取時使用的主機。為空時為 `ws://{host}:<監聽埠>/companion/v1`。 |
| `ticket_seconds` | `120` | 連線票據的有效時間。 |
| `pending_seconds` | `900` | 啟動等待遊戲登入的最長時間，逾時即捨棄。 |
| `max_launches`、`max_connections` | `4096`、`2048` | 啟動表與同時連線數的上限。 |
| `message_bytes`、`queue_messages`、`queue_bytes`、`messages_per_second` | `65536`、`128`、`1048576`、`128` | 每個連線的訊息、佇列與速率上限，與啟動器一致。 |
| `idle_seconds` | `60` | 連線靜默多久後關閉；啟動器每 20 秒傳送一次心跳。 |

### 啟動如何繫結

1. `/v1/launch` 開啟一個啟動，回傳遊戲票據和 Nirinsou 授權；遊戲票據記錄該啟動。
2. 啟動器以子協定 `fantasytale.companion.v1` 升級 `GET /companion/v1`，並以 `Authorization: Bearer` 攜帶票據。
   票據只能使用一次；重新連線時透過 `/v1/companion/tickets` 取得新票據，新連線會取代舊連線。
3. 啟動器傳送 `companion.hello`，包含版本、啟動 ID、語言及其支援的訊息類型；伺服器回覆 `companion.welcome`，
   給出雙方都支援的類型和狀態 `pending_game`。
4. 遊戲登入兌換遊戲票據後，啟動繫結到該遊戲連線，啟動器收到狀態 `connected`。伺服器訊息只在此之後傳送。
5. 遊戲連線關閉時，啟動結束，Nirinsou 連線隨之關閉。

遊戲始終沒有登入的啟動會在 `pending_seconds` 後捨棄。每則訊息都是 JSON，包含 kind、id、type、結構描述版本和
payload；格式錯誤、重複、未協商或過快的訊息會以原因代碼關閉連線，啟動器再用新票據重新連線。

監聽器使用明文 `ws`。透過網路存取時，請像啟動器 API 一樣放在 TLS 終止之後，並把 `public_url` 設為 `wss`。

## 疑難排解

以下是目前程式碼中存在的失敗路徑。目前檢查點的啟動器沒有應用程式內修復操作；復原意味著閱讀訊息或記錄並修正輸入。

| 症狀 | 檢查 | 可能原因與修復 |
| --- | --- | --- |
| `launcher.http` 列保持 `Pending` 或失敗 | 管理 API 中的 `/api/plugins` 與啟動記錄 | 沒有持續性提供者公布啟動器帳號與工作階段儲存。請只啟用一個持續性提供者。 |
| 大廳啟動失敗並回報 `authentication: ticket requires an active launcher.http row` | 啟動錯誤 | 已啟用票據驗證（預設）或 `launcher_tickets`，但沒有處於啟用狀態的 `launcher.http` 列公布票據服務。請啟用啟動器列，或在可信任的區域網路中設定 `BFF_LOGIN_AUTHENTICATION=open`。 |
| 玩家登入後立即以 `launcher ticket required` 斷線 | 伺服器記錄 | 大廳以 `authentication: ticket` 執行，而用戶端送出的是一般帳號名稱：它不是由啟動器啟動的，或其版本目前還拿不到啟動器票據。請透過啟動器啟動，或在可信任的區域網路中使用 `open`。 |
| 遊戲以 `invalid launcher ticket` 關閉連線 | 票據是否由本伺服器針對該確切版本簽發且尚未使用 | 票據已被停用、已使用、已過期、為另一版本簽發，或自其簽發以來啟動器列已重新啟動。請重新啟動啟動器、重新登入並再次啟動用戶端。 |
| 舊啟動器收到 HTTP 426 | 該列的 `minimum_launcher_version` 與啟動器版本 | 啟動器低於設定的最低版本。請更新啟動器，或為測試清除最低版本。目前啟動器尚不顯示更新提示。 |
| HTTP 400 `invalid_request` | 請求主體與版本欄位 | JSON 格式錯誤、重複的頂層鍵、類型錯誤、使用者名稱無效，或標頭/內容版本不一致。 |
| HTTP 401 `unauthorized` | 存取權杖有效期限與伺服器重新啟動 | Bearer 權杖已過期，或伺服器重新啟動後捨棄了記憶體中的 Bearer 權杖。請重新登入；更新憑證應能續期工作階段。 |
| HTTP 429 或 503 | 請求速率、並行請求、保留的工作階段 | 觸達伺服器有界的驗證限制。請稍後重試，或有意識地提高限制。 |
| 某個版本回報「has no verified launch arguments yet」 | 該版本的 `login` 值 | 該版本定義為 `login = "unverified"`。請使用具有已驗證登入模式的版本。 |
| 用戶端顯示「The launcher session file is invalid; no client plugins were loaded.」 | 訊息方塊文字與用戶端資料夾 | `session.toml` 已過期、被手動編輯或格式錯誤。請透過啟動器啟動用戶端，以便寫入全新的工作階段；不要手動編輯工作階段。 |
| 用戶端出現必要模組警告 | hook 記錄的結果行 | 標記為必要的模組失敗或對該版本無法使用。請閱讀該模組 id 對應的記錄行，並修正其選項或相容性。 |
| 啟動器預覽無法啟動用戶端 | — | 目前檢查點尚未實作啟動；預覽中的啟動按鈕沒有動作。 |
| 用戶端啟動後缺少預期的模組 | 工作階段記錄路徑下的 hook 記錄（預設 `%LOCALAPPDATA%\FantasyTale\BubbleFighter\logs\fthook.log`） | 工作階段未寫入、模組對該版本無法使用，或修補未符合。請閱讀該模組 id 的結果行。啟動器沒有記錄檢視器，也沒有修復操作。 |

對於啟動器 API 之外的伺服器端故障，請參見[疑難排解](troubleshooting.md)。

## 散布邊界

- 本伺服器儲存庫與啟動器儲存庫都不散布遊戲用戶端、解包後的用戶端資料、專有原生檔案或其他遊戲用戶端材料。啟動器儲存庫包含啟動器程式碼、定義與資料 hook 模組；遊戲用戶端始終是玩家自己的副本。
- 每版本的原生參數、`login.ini` 差異與 hook 選擇都是**資料**：版本定義、模組定義與工作階段項目。它們不是針對個別版本的程式碼分支。
- 原生 hook 執行階段與 messenger 宿主由啟動器儲存庫自己的原始碼建置。hook 模組由該執行階段根據模組定義套用；手動編輯用戶端執行檔或其函式庫不是受支援的安裝方法。
- 伺服器把下載來源與用戶端預設作為資料公布。它不代理、代管或散布用戶端套件。

## 下一步

- [伺服器設定](configuration.md)介紹啟動器 API 背後的 profile 列與環境變數。
- [部署](deployment.md)介紹如何執行伺服器、持續性、備份與網路安全。
- [疑難排解](troubleshooting.md)介紹與啟動器無關的伺服器端故障。

## 伺服器公開展示與准入 {#server-presentation-and-admission}

`GET /v1/catalog` 新增 `presentation`，包含 `motd`、`iconUrl`、`images`、`onlinePlayers`、
`playerLimit`、`state`、`whitelist` 和 `rosterEnabled`。在 `launcher.http` 列中透過
`motd`、`icon_url`、`images`、`show_online_players`、`player_limit`、`maintenance`、
`whitelist_enabled` 和 `roster_enabled` 設定。預設不限制准入及人數，公開名單預設開啟。
工作階段服務不可用時人數為 null，`show_online_players: false` 時為 -1。只計算已驗證的
遊戲玩家，按帳號去重，排除已關閉連線及已識別的對局連線。尚未接入持久化峰值儲存，因此不提供歷史最高人數。

`GET /v1/players` 不需要 Bearer，回傳 `schema: 1`、`enabled`、`players` 和 `truncated`。
每位玩家只有 `name` 和 `level`。`roster_enabled: false` 回傳空名單且不讀取工作階段登錄表；
名單開啟但登錄表不可用時回傳 HTTP 503 `player_state_unavailable`。
`max_roster_players` 限制單次筆數，預設 1000，範圍 1–10000；`truncated: true` 表示
實際線上玩家更多。人數與名單公開開關互相獨立。HTTP 模組讀取 `ISessionRegistry`，透過
`IPlayerSession.LoginConnectionId` 識別實際登入連線。共用玩家狀態的對局連線，以及未提供
該身分的外掛工作階段，不計入公開線上名單。票據簽發因此無需等待使用票據的大廳與對局服務啟動。

用戶端預設支援選用的 `protocol_version`（1–65535）和 `patch_version`（最多 64 字元），
分別輸出為 `protocolVersion` 和 `patchVersion`，與現有的 `build`、`releaseVersion`、
`clientLanguages`、`downloadSource` 並列。未設定的值保持未知；這些展示欄位尚未用於驗證啟動要求中的用戶端版本。

`maintenance: true` 阻止登入及簽發新票據，回傳 HTTP 503 `server_maintenance`。
選用的 `maintenance_start`、`maintenance_end`、`maintenance_description` 分別輸出為
`maintenanceStart`、`maintenanceEnd`、`maintenanceDescription`；時間僅作說明，不會自動排程。
`whitelist_enabled: true` 依 `whitelist` 驗證帳號名稱，不區分大小寫，最多 4096 個不重複帳號；
未列入時回傳 HTTP 403 `not_whitelisted`。正數 `player_limit` 要求啟動時有工作階段服務；
達到上限時新玩家收到 HTTP 503 `server_full`，已在線上帳號仍可取得替換票據。
這些是啟動器准入檢查，不是原子席位預留；直接遊戲連線和票據兌換尚未重新檢查這些選項。

驗證失敗透過 JSON `error` 回傳 HTTP 401 `account_not_found`、`wrong_password` 或
`account_banned`，只有密碼正確後才披露停權狀態，不編造停權原因。Bearer 失敗仍為 `unauthorized`。
關閉註冊回傳 HTTP 403 `registration_disabled`，要求限制回傳 HTTP 429 `rate_limited`，
工作階段容量不足回傳 HTTP 503 `authentication_capacity`。維護期間仍可更新工作階段及登出；
網路連線失敗由呼叫端處理，不偽裝為伺服器驗證錯誤。

同一個 `POST /v1/launch` 要求也可攜帶 `release_version`、`patch_version`、
`launcher_language`、`native_language` 與 `client_language`。版本標籤若有提供，必須是非空白、
不含控制字元且不超過 64 個字元的字串；它們描述發行版本，不會解析為通訊協定版本，也不與公告的標籤比較。
語言若有提供，必須精確為 `zh-CN`、`zh-TW`、`ko`、`en`、`id` 或 `vi`。
已知欄位不合法時，在占用票據名額之前回傳 HTTP 400 `invalid_request`。舊要求可以省略全部五個欄位；
未知頂層欄位仍會忽略，重複鍵仍會拒絕。成功簽發票據時，模組日誌記錄 build 與這些描述資訊，
不記錄帳號名稱、Bearer 或票據。這些資訊不能證明已安裝用戶端的版本、語言或完整性。

遊戲連線兌換啟動票據後，`launcher_language` 決定該連線收到的伺服器提示語言，包括房間指令與自動開局提示。
用戶端所屬地區、`native_language` 和 `client_language` 均不決定這些提示的語言。
未提供語言或缺少譯文時，使用本地化模組的 `default_language`。
`launcher_languages` 將介面語言標籤對應到資源名稱；預設設定將 `zh-CN` 對應到 `zh-Hans`，
將 `zh-TW` 對應到 `zh-Hant`，其他標籤直接比對資源名稱。目前沒有越南語字典，`vi` 使用部署預設語言。
沒有啟動票據語言資訊的連線同樣使用部署預設語言。語言設定持續到連線關閉，並保留至首次建立角色完成。
