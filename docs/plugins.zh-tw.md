---
title: "模組"
description: "了解模組模型，並建置、掛載、設定及重新載入原生 .NET 模組。"
updated: 2026-10-23
---

伺服器的一切行為都由模組實作。profile 組合模組資料列，每個資料列把一個模組實作與一份設定綁定到穩定的執行個體 id，Host 則依各列宣告的服務相依性可被滿足的順序啟用這些資料列。本頁說明這個模型，示範如何查看 Host 發現並載入了什麼，然後從專案檔到可執行的資料列，完整走過第一個原生 .NET 模組。最後說明設定、生命週期與重新載入、原生模組與選用語言 SDK 的界線、模組作者必須遵守的規則，以及最可能遇到的故障。

**本頁適合：** 能夠建置解決方案、閱讀 C# 與 YAML，並希望在不更動伺服器核心的前提下加入行為的開發者。

## 開始之前

- 已完成[安裝](installation.md)：`dotnet build BubbleFighter.Fantasy.sln -c Release` 報告零警告、零錯誤。
- 終端機位於儲存庫根目錄；除步驟另有說明外，本頁命令都在此執行。
- .NET 10 SDK 可用：`dotnet --version` 輸出 10.x 版本。
- 執行步驟需要一份 `.env` 檔案，其中 `BFF_MYSQL_PASSWORD` 為非空的臨時值。為什麼即使停用 MySQL 也需要該變數，見[快速入門](quickstart.md)。
- 本機檢查修補檔 `tools/local-checks/isolated-memory.patch.yaml` 存在。本頁所有本機執行都會套用它，因此不會接觸真實資料庫。
- 首次執行使用的 `management` profile 只提供管理功能，不需要用戶端資料。`kr-812` 這類遊戲 profile 還需要該版本對應的用戶端資料。

## 模組模型

模組是標記 `[Plugin("plugin.id")]` 並實作 `BubbleFighter.Fantasy.Abstractions` 中 `IPlugin` 的類別。屬性的 id 就是資料列引用的**模組 id**。profile 提供**模組資料列**，每個資料列的 `id` 是**資料列 id**，也就是組合後 profile 內唯一的執行個體識別。同一個實作可以掛載多次：`kr-1256` bundle 把 `net.tcp` 模組掛載為 `game-listener` 與 `relay-listener` 兩個資料列，`all` profile 則為每個版本的資料列加上 `<build>.<資料列 id>` 前綴。

模組彼此看不到對方的類別，只透過共用介面類型發布與取得服務。這些介面位於 Host 只載入一次、並共用給所有模組的兩個組件：`BubbleFighter.Fantasy.Abstractions`（`IPlugin`、`IServiceRegistry`、`IEventBus`、`IMessageRouter` 等核心契約）與 `BubbleFighter.Fantasy.Contracts`（訊息記錄、領域服務等遊戲契約）。資料列宣告自己需要什麼、發布什麼，Host 只在其相依性已註冊後啟用該資料列，因此啟用順序由服務可用性決定，而不是 YAML 中資料列的先後。

| 宣告 | 意義 |
| --- | --- |
| `Requires` | 本列啟用前必須已註冊的單例服務 |
| `Optional` | 可以缺席的服務；已選取的提供者參與卸載與重新載入順序 |
| `Provides`、`ConditionalProvides` | 本列在啟用時必須發布、或在某些設定下可以發布的服務 |
| `ProvidesMany`、`RequiresMany`、`OptionalMany` | 有序擴充點：發布、要求至少一個、或允許空清單 |
| `Decorates` | 包裹透過 `RegisterDecoratable` 明確允許裝飾的單例的裝飾層 |
| `Overrides`、`Middleware` | 型別化訊息處理常式的替換與包裝，寫成 `rowId:完整限定訊息型別` |
| `SupportedBuilds`、`SupportedFamilies` | 針對資料列所屬 profile 的用戶端版本相容性檢查 |

