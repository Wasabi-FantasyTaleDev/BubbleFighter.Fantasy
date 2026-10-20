# 封包結構表

這個資料夾描述遊戲客戶端的封包長什麼樣。伺服器啟動時讀取，不編譯進程式：改檔案、重啟，伺服器就按新結構收發。

英文原文：[README.md](README.md)。

| 檔案 | 內容 |
|---|---|
| `builds.yaml` | 有哪些客戶端版本、各自以誰為基底、各版本自己的設定值。 |
| `room.yaml` | 房間相關結構：`SessionSetting`、`GameSetting`、`UserSlot`、`EquippedItemList`、`gspGotoRoom`。 |
| `room-details.yaml` | 已核對的房主變更請求／回覆、回合設定請求、房間背包通知和共享擴音器請求；分輪地圖綁定還要求具名的 game opcode 與 `builds.yaml` 中已核對的目標映射。 |
| `equipment-attributes.yaml` | 完整裝備清單中的原生 `ItemAttr` 與 `ItemExpiredTime` 元素布局。 |
| `cash-inventory.yaml` | 帶數量的現金收件箱清單與領取封包，包含 modern 的展示尾段；結構表不會啟用道具領取。 |
| `damagers.yaml` | 伺服器原樣轉發的回合內傷害體封包，例如 `SplashDamagerByDamager`（32900）以及 `packets/damagers.yaml` 中的七个生成封包。 |
| `score.yaml` | 計分板：`ScoreRow`，`gspGameBroadcastPlayerScoreData` 裡的一個座位。 |
| `score-reports.yaml` | 原生入站積分上報布局；表中有布局不代表啟用尚未審核的事件類型或 opcode 路由。 |
| `inventory.yaml` | `InventoryUpdate` 和 `InventorySlot`：S2C 801/802 的背包。 |
| `login.yaml` | `LoginReply`：legacy 的 S2C 登入回覆，包含早期客戶端差異。 |
| `weapon-presets.yaml` | legacy 武器預設請求、狀態與平行向量列表，以及 KR、CN、TW 元素寬度各異的 modern 更新布局；表中有布局不代表啟用預設功能。 |
| `customized-keys.yaml` | kr-812 與三個 modern 版本已核對的自訂按鍵更新和回覆；顯式啟用的 `player-settings` bundle 將其接入獨立作用域儲存與登入恢復。 |
| `user-stat.yaml` | `UserStat`（S2C `gspUserStat`，錢包和 FighterInfo），以及其中的 `FighterInfo`、`EloRating`。 |
| `fighter-info.yaml` | `FighterInfoRequest`、`FighterInfoReply`：legacy 版本與 kr-1256 家族（`FighterInfoReply@kr-1256`）玩家菜單的「查看資訊」（C2S `gcpReqFighterInfo`，S2C `gspRepFighterInfo`）。 |
| `clan.yaml` | 氏族（clan）game 連線的封包體：`ClanPassport`、`ClanMarkInfo`、`ClanGuildInfo`、`ClanAttendanceState`，以及空的氏族請求；以 kr-1256 的結構為主，並列 kr-812 的基底結構。 |
| `relay-connection.yaml` | 已核對的原生中繼地址、key、連接通知、加入請求和連接報告；存在封包表不代表已啟用中繼監聽或握手。 |
| `relay-validation.yaml` | 加入中繼後的原生校驗報告與簽名資料；CN2011 改用直接報告連接成功的順序。 |
| `relay-success.yaml` | 原生加入／場景成功的空消息體；不初始化場景種子表，也不啟用監聽器。 |
| 其他 `*.yaml` | 更多結構。這個資料夾裡的每個檔案都會被讀取，插件可以放自己的檔案。 |

每個檔案開頭都有 `version: 1`。這是檔案格式的版本，不是遊戲版本：伺服器遇到不認識的版本會拒絕啟動，所以以後格式改動時舊檔案不會被誤讀。

## 版本标识

