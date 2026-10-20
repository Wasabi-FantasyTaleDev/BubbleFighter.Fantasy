---
title: "參與貢獻"
description: "設定工具鏈、建置與測試、遵循專案規則，並提交可審查的改動。"
updated: 2026-10-18
---

## 本頁內容

本頁帶新貢獻者從一台乾淨的機器走到一個可審查、可合併的改動：先讀哪些規則、需要什麼工具鏈、建置與驗證命令、儲存庫結構、改動附帶的責任，以及審查流程。Bubble Fighter Fantasy 是由 Fantasy Tale Studio 維護的預發行社群伺服器，使用 C# 與 .NET 10 撰寫，並由模組組合而成。

**適用對象。** 首次貢獻的人，以及需要重現或審查他人改動的貢獻者。

**開始之前。**

- 閱讀儲存庫根目錄的 `AGENTS.md` 與 `CONTRIBUTING.md`。`AGENTS.md` 具有約束力，包含架構、packet 資料、證據、命名與語言規則。
- 安裝 .NET 10 SDK 與 Git；建置並執行伺服器需要這兩者。
- 真實遊戲用戶端及其資料只在原生用戶端測試時需要。儲存庫本身不包含它們。

## 先讀規則

| 規則 | 實際意義 |
| --- | --- |
| 一切都是模組 | 新能力以 profile 中的模組資料列形式交付，而不是核心或 Host 中的特例。 |
| Context 是服務登錄 | 只透過宣告的介面型別消費服務，不要存取其他模組的具體類別。 |
| 註冊即 effect | 每個 `Register*`/`On*` 都傳回可處置控制代碼，交給 `context.Effect(...)`，卸載時依相反順序回退。 |
| 不允許硬編碼可調參數 | 隨部署變化的值是模組資料列上經過驗證的設定欄位，而不是常數。 |
| 失敗要響亮 | 錯誤設定、缺少服務、畸形 packet 與連線失敗都是錯誤，絕不靜默使用預設值。 |
| 資源必須有界 | 不允許無界佇列、緩衝區或檔案寫入；溢位要回報，且不能拖住遊戲。 |
| packet 佈局是資料 | 結構寫在 `packets/` 中，以基線寫一次並依建置註解；程式碼依欄位名稱提供值。 |
| 證據來自用戶端 | 佈局與順序透過閱讀用戶端自身程式碼確定，每項結論保留帶建置前綴的函式位址。 |
| 程式碼使用英文 | 程式碼、註解、XML 文件、提交訊息、`opcodes.yaml` 與 `profiles/opcode-policy.md` 使用英文。 |
| 使用者文件雙語 | 英文指南與其中文翻譯在同一次提交中更新。站台頁面還需要繁體中文與韓文。 |

任何地方都不要使用 emoji。不要引用或提及第三方專案、其他伺服器或參考樹；用本專案自己的術語描述設計。

## 設定工具鏈

| 工具 | 用途 | 檢查 |
| --- | --- | --- |
| .NET 10 SDK | 建置與執行伺服器 | `dotnet --version` 顯示 10.x |
| Git | 複製、分支、驗證關卡 | `git --version` |
| Python 3 | CI 關卡指令碼及其單元測試 | `python --version` |
| Node.js 與 npm | 文件站、TypeScript SDK 一致性 | `node --version`、`npm --version` |
| Go | Go SDK 一致性檢查與測試 | `go version` |
| Rust 與 cargo、rustup，以及 `wasm32-wasip1` | Rust SDK 一致性與 WebAssembly 測試 | `rustc --version`；`rustup target add wasm32-wasip1` |

建置並執行伺服器只需要 .NET 與 Git。其餘工具鏈供驗證關卡、文件站與 SDK 測試套件使用；缺少時關卡會回報，而不是靜默略過。

在 PowerShell 或 shell 中，從應放置檢出的目錄執行：

```sh
git clone https://github.com/Wasabi-FantasyTaleDev/BubbleFighter.Fantasy.git
cd BubbleFighter.Fantasy
dotnet build BubbleFighter.Fantasy.sln -c Release
```

**確認成功。** 複製目錄包含 `BubbleFighter.Fantasy.sln`，建置以 `0 Warning(s)` 和 `0 Error(s)` 結束。