註冊只有被擁有之後才算完成。`IPluginContext.Effect(IDisposable)` 與 `Effect(Action)` 註冊一個清理器，Host 在清理時依反向順序執行；`AcquireEffectAsync` 在非同步工廠執行前預留名額；`CreateResourceScopeAsync` 建立子資源生命週期；`RunTaskAsync` 啟動由 Host 監督完成與取消的背景工作。透過 context 的 `Services` 與 `Events` 檢視完成的服務註冊、事件訂閱與訊息路由會被自動追蹤，但透過其他途徑取得的資源仍必須明確交給 effect。每個資料列擁有有界的 effect 容量（`lifecycle.effect_capacity`，預設 4096），由本次啟用及其資源範圍共用，因此洩漏註冊的模組最終會失敗，而不是耗盡整個處理程序。

## 查看已載入的內容

### 1. 列出模組定義

```powershell
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- plugins list --log-level Error
```

`plugins list` 啟動 Host 的定義引導流程，在模組根目錄下發現原生模組，並為每個被接受的定義輸出一行以 Tab 分隔的內容：

```text
<模組 id>	<版本>	<逗號分隔的支援版本>	<來源>
```

第三欄為空表示模組與版本無關（例如 `loader.native`）；宣告限制版本時會顯示 `kr:1256` 這類區間。第四欄是定義來源：由 Host 自身攜帶的定義顯示組件名，在根目錄下發現的程式模組顯示模組目錄。`--log-level Error` 可讓啟用記錄不進入清單；否則啟用訊息與清單共用標準輸出。`--plugin-root <dir>` 可重複指定，一旦出現就取代預設根目錄清單；預設根目錄是 Host 可執行檔旁的 `plugins` 目錄（對於 `dotnet run` 建置，即 `src/BubbleFighter.Fantasy.Host/bin/Release/net10.0/` 下的建置輸出）。

**檢查是否成功：** 命令以代碼 0 結束，且你的模組 id 以預期的版本與版本區間出現。該命令只列出 Host 可以掛載的定義，不會啟用任何資料列。

### 2. 讀取執行階段狀態

執行中的伺服器在 `GET /api/plugins` 暴露資料列執行個體：

```powershell
Invoke-RestMethod http://127.0.0.1:8080/api/plugins | Select-Object rowId, plugin, state, error
```

```sh
curl -s http://127.0.0.1:8080/api/plugins
```

每個項目包含 `rowId`、`plugin`、`source`、`state`、`error`、`codeRefreshPolicy`、`codeLifetime`、`restartRequiredForReclamation`、`isRetiring`、`overrides`、`version`、`contractVersions` 與 `supportedBuilds`。狀態以字串寫出，取自 `PluginState` 列舉。在以 `start` 啟動的伺服器上，`/api/plugins` 需要已登入的管理員工作階段；`run --profile` 伺服器在回送上不需登入即可回應，詳見[快速入門](quickstart.md)。

### 3. 了解模組組件的位置

建置後的 Host 把模組根目錄下的每個子目錄當成一個原生程式模組。規則固定：對於根目錄 `<root>`，目錄 `<root>/<Name>` 是一個程式模組，其主要組件必須是 `<root>/<Name>/<Name>.dll`，該程式模組的其他相依套件放在同一目錄。儲存庫建置正好產生這種配置：每個模組專案的建置輸出鏡像到 `artifacts/plugins/<AssemblyName>/`，Host 建置再用這些鏡像資料夾取代輸出的 `plugins` 目錄。發佈腳本產生 `<發佈目錄>/plugins/<專案名>/`，結構相同。

Host 從自己的預設內容提供 `BubbleFighter.Fantasy.Abstractions`、`BubbleFighter.Fantasy.Contracts` 與記錄抽象組件，因此這些組件不能複製到模組目錄；儲存庫的模組建置目標會自動移除它們。模組執行階段需要的其他所有內容都必須位於自己的程式模組目錄內。

## 撰寫第一個原生模組

### 1. 建立專案

建立 `src/Plugins/BubbleFighter.Fantasy.Heartbeat/BubbleFighter.Fantasy.Heartbeat.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <Description>Logs one heartbeat line per activation.</Description>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\BubbleFighter.Fantasy.Abstractions\BubbleFighter.Fantasy.Abstractions.csproj" />
    <ProjectReference Include="..\..\BubbleFighter.Fantasy.Contracts\BubbleFighter.Fantasy.Contracts.csproj" />
  </ItemGroup>
</Project>
```

