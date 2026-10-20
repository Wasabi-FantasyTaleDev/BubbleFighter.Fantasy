---
title: "安裝"
description: "準備工具鏈，從原始碼建置伺服器，或以 Docker 執行伺服器。"
updated: 2026-10-20
---

Bubble Fighter Fantasy 是 Fantasy Tale Studio 維護的遊戲《泡泡大亂鬥》（Bubble Fighter）社群伺服器，以 C# 與 .NET 10 撰寫。伺服器能力由 YAML profile 中的模組列組合而成：profile 選擇某個用戶端版本需要的模組列，Host 負責啟用它們。本頁帶你從空白機器完成到確認可用的安裝：先決條件、目前已有的安裝方式、在 Windows 與 Linux 上從原始碼建置、執行 Docker Compose 組合、首次執行安裝助理，以及用來驗證每個步驟的檢查。

**本頁適合：** 第一次部署伺服器、希望完成安裝並確認可執行的讀者。不需要事先了解本專案。

## 安裝的是什麼

你安裝的是伺服器 Host 以及它啟動時讀取的資料：

- Host 執行檔（`BubbleFighter.Fantasy.Host`）、模組組件，以及隨套件提供的 `profiles`、`packets`、`data` 與 `bootstrap` 目錄；
- 首次執行助理會在執行檔旁建立的安裝狀態目錄；
- 選用：你為要執行的用戶端版本自行準備的用戶端資料。

儲存庫不包含遊戲用戶端，也不包含用戶端的專有資料。部分 profile 列需要相符版本的在本機檔案，缺少這些檔案時對應列會在啟用階段失敗。專案目前是預先發布版本（prerelease）：不同 profile 可用的玩法與所需資料不同，列出某個 profile 或通過自動檢查並不代表真實用戶端的相容性已經驗證。

每個版本都附有 Windows x64、Windows ARM64、Linux x64 與 Linux arm64 的預先編譯套件。在 Linux 上可以用安裝指令碼以原生方式或 Docker 方式安裝，見下文「安裝預先編譯套件」。原始碼建置與本儲存庫中的 Docker 檔案仍然可用。

## 開始之前

| 需求 | 用於 | 如何檢查 |
| --- | --- | --- |
| Windows x64、Windows ARM64、Linux x64 或 Linux arm64 | 執行伺服器 | 系統設定；Linux 上執行 `uname -m` |
| .NET 10 SDK | 從原始碼建置 | `dotnet --version` 顯示 10.x |
| Node.js 20.19 或更新版本 | 從原始碼建置（建置時會編譯網頁介面） | `node --version` 顯示 20.19 或更新 |
| ASP.NET Core Runtime 10 | 執行相依於框架的建置輸出 | `dotnet --list-runtimes` 顯示 `Microsoft.AspNetCore.App 10.x` |
| Git | 從原始碼建置 | `git --version` |
| Docker Engine 與 Compose | 僅 Docker 方式 | `docker compose version` |
| 執行版本對應的用戶端資料 | 遊戲 profile | `client-data/<build>/data` 存在 |
| 位於儲存庫根目錄的終端機 | 本頁除容器內部之外的每條命令 | `Get-Location` 或 `pwd` |

儲存庫沒有規定最低 Windows 或 Linux 版本；請使用 .NET 10 SDK 支援的系統。選用的模組 SDK（Python、TypeScript、Go 與 Rust）在安裝或執行伺服器時都不需要。

## 選擇安裝方式

| 方式 | 狀態 | 適用情境 |
| --- | --- | --- |
| Windows x64 建置套件 | 可用 | Windows x64，希望使用現成建置 |
| Windows ARM64 建置套件 | 可用 | Windows on ARM，希望使用現成建置 |
| Linux 安裝指令碼 | 可用 | 不使用 Docker 的 Linux 發行方式 |
| Docker 一鍵安裝 | 可用 | 無需取出儲存庫，在容器中執行伺服器，可選附帶 MySQL |
| 從本儲存庫建置 Docker | 可用 | 已有取出與 Docker，在容器中執行伺服器與 MySQL |
| 從原始碼建置 | 可用 | 希望在本機取得建置輸出與首次執行助理 |

## 安裝預先編譯套件

每個版本附有四個套件與一份 `SHA256SUMS` 校驗清單。這些套件相依於框架：執行它們的電腦需要 ASP.NET Core Runtime 10。缺少執行階段時，Linux 安裝指令碼會自動安裝。