## 本機建置與測試

### 建置方案

```sh
dotnet build BubbleFighter.Fantasy.sln -c Release
```

警告即錯誤（`TreatWarningsAsErrors` 已啟用），且 `GenerateDocumentationFile` 要求每個公開型別與成員都有有意義的 XML 摘要。修正原因，絕不要為了讓建置變綠而停用警告。

### 執行測試專案

```sh
dotnet test BubbleFighter.Fantasy.sln -c Release
```

這是開發時的快速循環。發行關卡並不直接使用它：關卡以 `git ls-files tests/*/*.csproj` 發現每個被追蹤的測試專案（包括方案之外的），並逐一序列執行，每個專案一份 TRX。每次嘗試使用獨立的結果目錄或檔名，避免重跑覆蓋先前的失敗。

### 執行關卡自測

```sh
python -m unittest discover -s tools/ci -p "test_*.py"
```

這些測試保護關卡執行器本身，防止空報告、缺少報告或被略過的報告被誤報為成功。

### 執行公開關卡

```sh
python tools/ci/serial-gates.py --public --results-directory artifacts/validation/public-01
```

每次嘗試都使用新的、不存在的結果目錄——執行器拒絕已存在的目錄。它會建置 Release、執行 SDK 一致性檢查、逐一序列執行測試專案、檢查 packet 表，並傾印帶隔離修補檔的 `kr-1256` 設定。最後輸出 `PASS public gates: <n> populated test reports, <n> tests, no skips`，並把排除範圍記錄到 `scope.json`。公開模式執行每個被追蹤的測試專案，只排除 `Category=LocalClientData` 案例；它不是完整發行關卡。如果設定了 `CI=true`，執行器還會拒絕含有 `.env` 或 `profiles/*/local.patch.yaml` 的檢出。

**確認成功。** 結束碼為 0，最後一行是上面的 `PASS` 行。非零結束會印出 `FAIL <reason>`；失敗步驟的記錄在結果目錄中。

### 執行本機 Host 檢查

遵循[快速開始](quickstart.md)：將 `.env.example` 複製為 `.env`，把 `BFF_MYSQL_PASSWORD` 設為非空的臨時值，並且一律加上隔離修補檔——`--dump-config` 也一樣：

```sh
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- run --profile management --patch tools/local-checks/isolated-memory.patch.yaml
```

該修補檔停用 `persistence.mysql` 並啟用記憶體持久化，因此開發者的 `.env` 或本機覆寫不會連線真實資料庫。絕不要把測試或本機檢查指向生產資料庫。**確認成功：** 另一個終端機從 `http://127.0.0.1:8080/health` 得到 `{"status":"ok", ...}`；用 Ctrl+C 停止。

## 儲存庫結構

| 路徑 | 內容 |
| --- | --- |
| `src/BubbleFighter.Fantasy.Abstractions`、`src/BubbleFighter.Fantasy.Contracts` | 共用模組契約；由 Host 載入一次並共用給每個模組 |
| `src/BubbleFighter.Fantasy.Kernel` | 模組執行階段：探索、啟用、服務登錄、effect、profile 組合 |
| `src/BubbleFighter.Fantasy.Host` | `bff` 主控台 Host 及其 bootstrap profile |
| `src/Plugins/` | 監聽器、方言、遊戲功能、持久化、管理與 SDK loader |
| `profiles/` | profile 資料列、bundle、修補檔與逐建置 opcode 表 |
| `packets/` | packet 佈局資料與逐建置繼承 |
| `sdk/` | 可選的 Python、TypeScript、Go 與 Rust 模組 SDK |
| `tests/` | .NET 測試專案與共用 fixture |
| `tools/` | 驗證關卡（`tools/ci`）、本機檢查修補檔（`tools/local-checks`）與維護指令碼 |
| `docs/` | 已發布指南的來源 |
| `docs-site/` | VitePress 站台、頁面清單與內容同步 |
| `deploy/` | Docker 檔案與 Compose 範本 |

## 進行改動

### 分支與拆分工作