儲存庫根目錄的 `Directory.Build.props` 提供 `net10.0`、可為空值參考與警告即錯誤，`src/Plugins/Directory.Build.targets` 加入 ASP.NET Core 架構參考、剔除 Host 共用組件並把建置鏡像到 `artifacts/plugins/`。儲存庫外的模組應放在 `<root>/<Name>/` 資料夾中，DLL 位於 `<root>/<Name>/<Name>.dll`，並用 `--plugin-root` 把 Host 指向 `<root>`。

### 2. 撰寫模組類別

建立 `src/Plugins/BubbleFighter.Fantasy.Heartbeat/HeartbeatPlugin.cs`：

```csharp
using System.ComponentModel.DataAnnotations;
using BubbleFighter.Fantasy.Abstractions;
using Microsoft.Extensions.Logging;

namespace Example.Heartbeat;

[Plugin("example.heartbeat", Description = "Logs one heartbeat line per activation.")]
public sealed class HeartbeatPlugin : IPlugin
{
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<HeartbeatOptions>();
        context.Logger.LogInformation("Heartbeat every {IntervalSeconds} s.", options.IntervalSeconds);
        context.Effect(() => context.Logger.LogInformation("Heartbeat stopped."));
        return ValueTask.CompletedTask;
    }
}

public sealed class HeartbeatOptions
{
    [Range(1, 3600)]
    public int IntervalSeconds { get; set; } = 30;
}
```

`Config.Bind<T>()` 讀取資料列的 `config` 對應，把 snake_case 鍵轉為 PascalCase 屬性，執行 DataAnnotations 驗證，並拋出一個列出全部失敗的 `ConfigException`。傳給 `Effect` 的 lambda 在清理時執行一次，此時資料列的生命週期權杖已經取消；在真實模組中應先取得資源，然後立即註冊其清理器。

### 3. 建置

```powershell
dotnet build BubbleFighter.Fantasy.sln -c Release
```

建置成功會輸出 `0 Warning(s)` 與 `0 Error(s)`。模組產生到 `artifacts/plugins/BubbleFighter.Fantasy.Heartbeat/`，且由於 Host 建置會複製該鏡像，也會出現在 Host 輸出的 `plugins/BubbleFighter.Fantasy.Heartbeat/`。

**檢查是否成功：** 定義現在可以被發現。不必重新建置 Host，只要把清單指向產物鏡像：

```powershell
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- plugins list --plugin-root artifacts/plugins --log-level Error
```

輸出中應有一行第一欄為 `example.heartbeat`。

### 4. 用資料列掛載

用修補檔加入資料列，因此不更動隨儲存庫發佈的 profile。建立 `artifacts/heartbeat.patch.yaml`：

```yaml
- insert:
    - id: example.heartbeat
      plugin: example.heartbeat
      config:
        interval_seconds: 15
```

`insert` 追加資料列；其他修補操作是 `replace`、`set` 與 `remove`。先套用隔離記憶體修補檔，最後套用 heartbeat 修補檔，啟動僅管理用途的 profile：

```powershell
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- run --profile management --patch tools/local-checks/isolated-memory.patch.yaml --patch artifacts/heartbeat.patch.yaml
```

Host 掛載並啟用該資料列。預期輸出包含下面這種形狀的資料列，隨後是模組自己的訊息與作用中資料列數：

```text
Row 'example.heartbeat' activated (plugin 'example.heartbeat' version 1.0.0+<commit>; contracts ...; builds independent).
Heartbeat every 15 s.
```

**檢查是否成功：** 啟用資料列寫明你的資料列與模組，模組記錄訊息出現，且沒有任何資料列回報 `Failed` 或 `Pending`。`GET /api/plugins` 顯示 `example.heartbeat` 狀態為 `Active`。按 Ctrl+C 停止；主控台輸出 `Shutdown complete.`，heartbeat 的 effect 會在結束時執行。

### 5. 復原

刪除 `artifacts/heartbeat.patch.yaml` 並停止傳入它。刪除修補檔只移除執行個體；在你刪除程式模組目錄之前，定義仍可被發現。若同時刪除模組專案，請在重新建置 Host 前刪除 `artifacts/plugins/BubbleFighter.Fantasy.Heartbeat/`，因為 Host 會複製產物鏡像中存在的所有資料夾。

## 設定資料列

