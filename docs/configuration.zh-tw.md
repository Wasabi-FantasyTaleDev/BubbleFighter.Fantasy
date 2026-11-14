---
title: "伺服器設定"
description: "組合模組 profile、設定環境變數、連接埠與持久性。"
updated: 2026-11-12
---

Bubble Fighter Fantasy 沒有固定的一組伺服器元件。`profiles/` 下的一個 **profile** 決定執行哪些模組資料列、服務哪個用戶端組建、開啟哪些監聽連接埠，以及使用哪種儲存方式。本頁說明 profile 如何變成執行中的伺服器、設定檔案的位置、環境變數與連接埠的運作方式，以及如何切換持久性與用戶端資料。

**本頁對象：** 需要改變伺服器執行內容的維運人員，以及在啟動前需要檢查實際設定的開發者。

**開始之前：**

- 先建置一次伺服器：`dotnet build BubbleFighter.Fantasy.sln -c Release` 必須以零警告完成。參見[安裝](installation.md)。
- 在儲存庫根目錄開啟終端機。本頁使用 `dotnet run` 的命令都假定目前目錄是儲存庫根目錄，因為 profile、packets 與用戶端資料都相對於工作目錄解析。
- 真實部署時將 `.env.example` 複製為 `.env`。停用 MySQL 或使用記憶體或 SQLite 的檢查不需要任何 `BFF_MYSQL_*` 變數。不要把真實憑證寫入準備提交的檔案。
- 如果打算啟動遊戲組建，請準備好用戶端資料。儲存庫不包含用戶端資料；用戶端資料見本頁「用戶端資料」一節。

## 設定的運作方式

啟動伺服器有兩種方式：

- `bff run --profile <id>` 從 YAML 檔案組合 profile 並啟動其模組資料列。以下各節都描述這條路徑。
- `bff start`（可執行檔不帶參數啟動時也會使用）執行封裝的首次安裝 profile，包含安裝助手與管理資料列。它刻意不讀取儲存庫 `.env`；環境變數見本頁「環境變數」一節。

### 組合順序

profile 按層組合，後一層會修改前一層的組合結果：

1. `profiles/<id>/profile.yaml` 宣告 profile 身分、服務的用戶端組建、使用的 bundle、自己的 `vars`，以及要套用的修補檔案。
2. `profiles/bundles/<name>.yaml` 中每個 bundle 依檔案順序提供模組資料列。
3. 依 `patches:` 列出的順序套用 profile 的修補檔案。組建 profile 通常先套用自己的 `patch.yaml`，再套用 `profiles/bundles/shop-supplement-lucci.yaml` 之類的共用補充修補檔。
4. 當 profile 列出 `profiles/<id>/local.patch.yaml` 時，隨後套用該檔案。它被 Git 忽略，缺少時靜默跳過，是放置開發者覆寫設定的位置。
5. 命令列上的每個 `--patch <file>` 在所有 profile 修補檔之後，依給定順序套用。
6. 字串值依 profile 的 `vars` 與處理程序環境進行插補。`--dump-config` 顯示最終結果。

由於命令列修補檔最後套用，本機覆寫檔案可以把 `persistence.mysql` 指向某個資料庫，而命令列修補檔仍能在某次執行中將它關閉。

### 模組資料列

實際執行的一切都是**模組資料列**。一列指定模組實作並向它傳入設定：

```yaml
- id: admin.http
  plugin: admin.http
  config:
    listen: "${BFF_ADMIN_HOST:-127.0.0.1}:${BFF_ADMIN_PORT:-7300}"
```

| 鍵 | 必要 | 意義 |
| --- | --- | --- |
| `id` | 是 | 穩定的資料列識別，供修補檔與管理 API 使用。在同一 profile 內唯一。 |
| `plugin` | 是 | 從模組目錄探索到的模組實作 id。 |
| `config` | 否 | 綁定到模組選項類型的對應。未知鍵會綁定失敗。 |
| `disabled` | 否 | `true` 保留該資料列的組合結果但阻止啟用；預設 `false`。 |

進階資料列還可以宣告 `parent_id`、`isolated_services`、`version` 與 `lifecycle`。這些鍵控制巢狀、依組建隔離的服務以及資源預算；儲存庫提供的 profile 已在需要處設定它們。應將它們視為 profile 定義的一部分，而不是部署設定。

伺服器依「所需服務何時可用」啟用資料列，而不是依清單順序。兩個已啟用的資料列不能發佈同一個服務介面：登錄會拒絕第二次註冊。這正是同一時間只能啟用一個持久性提供者、切換儲存屬於組合變更而非執行階段開關的原因。

### 修補操作

修補檔是單鍵操作組成的 YAML 清單：

| 操作 | 形式 | 作用 |
| --- | --- | --- |
| `replace` | `{id, plugin?, disabled?, config}` | 取代指定資料列。給出的 `config` 會取代整列設定。 |
| `set` | `{id, config: {...}}` | 只修改指定的設定鍵並保留其餘鍵；`null` 值會刪除該鍵。 |
| `insert` | 資料列清單 | 附加新資料列。 |
| `remove` | 資料列 id | 移除該資料列。 |

資料列不存在時 `replace` 與 `set` 會失敗，未知操作會使組合失敗。本機檢查使用的記憶體隔離修補檔就是一個很小的 `replace` 修補檔：

```yaml
- replace:
    id: persistence.mysql
    disabled: true
- replace:
    id: persistence.memory
    disabled: false
```

### 插補與驗證

`${VAR}` 插入變數值，`${VAR:-default}` 提供預設值。如果 profile 的 `vars` 與處理程序環境都沒有定義該變數且沒有預設值，組合會失敗並指出檔案、資料列與變數。profile 自己的 `vars` 優先於同名環境變數，停用的資料列不參與插補。

設定綁定會將 `snake_case` 鍵對應到模組的型別化選項。未知鍵、錯誤型別、超出範圍的數字與選項驗證失敗會合併回報為一則 `Invalid configuration for row '<id>': ...` 錯誤。

### 背包發布限制

`dialect.messages` 外掛列會依連線循序發布背包、商店狀態和舊版實驗室完成結果。`max_inventory_publications_per_connection` 限制執行中的發布與排隊呼叫者總數（預設 `8`，範圍 `1..256`）。`max_shop_sessions` 限制保留時間基準或已獲准發布的連線數（預設 `512`，範圍 `1..65536`）；已失效但尚未結束的工作仍計入限制。不同連線可以獨立發布。超過任一上限時立即失敗，不再增加排隊呼叫者。

連線關閉或外掛列卸載會取消等待者並使準備中的發布失效，延遲恢復的呼叫不能重新安裝舊時間基準。確定性的編碼錯誤會保留原有時間基準。傳送序列開始後的失敗會使該連線的發布狀態失效並要求關閉連線；已提交的背包保留，清理過程不會再次扣款或發放物品。提高排隊上限不會使發布變成客戶端確認或資料庫交易。

## 檔案位置

| 路徑 | 內容 |
| --- | --- |
| `profiles/<id>/profile.yaml` | profile 身分、用戶端組建、`parent`、bundle 清單、`vars`、修補檔清單。 |
| `profiles/<id>/patch.yaml` | profile 自己的修補檔，在 `profile.yaml` 中列出。 |
| `profiles/<id>/opcodes.yaml` | 該用戶端組建依名稱解析的 opcode 表。 |
| `profiles/<id>/local.patch.yaml` | 選用的、被 Git 忽略的開發者覆寫設定，存在時自動套用。 |
| `profiles/bundles/*.yaml` | profile 引用的共用資料列集合。 |
| `packets/*.yaml` | 封包配置與 `builds.yaml` 的組建繼承關係；啟動時讀取，不編譯進程式。 |
| `.env` | 部署變數；被 Git 忽略。 |

Host 專案會把 `bootstrap/`、`profiles/`（不含 `local.patch.yaml`）、`packets/` 以及兩個 `data/` 檔案（`data/level-experience.csv` 與 `data/locales/*.json`）複製到建置與發佈輸出中。模組組件發佈在 `plugins/<ProjectName>/` 下，這也是 Host 旁預設的模組根目錄。因此除了執行階段、用戶端資料以及啟用 MySQL 時所需的資料庫外，發行目錄是自足的。

資料列內的路徑相對於處理程序工作目錄解析。使用 `dotnet run` 時從儲存庫根目錄執行；使用已發佈的 Host 時從發行目錄執行。若從其他位置啟動，請傳入 `--profiles-dir`，並注意 `packets` 與 `client-data` 路徑仍依工作目錄解析。

## 執行前檢查

列出目前檢出提供的 profile 與探索到的模組實作：

```sh
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- profiles list
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- plugins list
```

`profiles list` 每列輸出 profile id、定位字元與描述；本次發行共有 19 個 profile（各用戶端組建 profile，以及 `all`、`management`、`setup`）。`plugins list` 輸出模組 id、版本、支援的用戶端組建與來源；使用 `--plugin-root <dir>`（可重複）可將探索指向其他目錄。

不啟動伺服器而列印實際設定。務必附加記憶體隔離修補檔，使指向真實資料庫的開發者 `.env` 或 `local.patch.yaml` 不會被連線：

```sh
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- run --profile kr-812 --patch tools/local-checks/isolated-memory.patch.yaml --dump-config
```

傾印會列出每一列的 `id`、`plugin`、提供該列的 `source` 檔案、設定時的 `disabled`、生命週期預算以及插補後的 `config`。在修改連接埠或儲存前閱讀它：這就是伺服器實際會執行的組合。組合失敗時，Host 會在 stderr 列印 `error: <message>` 並以結束碼 1 結束；訊息會指出失敗的檔案、資料列與鍵。

## 環境變數

### 載入規則

- 除 `start` 外，所有命令都會先從工作目錄、再從 Host 目錄向上尋找包含 `BubbleFighter.Fantasy.sln` 或 `.git` 項目的儲存庫根目錄。若該目錄存在 `.env`，其中的指派會套用到尚未設定的變數。
- 已存在的處理程序環境變數優先。`.env` 只補齊空缺，從不覆寫你的 shell 或服務管理員。
- `start` 從不讀取儲存庫 `.env`。首次安裝 profile 從處理程序環境與 `--state-dir` 取得自己的變數。
- 檔案以 UTF-8 讀取，大小上限為 64 KiB。每列是 `KEY=VALUE`，也接受 `export KEY=VALUE`；值可以加引號；空白後未加引號的 `#` 開始註解；格式錯誤的列會失敗並回報檔案路徑與行號。變數值從不寫入日誌。載入檔案時主控台會列印 `Loaded unset variables from <path>.`。
- 發行目錄沒有方案檔或 `.git` 項目，因此複製到已發佈 Host 旁的 `.env` **不會**被讀取。部署時應透過處理程序環境、服務管理員或容器環境設定變數。

### 常用變數

| 變數 | 用途 | 預設值 |
| --- | --- | --- |
| `BFF_BIND_HOST` | 遊戲與中繼監聽器綁定的位址。 | `0.0.0.0` |
| `BFF_PUBLIC_HOST` | 告知用戶端用於連線與中繼的位址。 | `127.0.0.1` |
| `BFF_ADMIN_HOST` / `BFF_ADMIN_PORT` | 管理 HTTP 綁定位址。 | `127.0.0.1` / `7300` |
| `BFF_LOG_LEVEL` | 主控台日誌等級：Trace、Debug、Information、Warning、Error、Critical、None。 | `Information` |
| `BFF_LOG_FILE` | 選用 Host 日誌檔；`--log-file` 優先。未設定時關閉檔案日誌。 | 未設定 |
| `BFF_LOG_FILE_LEVEL` | 檔案日誌門檻；`--log-file-level` 優先。使用與主控台相同的等級名稱。 | `Warning` |
| `BFF_LOG_FILE_MAX_BYTES` | 每個保留檔案的 UTF-8 位元組上限；1024–1073741824。 | `16777216`（16 MiB） |
| `BFF_LOG_FILE_COUNT` | 保留檔案數，包含目前檔案；1–32。 | `5` |
| `BFF_LOG_FILE_QUEUE_CAPACITY` | 等待寫入的記錄槽數；1–65536。 | `256` |
| `BFF_LOG_FILE_MAX_QUEUED_BYTES` | UTF-8 內文位元組預算，包含正在寫入的記錄；1024–268435456。 | `16777216`（16 MiB） |
| `BFF_LOG_FILE_SHUTDOWN_TIMEOUT_MS` | 關閉時排空等待上限，單位毫秒；100–30000。 | `5000` |
| `BFF_LOG_FILE_RETRY_INITIAL_MS` | 重新開啟檔案的初始重試延遲，單位毫秒；100–60000。 | `1000` |
| `BFF_LOG_FILE_RETRY_MAX_MS` | 指數重試延遲上限，單位毫秒；100–60000，且不得小於初始延遲。 | `60000` |
| `BFF_CLIENT_DATA` | 各組建用戶端資料的根目錄。 | `client-data` |
| `BFF_COMPILED_DATA` | 各組建編譯後用戶端資料檔案所在的目錄；它是伺服器會自行重建的快取，見部署指南的小型伺服器一節。 | `state/compiled-data` |
| `BFF_PACKET_LOG` | 將每個封包寫為封包日誌檔案中的一列，用於除錯。 | `false` |
| `BFF_PACKET_CAPTURE` | 啟用選用的完整資料包記錄器。 | `false` |
| `BFF_STATE_DIR` | 首次安裝流程（`start`）的狀態目錄。 | 可執行檔旁的 `state` |
| `BFF_SQLITE_DATABASE_PATH` | 修補檔啟用 SQLite 提供者時使用的絕對存檔路徑。 | 無 |
| `BFF_LEVEL_EXP_TABLE` | 回合獎勵使用的累計經驗表。 | `data/level-experience.csv` |

預設組合還要求下列 MySQL 變數：

| 變數 | 用途 | 預設值 |
| --- | --- | --- |
| `BFF_MYSQL_HOST` | 資料庫主機。Docker Compose 會將它改為其 `mysql` 服務。 | `127.0.0.1` |
| `BFF_MYSQL_PORT` | 資料庫連接埠。 | `3306` |
| `BFF_MYSQL_USER` | 資料庫帳號。 | `bff` |
| `BFF_MYSQL_PASSWORD` | 密碼。啟用 MySQL 列時必須非空；停用的列不會讀取它。 | 空，啟用 MySQL 時必須設定 |
| `BFF_MYSQL_DATABASE` | 資料庫名稱。可以使用任何已有的資料庫。 | `bf_game_db` |

連接埠與遊戲數值在後續章節說明。`.env.example` 是含註解的完整清單；請複製它，不要自行發明變數名稱。

## 連接埠與位址

`BFF_BIND_HOST` 是伺服器監聽的介面，`BFF_PUBLIC_HOST` 是伺服器告知用戶端使用的位址。位於 NAT 後方時兩者可以不同：綁定到能到達用戶端的介面，並公佈用戶端實際可存取的公用位址。讓 `BFF_PUBLIC_HOST` 保持回送預設值，只在用戶端與本機同機時有效。

監聽連接埠來自 profile，通常經由依組建的變數與 `vars` 設定。本次發行的兩個基準使用：

| 端點 | kr-1256（現代） | kr-812（舊版） |
| --- | --- | --- |
| 遊戲 TCP | `7100`（`KR_1256_GAME_PORT`） | `7110`（`KR_812_GAME_PORT`） |
| 中繼 TCP | `7101`（`KR_1256_RELAY_PORT`） | `7111`（`KR_812_RELAY_PORT`） |
| 管理 HTTP | `7300`（`BFF_ADMIN_PORT`） | `7300`（`BFF_ADMIN_PORT`），同一個 WebUI |
| 選用原生選擇器 | 不使用 | 掛載 `center-selector.yaml` 時為 `7112` |

`all` profile 將已接上的用戶端組建組合進一個處理程序，每個組建使用自己的遊戲監聽器。其預設遊戲連接埠從 `7100` 開始以十遞增（`kr-1256` 7100、`kr-812` 7110、`id-400` 7120，直到 `cn-408` 7250），每個組建的中繼監聽在遊戲連接埠加一（`<BUILD>_RELAY_PORT`，對用戶端公布 `<BUILD>_PUBLIC_RELAY_PORT`）。舊版組建在中繼連上時透過中繼進行回合，中繼失敗時退回遊戲連線；`cn-706` 與 `cn-705` 的中繼封包未經稽核，回合仍走遊戲連線。開放防火牆規則前，請用 `--dump-config` 讀取所選 profile 的實際 `listen` 字串。

管理連接埠應保持在回送或受控網路。只開放所選 profile 提供服務的遊戲與中繼連接埠，絕不要將 MySQL 暴露在公用介面。

## 持久性

核心 bundle 提供三個持久性資料列：

| 資料列 | 持久性 | 適用時機 |
| --- | --- | --- |
| `persistence.memory` | 僅處理程序生命週期；重新啟動即重設。 | 一次性本機檢查與測試。 |
| `persistence.sqlite` | 一個本機存檔檔案。 | 需要持久資料但不想執行資料庫伺服器的單機部署。 |
| `persistence.mysql` | 外部 MySQL 資料庫。 | 真實部署，尤其是在多處理程序或多主機時。 |

同一時間只能有一個提供者生效。登錄會拒絕同一儲存庫介面的第二次註冊，因此必須在同一個修補檔中只啟用一個並停用另外兩個。核心 bundle 中 MySQL 資料列已啟用、另外兩個已停用；修補檔負責切換。

### 用修補檔切換提供者

從預設 MySQL 設定切換到 SQLite：

```yaml
- replace:
    id: persistence.memory
    disabled: true
- replace:
    id: persistence.sqlite
    disabled: false
    config:
      database_path: "${BFF_SQLITE_DATABASE_PATH}"
```

要回到 MySQL，將 `persistence.mysql` 取代為 `disabled: false`，並停用已啟用的提供者。

### MySQL

預設連接字串會插入 `BFF_MYSQL_*` 變數並固定 `SslMode=none`。需要 TLS 的部署應在修補檔中覆寫整個 `connection_string`，而不是修改 bundle。

該資料列啟用時會開啟連線並執行 `SELECT 1`。密碼缺少、連接字串格式錯誤或伺服器無法連線都會使該資料列失敗，啟動會回報失敗而不是在沒有儲存的情況下繼續執行。`auto_migrate` 預設為 `true`，此時模組會套用內嵌移轉：

- 移轉指令碼是名為 `NNNN_description.sql` 的內嵌資源，依檔案名稱順序套用。本次發行最高版本為 `0032`。
- 先建立 `schema_migrations`（若不存在），隨後每個待套用版本在自己的交易中執行並記錄。主控台會列印 `Applied migration <version> (<name>).`。
- 命名錯誤、版本重複或內容為空的指令碼會使本次執行失敗，而不是被跳過。
- 移轉**僅向前**。沒有降級路徑，因此較舊的伺服器程式可能無法在較新版本移轉過的資料庫上執行。備份與復原流程見[部署](deployment.md)。