在自己的分支上工作，一次提交只做一個邏輯改動；改動經審查後才進入 `main`。提交訊息使用英文：改了什麼、為什麼改、有什麼證據支持。

```sh
git switch -c fix/short-topic
```

### 提供證據

- 隨改動新增或更新測試。packet 改動要加位元組釘住測試，做到改動前失敗、改動後通過。
- 先執行目標測試，再執行完整建置與測試。保留先前失敗嘗試的輸出，不要被重跑覆蓋。
- 依程式碼中的方式引用證據：wire 結論使用帶建置前綴的用戶端函式位址（`kr-812 sub_810C20`）。
- 在改動說明中寫明執行了什麼、能證明什麼；自動化測試不能證明真實用戶端相容性。

### 遵循模組與資源規則

- 以模組資料列新增能力：在 `[Plugin(...)]` 中宣告所需與提供的介面，透過 `IPluginContext` 消費，並且只透過它註冊。
- 每個註冊與外部資源都用 effect 持有，使卸載時可以回退；不要留下無界背景工作或佇列。
- 把隨部署變化的值放進該資料列經過驗證的選項類別（`[Required]`、`[Range]`、`IValidatableOptions`），而不是常數。
- 以共用契約組件建置；不要把 `Abstractions` 與 `Contracts` 的私有副本隨模組發布。
- 公開型別與成員都要有真實的 XML 摘要。註解說明原因，並指向證據。

### 修改 packet 或 opcode 資料

改動佈局前先讀 `packets/README.md` 與 `profiles/opcode-policy.md`。簡要規則：

- 每個結構以 `kr-812` 基線寫一次，一行一個具名欄位並標註用戶端位移；有差異的建置在該列上加註解（`except`、`only`、`also`，或自己的取代）。表能表達的內容，絕不要在程式碼裡加逐建置欄位表或 `if (build ...)`。
- 用途未經證實的欄位保持 `unknown_<offset>`；不要臆造含義。每個 `sub_` 位址都帶用戶端建置前綴。
- opcode 列記錄 `verified`、`inferred`、`unverified` 或 `observed-session` 證據等級與 `name_source`；未知入站 opcode 只寫入記錄，絕不樂觀解析。C2S 與 S2C、遊戲與 relay 連線是各自獨立的空間。
- 每個 codec 都需要位元組長度釘住測試；wire 改動在提交前要對照用戶端消費路徑檢查。
- 用 `bff packets show <build> <structure>`、`bff packets diff <build> <build>` 與 `bff packets check` 核對表。

## 文件改動

`docs/` 是已發布指南的來源；`docs-site/` 透過 `docs-site/pages.json` 中的明確頁面清單建置站台。每個已發布頁面有四個檔案——`X.md`（英文，權威版本）、`X.zh.md`、`X.zh-tw.md` 與 `X.ko.md`——每個檔案都需要包含 `title`、`description` 與 `updated` 日期的 front matter。

- 本機連結必須指向已發布的頁面來源，例如[設定伺服器](configuration.md)。外部 URL 與錨點可以保留。
- 不要編輯 `docs-site/content/` 下產生的內容，也不要提交建置產物；兩者都被忽略。
- 英文與中文指南要一起更新，並保持站台頁面四種翻譯同步。
- 在同一次提交中為兩份根目錄更新紀錄（`CHANGELOG.md` 與 `CHANGELOG.zh.md`）新增帶日期的條目；日期格式為 `YYYY-MM-DD`，註明時間時使用 Asia/Shanghai。新條目放在舊條目上方。

在站台目錄中建置：

```sh
cd docs-site
npm ci
npm test
npm run build
```

同步步驟在建置前執行，驗證中繼資料與連結。front matter 不完整時以 `Missing guide metadata: <path>` 失敗，缺少四個檔案之一時以 `Missing public guide: <path>` 失敗，本機連結未指向已發布頁面時以 `Unpublished local link: <target>` 失敗。可以參考其他已發布指南，例如[設定](configuration.md)與[模組](plugins.md)。

## 驗證等級