| 資料列鍵 | 用途 |
| --- | --- |
| `id` | 必填，組合後 profile 內唯一的執行個體 id |
| `plugin` | 必填，`[Plugin]` 宣告中的模組 id |
| `config` | 由模組繫結的對應；模組繫結鍵時執行驗證 |
| `disabled` | 為 `true` 時列出但不啟用該資料列；停用的資料列不做變數插值 |
| `version` | 發行版本限制，例如 `>=1.2 <2`，與定義版本比較 |
| `parent_id` | 執行階段父資料列：父資料列先啟用，並在子資料列清理完成後才釋放 |
| `isolated_services` | 為該資料列子樹分配全新服務命名空間的介面名稱 |
| `lifecycle` | 資源容量、清理時限與程式碼重新整理原則；見下一節 |

啟用資料列中，`config` 的值在組合時支援環境變數插值：`${NAME}` 要求變數存在，`${NAME:-default}` 提供預設值；變數缺少且沒有預設值時組合失敗，回報 `environment variable '<NAME>' is not set and has no default.`。`disabled: true` 的資料列永不啟用，因此其設定完全不做插值。資料列的 `version` 限制在啟用時檢查，失敗時回報 `Plugin '<id>' version <version> does not satisfy row '<row>' constraint '<constraint>'.`。

啟用順序由宣告服務的可用性決定。相依性尚未註冊的資料列保持 `Pending`，其 `error` 會列出等待內容，例如 `Waiting for services: <interface>; provider rows: <rows>; override rows: <rows>.`。啟動無法推進時伺服器回報 `Plugin activation stalled; the following rows have unsatisfiable service dependencies:`，隨後每個卡住的資料列各一行。模組需要服務時應先宣告，且只能透過 context 讀取；未宣告的存取會拋錯，而不會悄悄觀察到其他模組的實作。

## 生命週期與重新載入

| 狀態 | 意義 |
| --- | --- |
| `Active` | 資料列已啟用，其 effect 生效中 |
| `Pending` | 資料列是期望狀態，但所需服務或已選取的提供者尚不可用 |
| `Unloading` | 已關閉 effect 准入，資料列正在排空 |
| `Unloaded` | 資料列已停止，沒有生效的 effect |
| `Failed` | 啟用或擁有的工作失敗，資料列不持有 effect |
| `WorkFailed` | 擁有的背景工作失敗；資源在清理完成前仍被持有 |
| `CleanupIncomplete` | 清理超過觀察時限；所有權與相依性被保留 |
| `CleanupFailed` | 清理回報錯誤；拒絕重新啟動 |

卸載會關閉 effect 准入、取消本次啟用的 `Lifetime` 權杖、等待已接受的事件回呼結束，然後依反向順序清理 effect。只要仍有作用中的資料列相依於某資料列發布的服務，該資料列就不能卸載：Host 會拋 `Row '<id>' cannot unload; active row(s) depend on its services: ...`。

`POST /api/plugins/{rowId}/reload` 透過 `IPluginHost.ReloadAsync` 重新載入一個資料列。相依於該資料列服務的其他資料列先依啟用反向順序卸載，目標資料列重新載入，相依資料列再依原始順序重新啟用；回應包含重新載入的資料列 id。對從隔離程式模組載入的原生模組，重新載入也會從磁碟重新載入程式模組組件。契約中有兩個限制：

- `lifecycle.code_refresh_policy: restart_required` 拒絕就地重新整理程式碼，回報 `Row '<id>' requires a process restart for code refresh (lifecycle.code_refresh_policy=restart_required).`。對於無法安全重新整理的程式碼應使用該原則。
- 重新載入無法回收處理程序生命週期的程式碼。一般原生程式模組的 `codeLifetime` 為 `ProcessLifetime`，只有後端支援回收時才是 `Collectible`；需要重新啟動才能回收時 `restartRequiredForReclamation` 為 true。重新載入只在後端支援時重新整理程式碼。

失敗或逾時的清理絕不假裝完成。逾時讓資料列保持 `CleanupIncomplete`；錯誤讓資料列保持 `CleanupFailed`，該資料列保留其所有權與相依性租約，在清理底定前拒絕重新啟動。在 `lifecycle` 下設定每個資料列的限制：模組作者通常只涉及 `effect_capacity`（預設 4096）、`teardown_timeout_ms`（預設 10000）與 `code_refresh_policy`（預設 `allow`）。發布定義的資料列還要宣告啟動額度（`definition_source_reserve`、`definition_reserve`）；這些額度應保留在發布定義的資料列上，啟動後不要更動。

