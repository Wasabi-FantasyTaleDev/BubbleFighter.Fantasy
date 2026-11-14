# 封包結構表

這個資料夾描述遊戲客戶端的封包長什麼樣。伺服器啟動時讀取，不編譯進程式：改檔案、重啟，伺服器就按新結構收發。

英文原文：[README.md](README.md)。

`tutorial_score_builds` 显式列出已核对的选关教程客户端，未列出的派生版本不继承启用状态。
`tutorial_score_help_field` 指定该版本 `ScoreRow` 中救援目标对应的原生四字节字段；
字段必须存在，且不能与泡封、击破字段相同。这些值仅影响已认证单人教程的计分更新，
不改变普通对局的计分或奖励。
`tutorial_score_npc_finish_fields` 将 `victim`、`scorer`、`auxiliary` 映射到已核对的八个四字节字段 NPC 爆破报告中的不同字段；
仅在角色引用均为零、受击席位为负值标记且其余辅助字均为零时使用该席位，
共享教程处理器仍核验当前单人玩家。未指定时只按角色引用归属。
继承的映射仅在 `tutorial_score_builds` 明确列出当前版本时生效。

| 檔案 | 內容 |
|---|---|
| `builds.yaml` | 有哪些客戶端版本、各自以誰為基底、各版本自己的設定值。 |
| `room.yaml` | 房間相關結構：`SessionSetting`、`GameSetting`、`UserSlot`、`EquippedItemList`、`gspGotoRoom`。 |
| `room-details.yaml` | 已核對的房主變更請求／回覆、回合設定請求、房間背包通知；分輪地圖綁定還要求具名的 game opcode 與 `builds.yaml` 中已核對的目標映射。 |
| `equipment-attributes.yaml` | 完整裝備清單中的原生 `ItemAttr` 與 `ItemExpiredTime` 元素布局。 |
| `cash-inventory.yaml` | 帶數量的現金收件箱清單與領取封包，包含 modern 的展示尾段；結構表不會啟用道具領取。 |
| `damagers.yaml` | 伺服器原樣轉發的回合內傷害體封包，例如 `SplashDamagerByDamager`（32900）以及 `packets/damagers.yaml` 中的七个生成封包。 |
| `weapon-master.yaml` | kr-1256 在回合模式 26 中分配武器所用的装备封包（S2C 1001，`EquipmentApply`），以及客户端多次死亡后发送的空复活请求（C2S 765，`WeaponMasterRevivalRequest`）。仅为布局；能力 `weapon_master` 决定是否使用。 |
| `recruit.yaml` | 共享擴音器請求與傳統招募訊息；排除尚未核實的現代招募格式。 |
| `plaza-recruitment.yaml` | 原生廣場招募開關與文字、現代版本的招募發布及分頁清單；入隊由共享隊伍邏輯處理。 |
| `round-feed-native.yaml` | 已核对字段顺序差异的原生战斗消息；封包表不会自行注册中继路由或启用对局。 |
| `map-scripts.yaml` | 三个现代版本的原生对象名字节串、显隐、可破坏物体生命值与不透明脚本序号；共享特效布局保留在 `final-zone.yaml`。 |
| `score.yaml` | 計分板：`ScoreRow`，`gspGameBroadcastPlayerScoreData` 裡的一個座位。 |
| `score-reports.yaml` | 原生入站積分上報布局；表中有布局不代表啟用尚未審核的事件類型或 opcode 路由。 |
| `inventory.yaml` | `InventoryUpdate` 和 `InventorySlot`：S2C 801/802 的背包。 |
| `login.yaml` | `LoginReply`：legacy 的 S2C 登入回覆，包含早期客戶端差異。 |
| `weapon-presets.yaml` | legacy 武器預設請求、狀態與平行向量列表，以及 KR、CN、TW 元素寬度各異的 modern 更新布局；表中有布局不代表啟用預設功能。 |
| `customized-keys.yaml` | kr-812 與三個 modern 版本已核對的自訂按鍵更新和回覆；`dialect.messages` 行的 `customized_keys` 選項將其接入獨立作用域儲存與登入恢復。 |
| `user-stat.yaml` | `UserStat`（S2C `gspUserStat`，錢包和 FighterInfo），以及其中的 `FighterInfo`、`EloRating`。 |
| `fighter-info.yaml` | `FighterInfoRequest`、`FighterInfoReply`：legacy 版本與 kr-1256 家族（`FighterInfoReply@kr-1256`）玩家菜單的「查看資訊」（C2S `gcpReqFighterInfo`，S2C `gspRepFighterInfo`）。 |
| `clan.yaml` | 氏族（clan）game 連線的封包體：`ClanPassport`、`ClanMarkInfo`、`ClanGuildInfo`、`ClanAttendanceState`，以及空的氏族請求；以 kr-1256 的結構為主，並列 kr-812 的基底結構。 |
| `udp-peers.yaml` | 原生 peer 发现、端点表、打洞控制及 relay 成员消息；布局不会启用监听或直接移动。 |
| `round-movement.yaml` | 原生完整及掩码移动、UDP 中继探测、身份、控制和头部布局，包含各版本身份宽度差异。 |
| `relay-connection.yaml` | 已核對的原生中繼地址、key、連接通知、加入請求和連接報告；存在封包表不代表已啟用中繼監聽或握手。 |
| `relay-latency.yaml` | 單一字值的原生中繼延遲請求與未知值回覆；存在封包表不代表已啟用被動回覆或客戶端取樣。 |
| `relay-validation.yaml` | 加入中繼後的原生校驗報告與簽名資料；CN2011 改用直接報告連接成功的順序。 |
| `relay-success.yaml` | 原生加入／場景成功的空消息體；不初始化場景種子表，也不啟用監聽器。 |
| 其他 `*.yaml` | 更多結構。這個資料夾裡的每個檔案都會被讀取，插件可以放自己的檔案。 |
| `dynamic-config-content.yml` | kr-1256 原生 DynamicConfig 各类别的内容；这是内容文件，`.yml` 后缀避免将其当作结构表读取，布局见 `dynamic-config.yaml`。 |
| `dynamic-config-cn-pets.yml` | cn-774 第 14 类的角色创建模板，仅在活动宠物服务提供冷却时间和里程显示上限时发布；这是内容文件，布局见 `dynamic-config.yaml`。 |

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