### SQLite

SQLite 資料列要求絕對 `database_path`，該選項拒絕相對路徑。請將檔案放在發行目錄與日誌目錄之外，以免取代發行版本時遺失；也不要讓兩個執行中的伺服器共用同一個存檔檔案。該資料列還有玩家數、帳號數、資料庫大小與鎖定等待的上限，可在同一設定對應中設定。

記憶體提供者是基於處理程序內字典的有界儲存，提供相同的儲存庫介面。狀態在關閉時消失，其資料列刻意不發佈資料庫工廠。記憶體與 MySQL 互斥。

### 戰績記錄

`ingame` 的 `record_battle_statistics` 預設為 `false`。啟用時需要明確支援戰績的進度儲存、等級表與共用結算傳送器。它依遊戲模式記錄已接受的泡封、擊爆、營救、死亡與分數，並保存最終結果。對局中退出記錄為逃跑，不發完成獎勵；完成記錄在顯示結算前提交，退出結算畫面不會抹除已完成對局。載入失敗與被取消的對局不偽造結果。

Memory、MySQL 和 SQLite 均支援此選項。MySQL 遷移 `0039` 新增 `player_battle_records`；SQLite schema `9` 在玩家快照加入各模式統計。兩種持久化儲存將統計、進度、錢包獎勵與原有對局憑據放進同一交易，重新開啟資料庫後記錄保留，重複提交同一對局不會再次累計。舊憑據不會因重試附帶統計而被補登。升級前備份儲存；舊 SQLite 讀取器拒絕 schema `9`。不會重建先前未記錄的歷史。所有儲存提供 `max_battle_modes_per_player`（預設 `128`），MySQL 與 SQLite 允許 `1..4096`。模式容量滿時拒絕整筆新結算，不淘汰歷史。Memory 仍屬暫存，另提供 `max_battle_settlements_per_player`（預設 `4096`）；持久化儲存保留原有資料庫結算憑據。

爆破系列在下一回合載入期間保留同一個對局身分與累計計數。已有回合結束後，若對手在局間退出，剩餘終局成員會在下一次離場可能取消顯示之前完成記錄；只有首次載入不算已進行對局。結算重試次數有限，最終失敗會寫入日誌；沒有用於恢復任意持續儲存故障的持久化 outbox。

### 人物外觀與已記錄戰績

`lobby.fighter_info` 讀取被查詢線上帳號角色背包中目前穿戴的物品，過濾過期與壽命耗盡項，並在人物資料回應前發布該目標的裝備快取。角色缺失、角色選擇不唯一或登入工作階段已被替換時，不回傳成功快照。查詢唯讀，範圍是同一 build、同一資料域的線上玩家；不會預覽獨立預設頁或偽造逐實例屬性。

獨立選項 `lobby.fighter_battle_statistics` 預設為 `false`。開啟時需要 `fighter_info` 與同一資料域的戰績儲存。原生主頁面「無道具／道具」只接收模式 `1`、`3` 的已記錄總數：勝、負、平、逃、泡封、擊爆與營救。其他模式、伺服器個人分數與競技場等級不投影。用戶端根據各項計數自行計算場次與勝率，保留其自身對平局的處理規則。

沒有已知主模式記錄，或真實數值會使客戶端的有號計算溢位時，回傳原生失敗回應；不會截斷、縮放或代入全零生涯。關閉該選項時，外觀查詢保留原生建構子的統計預設值，這些預設值不代表真實歷史。Memory 只涵蓋目前行程實際記錄過的對局，不重建原服生涯或排位資格規則。

已審計主頁面結構涵蓋 `kr-1256`（KR2026）、`kr-812`、`id-400`、`id-1809`、`cn-324`、`cn-717`、`cn-774`、`tw-454`、`cn-279`、`cn-406`、`cn-706`、`cn-404`、`cn-408`。TW454 完整人物資料交換另由 `BFF_TW_454_FIGHTER_INFO=false` 控制；設為 `true` 才同時開啟方言與大廳繫結。結構涵蓋不代表原生用戶端驗收通過。

在隔離的單 build profile 中，將以下 patch 與 `tools/local-checks/isolated-memory.patch.yaml` 一起套用。`set` 保留原有協定、等級與結果設定；缺少能力時啟動失敗，不產生假資料。

```yaml
- set:
    id: ingame
    config:
      record_battle_statistics: true
- set:
    id: lobby
    config:
      online_players: true
      fighter_info: true
      fighter_battle_statistics: true
```
### 情侶關係儲存

記憶體資料列也提供作為基礎準備的 `ICoupleStore` 關係契約。其 `couples` 對應具有獨立上限：`max_relationships`（預設 4096，範圍 1–65536）、`max_pending_invitations`（1024，1–65536）、`max_pending_per_account`（16，1–1024）、`max_completed_invitations`（1024，1–65536）、`max_message_length`（512 個 UTF-16 程式碼單元，1–4096）及 `max_authority_length`（128 個 UTF-16 程式碼單元，1–1024）。接受邀請與分手會在同一處理程序內原子更新雙方關係。已完成回執有容量限制，可能被淘汰；呼叫端必須始終使用伺服器產生的全新邀請識別碼。此契約尚無 MySQL 或 SQLite 提供者，不消耗戒指，也不啟用原生情侶請求、登入還原或跨節點通知。僅有封包結構表涵蓋不代表這些玩法已經啟用。

### 本機隔離檢查

`tools/local-checks/isolated-memory.patch.yaml` 停用 `persistence.mysql` 並啟用 `persistence.memory`。由於命令列修補檔最後套用，它會覆寫指向真實資料庫的開發者 `.env` 或 `local.patch.yaml`。每次本機 `run`（包括 `--dump-config` 檢查）都要附加它；下文「本機隔離檢查」配方提供了完整命令。處理程序停止後記憶體資料即遺失，該修補檔絕不能取代正式環境的持久性設定。

## 用戶端資料

遊戲 profile 讀取所選用戶端組建自己的資料；儲存庫不包含遊戲用戶端及其檔案。資料根目錄預設為 `client-data`，可用 `BFF_CLIENT_DATA` 移動。其配置為：

```text
client-data/
  <build>/
    data/      保留用戶端原始路徑的用戶端封包，例如 Packs/ 與 Config/
    dataraw/   同一用戶端路徑下的解包散檔案，優先讀取
```

`<build>` 是 profile id，例如 `kr-812` 或 `kr-1256`。舊版 profile 讀取 `${BFF_CLIENT_DATA:-client-data}/${BFF_PROFILE_ID}/data` 與 `.../dataraw`；現代 `kr-1256` profile 對 `kr-1256` 使用相同結構。

若兩個目錄都不存在，`clientdata` 資料列會在啟用時失敗，錯誤訊息會指出它尋找的兩個路徑，伺服器無法完成啟動。啟動遊戲 profile 前請準備好資料。這些檔案是唯讀輸入：伺服器不會寫入 `client-data`，因此 Docker 設定以唯讀方式掛載它。

`management` 與 `setup` 不掛載用戶端資料資料列。首次安裝流程可以將準備好的資料匯入其狀態目錄；參見[安裝](installation.md)。

已安裝用戶端的上傳流程與 `tools/client-data/bfdata.py` 使用同一套產生規則。地圖 `.wb` 資源（包括巢狀的 `Maps/<Directory>/track.wb`）按原用戶端路徑解包到 `dataraw`，並在 `sources.tsv` 記錄來源與 SHA-256；準備好的 `data`/`dataraw` 匯入也會保留這些檔案。舊資料樹若缺少地圖 WB，請先從對應版本用戶端重新產生到新目錄，再啟用讀取它的功能。共用的 `WorldBlockFile` helper 讀取器保留名稱、位置與旋轉，供不同模式使用；伺服器不硬編碼地圖座標。

CN2011（`cn-279`）設定 `ingame.item_mode_fallback_point_count: 4`，在普通道具模式房主地圖報告沒有隨機箱點時啟用伺服器補充策略：從目前地圖的 `#sp_star_` helper 選取最多四個相異且空間分散的位置，沒有星點時使用 `#ic_` helper，以 `supply_default_min_ms` / `supply_default_max_ms`（預設 15–30 秒）更新中立 kind-0 箱。週期更新屬於伺服器策略，不代表原版用戶端存在固定隨機箱標記。原生已報告的隨機點繼續保留，其他 profile 預設為零。`item_mode_fallback_maximum_file_bytes` 預設 8388608，`item_mode_fallback_maximum_candidates` 預設 4096；地圖資源缺失、不支援或無效時會明確拒絕該報告，不猜測座標。

CN279 保留年級權重表中的十七種道具與一個原生持有欄位。碰撞時由用戶端抽取道具，伺服器驗證結果。CN279 還啟用 `item_mode_resolve_invalid_random_pickups`：對存在且符合拾取條件的隨機箱，若請求種類為零或不在允許池中，伺服器從允許池均勻抽取一次；有效用戶端結果繼續保留，固定種類箱與不合格物件不能使用該策略。關閉時，未解析的零種類請求被拒絕且不消耗箱子。補救分布屬於伺服器策略，不等同於原生年級權重。原生行動計量掉落保留初始速度；空出欄位後，仍存在且曾因滿欄被拒絕的箱會換用新物件 ID，掉落箱重建時靜止。使用道具會廣播給包含使用者在內的所有參與者，團隊醫療包 129 也走原生效果路徑；伺服器不為此另建 HP 帳本。這些自動化路徑仍需原生用戶端驗收。

## 常用設定

第一個配方是隔離檢查。其餘是明確指向持久儲存的設定：它們指向你自己擁有的儲存，且**不**包含隔離修補檔，因為那會停用正在設定的提供者。不要將持久性配方指向你無意修改的資料庫。

### 本機隔離檢查

```sh
cp .env.example .env
# 不需要資料庫設定：隔離記憶體修補檔會停用 MySQL。
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- run --profile management --patch tools/local-checks/isolated-memory.patch.yaml
```

主控台會回報已啟用的資料列，Host 就緒後輸出 `Server started with <n> active row(s).`。存取 `http://127.0.0.1:7300/` 的 `GET /health`：它傳回含 `status` 欄位的 JSON。用 `Ctrl+C` 停止。

### SQLite 伺服器

在儲存庫根目錄建立 `sqlite.patch.yaml`：

```yaml
- replace:
    id: persistence.mysql
    disabled: true
- replace:
    id: persistence.memory
    disabled: true
- replace:
    id: persistence.sqlite
    disabled: false
    config:
      database_path: "${BFF_SQLITE_DATABASE_PATH}"
```

在 `.env` 中將 `BFF_SQLITE_DATABASE_PATH` 設為絕對路徑，然後啟動目標 profile（遊戲組建需要其用戶端資料）：

```sh
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- run --profile kr-1256 --patch tools/local-checks/isolated-memory.patch.yaml --patch sqlite.patch.yaml
```

在主控台中尋找 SQLite 啟用列，並確認存檔檔案已出現在設定的路徑。用 `Ctrl+C` 停止；存檔檔案會保留。

### MySQL 伺服器

在 `.env` 中設定真實連線值，並保持預設 MySQL 資料列啟用：

```sh
BFF_MYSQL_HOST=127.0.0.1
BFF_MYSQL_PORT=3306
BFF_MYSQL_USER=bff
BFF_MYSQL_PASSWORD=<your-database-password>
BFF_MYSQL_DATABASE=bf_game_db
```

在本機核對設定時，使用隔離的記憶體儲存檢查 profile，不連線至此資料庫：

```sh
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- run --profile kr-1256 --patch tools/local-checks/isolated-memory.patch.yaml --dump-config
```

此傾印明確停用 MySQL，也不啟動監聽器；它不驗證資料庫連線或執行移轉。持久化執行請依照[部署](deployment.md)操作，並明確核對資料庫設定。實際啟用 MySQL 時會記錄 `MySQL persistence connected to <host>:<port>/<database> as <user>.` 並執行待處理的移轉。

### NAT：綁定位址與公佈位址

在 `.env` 中設定兩個位址，使監聽器綁定所有介面，同時向用戶端公佈公用位址：

```sh
BFF_BIND_HOST=0.0.0.0
BFF_PUBLIC_HOST=<public-address-clients-use>
```

連接埠變數也用同樣方式修改，例如 `KR_1256_GAME_PORT` 與 `KR_1256_RELAY_PORT`。只開放這些連接埠的防火牆規則，並在啟動前用 `--dump-config` 核對位址。若某個監聽器的位址必須偏離 profile 預設值，也可以對該監聽器資料列使用 `set` 修補檔：

```yaml
- set:
    id: game-listener
    config:
      listen: "0.0.0.0:7100"
```

## 多用戶端 profile

不同 profile 服務不同的歷史用戶端。組建 ID 是 `<region>-<wire version>`，其中 wire version 是用戶端的 `ClientVersion_Major`，不是檔案版本：`kr-812`、`cn-324`、`id-400`、`tw-454`、`kr-1256`。profile 目錄名稱就是封包表中使用的組建 ID。

`kr-812`（韓國 2015）是**舊版基準**：`profiles/bundles/legacy.yaml` 是它的資料列集合，舊版組建只在各自的 `vars` 與 `patch.yaml` 中宣告差異（連接埠、加密表、座位數、座位相關設定、封包開關）。`kr-1256`（韓國最終組建）是**現代基準**：它有自己的 `kr-1256`、`ingame` 與 `shop` bundle，其封包配置是 `cn-774` 與 `tw-454` 的參照。

| Profile | 用戶端組建 | 角色 |
| --- | --- | --- |
| `kr-812` | `kr:812:458` | 舊版家族的基準。 |
| `kr-1256` | `kr:1256:1304` | 現代基準；Dockerfile 預設使用的 profile。 |
| `all` | 16 個組建 | 所有已接上的組建在同一處理程序內執行，各自使用獨立監聽連接埠。 |
| `management` | 無 | 僅管理 HTTP 與儲存；沒有遊戲或中繼連接埠。 |
| `setup` | 無 | 與資料庫無關的首次安裝與管理。 |

其餘用戶端組建 profile 各自服務一個組建。它們的封包配置透過 `packets/builds.yaml` 與 profile 自己的 `parent:` 繼承：`cn-279` 基於 `cn-324`，後者基於 `kr-812`；`cn-738` 與 `cn-742` 沿用 `cn-735` 的配置；`tw-454` 基於 `cn-774`，後者基於 `kr-1256`。差異較小的組建只需標註差異，不必重寫配置。

組建之間還存在能力（capability）差異，依組建記錄在 `packets/builds.yaml` 中並沿基準繼承。例如「捨棄過期物品時排除類別 6」的限制在 `kr-812` 上開啟、在 `cn-324` 上關閉；現代 `kr-1256` 關閉了許多舊版能力，直到在該組建中稽核完對應的消費者；舊版房間在基準中為十二個座位，在 `cn-324` 中為八個。能力開關決定組建的協定層送出與啟用什麼；它們是資料，不是猜測。

依你準備連線的用戶端組建選擇 profile：讀取用戶端版本，找到符合的 profile id，使用該 profile 的連接埠並提供該組建的用戶端資料。`all` 用於同時執行多個組建；它會啟動每個組建的監聽器，並需要所有所選組建的資料。

已列出的 profile 意味著：儲存庫能組合這些資料列，並帶有該組建的封包資料與能力記錄。它不意味著：所有遊戲功能都已實作，或真實用戶端已驗證。自動化檢查通過、能力啟用或 profile 出現在清單中，都不構成真實用戶端相容性證明；能力記錄描述的是已稽核的原生支援，原生驗收仍需另行進行。

## 預設啟用的功能

發布的 profile 預設啟用所有已實作的功能，新伺服器可以直接測試全部內容。下表中的每個玩法開關預設皆為 `true`；在 `.env` 中設為 `false` 即可關閉對應功能。用戶端沒有給出的數值（成功率、扣除數量、武器池），profile 中已填好可用的伺服器策略，可用本機補丁取代。下文各節說明的欄位皆已由 profile 填寫。

| 變數 | 功能 |
| --- | --- |
| `BFF_TITLES_ENABLED` | 稱號、勳章兌換與自動回合勳章（`kr-812`、`kr-1256`） |
| `BFF_FAVORITE_WEAPON_ENABLED` | Favorite 武器點數與等級提示（`kr-1256`） |
| `BFF_FAVORITE_TITLES_ENABLED` | Favorite 挑戰稱號及其進度（`kr-1256`） |
| `BFF_ITEM_ENHANCEMENT_ENABLED` | 研究所的強化、永久轉換與升級（`kr-1256`） |
| `BFF_ITEM_MODIFICATION_ENABLED` | 武器改造（`kr-1256`） |
| `BFF_SCIENCE_LAB_ENABLED` | 舊版合成（`kr-812`） |
| `BFF_PET_COMBAT_ENABLED`, `BFF_PET_REWARDS_ENABLED`, `BFF_PET_CANCELLATION_SEEDS` | 寵物戰鬥效果、回合獎勵與抵消種子，適用於所有一般模式 |
| `BFF_CUSTOM_ROOM_RULES_ENABLED` | 自訂房間選項（`kr-1256`） |

各版本 profile 也列出了所有已稽核的模式補丁：`kr-1256` 的炸彈、隊長、BnB、Extreme Racing、Final Zone、OneKill、星星、幽靈與腳本殭屍、Rhea、AI 房間、Zombie Escape、等待室踢人、防禦種子與 WeaponMaster；`cn-774` 和 `tw-454` 為其已稽核的子集；舊版為炸彈與隊長。每個版本的 `modes.patch.yaml` 放在最後，統一給出結果計分板模式的聯集。啟動器票證、UDP 傳輸、叢集資料列與封包日誌保持關閉：它們改變的是用戶端的連線方式，而不是遊戲內容。所有時區設定在任何主機上都同時接受 IANA 名稱（`Asia/Seoul`）與 Windows 名稱（`Korea Standard Time`）。

## 選用功能 bundle

少數 bundle 需要明確啟用，且只被用戶端稽核支援的 profile 列出。

**`player-settings`** 掛載 `player.settings`，即玩家自訂按鍵的類型化儲存。封包本身由 `dialect.messages` 資料列的 `customized_keys` 選項綁定，由 `kr-812`、`kr-1256`、`cn-774` 與 `tw-454` 宣告；其他 profile 需先稽核其原生控制名稱與封包路徑。每個 `player.settings` 資料列都需要 `protocol`，並使用依 profile 的 `scope`，避免混淆不同組建的名稱。持久性提供者依帳號儲存對應；MySQL 透過移轉 `0032_customized_keys.sql` 儲存。每個提供者用 `customized_keys` 設定每帳號預算（`max_scopes` 1–64、`max_mappings` 1–512、`max_name_characters` 1–1024，三者組合必須能放入封包主體）。超出預算會直接失敗，而不會驅逐其他已儲存對應。

