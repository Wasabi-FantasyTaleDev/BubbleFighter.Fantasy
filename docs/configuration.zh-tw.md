---
title: "伺服器設定"
description: "組合模組 profile、設定環境變數、連接埠與持久性。"
updated: 2026-10-18
---

Bubble Fighter Fantasy 沒有固定的一組伺服器元件。`profiles/` 下的一個 **profile** 決定執行哪些模組資料列、服務哪個用戶端組建、開啟哪些監聽連接埠，以及使用哪種儲存方式。本頁說明 profile 如何變成執行中的伺服器、設定檔案的位置、環境變數與連接埠的運作方式，以及如何切換持久性與用戶端資料。

**本頁對象：** 需要改變伺服器執行內容的維運人員，以及在啟動前需要檢查實際設定的開發者。

**開始之前：**

- 先建置一次伺服器：`dotnet build BubbleFighter.Fantasy.sln -c Release` 必須以零警告完成。參見[安裝](installation.md)。
- 在儲存庫根目錄開啟終端機。本頁使用 `dotnet run` 的命令都假定目前目錄是儲存庫根目錄，因為 profile、packets 與用戶端資料都相對於工作目錄解析。
- 將 `.env.example` 複製為 `.env`，並設定非空的 `BFF_MYSQL_PASSWORD`。即使某次檢查會停用 MySQL，profile 插補也要求該變數有值。不要把真實憑證寫入準備提交的檔案。
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
    listen: "${BFF_ADMIN_HOST:-127.0.0.1}:${BFF_ADMIN_PORT:-8080}"
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
| `BFF_ADMIN_HOST` / `BFF_ADMIN_PORT` | 管理 HTTP 綁定位址。 | `127.0.0.1` / `8080` |
| `BFF_LOG_LEVEL` | 主控台日誌等級：Trace、Debug、Information、Warning、Error、Critical、None。 | `Information` |
| `BFF_CLIENT_DATA` | 各組建用戶端資料的根目錄。 | `client-data` |
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
| `BFF_MYSQL_PASSWORD` | 密碼。插補發生在停用修補檔之前，因此必須非空。 | 空，必須設定 |
| `BFF_MYSQL_DATABASE` | 資料庫名稱。可以使用任何已有的資料庫。 | `bf_game_db` |

連接埠與遊戲數值在後續章節說明。`.env.example` 是含註解的完整清單；請複製它，不要自行發明變數名稱。

## 連接埠與位址

`BFF_BIND_HOST` 是伺服器監聽的介面，`BFF_PUBLIC_HOST` 是伺服器告知用戶端使用的位址。位於 NAT 後方時兩者可以不同：綁定到能到達用戶端的介面，並公佈用戶端實際可存取的公用位址。讓 `BFF_PUBLIC_HOST` 保持回送預設值，只在用戶端與本機同機時有效。

監聽連接埠來自 profile，通常經由依組建的變數與 `vars` 設定。本次發行的兩個基準使用：

| 端點 | kr-1256（現代） | kr-812（舊版） |
| --- | --- | --- |
| 遊戲 TCP | `7100`（`KR_1256_GAME_PORT`） | `7110`（`KR_812_GAME_PORT`） |
| 中繼 TCP | `7101`（`KR_1256_RELAY_PORT`） | 舊版 bundle 不開啟 |
| 管理 HTTP | `8080`（`BFF_ADMIN_PORT`） | `8081`，由 profile 修補檔移動 |
| 選用原生選擇器 | 不使用 | 掛載 `center-selector.yaml` 時為 `7112` |

`all` profile 將已接上的用戶端組建組合進一個處理程序，每個組建使用自己的遊戲監聽器。其預設遊戲連接埠從 `7100` 開始以十遞增（`kr-1256` 7100、`kr-812` 7110、`id-400` 7120，直到 `cn-408` 7250）。開放防火牆規則前，請用 `--dump-config` 讀取所選 profile 的實際 `listen` 字串。

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

## 常用設定

第一個配方是隔離檢查。其餘是明確指向持久儲存的設定：它們指向你自己擁有的儲存，且**不**包含隔離修補檔，因為那會停用正在設定的提供者。不要將持久性配方指向你無意修改的資料庫。

### 本機隔離檢查

```sh
cp .env.example .env
# 編輯 .env，將 BFF_MYSQL_PASSWORD 設為一次性的非空值。
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- run --profile management --patch tools/local-checks/isolated-memory.patch.yaml
```

主控台會回報已啟用的資料列，Host 就緒後輸出 `Server started with <n> active row(s).`。存取 `http://127.0.0.1:8080/` 的 `GET /health`：它傳回含 `status` 欄位的 JSON。用 `Ctrl+C` 停止。

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

## 選用功能 bundle

少數 bundle 需要明確啟用，且只被用戶端稽核支援的 profile 列出。