`PacketCodec.ReadPrefix` 從 `PacketReader` 當前位置讀取一個結構，保留外層剩餘資料。
調用前後的位置可用於取得原始位元組範圍，無需重新編碼。每次調用套用相同的結構樹限制；
外層完整封包仍須另外驗證，以限制整包的總量並拒絕尾隨位元組。

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

`relay_latency_builds` 明確列出被動中繼配接器可繫結原生請求／回覆組合的版本。
繼承單一字值的布局不會加入此名單，也不會啟用任一插件列。

`weapon_critical_seed_builds` 显式列出已审核种子消费路径、可在 `dialect.scene-seeds` 中
启用 `weapon_critical_bonuses` 的客户端；后继版本不会自动启用。
`weapon_critical_mode_fields` 将已审核模式映射到该模式的武器规格替换字段。
`weapon_critical_view_refresh` 说明局内装备视图是否重建已安装武器：CN324 保留已安装的规格，
其余四个已审核版本在各自原生功能开关下，以发布的穿戴集合和已接纳的房间覆盖值重建武器。
此选项默认关闭，已审核的 cn-324、cn-406、cn-404、cn-408 和 id-400 配置启用它。
所有正数模式保留普通武器及装备贡献；七条已映射的原生模式路径执行一次资源替换。
原生武器报告只修订报告者自己的主武器或副武器槽；已审核的效果触发会刷新原生持续时间，
到期后移除限时贡献。概率相同时只推进输入修订，不重装种子表或重置读取位置。
资源或开局装备缺失会在发布倒计时前报错。本地视图及预设发布完成后，当前房间执行器会用
实际发送的同一份不可变装备快照协调状态；预设页索引和未解释的页字段不作为已安装武器规格。
完成表示发送器成功，不代表客户端已经接收或应用。客户端自行计算这些贡献的版本被排除。

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