## 選擇原生 .NET 或語言 SDK

| 能力 | 原生 .NET 模組 | 選用語言 SDK 模組 |
| --- | --- | --- |
| 擴充面 | 所有核心與遊戲契約 | 型別化訊息、已宣告服務、裝飾器、有序集合、事件、工作、資源 |
| 載入方式 | 作為原生程式模組被發現 | `loader.process` 子處理程序或 `loader.wasm` 程式模組 |
| 契約存取 | 直接使用介面與型別 | 宣告的名稱依產生的契約清單解析 |
| 程式碼重新整理 | 依設定的後端重新載入程式模組 | 處理程序或程式模組執行到資料列或處理程序結束 |
| 工具鏈 | 僅 .NET 10 | Python、Node.js、Go 或 Rust，外加對應 loader |

.NET 仍是主要平台：SDK 介接相同的 Host 契約，也不重複實作遊戲邏輯，但只涵蓋其中一個子集。完整原生對等尚未實作。packet 編解碼器、原生連線與型別識別、委派、參考參數、共用契約組件之外的契約探索，以及接收原生呼叫端內容的提供者方法仍需專用橋接。契約清單把每個操作標記為可呼叫（`rpc_shape`）或可由 SDK 提供者實作（`provider_rpc_shape`），不支援的形式會明確失敗，而不會悄悄降級。

需要編解碼器、解碼層訊息 middleware 或橋接未建模的契約時，使用原生模組。希望用其他語言實作原則、遊戲邏輯或服務，且能力落在宣告範圍內時，使用 SDK 模組。[SDK 通訊協定](sdk-protocol.md)記錄通訊協定、各語言快速入門、契約清單與一致性測試執行器。

## 模組作者規則

- 不要硬編碼可調參數。把限制、間隔與識別項作為帶界限與驗證的資料列設定暴露；維運無法修改的值就是缺陷。
- 每個資源都要有界。佇列、快取、工作階段、處理常式表與背景工作都需要明確上限；在取得前預留 effect 容量，而不是事後才發現耗盡。
- 明確失敗。設定或所需狀態不對時在啟用中拋錯，絕不要吞掉失敗並以部分狀態繼續。Host 會把例外轉換為帶訊息的 `Failed` 資料列。
- 擁有你註冊的東西。取得資源後立即註冊 effect，絕不清理不屬於你的控制代碼；清理失敗要回報而不是隱藏，未完成的清理依設計保留所有權。
- 非同步工作使用具所有權的 API。`AcquireEffectAsync` 與 `RunTaskAsync` 提供取消與完成追蹤；裸的 fire-and-forget 工作會脫離資料列的生命週期，屬於洩漏。
- 保持生命週期回呼簡短。啟用、停用與清理器不得阻塞在無關工作上，也不得等待本資料列自身排空。
- 為公開型別與成員撰寫英文 XML 文件。解決方案以警告即錯誤的方式為所有專案產生文件，未寫文件的公開成員會導致建置失敗。
- 絕不隨模組散布共用契約組件的私有副本，模組碼也不得參照第三方專案。
- 把訊息與事件承載內容當作值。修改收到的物件不會發佈變更，還可能破壞其他模組的檢視。

## 疑難排解