`dialect.messages.config.science_lab` 預設為 `false`，用於路由 `kr-1256`、`cn-774` 和 `tw-454` 已驗證的實驗室操作。沒有對應處理器的操作會回傳客戶端的實驗室關閉結果（KR 為 5，CN/TW 為 4）。啟用要求每組請求與回覆均已驗證且版面配置齊全，資料表不完整會直接失敗。此選項本身不提供配方、經濟策略、物品變更或實驗室設定。[選用物品強化](#選用物品強化) bundle 為 `kr-1256` 將其與 `science_lab_configuration` 和強化處理器一同啟用，並為該組建提供獨立啟用的選用改造（Modification）策略。舊版 `kr-812` 的合成與分解使用獨立的[選用舊版科學室](#選用舊版科學室)設定。原生實驗室介面驗收尚未完成。

過期物品捨棄也以同樣方式宣告：`dialect.messages` 資料列的 `inventory_actions` 選項綁定 `gcpReqInventoryThrowItem`/`gspRepInventoryThrowItem`（每個 legacy 組建和 `kr-1256` 均開啟），組建自己的 bundle 內的 `inventory.throw` 資料列透過儲存裝備服務移除該物品。行為被刻意限制得很窄：只移除一個已驗證玩家兩個背包中的該過期物品；若有符合的權益仍有效，整個移除會被拒絕；組建的類別 6 排除規則會被遵守。比賽中的請求會被拒絕。真實用戶端驗收仍待進行；僅憑封包表涵蓋不足以在另一個組建上宣告該選項。

**`launcher.http`** 是選用的公用啟動器 API。它預設停用，與管理工作分離。它接受 `minimum_launcher_version`（留空則關閉檢查）與 `client_policy`，即啟動器執行的依模組分類的型別化規則；規則只引用商店識別與已宣告的商店來源，絕不包含可執行檔路徑。設定最低版本後，較舊的啟動器會收到 HTTP 426。每個大廳列的 `authentication` 選項（`BFF_LOGIN_AUTHENTICATION`，預設 `ticket`）只接受能兌換啟動器票據的登入，此時需要本列處於啟用狀態；`open` 接受任何帳號名稱，只適用於可信任的區域網路。[啟動器](launcher.md)頁面說明此 API、用戶端預設、帳號與票據流程以及面向營運者的設定。

啟動器請求與登入工作階段限制在 `launcher.http` 行設定：

| 設定項 | 預設值 | 行為 |
|---|---|---|
| `requests_per_minute` | `120` | 每個連線來源 IP 每分鐘可接收的請求數；轉送標頭不會改變來源 IP。 |
| `max_rate_limit_addresses` | `4096` | 最多保留的 IP 視窗數（1–100000）；滿時淘汰最早的視窗。 |
| `max_sessions_per_account` | `8` | 每帳號的有效持久登入工作階段數（1–1024）；新登入會撤銷建立時間最早的工作階段。 |
| `max_sessions` | `1024` | 既有的行程內存取憑證與待用遊戲票據容量上限。 |

撤銷舊工作階段時也會清除它在本行程中的存取憑證與待用遊戲票據。監聽器啟動前刪除過期更新工作階段，之後在登入時最多每小時清理一次。帳號上限由儲存層執行，因此伺服器重新啟動後仍然有效。

## 被動中繼延遲回覆

`relay-latency` bundle 僅在 `kr-1256`、`cn-738`、`cn-742`、`cn-774` 和 `tw-454` 中加入預設停用的 `dialect.relay-latency`、`match-latency` 列。進行原生客戶端驗收測試時，可透過部署補丁同時啟用兩列：

```yaml
- set:
    id: dialect.relay-latency
    disabled: false
- set:
    id: match-latency
    disabled: false
    config:
      reply_word: 0
```

兩列的 `protocol` 都必須指向所屬版本的中繼協定，例如 `kr-1256/relay`；bundle 已提供此值。dialect 的 `packets_directory` 預設為 `packets`。邏輯列的 `reply_word` 範圍為 `0..4294967295`，預設 `0`；它是原生回覆處理器讀取後捨棄的四位元組未知值，不是請求時間戳記的回傳。

每個完整請求只會在發起請求、已通過認證且租約仍有效的中繼連線上收到一次回覆。回覆與握手傳送共用連線擁有者的序列化閘門，不必等待場景初始化完成。本文格式錯誤、租約失效及傳送失敗仍按連線錯誤處理，資源受現有連線及傳送佇列預算約束。握手順序和客戶端取樣設定保持原樣：啟用這兩列不會啟動客戶端探測。兩列預設保持停用，等待原生客戶端驗收。在 `all` profile 的補丁中使用帶版本前綴的列 ID。

## 可選 UDP 傳輸

UDP 由 `net.udp` 外掛提供。遊戲登入、房間准入與對局身分驗證繼續使用 TCP；UDP 承載已審查的原生移動與探索流量。所有現有設定預設都不會掛載 UDP 接聽器。

| 可選修補檔 | 已審查設定 | 傳輸方式 |
| --- | --- | --- |
| `profiles/bundles/udp-relay.patch.yaml` | `kr-812`、`cn-717`、`id-1809`、`kr-1256`、`cn-774`、`tw-454` | 透過伺服器 UDP 端點傳遞已驗證移動；接收者未綁定端點時退回 TCP。 |
| `profiles/bundles/udp-peers.patch.yaml` | `cn-279`、`cn-324`、`cn-406`、`id-400`、`cn-404`、`cn-408`、`cn-705`、`cn-706` | 伺服器協調端點探索及玩家之間的直接移動傳輸；失敗路徑保留對局 TCP 傳遞。 |

將其中一種修補檔套用到對應的單版本設定。兩種修補檔互為替代，其不帶版本前綴的列識別碼不適合直接用於 `all`。`BFF_UDP_PORT` 指定空閒 UDP 連接埠，`BFF_BIND_HOST` 指定接聽位址，`BFF_UDP_PUBLIC_PORT` 可選填外部對應的 UDP 連接埠（零表示通告實際綁定連接埠）。UDP 轉送獨立於遊戲與中繼 TCP 連接埠。玩家端點探索使用已驗證遊戲伺服器的 IPv4 位址，中繼旁路使用中繼伺服器的 IPv4 位址，因此外部 UDP 對應必須使用相應公網位址。

例如，先將一般 TCP/管理連接埠改到本機空閒連接埠並準備該版本用戶端資料，再執行：

```powershell
$env:BFF_LOGIN_AUTHENTICATION = "open"
$env:BFF_BIND_HOST = "127.0.0.1"
$env:BFF_UDP_PORT = "7603"
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- run --profile cn-324 --patch tools/local-checks/isolated-memory.patch.yaml --patch profiles/bundles/udp-peers.patch.yaml
```

掛載任一可選 UDP 補丁後，`BFF_UDP_ENABLED` 預設為 `true`。設為 `false` 可保留 TCP 運作，同時不開啟 UDP 接聽器、不啟動 UDP 協商。單人房間預設使用 TCP。只有對局開始、所有 relay 初始化且端點驗證完成後，才允許使用 UDP 移動。

UDP 設定僅限正式遊戲房間。CN279、CN324、CN406 和 ID400 的原生遊戲場景不傳送探索探測，因此在等待房間中準備端點並確認 peer 表。完整房間安裝會使已準備的表失效；控制訊息與房間操作共用序列佇列，不能越過房間安裝或場景切換。打洞和直接移動仍須等待對局開始且全部 relay 初始化。其他已審計版本在此屏障後發起協商。登入及自動場景、主選單、大廳、廣場和單人訓練均不協商 UDP；房間強制開關也不能繞過此限制。

房主可用 `!udp on` 強制協商 UDP（包括單人房間）、用 `!udp off` 保持 TCP，或用 `!udp` 切換目前房間偏好。房間命令無法開啟部署中不可用或已全域關閉的 UDP。未驗證或無法連線的路徑保留 TCP 備援。所有外掛命令都等價接受 `/`、`!`、`！`，這些字元僅用來觸發命令解析；遊戲內提示使用 `!`，因為 `/` 可能被客戶端原生命令攔截。管理後台與主控台也支援直接輸入 `udp on` 等無前綴命令，其用法提示使用 `/`。

房間內任何成員都可以輸入 `!udp status` 查看是否在使用 UDP：傳輸方式（經伺服器的 `relay` 或直連的 `peer`）、房間偏好，以及每名成員的 `UDP`、`NEGOTIATING` 或 `TCP` 狀態和原因，例如等待對局開始、已送出邀請但未收到 UDP 探測、已驗證，或客戶端退回 TCP。伺服器關閉 UDP 或該客戶端版本沒有 UDP 設定時也會說明。`!plaza` 立即離開目前房間（對局中也可以）並進入廣場：座位按主動退出的方式釋放，因此對局會結算離開者，其他人繼續對局，最後一名玩家離開時房間會被刪除。退回 TCP 時會說明原因：沒有 UDP 探測到達伺服器、挑戰回覆在返回途中遺失、依座位列出的探索或節點表逾時、比賽連線已關閉，或玩家之間沒有直連路徑。

`match-udp-directory.max_sessions` 限制旁路驗證租約數量。`match-udp-peers` 的 `max_rounds`/`max_peers` 限制保留的對局與玩家數量；探索、端點表確認、打洞及控制訊息的期限均可設定。私網端點候選只會在觀察到相同公網 IPv4 位址的玩家之間分享。換房、換局、斷線、消費者卸載與中繼替換會撤銷相應路徑。目前僅支援 IPv4，無法保證任意 NAT 或防火牆都允許玩家直接互通。

封包表也審查了 `cn-735`、`cn-738`、`cn-742` 的旁路封包：四位元組 cookie 與浮點移動座標。其傳輸轉接器已有隔離線路測試，但研究設定尚未掛載中繼修補檔所需的完整房間/對局元件。這表示封包涵蓋範圍，不代表可用的端到端設定。自動化通訊端場景模擬原生用戶端協定，真實遊戲用戶端驗收仍須另行進行。

多版本伺服器應將對應補丁的操作放入各個目標版本的可選 `local.patch.yaml`，使用不帶版本前綴的插件列 ID。共用 profile 的補丁先於各版本插件列執行，因此不能在 `all` 的補丁裡修改 `kr-812.dialect.rooms` 等帶前綴的列。每個版本應使用獨立 UDP 連接埠。peer 補丁同時啟用必要的原生 relay；`cn-705` 與 `cn-706` 預設保持這些列停用，且此補丁不會啟用場景種子表權限。

UDP 通訊端訂閱、租約註冊、房間佇列回呼、使用 Span/out 參數的查詢與 IP 端點記錄目前需要原生插件；遠端 SDK 尚無這些操作的轉接器。可序列化的房間快照和中繼控制操作仍透過契約 schema 提供。

`match-udp-peers` 支援 `punch_retry_ms`（預設 250；0 關閉重試），在 `punch_window_ms` 內重複傳送原生開始打洞指令，不延長視窗、不更換節點識別值。直連路徑仍要求目前已驗證的對局連線回報對應玩家的成員關係。`!udp status` 區分未收到原生結果和用戶端回報成功路徑為零；僅憑這些結果不能認定是防火牆或 NAT 故障。

對只在等待房間執行探測的版本，`discovery_refresh_ms`（預設 1000；0 關閉重新整理）會在房間等待階段使用相同玩家身分重複傳送原生單次探測請求。進入載入後停止重送，因為這些用戶端在載入階段不再傳送探索。公網端點或本機候選位址變更時，重新傳送清單並再次等待確認。在初始化完成的回合開始打洞前停止探索。

## 驗證與安全

`nodes[].authentication` 明確設定 `authority_id`、`api_url`、`mode`（`master-issued` 或 `own-authority`）與 `policy_mode: authority`。同一認證身分只能對應一個由管理員設定的 HTTP 或 HTTPS 來源，採用該服務的目錄與所選用戶端策略，不跨獨立認證服務隱式繼承或合併策略。缺少中繼資料不授權認證信任。

可選 `cluster-ticket-authority.patch.yaml` 在本機 Launcher 票據簽發器旁掛載 `cluster.ticket-authority`。各可信節點須有不同的兌換金鑰與明確的用戶端版本清單。私有 `POST /v1/nodes/{node}/tickets/redeem` 單次消費 `{ticket,build}` 並回傳帳號、Launcher 語言及啟動身分。`cluster-ticket-client.patch.yaml` 在工作節點掛載遠端提供者以取代本機票據提供者；遊戲登入策略必須要求票據。設定 `BFF_CLUSTER_TICKET_URL`、`BFF_CLUSTER_NODE_ID`、`BFF_CLUSTER_REDEMPTION_KEY`，認證位址的 HTTP 或 HTTPS 由管理員明確選擇。非同步請求有界，不重新導向、重試或回退為帳號名稱。逾時前票據可能已消費，須取得新啟動票據。獨立認證服務須使用自身已登入的 Launcher 工作階段與啟動票據。此功能尚非全域工作階段歸屬或原生選服認證整合。

- 未知設定鍵與無效值會使組合或啟用失敗，絕不會被忽略。啟用失敗的資料列會連同狀態與診斷一起回報，而不是被靜默跳過。
- `--dump-config` 會列印插補後的值，其中包括憑證。在問題報告中分享前請先去除識別資訊。
- 絕不提交 `.env` 或 `local.patch.yaml`；兩者都被 Git 忽略，且都可能包含機密或本機專用路徑。
- 每次本機 `run` 都附加 `--patch tools/local-checks/isolated-memory.patch.yaml`。這是防止開發者 `.env` 連到真實資料庫的唯一可靠辦法。
- profile 或修補檔的修改在下次啟動時生效：profile 只在啟動時組合一次。管理 API 可以重新載入單一資料列，但那是模組生命週期操作，不是套用新 profile 的方式。重新載入行為見[模組](plugins.md)。

## 可選伺服器探索

schema-1 伺服器目錄新增回應欄位 `generated_at_utc` 與目標欄位 `observed_at_utc`、`expires_at_utc`、`status_revision`（Contracts 4.5.0）。時間戳使用 UTC RFC3339；首次接受回報前，三個目標欄位皆為 null。目標欄位描述同一次由來源維護的回報。重複讀取或重新註冊同一租約不會續期；過期的手動入口保留過期中繼資料，撤銷租約或時鐘變化可縮短到期時間。到期時間不超過來源剩餘租期映射至 UTC 的截止點。修訂值只能作不透明的相等比較，不是可排序版本或租約憑證。用於分配時必須拒絕缺失、格式錯誤或過期的中繼資料；產生新回應不會為目標續期。這些公開的新鮮度欄位不授予帳號准入，也不將票據綁定至後端成員；自動啟動仍須取得明確准入的本次候選集合，其有效期同時受回報與認證授權的到期時間約束。

選用的 `cluster-launch-selection.patch.yaml` 在票據簽發端掛載 `cluster.launch-selection`。透過 `BFF_CLUSTER_NODE_ID`、`BFF_CLUSTER_AUTHORITY_ID`、`BFF_CLUSTER_AUTHORITY_URL`、`BFF_CLUSTER_DATA_SCOPE`、`BFF_CLUSTER_AUTOMATIC_SELECTOR_ID` 明確設定本機節點、認證權威識別及確切 API 來源、持久化範圍和自動入口識別。已認證的 `POST /v1/launch` 攜帶 `serverSelection: true` 後，回傳與票據綁定的 `serverSelection`，包含 `schema`、`grantId`、`build`、`authorityId`、`policyRevision`、`dataScope`、`expiresAtUtc`、`generated_at_utc`、`servers`。已授權成員池可以包含離線或尚未回報的設定節點；這表示可認證範圍，不代表目前可分配。快照固定本次啟動的原生入口識別、實體節點識別和端點綁定。票據保留原有期限並受授權 bearer 限制，不會因短心跳租約而縮短慢啟動登入時間。`POST /v1/launch/servers` 使用相同認證工作階段及 `{ticket,build,grantId}`，只更新原有綁定內的回報狀態，不消耗或替換票據、不新增成員、不更換端點、不延長來源期限。票據已消耗、授權已過期或無效時回傳 HTTP 409 `admission_closed`，讓更新循環可於正常登入後結束。可信工作節點兌換時，在一次性消耗的同一把鎖內檢查已認證節點是否屬於票據成員；本機兌換使用明確設定的本機節點識別。一般啟動維持原有行為。此 API 不代表原生重新導向、共用儲存或跨節點房間准入已完成。只有明確啟用 Center 的 gateway 模式且目錄存在自動入口時，掛載簽發端選服行後，所有已認證票據都受範圍限制，省略標記也無法繞過；明確傳入 `serverSelection: false` 回傳 HTTP 409 `server_selection_required`。`launcher.http` 的 `max_selection_targets` 限制每個授權保留的入口端點總次數，預設 128。 每個已授權工作節點必須對應唯一的標準手動入口，且端點綁定一致；簽發端在發布服務前檢查所有已設定版本，缺少或有歧義的原生對應會使啟用失敗。即使透過自動入口更新，也不能更換該標準入口識別。

單服 profile 預設不掛載 `cluster.directory` 與 `cluster.node`。`profiles/bundles/cluster-directory.patch.yaml` 為 `kr-1256` 同時掛載目錄及回報模組；`cluster-node.patch.yaml` 只掛載回報模組。明確設定 `builds` 與各節點的 `endpoints`，透過部署機密提供各不相同的隨機 `BFF_CLUSTER_NODE_KEY`。私有監聽預設為 `127.0.0.1:8093`；回報位址的 HTTP 或 HTTPS 由管理員明確選擇，不自動升級。

可選的 `profiles/bundles/cluster-room-directory.patch.yaml` 掛載 `cluster.room-directory`，提供 `IFederatedRoomDiscovery`。必須設定 `node_id`、`directory_url`、`access_key` 與明確的 `builds` 允許清單。每次呼叫都經過驗證讀取目前房間列表；請求失敗時不會傳回舊快取。`max_rooms`、`max_response_bytes`、`max_concurrent_requests`、`request_timeout_seconds` 分別限制房間數、緩衝位元組數、並行請求數與整個讀取過程的期限。超出並行預算會直接報錯，不排隊。重新導向、無效回應與未設定的版本也會明確失敗。房間目錄成員關係獨立於帳號資料，只控制可見範圍；探索服務不授予入房權限、不預留座位，也不修改原生房間列表。單服 profile 不掛載該列。Contracts 4.6.0 新增探索介面。

`cluster.directory.room_preparation_permits` 可選啟用僅供已認證節點使用的準備許可介面，預設 false。來源節點向 `/v1/nodes/{source}/room-preparations` 提交目前租約權杖、世代及目標節點、目標世代、用戶端版本、原生房號、房間建立識別與帳號編號。簽發要求節點租約新鮮且版本就緒、非空資料與入房範圍相同、共享房間可見範圍、明確的同一 master-issued 認證權威與同一已回報權威資料庫識別。目標節點使用自身節點金鑰與目前程序租約，在 `/v1/nodes/{destination}/room-preparations/claim` 一次性兌換，並重新核驗租約、端點權威及準確的等待中房間；已釋放節點與重用房號不能繼承許可。來源節點可在簽發路徑以已認證 DELETE 取消自身許可。`max_room_preparation_permits` 限制保存數量，預設 512；`room_preparation_permit_lifetime_ms` 設定單調時鐘有效期，預設 10000，允許 100..60000。目錄只保存權杖雜湊，不接收房間密碼。此許可僅授權目標節點準備，不預留座位、不認證原生遊戲連線、不移轉帳號占用，也不啟用跨節點房間點選。友好伺服器與獨立目錄仍僅分享可見資訊。

已發布的實際遊戲房間透過 `IPublishedRoomDirectory`（Contracts 4.7.0）提供分離的原生列表快照。每次建立都有獨立的 `instanceId`，不依賴可重複使用的房間編號；心跳保留原生列表所需的房主暱稱、模式及設定，不包含密碼、帳號記錄或工作階段前綴。目錄及探索用戶端均檢查原生中繼資料與摘要的一致性，並以 `max_room_settings_bytes`（預設 65536）限制所有設定區塊與回合選擇的總位元組數。隱藏房間、訓練及教學房間不會發布。這些中繼資料尚不會將遠端項目投射到原生列表，也不授權跨節點入房。

可選修補檔 `profiles/bundles/cluster-room-browser.patch.yaml` 為每個 `client_build` 掛載一個 `cluster.room-browser`，使用認證目錄並提供 `IRoomBrowser`（Contracts 4.8.0）。`node_id` 必須與目錄用戶端一致。瀏覽狀態與其遊戲連線同生共滅，連線數已由監聽器限制。`max_aliases_per_connection` 限制單一連線生命週期內的房間身分數量（預設值與上限皆為 32767，即原生有號 16 位元別名）。本節點使用即時快照取代心跳舊資料；其他節點完整的原生房間資料使用獨立正數別名，重排不改變別名，連線內不回收舊身分。點選遠端房間時重新查詢目錄並核對節點世代與房間建立身分；加入提示不是認證。目標消失、重建或無法取得時拒絕加入，不會退回恰好同號的本機房間。更新失敗清除可見別名，連線關閉或外掛卸載釋放狀態。僅已認證的遊戲主連線可瀏覽；本機加入維持密碼、容量及階段檢查，另核對建立身分。僅在掛載下述移轉修補檔、且目錄於兩個節點的 `native_migration_builds` 皆列出該版本時，點擊遠端房間才會發起已登入移轉，否則拒絕。單機設定不掛載此列。

可選的 `profiles/bundles/cluster-plaza-federation.patch.yaml` 掛載 `cluster.plaza-federation`，在節點之間共享廣場：同一客戶端版本的玩家無論登入哪個節點，都能在廣場中看到彼此。在目錄列設定 `plaza_federation: true`，並在每個節點列出共享的 `builds`，`node_id`、`directory_url`、`access_key` 與心跳列相同。只有 `data_scope` 相同且非空、心跳證明了該範圍資料庫的節點才共享廣場；擁有獨立資料的友好伺服器互相不可見。每個節點提交本地廣場的進入、移動和離開（`flush_interval_ms`，預設 100），並以一個請求為所有版本長輪詢其他節點的變化（`poll_wait_seconds`）。遠端玩家在本地廣場佔一個座位，以與本地玩家相同的座位與位置封包顯示，客戶端看不出差別。租約失效的節點會帶走其玩家；重新連線或目錄重啟後，節點會收到快照並重新送出自己的廣場。`max_queued_events`、`max_plaza_events` 與 `max_plaza_members` 限制佇列大小；客戶端廣場仍為 36 個座位。Contracts 4.25.0 新增 `IPlazaFederationEndpoint`。

可選修補檔 `profiles/bundles/cluster-migration.patch.yaml` 包含節點層級的移轉列（broker、arrivals、destination），`cluster-migration-build.patch.yaml` 包含各版本的列（瀏覽、來源、准入，以及 `lobby`、`dialect.messages`、`ingame` 開關）。先掛載 node、room-directory、account-store、account-sessions 與 write-fencing 修補檔，再掛載這兩個，並於目錄設定 `migrations: true` 與 `native_migration_builds`。目標端點的 `listener_id` 指遊戲監聽列：單一版本設定為 `game-listener`，多版本設定為 `<build>.game-listener`。須於來源列設定 `channel`、於准入列設定 `channels`，取該版本經核查的值；修補檔本身不填寫，未核查的版本會啟動失敗，而不會繼承其他版本的值。用戶端只接受其允許頻道的移轉回覆：kr-812 無條件允許 50、60、80，cn-324 允許 50、60 而拒絕 80。每個被持有的帳號佔用一個資料庫連線，資料庫連線上限必須涵蓋所有節點的線上玩家。

該按版本修補檔還會設定 `protocol.config.accept_retained_moves: true`，使移轉用戶端的 `gcpReqMoveInto` 可以在修補請求之前開啟目標連線；其處理程式仍須授權繼續交握。啟動清單只寫封包名稱，不寫數字：`pre_handshake_opcodes` 與 `alternate_handshake_sequence` 接受用戶端到伺服器的封包名稱，由各版本透過自己的操作碼表解析；數字、未知名稱、伺服器到用戶端的名稱或僅限中繼連線的名稱都會在啟動時終止伺服器。用戶端在每條輔助連線上先送出空標記的版本，於其設定檔中以 `auxiliary_prefix` 宣告一次（cn-279、cn-324 與 cn-406 送出 `gcpStartGPK`）。`answer_center_status: true` 將原生選服的狀態查詢辨識為該前綴加 `gcpReqCenterUserCnt`；明確設定的 `alternate_handshake_sequence` 仍然優先。目標端點公布設定檔自身的版本及其 `GAME_PUBLIC_PORT` 設定檔變數（取自 `<BUILD>_PUBLIC_PORT`）；多版本設定檔需以每個版本一項的端點清單取代它。

對於預置的 13 版本 `all` 設定，使用 `profiles/all/migration.patch.yaml` 取代單版本的節點層級遷移補丁。該補丁逐一指定 `<build>.game-listener`，並透過各版本的 `<BUILD>_PUBLIC_PORT` 公布連接埠，預設使用該版本的預置連接埠。將 `cluster-migration-build.patch.yaml` 的操作附加到各版本可選的 `local.patch.yaml`，隨後明確設定來源端 `channel: 60` 與准入端 `channels: [60]`。這些操作使用不帶前綴的列 ID；組合時，來源端與准入列會映射到該版本自己的 `inventoryview` 子樹。節點、房間目錄、帳號所有權、工作階段及寫入保護列只在共享設定層掛載一次。將節點回報器及房間目錄的版本清單設為選定版本。在目錄權威端啟用遷移，逐一列出參與節點支援的版本，並設定相符的資料、加入及房間目錄範圍。獨立資料庫必須保持獨立範圍，不能僅憑目錄探索獲得共享帳號遷移權限。單服 `all` 設定維持不變。

`home_unavailable_notice_enabled` 預設 `true`，要求存在原生通知傳送器。遷移補丁透過版本變數 `MIGRATION_HOME_NOTICE`，僅為沒有已確認通知處理能力的 cn-406、cn-774、tw-454 關閉它。返服遭拒時一律記錄警告，並讓玩家攜帶帳號權限留在訪問節點。

保留身分遷入服務按各版本的 `inventoryview` 子樹隔離，同一版本的大廳與遷入處理僅共用本版本的登入服務。

`ingame` 列可透過 `room_admission_reservations: true` 啟用目的房間名額預留，為每個版本發布可卸載的 `IRoomAdmissionDirectory`（Contracts 4.9.0）。`room_admission_lifetime_ms` 預設 10000，範圍 100–60000。預留數量受房間座位數限制，採用單調時鐘到期，並固定房間建立身分與帳號。一般加入及機器人不能占用預留名額；取消、到期、房主關閉座位、房間開始或關閉及卸載都會釋放未用名額。使用時再次核對房間密碼，要求同帳號、同版本且獨立完成認證的遊戲主連線，並維持原選單或廣場場景，再沿用正常入房及成員通知。預留不等於認證或帳號所有權；尚未串接遠端房間點選、目的原生通訊端認證和來源節點所有權移轉。單機預設不啟用。

`launcher.http` 的 `center: { enabled: false, mode: gateway }` 設定預設關閉；明確設定 `enabled: true` 才啟用，省略 mode 時使用 `gateway`，只有明確指定 `native` 才由伺服器承擔 Center。`GET /v1/catalog` 與 `GET /v1/servers/{build}` 回傳相同的頂層 `center: {enabled,mode}` 物件。舊 catalog 沒有宣告 Center，或僅指定 mode 而未啟用時，一律視為關閉。Launcher 先讀 catalog；關閉或 native 模式保留遊戲端點，不需目錄 API 或 Gateway。只有明確啟用 gateway 才讀取 schema-1 目錄，並要求兩處 Center 宣告一致。Launcher 仍為一個入口，選項在遊戲原生 UI 顯示。只有服務端公布 `automatic: true` 入口才啟用自動分配，其 `targets` 限定候選池；沒有自動入口時僅手動選服，不簽發授權。關閉或 native 模式即使掛載選服服務，也不簽發授權；明確請求回傳 HTTP 409 `server_selection_unavailable`。選項與實體節點的身分、名稱、端點保持獨立。這些設定本身不實作選服後認證或跨節點入房。

手動入口允許空 `nodes` 清單，以保留不可選項而不猜測目標位址；自動入口仍須設定候選。啟動器介面保留空入口，讀取端必須維持其不可選，不解析或連線至虛構位址。

入口支援選填的 `short_name`：長度為 1..128 個 UTF-16 程式碼單元，不能全為空白、不能含控制字元，代理項必須成對。目錄和啟動授權均以 `short_name` 回傳；未設定時仍退回完整 `name`。健康重新整理保留授權時的兩個名稱。簡短地區名稱須明確設定，不從完整名稱推導；名稱不會改變端點或驗證繫結。

入口可分組為 `sections`（例如 Main、Test Server、Oversea Server），第一個分區為預設分區。每個分區有 `id`、`name` 和選填的 `api_url`。沒有 `api_url` 的分區與發布目錄的啟動器 API 共用登入，一次登入即可經遊戲內清單進入其伺服器；設定了 `api_url` 的分區經該 API 登入並簽發自己的票據，啟動器分開保存其帳號。分組要麼全有要麼全無：設定分區時每個入口都必須在 `section` 中指定一個分區，未設定時都不得指定。`GET /v1/servers/{build}` 新增 `sections` 與每個入口的 `section`。節點也可宣告 `maintenance`，包含 `start` 與 `end`（含秒與時區偏移的 ISO 8601，皆可省略）和 Markdown 格式的 `description`；維護生效期間其目標會帶有維護時段，自動分配不再選擇該節點，啟動器將該伺服器顯示為維護中，並從遊戲內清單中移除。啟動器保留最後讀取的目錄，因此發布目錄的 API 無法連線時仍會列出所有分區，經各獨立分區自己的 API 查詢狀態，並將其餘伺服器顯示為離線。

`selectors[].nodes` 控制可見性，手動選項有零個或一個節點。`selectors[].automatic` 只回傳租約有效、就緒、未排空、未滿員的候選，成員須宣告相同且非空的 `data_scope`。`nodes[].data_scope`、`nodes[].join_scope` 是不透明宣告；空值不授權共享或加入，相同名稱也不實作這些能力。`nodes[].room_directories` 獨立控制房間摘要共享，友好伺服器可只互通目錄而保留獨立帳號。

自動選項還必須設定 `persistent_domain_id`，以 `SELECT REPLACE(domain_id, '-', '') FROM cluster_data_domain WHERE singleton_id = 1` 讀取權威資料庫標識。MySQL 遷移 0035 只初始化一次。先在權威資料庫執行遷移，再以 `auto_migrate: false` 啟動工作節點；共享資料的工作節點必須連線至同一權威資料庫。每次心跳在請求期限內重新讀取標識，只有相符候選才能進入自動分配池。設定錯誤的節點不會移除健康且相符的候選。同一已固定資料域的手動入口仍保留顯示，但回報標識不符時標記為離線。還原的資料庫副本會保留標識，所以此檢查用於發現設定錯誤，不能證明資料庫身分或即時複寫；獨立運行的副本須在啟用分配前使用不同標識。沒有標識服務的節點只能以空 `data_scope` 回報，不能進入自動分配池。

註冊建立具代際隔離的租約，遞增序號心跳續期。過期移除自動候選與房間，手動選項保留離線狀態。就緒需宿主就緒及已註冊遊戲監聽服務。回報僅含已認證登入人數和正式遊戲房間摘要，不含密碼或玩家身分；實際跨服路由完成前，遠端房間一律為 `joinAllowed: false`。

節點、選項、房間、本文大小、連線及期限均有上限。`lease_seconds` 須大於 `heartbeat_seconds + request_timeout_seconds`，錯誤組合使回報失敗。`draining` 停止自動分配並保留手動狀態及房間摘要。探索不提供共享持久化、全域工作階段權威、跨服加入、隊伍互通或中繼遷移；正式接納多節點玩家前須分別實作與驗證。

可選行 `persistence.mysql-account-ownership` 提供帳號佔用原語 `IAccountOwnershipStore`。完成遷移 0036 後才能掛載 `profiles/bundles/cluster-account-store.patch.yaml`，並設定 `BFF_CLUSTER_ACCOUNT_CONNECTION_STRING` 和權威資料庫識別碼；依據資料庫記憶體設定 `max_owners`。每個佔用使用獨立、停用連線池的連線立即嘗試取得命名鎖。驗證同時檢查原始鎖和固定的資料庫識別碼，失去佔用後不會自動重新取得。卸載會等待進行中的取得作業結束並關閉現有佔用。此原語尚未接入遊戲登入或隔離儲存庫寫入，不能單獨保證共用節點登入安全；單服設定不載入它。 取得佔用會傳回持有原生連線的生命週期控制代碼，遠端 SDK 尚無對應結果轉接器，契約清單明確標記此取得操作不受支援。

可選行 `cluster.account-sessions` 將帳號佔用接入遊戲登入。先掛載帳號儲存補丁，再掛載 `profiles/bundles/cluster-account-sessions.patch.yaml`，並對每個大廳設定 `require_account_ownership: true` 和 `duplicate_login: reject_new`。缺少接入服務時啟動失敗。工作階段只為開啟中的遊戲連線建立，其數量由監聽器的連線上限決定；`verification_seconds` 和 `verification_concurrency` 限制背景檢查。失去權威會關閉連線；卸載會取消等待中的接入、關閉已接入連線並等待清理完成。`IAccountSession` 特徵允許驗證佔用，但不移交其生命週期。此層尚未隔離進行中的儲存庫寫入和角色建立；完成這些寫入路徑的隔離與排空前，不要啟用共用正式資料庫。隨附的單服設定均不掛載這兩個補丁。

帳號交接還要求遷移 0037，並在 `persistence.mysql-account-ownership` 上明確設定與目錄身分一致的 `node_id`。預設空值保留一般佔用功能，不發布 `IAccountHandoffStore`。來源節點只能登記自己的交接；資料庫僅允許記錄中的目標節點認領、來源節點撤回。`cluster.account-sessions` 只有在該儲存服務存在時才發布 `IAccountSessionHandoff`。遠端 SDK 無法傳輸原生佔用控制代碼、遷移 IP 位址或代理回呼註冊，這些方法在提供轉接器前明確不受支援。此設定不會在單服設定中啟用原生遷移。

可選的 `cluster.migration-broker` 要求與 `cluster.node` 使用相同的 `node_id`、`directory_url`，`access_key` 為該節點的私有金鑰。目錄必須開啟 `migrations`（預設 false）。請求攜帶目前租約憑據，使用 snake_case JSON，經 TLS 或安全通道傳輸；診斷記錄不包含密碼。共用帳號遷移要求加入範圍、資料範圍、認證權威和實際持久資料域識別碼一致；訪客要求明確的加入信任與不同資料範圍。僅啟用傳輸不會啟用原生遷移或預留席位。收件匣已滿回傳 503，重複識別碼或失效租約回傳 409，未收到回覆回傳 504。

| 外掛行 | 設定項 | 預設值 | 範圍 |
|---|---|---|---|
| `cluster.directory` | `max_pending_migrations` | 256 | 1..4096 每個目標 |
| `cluster.directory` | `migration_request_timeout_ms` | 5000 | 500..30000 |
| `cluster.migration-broker` | `migration_request_timeout_ms` | 5000 | 500..30000 |
| `cluster.migration-broker` | `max_concurrent_requests` | 4 | 1..32 |
| `cluster.migration-broker` | `max_body_bytes` | 65536 | 1024..1048576 |
| `cluster.migration-broker` | `poll_wait_seconds` | 20 | 1..25 |
| `cluster.migration-broker` | `http_timeout_ms` | 35000 | 1000..90000 |
| `cluster.migration-broker` | `retry_delay_ms` | 1000 | 100..30000 |

HTTP 逾時必須大於輪詢等待與回呼時限。目標收件匣由一個受管工作輪詢；出站並行容量耗盡時立即拒絕新請求。

可選列 `cluster.migration-destination` 需要 broker、arrivals、心跳身分及帳號工作階段交接服務。`node_id` 必須與心跳身分一致；`endpoints` 包含 1..64 個不同的 `{ client_build, listener_id, host, port }` 項目。host 必須是標準 DNS 名稱，目的端逐次請求解析並要求取得 IPv4 位址。房間請求還需要對應版本的 `IRoomAdmissionDirectory`。缺少端點、目的租約世代失效或 DNS 解析失敗時拒絕準備；回原服請求不預留房間座位。此列尚不認證原生遊戲連線，也不啟用遠端房間點選。

必填的 `listener_id` 是目的端傳輸列的完整 ID，包含設定前綴。啟用時必須恰好找到一個具有該 ID、且使用對應版本遊戲協定的可見活動監聽器。待遷入記錄保留此 ID，取用時核對 `ConnectionAcceptedEvent` 實際觀測到的連線；未被觀測、已關閉、非 IP 或來自其他監聽器的連線，即使版本、帳號編號和位址相同，也不能取走記錄。遭拒的嘗試會保留記錄供正確連線使用。此檢查不授予帳號權限，原生遷入處理器仍須先認領交接，再載入玩家狀態。

`arrival_lifetime_ms` 預設 10000（100..60000），`cleanup_interval_ms` 預設 1000（100..10000），`operation_timeout_ms` 預設 5000（100..30000）。房間的 `room_admission_lifetime_ms` 應涵蓋到達期間及準備耗時。掛載 `cluster.migration-arrivals` 並設定 `max_arrivals`（預設 64，1..4096）：等待到達和已過期但仍待清理的記錄共用此容量。過期到達不能被消耗；取消失敗會記錄錯誤並重試，期間不釋放容量。卸載在操作期限內取消剩餘預留並報告失敗。成功取走到達記錄的使用者負責後續入房或取消該預留。

`INodeLeaseSource` 與 `IMigrationArrivalCleanup` 是共享 Contracts 4.12.0 服務，使獨立載入的插件保持相同介面身分。租約記錄可透過 SDK 序列化，但其持有權杖是私密資訊，診斷文字不包含權杖。到達清理記錄包含 IP 位址，且完成操作保留物件身分限制，因此仍需原生轉接器。

可選 `profiles/bundles/cluster-account-write-fencing.patch.yaml` 要求先掛載帳號儲存與工作階段補丁、執行遷移 0036，並在大廳啟用帳號佔用。它設定 `core.packet-router.decoratable: true`、啟用 `persistence.mysql.require_account_write_authority` 並掛載 `cluster.account-mutations`；未明確開啟裝飾的普通路由仍直接呼叫。`IAccountMutationScopes` 將不可變佔用控制代碼繫結至原始遊戲工作階段，封包分發選擇該工作階段，對局逐人結算與獎勵卡發放則選擇收款玩家的原始工作階段；延遲舊工作不能借用後來工作階段的權限。受保護的 MySQL 寫入在修改玩家狀態前，使用同一連線與交易鎖定並驗證權威資料列；佔用接管等待已准入的交易結束。缺失或已替換的權限直接失敗，不重新取得。新帳號角色建立在交易內初始化，修改既有帳號需要其佔用。Contracts 4.4.0 提供這些原生生命週期與交易介面，遠端 SDK 明確拒絕呼叫。此功能不設定共用儲存、不自動保護無關外掛，也不實作跨節點加入房間；啟用共用正式資料前仍須審核其餘全部寫入者。單服設定不掛載此補丁。

## 遇到問題時

- **組合因缺少環境變數而失敗。** 錯誤訊息會指出變數名稱；將它加入 `.env`（或處理程序環境），或在空值合法時改用 `${VAR:-default}` 形式。
- **修補檔指向未知資料列。** profile 與修補檔清單不一致；用未套用修補檔的 profile 或提供該資料列的 bundle 搭配 `--dump-config` 檢查資料列 id。
- **遊戲 profile 因用戶端資料訊息啟用失敗。** 訊息提到的 `data`/`dataraw` 目錄不存在；為該組建 ID 準備目錄，或改用 `management`。
- **監聽器無法綁定。** 其他處理程序佔用了連接埠；修改依 profile 的連接埠變數或停止該處理程序。

依序排查的清單見[疑難排解](troubleshooting.md)。

## 對局取消與開局規則

以下 `ingame` 行選項適用於所有版本。對局載入或進行中有玩家斷線時，對局會被取消：不顯示結算，其餘所有玩家都被送回廣場，方式與等待房間中離開的玩家相同。`cancel_round_on_disconnect`（預設 `true`）可關閉此行為，此時斷線的玩家只是單獨離開。玩家主動離開房間或輸入 `!plaza` 通常讓其他人繼續對局。啟用殭屍逃生後，Lua 房主離開會取消本局且不發獎勵。`load_timeout_ms`（預設 `60000`，`0` 表示不限，最大 `600000`）在屆時仍有玩家未完成載入時以同樣方式取消對局；該值應大於最慢用戶端的地圖載入與比賽連線建立時間。

在殭屍模式中，只有所有在場玩家都被感染時殭屍才獲勝；連線已經關閉的玩家不算作倖存者。

兩條開局規則預設關閉。`start_min_players`（0–32，座位與 AI 玩家合計）在人數不足時拒絕房主開局。`start_requires_team_balance` 在紅藍隊伍模式中，依 `assign_spawn_teams_on_start` 為無隊伍玩家分隊之後，兩隊人數相差超過一人時拒絕房主開局；否則無隊伍的玩家計入紅隊。用戶端沒有這類規則的拒絕原因，因此伺服器只記錄日誌，房間繼續等待。原生自動開局不受檢查。

## 下一步

繼續閱讀[部署](deployment.md)以建置並執行發行版本，或閱讀[模組](plugins.md)了解這些資料列的實際作用。

## 現代版本等待房間踢人

`dialect.rooms.config.room_kicking` 預設為 `false`。在 kr-1256、cn-774 和 tw-454 上啟用後，已審核的暱稱請求與移除通知接入共用的房主權限邏輯。只有等待房間的房主能移除另一位在房玩家；通知先於座位移除送出，被移除玩家返回廣場。此選項要求 `modern_rooms: true`，不能與 `plaza_only` 同用；缺少任一原生封包名稱或格式時，啟用失敗。

`dialect.rooms.config.npc_rounds` 用於應答現代版對局房主的原生波次管理器，海盜模式依賴它推進。kr-1256 設定包預設開啟。在海盜模式房間中，房主的波次開始、波次結束與刷怪表應答送給所有成員，全部波次結束的應答只送給房主；其他模式忽略這些請求。一般對局中的刷怪請求會檢查格式但暫不轉發，因為其傳輸方式尚未確認。初始教學使用同一綁定，仍只應答其單人房主。此選項要求 `modern_rooms: true` 並掛載對局綁定。

單版本隔離試驗時，在 `--patch tools/local-checks/isolated-memory.patch.yaml` 之外增加 `--patch profiles/bundles/modern-room-kicking.patch.yaml`。使用 `all` 部署時，將修補檔中的 `set` 操作複製到各目標版本被忽略的 `local.patch.yaml`，列 ID 使用不帶前綴的 `dialect.rooms`。真實用戶端驗收前保持關閉。此選項不改變舊版既有的踢人綁定。

## 帶類型的快速配對

對於已審核此能力的組建，`dialect.rooms.config.quick_matching` 掛載原生配對請求。`quick_match_default_mode_id` 選擇共用配對模式（0–65535，預設 0 拒絕請求）。類型 1 開始配對，類型 2 更新配對；各自不同的原生尾部皆完整驗證。隊伍加入與清單項目請求在流程實作前回傳原生拒絕。隨附現代版本設定保持關閉，等待真實用戶端驗收。

## 原生選服登入銜接

可選項 `dialect.messages.config.center_migration_prelogin` 預設為 `false`，且要求同時啟用一般登入。對 kr-1256，同時設定 `protocol.config.pre_handshake_opcodes: [gcpReqMoveInto]`，才能在修補請求之前接收全新的原生選服連線。介接器嚴格驗證雙字欄位長度、零保留身分及值為 0 或 1 的階段旗標，拒絕已登入或重複請求，並傳送原生修補回覆以繼續一般登入。僅在目前封包分派期間可用的 `IInitialHandshakeContinuation` 在分派結束時失效，且僅在處理器成功返回後生效。它不會標記版本已驗證、兌換票據、安裝玩家身分或授權遷移。大廳仍須要求有效的一次性 Launcher 票據，帳號名稱不能取代票據。單服預設設定維持不變。伺服器封包測試通過不代表真實用戶端已驗收。

`dialect.messages.native_migration` 預設關閉。它與 `center_migration_prelogin` 共用原生移動請求的單一路由：零編號仍要求明確啟用預登入選項，非零編號則分派為 `MoveIntoRequested`。分派本身不解除首包握手限制，也不授予帳號占用。此選項另提供由資料表校驗的遷入回覆與連線切換訊息，只有 opcode 表宣告確認及完成封包時才掛載這對訊息。目的端准入處理器必須先校驗綁定監聽器的待遷入記錄、認領帳號權限並安裝玩家狀態，才能要求握手續接。目的端准入由下述可選 `cluster.migration-admission` 行提供；來源協調由 `cluster.migration-source` 提供，部署接線仍須明確啟用。


可選的 `cluster.migration-admission` 行在目標預留完成後協調共用帳號入房遷移，應放在能看見對應版本大廳與房間服務的位置。啟用 `lobby.native_migration`（預設 false）與 `dialect.messages.native_migration`；大廳另需帳號所有權、持久化保留身分查詢、儲存型背包與錢包，且不允許角色建立預覽。獨立協調外掛避免大廳與房間服務形成循環依賴。Contracts 4.16.0 新增 `IRetainedPlayerAdmission`；安裝結果是原生玩家狀態，遠端 SDK 尚無配接器。

`channels` 必須明確列出已證明適用於該版本的原生頻道值，來源頻道不在清單中時拒絕遷入。kr-812 的 50、60、80 才會通過 kr-812 sub_A4A440 的無條件分支，其餘命名頻道依賴用戶端權限。不能直接把現代版登入頻道用於舊版。`max_migrations` 預設 64（1..4096），`timeout_ms` 預設 10000（100..60000），`cleanup_interval_ms` 預設 1000（100..10000）。等待確認與清理失敗共用容量；逾期、斷線或租約變更會釋放預留與帳號占用，清理失敗保留容量等待重試，卸載回報未完成的清理。

准入消費監聽器與位址綁定的一次性記錄，檢查目標租約，拒絕超出玩家模型正有號整數範圍的身分，並在讀取保留身分、解析背包與錢包前認領精確的帳號交接。不會建立身分或重複發放每日登入獎勵。只有玩家狀態安裝且連線提升傳送成功後才繼續初始握手。需要提升確認的版本保持認證階段，收到確認後完成提升並走一般房間准入；其他版本在立即提升後入房。返服遷入使用下述可選主選單准入，訪客仍被拒絕。訪客及部署接線尚未完成；發布的設定未掛載此行，仍需真實用戶端驗收。

可選的 `cluster.migration-source` 行將重新驗證後的遠端房間點擊接至原生遷出流程。它需要遷移代理、節點租約、帳號連線交接、線上玩家目錄，以及與 `client_build` 相符的唯一 `ChannelMoveOffered` 傳送器。必須明確設定 `channel`（1..255），且目的端須允許該頻道。`max_migrations` 預設 64（1..4096），`operation_timeout_ms` 與 `migration_timeout_ms` 預設 10000（100..60000），`cleanup_interval_ms` 預設 1000（100..10000）。發佈設定未掛載此行。

只有目前持有登入的主選單或廣場遊戲連線可以遷出。房間流程持有場景鎖；已掛載的隊伍目錄必須支援原子單人預留：撤銷既有邀請，並在清理前拒絕涉及該玩家的新邀請及入隊。單人預留以設定的待處理邀請容量為上限。準備前玩家進入 `Migrating` 階段，目的端先預留精確房間實例，來源再釋放帳號寫入權限，最後傳送資料表定義的原生轉服通知。逾時、回覆遺失、傳送失敗或連線失效時，取消精確目的端請求並只撤回本次交接；目的端已認領時關閉來源連線，不重新一般搶占帳號。清理失敗保留容量及單人預留以供重試。Contracts 4.17.0 新增來源介面及原生通知；需要生命週期所有權的隊伍預留尚無遠端 SDK 介接器。

共用帳號遷移在連續造訪多個房間時保留首次登入節點與原生頻道（Contracts 4.18.0）。來源外掛另需 `IMigrationTargetDirectory`，並在 `home_unavailable_notice_enabled` 為 true 時要求唯一原生 `ServerNotice` 傳送器。代理透過認證查詢重新解析原服租約，複核就緒狀態、容量、加入範圍、持久域與帳號簽發方；查詢本身不授予待遷入記錄或帳號權限。原服拒絕或無法使用時，玩家留在目前節點並在啟用通知時收到 `home_unavailable_notice`（1..512 字元）。

InGame 的 `return_on_leave` 預設 `home`，設為 `stay` 則留在造訪節點。`return_delay_ms` 預設 1500（0..60000）。先完成正常離房清理與對局離房結算，再等待返服。等待期間不占用場景鎖；斷線、連線被取代或任何場景代數變更都會取消舊返服請求，之後可透過退出廣場重試。原服的 InGame 須啟用 `home_migration_admission: true`，並具備 `room_admission_reservations` 與原生主選單傳送器。返服準備不預留房間席位。完成相同的帳號認領與連線提升後，目的端使用保留裝備還原來源節點的閒置場景，不重複提示教學。返服請求攜帶 `home_scene`（`MainMenu` 或 `Plaza`），入房請求一律使用 `MainMenu`。從廣場返服時沿用原生廣場安裝流程，避免額外跳回主選單；未提供廣場編碼器的版本退回主選單。Contracts 4.21.0 為 `MigrationRequest` 與 `IMigrationHomeAdmission.EnterAsync` 增加場景參數。掛載隊伍目錄時，目的端在等待提升確認及場景安裝期間持有原子單人預留；清理失敗時持續保留，直到資源釋放。發布設定仍保持關閉，等待部署與真實用戶端驗收。

隊長會帶著隊伍一起移動。隊長前往遠端房間或返回原服時，同一版本、位於主選單或廣場的每名線上隊員會在隊長的遷移邀請送出後立即收到同一目的地的邀請：兩個客戶端系列都會在收到任何已接受的遷移邀請時開啟待定連線，無需自行先請求。隊員遷移不使用單人預留；返服只帶走原服與隊長相同的隊員。在房間或對局中的隊員，或被目的地拒絕的隊員（例如房間已滿），留在原節點並保留本地隊伍；隊長的遷移不會撤銷。非隊長隊員不能單獨遷出，也不會自行發起返服。每個請求在 `party_leader` 中寫明隊長，單人遷移時節點傳輸省略該欄位；目的地透過 `IPartyDirectory.RejoinAsync` 把每名到達者重新加入已到達隊長的隊伍，到達順序不限，未配對的到達者在其到達時限過後捨棄。Contracts 4.23.0 新增這兩項。尚未經原生客戶端確認。

目錄的 `nodes[].native_migration_builds` 預設為空清單。僅列出已部署原生遷出、監聽器綁定目標准入與連線提升處理器，並已核對用戶端路徑的版本；項目必須唯一且對應已設定端點。目錄啟用 `migrations: true` 後，兩端都列出該版本，才會將共用帳號遠端房間標為可加入或接受遷移請求，返服也受此檢查。加入提示另需兩端租約有效、版本就緒、目的端有容量、加入範圍與房間目錄相符、持久資料庫身分及帳號簽發方完全一致。清單不授予席位預留或帳號所有權，準備階段仍會重新驗證路徑；訪客准入完成前，獨立資料域僅提供探索。

預留房間在玩家遷入前關閉、開局或變更密碼時，准入協調器會釋放席位並安裝保留玩家狀態的主選單，同時繼續持有目標節點的帳號寫入權。`cluster.migration-admission` 要求每個版本恰有一個 `IMigrationRoomRecovery` 服務；InGame 在 `home_migration_admission: true` 時發佈此服務。恢復作業等到原握手訊息處理結束後，再套用 `return_on_leave` 和 `return_delay_ms`。期間切換場景會取消返回，`stay` 則保留玩家在目標節點。恢復工作佔用 `max_migrations` 容量，在准入鎖外執行。如果主選單安裝失敗，仍會關閉連線並釋放已認領的資源。Contracts 4.19.0 新增此恢復服務，原生主選單訊息格式不變。

房間服務發佈 `IRemoteRoomMigrationEndpoint`（Contracts 4.20.0），`cluster.migration-source` 為其 `client_build` 綁定恰好一個端點。房間發佈、節點心跳與遷移因此能一起啟動：房間服務不再等待本身依賴心跳的遷移來源。重複來源或缺少匹配的房間端點會導致啟用失敗。卸載來源時拒絕新的遠端入房，並等待正在執行的入房與返回原服呼叫結束後才釋放；重新載入來源或心跳不會重新載入房間服務。無需增加設定列，也不會預設啟用遷移。

舊版房間配接器分別控制連線就緒與自動教學。`dialect.rooms.config.tutorial_move_in` 啟用原生主選單移入回覆；`automatic_tutorial`（預設 `true`）獨立控制隨後傳送的自動教學通知。需要略過首次教學的測試部署可設定 `automatic_tutorial: false`，同時保留房間操作所需的就緒回覆。單次登入的 `OfferTutorial: false` 同樣只抑制教學通知。

## 可選房間物件經濟

現代版房間方言與舊版共用相同的可選物件經濟綁定：`supply_items`、`lucci_drops`、`star_items`、`horde_lottery`、`instant_revival`、`pve_points`、`rescue_missions`、`counted_pve_items`。每項綁定仍要求自身已確認的封包能力，不支援的選項會在啟用時失敗。kr-1256、cn-774、tw-454 設定啟用補給、合作模式計數道具及 PvE 即時計分。模式 3 使用自身啟用的道具池和兩個普通持有槽。模式 16 初始化三個計數槽；槽內道具選擇及其餘立即生效的合作模式拾取道具，均為來自各版本自身已啟用 ItemSpec 資料的可設定伺服器策略。現代版地圖回報有十四個向量，隊伍元素為單位元組；產生、丟棄訊息分別為 37、33 位元組。KR 切槽回覆有三個欄位，CN、TW 額外攜帶第四個結果欄位。原生客戶端驗收仍待完成。


`dialect.rooms.config.weapon_objects` 預設為 `false`。僅在版本宣告已核對的 `weapon_objects` 能力時掛載原生武器掉落建立和拾取，目前為 cn-324。共用 `ingame` 行以 `max_weapon_objects_per_round` 限制每局累計建立數量（預設 256，範圍 1..65536），物件識別值從 `weapon_object_id_base` 之後分配（預設 `0x64000000`）。此區間應與玩家及其他對局物件識別值分開。拾取僅接受回報玩家自身角色，同一物件只能消耗一次；對局重設清除狀態。自動檢查不等於實機驗收。

### 用戶端顯示等級

每個用戶端版本在 `packets/builds.yaml` 中宣告 `display_level_max`，數值來自該版本自身 `Images/Icons/LevelMark.smf` 的圖示影格數。傳送玩家狀態、隊伍成員、廣場／房間座位、玩家資料、等待清單和結算時，超過上限的等級顯示為該版本的最高等級。資料庫等級、經驗和獎勵計算保留真實值。例如，194 級在 cn-324、cn-406 顯示為 180 級，在 kr-812 仍顯示為 194 級。新增版本必須宣告其自身資源支援的正整數上限。

### 早期暫時房間裝備

`dialect.rooms.config.room_inventory_selection` 預設為 `false`，要求同名封包版本能力。CN2011、CN2012 的設定啟用此項，並將房間行放在各自隔離的 `clientdata` 行之下。它將早期 `gcpRoomChangeInventory` / `gspRoomChangeInventory` 路徑與 `weapon_presets` 分開掛載。CN2011 透過 `room_inventory_selection_count` 宣告四個選擇值，CN2012 宣告五個；`room_inventory_selection_fields` 指定 `UserSlot` 中原生角色與武器短整數的欄位。綁定要求該版本的用戶端資料，依據 `Config/FighterConfig.xml` 與 `Config/WeaponSpecDetail.xml` 驗證角色和武器規格識別值，拒絕未知值，尚未確認用途的尾部選擇值只接受空值。玩家只能修改自己尚未準備的等待房間席位。房間快照保留該選擇，不發放持有道具，一般換裝或離開房間後清除。原生按鈕可見性是另一項問題：CN2012 初始化時隱藏舊按鈕，因此伺服器掛載此路徑不等於已具備可用的原生入口或通過實機驗證。

### 隊長對局

`dialect.rooms.config.captain_mode` 預設為 `false`，需要該版本獨立審核的 `captain_mode` 能力。支援 kr-812、cn-717、id-400、id-1809、cn-404、cn-408、cn-706、kr-1256、cn-774 和 tw-454，使用完整的 legacy 或 modern 對局配接器；僅選單、瀏覽器、廣場及等待房間配接器拒絕此選項。 模式 20 在對局開始後為每隊隨機指定一名在線隊長，變身、武器及技能由客戶端處理。隊長本人的死亡回報會把身分交給存活隊友；無人可選時等待共用復活流程。普通擊殺與隊長擊殺共用同一次死亡計分，擊殺隊長只增加一個普通團隊分，沿用共用時限、團隊總分、平手、結算及獎勵規則。隊長離開時會在有存活隊友後替換。此選項不傳送 `gspCaptainEvent`，不授權未經審核的版本，也不代表已通過原生客戶端驗收。 `ingame.config.captain_ultra_item_kind`（legacy 套件為 104，0 關閉）讓目前隊長的 ULTRA 請求只回給隊長本人，之後由客戶端自行把 ULTRA 顯示給其他玩家；其他玩家及已倒下隊長的請求不會得到回應，也不會重複任命。

### 蕾雅角色技能

`dialect.rooms.config.rhea_character_skill` 預設為 `false`。套用對應版本的 `rhea-character-skill.patch.yaml`，可為 kr-1256、cn-774 或 tw-454 啟用已核實的變身協定，需要完整的現代對局配接器及該版本的原生能力。只有目前穿戴角色物品 300029 的玩家，才能為目前回合、目前生命中的自身角色請求變身。接受的變化同步給本人和房內玩家；重複啟動會被忽略，已結束狀態的重複結束請求僅向本人確認。死亡、復活和轉化會清除已存變身狀態。充能及結束時機仍由原生客戶端控制；此選項不增加伺服器計時強制校驗，也不代表已通過原生客戶端驗收。

### 爆破對局

`dialect.rooms.config.bomb_mission` 啟用已審核的原生炸彈操作與結果繫結。`ingame.config.bomb_round_series` 進一步啟用連續回合，逐局交換攻守，將勝局數與擊殺總分分別記錄。legacy 格式為三局兩勝；kr-1256、cn-774 和 tw-454 為五局三勝。兩項預設均為 `false`，完整系列支援 kr-812、cn-717、id-400、id-1809、cn-404、cn-408、cn-706、kr-1256、cn-774 和 tw-454，需要共用結果介面及已審核的連續回合入場傳送器。套用對應的 `profiles/<build>/bomb.patch.yaml` 可啟用兩項並從一般復活流程中排除模式 19，要求具備觀察模式支援。modern 明確啟用修補同時將模式 19、20 加入計分板選擇。CN2013 cn-406 的模式工廠沒有這兩個專用分支，因此保持排除；此結論不代表其他 CN2013 修訂。

`dialect.rooms.config.extreme_racing` 預設為 `false`，需要已審核的 `extreme_racing` 版本能力（目前僅 kr-1256），並需要 `modern_rooms` 及其對局繫結；cn-774 和 tw-454 已有經過審核的計時、回合、記錄、終點、金幣與檢查點資料列，但競速能力仍關閉並拒絕此選項，等待完整對局審核。套用 `profiles/kr-1256/extreme-racing.patch.yaml` 即可啟用。模式 24 隨後進行 `ingame.config.extreme_racing_rounds` 個競速回合（1 至 5，預設 3）。每回合開始時啟動競速計時（`extreme_racing_round_ms`，預設 180000）。首位抵達終點後啟動終點倒數（`extreme_racing_finish_countdown_ms`，預設 10000）；倒數或競速計時結束，或所有在線玩家都完成時，該回合結束。完成者依名次獲得 `extreme_racing_place_points` 分數（預設 10、8、6、5、4、3、2、1），競速記錄板顯示本回合與累計分數，回合之間暫停 `extreme_racing_intermission_ms`（預設 5000，須大於 1000）。最後一回合後執行一次共用結算：分數較高的隊伍獲勝；房間不是兩隊時，分數最高的玩家獲勝。共用對局時限不會結束競速。檢查點觸發會被轉送，使每名玩家的進度標記前進；標記與復活點由客戶端自行配置。需要伺服器物件清單的地圖物件須透過下述獨立初始化選項建立。回合數、計時與分數屬於伺服器規則，原生客戶端驗收尚未完成。

伺服器依據具體操作人、隊伍與目前回合驗證安裝、取消和拆除。原生房主用戶端回報爆炸與目標結果；一般回合時間到期後，已安裝炸彈保留自身倒數。各局沿用共用載入、角色生命與取消流程，整場只結算一次獎勵，並保留同一場次識別值。一整隊主動離開會判該隊整場棄權，不增加未進行的勝局；現有斷線取消策略保持生效。此選項不啟用未經審核的版本，也不代表已通過原生用戶端驗證。

### 競速地圖初始化

`dialect.rooms.config` 和 `ingame.config` 的 `extreme_racing_initialization` 均預設為 `false`，須與既有競速交換一起在兩處啟用。執行階段能力目前僅支援 kr-1256。封包表亦描述 cn-738、cn-742、cn-774 和 tw-454，包括早期沒有石塊清除器欄位的雙映射初始化；存在表項不代表這些版本已啟用玩法。

在 `ingame.config.extreme_racing_stages` 中明確設定地圖策略。每項必須指定 `stage_directory`、正數 `first_cannon_identity`、`first_coin_identity`、`first_stone_disposer_identity`、`coin_variant: 0`，以及 `coin_rounds`：依原生金幣 helper 的數字後綴順序，為每枚金幣指定從 1 開始的回合數。地圖的 `SavePoint/StageCount` 必須等於 `extreme_racing_rounds`。砲台與清除器、金幣、石塊的身分區間不能重疊，且須低於玩家 actor 身分空間。匯入對應的 `Maps/<stage_directory>/option.ini` 和 `track.wb`；缺少 helper、身分重疊或金幣回合設定不完整會導致啟用失敗。

資源上限為 `extreme_racing_maximum_file_bytes`（預設 8388608，範圍 8..67108864）、`extreme_racing_maximum_helpers`（4096，1..65536）和 `extreme_racing_maximum_objects`（1024，1..4096），最多接受 128 項不同地圖策略。每次進入地圖時，初始化在開局倒數結束前向每名已載入參與者傳送一次相同的物件身分。部分傳送失敗或成員變化會取消該回合並關閉其連線。此功能建立初始物件清單；移動碰撞車輛、砲台排程及真實用戶端驗收尚未完成。

### 競速砲台中繼

`extreme_racing_cannon` 在 `dialect.rooms.config` 和 `ingame.config` 中均預設為 `false`，須與 `extreme_racing_initialization` 一起在兩處啟用，並要求版本具備 `extreme_racing_cannon` 能力（目前僅 kr-1256）。啟用後，房間會在每個已設定地圖的 `option.ini` 旁讀取 `Maps/<stage_directory>/cannon.ini`：宣告的砲台數必須等於該地圖的 `Cannon/PairCount`，或者為 0；每門砲台需要 `WEAPONCOUNT`、正數 `INTERVAL` 與該地圖擁有的 `STAGE`，每件武器需要含 `WEAPONID`（50000 至 50008）、`COUNT` 與有限正數 `SCALE` 的 `[CannonN_WeaponM]` 小節。其他任何不一致或格式錯誤都會使啟用失敗。宣告 0 門砲的地圖（例如初始化了 9 對的 rel_Racing01_Extreme）不安排任何砲擊：這些砲台保持閒置，不放行任何射擊。房間僅在中繼砲台射擊封包（中繼 32902）的行為者等於傳送者自己的座位、向量為有限數、砲台識別碼已在本局初始化、武器屬於該砲台的資源清單，且它恰好對應一條尚未回應的伺服器命令時才接受，然後將原始位元組向其他已初始化的參與者轉送一次，不回送給傳送者，不重試，也不改寫序號。每個砲台組最多掛起一條命令，帳本大小即地圖的砲台數。此選項本身不發送任何砲台命令：在下面的排程發出命令之前不會放行任何射擊。原生客戶端驗收尚未完成。

### 競速砲台排程

`extreme_racing_cannon_fire` 在 `ingame.config` 中預設為 `false`，並要求 `extreme_racing_cannon`。客戶端沒有任何計時器會開砲，因此不啟用此選項就不會有砲擊。啟用後，每個競速回合在傳送比賽時鐘（S2C 711）之後立即啟動砲台，命令（S2C 718）只傳給一個座位，即來源座位；其客戶端開砲並中繼射擊（中繼 32902），由房間路由轉送。來源座位的選擇、節奏與武器順序皆為伺服器策略而非原生行為，且尚未找到讀取 `INTERVAL` 的客戶端程式碼：

- 來源座位是已收到初始化且遊戲連線仍然開啟的最小座位號。命令從不傳給多個座位，因為每個接收者都會各自發射一枚子彈。
- 每門砲台在比賽時鐘啟動後經過一個 `INTERVAL` 到期，此後每次到期再經過一個間隔。砲台組仍有未回應命令時會被略過而不是排隊，時鐘停頓也不會補發錯過的射擊。不使用 `STAGE`。擁有多件武器的砲台依檔案順序循環使用。
- 命令攜帶群組 0（任意砲台群組）、該砲台組的第一個識別碼和縮放 1.0：中繼的射擊不含縮放，其他值只會改變來源座位的砲台。
- 競速回合結束、對局被取消或結束、房間停止或局代更換時，節奏停止，其未回應命令作廢；下一個競速回合會重新啟動。
- 來源座位離開或斷線時，其未回應命令作廢且不會重送；剩餘已初始化座位中座位號最小者僅為之後的命令成為新的來源座位。未收到初始化的座位不會被選中，也不會收到命令歷史。
- 傳送失敗會結束該局的排程：命令作廢、不重試，比賽繼續但沒有砲台流量。

原生客戶端驗收尚未完成。

### WeaponMaster 對局

`dialect.rooms.config.weapon_master` 預設為 `false`，要求版本具備 `weapon_master` 能力（目前僅 kr-1256；cn-774 與 tw-454 不繼承），並同時啟用 `modern_rooms`、在 `result_scoreboard_modes` 中包含模式 26。它會掛載為座位分配武器的裝備封包，以及客戶端在多次死亡後請求新武器的封包。

在同一協定的遊戲內資料列上同時設定 `ingame.config.weapon_master_levels`，缺少任一項都會使啟用失敗。該選項沒有預設值：空清單時模式 26 只是一般的計時對局。清單每一項依序代表一個等級，包含其 `experience` 門檻（第一級為 `0`，之後每級更高，且小於 65535）以及選用的 `weapons` 清單（`item` 為正的物品 ID，同級內不重複；`weight` 為正數）。沒有列出武器的等級從共用的 `weapon_master_weapons` 武器池抽取，因此與等級無關的武器只需寫一次。`weapon_master_experience` 以 `{ event, points }` 列出每種被接受的事件給予的經驗，事件取自 `kill`、`knockdown` 與 `rescue` 且互不重複，點數為正；未列出的事件不給經驗。模式 26 還必須透過 `native_life_every_mode` 或包含 26 的 `native_life_mode_ids` 使用原生生命判定。封包以武器目錄 ID 指定武器，由 `kr-1256 sub_4ADD70` 解析；主武器的商店物品 ID 為 200000 加上該 ID。`kr-1256` 設定的補丁使用十個等級、每級相差 200 經驗、每次擊殺 100 經驗，以及由舊版 L1、L2、L3、S1、S2、R3 主武器組成的單一隨機武器池。

每個座位在對局開始時獲得一把武器。被接受的事件累加其經驗；當經驗達到後續等級的門檻時，座位升到該等級並獲得該等級的一把武器，一次事件跨越多個門檻也只發一把。即時比分以種類 31 到 34 傳送每個座位的等級、經驗、目前等級的起始經驗和下一等級的經驗（`kr-1256 sub_9E9160` 將其存放在座位記錄 +204 到 +216）；在最後一級時，下一等級的值為其起始值加一。最先到達最後一級的座位獲勝，對局立即結束。當用戶端回報其玩家自上次換武器後已死亡規定次數時，伺服器發給它同一等級的新武器。伺服器從用戶端的 `Config/GameOption.xml` 讀取該次數 `WeaponMasterNoPangDeadCount`，因此必須匯入該檔案；數值缺少、重複或不為正都會使啟用失敗。過早的請求會被記錄並忽略。復活遵循通用規則。若時間結束時仍無人到達最後一級，等級最高的座位獲勝；所有座位等級相同則為平手。結果畫面的等級欄仍顯示帳號等級。原生用戶端驗收尚未完成。

### BnB 對局

`dialect.rooms.config` 和 `ingame.config` 的 `bnb` 均預設為 `false`。啟用已審核的 kr-1256 模式 41 交換時，兩處均須開啟，並要求 `modern_rooms`、`result_scoreboard_modes` 包含 41、`native_match_relay: true`，以及透過 `native_life_every_mode` 或包含 41 的 `native_life_mode_ids` 啟用原生命狀態接納。其他版本在完整交換審核前拒絕此選項；此路徑不接納機器人。套用 `profiles/kr-1256/bnb.patch.yaml` 可依已核實的場景邊界為 `rel_bnb01_village06` 啟用交換。

匯入 `Config/GameSpec/BnBSpec.yaml`，以及所選地圖的 `Maps/<stage_directory>/CustomMap.json` 和 `BnBItemLayout.json`。`ingame.config.bnb_stages` 最多包含 128 項不同地圖，每項必須指定 `stage_directory`、`tile_layer_minimum_x` 和 `tile_layer_maximum_z`。兩個座標是套用 JSON 偏移前的原生 TileLayer 邊界，必須明確提供有限值，並從所選地圖的場景資料取得。猜測原點可能導致格子定位錯誤，即使物品生成結果一致。

`bnb_maximum_file_bytes` 預設 1048576（1..16777216），`bnb_maximum_cells` 預設 1024（1..4096），`bnb_maximum_position_cell_distance` 預設 1（距離最近已接納移動位置的曼哈頓格數，0..4），`bnb_explosion_report_grace_ms` 預設 1000（0..10000）。引信、火焰、方塊銷毀時間及能力上限取自原生資源。載入前同步地圖種子，每個回合保留一份依序處理的放置、爆炸和拾取狀態。重複放置到已占格子不會傳送通知或推進序號；對已被領取物品的拾取仍傳送確認，使原生輸入狀態能夠釋放，同時避免重複獎勵。錯誤歸屬、舊生命與舊回合不能修改目前狀態。同步失敗或必要參與者連線遺失會取消對應回合。真實用戶端驗收仍待完成。

同一角色生命代次中緊隨泡泡報告的拾取仍可通過相同的位置與歸屬校驗；死亡或已移除的角色不能拾取。死亡清理完成後只回覆一次角色移除，不授予復活，後續重設確認也不能讓該角色復活。新回合會建立新的角色生命代次。

### FinalZone

`dialect.rooms.config.final_zone` 預設為 `false`。套用 `profiles/kr-1256/final-zone.patch.yaml` 可啟用模式 23、25、30、45 共用的 Area32 互動，需要 modern 對局繫結、`final_zone` 版本能力及記分板模式 30，目前僅審核 kr-1256。補丁同時保留記分板模式 1、5、6、19、20、24，可在其他模式補丁之後套用，但不會自行開啟這些模式。

透過 `client-data` 匯入對應用戶端的 `Config/GameSpec/GenItemSetting.yaml`、`Config/GenDroneSpec.csv`、`Config/SurvivalMode.xml`、`ServerShared/MapMeta.xml`，以及所選地圖的 `GenClusterTable.csv`、`GenSpawnTable.csv`、`GenSpotList.csv`。使用無人機或金色補給的地圖還需要 `GenDroneRewardTable.csv`。公寓地圖關閉了無人機，原始資源也沒有這張表。資源缺失或格式錯誤會在倒數前報錯。目前明確物品產生路徑支援一般生成物，包括 `rel_Sudden08_Finalzone` 地圖；使用獨立 `AutoItem` 地形物件類別的地圖（包括 `rel_space04_64room`）會被拒絕。可選開局包的原生消耗與發放互動尚未接入，因此裝備開局包也會在倒數前被拒絕。

伺服器在對局開始後發布地面物品，每個物件只授予一名領取者，依據已發放數量驗證主動丟棄與一次死亡背包，並使用原生使用者編號轉移背包內容。洪水階段遵循地圖資源時序。無人機使用受限的房間計時器，補給箱與房主去重後的金色補給申請採用原生獎勵種子。陷阱每個端點消耗一根已持有的桿，只允許放置者連接已確認的兩個端點；必要確認包含傳送者。玩家淘汰後不能復活，存活人數只更新一次，有競爭者的對局在只剩一人時進行一次結算，模式 25 則按最後存活的隊伍結算。模式 45 接入共用物資和淘汰流程，獨有的殭屍機制另行實作。一般分數與時限不會結束 FinalZone。取消、房間銷毀與最終結算都會停止模式計時器。

資源與物件限制位於 `ingame.config`：

| 設定鍵 | 預設值 | 限制 |
|---|---:|---|
| `final_zone_first_object_id` | 65536 | 首個預留世界編號，整個範圍必須低於玩家編號。 |
| `final_zone_maximum_objects` | 16384 | 每局不重用的編號總量，由物品、背包、無人機、補給與陷阱桿共用。 |
| `final_zone_maximum_quantity` | 100000 | 每個物品規格可發放或保留的最大數量。 |
| `final_zone_maximum_drones` | 1 | 同時活動的無人機數。 |
| `final_zone_maximum_golden_supplies` | 3 | 每局不同金色補給申請數，零表示不發放。 |
| `final_zone_maximum_file_bytes` | 4194304 | 每個匯入資源的位元組數。 |
| `final_zone_maximum_rows` | 16384 | 每張地圖表的列數。 |
| `final_zone_maximum_item_specs` | 4096 | 物品規格與開局包識別數量。 |
| `final_zone_maximum_alternatives` | 256 | 單個產生欄位中的候選數。 |
| `final_zone_maximum_bonus_items` | 64 | 單次產生選擇附帶的物件數。 |

此功能仍是受開關保護的增量。台帳限制已發放數量，但尚未完整重現原生背包欄位、武器變體與即時彈藥消耗。模式 30 的同盟與背刺行為尚未得到證實。自動檢查與資源匯入不代表原生用戶端驗證，也不代表地圖機制全部涵蓋。

### 奪星模式

`ingame.config.star_mode_ids` 和 `star_team_mode_ids` 指定個人和團隊奪星模式（原生用戶端為模式 5 和 6），需要啟用 `dialect.rooms.config.star_items`。奪星對局時長為 `star_round_time_limit_ms`（預設 240000），使用地圖本身的星星點：位置、種類和更新時間來自房主的地圖回報；房主沒有回報星星點時，伺服器改為讀取地圖 `track.wb` 中的 `#sp_star_` 和 `#sp_bigStar_` helper（`star_map_fallback`，預設 `true`）。每個點依自身延遲出現第一顆星，這顆星被拿走後才會補充。奪星對局依各座位持有的星數決定勝負，即時比分和結算以每隊的星數作為隊伍總分。現代版房間啟用 `star_items` 時，`result_scoreboard_modes` 還必須包含模式 5 和 6，因為結算表只對列出的模式傳送。`star_fever_lead_ms`（預設 0，關閉）在時間結束前這麼多毫秒啟動該版本的終局星星 Fever，目前只有 kr-1256 的 Fever 封包經過審核。套用 `profiles/kr-1256/stars.patch.yaml` 可在 kr-1256 啟用這兩個模式，並在最後 45 秒啟動 Fever。補丁重述完整的記分板模式清單，可在其他模式補丁之後套用。原生用戶端驗證仍待完成。

## 禁選（Ban-pick）

禁選是原生用戶端沒有的伺服器功能。它預設安裝並啟用，但在有人設定規則之前不禁用任何東西。`core` 中的 `banpick` 列保存全伺服器文件，每個版本的 `banpick.room` 列（該版本 `ingame` 列的子列）把它套用到該版本的房間。停用這兩列即可移除此功能。

禁用分兩種：

- **房間禁用**由房主設定，只在房間存在期間有效。`!banpick`（別名 `!bp`）支援 `add <物品編號、名稱或分組>`、`remove <...>`、`set <編號或分組...>`（一次替換整個清單，浮層即用此方式）、`clear` 和 `list [頁碼]`。所有人都能查看清單；只有房主能修改，且只能在遊戲開始前修改。
- **全伺服器策略**由管理員用 `gbanpick`（別名 `gbp`，權限 `game.management.banpick`）設定，保存到 `state_path`（預設 `data/banpick-policy.json`），每次保存都要寫明所修改的版本號。`state_path` 為空時，設定裡的 `policy` 唯讀。

文件結構與用戶端策略相同。`policy.groups` 依 `ShopItem2` 的 `MainCategory`/`SubCategory` 表編號（各版本共用）或 `function` 欄定義物品分組；內建的分組包括 `weapon`、`sniper`、`rifle`、`shotgun`、`clothes`、`hat`、`character` 和 `pet`。`policy.policies` 可以列出任意多個策略，每個策略有作用範圍（`builds`、`modes`、`rooms: all|public|password`）和帶類型的規則：

| 規則類型 | 效果 |
|---|---|
| `block` | 在策略生效的地方拒絕裝備該物品，穿著它的對局無法開始。 |
| `confirm` | 與房間禁用同等強度：提醒房主，房主仍可開始。 |
| `warn` | 允許使用，但會告訴房間裡的人誰在用；新進房間的人會收到所有正在使用者的提醒。 |

房主點擊開始時，如果有人穿著房間禁用或 `confirm` 物品，所有成員都會收到誰穿著什麼的提醒。裝有 Nirinsou Link 浮層的房主會看到確認對話框；其他房主在 `reclick_window_ms`（預設 10000）內再次點擊開始或輸入 `!bp start` 即可開始。超過 `announcement_items`（預設 25）項的清單只公告數量。浮層接收 `game.ban.list.changed`、`game.ban.start.confirm`、`game.ban.warn`，並用 `game.ban.list.get`、`game.ban.catalog.search` 請求讀取清單。浮層做出的每個修改（包括對話框裡的確定）都是透過啟動綁定的命令通道（`game.command.execute`）執行的 `bp` 命令，因此與手動輸入的命令接受同樣的檢查；這需要啟用 `companion.commands` 列（`core` 中預設停用）。訊息列不發送伺服器公告的版本會以系統聊天訊息收到同樣的文字。原生用戶端驗收尚未完成。

## 原生廣場招募

支援的十三個設定已啟用原生廣場招募。自訂設定需在 `dialect.rooms` 與 `ingame` 兩列設定 `plaza_recruitment: true`，並掛載對應的 `party` 列。兩個選項預設均為 `false`；缺少原生繫結、隊伍目錄或廣場支援時，啟用會失敗。`ingame.plaza_recruitment_text_length` 預設限制為 128 個 UTF-16 字元，可設定 1–1024。發布與修改共用聊天限流視窗，取消招募不受該視窗阻止。

只有目前廣場隊長能招募。原生木牌標誌、文字氣泡、隊伍成員與場景跟隨共同更新；取消、離開、切換場景與失去隊長身分會清除舊木牌。KR1256 與 CN2019 還提供每頁十筆的原生招募清單，使用隊長選定的分類與人數上限。清單只允許加入本節點隊長的隊伍；顯示遠端玩家的廣場外觀不代表可以跨節點入隊。入場位置由原生客戶端決定，伺服器只重播真實的位置回報。

## 可選的裝備防禦表

裝備普通防禦預設關閉。在 `kr-812`、`cn-324`、`cn-406`、`cn-404`、`cn-408`、`cn-705`、`cn-706`、`id-400`、`id-1809`、`cn-717`、`cn-774`、`tw-454` 或 `kr-1256` 加上 `--patch profiles/bundles/defence-seeds.patch.yaml` 即可啟用。補丁也會啟用傳送防禦表所需的原生 TCP relay，包括 `cn-705`、`cn-706` 預設停用的相關外掛列，無須開啟 UDP。使用 `all` 時，將補丁操作放入各指定版本的 `local.patch.yaml`；共用補丁無法修改稍後附加的版本列。`cn-705` 可作為獨立設定使用，但不在 `all` 中。缺少已核實能力或授權列的版本會拒絕此補丁。

CN2011（`cn-279`）在原生傷害流程中於本機計算普通裝備防禦，不使用此種子表補丁。

授權外掛讀取對應版本的 `Config/ShopItem2.xml` 與 `defence_seed` 規則。回合開始時凍結穿戴物品 ID，向每個接收端傳送每名受擊者相同的 1000 位元組表，減傷仍由原生客戶端套用。暴擊與抵消表保持獨立；啟用 `cn-705` 或 `cn-706` 的防禦不會啟用尚未核實的暴擊交換。關閉時保留原本的零防禦交換，僅防禦外掛列保持停用。

表內頻數均衡，近似本地整數隨機取值分布，不重現客戶端隨機序列。目前投影涵蓋目錄中的 min/max、設定的 RC/KR/BB 套裝效果及端點精度；`cn-774` 與 `tw-454` 均採用各自的 RC/KR/BB 規則和單精度端點。暫不投影實例屬性、變身專屬屬性及獨立特殊修正。CN2019 的 AI 角色採用不同的種子編碼，不在這套玩家投影範圍內。正式啟用前需要原生客戶端驗收。`defence_items_client_path` 選擇活頁簿；`defence_workbook_max_bytes` 限制大小，預設 268435456 位元組。

## 新版殭屍模式

對 `kr-1256`、`cn-774` 和 `tw-454`，套用 `profiles/<build>/script-zombies.patch.yaml`，同時在 `dialect.rooms` 與 `ingame` 列啟用 `script_zombies`，接入一般模式 8。兩項預設關閉，等待原生用戶端驗收。原生房主指令碼依裝備權重選出多個小殭屍，伺服器將初始數量限制為在線人數除以 4 後無條件進位，再由角色所屬用戶端的原生確認完成隊伍 34 變身。人類開局為隊伍 1。之後的感染與殭屍復活須有角色擁有者的死亡或復活依據；一般死亡回報不會結束回合。全部感染確認完成後殭屍獲勝，到伺服器時限仍有人類存活則人類獲勝。

`ingame.script_zombie_round_time_limit_ms` 預設 `180000`，範圍為 `1..3600000`。模式 8 必須啟用原生生命與復活，`dialect.rooms.result_scoreboard_modes` 必須包含 `8`。兩列的 `script_zombies` 必須一致。此選用修補可與已審核的炸彈、隊長、跑酷及 FinalZone 修補疊加，保留各自計分板。其他模式維持版本宣告的 peer feed 策略。這三個版本均關閉通用轉送，因此共用變身操作不會在已啟用模式之外被轉送。原生指令碼房主離開時會取消回合且不結算，因為選擇狀態尚不能移交另一用戶端。

本次接入為單回合，指令碼的十回合要求尚未組成伺服器連續比賽。幽靈模式 28、原版模式 43、殭屍生存模式 44 各有不同原生規則，不由此選項啟用。原生用戶端實機驗收仍待完成。

## 幽靈殭屍

為 `kr-1256` 或 `cn-774` 套用 `profiles/<build>/ghost-zombies.patch.yaml`，即可在房間適配器與對局列同時啟用 `ghost_zombies`。模式 28 與普通模式 8、逃生模式 32 共用轉換綁定。原生房主選擇一名殭屍王（201 或 203），使用武器 110290；指令碼隨後重複的零武器選擇不會再次授予轉換。已確認殭屍王的死亡清理使存活人類獲勝；殭屍王或房主離開則取消對局，不發獎勵。其他人類經自身死亡／重生流程變為小殭屍。

普通與幽靈殭屍還會同步目前角色擁有者發出的原生 ULTRA 啟動與計時結束恢復，保留共用 HP 和生命代次。充能仍由原生計量槽負責。目前未實作指令碼的十回合系列。TW454 沒有完整幽靈指令碼路徑的核實證據，因此拒絕啟用。修補保留其他已啟用計分板，原生用戶端實機驗收仍待完成。

## 殭屍逃生

`dialect.rooms.config.zombie_escape` / `ingame.config.zombie_escape` 預設為 `false`。kr-1256、cn-774 或 tw-454 的單一版本設定可套用 `profiles/bundles/zombie-escape.patch.yaml`；使用 `all` 時，將操作放入各選定版本被忽略的 `local.patch.yaml`。啟用需要現代對局繫結、該版本已審核的 `zombie_escape` 能力及計分板模式 32。修補檔保留計分板模式 1、8、19、20、23、24、25、30、32、45，但不會自行開啟各模式的獨立選項。請使用對應客戶端的殭屍逃生地圖與腳本，原生客戶端驗收尚未完成。

房間方言與對局外掛必須啟用相同的殭屍模式。普通殭屍與殭屍逃離共用一個型別化變身繫結，同時啟用不會重複註冊原生操作。選用修補保留已支援的所有生存模式結算表；返回等候房間時恢復開局前選擇的隊伍。

客戶端房主腳本負責初始殭屍抽籤、分離的殭屍出生點、準備門、檢查點按鈕、定時門、感染量表及撤離條件。伺服器驗證房主有上限的初始選擇，允許每名玩家自身的後續感染或重生變身，向全體參與者回傳獲准的 peer 變身，並使用共用生命、結果及獎勵結算流程。初始名額為目前線上玩家人數除以五後無條件進位。腳本可透過感染量表直接感染仍存活的玩家；按鈕掃描並未證實僅限人類觸發。

房主透過原生關卡結果回報人類撤離。沒有線上人類剩餘，或伺服器時限屆滿時，殭屍獲勝。`ingame.config.zombie_escape_round_time_limit_ms` 預設為 600000，範圍是 1..3600000。一般死亡關卡回報不會結束此模式。對局中拒絕新玩家加入。Lua 房主離開會取消本局且不發獎勵，因為無法重播其私有計時與檢查點狀態；其他斷線遵循已設定的取消策略。

此選項透過共用結果流程結算一個關卡。原生腳本寫入遊戲次數三，但三關系列語義尚未證實，本次未實作。自動測試不代表原生地圖機制與介面行為已全部驗收。

## 依客戶端版本儲存穿戴方案

`inventory.loadouts` 列讓各版本獨立儲存已穿戴角色、服裝、武器、武器預設頁及目前預設欄位。`build_key` 使用穩定的設定身分，例如 `kr-1256` 或 `cn-279`；執行相同版本的節點使用相同鍵。帳號的道具所有權、數量、期限、錢包及進度繼續共用。客戶端庫存畫面只顯示自身 `ShopItem2.xml` 中存在的物品，角色也會核對 `FighterConfig.xml`。不相容物品仍歸帳號所有，切回支援它的版本後即可再次看到。

首次使用時，系統從舊紀錄繼承相容的選擇，並透過既有穿戴規則補齊必要裝備位置。後續變更只影響目前版本。預設初始化不會覆寫已儲存的預設頁。此列位於 `clientdata` 下，隔離 `IInventoryRepository` 與 `IPlayerInventoryState`，使用共用儲存提供的 `IInventoryLoadoutStore`。自訂設定應將大廳、裝備畫面、裝備、商店、房間協定與遊戲邏輯的使用者放在此子樹中。

`inventoryview` 列的 `persist_preset_pages` 開關把武器預設頁作為伺服器擁有的狀態儲存。legacy 設定包與 kr-1256 設定包已開啟；cn-774 與 tw-454 的頁記錄結構不同，維持關閉。客戶端沒有儲存預設頁的封包，因此由伺服器記錄：每次裝備變更提交時，將已穿戴的主武器與特殊武器寫入當下啟用的頁，其他頁保留各自的武器，讓每個分頁擁有獨立的配置。卸下主武器不會清空該頁的武器，登入重播也不會寫入。切換到另一頁時，若本版本自己的背包持有該頁武器，會透過一般的裝備提交穿上它；無法穿戴的頁武器仍保留在該頁，已穿戴組合維持不變。預設頁屬於該版本的 `inventory.loadouts` 範圍。新版本中同時包含水晶或寵物的預設頁尚未涵蓋。

Memory、MySQL 與 SQLite 提供者都有 `max_loadouts_per_player`，預設為 `32`，範圍 `1..256`。達上限後拒絕新增版本方案，不移除既有方案。MySQL 移轉 `0038` 新增 `player_loadouts`；SQLite 結構 `8` 增加方案快照並保留舊帳號狀態。升級前請備份持久儲存。回退 MySQL 程式時保留新增表；舊 SQLite 讀取器會拒絕結構 `8`，回退需要相容讀取器。請勿用舊存檔覆蓋玩家的新進度。

## 選用物品強化

`kr-1256` 設定已包含 `profiles/bundles/item-enhancement.patch.yaml`。`BFF_ITEM_ENHANCEMENT_ENABLED` 預設為 `true`，控制一般物品的新 Article 分配、強化變更及實驗室操作與設定繫結。`item-enhancement.articles` 中繼資料列保持啟用；其經驗證的 `initialize_articles` 欄位預設為 `false`，隨同一選項變化。關閉選項後，既有持久化身分仍可解析、穿戴和卸下，並繼續顯示已儲存的強化與改造屬性；尚無 Article 身分的一般背包物品不會因此取得新身分。啟用強化時必須明確填寫下列五項配方數值，缺漏或超出範圍都會導致啟用失敗。

設定以 `-1` 表示尚未填寫的配方值。強化列將其視為未設定，只有五項數值都符合下列範圍才允許啟用；`-1` 不會作為成功率或材料數量使用。

強化僅接受主選單中一件符合條件的永久、未穿戴武器或服裝 Article，已強化的物品不能再次強化。此修補檔的強化與武器改造（Modification）准入已針對 `kr-1256` 審查；這不代表其他客戶端版本已受支援，也不會啟用其他實驗室操作。

| 環境變數 | 強化列欄位 | 必填整數值 |
| --- | --- | --- |
| `BFF_ENHANCEMENT_SUCCESS_BASIS_POINTS` | `success_basis_points` | `0..10000`；`5000` 表示 50% 成功率。 |
| `BFF_ENHANCEMENT_ADDITIVE_SUCCESS_BASIS_POINTS` | `additive_success_basis_points` | `0..10000`；玩家選擇添加劑時的成功率（`10000` 表示必定成功）。 |
| `BFF_ENHANCEMENT_CARD_QUANTITY` | `card_quantity` | 每次獲准嘗試消耗卡片 `190111` 的數量，範圍 `1..1000000`。 |
| `BFF_ENHANCEMENT_KIT_QUANTITY` | `kit_quantity` | 每次獲准嘗試消耗四種工具 `190106`、`190107`、`190108`、`190109` 中每一種的數量，範圍 `2..1000000`。 |
| `BFF_ENHANCEMENT_ADDITIVE_QUANTITY` | `additive_quantity` | 玩家選擇添加劑時消耗添加劑 `101745` 的數量，範圍 `1..1000000`。 |

這些數值定義伺服器的配方策略。`.env.example` 中註解的數字僅為範例，不是預設經濟數值，也不代表歷史價格。每次獲准嘗試都會扣除設定數量的卡片及每一種工具，強化失敗也會扣除。選擇添加劑還會扣除其設定數量，並以 `additive_success_basis_points` 取代 `success_basis_points`。材料不足或材料身分有歧義時，在提交任何變更前拒絕請求。

武器改造由 `BFF_ITEM_MODIFICATION_ENABLED` 單獨控制，預設為 `true`。啟用時需要同時設定 `BFF_ITEM_ENHANCEMENT_ENABLED=true`、填齊上述五項強化數值，並在部署修補檔的 `item-enhancement` 列提供完整的巢狀 `modification` 策略。開關本身不提供材料數量、價格、效果幅度或機率策略。

| 改造欄位 | 必填策略 |
| --- | --- |
| `part_quantity` | 每個所選零件的消耗數量，範圍 `1..715827882`；固定選擇三個零件，重複選擇時的總扣除量必須適合 Int32。 |
| `apply_lucci_cost`、`delete_lucci_cost` | 添加或刪除一組改造的明確 Lucci 費用，各為 `0..2147483647`；免費也必須明確填 `0`。 |
| `parts` | 一至 24 項，`item_id` 與 `(numeric_tag, effect_category)` 組合均不得重複；每件物品必須是原生按數量計算的改造零件。 |
| `parts[].numeric_tag`、`parts[].effect_category` | 原生分類分別為 `0..2` 與 `1..8`；已設定的標籤與效果分類的每種組合都必須有零件項目。 |
| `parts[].attribute_ids` | 恰好七個互不相同的已審查原生 ItemAttr 編號，範圍 `101..156`。同一數字標籤內，每個編號只能屬於一個設定項；不同標籤可以重複使用編號。 |
| `parts[].value0` | 正向有符號效果單位，範圍 `1..2147483647`；`160` 顯示為 `1.60%`。 |
| `parts[].negative_values` | 恰好七項 `{ attribute_id, value }`，與上述編號一一對應；值為 `-2147483648..0`，`-40` 顯示為 `-0.40%`。 |
| `tag_weights`、`effect_weights` | 每項包含 `key` 與三個 `weights`，依序用於已安裝零、一、兩組改造的狀態；鍵必須符合零件分類。每個權重為 `1..715827882`，確保重複選擇三個零件後的權重總和適合 Int32。 |

改造接受主選單中符合條件的永久、未穿戴武器 Article。玩家必須選擇三個已擁有的零件，重複選擇會合併扣除數量。演算法分別累加所選零件的標籤與效果分類權重，再從所得分類組合的七個屬性編號中等機率選擇一個。同一策略用於原生實驗室預覽。預覽幅度採用浮點百分比，已安裝的 Article 屬性保留有符號整數單位、組號與數字標籤。這些值屬於明確設定的部署策略。

每件武器最多儲存三組改造，組號為 `0..2`，與強化狀態獨立。添加時使用第一個空組，刪除時必須指定已安裝組。每次成功添加或刪除都會推進該武器在目前目錄範圍內的版本號，刪除最後一組後仍保留空狀態與版本號。完全相同的過期持久化計畫不能再次扣款；之後重新解析的新添加請求可以正常安裝另一組改造。

材料扣除與成功強化在同一交易中提交。改造將 Lucci 費用、全部所選材料、組變更及所需 Article/寵物中繼資料一併提交，並在操作結果之前傳送已提交餘額與完整背包，讓原生處理器能找到變更後的 Article。關閉消耗開關後，既有屬性繼續顯示。原生客戶端驗收與真實 MySQL 環境驗證仍待完成。

永久轉換（實驗室的 ChangeUnlimited 分頁，僅 `kr-1256`）另外預設關閉。在部署修補檔中為已啟用的 `item-enhancement` 行加入巢狀的 `conversion` 策略即可啟用：

| 轉換欄位 | 必要策略 |
| --- | --- |
| `enabled` | `true` 顯示該分頁並接受請求；預設的 `false` 使其隱藏並拒絕請求。 |
| `card_item_id` | 每次轉換消耗的計數卡片，例如原生的 `190151`。它必須是該版本目錄中的計數材料，且不能是轉換目標。 |
| `minimum_days` | 目標至少還需剩餘的整天數，`1..10000`。用戶端在送出請求前會檢查同一個值。 |
| `set_card_quantity` | 選填。一次原生套裝轉換消耗的卡片數，至少為 1。留空則套裝仍被拒絕；用戶端只為該分頁指定一張卡片，沒有依套裝的數量，因此由部署方提供。 |

轉換在主選單中接受一個以物品 ID 選擇的單件限時物品。該物品必須帶有用戶端自身的轉換標記，是武器或服裝，尚無裝備身分，未穿戴也不帶其他排除標記，並且剩餘時間不少於 `minimum_days`（含用戶端的半秒容差）。伺服器消耗一張卡片，並把同一行變為永久，保留其格位、標記與已裝備副本；即使強化和改造都不涵蓋該物品，它也會獲得裝備身分。伺服器在儲存層持有帳號時再次判定計時，因此等待期間到期的計時、過期的請求或物品變為永久後的重複請求都不會消耗任何東西。完整背包會先於結果發布。設定 `set_card_quantity` 後，指定原生套裝的請求會在一個交易中轉換套裝列所列出的每個成員（前 `setCnt` 個編號）：每個成員都必須通過與單件物品相同的檢查，卡片只消耗一次，任何一個成員不合格都會讓整套保持不變。回覆包含套裝編號與第一個成員的 Article 鍵。kr-1256 用戶端只有在每個成員的部位位置都已設定時才會送出套裝請求，而其活頁簿對所有僅由可轉換物品組成的套裝都把它留空，因此原生用戶端在該資料下無法送出此類請求。原生用戶端驗收仍待完成。

實驗室升級（MakeItemAdvanced 分頁，僅 `kr-1256`）另外預設關閉。在部署修補檔中為已啟用的 `item-enhancement` 行加入巢狀的 `advancement` 策略即可啟用：`enabled`、`tab_card_item_id`（用戶端為該分頁列出的卡片；它必須是某條規則的卡片，用戶端還會拿所選卡片與它比較，以決定一般 HC 套裝的確認文字）以及 `rules`，每張升級卡一條，包含 `card_item_id`（目錄中的計數材料，例如原生的 `190247`）、`source_tier`（用戶端的階數：`3` 為 HC 升級為進階 HC，`4` 為進階 HC 升級為進階 L4HC，`5` 為 L4HC 升級為進階 L4HC）、`card_quantity`（一次升級消耗的卡片數）和選填的 `set_ids`（把該卡片限定在指定的原生套裝）。用戶端沒有給出卡片、價格或機率，因此不作任何假設，且各卡片必須互不相同。伺服器在實驗室設定中依用戶端自身規則引擎讀取的形式發布這些規則，並用同樣的規則判斷每個請求：請求指明一個原生服裝套裝以及完整套裝中每個成員的 Article（不完整的套裝會被拒絕），所有成員必須是永久且未穿戴的，卡片必須符合套裝目前升級狀態所允許的規則，並且卡片在同一交易中扣除。結果是原生升級屬性（HC 升級為進階 HC 為 ItemAttr `100`，進階 HC 升級為進階 L4HC 為 `157`，L4HC 升級為進階 L4HC 為 `158`），存放在套裝列順序中的第一個成員上，帶有活頁簿中固定的效果數值；之後的升級會取代它，強化嘗試則保留它。它存放在 Article 既有的強化中繼資料中，無需遷移。升級是確定性的，因為用戶端沒有該分頁的失敗顯示；被拒絕的請求回覆關閉結果且不扣除任何東西。完整庫存先於回覆發布，回覆指明套裝和攜帶屬性的成員。用戶端會合併所有成員的效果，並未說明由哪個成員攜帶該屬性；選擇第一個成員屬於伺服器策略。原生用戶端驗收仍在等待中。

## 本命武器

`BFF_FAVORITE_WEAPON_ENABLED` 預設為 `true`。啟用後，`item-enhancement.articles` 與 `item-enhancement` 兩列都會讀取客戶端 `Config/FavoriteWeaponSystem.xml`（`favorite.workbook_path`）的 `Base` 與 `Upgrade` 工作表。武器強化成功時，會在與版本號和材料扣除相同的交易中為該武器增加活頁簿的 `IncreasePointEnchant` 點數；失敗的嘗試不增加點數。點數依武器與目錄範圍儲存為非負整數；等級（Normal、Expert、Special、Master）一律由 `Upgrade` 門檻從點數推導，不會儲存。之後武器的物品呈現帶有攜帶等級與點數的 ItemAttr 164。沒有進度的武器沒有該項目，外部背包寫入不能建立或修改點數。MySQL 需要遷移 `0047`；SQLite 維持 schema `13`。改造成功安裝一個屬性時，也會在與所裝屬性組相同的交易中，按其等級（S、A 或 B，即所裝屬性的第一類別 0、1 或 2）為該武器增加活頁簿的 `IncreasePointModification` 點數；刪除改造不增加點數。當強化或改造的點數使武器越過等級門檻時，玩家會在背包刷新後收到客戶端的等級聊天提示（S2C 1548，旗標為零）；`dialect.messages` 的 `favorite_weapon_grade_notices` 選項跟隨同一變數。對局點數、衰減、升級獎勵與降級提示尚未實作。原生客戶端驗收仍待完成。

## 選用稱號與勳章

`kr-812` 和 `kr-1256` 設定已包含 `profiles/bundles/titles.patch.yaml`。`BFF_TITLES_ENABLED` 預設為 `true`；設為 `true` 會同時啟用稱號列及其訊息繫結。外掛依版本宣告的稱號目錄格式載入 `Config/BF_title.xml` 和 `Config/ShopItem2.xml`。

此功能支援查詢勳章、用勳章兌換稱號，以及穿戴或卸下已擁有且符合條件的稱號。稱號所有權來自現有背包，稱號選擇遵循目前版本的穿戴方案。兌換時，勳章扣除與稱號發放在同一交易中提交；背包同步保留一般 Article 與寵物中繼資料。SQLite 和 MySQL 持久儲存勳章餘額，Memory 提供者支援相同操作供本機使用。

對局自動勳章預設單獨關閉。在 `titles` 列設定 `round_medals_enabled: true`，並提供非空的 `round_medal_rules`。每條規則指定原生 `medal_id`、精確的 `mode_ids`、`metric`、包含邊界的 `minimum` 與選用的 `maximum`、發放 `count`（預設 `1`），以及 `minimum_participants`（預設 `2`，只計算真人參與者）。可用指標為 `Completed`、`Wins`、`Kills`、`Knockdowns`、`Rescues`、`Score`、`Deaths` 和 `Stars`。每個勳章只能出現在一條規則中。`max_round_medal_rules`（預設 `256`）和 `max_modes_per_round_medal_rule`（預設 `128`）限制保留的策略規模。門檻與允許模式必須作為部署策略明確設定；單獨啟用稱號不會自動選定歷史發獎規則。

對局策略使用伺服器接受的最終統計。勳章、對局進度、錢包獎勵和對局回執由宣告支援原子對局勳章結算的提供者在同一交易中提交。Memory、MySQL 和 SQLite 均實作此能力。完全相同的對局重播不會重複發放勳章。發獎不會開啟稱號視窗；下次查詢勳章時傳回目前餘額。例如，以下修補檔為有效擊倒數為五至九次、至少兩名真人參與的道具模式對局發放一枚原生勳章。這些門檻僅為策略範例：

```yaml
- set:
    id: titles
    config:
      round_medals_enabled: true
      round_medal_rules:
        - medal_id: 30007
          mode_ids: [3]
          metric: Knockdowns
          minimum: 5
          maximum: 9
          count: 1
          minimum_participants: 2
```

限時取得需要明確設定伺服器已安裝的 `acquisition_time_zone`，例如 `Asia/Shanghai`。空值會維持限時取得不可用。原生取得區間按月、日、時、分比較，包含起訖邊界且忽略年份（`kr-812 sub_4FE3D0`、`kr-1256 sub_EAE090`）；這是依當地時間每年重複的區間，不會自動處理跨年環繞。此限制不影響穿戴已擁有的稱號。

原生 stock 與 stock-set 附加獎勵透過同一商城活頁簿解析，與稱號及其勳章消耗一併提交。Memory 和 MySQL 保留獨立回執，刪除或過期的稱號不會再次獲得附加獎勵；MySQL 需要遷移 `0046`。SQLite schema `13` 不具備附加獎勵回執能力，因此只拒絕帶附加獎勵的稱號取得，普通兌換、勳章查詢和對局勳章仍可使用。目前已審核的 stock 獎勵使用永久或按天發放；包含 Lucci 獎勵的 stock-set 不可用。原生勳章附加獎勵引用沒有數量欄位：`bonus_medal_quantity` 用於明確指定正整數發放量，不設定時此類取得維持不可用。

情侶及挑戰稱號取得仍不可用。單純存在情侶關係不能證明鑽石戒指條件；Luxury、Gallery 和 Honor 條件需要各自的權威玩法狀態及自動發放路徑。Favorite 稱號是 kr-1256 上的例外：設定 `BFF_FAVORITE_TITLES_ENABLED=true`（需同時啟用 `BFF_TITLES_ENABLED` 與 `BFF_FAVORITE_WEAPON_ENABLED`；預設開啟，僅 kr-1256）後，當帳號有 1、2 或 3 把武器 Article 達到 Favorite 大師等級時，稱號 400312、400313、400314 會被永久發放，不消耗勳章，並在登入時與每次 Favorite 點數發放後檢查。發放會發布背包，隨後傳送稱號視窗的進度（S2C 933，條件 6，目前值不超過要求值）。檢查時若帳號正忙於其他寫入，本次檢查會略過，稱號在下次登入或下次 Favorite 點數發放時發放。以勳章換取這些稱號仍被拒絕。原生客戶端驗收和真實 MySQL 環境驗證仍待完成。

## 選用自訂房間規則

`kr-1256` 設定已包含 `profiles/bundles/custom-room-rules.patch.yaml`。`BFF_CUSTOM_ROOM_RULES_ENABLED` 預設為 `true`；設為 `true` 會同時啟用 `dialect.rooms` 與 `ingame` 的 `custom_room_rules`，使用客戶端資料中的 `Config/CustomGameMode.xml`。此修補檔不會為其他版本啟用該功能。

只有等候中房間的目前房主可以修改選項。原生目錄提供模式 `1`、`3`、`8`、`11`、`13`、`28`、`30`、`32`、`37`、`38` 和 `39` 允許的控制項及數值。這些是既有模式的選項，各模式的獨立玩法仍遵循各自的啟用條件。完整房間設定會先於修改回饋同步。新成員收到目前設定，移交房主時保留設定，切換模式或重設時恢復原生模式預設值。自訂規則隨房間存續，不是伺服器重新啟動後仍由資料庫保留的設定。

`ingame` 列透過以下欄位限制活頁簿解析資源：`custom_room_rules_max_bytes`（預設 `8388608`）、`custom_room_rules_max_rows`（所選工作表合計 `4096` 列，包含稀疏列填補）、`custom_room_rules_max_columns`（`64`）和 `custom_room_rules_max_cell_characters`（`8192`）。缺少客戶端資料、繫結不受支援或設定無效時啟用失敗。原生客戶端驗收仍待完成。


## 選用舊版科學室

`kr-812` 設定已包含 `profiles/bundles/science-lab.patch.yaml`。`BFF_SCIENCE_LAB_ENABLED` 預設為 `true`，同時控制 `science-lab` 列與 `dialect.messages.config.legacy_science_lab`。該列需要共用背包繫結、與客戶端目錄一致的背包範圍 `build_key`，以及原生 `Config/ShopItem2.xml` 和 `Config/SynthesisSystem.ini`。目前只接受已稽核的 `kr-812`；其他版本必須先核對自己的請求讀取器、結果消費路徑和材料資格，再修改准入資料。該列處理合成與拆解，既有 item-enhancement 列仍獨占其科學室設定請求處理器。

合成消耗三個互不重複、符合條件、未穿戴且未過期的期限物品，或三套完整原生套裝，再傳回依設定選擇的保留物品。原生套裝使用 `stockSetId` 和依序排列的 `setCnt` 個成員，編號可能不同於 `setId`；拒絕混合套裝與一般物品的請求。同一套裝的所有成員必須具有相同有效期限。拆解消耗一個符合條件的永久物品或一套完整永久套裝。兩種操作都扣除設定的計數卡片，選中的添加劑另依明確設定扣除。請求必須來自主選單；缺少、過時、有歧義、已穿戴、已過期或不支援的所有權會使整項操作拒絕提交。

下列 `profiles/kr-812/local.patch.yaml` 範例僅定義合成策略。數字屬於部署範例，並非還原的原版經濟規則；安裝資源中 `SYNTHESISINFO` 的數值不能證明伺服器配方價格或機率。啟用前應確定實際採用的策略。

```yaml
- set:
    id: science-lab
    config:
      enabled: true
      synthesis_card_item_id: 100622
      card_quantity: 1
      synthesis_survivor: first
      synthesis_maximum_days: 729
      synthesis_permanent_at_days: 730
- set:
    id: dialect.messages
    config:
      legacy_science_lab: true
```

`card_quantity` 是各設定配方共用的正整數卡片扣除量。`synthesis_survivor` 必須為 `first` 或 `uniform`。合成累加精確的剩餘天數小數，以低於原生 730 天永久顯示門檻的 `synthesis_maximum_days` 設上限，並在總和達到更高的 `synthesis_permanent_at_days` 時轉為永久。輸入包含永久物品時也產生永久所有權。沒有透過隱含經濟預設值啟用的合成卡或拆解配方。

選用 `decomposition` 項目包含 `card_item_id`（此原生目錄為 `100623` 或 `100656`）、`category`（`0` 飾品、`1` 服裝/套裝、`2` 武器）及非空的 `rewards` 陣列。每項獎勵包含原生 `stock_id`、唯一 `item_id`、`kind`（`Count` 或 `Days`）、正整數 `amount`、`maximum` 和正整數 `weight`。`Stock` 列必須指向同一物品，且 `duration` 必須精確等於本次發放數量或天數。客戶端顯示該庫存商品規格，不顯示累加後的背包所有權。未上架的 Stock 列仍可表示獎勵。拒絕小數天數的 Stock 獎勵，合成主物品有效期限仍保留小數。永久拆解和添加劑獎勵尚不可用：原生結果格式化器不會將 `Stock.duration: -1` 視為永久，此目錄也沒有已經稽核、可顯示永久所有權的 Stock 規格。合成主物品的永久化使用另一條已支援路徑。設定必須符合原生目錄，總權重必須位於帶正負號 32 位元抽樣範圍內。每次拆解選擇一項獎勵。

`permanent_additive_item_id`（`100624`）需要 `permanent_additive_quantity` 和 `permanent_additive_basis_points`（`0..10000`）；機率低於 10000 時還需要採用上述獎勵格式的 `permanent_failure_reward`。它僅適用於尚未轉為永久的合成。`bonus_additive_item_id`（`100655`）需要 `bonus_additive_quantity` 與 `bonus_reward`。添加劑編號和操作/效果會與原生 `ASSISTINFO` 核對；編號為零表示停用。被接受配方中的獎勵編號不能衝突。

資源預算都是經驗證的部署欄位：`max_workbook_bytes` 預設每份原生資源 `134217728` 位元組，`max_catalog_rows` 為 `65536`，`max_catalog_columns` 為 `256`，`max_cell_characters` 為 `16384`，`max_in_flight_accounts` 為 `128`，`max_decomposition_recipes` 為 `64`，`max_rewards_per_recipe` 為 `128`。這些值必須為正整數，可依部署需要提高。欄數上限約束非空值；原生表格尾端只有樣式的空儲存格不會配置稀疏填補空間，但其索引仍須遞增。背包容量及原生選擇/訊框長度限制仍獨立執行。

Memory、SQLite 和 MySQL 使用同一消耗與發放交易，保留無關所有權、保留物品的中繼資料、一致的背包鏡像和欄位分配水位。一般 Article 與寵物準備在新獎勵存在後執行，並隨交易一起提交。完整背包先於結果發布。小數有效期限以絕對時間戳記保存；合成主結果天數採用客戶端同一次完整背包同步的工作階段基準，而不是傳送結果時重新計算的倒數。拆解和附加獎勵結果攜帶設定的 Stock 編號，數量和期限由客戶端 `Stock` 表讀取。提交後的傳輸失敗不會撤銷所有權，也不會在背包未發布時宣告成功。

原生開頭字串是二級密碼（`kr-812 sub_9E29F0` 和 `kr-812 sub_4F8A20`）。目前沒有經驗證的密碼檢查服務，因此拒絕非空密碼，也拒絕非零診斷請求路徑。獨立武器進化尚不可用：`gspEvolveResult` 屬於寵物，尚未建立獨立武器請求/結果流程。原生玩法與介面驗收、真實 MySQL 科學室驗證仍待完成；自動協定和持久化檢查不能證明這兩項驗收。