版本使用 `<地区>-<wire version>` 标识，与 profile 目录对应。`builds.yaml` 记录结构继承关系和能力。继承结构或启用能力表示数据覆盖范围，不能证明真实客户端通过验收。部署端口由 profile 配置。

## 所有版本共用一張表

一個結構只寫一次，照基準版本（`kr-812`）的樣子，一個欄位一列，順序就是封包裡的順序：

```yaml
structs:
  SessionSetting:
    - session_state: u32
      offset: 4
    - room_id: u16                  # 房間號
      offset: 8
    - open_seats: u16
      offset: 44
      except: [id-400]              # id-400 沒有這個欄位
```

沒有標註的欄位，所有版本都有。一列可以標註四種情況：

| 寫法 | 意思 |
|---|---|
| `except: [a, b]` | 列出的版本沒有這個欄位。 |
| `only: [a]` | 只有列出的版本有這個欄位。 |
| `also: [a]` | 該版本的基底沒有這個欄位，但它自己又有。 |
| `a:` 後面接欄位列表 | 該版本在這個位置換成這些欄位。 |

```yaml
    - clan_name: wstr
      offset: 180
      id-400:
        - unknown_180: u32          # kr-812 是字串，這裡是 u32
          offset: 180
          default: 1000
```

對某個版本的標註，同樣適用於以它為基底的所有版本（誰以誰為基底寫在 `builds.yaml`），除非該列另外點名了更近的版本。所以一個完全沒被提到的版本就等於它的基底；新增一個只差三個欄位的版本，只要寫三處標註。

### 差異很大的時候

一個版本在某個結構裡差異很多時，把整個結構重寫一張，和共用表並列：

```yaml
  UserSlot@cn-324:
    - occupied: bool
    - closed: bool
    ...
```

以 `cn-324` 為基底的版本就改用這張表，標註方式相同。經驗法則：一個版本在一個結構裡需要超過八處左右的標註，就重寫。重寫的表是獨立的拷貝：共用表裡改了欄位名，這裡要手動跟著改。

## 在代碼裡使用結構

插件用 `PacketCodec.Write(tables, build, "UserStat", values)` 寫出一個結構，`values` 是欄位名到值的對應。沒有給值的欄位寫它的 `default`。巢狀結構從自己欄位名下的對應表取值，沒有的話就用父結構的同一個對應表，所以調用方可以直接填 `nickname`，不用知道它在 `FighterInfo` 裡面。表裡沒有列出的版本，使用它最近祖先的表（`PacketTables.NearestBuild`）。

入站的平面標量布局可用 `PacketCodec.ReadScalars`，按相同的版本繼承規則回傳欄位名與值。
它要求完整包體，拒絕尾隨位元組，並在解碼前限制字串長度（預設 1,024 字元）。
32 位元整數以有號整數保留原始位元。這個讀包器不支援巢狀結構、清單或固定陣列；
這些結構需使用 `PacketCodec.Read` 與經核對的封包表，不會嘗試猜測解析。

`PacketCodec.Read` 按相同的版本繼承規則讀取巢狀結構、帶計數的標量或結構清單，以及固定陣列。
回傳的巢狀欄位對應表與元素陣列也可交給表驅動寫包器。它要求完整包體，並拒絕非零的
`empty_list` 計數：出站占位清單不能證明入站元素布局。`PacketReadLimits` 預設每字串最多
1,024 字元、每清單最多 4,096 元素、整包最多 16,384 個欄位或元素、最多 16 層巢狀容器。
對應容器配置前會先檢查這些限制；標量清單所需位元組也必須能放進剩餘包體。
原生包體的 65,535 位元組上限始終有效。調用方可為已核對的消息指定更嚴格的限制與規範布林值。
能讀取結構不代表註冊了 opcode、啟用了能力或通過連線認證。

## 一個欄位

```yaml
- 名字: 型別          # 說明
  offset: 44
  default: 8
  vacant: 1
  menu: 8
```

只有第一行是必填的。