| 症狀 | 檢查 | 可能原因與修正 |
| --- | --- | --- |
| `plugins list` 看不到模組 | 傳入的根目錄；資料夾與 DLL 名稱 | 程式模組目錄名必須等於組件檔案名主幹，且組件必須包含 `[Plugin]` 型別。壞程式模組會回報 `Plugin assembly '<path>' defines no [Plugin] type.` 或重複 id 錯誤。 |
| 資料列持續 `Pending` | `/api/plugins` 的 `error` 欄位與啟動記錄 | 缺少必要服務。閱讀 `Waiting for services: ...; provider rows: ...` 並啟用或修正提供者資料列；override 目標資料列也必須處於作用中狀態。 |
| 啟動失敗並回報 `references unknown plugin '<id>'` | 定義來源資料列是否已掛載並啟用 | 原生定義需要包含該程式模組的模組根目錄。SDK 定義需要在同一 profile 中有 `loader.process` 或 `loader.wasm` 資料列，且該 loader 資料列必須預留定義來源（`lifecycle.definition_source_reserve`）。 |
| `Declared service(s) were not published: ...` | 本應呼叫 `Services.Register` 的啟用程式碼 | `Provides` 宣告是承諾。在啟用中註冊服務，並把註冊交給 `Effect`。 |
| `Invalid configuration for row '<row>': unknown key '<key>'` | 資料列的 `config` 鍵與繫結選項型別 | 繫結拒絕未知鍵。重新命名鍵或在選項型別中接受它；訊息會在一個例外中列出全部失敗。 |
| `Duplicate row id '<id>' in profile '<profile>'` | `--dump-config` 組合出的資料列清單 | 兩個資料列共用同一 id，或 `all` profile 中版本範圍的資料列發生衝突。重新命名執行個體 id。 |
| 版本或版本家族不符 | 資料列的 `version`、profile 的 `client_build`、模組支援的區間 | 錯誤會寫明限制：`does not satisfy row ... constraint`、`does not support client build ...` 或 `Plugin requires BubbleFighter.Fantasy.Abstractions ... host provides ...`。讓模組與部署的契約保持一致。 |
| 資料列為 `Failed` 或清理未完成 | `error` 文字與模組的清理器 | 先讀第一個失敗；`CleanupIncomplete` 與 `CleanupFailed` 保留所有權並拒絕重新啟動，因此應修正失敗的清理器或取消路徑，而不是盲目重試。 |

如果整個伺服器在啟動時結束，訊息 `Server startup did not converge (<outcome>): <row>: <state> (<error>)` 會列出最先未啟用的資料列。先修正第一個 `Failed` 或 `Pending` 項目，其餘通常是連鎖結果。更多依症狀組織的指引見[疑難排解](troubleshooting.md)。

## 後續

繼續閱讀 [SDK 通訊協定](sdk-protocol.md)，掛載以 Python、TypeScript、Go 或 Rust 撰寫的模組；然後閱讀[參與貢獻](contributing.md)了解驗證關卡與審查規則。[伺服器設定](configuration.md)完整說明 profile、bundle、修補檔與環境變數。
## 註冊指令與權限 {#register-commands}

需要先通知再中斷連線時，`ISession.SendAsync` 只保證訊息入列。若工作階段實作 `ISessionOutputDrain`，應在關閉前使用該操作設定的取消期限等候 `DrainOutputAsync`。此屏障只等候先前的傳輸寫入完成，不確認用戶端收到或顯示訊息。取消只停止等候，不撤回已入列的訊息。佇列滿、工作階段停止或寫入失敗都會使等候失敗；不要用固定延遲或具體協定類別代替共用介面。

核心組合中的 `permissions`、`commands` 模組列提供原生指令基礎。模組在 `Requires` 宣告 `BubbleFighter.Fantasy.Contracts.Administration` 的 `IPermissionRegistry`、`ICommandRegistry`，先註冊權限節點，再註冊指令，並將兩份回傳控制代碼交給 `context.Effect(...)`：

```csharp
context.Effect(context.Services.Get<IPermissionRegistry>().Register(
    new PermissionDefinition("example.ping", "Check whether the example plugin is ready.")));
context.Effect(context.Services.Get<ICommandRegistry>().Register(context,
    new CommandDefinition("ping", ["ready"], "example.ping", "Check plugin readiness.", []),
    (invocation, token) => ValueTask.FromResult(
        new CommandResult(CommandStatus.Success, "Ready."))));
```

指令名稱和別名全域唯一，註冊時保存中繼資料快照。取消註冊會移除所有別名並通知執行中的工作取消；處理常式透過註冊者內容的受監管工作執行，卸載會取消並等待工作結束。處理常式必須回應取消權杖。一般狀態限制回傳 `InvalidState`；非預期例外向呼叫端傳遞並回報所屬模組。`ICommandRegistry.List(principal)` 回傳經權限篩選的說明與面板資料。

`ICommandExecutor.ExecuteAsync` 接收名稱及結構化位置參數。`ExecuteTextAsync` 識別 `/`、`!`、`！`，支援雙引號參數、帶正負號的整數與結尾的 `RemainingText`；結構化呼叫將正文作為一個參數傳入。正文沒有獨立字數限制。主控台配接器可明確允許無前綴輸入，不會執行 shell 文字。

