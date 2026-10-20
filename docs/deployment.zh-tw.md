---
title: "部署"
description: "使用發行組建、Docker Compose、備份與復原可重現地執行伺服器。"
updated: 2026-10-18
---

本頁面向為其他人執行 Bubble Fighter Fantasy 的情境。內容涵蓋發行組建、本儲存庫的 Docker Compose 設定、Linux 與 Windows 手動安裝、持久資料與備份、日誌、網路安全，以及如何安全升級與復原。

**本頁對象：** 部署並維護伺服器處理程序的維運人員，而不只是在筆記型電腦上啟動一次。

**開始之前：**

- 閱讀[設定](configuration.md)。部署就是選擇 profile 再加上環境變數；本頁不重複組合機制。
- 建置並驗證你準備部署的確切版本。發行目錄來自一次零警告的 `dotnet build BubbleFighter.Fantasy.sln -c Release`。
- 規劃主機：受支援的作業系統與架構、相依於框架的組建所需的 ASP.NET Core Runtime 10（或 Docker）、專用服務帳號，以及你自己擁有的資料庫。
- 為將要服務的每個組建準備用戶端資料。缺少資料時遊戲 profile 會在啟用階段失敗。
- 決定備份存放位置。備份不能放在發行目錄內。

## 部署之前

這是預發行專案。它尚未針對所有受支援用戶端驗證，列出的 profile 或通過的自動化檢查都不構成真實用戶端相容性。請將它當作預發行服務執行，用真實用戶端測試，並保留還原路徑。

本次發行的受支援目標為 Windows x64、Windows ARM64、Linux x64 與 Linux ARM64。已知的部署環境是 Debian 13；其他 Linux 發行版未宣稱經過測試。發佈指令碼涵蓋 Windows x64 與 Linux x64/ARM64；Windows ARM64 是列出的目標，使用相同的 Host 發佈命令並指定 `-r win-arm64`。

公開的 Windows 套件、Linux 安裝指令碼與一鍵 Docker 安裝尚未發佈。本儲存庫現有的是原始碼、`tools/` 中的兩個發佈指令碼、`deploy/docker/` 中的 Docker 檔案，以及 Compose 設定。不要用其他來源的安裝器或映像儲存庫替代它們。

部署需要規劃四類網路與儲存事項：

| 事項 | 需要規劃的內容 | 本次發行的預設值 |
| --- | --- | --- |
| 遊戲連接埠 | 每個提供服務的組建一個 TCP 監聽器。 | `kr-1256` 使用 7100；舊版 `kr-812` 使用 7110。 |
| 中繼 | 只有 profile 開啟中繼監聽器的組建才需要。 | `kr-1256` 使用 7101；舊版組建不開啟。 |
| 管理 / WebUI | 保持在回送或受控網路；遠端管理需要以 `start` 啟動的伺服器，並使用管理員帳號登入。 | `127.0.0.1:8080`。 |
| MySQL | 只允許伺服器存取，絕不公開。 | `127.0.0.1:3306`，資料庫 `bf_game_db`。 |

請將用戶端資料放在本機儲存，並盡可能以唯讀方式掛載或複製。伺服器只讀取它。

## 建置發行目錄

`tools/` 中的兩個指令碼將 Host 與所有模組發佈到一個可複製到目標機器的目錄。兩者都產生**相依於框架**的組建：不綁定 .NET 執行階段，因此目標機器需要相符的 ASP.NET Core Runtime 10。

| 指令碼 | 目標 | 說明 |
| --- | --- | --- |
| `tools/publish-windows-x64.ps1` | `win-x64` | 驗證 apphost 是 Windows x64，且不包含綁定的執行階段。 |
| `tools/publish-linux.ps1` | `linux-x64`（預設）或 `linux-arm64` | 驗證 apphost 是指定架構的 ELF 檔案。 |

兩個指令碼都需要 PowerShell 工作階段，並接受必填的 `-OutputPath`。目錄可以不存在；若已存在則必須為**空**：發佈到包含舊發行版本的目錄會保留該版本的執行階段檔案。指令碼會失敗而不是覆寫。