`builds.yaml` 中的 `play` 描述各构建的教程房与游戏房在仍由代码写出形状的封包上的差异。其子块为 `tutorial`（`old_map_key`、`select_kind`、`skip_enter`、`old_mode`、`wave_map_key`、`master_slot_packet`）、`wire`（`server_revives`、`pre_count_down_first`、`unchosen_map_key`、`unchosen_map_theme`、`spawn_group_applies_locally`、`unchosen_difficulty_mode`、`unchosen_difficulty_map_key`、`restate_left_room`、`plaza_entry_settle_ms`）与 `score`（`wide_report`、`live_score_packet`、`outcome_first`、`time_over_end`、`npc_team_outcome`、`rescue_credit`、`horde_stage_outcome`、`star_report` 及 `star_small_gained`、`star_big_gained`、`star_small_lost`、`star_big_lost` 四个星星上报种类），另有平铺键 `match_start`、`host_start`、`one_seat_waves`、`npc_waves`、`score_reports` 与 `tutorial_skip_start`。旧版房间方言通过 `PlayValues.Of` 读取，启动时拒绝未知键、不属于该键名称的取值，以及缺失的 `match_start` 或 `host_start`。

`play.score.zombie_result_cause` 让已核对的僵尸结果横幅使用全体接收者共享的终局原因值，而不是各接收者的奖励胜负值。默认关闭；CN2012 启用，尚未核对消费路径的派生构建显式关闭。此选项不改变奖励、血量或结算时序。

`room_projection_fields` 描述 modern 类型化等待房间的模式字段、按顺序使用的地图候选及普通房间 UI 门控覆盖规则。`clear_mask` 只从 `flags` 清除已核对的阻断位，保留分轮地图选择位及其他无关位；`clear_words` 和 `clear_booleans` 指定其余已核对的门控字段。绑定挂载前校验全部别名及类型。较晚构建若不存在继承映射中的字段，会报告错误，不会借用基线偏移。

`room_unchosen_map` 内的空值条目用于取消继承的字段，适用于后代版本重新定义 `GameSetting` 的情况；其余条目仍按后代版本的布局校验。

`round_score_row_marker` 指定 modern 结算适配器为已入座记录填写的 `ScoreRow` 首字段，必须是该行开头的 `u32` 字段；这个别名不为未知字段赋予未经恢复的含义。等级和奖励使用共享字段名，线上顺序由各自封包表决定。结算尾部的条件字符串通过该表显式提供的默认门控值保持缺省。

`live_score_kinds` 将 `score`、`knockdowns`、`kills`、`deaths`、`stars`、`collected_lucci` 映射为实时席位向量中的原生单字节编号。六个键均须存在；后两项为可选计数，尚未核对原生投影时可显式设为 `null`，计数在获准发布后必须有对应编号，否则报错。编号必须互不相同。拾取 Lucci 的既有版本名单和 StarTeam 专用发布规则仍分别控制各自的路径；此映射不会启用发送路由或改变游戏计分规则。
现有 modern 结算发送器会在第一队总分帧中发布发生变更席位的普通战斗计数，随后发送第二队总分，不重复席位向量。KR1256 的 WeaponMaster 进度附在战斗计数之后。持有星数仍由单独获准的版本和模式投影处理，其他星数不会进入普通战斗向量。


`builds.yaml` 中的 `inventory` 决定共享背包服务如何发布各版本的背包。`envelope` 为 `plain` 或 `zlib`；`zlib` 表示一个 `u32` 压缩字节数，后接一条 zlib 数据流。`article_envelope` 单独选择 Article 列表的封装，省略时使用 `envelope`。kr-1256 的普通背包和 Article 都压缩；cn-774 普通背包不压缩而 Article 压缩；tw-454 两者均不压缩。现金背包始终不压缩。各版本读取函数及 CN/TW 覆盖值记录在 `inventory.yaml` 和 `builds.yaml`。

`rows` 为 `ranked`（传统基线：按物品编号升序，完整发布时省略空行，天数相对于连接的时间基准）或 `mirror`（保留存储顺序并使用现代槽位字段）。`replaced`、`purchase` 和 `changed` 按顺序列出发布内容：`bag` 为角色背包的完整包，`slots` 为角色背包的槽位包，`equipped` 为装备背包的槽位包，`changed` 为变更行的槽位包，`articles` 为包含穿戴标志与展示属性的完整已拥有 Article 表，`cash` 为空现金背包。现代普通槽位列表不重复包含已由 Article 表示的行。服务按名称解析各封包，所选版本缺少某个列表项的封包时会报错。