**`player-settings`** 掛載 `dialect.customized-keys` 與 `player.settings`，讓玩家儲存自訂按鍵。它被 `kr-812`、`kr-1256`、`cn-774` 與 `tw-454` 列出；其他 profile 需先稽核其原生控制名稱與封包路徑。每個 `player.settings` 資料列都需要 `protocol`，並使用依 profile 的 `scope`，避免混淆不同組建的名稱。持久性提供者依帳號儲存對應；MySQL 透過移轉 `0032_customized_keys.sql` 儲存。每個提供者用 `customized_keys` 設定每帳號預算（`max_scopes` 1–64、`max_mappings` 1–512、`max_name_characters` 1–1024，三者組合必須能放入封包主體）。超出預算會直接失敗，而不會驅逐其他已儲存對應。

**`inventory-actions`** 掛載 `dialect.inventory-actions` 與 `inventory.throw`，讓玩家捨棄過期物品。它由已稽核操作消費者並具備儲存裝備服務的 profile 列出（`kr-812`、`cn-279`、`cn-324`、`cn-406`、`cn-404`、`cn-408`、`cn-706`、`cn-717`、`id-400`、`id-1809`、`kr-1256`）。行為被刻意限制得很窄：只移除一個已驗證玩家兩個背包中的該過期物品；若有符合的權益仍有效，整個移除會被拒絕；組建的類別 6 排除規則會被遵守。比賽中的請求會被拒絕。真實用戶端驗收仍待進行；僅憑封包表涵蓋不足以在另一個組建上掛載該 bundle。

**`launcher.http`** 是選用的公用啟動器 API。它預設停用，與管理工作分離。它接受 `minimum_launcher_version`（留空則關閉檢查）與 `plugin_recommendations`，後者包含商店識別與選用的精確 `builds`，絕不包含可執行檔路徑或下載位址。設定最低版本後，較舊的啟動器會收到 HTTP 426。每個大廳列的 `authentication` 選項（`BFF_LOGIN_AUTHENTICATION`，預設 `ticket`）只接受能兌換啟動器票據的登入，此時需要本列處於啟用狀態；`open` 接受任何帳號名稱，只適用於可信任的區域網路。[啟動器](launcher.md)頁面說明此 API、用戶端預設、帳號與票據流程以及面向營運者的設定。

啟動器請求與登入工作階段限制在 `launcher.http` 行設定：

| 設定項 | 預設值 | 行為 |
|---|---|---|
| `requests_per_minute` | `120` | 每個連線來源 IP 每分鐘可接收的請求數；轉送標頭不會改變來源 IP。 |
| `max_rate_limit_addresses` | `4096` | 最多保留的 IP 視窗數（1–100000）；滿時淘汰最早的視窗。 |
| `max_sessions_per_account` | `8` | 每帳號的有效持久登入工作階段數（1–1024）；新登入會撤銷建立時間最早的工作階段。 |
| `max_sessions` | `1024` | 既有的行程內存取憑證與待用遊戲票據容量上限。 |

撤銷舊工作階段時也會清除它在本行程中的存取憑證與待用遊戲票據。監聽器啟動前刪除過期更新工作階段，之後在登入時最多每小時清理一次。帳號上限由儲存層執行，因此伺服器重新啟動後仍然有效。

## 驗證與安全

- 未知設定鍵與無效值會使組合或啟用失敗，絕不會被忽略。啟用失敗的資料列會連同狀態與診斷一起回報，而不是被靜默跳過。
- `--dump-config` 會列印插補後的值，其中包括憑證。在問題報告中分享前請先去除識別資訊。
- 絕不提交 `.env` 或 `local.patch.yaml`；兩者都被 Git 忽略，且都可能包含機密或本機專用路徑。
- 每次本機 `run` 都附加 `--patch tools/local-checks/isolated-memory.patch.yaml`。這是防止開發者 `.env` 連到真實資料庫的唯一可靠辦法。
- profile 或修補檔的修改在下次啟動時生效：profile 只在啟動時組合一次。管理 API 可以重新載入單一資料列，但那是模組生命週期操作，不是套用新 profile 的方式。重新載入行為見[模組](plugins.md)。

## 遇到問題時

- **組合因缺少環境變數而失敗。** 錯誤訊息會指出變數名稱；將它加入 `.env`（或處理程序環境），或在空值合法時改用 `${VAR:-default}` 形式。
- **修補檔指向未知資料列。** profile 與修補檔清單不一致；用未套用修補檔的 profile 或提供該資料列的 bundle 搭配 `--dump-config` 檢查資料列 id。
- **遊戲 profile 因用戶端資料訊息啟用失敗。** 訊息提到的 `data`/`dataraw` 目錄不存在；為該組建 ID 準備目錄，或改用 `management`。
- **監聽器無法綁定。** 其他處理程序佔用了連接埠；修改依 profile 的連接埠變數或停止該處理程序。

依序排查的清單見[疑難排解](troubleshooting.md)。

## 下一步

繼續閱讀[部署](deployment.md)以建置並執行發行版本，或閱讀[模組](plugins.md)了解這些資料列的實際作用。