- **名字**決定伺服器往這個欄位填什麼。伺服器認得的名字（房間結構裡有 `nickname`、`user_number`、`room_id`、`title`、`mode` 等；完整清單是 `RoomFieldValue` 列舉的小寫底線形式，再加上 `seated`）會從房間資料填入。其他名字一律寫預設值。用途未知的欄位叫 `unknown_<位移>`；查明用途後再改名。名字只能用小寫字母、數字和底線，不能有空格，同一結構內不能重複。
- **型別**有 `u8`、`u16`、`u32`、`u64`、`bool`（4 位元組）、`f32`、`f64`、`wstr`（u32 字元數 + UTF-16LE）、`list<T>`（u32 個數 + 那麼多個 `T`）、`empty_list`（伺服器永遠送空的列表）、固定連續欄位如 `u32[49]`（同一種數字型別連續那麼多個，沒有個數前綴；用於客戶端逐個讀取、用途還不清楚的欄位，其中某個有了名字就把它拆出來），`optional<0x3A,Tail>`（一個標記位元組，只有它等於 `0x3A` 時後面才跟著結構 `Tail`；寫入時沒有給出對應的值就把欄位的 `default` 當作「不存在」標記寫出，讀取時保留這個標記位元組，所以讀回的記錄能原樣寫回），或另一個結構的名字。`ScoreRow[16]` 表示沒有個數前綴的十六個結構；供給值的長度必須完全相同，固定陣列最多 65,535 個元素。型別裡有 `<` 或 `[` 時要加引號。
- **offset**：客戶端讀取函式把這個欄位存進物件的位置。伺服器不使用它；它的用途是方便到反編譯裡查，`bff packets show` 也會印出來。在重寫的表裡或版本名底下，寫的是那個版本自己的位移。
- **default**：伺服器沒有自己的值時寫這個（沒寫就是 0 或空字串）。
- **vacant**：空座位時改寫這個。
- **menu**：主選單那個單人房間裡這個欄位的值，和對局房間不同時才寫：數字、另一個欄位的名字，或 `empty`（列表送空）。
- **註解**寫已知的用途和證據出處。

註解裡每個函式位址都要標明客戶端版本（`kr-812 sub_4196F0`），因為同一個位址在每個版本裡是不同的函式。

## 條件欄位

同一結構中較早出現的無號整數決定某欄位是否存在時，使用 `when<mask&0x01,f32>`。
十六進位遮罩採「任一位元命中」：`(mask & 0x01) != 0` 時才有這個欄位，不另寫標籤或存在旗標。

```yaml
  Delta:
    - mask: u8
    - x: 'when<mask&0x01,f32>'
    - name: 'when<mask&0x02,wstr>'
    - detail: 'when<mask&0x04,Detail>'
```

內層型別可以是純量（`u8`、`u16`、`u32`、`u64`、`bool`、`f32`、`f64`、`wstr`）或已存在的結構名。
巢狀結構使用自己的欄位判斷條件。列表、固定長度陣列、帶標籤的 optional 和另一層 when 不可直接
作為內層型別；客戶端布局有此需要時，先把它們放進具名結構。載入時逐一檢查每個版本：判斷來源必須
是前面的非條件 `u8`／`u16`／`u32`／`u64`，遮罩不可為零或超過來源寬度。版本覆寫移除了來源也會報錯。

兩種讀取器都不會在結果中加入缺席欄位的鍵；實際傳來的零仍保留，所以「沒有座標」不會變成「座標零」。
`ReadScalars` 支援條件純量；條件結構使用 `Read`。寫入器要求明確提供遮罩，且每個被選中的欄位都要有
非 null 的值；未選中的欄位可省略或給 null，但給任何非 null 值（含零）都會報錯。寫入器不會自行推算
遮罩，也不會為選中的欄位偷偷補預設值。來源保留原生位元，包括讀取器以有號 `int`／`long` 回傳的值。

`ReplaceStrings` 跳過缺席欄位並保留其餘原始位元組，可替換存在的條件字串，但拒絕新增遮罩未選中的字串。
`packets show`、`diff`、`json` 保留完整的 `when<...>` 型別。這項擴充仍用格式版本 1；舊版服務端載入時
會拒絕未知型別，不會誤讀資料。