`inventory.ordinary_expiry_builds` 明确列出将普通限时所有权发布为相对天数双精度位模式的 `mirror` 版本。kr-1256、cn-774 和 tw-454 使用持久化的 UTC 到期时间；功能提供独立展示期限时，优先使用该期限。每次普通背包全量发布（包括购买后的重发）都建立新的时间基准，纯增量沿用此前基准；尚未全量发布时拒绝发送计时值。派生版本只有被明确列名才会启用。永久、未分类和生命次数物品沿用既有表示。Article 条目仍走独立列表，不通过此设置获得所有权计时。一次发布会验证有界条目并完成全部帧的编码，然后才推进时间基准并发送首帧。

`dynamic_config.pet_configuration_builds` 明确列出第 14 类能够发布两项宠物设置的版本；派生版本只有被明确列名才会启用，不能仅靠继承列表。`pet_content_file` 可在没有普通 `content_file` 时提供第 14 类模板。CN 模板仅在当前协议的宠物服务提供完整的冷却时间和里程显示上限时发布。TW 清空宠物版本列表与模板，改用原生 PetConfig 文件。

`builds.yaml` 的 `round_feed` 按动作标识配置原生 Opcode 名称、封包 `structure`、
报告者身份字段（`actors`）及可选的发送者回显（`echo_sender`）。嵌套配置可以继承，
其他版本仅覆盖差异属性；不存在的动作通过 `enabled: false` 显式关闭。这些协议数据不代表
该版本已支持完整对局。游戏 C2S、中继 C2S、中继 S2C 分别从各自连接的表中查找，不假定编号相等。
缺失名称、重复编号、与既有动作族冲突或身份字段不是完整整数字时，在发布服务之前报错。

可选的 `life_event` 和 `life_actor` 将已验证的原生消息转换为共享生命处理器的事件。
可选的 `builds` 列表将路由限制在明确审核的版本内，后代版本继承配置时也必须列名。
未列出的版本继续使用其余路由或既有透传策略。列表必须非空，并且只包含互不重复的已知版本名称。
`finished` 还必须提供 `life_initiator`。对于 `bubbled` 或 `died`，可选的
`life_initiator` 指定攻击角色；对于 `released`，则指定救援者。基础通知省略此字段，
不产生归属计分。共享对局逻辑对每次原生泡泡、完成救援和死亡仅计分一次。
`life_success` 控制报告是否成功，
`life_death_flag` 区分直接死亡与进入泡泡。这些配置必须指向所选结构中类型正确的字段。
`nested: true` 使用完整封包表读取器，并校验原生复活确认的缺省标签。
基线动作的结构和报告者证据记录在 `round-feed.yaml`；伤害体创建消息和仅有写入证据的中继移动消息
继续使用其各自已记录的处理方式。

`one_kill: request|confirmation` 为具备已核对 `native_one_kill` 能力的版本附加类型化参与者信息。
请求由攻击者报告，确认由受害者报告。两者都使用标量结构，不向发送者回显，也不附加独立的
`life_event`；房间先验证当前参与者与报告者归属，再处理已确认的死亡。部署时需要在 `ingame`
开启默认关闭的 `native_one_kill`，并提供 FinalZone 交换流程。KR1256 的可选
`one-kill.patch.yaml` 同时开启这两项，供真实客户端验收。

`star_score_builds` 将已核对的星星投影限制在明确列出的版本；`star_score_modes` 选择适用模式，
`star_score_team_modes` 选择其中需要发布独立团队星星总数的模式。`star_live_stat_kind` 与
`star_live_team_total_field` 描述实时更新；`star_scoreboard_count_field` 与
`star_scoreboard_team_total_field` 在完整记分板替换时保留同一状态。首个映射仅覆盖 KR1256
模式 6，派生版本与其他模式沿用既有投影。`star_report_slot_builds` 单独列出已核对报告席位
含义的版本，避免客户端为已离场玩家发送的清理报告改变仍在场报告者自身的数量。