### Windows

在儲存庫根目錄的 PowerShell 提示字元下：

```powershell
.\tools\publish-windows-x64.ps1 -OutputPath C:\BFF\release-2026-10-18
```

成功執行會列印 `Windows x64 framework-dependent publish ready: <path>`，並提示目標機器需要 x64 ASP.NET Core Runtime 10.0。

### Linux

在任意機器的 PowerShell 7 工作階段中發佈；輸出為 Linux 二進位檔：

```powershell
pwsh -File tools/publish-linux.ps1 -OutputPath /srv/bff-release/server
```

為 ARM64 目標加入 `-Runtime linux-arm64`。成功執行會列印 `Linux framework-dependent publish ready: <path> (<runtime>)`，並檢查 InGame 模組已發佈。PowerShell 只是指令碼宿主；目標機器不需要 PowerShell。

### 發佈內容

- `BubbleFighter.Fantasy.Host`（apphost）與 `BubbleFighter.Fantasy.Host.dll`，以及 Host 的相依項目和 `runtimeconfig.json`。
- `plugins/<ProjectName>/`，每個模組專案一個目錄，也是 Host 旁的預設模組根目錄。
- `profiles/`，每個 profile 一個目錄，不包含任何 `local.patch.yaml`。
- `packets/`，包含封包表與 `builds.yaml`。
- `bootstrap/`，包含 loader profile；以及 `data/level-experience.csv` 與 `data/locales/*.json`。

指令碼的替代做法是 `dotnet publish src/BubbleFighter.Fantasy.Host -c Release -o <directory>`，它執行相同的 Host 發佈，包括模組發佈目標與內容複製。它跳過指令碼的空目錄與 apphost 檢查，因此請使用新目錄並自行驗證結果。Windows ARM64 請使用這種形式並加 `-r win-arm64`。

複製前先檢查結果：目錄中包含 Host 可執行檔、每個模組一個目錄的 `plugins/`，以及 `profiles/`、`packets/`、`bootstrap/` 與 `data/` 目錄。不應出現 `coreclr.dll`、`libcoreclr.so` 或同類執行階段檔案；若發現這些檔案，表示發佈綁定了執行階段，不是你想要的發行版本。

## 從本儲存庫使用 Docker Compose

`deploy/docker/compose.yaml` 使用本儲存庫原始碼啟動伺服器與 MySQL 資料庫，是取得可執行容器設定的最快途徑。

1. 複製環境檔案，設定非空資料庫密碼與安裝權杖。Compose 檔案要求 `BFF_MYSQL_PASSWORD` 與 `BFF_SETUP_TOKEN`（至少 32 個隨機字元），缺少時拒絕啟動：

   ```sh
   cp .env.example .env
   ```

2. 在 Compose 檔案尋找的位置準備用戶端資料：為將要選擇的每個版本在儲存庫旁準備 `client-data/<build>/data` 與 `client-data/<build>/dataraw`，或以 `BFF_CLIENT_DATA_HOST` 指向其他位置。它會掛載到容器內的 `/app/client-data`。

3. 在儲存庫根目錄建置並啟動兩個服務：

   ```sh
   docker compose -f deploy/docker/compose.yaml up --build
   ```

這會啟動：