只有可信配接器可以建立 `CommandCaller`：從驗證結果取得穩定帳戶 ID，代理操作保留原始 `Actor` 及實際 `Subject`，不可從請求參數還原這些身分。`Language` 使用使用者介面語言，空值表示使用部署預設語言。稽核事件包含身分與結果，不保存原始參數。原生模組可註冊指令；遠端 SDK 註冊仍需要回呼及生命週期配接。主控台輸入及 WebUI 尚未接入此分派器。

在 `ingame` 列的 config 設定 `shared_chat_commands: true`，將該遊戲範圍接入共用聊天指令。等待原生用戶端驗收期間，此項預設 `false`；關閉時保留原有僅房主可用的 `!start`、`!set`。啟用時必須同時提供兩項共用指令服務，否則啟用失敗。

核心組合的 `commands.game` 列將啟用的範圍接入同一分派器，也能呼叫原生模組註冊的指令。它在所有遊戲範圍中統一註冊 `/start`、`/mode <編號>`（別名 `/set`），也支援 `!`、`！` 前綴。`game.room.start`、`game.room.mode` 權限節點預設允許一般玩家使用，只操作自己所在的等候中房間；已設定的模式表仍會拒絕不支援的編號。指令開始會略過準備標記，原生開始按鈕仍要求房主操作。前綴輸入在權限拒絕後不會回退至本機處理常式。

遊戲模組透過 `IGameCommandEndpoints` 註冊 `IGameCommandEndpoint`，並持有回傳的取消註冊控制代碼。路由會核對已驗證遊戲帳戶與目前座位，拒絕重複的工作階段歸屬，並在遊戲模組的生命週期內執行。`commands.game.max_endpoints` 預設 512，用於約束同時註冊的範圍儲存。本列尚未提供地圖、管理或房間傳送指令。

取消註冊或替換遊戲範圍後，先前的路由快照即失效。尚未開始執行的呼叫不能繼續使用已撤銷的端點，也不能自動改用替換後的端點。

權限設定寫在 `permissions` 列：

```yaml
config:
  groups:
    - name: players
      allow: [example.ping]
    - name: helpers
      inherits: [players]
      deny: [example.restricted]
  subjects:
    - kind: game
      id: "42"
      groups: [helpers]
    - kind: game
      id: "7"
      operator: true
    - kind: webui
      id: "admin-account-id"
      allow: [webui.access, webui.admin]
```

ID 指向各自驗證空間內已有的帳戶；設定不會建立帳戶或登入憑證。一般身分的拒絕規則優先於個人、繼承及萬用字元授權。規則支援完整節點、`prefix.*` 及 `*`。不存在的群組及繼承循環會使啟用失敗；未註冊節點拒絕存取。遊戲 OP 及本機主控台擁有所有已註冊權限；OP 不授予 WebUI 登入資格。`webui.access` 只代表入口，`webui.admin` 代表 WebUI 超級管理員。權限不能跳過參數或業務狀態驗證。這些 WebUI 節點供後續整合使用，尚未取代現有 HTTP 授權。

資源設定保護登錄表儲存及執行中的工作，不限制遊戲功能。`commands` 支援 `max_commands`（512）、`max_input_characters`（4096，同時作為註冊名稱與參數定義文字預算）、`max_result_characters`（16384）、`max_concurrent`（64）。執行容量滿時立即回傳 `Busy`，不建立等候佇列。`permissions` 支援 `max_nodes`（2048）與 `max_policy_entries`（65536，帳戶、群組、繼承關係及展開規則的合計儲存預算）。設定經過驗證，可依部署調整。

### 線上管理指令 {#online-management-commands}

Contracts 2.61.0 新增原生線上玩家範圍註冊。`commands.management` 統一註冊 `/list`、`/notice <文字>`、`/disconnect <暱稱> [原因]`；`commands.players` 為各版本接入已驗證玩家目錄與指定通訊協定的通知編碼器。等待原生用戶端驗收期間，兩種模組列預設停用，啟用時須同時啟用管理列及所選版本的玩家列。玩家列位於 `lobby` 下，要求 `lobby.online_players: true`，必填 `protocol` 指定實際通訊協定。對應的 `dialect.messages` 還須啟用 `server_notices: true`；編碼器缺少或重複會使啟用失敗。組合設定的完整列 ID 以 `--dump-config` 為準。