| 套件 | 適用於 |
| --- | --- |
| `bubblefighter-fantasy-win-x64.zip` | Intel 或 AMD 64 位元處理器上的 Windows |
| `bubblefighter-fantasy-win-arm64.zip` | Windows on ARM |
| `bubblefighter-fantasy-linux-x64.tar.gz` | x86_64 上的 Linux |
| `bubblefighter-fantasy-linux-arm64.tar.gz` | aarch64 上的 Linux |

最新版本的檔案一律位於 `https://github.com/Wasabi-FantasyTaleDev/BubbleFighter.Fantasy/releases/latest/download/<package>`。

### Windows

1. 安裝符合處理器（x64 或 Arm64）的 [ASP.NET Core Runtime 10](https://dotnet.microsoft.com/download/dotnet/10.0)，或安裝已包含它的 .NET 10 SDK。
2. 下載符合處理器的 ZIP，解壓縮到較短的資料夾，例如 `C:\BubbleFighterFantasy`。完整路徑達到約 259 個字元的模組檔案無法在 Windows 上載入；從這麼深的資料夾啟動時，伺服器會拒絕啟動並指出最長的檔案。
3. 按兩下 `BubbleFighter.Fantasy.Host.exe`。安裝助理隨即啟動，WebUI 會在預設瀏覽器中開啟；接著依照下文「首次執行設定」操作。關閉主控台視窗即停止伺服器。啟動失敗時視窗會保持開啟並顯示錯誤，按任意鍵後才關閉。

### Linux 安裝指令碼

```bash
curl -fsSL https://wasabi-fantasytaledev.github.io/install.sh | bash
```

指令碼會辨識 x86_64 或 aarch64，從最新版本下載對應的套件與 `SHA256SUMS` 並校驗。系統沒有 ASP.NET Core Runtime 10 時，它會用 Microsoft 的 `dotnet-install.sh` 將執行階段安裝到安裝目錄中，不更動系統層級的 .NET。安裝目錄為 `~/bubblefighter-fantasy`，以 root 執行時為 `/opt/bubblefighter-fantasy`：

| 路徑 | 內容 |
| --- | --- |
| `releases/<tag>/` | 每個已安裝版本解壓縮後的套件 |
| `current` | 指向目前版本的連結 |
| `state/` | 安裝助理儲存的安裝狀態，升級時保留 |
| `dotnet/` | 執行階段，僅在安裝指令碼需要安裝時存在 |
| `bff` | 啟動器；不帶參數時啟動安裝助理 |

用 `~/bubblefighter-fantasy/bff` 啟動伺服器，然後開啟 `http://127.0.0.1:8080/`。助理只監聽本機回送位址；在遠端伺服器上，請先在自己的電腦上用 `ssh -L 8080:127.0.0.1:8080 <user>@<server>` 轉送連接埠。

選項寫在 `bash -s --` 之後，例如 `curl -fsSL https://wasabi-fantasytaledev.github.io/install.sh | bash -s -- --service`：

| 選項 | 作用 |
| --- | --- |
| `--version <tag>` | 安裝指定版本而非最新版本 |
| `--dir <path>` | 安裝目錄 |
| `--admin-port <n>` | 安裝助理與 WebUI 的連接埠（預設 8080） |
| `--service` | 以 root 執行時：建立並啟動 `bubblefighter-fantasy` systemd 服務，以 `bff` 系統使用者執行 |
| `--start` | 安裝完成後啟動伺服器 |
| `--no-runtime` | 缺少執行階段時直接失敗，而不是安裝它 |
| `--from <dir>` | 從存放版本檔案的目錄安裝，不下載 |

升級時再次執行同一條命令即可。新版本會解壓縮到舊版本旁邊，並透過列出其 profile 進行檢查，檢查通過後才成為 `current`。`state/` 不會被更動。

### Docker 一鍵安裝

```bash
curl -fsSL https://wasabi-fantasytaledev.github.io/install.sh | bash -s -- --docker
```

需要 Docker Engine 與 Compose 外掛。安裝指令碼會把 Linux 套件解壓縮到 `~/bubblefighter-fantasy/docker`，用它建置本機映像檔，並透過 Docker Compose 啟動伺服器容器。容器使用主機網路，因此你選擇的用戶端所用的遊戲連接埠都可以連線。預設不啟動資料庫容器：在安裝助理中選擇 SQLite（資料檔儲存在 `bff-state` 磁碟區中），或填寫既有的 MySQL 伺服器。

如需同時執行內建的 MySQL 8.4，請加上 `--with-mysql`：

```bash
curl -fsSL https://wasabi-fantasytaledev.github.io/install.sh | bash -s -- --docker --with-mysql
```

此時 MySQL 只監聽 `127.0.0.1:3306`（可用 `--mysql-port` 修改），產生的 `.env` 儲存隨機產生的資料庫密碼，只有其擁有者可以讀取。在安裝助理中選擇 MySQL，主機填 `127.0.0.1`，連接埠填安裝指令碼輸出的連接埠，資料庫 `bf_game_db`，使用者 `bff`，密碼為 `~/bubblefighter-fantasy/docker/.env` 中 `BFF_MYSQL_PASSWORD` 的值。

再次執行同一條命令即可升級；`.env` 與各個磁碟區都會保留，帶 MySQL 建立的部署升級後仍保留 MySQL。在該目錄中執行 `docker compose logs -f bff` 可以查看記錄。

## 在 Windows 上從原始碼建置

在 Windows PowerShell 中執行以下步驟。儲存庫可以複製到任意目錄；後續命令都在儲存庫根目錄執行。

### 1. 複製儲存庫

```powershell
git clone <repository-url> BubbleFighter.Fantasy
cd BubbleFighter.Fantasy
```

`<repository-url>` 是專案公布的儲存庫位址。如果你已經有取出或原始碼壓縮檔，請直接在該目錄開啟 PowerShell。

### 2. 檢查 .NET SDK

```powershell
dotnet --version
```

命令必須輸出 10.x 版本。如果版本不符或找不到命令，請先安裝 .NET 10 SDK。

### 3. 建置所有專案 {#build-all-projects-windows}

```powershell
dotnet build BubbleFighter.Fantasy.sln -c Release
```

首次建置會下載相依套件，可能需要幾分鐘。本儲存庫將警告視為錯誤，因此成功建置以下列輸出結束：

```text
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

### 4. 找到建置輸出

| 內容 | 位置 |
| --- | --- |
| Host 執行檔與程式庫 | `src/BubbleFighter.Fantasy.Host/bin/Release/net10.0/` |
| 模組建置輸出 | `artifacts/plugins/<AssemblyName>/` |
| 執行時使用的副本 | `<host output>/plugins/` |
| Host 旁的隨附資料 | `<host output>/profiles/`、`packets/`、`data/`、`bootstrap/` |

Host 建置會把模組目錄複製到 `<host output>/plugins/`，並在每次建置時取代該目錄，因此已刪除的模組不會殘留。複製到輸出中的 `profiles/` 不包含 `local.patch.yaml`；該檔案是被 Git 忽略的開發者覆寫檔案，只在取出的目錄中存在時才會生效。

### 5. 執行建置好的 Host

在儲存庫根目錄執行，這樣預設的 `./profiles` 目錄才能解析：

```powershell
.\src\BubbleFighter.Fantasy.Host\bin\Release\net10.0\BubbleFighter.Fantasy.Host.exe profiles list
```

命令會為每個隨附的 profile 輸出一行，說明執行檔及其 profile 資料已就位。啟動安裝助理的隨附首次執行方式見下文「首次執行設定」。

**檢查是否成功：** 建置以 `0 Warning(s)` 與 `0 Error(s)` 結束，且上面的命令輸出 profile 行而不是錯誤。

## 在 Linux 上從原始碼建置

在 POSIX shell 中執行以下步驟。儲存庫可以複製到任意目錄；後續命令都在儲存庫根目錄執行。

### 1. 複製儲存庫

```sh
git clone <repository-url> BubbleFighter.Fantasy
cd BubbleFighter.Fantasy
```

### 2. 檢查 .NET SDK

```sh
dotnet --version
```

命令必須輸出 10.x 版本。

### 3. 建置所有專案 {#build-all-projects-linux}

```sh
dotnet build BubbleFighter.Fantasy.sln -c Release
```

成功建置以 `Build succeeded.`、`0 Warning(s)` 與 `0 Error(s)` 結束。

### 4. 執行建置好的 Host

```sh
dotnet src/BubbleFighter.Fantasy.Host/bin/Release/net10.0/BubbleFighter.Fantasy.Host.dll profiles list
```

請在儲存庫根目錄執行，這樣預設的 `./profiles` 目錄才能解析。DLL 旁的 apphost 執行檔用法相同。

### 5. 注意執行階段需求

建置需要 .NET 10 SDK 與 Node.js 20.19 或更新版本：WebUi 專案會在首次建置時用 npm 編譯網頁介面，之後只在其原始碼變更時重新編譯。相依於框架的輸出（例如 `tools/publish-linux.ps1` 產生的發行目錄，或從容器映像複製出來的檔案）需要執行機器上安裝 ASP.NET Core Runtime 10。可用 `dotnet --list-runtimes` 檢查，確認存在 `Microsoft.AspNetCore.App 10.x`。下面的 Docker 方式會在映像內提供執行階段。

**檢查是否成功：** 建置無警告無錯誤，且 `profiles list` 輸出 profile 行。

## 使用本儲存庫的 Docker 執行

在儲存庫根目錄、已安裝 Docker Engine 與 Compose 模組的前提下執行以下步驟。

### 1. 建立環境變數檔案

PowerShell：

```powershell
Copy-Item .env.example .env
```

Linux shell：

```sh
cp .env.example .env
```

啟動前把 `.env` 中的 `BFF_MYSQL_PASSWORD` 設為非空的臨時值。Compose 會用它設定 MySQL 容器；`.env` 被 Git 忽略，絕不要提交。MySQL 連線變數在 `.env.example` 中有說明。

### 2. 啟動伺服器與資料庫

```sh
docker compose -f deploy/docker/compose.yaml --env-file .env up --build
```

compose 檔案會啟動兩個服務：

- `bff` 使用 `deploy/docker/Dockerfile` 建置，建置內容為儲存庫根目錄。Dockerfile 先發布不含執行階段的 Host，再於 ASP.NET Core Runtime 10 映像中以非 root 系統使用者執行，進入點命令為 `dotnet BubbleFighter.Fantasy.Host.dll run --profile kr-1256`。映像宣告連接埠 7100、7101 與 8080。
- `mysql` 使用 `mysql:8.4` 映像，資料庫名稱為 `bf_game_db`、使用者為 `bff`、字元集為 utf8mb4，資料存放在 `mysql-data` 磁碟區中，並在伺服器啟動前通過健康檢查。

對外發布的連接埠為伺服器的 7100、7101、8080，以及 MySQL 的 `${BFF_MYSQL_PORT:-3306}`。伺服器把 `${BFF_CLIENT_DATA_HOST:-../../client-data}`（即儲存庫中的 `client-data` 目錄）以唯讀方式掛載到 `/app/client-data`；容器需要 `client-data/kr-1256/data` 或 `client-data/kr-1256/dataraw`，否則 clientdata 列會失敗。兩個服務都使用 `restart: unless-stopped`。

容器方式會刻意啟動 MySQL，它不是隔離的本機檢查，也不使用本機檢查修補檔。管理監聽在容器內綁定 `127.0.0.1:8080`，如需透過發布的 8080 連接埠存取 WebUI，請在 `.env` 中加入 `BFF_ADMIN_HOST=0.0.0.0`。

### 3. 追蹤記錄

```sh
docker compose -f deploy/docker/compose.yaml logs -f bff
```

啟動記錄應與下文「首次執行設定」描述的行一致。按 Ctrl+C 只會停止記錄追蹤，`up` 啟動的服務仍在執行。

### 4. 停止容器

在執行 compose 命令的終端機按 Ctrl+C，或在另一個終端機執行：

```sh
docker compose -f deploy/docker/compose.yaml down
```

`down` 會保留 `mysql-data` 磁碟區，之後再次 `up` 會繼續使用同一個資料庫。`docker compose -f deploy/docker/compose.yaml down -v` 會同時刪除該磁碟區及其中的全部遊戲資料，請只在資料可以捨棄時使用。

### 手動單一容器方式

如果不使用 compose 組合，而是自行執行一個容器，可以在儲存庫根目錄建置映像，並傳入可存取的 MySQL 與用戶端資料掛載：

```sh
docker build -f deploy/docker/Dockerfile -t bff-fantasy .
docker run --rm --env-file .env -e BFF_MYSQL_HOST=<mysql-host> -e BFF_ADMIN_HOST=0.0.0.0 -p 7100:7100 -p 7101:7101 -p 8080:8080 -v "<client-data-directory>:/app/client-data:ro" bff-fantasy
```

映像只複製發布後的 Host 與 `profiles` 目錄，不包含資料庫，也不包含用戶端資料。請把 `BFF_MYSQL_HOST` 指向容器能夠存取的位址。

**檢查是否成功：** `docker compose -f deploy/docker/compose.yaml ps` 顯示各服務正在執行，伺服器記錄出現下一節所述的啟動行。首次建置可能需要幾分鐘。

## 首次執行設定

隨附的首次執行體驗會啟動內建 `setup` profile：安裝助理與管理 WebUI，不監聽任何遊戲連接埠。它可以在沒有用戶端資料、也沒有資料庫的空白機器上啟動；套用前需要選擇並測試資料庫。

### 1. 不帶參數啟動 Host

在 Windows 上，開啟建置輸出目錄中的終端機並執行執行檔：

```powershell
.\BubbleFighter.Fantasy.Host.exe
```

請把隨附的 `profiles`、`packets` 與 `plugins` 目錄放在執行檔旁；建置輸出已經包含它們。處理程序會把工作目錄切換到執行檔所在目錄，因此從哪裡啟動都不影響。`start` 命令從不讀取儲存庫根目錄的 `.env`；助理把設定儲存在狀態目錄中。

狀態目錄預設是執行檔旁的 `state/`。`BFF_STATE_DIR` 或 `--state-dir <directory>` 可以覆寫它；相對路徑會依啟動處理程序時所在的目錄解析。

### 2. 讀取列印的位址

主控台會列印管理監聽位址與 WebUI 位址：

```text
Row 'admin.http' admin API listening on 127.0.0.1:8080.
WebUI: http://127.0.0.1:8080/
```

在瀏覽器開啟 `http://127.0.0.1:8080/`。管理監聽預設是 `127.0.0.1:8080`；如需修改，請在處理程序環境中設定 `BFF_ADMIN_HOST` 或 `BFF_ADMIN_PORT`（`start` 不讀取 `.env`）。

### 3. 完成助理

1. 選擇介面語言：英文、簡體中文、繁體中文或韓文。
2. 為伺服器命名（1 到 80 個字元）。
3. 選擇一個或多個要啟用的用戶端版本。每個選取的版本有各自的用戶端資料選擇；你也可以暫時不提供資料，稍後在伺服器設定中匯入。
4. 選用：現在匯入用戶端資料。選擇本機資料夾或準備好的 ZIP 套件，檢查篩選後的檔案數量與大小，然後按下「開始匯入」。瀏覽器本身的資料夾確認可能會統計資料夾內的全部檔案；助理會先把選擇篩選為用戶端封存檔與 `data`/`dataraw` 檔案，再顯示彙總。匯入會在狀態目錄中產生一份副本；失敗或取消的匯入只清理它自己的暫存檔案。
5. 選擇資料庫。預設選取 MySQL：主機 `127.0.0.1`、連接埠 `3306`、資料庫 `bf_game_db`、使用者 `bff`。SQLite 預設把資料庫儲存在狀態目錄中。請執行「測試連線」；連線測試成功前助理不會繼續。
6. 建立管理員帳號。使用者名稱 3 到 64 個字元，密碼至少 12 個字元。此帳號用於管理伺服器，隨安裝資訊一起儲存，與遊戲帳號分開。
7. 檢查各項設定並套用。Host 會啟動所選 profile 的列；如果啟用失敗，會顯示可重試的錯誤碼。

### 4. 套用成功後磁碟上的內容

在狀態目錄中會出現：

- `installation.json`：儲存的設定與管理員憑證雜湊；
- `installation.lock`：防止兩個安裝程序同時管理同一狀態的鎖定檔案；
- `runtime.generated.patch.yaml`：衍生的持續性覆寫檔案（只有預留位置，不含資料庫憑證）；
- `administration/client-imports/<token>/`：每次匯入的用戶端資料副本各佔一個目錄；
- 選擇 SQLite 時的 `saves/fantasy.db`。

### 5. 匯入規則與限制

資料夾匯入接受用戶端原始相對路徑下的封存檔案（保留用戶端的字母大小寫），也接受路徑中包含 `data` 或 `dataraw` 的散裝檔案。ZIP 匯入接受一個標準單卷 ZIP，其項目為 `<build>/data` 或 `<build>/dataraw`，也可以位於最多兩層包裝目錄之下，例如 `client-data/<build>/data`。包含多個版本目錄的套件只匯入所選版本。不支援 ZIP64 與分卷壓縮檔；請先在本機解壓縮，再使用資料夾選擇。

也可選擇已安裝的遊戲資料夾：伺服器會自動產生所需資料，不需要 Python。只含伺服器資料的封存檔整包複製到 `data/`；混合封存檔中所需的小檔案解到 `dataraw/`，不擷取模型與材質。產生前會檢查必要檔案並回報缺少的檔名，成功後儲存到 `<state-dir>/client-data/<build>`。既有產生資料不會被覆寫；若需更換設定來源，請選擇預先整理好的資料。預先整理好的資料夾與 ZIP 仍作為獨立副本儲存到 `administration/client-imports/<token>/`。產生過程受上傳、解壓縮、儲存、檔案數與逾時限制。

匯入只是把檔案複製到安裝狀態；所需的資料表與封存檔內容會在遊戲服務啟動時檢查。匯入成功並不代表玩法或用戶端相容性已經驗證。

預設限制為每次上傳 8 GiB、每次匯入解壓資料 16 GiB、每次匯入 32,768 個檔案、保留 64 份副本、匯入儲存總量 64 GiB、每次匯入最長 20 分鐘。可在 `installation` 列中以 `client_data_upload_bytes`、`client_data_extracted_bytes`、`client_data_file_limit`、`client_data_copy_limit`、`client_data_storage_bytes` 與 `client_data_import_seconds` 調整。再次匯入不會刪除先前成功的副本。

**檢查是否成功：** 助理報告安裝完成，WebUI 切換到管理介面。即使還沒有管理員，下文所述的 `/health` 檢查在助理執行期間也會正常回應。

## 檢查安裝

請在原始碼建置的儲存庫根目錄執行以下命令。它們檢查不同層面，建議三條都執行。

### 1. 列出隨附的 profile

```powershell
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- profiles list
```

命令會為每個隨附的 profile 輸出一行：id 與描述。本版本列出 19 個，其中包括 `setup`（“Database-independent first-run installation and administration.”）與 `management`（“Administration and durable storage only; no game or relay ports.”）。這證明 profile 檔案可以讀取並參與組合；它不能證明任何模組已經啟用。

### 2. 列出找到的模組

```powershell
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- plugins list
```

命令會為每個找到的模組定義輸出一行：模組 id、版本、支援的用戶端版本以及載入來源。這證明 Host 旁的模組目錄可以被找到；它不能證明某個 profile 的列會啟用。

### 3. 組合 profile 但不啟動

```powershell
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- run --profile kr-812 --patch tools/local-checks/isolated-memory.patch.yaml --dump-config
```

這條命令要求已設定 `BFF_MYSQL_PASSWORD`，因為組合 MySQL 列時會插值 `${BFF_MYSQL_PASSWORD}`，之後最後的修補檔才會停用該列。如果還沒有準備，請依[快速入門](quickstart.md)建立 `.env`。

命令以 YAML 輸出生效的列清單，每一列是一個以 `- id:` 開頭的區塊，然後以結束碼 0 結束。請確認以下內容：

- `persistence.mysql` 列帶有 `disabled: true`；
- `persistence.memory` 列沒有 `disabled: true`，即已啟用；
- `clientdata` 列顯示 `kr-812` 解析後的 `data_directory` 與 `raw_directory`。

此匯出證明組合、插值與修補檔順序：命令列修補檔最後套用，會覆寫開發者 `.env` 或本機修補檔中指向真實資料庫的設定。它不會啟動監聽，因此不能證明啟用或用戶端資料。

::: warning
當修補檔啟用 MySQL 時，設定匯出可能包含插值後的值，包括憑證。請把匯出視為敏感內容，不要貼到聊天或問題中。
:::

### 安裝助理路徑的瀏覽器檢查

助理在預設位址執行時：

| 檢查 | 命令 | 應看到的內容 |
| --- | --- | --- |
| 健康檢查 | `Invoke-RestMethod http://127.0.0.1:8080/health` | `status` = `ok` 以及 UTC 時間戳 |
| 安裝狀態 | `Invoke-RestMethod http://127.0.0.1:8080/api/setup/status` | 包含 `phase`、`installed`、`profiles` 與匯入限制的 JSON |

在 Linux 上使用 `curl -s http://127.0.0.1:8080/health` 與 `curl -s http://127.0.0.1:8080/api/setup/status`。`/health` 與 WebUI 檔案始終開放；在管理員尚未建立時，`/api/plugins` 依設計傳回 401，因此請先透過助理建立管理員。

**檢查是否成功：** 三條原始碼建置命令都成功結束，瀏覽器檢查傳回上述 JSON。

## 各部分的位置

| 內容 | 位置 |
| --- | --- |
| 儲存庫取出 | 你複製到的目錄 |
| Host 建置輸出 | `src/BubbleFighter.Fantasy.Host/bin/Release/net10.0/` |
| 模組建置輸出 | `artifacts/plugins/<AssemblyName>/`（被 Git 忽略） |
| Host 旁的檔案 | `<host output>/profiles/`、`packets/`、`data/`、`bootstrap/`、`plugins/` |
| 安裝狀態 | 執行檔旁的 `state/`；`BFF_STATE_DIR` 或 `--state-dir` 可覆寫 |
| 環境變數檔案 | 儲存庫根目錄的 `.env`；被 Git 忽略；`start` 不讀取 |
| 記錄 | 主控台輸出；封包記錄寫入工作目錄下的 `logs/{date}/` |
| 用戶端資料 | `client-data/<build>/data` 或 `dataraw`；可用 `BFF_CLIENT_DATA` 覆寫 |
| Docker 資料 | `mysql-data` 磁碟區；用戶端資料以唯讀方式掛載 |

除 `start` 之外，每條命令都會先把儲存庫根目錄的 `.env` 套用到尚未設定的處理程序環境變數；已存在的環境變數優先，且值永遠不會寫入記錄。

## 停止與刪除

- **停止原始碼建置或助理：** 在終端機按 Ctrl+C。主控台會列印 `Shutdown complete.`，隨後處理程序結束。
- **停止 Docker：** 在前台 compose 命令中按 Ctrl+C，或執行 `docker compose -f deploy/docker/compose.yaml down`。只有確實要刪除資料庫磁碟區時才加 `-v`。
- **刪除安裝狀態：** 會移除儲存的安裝設定、管理員帳號、匯入的用戶端資料副本以及預設 SQLite 資料庫。此操作無法復原，請先停止伺服器。
- **刪除資料庫：** 會移除長期儲存的遊戲資料。請先備份，絕不要刪除玩家正在使用的線上資料庫。[部署](deployment.md)頁面說明了備份與升級。
- **刪除建置輸出：** 安全，重新執行建置命令即可恢復。

## 發生問題時

- **建置失敗：** 本儲存庫將警告視為錯誤。第一條 `error` 會指出專案與原因，修正後重新建置。參見[疑難排解](troubleshooting.md)。
- **相依於框架的建置無法啟動：** 缺少 ASP.NET Core Runtime 10。用 `dotnet --list-runtimes` 確認，然後安裝相同架構的執行階段。
- **連接埠已被占用：** 助理使用 8080 連接埠，遊戲 profile 使用自己的遊戲連接埠，例如 `kr-812` 的 7110。請在 `.env` 中設定 `BFF_ADMIN_PORT` 或該版本的連接埠變數，或停止占用連接埠的處理程序。
- **遊戲 profile 因用戶端資料失敗：** `clientdata` 列會報告 `data_directory` 與 `raw_directory` 都不存在。請提供相符的用戶端資料，或透過安裝助理匯入，然後重新啟動該 profile。參見[疑難排解](troubleshooting.md)。

## 下一步

繼續閱讀[快速入門](quickstart.md)，啟動一個隔離的本機伺服器並確認它正在執行；然後閱讀[伺服器設定](configuration.md)，選擇持續性方式、連接埠與 profile 調整。如果需要長期執行的服務、備份與升級，請繼續閱讀[部署](deployment.md)。用戶端資料始終需要單獨提供：本儲存庫不包含遊戲用戶端及其資料。配套的 Fantasy Tale Launcher 有獨立指南：[啟動器](launcher.md)。