- `bff` 服務從 `deploy/docker/Dockerfile` 建置：基於 .NET SDK 與 ASP.NET 10 映像的多階段建置，以非特權使用者 `bff`（uid 1001）執行。它不帶指令執行主機程式，也就是首次安裝：開啟 `http://<docker-host>:8080`，頁面要求時輸入 `BFF_SETUP_TOKEN`，建立管理員帳號，選擇 MySQL，主機填 `mysql`，資料庫名稱、使用者與密碼取自 `.env`，並以資料資料夾 `/app/client-data` 選擇用戶端。之後的啟動會執行已儲存的安裝，管理員以該帳號登入。它公開 7100（遊戲）、7101（中繼）與 8080（管理與 WebUI）；Compose 設定了 `BFF_ADMIN_HOST=0.0.0.0`，讓公開的管理連接埠能到達容器。
- `mysql` 服務執行 MySQL 8.4，依 `BFF_MYSQL_DATABASE`、`BFF_MYSQL_USER` 與 `BFF_MYSQL_PASSWORD` 建立資料庫與帳號，在主機上發佈 `${BFF_MYSQL_PORT:-3306}`，並將資料保存在 `mysql-data` 磁碟區中。伺服器會等待其健康檢查通過後啟動，並在容器網路內收到 `BFF_MYSQL_HOST=mysql`。
- 用戶端資料以唯讀方式掛載到 `/app/client-data`；已儲存的安裝位於 `bff-state` 磁碟區，資料庫狀態位於 `mysql-data` 磁碟區。伺服器本身的記錄寫在容器內，此 Compose 檔案不會把它們保存到主機。

確認是否正常：在 Docker 主機上執行 `curl http://127.0.0.1:8080/health` 會傳回帶 `status` 欄位的 JSON，主控台會顯示安裝或執行進度。可用以下指令追蹤主控台：

```sh
docker compose -f deploy/docker/compose.yaml logs -f bff
```

用 `docker compose -f deploy/docker/compose.yaml down` 停止兩個服務。這會保留資料庫磁碟區。`down -v` 還會刪除它，因此除非確實要捨棄資料庫，否則不要使用 `-v`。

對外暴露前請審查該 Compose 檔案：它將 MySQL 發佈到主機，保留 Docker 的預設日誌處理（未設定大小限制），並執行預設遊戲 profile。它屬於開發風格設定，不是加固過的生產範本。

## 在 Linux 發行根目錄使用外部資料庫的 Docker

`deploy/docker/compose.external-db.yaml` 是另一種產物：面向已在執行資料庫的 Linux 主機上的發行根目錄範本。它預期以下配置，由你在目標機器上建立：

```text
<release-root>/
  compose.yaml     deploy/docker/compose.external-db.yaml 的副本
  Dockerfile       deploy/docker/Dockerfile.published 的副本
  .env             部署變數，包括 BFF_RELEASE
  server/          相依於框架的 Linux 發佈輸出
  logs/            由容器寫入
  client-data/     選用；或用 BFF_CLIENT_DATA_HOST 指向其他位置
```

1. 使用 `tools/publish-linux.ps1` 將發佈輸出寫入 `server/`。發佈目錄必須為空，因此在複製其他內容前先把發佈輸出寫入 `server/`。
2. 將 `deploy/docker/compose.external-db.yaml` 複製為 `<release-root>/compose.yaml`，將 `deploy/docker/Dockerfile.published` 複製為 `<release-root>/Dockerfile`。
3. 建立 `<release-root>/.env`。它必須設定 `BFF_RELEASE`（映像標籤）以及本機資料庫的 MySQL 變數。請審查位址與權杖：範本會強制設定 `BFF_BIND_HOST`、`BFF_MYSQL_HOST` 與管理位址。
4. 在 `<release-root>` 中啟動：

   ```sh
   docker compose up -d --build
   ```

該範本提供：從 `server/` 建置的 `bf-fantasy:<release>` 映像；主機網路，因此 `.env` 中設定的遊戲、中繼與管理連接埠直接使用，且可存取 `127.0.0.1` 上的資料庫；唯讀容器檔案系統加上小型可寫 `/tmp`；捨棄全部 capability；`no-new-privileges`；30 秒停止寬限期；除非停止否則重新啟動；將 `./logs` 掛載到 `/app/logs` 以保存封包日誌檔案；用戶端資料以唯讀方式掛載到 `/app/client-data`；json-file 日誌限制為 5 個 10 MB 檔案。

該 Dockerfile 變體以 `run --profile all` 啟動映像，因此發行版本會在各自連接埠上服務所有已接上的組建，並需要這些組建的用戶端資料。若只服務一個組建，請修改你那份 Dockerfile 中的 `CMD`，該檔案由維運人員管理。