`/list` 傳回去重後的線上帳號數，包含大廳玩家，不公開暱稱或網路位址。`game.management.list` 預設允許一般玩家使用。`/notice` 將輸入文字送給各版本的全部已驗證遊戲連線，報告排入佇列及失敗數量，要求 `game.management.notice`。`/disconnect` 要求 `game.management.disconnect`，精確比對目前暱稱，有多個符合的連線時拒絕執行。遊戲 OP 擁有這兩項管理權限；遊戲呼叫還會核對呼叫者的固定帳號是否仍持有目前連線。聊天入口需要 `ingame.shared_chat_commands: true`；主控台與 WebUI 入口尚未接入。

中斷通知依目標的 `launcher_language` 顯示，並包含選填原因。先將通知排入佇列、等候 `ISessionOutputDrain`，再關閉所選連線。通知失敗會記錄；目標仍有效時繼續中斷，不會追蹤並關閉重新登入的新連線。傳回結果會說明通知未完成。這不保證用戶端收到或顯示訊息；公告只報告排入佇列及部分失敗，不代表玩家已閱讀。

`commands.management` 支援 `max_scopes`（512 個註冊）、`disconnect_notice_timeout_ms`（5000）、`max_notice_characters`（511）。文字預算可調低，上限來自原生通知緩衝區。格式化後的原因過長時，在傳送或關閉前拒絕執行。範圍註冊由模組效果管理，操作在註冊者受監督的生命週期內執行。遠端 SDK 尚無原生擁有者與回呼傳輸介接，不能宣稱指令註冊已與原生等效。本批不包含 kick、sudo、OP 持久化編輯、房間傳送、WebUI 設定或分發商店。


### 用戶端策略 {#client-plugin-policy}

WebUI 可以編輯已安裝伺服器的[用戶端策略](launcher.zh-tw.md#client-policy)：商店來源與依模組的型別化規則。這些規則設定的是相容的啟動器，用戶端檔案不會安裝至伺服器。沿用設定檔時，伺服器使用所選設定檔中的策略；第一次儲存修改後切換為自訂策略，還原沿用設定檔則回到設定檔本身的策略。

所有路由都要求已登入的 WebUI 帳號同時擁有 `webui.access` 與 `webui.settings.client_policy`；`webui.admin` 可授予這兩項權限。遊戲 OP 不會取得 WebUI 存取權。

| 路由 | 作用 |
|---|---|
| `GET /api/setup/client-policy` | 回傳 `mode`（`profile` 或 `custom`）、`revision`、已儲存的 `policy`（沿用設定檔時為 null）以及 `active`，即啟動器目前收到的策略。 |
| `GET /api/setup/client-policy/rule-types` | 每種規則類型的 `name`、表單 `field`（`none`、`version` 或 `sources`）以及可能衝突的類型 `conflicts`。 |
| `PUT /api/setup/client-policy` | 取代整個策略：`{"policy":{"sources":[],"plugins":[]}}`，或以 `{"policy":null}` 回到設定檔的策略。 |
| `PUT /api/setup/client-policy/sources` | 只取代來源清單。 |
| `PUT /api/setup/client-policy/plugins/{id}` | 取代一個模組的項目 `{"id":"{id}","name":null,"description":null,"rules":[...]}`，其他模組不變。 |
| `DELETE /api/setup/client-policy/plugins/{id}` | 刪除一個模組的項目。 |

寫入使用現有的同源請求保護，且必須在 `If-Match` 中傳送所編輯的 `revision`。缺少時回傳 428；依據舊版本的寫入回傳 409 並附上目前的 `revision`，兩個同時開啟的編輯器不會互相覆寫。沿用設定檔時的部分寫入以目前生效的策略為基礎。被拒絕的策略回傳 400，包含 `error`（`client_policy_invalid`、`client_policy_unknown_type`、`client_policy_unknown_source`、`client_policy_unknown_build` 或 `client_policy_conflict`），並在可得知時提供 `plugin`、`rule` 與衝突的 `other` 規則。規則的版本必須是已設定的、`management` 以外的用戶端設定檔。

變更獨立於資料庫、帳號、獎勵及用戶端資料設定持久儲存，再使用現有受監督的設定套用流程。HTTP 202 僅表示受理。套用可能使遊戲服務重新連線，安裝狀態會顯示進度與失敗，可透過現有設定流程修正或重試。