六位元回歸案例依照 kr-1256 sub_10F4A10（讀取）和 kr-1256 sub_10F4930（寫入）的順序：三個四位元組
欄位，再三個單位元組欄位。新增格式支援不會啟用移動綁定或改變現有封包布局。

## 查看某一個版本

不需要在腦中把差異疊到基底上：

```sh
bff packets show id-400 UserSlot        # id-400 的這個結構，以及每個欄位來自哪個版本
bff packets diff kr-812 id-1809         # 兩個版本在所有結構上的差異
bff packets check                       # 每個版本、它的基底、為它寫了多少
bff packets json id-400                 # 一個版本的全部內容，JSON 格式，給腳本用
```

## 哪些改動要重新編譯

| 改動 | 要重新編譯嗎 |
|---|---|
| 某版本多一個或少一個欄位、順序或寬度不同 | 不用 |
| 改預設值；給未知欄位取一個伺服器不填值的名字 | 不用 |
| 新增一個和現有版本相近的版本 | 不用 |
| 原本填常數的欄位要改填真實資料 | 要：得有程式提供那個值 |
| 新的封包或新的行為 | 要 |

結構是資料，含義是程式。

原生功能支援寫在 `builds.yaml` 的 `capabilities` 下。基準版本宣告已核對的能力，後繼版本
繼承並只覆寫差異。方言在啟用功能前呼叫 `PacketTables.CapabilityOf(build, name)`；未知或
格式錯誤的值會報配置錯誤。能力開啟表示原生支援已有證據，不表示客戶端已實測通過。

## 從插件擴充

讀取器是程式庫 `BubbleFighter.Fantasy.Packets`。有自己結構的插件，在這個資料夾放一個檔案（任意 `.yaml` 檔名，開頭 `version: 1`，結構寫在 `structs:` 底下），然後這樣讀：

```csharp
var tables = PacketTables.Load(options.PacketsDirectory);
IReadOnlyList<PacketField> fields = tables.Resolve("id-400", "MyStructure");
```

`Resolve` 回傳某個版本的欄位（封包順序），每個欄位有名字、型別、預設值、位移和它來自哪個版本。讀取器接受任何結構名和任何 `list<...>` 型別；型別在線上怎麼編碼由寫它的插件決定。同一個結構名只能出現在一個檔案裡。

## 修改規則

- 欄位順序、寬度、預設值由客戶端讀取或寫入路徑確立。在結構的標題註解裡寫明讀取函式的位址和版本。
- 不要編造含義：用途沒被證明的欄位保持 `unknown_<位移>`。
- 會改變某個版本位元組的修改，要對照該版本的讀取函式核對，而且該結構的位元組長度測試必須仍然通過。

扁平标量读取器的 `requireCanonicalBooleans` 可选参数启用后只接受值为 0 或 1 的 bool32；默认保持原生非零即真的语义。

`PacketMenuProjection.Project` 将所选结构的 `menu` 标注应用到按字段名提供的源数据。数字标注覆盖同名源值；字段名标注读取覆盖前的源值。嵌套字典和列表由调用者分别投影后提供。缺少引用字段时报告配置错误，没有标注且未提供源值的字段保留表中默认值。

`builds.yaml` 中的 `room_setting_change_fields` 将原生地图修改请求的第三个字符串和前两个标志映射到已核对的偏移字段名。各构建继承基线，只覆盖不同字段名。房间方言在挂载类型化请求前校验映射键、目标类型及两个标志目标互不重复。

`room_projection_fields` 描述 modern 类型化等待房间的模式字段、按顺序使用的地图候选及普通房间 UI 门控覆盖规则。`clear_mask` 只从 `flags` 清除已核对的阻断位，保留分轮地图选择位及其他无关位；`clear_words` 和 `clear_booleans` 指定其余已核对的门控字段。绑定挂载前校验全部别名及类型。较晚构建若不存在继承映射中的字段，会报告错误，不会借用基线偏移。