`docker compose down` 停止服務並保留 `logs/` 目錄。資料庫不由該範本管理：請用其自身工具備份與升級。`deploy/docker/Dockerfile.published` 是該配置使用的儲存庫檔案；沒有任何映像發佈到映像儲存庫，因此映像一律在目標機器上建置。

## Linux 手動部署

不想使用容器時可採手動方式：將 Linux 發佈輸出複製到目標機器並直接執行 Host。

1. 使用 `tools/publish-linux.ps1` 發佈，並將輸出複製到 `<release-root>`。保持發佈的目錄結構不變。
2. 建立專用服務帳號（例如 `bff`），擁有發行目錄但沒有互動式登入。Host 只需讀取 `profiles/`、`packets/`、`bootstrap/`、`plugins/` 與 `data/`；它需要可寫的工作目錄來寫 `logs/`，使用 SQLite 時還需要可寫位置存放資料庫檔案。
3. 若複製過程遺失執行權限，請讓 apphost 可執行：`chmod +x BubbleFighter.Fantasy.Host`。
4. 在處理程序環境中提供部署變數。發行目錄不包含儲存庫標記，因此放在旁邊的 `.env` **不會**被 Host 讀取；`.env` 載入規則只在檢出目錄內生效。請使用由服務管理員或 shell 載入的環境檔案，例如：

   ```sh
   set -a
   . <release-root>/deployment.env
   set +a
   ```

   該檔案包含與 `.env.example` 相同的 `KEY=VALUE` 項目，填入真實的 MySQL 密碼、公用位址以及管理權杖。

5. 進入發行根目錄並以前景方式啟動 Host：

   ```sh
   cd <release-root>
   ./BubbleFighter.Fantasy.Host run --profile kr-1256
   ```

   工作目錄很重要：`profiles`、`packets`、`client-data` 與 `logs` 都相對於它解析。`Ctrl+C` 或 SIGTERM 要求正常關閉，主控台最後輸出 `Shutdown complete.`。

6. 儲存庫不提供服務單元。長時間執行時，請將 Host 放在你管理的服務管理員（例如 systemd 服務）下，將工作目錄設為 `<release-root>`，並透過其環境檔案提供變數。保持服務帳號非特權，只授予它發行目錄、日誌目錄以及（若使用）SQLite 檔案的存取權限。

在主機上執行 `curl http://127.0.0.1:8080/health` 檢查，並用下方的監聽連接埠檢查。用服務管理員的 SIGTERM 或前景的 `Ctrl+C` 停止。

## Windows 手動部署

1. 使用 `tools/publish-windows-x64.ps1` 發佈，並將輸出複製到部署目錄。
2. 在啟動前為處理程序設定部署變數，例如在將擁有該處理程序的 PowerShell 工作階段中：

   ```powershell
   $env:BFF_MYSQL_HOST = '127.0.0.1'
   $env:BFF_MYSQL_PASSWORD = '<your-database-password>'
   $env:BFF_PUBLIC_HOST = '<public-address-clients-use>'
   ```

   與 Linux 一樣，複製到發行目錄中的 `.env` 不會被讀取，因為 Host 只有在能找到儲存庫根目錄時才載入儲存庫 `.env`。

3. 從發行目錄啟動 Host，使相對的 `profiles`、`packets`、`client-data` 與 `logs` 路徑在此解析：

   ```powershell
   Set-Location C:\BFF\<release>
   .\BubbleFighter.Fantasy.Host.exe run --profile kr-1256
   ```

4. 只允許計畫中的遊戲、中繼與管理連接埠通過防火牆。管理連接埠只對可信網路可達，並讓 MySQL 遠離公用介面。
5. 本儲存庫沒有 Windows 服務包裝器。若必須隨機器啟動，請在你自己管理的服務管理員下執行可執行檔；保持服務帳號非特權。

用 `Invoke-RestMethod http://127.0.0.1:8080/health` 檢查，它傳回帶 `status` 欄位的 JSON。用 `Ctrl+C` 停止；在平台提供相應訊號時，Host 也處理 SIGTERM 與 SIGINT。

## 持久資料與備份