| 等級 | 執行內容 | 需要 | 證明什麼 |
| --- | --- | --- | --- |
| 快速 | `dotnet build` 與 `dotnet test` | .NET | 改動可編譯，.NET 測試套件通過 |
| 公開關卡 | `tools/ci/serial-gates.py --public` | .NET、Python、Node.js/npm、Go、Rust 與 `wasm32-wasip1` | 除本機資料案例外每個被追蹤測試專案、SDK 一致性、packet 表、隔離設定 |
| 完整關卡 | 同一命令去掉 `--public` | 下列本機用戶端工作簿 | 全部測試、無略過；這是發行關卡 |
| 發行審查 | 隔離啟動與受影響的真實用戶端流程 | 真實遊戲用戶端 | 用戶端驗收；任何自動檢查都無法替代 |

完整關卡需要以下本機取得的用戶端檔案；它們不隨儲存庫散布：

| 設定或路徑 | 必要檔案 |
| --- | --- |
| `client-data/cn-324/` | `Config/ShopItem2.xml` |
| `client-data/kr-1256/data/` | `Packs/Config_00000.pkn`（商店與角色工作簿）與 `Packs/ConfigSh_00000.pkn`（寵物疊加表） |
| `artifacts/shop-tables-survey/id400/xml/` | `Config/ShopItem2.xml` |

如果檔案缺少，完整關卡會列出缺少的前置條件並停止：`Local game data is missing for <tests>; provision it or pass --public to run the publicly reproducible gates only.`

如實回報驗證結果：

- 每次嘗試使用新的結果目錄；有內容的 TRX 必須與全部通過的計數一致，沒有失敗、空結果或略過結果。被略過的測試不算通過。
- 回報你實際執行的模式。公開關卡把排除範圍寫入 `scope.json`，它不是完整發行關卡。
- 不要刪除或改寫失敗結果。保留失敗嘗試的主控台記錄與 TRX。
- 說明結果來自自動檢查還是真實遊戲用戶端。

公開儲存庫工作流程在 GitHub 託管的 Windows 執行器上，為 `main` 的推送與 pull request 執行可公開重現的關卡；它不執行本機資料用例。有本機用戶端資料前置條件時，請在本機執行完整關卡，並讓 CI 檢出保持不含 `.env` 與本機 profile 修補檔。

## 可審查改動包含什麼

- 只做一件事的聚焦 diff，並帶有涵蓋所改行為的測試。
- 驗證證據：命令、結果，以及修正前曾有的失敗輸出。
- 改動所必需的資料與文件更新：wire 佈局的 packet 表、帶證據的 opcode 列、英文與中文指南更新、更新紀錄條目。
- 不包含無關的重格式化、產生檔案、建置產物、記錄或本機覆寫。

## 哪些會被拒絕

- 憑證、權杖、`.env` 檔案、本機 profile 修補檔，或出現在程式碼、文件、記錄中的任何機密。
- 私有調查材料、擷取、反編譯用戶端匯出或用戶端資源。
- 第三方程式碼、抄來的佈局，或提及其他專案與伺服器名稱的引用。
- 猜測的 packet 名稱或欄位含義，或基於 `unverified` 證據啟用的處理常式。
- 無界佇列、緩衝區或檔案寫入，以及用硬編碼代替經過驗證設定的部署值。
- 停用警告的建置，或被略過卻回報為通過的測試。
- 提交產生的文件內容、建置產物，或把未合併分支中的功能描述為可用。

## 提交流程

1. 推送你的分支並向 `main` 發起 pull request。
2. 說明行為改動、證據以及所執行的驗證模式；如果改動修正缺陷，附上失敗輸出。
3. 確認 diff 不含機密、私有材料或產生內容。
4. 用新的提交或清楚的解釋回應審查意見；保持歷史可審查。
5. 審查通過後合入 `main`。注意隨後一次序列驗證執行，因為工作流程在 `main` 上執行。

## 後續

- 專案的 GitHub 儲存庫是 README 記錄的入口：https://github.com/Wasabi-FantasyTaleDev/BubbleFighter.Fantasy 。儲存庫沒有記錄聊天伺服器或論壇。
- [設定](configuration.md) 與[模組](plugins.md) 介紹改動通常涉及的結構。
- [SDK 協定](sdk-protocol.md) 介紹可選語言 SDK，[疑難排解](troubleshooting.md) 幫助解決建置或關卡失敗。