| 資料 | 位置 | 升級前是否備份 |
| --- | --- | --- |
| 遊戲與帳號資料 | MySQL 資料庫 `bf_game_db`，或一個 SQLite 存檔檔案。 | 是；每次升級都要。 |
| 用戶端資料 | `client-data/<build>/` 下的唯讀檔案。 | 一次即可，或更換用戶端副本時。 |
| 設定與機密 | Compose 的 `.env` 或服務管理員的環境檔案。 | 是；妥善保管。 |
| 日誌與封包日誌 | 工作目錄下的 `logs/<date>/`，或容器內路徑。 | 選用；依策略輪替與保留。 |
| 舊發行版本 | 獨立目錄。 | 保留到復原窗口關閉。 |

請將資料庫、其備份以及 SQLite 存檔檔案放在發行目錄之外，因為發行目錄會被取代。MySQL 請在伺服器停止或無寫入時使用資料庫自身的備份工具，例如邏輯傾印（`mysqldump`）或一致性儲存快照。SQLite 請先停止伺服器，再複製單一存檔檔案。伺服器正在寫入時絕不複製執行中的 SQLite 檔案。

在依賴備份前先在獨立執行個體上測試還原：還原資料庫或複製存檔檔案，用它啟動發行版本，並確認 `/health`、模組資料列狀態與用戶端流程。從未還原過的備份不算經過驗證的備份。

用戶端資料是輸入而非狀態：伺服器只讀取它，Docker 設定以唯讀方式掛載它，因此取代發行版本不會改變它。

## 日誌

伺服器將日誌輸出到主控台。每列是單列並帶 UTC 時間戳記；等級來自 `BFF_LOG_LEVEL` 或 `--log-level`，預設 `Information`。正常啟動會回報每個已啟用的資料列、每個監聽器的監聽位址，就緒後輸出 `Server started with <n> active row(s).`。正常停止以 `Shutdown complete.` 結束。

診斷資料列還會寫封包日誌檔案。它們與主控台分開，每個封包與連線事件一列：

- 預設路徑是相對於工作目錄的 `logs/{date}/packets_{timestamp}.log`。profile 會覆寫檔案名稱：`kr-812` 寫 `packets-kr-812_{timestamp}.log`，`all` profile 寫 `packets-all_{timestamp}.log`。
- 每個檔案達到 10 MiB 時輪替為 `.1`；`{date}` 變更時開始新檔案。預設依設定的檔案名稱模式保留最多 100 個檔案，寫入器啟動時會刪除較舊的檔案。檔案名稱時區預設為 China Standard Time。
- 寫入經過有界佇列（預設 32768 筆），關閉時最多等待 10 秒清除。溢位會以 `packet-log-overflow` 回報，絕不丟棄遊戲封包。
- 完整資料包記錄預設關閉。設定 `BFF_PACKET_CAPTURE=true` 可啟用，並在同一 `logs/` 目錄樹下寫出獨立的 JSONL 資料包檔案。

封包日誌列與擷取可能包含玩家與連線資料；請將 `logs` 目錄視為敏感資料並套用你的保留策略。在 Docker 設定中，發行根目錄範本掛載 `./logs`，使封包檔案保存在主機上，而主控台輸出進入容器的 json-file 日誌，限制為 5 個 10 MB 檔案。

## 管理與網路安全

- 管理監聽器預設綁定 `127.0.0.1:8080`。以 `start` 啟動的伺服器（執行檔不帶參數時即如此）要求管理員以帳號與密碼登入；`/health` 與 WebUI 檔案保持開放，讓瀏覽器能載入登入頁。以 `run --profile` 啟動的伺服器沒有帳號、不需登入，並且只在回送位址上監聽。
- 在將管理連接埠暴露到回送之外前，請限制來源網路。若無法限制，就讓它保持在回送，並使用你自行管理的通道或反向代理。
- 防火牆只開放所選 profile 提供服務的遊戲與中繼連接埠。不要將管理連接埠或 MySQL 發佈到公用介面。
- 使用專用資料庫帳號，將 MySQL 連接埠保持在回送或私有網路，絕不要將資料庫管理員帳號重用於伺服器。
- 機密保存在部署本機的 `.env`（Compose）或服務管理員環境檔案（手動）中。限制其權限，絕不提交。`--dump-config` 會顯示插補後的機密，不要將它的輸出附到公開報告中。
- 不要公開用戶端資料、封包日誌或擷取檔案；它們可能包含專有用戶端檔案與玩家資料。

## 升級與復原

升級採並排方式：將新發行版本安裝到新目錄，保留舊版本，透過停止一個處理程序再啟動另一個來完成切換。

1. 公布維護窗口並停止接收新工作。讓比賽結束或有意終止；然後用 SIGTERM 或 `Ctrl+C` 正常停止伺服器，等待 `Shutdown complete.`。
2. 依上述方式備份資料庫，並記錄正在執行的確切發行目錄與設定。
3. 將新發行版本發佈到全新的空目錄。不要覆寫舊發行版本。
4. 複製或沿用部署變數與用戶端資料。除非更新用戶端，用戶端資料在版本之間不變。
5. 從新目錄啟動新發行版本並驗證：`/health`、模組資料列狀態、監聽連接埠、儲存連線列，以及主控台無錯誤。
6. 保留舊發行目錄與升級前備份，直到不再需要復原。

復原時，停止新發行版本，用相同設定啟動舊發行目錄；若新版本改動了資料庫結構，則還原升級前的備份。僅復原二進位檔並不保證可行：MySQL 移轉在持久性資料列啟用時自動套用，且僅向前。伺服器沒有降級路徑，因此較舊的二進位檔可能無法在較新版本已移轉的資料庫上執行。安全的復原組合是舊發行版本加上升級前所做的備份。

## 部署後檢查

- [ ] `GET /health` 傳回 `status` 為 `ok` 的 JSON。
- [ ] WebUI 模組清單（或已登入工作階段中的 `GET /api/plugins`）顯示每個預期的列，且沒有失敗狀態。
- [ ] 主機上的監聽連接埠符合計畫；Linux 用 `ss -ltn`，PowerShell 用 `Get-NetTCPConnection -State Listen` 檢查。
- [ ] 主控台顯示儲存連線列：MySQL 已連線，或 SQLite 已啟用。
- [ ] 每個提供服務的遊戲組建的 `clientdata` 資料列處於啟用狀態，而非失敗。
- [ ] 日誌等級適合生產；若預期有封包日誌，封包日誌目錄可寫且在成長。
- [ ] 已取得備份，並至少在一個測試執行個體上還原過一次。
- [ ] 由擁有者用真實用戶端測試過。自動化檢查與列出的 profile 都不等於用戶端驗收。

## 遇到問題時

- **容器立即結束。** 預設 profile 是遊戲 profile，其用戶端資料缺少。加入 `client-data/<build>/data` 或 `dataraw`，或執行不需要用戶端資料的 profile。
- **伺服器無法連線 MySQL。** 檢查位址、連接埠、帳號與資料庫名稱。發行目錄不會讀取本機 `.env`，因此請在處理程序環境中核對變數。
- **`/api` 傳回 401。** 要求沒有已登入的管理員工作階段；請先在 WebUI 中登入。
- **連接埠已被占用。** 停止衝突處理程序或修改依 profile 的連接埠變數，然後重新啟動。

依序排查的清單見[疑難排解](troubleshooting.md)。

## 下一步

閱讀[設定](configuration.md)了解上面用到的 profile、連接埠與儲存鍵，以及[模組](plugins.md)了解資料列的啟用與重新載入方式。

## 安裝憑據

`state/administration/installation.json` 會以明文保存 MySQL 密碼，讓伺服器重啟後可以重新連線。請限制狀態目錄只有擁有者可讀取：Windows 透過檔案安全性只授予服務帳號；Linux 使用 `chown` 設定服務使用者，並執行 `chmod 700 state/administration` 及 `chmod 600 state/administration/installation.json`。遠端 MySQL 使用 `SslMode=None` 時伺服器會發出警告；資料庫不在本機時應啟用 TLS。
