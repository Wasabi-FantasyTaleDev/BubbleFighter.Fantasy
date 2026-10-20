# 更新日志

日期采用 `YYYY-MM-DD`；注明时间时使用 Asia/Shanghai（UTC+08:00）。新条目追加在旧条目上方，中英文在同一提交更新。本公开日志从预发布准备开始，更早的开发记录内部保留。

## 尚未发布 — 2026-10-18

- 2026-10-20：发布程序包。`tools/build-release.py --version <x.y.z> --output <dir>` 以依赖框架方式为 win-x64、win-arm64、linux-x64 与 linux-arm64 发布 Host，检查每个可执行文件的机器类型，并确认不含运行时文件或运维文件（`.env`、`local.patch.yaml`、state、logs、客户端数据），`src`、`profiles`、`packets` 或 `data` 中存在未跟踪文件时拒绝构建；输出不含版本号的可复现压缩包（`bubblefighter-fantasy-<rid>.zip|.tar.gz`，因此 `releases/latest/download/<name>` 始终指向最新版本）、`SHA256SUMS` 与 `install.sh`。`deploy/install.sh` 将 Linux 程序包安装到 `releases/<tag>`，由 `current` 链接指向，`state/` 在升级时保留；它会校验校验和，缺少 ASP.NET Core Runtime 10 时在本地安装，切换前先列出新版本的 profile 进行检查，并生成 `bff` 启动器；`--service` 添加 systemd 单元，`--docker` 用该程序包构建本地镜像并只运行服务器（使用 SQLite 或已有数据库），`--with-mysql` 再加上一个 MySQL 8.4 容器（服务器使用主机网络，MySQL 只监听回环地址，密码生成在权限为 0600 的 `.env` 中，升级时保留），`--from <dir>` 离线安装，`--admin-port`/`--mysql-port` 修改端口。文档站点在 `/install.sh` 提供该脚本。在 Windows 上从自己的控制台窗口不带参数启动可执行文件时，现在会在默认浏览器中打开 WebUI（新的 `admin.http` 选项 `open_browser`，仅在这种情况下由 Host 设置；`setup.yaml` 读取 `BFF_OPEN_BROWSER`），失败后窗口保持打开。在 Windows 上，若程序包文件夹中最深的插件文件路径超过 258 个字符，`start` 会拒绝启动并提示移动文件夹，而不是因无法解析插件依赖而失败。
- 2026-10-20：KR2026（kr-1256）商店的六个回复体改为在新建的 `packets/shop.yaml` 中描述，不再只存在于手写 C# 中：`gspRepCheckCash`（三个字，第二个是现金余额，与 money-stat 分支写同一个字段）、`gspRepRealCouponUse`（三个字，第一个是结果；kr-812 读取完全相同的三个字，因此作为共享基线）、`gspRepShopBuy@kr-1256`（key、result、名称以及现代对话框读取的四个尾随字；legacy 回复仍是两个字）、`gspRepShopItemCardInfo`（result 加无条件的卡片，带默认卡片时 78 字节）、`gspRepEnterTCShop`（一个无人读取的字、has-data、token 以及总会读取的卡片向量，空体 20 字节）与 `gspRepEnterShop`（八个无条件段，含卡片与分类向量及尾部计数字列表，空体 40 字节），并包含嵌套的 `ShopItemCardInfo`、`Stock`、`ShopMainCategory` 与 `ShopSubCategory` 记录。`ShopReplyTableParityTests` 用 `PacketCodec` 写出每个结构，并与现行的 `ShopWire` 写入器逐字节比较至少三组输入，且为每个回复固定长度。数据包字节不变：`ShopWire` 仍是现行实现，没有任何回复被改接线。
- 2026-10-20：KR2026（kr-1256）商店的请求体现在写入新建的 `packets/shop-requests.yaml`，不再只存在于手写 C# 中；配套的平行测试用 `PacketCodec` 把同一批字节读回并与现行的 `ShopRequests` 解析器逐值比较，并要求两边都拒绝少一字节与多一字节的报文：`gcpEnterShop`（i64 token 后跟一个 4 字节字；legacy 构建只发 token 或空体，已在字段旁注明）、`gcpEnterTCShop_`（一个 i64 token；无 legacy 类）、`ShopPurchaseRequest@kr-1256`（key 与一个来源为单字节的字）、`gcpReqShopItemCardInfo`、`gcpReqRealCouponUse`（一个带长度前缀的宽字符串）、`Unknown834`（同一种宽字符串，客户端只会发出 44 字节这一种形式）与 `Unknown2059`（一个字节加一个字，5 字节）。没有请求被改接线路，报文字节不变，现行解析器仍在位。
- 2026-10-20：炸弹任务与队长模式的报文体现在写入新建的 `packets/mode-control.yaml`，内容来自 kr-812 的读取器与写入器：`gspBombState`（六个四字节字，第二个是处理器的状态分支键）、`gcpBombState`（五个）、`gcpBombMissionOver`（三个）、`gspObserver`、`gspCreateCaptain`、`gcpDropCaptain` 与 `gspCaptainEvent`（各一个字），并记录线序、对象偏移与已验证的方向（973、974、982、994 为接收，2011、2012、2018 为发送）。`profiles/kr-812/opcodes.yaml` 补上表中原先没有名字的两行：973 `gspBombState` 与 974 `gspObserver`，各自标注分派分支与读取槽位。未加入任何回合逻辑，行为不变：表覆盖不会启用这些模式。
- 2026-10-20：网页界面不再以编译后的形式提交到仓库。WebUi 项目会在构建前用 npm 编译其 Vue 客户端（之后只在客户端源码变化时重新编译），并嵌入编译结果；`wwwroot/` 属于构建输出，已加入忽略列表。因此从源码构建除 .NET 10 SDK 外还需要 Node.js 20.19 或更高版本。Docker 镜像在独立的 Node 阶段编译客户端。`docs/protocol` 不再纳入版本控制。
- 2026-10-20：所有程序包的作者以及 `LICENSE` 中的版权持有者均为 Fantasy Tale Studio。
- 2026-10-20：KR2026（kr-1256）商店行的所有封包编号现在都按名称从该版本的 opcode 表取得，写死的 `ShopOpcodes` 编号已删除。请求名称来自客户端：道具商店与现金商店入口（`gcpEnterShop`、`gcpEnterTCShop_`）、购买与卡片请求（`gcpReqShopBuy`、`gcpReqShopItemCardInfo`）、优惠券请求（`gcpReqRealCouponUse`）以及背包打开（`gcpEnterInventory`）与字段相同的 kr-812 封包类相对应；两个用途尚未证实的请求保持为 `Unknown834` 与 `Unknown2059`。商店行以前误读的两个请求不再由它处理：C2S 816 是礼物盒的已发送列表翻页请求（`gcpReqPresentSendSubList`），以前被当成第二种购买，并把翻页偏移当作商品编号；C2S 817 是退款请求（`gcpReqRefund`），以前被当成卡片请求应答。在礼物盒与退款提供服务之前，两者都记录为未处理。购买后的钱包推送现在也遵循 opcode 表（以前即使表中重新映射了 `gspUserMoneyStat`，也总是发送 S2C 106）。其他封包字节不变。
- 2026-10-20：KR2026（kr-1256）商店购买现在与旧版本一样，遵循客户端自身 ShopItem 表的物品条款（durationType、maxDuration、buyAgain、function）：按天计的物品带到期时间保存，永久物品（stock type 9、duration -1）保存为永久，计数物品保存真实数量，不再以数量 99999、无期限的形式保存。同一账号登录旧版本时，这类记录不再依赖旧版背包写出的兜底处理，KR2026 的限时物品也会正常到期。条款拒绝的购买（已拥有的永久物品、超过天数或数量上限）会收到失败回复且不扣费，而以前会被再次发放。待办：有六个在售套装含有寿命计数（endurance）物品，kr-1256 尚未确认其规则，确认之前这些套装会被拒绝（stock set 2001347、2001740、2001888、2001986、2002116、2002363）。
- 原生角色创建现在由共用的 `dialect.messages` 适配器处理：kr-1256 的 dialect 行与 lobby 行设置 `character_creation: true`，通过与其他旧版版本相同的绑定提供创建阶段；该能力对六个旧版版本与 kr-1256 开启，并对 cn-774 与 tw-454 明确关闭。`packets/character-creation.yaml` 声明两个空封包（创建已打开、创建后登录）与 kr-1256 的创建正文（五个部分，20+2N 字节；旧手写解析器按 12+2N 读取是错误的，已随 fighters 的创建处理器一并删除）。绑定从各版本的 opcode 表解析该系列的封包名称（必须恰好一个），并在载入时检查每个正文，因此表中出现两个名称或非空正文的版本会在服务客户端之前失败。被拒绝的创建现在以明确原因关闭连接，而不是抛出异常，并会记录 C2S 32 请求。fighters 行变为纯逻辑（目录与角色名单）；其重复的创建编解码器与测试已删除，字节由 Dialect.Messages 的创建锁定文件固定，12 字符昵称上限保持不变。未决问题：C2S 32 第二个字的含义未证实，仅记录日志。待所有者测试：kr-1256 新账号的首次登录会显示创建阶段，接受名称、创建角色并携带该角色进入主菜单。
- KR2026（kr-1256）在主菜单之后发送原生到期通知：当登录时的物品清理移除了过期行时，共用的 `dialect.messages` 登录突发会以 `gspCheckExpiredTimeResult`（S2C 807，计数的 id 列表，读取器 kr-1256 sub_999C00，消费者 kr-1256 sub_93C6E0 每个 id 显示一条通知）作为最后一步发送，使用该版本自己的操作码与既有的到期物品发送器；旧版版本继续使用 S2C 806。空的 `gspNotifyFirstMoveChannel`（S2C 163）封包已由读取器确认（它不读取任何内容），因此保持为空。频道移动的请求与应答路径不变。交互式中心选择器保持关闭：kr-1256 的中心请求尚未读取，且该选择器属于旧版的多端点流程。礼物盒保持关闭，直到 kr-1256 的商店行挂载它，并且三个 modern 礼物请求获得读取器。
- 氏族（clan）封包现在来自数据与类型化消息：clan 插件按类型处理氏族消息，其数字操作码与手写网络辅助已删除，正文由新增的 `packets/clan.yaml` 声明（`ClanPassport`、`ClanMarkInfo`、`ClanGuildInfo`、`ClanAttendanceState` 以及三个空请求结构，kr-1256 结构与 kr-812 基线并列）。带 `clan: true` 的 `dialect.messages` 行接受三个空氏族请求（C2S 25、2040 与 2041），以空的护照、标记列表与公会信息应答氏族窗口打开，以空的出勤状态应答签到，并对 C2S 25 不发送任何封包。`profiles/bundles/clan.yaml` 不再接受 `client_profile` 与 `version_major`，并声明 `max_tracked_opens: 512`；clan 行仍未被任何内置 profile 挂载，因此 kr-1256 bundle 保持 `clan: false`（没有 clan 行的处理器时，每个 C2S 2040 都会失败）；在某个 profile 加入 clan bundle 并开启 `clan` 之前，客户端看不到任何变化。clan 插件现在没有数字操作码、原始路由注册或手写网络编解码器（其 wire-ratchet 行降为零；默认 protocol 保留一项 build check）。未决问题：现发送的护照正文为 16 字节，而 kr-1256、kr-812 与 cn-774 读取器都读取五个部分（含名称前的一个前导字），即应为 20 字节；kr-1256 读取器的反编译带有告警，因此在真实客户端会话确认之前保留 16 字节正文。
- 2026-10-20：封包表新增 `when<mask&0x01,f32>`，由前面的无号整数掩码决定字段是否存在。读取时区分字段缺席与数值零，写入时拒绝掩码和值不一致；载入时检查每个版本的条件依赖，替换字符串时保留条件字段边界。现有移动绑定和封包布局不变。
- KR2026（kr-1256）与 cn-774 的玩家菜单「查看信息」请求（C2S `gcpReqFighterInfo`、S2C `gspRepFighterInfo`）改由共用的 `dialect.messages` 行处理（`fighter_info: true`）；modern 行的 `fighter_info` 选项及其独立绑定与写入器已删除。回复字节不变：`packets/fighter-info.yaml` 新增 `FighterInfoReply@kr-1256`（旧版的结果字、两个 FighterInfo 块与两个字符串，随后是 modern 读取器读取的四个字），锁定长度为 kr-1256 找到 924 字节、失败 908 字节，cn-774 找到 970 字节、失败 954 字节。共用的用户状态写入器改为按该版本的 `FighterInfo` 表（其 `unknown_504` 行）选择 modern 取值，而不再依据 `family: modern`，字节不变：kr-1256 为 494 字节，cn-774 为 521 字节，旧版基线为 384 字节。tw-454 仍保持关闭：其回复读取器（`tw-454 sub_A97DD0`）已经识别，但启用会替换该版本在本次迁移前的紧凑正文，因此属于后续的所有者可见变更。fighters 行中重复的 C2S 118 -> S2C 133 处理器及其写入器已删除，两个封包只保留一个写入器。把 `fighter_info: true` 设置在 `dialect.modern.messages` 上的自定义补丁必须将其移到 `dialect.messages` 行，否则启动会因未知键失败。
- 2026-10-20：共用登录适配器的登录序列若包含背包，但未启用背包发布，现在会在启动时报错并提示如何启用。使用成对登录响应的版本保留原有序列。新增 TCP 回归测试，逐字节固定 cn-774、tw-454 的完整登录序列及其空主菜单探测响应。
- 2026-10-20：`tools/item-viewer` 的预览与 kr-812 背包格一致：武器带黑色描边、不加高光，按未摆姿势绘制（两把原先显示为空白的手枪现在可以显示），借用模型来源道具的镜头行，手枪不再显示过大；角色的头部保持朝前。
- 2026-10-20：cn-404、cn-406、cn-408 与 cn-706 不经创建角色阶段就新建玩家，其首次登录不再断开连接：过期清理对一无所有的玩家不再尝试单独穿戴默认角色，而是交给随后的初始背包发放。
- 2026-10-20：修复账号持有未记录期限的道具（在 KR2026 商店购买，或来自早期初始背包）时，旧版本客户端无法登录的问题：背包现在把这类道具显示为无期限，与服务器其他部分的处理一致，不再断开连接。

- KR2026（kr-1256）、cn-774 与 tw-454 的登录改由共用的 `dialect.messages` 适配器处理：`login: true` 绑定登录路径，`main_menu_init: true` 以空的会话签名正文应答空的 C2S 521 探测。登录后的发送顺序由 `packets/builds.yaml` 中该版本的 `login.burst` 列表决定；新增的 `login` 值为 `reply`（`legacy` 或 `modern`）、`main_menu`（`sender` 或 `table`）、`channel_id` 与 `burst`，并由 kr-1256 设置 `request_extra_wstr`，因为其启动器登录正文末尾还有一条 wstring。kr-812 的顺序仍为默认值，cn-735 保留自己的列表。`dialect.modern.messages` 行不再绑定登录、主菜单探测及其写入器，并移除 `send_user_info_notice`、`send_center_list`、`send_option_sync`、`send_equipped_view_on_login`、`send_reference_room_fields`、`send_fighter_info` 与 `initial_channel_id` 选项：自定义补丁必须删除这些键，否则启动会因未知键失败。cn-774 与 tw-454 的 bundle 新增带 `login`、`main_menu_init` 与 `inventory` 的 `dialect.messages` 行；kr-1256 的 bundle 打开 `login` 与 `main_menu_init`。该 modern 行原有的登录编解码器、请求/回复写入器与主菜单写入器已删除；它们写入的结构现由 `packets/login.yaml`（`UserMoneyStat`、`UserPcroomStat`、`MoveStageRequest`、`gspRepMoveInto`）声明，主菜单安装位于 `packets/room.yaml`，单人队伍位于 `packets/party.yaml`。报文逐字节不变：完整登录突发以及主菜单与单人队伍正文均锁定为迁移前的字节。待所有者测试：kr-1256、cn-774 与 tw-454 能登录并进入主菜单，背包显示已存物品，商店可以打开；主菜单安装必须保持 kind 1，因为 kind 2 会使客户端崩溃。
- 2026-10-20：核查公开指南的翻译和命令示例；MySQL 配置示例改为隔离的配置转储，并明确区分持久化部署。
- 新增独立工具 `tools/item-viewer`：从已安装的客户端按客户端自带的商店镜头预览 kr-812 与 kr-1256 的商店和背包道具，并导出透明 PNG。
- KR2026（kr-1256）房间：房主更换地图（包括海盗任务）后会保存并显示给所有成员，海盗回合从所选任务开始；未选择地图的海盗房间显示客户端自己的海盗标记（`Random_Hot_Dif1`）而不是 Random；自定义 AI 对战模式（38 与 39）允许房主在空位添加电脑玩家；客户端第一次房间探测不再重复安装房间（此前会重播进入房间动画；ingame 选项 `reinstall_room_on_first_probe`，kr-1256 关闭）。新增版本值 `room_unchosen_difficulty`、`room_setting_change_map` 与 `room_ai_seat`。
- 服务器读取的所有客户端文件现在都来自该版本的客户端数据：`Config/FighterConfig.xml` 与 `Config/ShopItem2.xml` 和其他文件一样，由 `clientdata` 按客户端路径读取。`fighter_config_xml` 与 `shop_item_xml` 路径选项已移除；`fighter_config_client_path` 与 `shop_item_client_path` 指定文件。`BF_KR1256_UNPACKED_CONFIG` 与 `BF_KR1256_FIGHTER_CONFIG` 已删除：请改为准备 `client-data/kr-1256/data`，其中的 `Packs/Config_00000.pkn` 与 `Packs/ConfigSh_00000.pkn` 提供商店、宠物与角色工作簿。
- KR2026（kr-1256）的道具使用（C2S 806 / S2C 813）与涂装后的用户状态刷新（S2C 105）改走共用的道具使用适配器，报文逐字节不变；`dialect.modern.messages` 的 `inventory_use` 选项与独立绑定已移除，改由设置了 `inventory_item_use: true` 的 `dialect.messages` 行按 `packets/inventory-use.yaml` 为该版本声明的计数字符串列表写入（携带多个字符串的请求仍在进入道具处理器前以结果 1 拒绝）。
- KR2026（kr-1256）新建房间在房主选择地图之前显示全随机条目，而不是客户端的启动默认地图（Boomhill 村庄）。建房请求总是携带该启动地图，因此它从来不是玩家的选择；房主开始游戏时携带 Random，由服务器抽取本回合地图。相关字段为 `packets/builds.yaml` 中的版本值 `room_unchosen_map`。
- KR2026（kr-1256）的房间、广场与回合现在默认走类型化房间路径，与其他版本使用同一套房间与回合逻辑，房间建立与回合开始的字节保持不变。旧的数据包路径保留一个版本，可用 `profiles/kr-1256/raw.patch.yaml`（`--patch`）切回；原先的 `typed.patch.yaml` 开关已移除。
- MySQL 存储在以 `SslMode=None` 连接远程主机时，启动时记录一条警告：此时账号凭据与游戏数据以明文传输；本机连接不提示。连接行为不变。
- KR2026（kr-1256）的背包（S2C 802/803/808/810）改由共用的背包适配器发送，字节与之前相同；独立的 `InventoryCodec`、其数字常量 `InventoryPushOpcodes` 以及 `dialect.modern.messages` 的背包发送器已移除。每个版本在 `packets/builds.yaml` 的 `inventory` 下声明发送顺序、压缩信封与行形式；设置了 `inventory: true` 的 `dialect.messages` 行若操作码表缺少所声明的封包，启动即失败。曾依赖 `dialect.modern.messages` 发送背包服务的自定义补丁，请在 `dialect.messages` 行加上 `inventory: true`。
- KR2026（kr-1256）商店：分类树按页面 ID 归入所属标签（客户端表格中页面名称重复，导致“礼包”等页面挂到错误标签、“角色”标签没有页面），只发送有商品的标签，回复打开客户端请求的标签（若存在），并携带内容版本号。新增商店选项 `catalog_tree`（默认 `card_names`，kr-1256 使用 `id_ranges`）；`enter_shop_main_category` 仅在 `card_names` 下生效。
- KR2026（kr-1256）的已穿戴视图及武器预设列表改由共用的装备适配器发送，字节与之前相同；独立的 `codec.modern.inventoryview` 插件已移除。每个版本在 `packets/builds.yaml` 的 `equipment` 下声明其视图结构与发送顺序；设置了 `equipment_view: true` 的行若操作码表缺少视图封包，启动即失败。
- KR2026（kr-1256）的穿戴与卸下请求改由共用的装备适配器处理，独立的 `codec.modern.equipment` 插件已移除。被拒绝的请求现在回复"没有物品"（结果 3），而不是数据库失败（结果 1）。
- KR2026（kr-1256）在聊天记录中显示服务器公告（例如模式变更、自动开始等房间消息），与其他所有版本使用同一个公告适配器。CN2019 和 TW2019 不发送：它们的客户端会忽略该封包。
- 插件 ID `dialect.legacy.*` 改为 `dialect.messages`、`dialect.rooms`、`dialect.match`、`dialect.match-offers`、`dialect.match-replies` 和 `dialect.scene-seeds`：这些适配器服务所有客户端版本，而不是某一个系列。自定义补丁中请同步改名。
- 启动器登录可在首次登录时创建未知账号（`create_account_on_login`，默认关闭），玩家无需注册；安装向导在选择启动器登录时提供此选项。
- CN-774 和 TW-454 登录初始化改用各自客户端处理器要求的主菜单会话类型，保留每个版本的封包布局。
- 安装向导可直接从已安装的游戏文件夹生成服务端数据，与 `bffarchive generate` 和 Python 菜单共用随附规则；缺少必需文件时显示文件名，并继续支持预先整理好的数据导入。

- `deploy/docker/compose.yaml` 改为运行首次安装，而不是固定的 `kr-1256` profile：管理员在 WebUI 上创建账号，安装结果保存在新的 `bff-state` 卷中。Compose 现在要求 `BFF_SETUP_TOKEN`。端口、数据库服务及其密码保持不变。

- 首次安装会询问玩家的登录方式。启动器（推荐）会启用启动器服务、向其公布每个已安装的版本，并在游戏登录时要求启动器票据；开放模式在可信网络中接受任何账号名。可选的游戏地址会覆盖默认值，默认使用每位玩家的启动器访问服务器时所用的地址。最后一页列出玩家、启动器和管理地址以及后续步骤。从安装向导安装版本时不再因运行时容量检查而失败。Contracts 2.56.0 新增 `InstallationSettings.PlayerLogin` 与 `PublicHost`。

- 管理员只能以账号和密码登录 WebUI；移除 `bearer_token` 选项与 `BFF_ADMIN_TOKEN`，残留的 `bearer_token` 键会在启动时报错。以 `start` 启动的服务器（可执行文件不带参数时即如此）使用首次安装时创建的管理员账号。`run --profile` 服务器没有账号，无需登录，并拒绝非回环的管理地址。

- 新增大厅 `authentication` 选项。`ticket` 是所有内置 profile 的默认值（`BFF_LOGIN_AUTHENTICATION`），只接受能兑换启动器票据的登录并拒绝普通账号名；它需要 `launcher.http` 行，缺少时启动即失败。`open` 保留原有行为，并在每次启动时记录警告。尚未从启动器获得票据的版本需要在可信网络中使用 `open`。

- Contracts 2.55.0 新增 `ILauncherSessionStore.CreateLimitedAsync` 与 `PruneExpiredAsync`，以及接收连接来源地址的 `IAdminAccessControl.SignInAsync` 重载。管理员登录失败次数现在按来源地址分别计算；首次安装期间带有代理转发头的请求会被拒绝。

- 启动器 API 改为按来源 IP 限流，每账号持久登录会话默认上限为 8，并在启动时及登录期间每小时清理过期刷新会话。

- 现代客户端登录的普通服务端日志不再输出凭据或格式错误的登录包内容，仍保留封包长度和解析失败原因。

- 在共享 Equipment 回复和 legacy 绑定中保留武器预设拒绝状态。无效或未绑定的请求会解除客户端等待锁，不应用其他预设。封包表补充已核实的 modern 五字节请求与状态回复。Contracts 2.54.0 增加明确的结果属性；现有成功回复仍默认为零。

- 在封包表中描述 modern 武器预设更新：KR-1256 元素为 12 字节，CN-774 为 14 字节，TW-454 为 10 字节。保留各版本未知字段及计数字节尾部，不改变 legacy 预设栏，也不启用切换道具收费。

- 重写并扩充公开指南与文档站内容（四种站点语言）：验证过的安装与快速开始流程，更完整的配置与部署说明（profile、持久化、Docker Compose、备份、升级、回退）、故障排查、插件与 SDK 入门，以及中英文 README 首页。仅涉及文档，运行行为没有改变。
- 新增四语言「启动器」指南：配套 Fantasy Tale Launcher 的客户端版本、会话与登录票据模型、hook 插件、分发边界，以及面向运营者的服务端 `launcher.http` API，包括 HTTP 426 版本门禁、JSON 兼容规则与票据安全边界。仅涉及文档，运行行为没有改变。
- 为 Python、TypeScript、Go 和 Rust 增加协议 v2 跨语言类型化消息 middleware，并支持 Go/Rust WebAssembly。middleware 共用原生优先级、类型化 session 与消息体、激活所有权和取消语义；单次 `next` 续调会一直保留已进入的下游工作直到完成。
- 为 Python、TypeScript、Go 和 Rust 增加由原生所有者管理的 SDK 任务句柄。任务准入使用激活实例的有界原生容量；wait 只观察回调工作，dispose 负责取消并等待清理，卸载时持续持有任务直到原生任务排空。
- 为 Python、TypeScript、Go 和 Rust 增加由原生所有者管理的资源作用域和释放回调。延迟清理会一直保留 SDK 回调，直到原生父作用域调用它们；作用域和资源所有权有界，并传播取消信号。
- 为 Python、TypeScript、Go 和 Rust 增加显式 SDK 消息处理器替换。替换目标必须写入插件定义，Host 会验证当前选中的原生处理器；释放自有句柄后恢复前一个处理器。

## 尚未发布 — 2026-10-17

- 为四种 SDK 增加激活生命周期取消信号，包含就绪协商、实例隔离、回滚及连接断开时取消，并由原生生命周期管理通知工作。旧 Host 保持兼容。
## 未发布 — 2026-10-17

- 在安装向导底部同一处状态标签切换连接测试提示、失败和成功信息；修改数据库配置后需要重新测试。
- 保持安装步骤指示器常驻，将纤细圆角内容滚动条贴合面板右边缘，停止滚动后淡出，不挤动表单布局。
- 为数据库选择增加表单淡入淡出和高度过渡，底部导航保持原位。
- 调整客户端数据引导文案，将第四步名称改为“权限”，保持账号创建功能不变。
- 文档明确 MySQL 为默认数据库选项，SQLite 可按需选择。
- 新增首次启动向导，提供语言选择、持久化 SQLite/MySQL 配置、管理员登录，以及无参数 Host 启动。
- 支持同时选择多个客户端版本，为各版本独立选择数据或稍后激活。版本名称改为本地化地区与客户端日期，不再使用内部配置 ID。
- 新增本地文件夹和已整理 ZIP 的导入、文件数量与大小确认、取消操作、可配置资源限制，以及独立导入副本。
- 优化多语言安装界面，固定各语言选项的字体，并支持点击角色或对话框立即显示完整文字。竖屏隐藏引导角色并停止音效；数据库选项调整为 MySQL 在前且默认选中、SQLite 在后。

## 尚未发布 — 2026-10-16

- 为 legacy 基线及三个 modern 版本描述现金收件箱领取布局，独立核对并修正领取请求名称，避免与同号的礼物回复混淆；尚未启用运行时领取。
- 补充带数量的现金收件箱记录布局，区分原生对象填充和实际变长网络记录。
- 修复关闭取消与故障观察竞争时丢失 SDK 激活错误的问题，避免清理误报正常停止。
- 为四种 SDK 及 Go/Rust WebAssembly 增加协议 v2 声明式事件，共用原生优先级、串行/并行分发、waterfall 续调和 bail 决策；事件回调中仍可跨语言调用服务。
- 在取消、自身移除和已进入的续调期间保留事件所有权；限制保留回调的容量，拒绝跨激活或过期调用令牌。
- 修复 Rust 在激活失败或停用后的回调/上下文循环引用，包括清理报错的情况，并保留仍在执行的回调。
- 记录 legacy 基线及三个 modern 构建已核对的 C2S `gcpRoomSendAmpMsg` 正文：类型、消息和两个保留的 unknown 字段。modern 的 opcode 行根据匹配的客户端写入器保持 inferred；运行时验证与真实客户端验收仍待完成。
- 记录仅适用于 kr-812 的招募请求和通知正文（`wstr + u8` 与 `u16 + wstr`）；modern 的编号冲突尚未解决，因此不套用到 modern 构建。
- 记录 kr-812 C2S 2009 观战队伍请求已核对为空正文，但不启用其菜单或生命周期行为。
- 根据 modern CN/TW 的直接座位数组消费者，将房间背包通知的第一个字命名为 `slot`；其余字和向量仍保留为 unknown。
- 将三个 modern 构建中由 dispatcher 直接处理的 S2C 506 房间背包通知补入各自的 game opcode 表，但暂不挂载背包处理器。
- 在发布等待房间快照前复制保留的设置变更正文，正文超限或缺少布局来源时在修改房间状态前拒绝。验证与真实客户端验收仍待完成。
- 为较新 modern CN/TW 等待房间投影分别描述已核对的门控掩码、地图别名与模式宽度，保留无关设置及分轮地图选择标志；验证与真实客户端验收仍待完成。
- 标明三个 modern 构建房间设置请求及其保留设置中的模式字段，保留较新 CN 的 16 位与 TW 的 32 位差异；入站请求投影仍待完成。
- 修正较新 CN/TW modern 房间外层布局，移除仅存在于韩服布局中的两个压缩标志字，并保留原生较短的尾部；独立完整字节样本锁定嵌套设置，检查截断与多余字节。真实客户端验收仍待完成。
- modern 类型化等待房间保留原生三回合地图选择标志，只清除已核对的普通房间阻断位；将模式、地图候选及门控字段别名移到经过校验的封包元数据中。真实客户端验收仍待完成。
- 分别描述已完整核对的 CN/TW modern 游戏设置，包含不同的头部、嵌套集合及 64 位尾部字段；使用独立字节样本和异常输入检查锁定两种布局。设置请求接入与真实客户端验收仍待完成。
- 将已核对的分轮地图选择接入共享等待房间权限校验，保留三个选择并广播设置，不重新安装房间场景。原生字符串目标由数据描述，保留未修改的 legacy 字节；尚未核对完整目标的 CN/TW modern 路径保持关闭。真实客户端验收仍待进行。
- 将三个 modern 构建的设置请求名称对齐到已核对的 legacy 控件，并确认分轮地图索引；不改变封包字节，也不提前启用请求处理器。
- 将已核对的房主变更回复与房间背包通知写入封包表，描述 modern 基线的短格式及 CN/TW 的长格式差异。请求处理与真实客户端验收仍待完成。
- 为 modern 房间设置请求补充独立核对的结构表：基线为三个字符串和十三个整数字段，CN/TW 差异为四个字符串和十二个整数字段。共享请求处理仍待接入。
- 增加共享等待房间房主转让逻辑，处理原生失败状态、清除新房主准备状态，并保留成员加入顺序。通过封包表接入已核对的基线请求和 modern typed 房间请求；真实客户端验收仍待完成。
- 将已核对的房主转让数据扩展到十个已支持的 legacy 构建，保留早期客户端请求 507、较晚请求 508 及紧凑回复对象偏移的差异。
- 在已有审核通过的出站设置通知时，使用它发送 typed 房间已提交的设置，保留创建时的其他选项并避免重新安装场景。modern 入站设置投影仍待完成。
- 将基线及三个 modern 构建已核对的回合设置请求描述为一个整数和两个字符串，未证明含义的字段保持 unknown，暂不启用请求处理。

- 为 Python、TypeScript、Go 和 Rust 增加协议 v2 服务提供者、装饰链与有序服务集合，包含 Go/Rust WebAssembly。原生与 SDK 消费者共用声明式依赖和优先级规则。
- 将提供者注册及集合句柄纳入激活所有权；取消和超时后继续管理已进入的内层工作，拒绝跨激活或已失效的调用令牌。
- 插件行超时但共享进程仍可用时，继续执行 SDK 停用清理，避免保留已经停止的行在 SDK 中的状态。
- 共享 SDK 进程继续存活时也能释放已移除的激活上下文；进程故障仍会通知当前所有已注册的插件行。
- 将只用标准库的 Python 测试替身与本机启动钩子隔离，保持原有请求超时限制。
- 修正 loader 发布定义后的启动选择，让新发现的提供者和装饰器参与后续消费者激活；在文档站四种语言中说明支持的服务形状与尚未实现的原生对等能力。
- 添加类型化过期物品移除 bundle，在 Memory、SQLite、MySQL 共用归属校验、续期保护与装备修复。已核对 kr-812 和三个 modern 构建的封包表；真实客户端验收仍待完成。
  单件移除使用独立服务契约，不会因此开启背包自动清理。补全另外九个 legacy 构建的已核对封包表，并通过构建数据选择原生类别限制。
  默认人物赠送中断后，修复重试会补齐缺失的背包镜像，不重新补发初始武器。
  十个已核对 legacy 配置与 kr-1256 默认选择此 bundle。CN774、TW454 保留协议覆盖，其默认装备前置服务仍待补齐。

- 为 kr-812、kr-1256、cn-774、tw-454 添加按作用域保存的自定义按键与登录恢复，支持 Memory、SQLite、MySQL。更新与已存映射均有容量和格式校验；真实客户端验收尚未完成。

- 重新整理安装、配置、插件、SDK、部署、故障排查和贡献指南，提供四种语言。
- 添加 Vue 3 文档站、明确的发布页面清单、本地搜索及即将提供的下载入口。
- 将原有文档保存在被排除的只读归档中，修正私有材料排除规则。
- 将内部检查及脚本客户端工具移出公开树，等宽替换 fixture 中的个人文本和网卡地址。
- 区分公开测试与需要本地客户端数据的完整验证，加强目录联接清理和 TRX 校验。
- 明确插件激活、服务访问和资源清理契约。
- 在退役恢复测试中等待依赖处理完成，再提交后续生命周期操作。
- 更新预发布候选的依赖约束。

上述条目描述候选改动；下载发布和真实客户端验收仍是独立的发布步骤。
# 变更记录

## 尚未发布

- 将持久化插件间接使用的 PKCS 依赖固定到修复版本 8.0.2，保留包漏洞检查。

- 启动器注册、登录、刷新和启动接口接受可选版本信息；认证和票据请求忽略未知顶层字段以兼容新版启动器，同时继续校验已知字段并拒绝重复键。部署可配置最低 SemVer 以及按标识、版本范围推荐的插件。旧启动器仍可发现目录并仅凭刷新凭据退出登录。

- 自动开局房间在计时器提前唤醒后会重新检查单调时钟的等待截止时间，确保已准备的玩家在等待结束后仍收到原生倒计时。
- 历史包日志清理现在只在内存中保留配置上限内的文件名，避免启动时对整个目录创建排序数组。清理保留最新的匹配日志及其轮转文件，不影响其他文件名系列。
- Kernel 生命周期回归在重试配置变更前等待自动依赖协调结束，并明确验证激活仍在运行时拒绝与其冲突的配置变更。
- 尚未确认击爆的击倒目标现在由本回合的固定座位计分状态保存，避免简化击爆报告把上一回合的目标记为本回合死亡。

- 房间、广场和聊天命令的 typed 与 packet 入口共用可配置的连接请求预算。超额请求在命令执行或向其他玩家广播之前被拦截；每个计时窗口只记录一次警告，连接关闭时释放策略记录。

- SDK运行时在调度工作前限制接纳数量，超限调用直接失败而不排队。Go/Rust WebAssembly等待宿主调用时可处理嵌套回调、保留乱序响应并在关闭时解除等待；`max_reentry_depth`配置栈深度上限。本次补齐回调传输基础，尚未新增服务提供者或事件API。
- 协议 v2 为 Python、TypeScript、Go、Rust 及 Go/Rust WebAssembly 增加声明式宿主服务调用，保留 64 位标识、校验参数、使用激活独占会话句柄，并持续追踪超时但未结束的工作。服务提供、装饰器和事件回调仍待实现，见 `docs/plugin-dev/service-calls.zh.md`。
- Contracts 2.51 在原子持久化之前加入共享装备决策策略，同时覆盖客户端请求和服务调用；插件可拒绝或修改目标，卸载后撤销策略。见 `docs/plugin-dev/equipment-policy.zh.md`。
- `bff contracts schema [--out file]` 通过普通的 `sdk.contracts.export` 插件行导出共享契约清单，包含继承的服务方法、可空性、整数位宽及明确的表示缺口（包括嵌套回调）。这是 SDK 编写工具，尚未启用协议 v2 服务调用。
- 日志写入器失败时释放尚未写入的排队记录和封包记录的 payload 预算，并在关闭时保留原始写入错误。
- 新增串行验证运行器和专用 GitHub Actions 工作流，逐项目保存 TRX，检查 SDK 一致性、封包表和隔离配置。缺少资源、跳过测试或空报告均会导致检查失败。

- 协议会话在限制排队封包数量之外，还限制保留的出站 payload 字节数；慢速读取端超过任一上限都会被关闭。


- TCP 监听默认启用可配置的操作系统 keepalive，检测未正常关闭连接就失联的对端。仍然连通的空闲玩家不会因此断开，不新增游戏心跳封包。探测时间和重试限制见配置指南。
- Shell 脚本检出时统一采用 LF 换行，PowerShell 脚本采用 CRLF 换行，避免多余回车导致 Linux 部署命令失败。
- 新增插件开发指南（`docs/plugin-dev/README.zh.md`）：何时编写C#插件、以进程或WebAssembly运行的SDK插件，各自如何部署、如何调试以及常见错误。
- profile 可以从启动时就列出 SDK 插件的行：当该 profile（或某次配置变更）同时添加了预留定义来源的行时，绑定到尚未发布插件的行以 `Pending` 状态等待而不是使启动失败，并在加载器发布该插件后激活。没有这样的行时，未知插件仍会使启动失败。
- SDK 插件可作为 WebAssembly 在宿主进程内运行：新增 `loader.wasm` 行运行由 Go（`GOOS=wasip1`）或 Rust（`wasm32-wasip1`）SDK 构建的 `.wasm` 模块，协议、限制与故障处理与 `loader.process` 相同，并在沙箱中运行（无文件、网络和标准输入输出，内存上限 `max_memory_bytes`，默认 64 MiB）。插件代码不变，作者或服主可按行选择进程或模块。运行时（Wasmtime 48.0.2）随加载器提供，服务器无需安装任何东西。两个模块均在宿主内通过全部共享 fixture。新增指南 `docs/sdk-authoring.zh.md`，说明如何为其他语言编写 SDK。
- 远程插件改称 SDK 插件：加载器行改为 `loader.process`（原 `loader.remote`），指南为 `docs/sdk-plugins.zh.md`，共享 fixture 位于 `tests/fixtures/sdk-protocol`，未命名进程条目的默认 `source_id` 为 `process-<index>`。挂载过 `loader.remote` 的配置需改用新的插件名。
- 新增SDK插件协议 v1 的 Go 与 Rust 插件 SDK（`sdk/go`、`sdk/rust`），声明、处理器、发送、日志与 effect 模型与 Python、TypeScript SDK 相同。四个 SDK 均通过 `tests/fixtures/sdk-protocol` 中的共享 fixture，可用 `python sdk/run-conformance.py` 一并运行；Go 与 Rust 示例还在加载器测试中经由真实的 `loader.process` 行完成往返（缺少工具链时跳过）。describe fixture 现在列出 Python 与 TypeScript SDK 早已发送的 `supported_families`。

- 现代版本道具使用与喷漆（kr-1256）。C2S 806 在 kr-1256、cn-774、tw-454 中命名为 `gcpReqInventoryUseItem`（与 kr-812 同类，道具、数量、参数相同）；其列表形式（kr-1256、cn-774）与单字符串形式（tw-454）连同 S2C 813 记录于 `packets/inventory-use.yaml`。`dialect.modern.messages.inventory_use` 将其绑定到共享大厅道具处理器，喷漆会设置并保存玩家颜色，S2C 105 在登录及喷漆后带上该颜色。未知道具与多参数请求会被拒绝，且不消耗任何东西。
- 原生生命回合会统计因角色状态不符而被忽略的生命报告（为他人角色报告泡泡、击破队友或未在泡泡中的角色、未获准或在错误连接上确认重置等）。每种情况每回合记录一次日志，回合结束时记录汇总。不会拒绝任何报告：这些检查只用于观察真实客户端的行为，再决定是否变成规则。
- 回合结算对每次开局和每位玩家至多执行一次。经验在持有玩家锁时加到已存储的进度上（并发修改不再被覆盖），Lucci 与结算凭据在同一事务中写入，失败的尝试会重试（`ingame.settlement_attempts`，默认 2）且不会重复发放。typed 流程中某个座位存储失败不再阻止其他座位的结果。Contracts 2.50.0 以 `SettleRoundAsync` 取代 `IPlayerProgressRepository.RecordRoundAsync`；MySQL 迁移 0031，SQLite 架构 6。
- 依据已核实的 kr-812 对应类，将回合内范围伤害体封包（32900）在 kr-1256、cn-774、tw-454 中命名为 gppSplashDamagerByDamager，并在 packets/damagers.yaml 中记录其结构：所有构建均为十二个字，kr-1256 之后另有一个可以非空的计数字列表。转发方式不变，封包仍原样转发。
- 依据与 kr-812 对应类行为一致的消费者，为 kr-1256、cn-774、tw-454 命名其余七个回合内伤害体生成封包：32772 gppSplashDamager、32877 gppMobDropBombOnDead、32881 gppDropHumilPrevItem、32898 gppFireBullet3、32899 gppStormDamager、32910 gppStormDamagerItemId（即 kr-812 中编号 32909 的类，并非 gppShocked2）、33278 gppNearDamager（kr-812 为 32773）。各封包结构记录于 packets/damagers.yaml；kr-1256 在其中四个之后另附计数字列表。转发方式不变。
- 玩家菜单“查看信息”（战士信息）。右键点击玩家并选择第一项现在会显示该玩家的信息窗口：客户端请求指向被点击的账号（C2S `gcpReqFighterInfo`），服务器从同一版本的已登录玩家中返回该玩家的昵称与等级（S2C `gspRepFighterInfo`，result 1）；账号未登录时返回该版本自身的失败结果，绝不会回传请求者自己的记录。legacy 版本 kr-812、cn-717、cn-324、id-400、id-1809、cn-706、cn-406、cn-404、cn-408（能力 `fighter_info`，布局在 `packets/fighter-info.yaml`；cn-324 新增请求行 C2S 112，cn-406 新增 115/124 两行），以及 modern 的 kr-1256（C2S 118，S2C 133）与 cn-774（C2S 117，S2C 133）。cn-774 的回复此前按其读取函数并不采用的紧凑形式写出，现改为 kr-1256 的结构并使用 cn-774 自己的 `FighterInfo`。cn-279（回复末尾是未读的向量）、tw-454（回复读取函数未读）以及 cn-735 及其后续版本保持关闭。新增选项 `lobby.fighter_info`、`dialect.legacy.messages.fighter_info`、`dialect.modern.messages.fighter_info`。Contracts 2.49.0 新增 `FighterInfoRequested` 与 `FighterInfoReplied`。同一菜单的称号与排名项仍无回应。

- 根据已核实的 legacy 对应关系补齐韩、中、台 modern 的单人地图加载完成通知名称，并记录它与全员加载完成传输标志的区别。
- 修正现代版本踢击失败和结束反馈未回送发送者的问题；同步核实韩、中、台三个版本的原生封包名称。
- 将 modern 创建角色的昵称检查请求纠正为 C2S 31。三个 modern 构建均由各自的创建角色发送器确认编号和 UTF-16 名称结构；KR1256 的可选 Fighters 处理器同步使用此编号。

- modern 组队邀请（kr-1256）。玩家可邀请他人入队（C2S 110）；受邀方收到提示（S2C 121），邀请方收到送达或失败答复（S2C 120/119）；受邀方接受（C2S 115，仅 kind 1）或拒绝（C2S 111）。双方收到队伍列表（S2C 201）与加入通知（S2C 206），队长或队员离开时也会通知（S2C 203/205）。拒绝封包回传共享 `party` 行为每个待处理邀请签发、并绑定到受邀方连接的令牌；未知、过期或来自其他连接的令牌，以及 kind 0 和 2 的接受（对应其他提示），都会被忽略并记录日志。cn-774 与 tw-454 的提示、接受、拒绝布局已写入表格并有操作码行（packets/party.yaml）；在邀请方发送函数与失败码证实之前，其 `party` 行保持关闭。Contracts 2.47.0 新增 `PartyInviteTokenRefused`、`PartyInviteNotified.Token` 与可选的 `PartyUpdated.Appearance`。房间、广场与主菜单刷新通过同一封包表投影保留队伍和接收者装备。邀请交互与队员外观仍待真实客户端验收。

- 升级礼物通过 clientdata 读取各版本原生 Config/LevelUpGuide.csv，连续预告合并为一个奖励等级，物品、期限和金币取自完整 Stock/StockSet 内容，支持非卖品和纯金币礼包。仅有占位值或缺少表时会记录，不虚构奖励；shop.level_rewards 可覆盖原生奖励。登录和升级会根据已保存等级补发尚未发放的奖励，包括启用前达到的等级；领取标记和礼物在同一事务写入，避免重发。共享结算流程发布 PlayerLevelRaised，礼物箱尚未启用的版本仍保持关闭。Memory、SQLite 架构 5 与 MySQL 迁移 0030 提供领取标记。Contracts 2.48.0。

- legacy 版本的礼物箱（Present）：玩家可以买商城物品赠送给其他玩家，接收者在任务栏礼物箱中领取到背包、丢弃或删除（kr-812、cn-717、id-400、id-1809、cn-324、cn-279、cn-706、cn-406、cn-404、cn-408；能力 `present_box`，封包布局在 `packets/present.yaml`，操作码行在各版本的 `opcodes.yaml`）。价格取自商城目录，在礼物存储时向发送者扣款；接收者收到客户端自带的新礼物提示（登录时及关闭礼物箱时也会发送）。领取礼物会运行商城的购买事务并在其中把礼物标记为已领取，因此重复、并发或重连后的领取请求只会发放一次物品。新增选项 `shop.present_box`、`shop.max_presents_per_player`、`shop.max_present_message_chars`、`shop.cash_presents` 与 `dialect.legacy.messages.present_box`；Memory、SQLite（架构版本 4）和 MySQL（迁移 0029）存储。Contracts 2.46.0 新增 `Contracts.Presents`、`ShopPurchaseCommand.PresentId` 与 `LoginMenuInstalled` 事件。现代系列的记录与请求行已为 kr-1256、cn-774、tw-454 建表（cn-774 与 tw-454 现已在 `packets/builds.yaml` 中命名），但这些版本的能力保持关闭。

- 回合可以按座位从中继退回游戏连接。开启 `ingame.match_transport_fallback`（kr-1256 typed 补丁已开启）后，若座位在加载期间报告中继连接失败（kr-1256 sub_960050 -> sub_961C00），该座位改用游戏连接：撤销其未使用的中继凭据，视为已就绪，其 S2C 585 清除两项中继选择（随后 kr-1256 sub_95B910 改从游戏连接发送），回合中发给它的移动、玩法消息、生命周期与重置封包都走游戏连接，其他座位继续使用中继。Contracts 2.45.0 新增 `RoundCountdownStarted.MatchOverGame`。legacy 版本在审查其发送选择逻辑前保持原有行为。

- modern 的类型化房间、广场与主菜单安装保留共享队伍的成员顺序，不再用单人列表覆盖；每位接收者仍使用自己的角色与装备资料。移除成员特征后恢复原有单人投影。这项改动准备场景间的队伍保留，modern 邀请路由仍待接入。

- 队员通过共享房间逻辑跟随队长加入房间、进入广场和返回主菜单，使用各自的座位与原生场景切换提示。目标已满时保留队员原场景，其他房间或进行中的比赛不受影响。并发切换会预留参与者、重新检查成员关系与连接，广场预留座位在入场前不可见。`ingame.max_pending_scene_transitions`（默认 128）限制已接收的切换。Contracts 2.43.0 为场景消息增加提示标志，两类方言均通过封包表编码；modern 的队伍成员绑定仍待完成。

- 组队请求现在通过有界、由插件生命周期管理的工作队列顺序执行成员变更和通知。断线或登录替换会清除双方的待处理邀请；旧连接迟到的关闭事件不会移除新会话的队伍。房间邀请的接受标志和未支持的邀请答案不会消耗队伍邀请。`party.max_pending_requests`（默认 1024）限制排队请求数，队列满时仍能进行断线清理。

- 游戏模式名称改为各地区客户端自己的名称：`data/locales/game-messages.json` 的 `mode.<id>` 取自客户端字符串表（韩文、简体中文、繁体中文、印尼文），例如国服把生存模式称为“热血”、终极战区模式称为“终极战区”；同时修正了模式 10、15、23、25、29、30、103 的自造名称（客户端语言中没有名称的模式仍使用英文说明）。新增选项 `localization.profile_languages`：为某个版本在其地区语言之上叠加一份只含差异文本的语言（`cn-774` 与 `id-400` 使用）；查找顺序为版本语言、地区语言、英文。Contracts 2.40.0 增加 `IGameText.Format/TryFormat(ClientProfile, ...)`。参见 `docs/profiles-and-config.zh.md`。
- legacy 版本支持组队：邀请（C2S `gcpReqInvite`，S2C `gspNotifyInvite`/`gspRepInvite`）、接受（C2S `gcpReqMoveToInviter`）、拒绝（C2S `gcpReqMoveToFriendAccept` 回答 3）、离队、踢人与移交队长，使用客户端原生的队伍列表（队长在首位）与成员变动通知（S2C 201-206、302-304、307）。新增 `party` 插件行、`lobby.online_players`、`dialect.legacy.messages.party`、能力 `party`，布局位于 `packets/party.yaml`，并按版本补充带证据的 C2S 109/111（以及缺失时的 108/202/302/303）封包号。成员经过主菜单安装时保留队伍列表。Contracts 2.41.0 新增 `IOnlinePlayers`、`IPartyDirectory`、`IPartyMembership` 与组队消息。
- 通过共享背包使用逻辑接入原生固定颜色喷漆，Memory、SQLite、MySQL 原子消费道具并保存玩家颜色。登录、商店刷新及 legacy 房间、广场快照保留颜色，独立于阵营；legacy 基线读取各客户端自己的喷漆定义，modern 启用仍需核验对应本地状态与使用路径。

- 角色颜色准备：封包表根据客户端读取路径，为本地 `UserStat.color_index` 四字节字段及其他玩家 `UserSlot.color_index` 两字节字段命名，包含 CN2011/CN2012 的较短记录和 modern 玩家槽。共享 legacy 房间投影可以独立于阵营接收颜色值，空座位保持零值。本步骤尚未启用喷漆消耗和颜色持久化。
- kr-1256 typed 回合在结算打开与奖励阶段之间只发送一次结算计分板（S2C 614），并带上奖励，与 raw 路径一致；共享结算流程通过 `IRoundResultSequencePolicy.SingleScoreboard` 得知这一点（Contracts 2.38.0，默认 false，legacy 版本保持两次发送）。kr-1256 raw 与 typed 回合对照场景现在完整进行一局：开始、移动、动作、计时得分模式中的泡泡、戳破与复位、计时结束、结算以及返回房间。
- kr-1256 typed 回合现在有生命周期与结算。`ingame.native_life_mode_ids`（kr-1256 typed 补丁中为计时得分模式）让回合根据客户端自己的报告跟踪每个座位的生命状态（`RoundLifeReported`：泡泡、死亡、解救、泡泡超时、戳破、死亡清理、复位确认）；戳破敌方泡泡只会杀死一次并为戳破者的队伍得分（向两队发送 S2C 985），死亡玩家的复位请求（C2S 602，`RoundResetRequested`）在死亡演出结束后才会被批准（S2C 632，`RoundResetGranted`），且只有该中继上的确认才能完成复位。共享结算流程在 kr-1256 上运行：S2C 612 打开结算，614 在 `dialect.modern.messages.result_scoreboard_mode_ids` 的模式中（默认计时得分模式）填充计分板，615 关闭结算进入奖励阶段，返回房间时发送 576 与 620（`RoomEntryReason.RoundReturned`）。Contracts 2.38.0 新增这些消息、`RoundScoreboardRow.UserNumber` 与 `RoundScoreboard.ModeId`。
- kr-1256 typed 回合现在传递游戏 feed：39 种回合内动作、伤害体生成与中继移动帧原样发送到其他玩家的中继连接，其中发送者自己的客户端只在收到时才应用的 11 种动作也会回传给发送者。`RoundFeedSent.ReportedActors`（Contracts 2.38.0）携带动作所指的角色；回合会拒绝替其他座位角色行动的动作，并丢弃所指角色不属于本房间的动作，但由房主客户端运行 NPC 的部落（horde）模式除外。
- kr-1256 typed 回合现在传递移动：完整（`gppWanderLean`）与部分（`gppWanderLeanOptimize`）移动报告转换为共享的 `RoundMovementReported`；回合把每个玩家的部分报告合并进其完整记录，并把完整记录（`RoundMovementRelayed`，Contracts 2.38.0）发送到同一轮其他玩家的中继连接。在任何完整记录之前的部分报告、针对其他座位角色的报告，以及发送者本人，都不会收到。
- Contracts 2.38.0：`RoundFeedSent.OnRelay` 让经由对战中继到达的游戏 feed 封包转发到同一轮其他玩家的中继连接，而不是游戏连接；中继已断开或属于更早一轮的座位不会收到。legacy feed 不变。这是 kr-1256 回合内流量走 typed 回合流程的准备。
- 生命值道具。带生命值、没有时间限制的道具（ShopItem 的 `durationType` 为 endurance，在 kr-812 与 cn-717 中是道具类型 3）在玩家每缺席一个完整的 UTC 天后失去 1 点生命，最低为 0；生命为 0 时像到期道具一样被删除（两个背包、脱下、用客户端自己的到期通知告知，其第三种文本即 Life 文本）。玩家有史以来第一次登录以及同一天重复登录都不扣。生命值道具是新的期限类型 `Life` 的背包行（数量即生命，随封包写在槽位的 +8 计数，时间字段用永久哨兵值）；使用路径不会消耗它；开启 `shop.life_items` 后 endurance 商品按持续值发放同等数量的生命（仍有生命的已拥有道具不能再次购买）。开关：`lobby.life_items`（需要 `expire_items`）与 `shop.life_items`，legacy 公共配置开启，id-400、id-1809、cn-324、cn-279、cn-404、cn-406、cn-408、cn-706 的补丁关闭；`packets/builds.yaml` 中的能力 `life_items`。MySQL 迁移 0027 新增 `players.last_login_day` 并放宽期限检查；SQLite 存档新增 `LastLoginDay`。Contracts 2.37.0 新增 `InventoryLifetimeKind.Life`、`InventoryLife` 与 `IInventoryRepository.AdvanceLifeAsync`。

- 限时道具现在会到期。登录时（发送背包之前）以及打开背包界面时，玩家已到期的限时道具会在一次按玩家加锁的事务中从两个背包删除（内存、SQLite、MySQL 三种存储），穿戴中的道具会被脱下，到期的穿戴角色会换成起始套装中的角色（若玩家没有则永久发放）。玩家会收到客户端自己的通知，即 S2C `gspCheckExpiredTimeResult`（806；带计数的 u32 道具 id 列表，每个道具显示一条屏幕通知；kr-812 sub_6D5890），登录时在菜单之后发送。开关：`lobby.expire_items` 与 `dialect.legacy.messages.expired_item_notices`（legacy 公共配置均开启）、`packets/builds.yaml` 中的能力 `expired_item_notice`（cn-735 系列关闭）、`packets/inventory.yaml` 中的 `ExpiredItems` 表；kr-812 与 cn-406 新增了封包号条目。Contracts 2.36.0 新增 `IInventoryExpiry`、`IInventoryRepository.RemoveExpiredAsync`、`InventoryExpiry.IsExpired`、`ItemsExpired` 消息与 `LoginSucceeded.ExpiredItemIds`。没有定时器，因此会话期间到期的道具会在下一次进入背包界面或登录时移除。

- 所有起始道具现在都是永久的：cn-279 配置方案中遗留的测试发放 `starter_expiry_days: ["204001:30"]` 已移除（选项本身保留，默认为空）。

- 僵尸大战：僵尸王的大招现在可以使用。能量条满后，僵尸王按 SHIFT 会发送一个不带对象、种类为 104 的道具请求；`ingame.zombie_king_ultra_item_kind`（legacy 公共配置为 104；CN2011 与 CN2014 0.2.51 没有直接道具效果封包，因此关闭）让本轮只向本轮的僵尸王确认该道具效果，之后由僵尸王的客户端把效果显示给所有玩家。人类玩家或尚未产生僵尸王时发出的同样请求不会得到回复。

- 房主可以用聊天命令 `!set <模式编号>`（例如 `!set 8`）更改等待中房间的游戏模式；创建房间时标题带有 `#<模式编号>#`（例如 `Fun room #8#`）则房间采用该模式，并显示去掉标签的标题。包括房主在内的所有成员都会收到带模式名称的本地化提示；命令失败（队员、已开始的房间、不是 1 至 65535 的数字）只回复发送者。所有带房间的版本都支持：typed 房间流程发送设置变更（没有设置编码器的版本则重新发送整个房间），封包房间流程发送 MapMeta 更新，legacy 编码器通过房间表把模式写入客户端自己设置块的模式字。Contracts 2.35.0 新增 `IGameText.TryFormat` 与 `RoomBuildSettings.Mode`。游戏模式的社区名称（英文和韩文）以 `mode.<id>` 存放在 `data/locales/game-messages.json`，其他语言回退到英文。详见 `docs/profiles-and-config.zh.md`。

- `dialect.modern.messages.typed_rounds`（默认关闭，需要 `room_binding: typed`）让 kr-1256 的回合通过共享的 typed 流程开始：读入开局请求、加入对象、地图加载完成与中继报告；写出开局名单、签名、进入关卡、中继邀请、加载闸门、倒计时和回合开始，全部通过封包表。中继加入按本轮密钥准入（`match-connection.capability_authentication`）。新增封包表 `RoundObjectAddRequest`；`RoomEnterGameRequest`/`TutorialPlayRequest` 中的规则记录字段以及 kr-1256 关卡记录字段改用客户端自己的名称。加入他人座位对象的请求会被记录并丢弃。kr-1256 的 typed 测试补丁现在能进行到回合开始；默认配置方案不变。

- Contracts 2.34.0 为 modern 系列准备共享回合开局。开局请求可以携带地图名称、房主的选择以及规则记录中的回合时长；开局消息给出抽到的地图所在的场景，以及它是否是任务地图。`ingame.server_map_draw` 从客户端的地图池抽取本轮地图，`ingame.assign_spawn_teams_on_start` 在发送开局名单前为没有队伍的玩家分配队伍（两者默认关闭）。带规则记录的开局使用其中的时长，训练场回合报告为不限时。在会回复此请求的版本上，玩家请求加入自己角色（`RoundObjectAddRequested`）后，每轮只向该玩家确认一次；没有单人加载通知的版本可以不提供该发送器。legacy 各版本的发包顺序不变。

- 客户端构建系列：`profile.yaml` 可声明 `family: legacy|modern`，通过 `parent` 继承（kr-1256 声明 `modern`，cn-774 与 tw-454 继承），每个插件行都带有该值。插件可声明 `SupportedFamilies`（远程 SDK 为 `supported_families` / `supportedFamilies`），同系列的新构建无需修改插件。架构测试保证配置方案的系列与 `packets/builds.yaml` 一致。Abstractions 8.13.0。`dialect.modern.messages` 改为声明 modern 系列，不再逐个列出 kr-1256、cn-774 与 tw-454。

- 服务整个 modern 家族（kr-1256、cn-774、tw-454）的插件已改名，项目、插件 id 和类型不再以地区命名：`dialect.kr.messages` 改为 `dialect.modern.messages`，`codec.kr1256.equipment` 改为 `codec.modern.equipment`，`codec.kr1256.inventoryview` 改为 `codec.modern.inventoryview`（项目分别为 `BubbleFighter.Fantasy.Dialect.Modern.Messages`、`BubbleFighter.Fantasy.Codec.Modern.Equipment`、`BubbleFighter.Fantasy.Codec.Modern.InventoryView`）。配置必须使用新 id；行为和选项不变。部署时必须删除旧的插件目录 `plugins/BubbleFighter.Fantasy.Dialect.Kr.Messages`、`plugins/BubbleFighter.Fantasy.Codec.Equipment.Kr1256` 和 `plugins/BubbleFighter.Fantasy.Codec.InventoryView.Kr1256`，否则旧程序集会与新程序集同时被加载。

- Contracts 2.33.0 在座位索引之外保留成员加入顺序，并为返回主菜单提供当前装备外观。共享广场策略可以关闭入场位置重播、向发送者回显及多余的同伴清空包；legacy 默认配置保留现有顺序。`ingame.plaza_enter_requires_finished_login`（默认关闭）忽略登录流程中途的进入广场请求；没有座位的会话发来的房间探测包只记录日志并忽略。

- Contracts 2.32.0 增加明确的房间测试路径开局校验通知，以及房间探测回复中的已认证座位。共享房间逻辑对不可用的开局每房只记录一次，不发送封包、不改变阶段；现有方言默认不挂载这项新通知。

- Contracts 2.31.0 明确房间进入与座位更新的原因，增加可选的已认证房间阶段探测及界面刷新接口。现代方言可以复现原生进入顺序，无需缓存成员关系，也不会把探测数据当成加入请求；现有方言保持原有封包行为。

- Contracts 2.30.0 为设置块增加可选的不透明会话前缀，并在座位通知中携带完整房间快照。共享房间逻辑在同一谱系的设置变更中保留来源字段，每次更新同时记录成员穿戴状态；现有方言的封包行为保持不变。

- Contracts 2.29.0 在共享等待房间请求和成员视图中保留商店、背包打开状态。准备处理器拒绝互相冲突的状态，并在玩家切换状态时清除旧状态；现有 legacy 请求仍默认不携带这两个状态。

- kr-1256 的房间列表（gcpReqListSession / gspRepListSession）是第一个从封包表而不是手写代码读取布局的 modern 编解码器；发送的字节不变（测试与旧的写入函数比较）。`dialect.kr.messages` 新增 `packets_directory`（默认 `packets`）。
- kr-1256 的房间请求以 legacy 结构写入封包表：房间列表（C2S 401）、准备（505）、座位开关（509）与房间内位置（511）请求与 kr-812 完全相同；gcpRoomReqEnterGame（503，kr-1256 构造函数 sub_9D7570）以一个字节和一个字结尾，而不是 kr-812 的六个 bool 字；聊天请求（502）多一个字符串。测试用手写的 kr-1256 解析器读回每个表写出的字节。
- kr-1256 的房间 opcode 改用 legacy 名称：15 个 `UnknownNNN` 行改名，并补上 14 个缺失的行（`name_source: label@kr-812`），每一行都经由 kr-1256 的构造或处理函数匹配（kr-1256 房间分发函数 sub_9D9930 保留了 kr-812 sub_7FBBB0 的 501-532 case 顺序，构造函数的字段形状也与 kr-812 的序列化函数一致）。kr-1256 的房间列表编解码器现在按 `gcpReqListSession` 与 `gspRepListSession` 查找。C2S 107、108、520、521 与 S2C 128 在匹配之前保留原名。
- kr-1256 的房间封包沿用 legacy 的 opcode 与类：S2C 501-518 即 kr-812 的 gspRoomSetSession、gspRoomSetMasterSlotIdx、gspRoomSetSlot、gspRoomChangeTeam、gspRoomReceiveChatMsg、gspRoomReceiveChatModuleMsg、gspRoomLeaveSlot 与 gspReqPingUser/gspRepPingUser，C2S 401-404 与 502-517 即 kr-812 的房间请求。因此封包表把 kr-1256 的房间记录写成共享结构的差异：房间安装（`gspGotoRoom@kr-1256`，kr-1256 sub_10D4520）及其 profile 条目与队伍面板，以及 RoomListReply、RoomSeatUpdate、RoomSeatLeave、RoomJoinResult、RoomChatReceived、ZombieTeamChange 与单人队伍快照上的注记；场景切换与房主座位直接沿用 kr-812。只有 kr-812 没有的 gspRepWaitUserList（S2C 118），以及 kr-812 结构尚未读出的封包，才单独为 kr-1256 写出（`packets/room-modern.yaml`）。测试把每一个都固定为手写 kr-1256 编解码器所写的字节；kr-1256 发送的内容暂时没有变化。
- kr-1256 以 kr-812 为基底加入 `packets/builds.yaml`，作为 modern 一族的基准（`family: modern`，以它为基底的版本会继承），其 profile 也相应把 `kr-812` 设为 parent。它的房间 session setting（kr-1256 sub_113BD20）、game setting（kr-1256 sub_113D030）与座位记录（kr-1256 sub_1143710）以 `SessionSetting@kr-1256`、`GameSetting@kr-1256`、`UserSlot@kr-1256` 重新写出；测试把它们固定为将被取代的手写 kr-1256 编解码器所写的字节。在 kr-1256 中读到对应的消费者之前，kr-812 的所有能力在 kr-1256 上都是关闭的；legacy 房间写入器会跳过 modern 一族。kr-1256 发送的内容暂时没有任何变化。
- 封包表新增字段类型 `optional<0x3A,Name>`：一个标记字节，只有该字节等于给定标记时才跟随结构 `Name`，与 kr-1256 座位读取函数（kr-1256 sub_1143710）读取 +204 与 +212 区块的方式一致。写入时未提供对应的值则把字段的 `default` 作为“不存在”标记写出；读取时保留该标记字节，因此读回的记录能写回相同的字节。
- 所有旧版本现在都转发道具模式（游戏模式 3），并按各自版本的规则处理。模式规则由客户端保有（kr-812 为它创建的是普通 ModeMgr，sub_53AB30），箱子由房主客户端上报，因此服务器负责转发，并只维护该版本声明的两件事。其一是箱子种类：旧版基线列出 `ItemSpecItem` 中启用的十五项，kr-812、cn-324、id-400、id-1809、cn-717、cn-706、cn-406、cn-404 与 cn-408 在该模式下都选用这张表（booster、shield、ghost、needle、freezeCannon、medipack、camouflage、polymorph、banana、teamAngel，以及火箭筒一族 airBomb、turret、bazooka、machinegun、shieldField）；cn-279（CN2011）列出其按房间等级划分的道具表中带权重的十七种（含巨人饮料，没有火箭筒一族）；道具模式中，其他种类的地图点、投掷出的箱子与对它们的拾取都会被拒绝。其二是队伍箱：房主把每个道具点的名称归类为 0、1（`red`）或 2（`blue`）并上报（kr-812 sub_50C840），生成封包以队伍结尾（kr-812 sub_519480 -> sub_636F20 为随机箱选用 ItemCube_red 或 ItemCube_blue），客户端会拒绝其他队伍的箱子（kr-812 sub_6378A0）；服务器现在把队伍从上报带入生成，并在道具模式中拒绝其他队伍的拾取。cn-279 没有队伍箱。cn-279 现在也挂载补给封包（702/707/708/727 与 711 至 714，授予与使用为两字、掉落请求 28 字节、没有切换封包；其上报不声明刷新间隔，因此使用新增的 `supply_default_min_ms` 与 `supply_default_max_ms`）。`ingame` 新增选项 `item_mode_ids`（旧版基线设为 `[3]`）、`item_mode_kinds`、`supply_default_min_ms` 与 `supply_default_max_ms`；Contracts 2.28.0 新增 `RoundSupplyPoint.Team` 与 `RoundSuppliesReported.IntervalsStated`。
- 旧版本现在可以玩僵尸对战（游戏模式 8）。客户端本身不保存该模式的规则（kr-812 ZombieModeMgr，由 sub_53AB30 创建），因此由服务器主持：所有座位开局为人类（阵营 1）；开局 `zombie_king_delay_ms`（5000）后，服务器把一名随机人类移到阵营 14，由于它是第一个进入该阵营的座位，所有客户端都把它显示为僵尸王（kr-812 sub_776870）；人类的死亡报告（gcpRoomChangeTeam，kr-812 sub_76C090）使其变成小僵尸，小僵尸的死亡报告使其复活，次数不限。僵尸王不会复活：它死亡或离开房间时人类获胜。没有人类剩下时僵尸获胜；`zombie_pvp_round_time_limit_ms`（180000，即客户端的房间默认值）结束时人类获胜。死亡报告不包含击杀者，因此摔落地图死亡的人类同样会转化。`ingame` 新增选项 `zombie_pvp_mode_ids`（旧版基线设为 `[8]`）、`zombie_king_delay_ms` 与 `zombie_pvp_round_time_limit_ms`。
- 旧版本现在可以玩星星模式（游戏模式 5：各自为战；游戏模式 6：按队伍）。客户端保有多方的星星、物理、掉落规则与自己的数量，并通过该版本声明的计分封包上报数量（`packets/builds.yaml` 中的 `score.star_report`）；服务器负责分配星星编号、把掉落转发给所有成员（S2C `gspGameCreateStar`，kr-812 sub_51AB40 只为 kind 1 与 2 生成星星）、应答某个星星编号的首次拾取（S2C `gspStarEaten`，kr-812 sub_5153D0 不改变任何计数）并累加上报的数量。这四个星星封包复用 Lucci 封包的序列化函数（kr-812 sub_530870、sub_5302A0、sub_6DB1B0），因此不需要自己的结构，716 与 717 也已加入每个可开房版本的 opcode 表。初始星星由每个客户端在加载结束时自行上报（kr-812 sub_661AF0：模式 5 为 10 颗，模式 6 为 5 颗），因此同样计入。时间结束时，模式 5 由持有星星最多的座位获胜，模式 6 由星星更多的队伍获胜，相等为平局。`ingame` 新增选项 `star_mode_ids`（旧版基线设为 `[5]`）、`star_team_mode_ids`（`[6]`）、`star_round_time_limit_ms`（240000）、`big_star_value`（10）、`max_round_stars`（999）、`max_dropped_stars`（512）与 `star_object_id_base`（`0x64000000`），并新增旧版房间方言的 `star_items` 开关与 `star_items` 能力。
- Contracts 2.26.0 新增 `GameModeId`：按所有客户端版本发送的编号列出每个游戏模式，名称取自客户端自己的代号（kr-1256 sub_1134540；kr-812 sub_8E8900 使用相同编号）。服务器中的模式判断（海盗与教学模式、无道具战的计时计分、训练场）改为使用模式名称而非数字。
- 新增 cn-408（CN2014 0.2.48.001，cn-408 sub_7CDA60 发送 ClientVersion_Major 408），以 cn-404 为基底，游戏端口 7250（`CN_408_GAME_PORT`），只用数据实现。它的八个分发函数与 cn-404 的 case 集合相同；595 个封包类中只有三个的线路格式不同，各多一个布尔值：UserSlot 的 +209（cn-408 sub_63B560）、gspRoomEnterGame 的结尾（cn-408 sub_7B24D0）以及 gppKnocked 的结尾（服务器原样转发）。其余全部沿用 cn-404，包括关闭原生角色创建、关闭奖励卡和海盗倒计时。部署需要 `client-data/cn-408`。
- 旧版商店目录与登录布局不再按版本名选择。`packets/builds.yaml` 中的 `shop.catalog` 现在描述回复的结构（`token`、`stock_type`、`tree`、`page_type`、`trailer`），由同一个写入函数服务所有版本，取代按版本命名的五个写入函数；新增的 `login` 值（`request_trailer`、`request_token`、`request_extra_wstr`、`login_response`）取代按版本命名的登录变体。各版本只写与基底不同之处，例如 cn-706 一目了然地是 id-400 的目录加上 id-1809 的结尾。各版本发送的字节不变。
- 旧版本的大厅数据改为版本数据，经 `packets/builds.yaml` 的 `base` 继承：是否启用原生角色创建（`character_creation` 能力；bundle 中的 `character_creation: true` 表示“在该版本支持时启用”）、创建界面提供的角色以及初始背包（新增的 `lobby` 值，由设置 `build_values: true` 的 lobby 行读取）。基于其他版本的版本直接继承这些值，不再在 profile 中重复；CN2013、CN2014 和 ID 版本的 `patch.yaml` 不再设置它们。各版本的实际取值不变。`builds.yaml` 中的列表会整体替换基底版本的列表。
- 新增 cn-404（CN2014 0.2.46.001，cn-404 sub_7CA490 发送 ClientVersion_Major 404），以 id-400 为基底，游戏端口 7240（`CN_404_GAME_PORT`），只用数据实现：一个 profile 文件夹、带 cn-404 地址的独立 opcode 表，以及封包结构表中与 id-400 不同之处的注解（与 cn-706 相同的 GameSetting 尾部、UserSlot 从 +180 开始的字段块、12 人 ScoreData、第三个载入标志和聊天后缀）。线路流为 legacy 的 5 字节帧和共用加密表（cn-404 sub_AD4AF0、sub_AD5390）。海盗房间按原生倒计时开始（cn-404 sub_5887F0 的判断与 id-400 sub_626970 相同）。原生角色创建与 cn-706 一样保持关闭（其创建界面提供哪两个角色尚未核实），新账号由服务器直接生成角色。奖励卡暂时关闭：该版本的 ShopItem2 缺少或改动了 kr-812 与 id-400 奖励表中的大部分 Stock 和 StockSet。部署需要 `client-data/cn-404`。
- 客户端版本改为纯数据：新增版本不再需要改代码。按版本划分的 `WireDialect.*` 类、四个按地区划分的方言插件以及 `profiles/bundles/dialects.yaml` 均已移除；每个 profile 挂载一行 `dialect`，由 `frame`（`modern_8`、`legacy_5` 或 `legacy_10`）及可选的 `cipher_table_a`/`cipher_table_b` 描述线路流，地区与版本取自该行的 `client_build`。legacy bundle 从 profile 变量 `WIRE_FRAME`（默认 `legacy_5`）、`WIRE_CIPHER_TABLE_A` 和 `WIRE_CIPHER_TABLE_B` 读取这些值；cn-279 在其中设置 10 字节帧和它自己的加密表。在 `--profile all` 中每个版本保留自己的方言行（`<build>.dialect`），`core.wire-dialects` 收齐全部方言后才发布注册表。共享的协议、游戏、商店、legacy 方言插件以及 `centers` 不再列出支持的版本。legacy 商店的 C2S 801 请求和目录布局改为读取 `packets/builds.yaml` 中新增的 `shop` 值（`enter_request`、`catalog`），并经 `base` 继承；`shop.catalog_schema` 现在只接受 `kr1256` 或 `legacy`。管理 API 中方言行的 id 改为 `dialect`（原为 `dialect.kr`、`dialect.cn`、`dialect.id` 或 `dialect.tw`）。
- 海盗/尖兵奖励卡只在通关后开启。模式 102（海盗波次）加入协作关卡模式，失败结果字现在按失败结束，而不再按比分判平；这些模式下房主的“全部波次完成”上报（gcpReqEndAllWave）不再单独结束对局，改由各客户端紧随其后发送的关卡结果字决定（kr-812 sub_A09190、id-400 sub_986A00），因此失败不会被先到的通关抢先。奖励对话框（S2C gspGameAwardDialog，客户端唯一的开启途径）对所有旧版统一遵守同一条规则：处于抽奖模式的、已通关的波次对局。
- 除 CN2013（其 `shop` 行只提供背包）外，所有 legacy 版本的玩家首次通关教学房间关卡时，现在会获得该关卡的奖励。奖励是该版本自己的 MapMeta `IsItemAward` 指定的 Stock 或 StockSet（kr-812 工作簿注明它是通关奖励，客户端不会读取它）；`shop` 行通过新的 `IShopGiftService` 按该版本的 ShopItem2 条款免费发放：不在售的 Stock 也会发放，已拥有的物品在其上限内累加天数或数量，套装的 Lucci（`lucci` 列，ID2014 工作簿中为 `Lucci`）会计入钱包。物品和钱包会像购买后一样推送。由 `ingame` 行的 `tutorial_gifts` 开启。ID2014 与 ID2015 沿用了 kr-812 的 bubble_bomb 奖励 1001520，而它们的工作簿中没有该项，因此这三个关卡不发放奖励，并在启动时记录日志。序章奖励尚未发放，因为序章的通关不会以关卡通关的形式到达服务器。
- 教学房间关卡结束后，现在会显示关卡自己的通关（STAGE CLEAR）或失败面板，而不是只显示时间结束（GAME OVER）提示，之后回到教学房间的关卡选择，而不是主菜单。单独的 gspGameEndGame 在模式 15 中只会播放该提示（kr-812 sub_803DF0）；现在在 `tutorial_score_ms`（3000 毫秒）之后发送 gspGameTimeOverEnd 中的单人记分板，其处理函数会根据关卡自己的通关标记显示单人关卡的通关或失败面板（kr-812 sub_804B10 -> sub_5ACB20）。不发送 gspGameShowResult_，因为它会打开 3D 结算仪式（kr-812 sub_5AD030）。面板保留 `tutorial_result_ms`（由 11000 改为 5000）。旧版教学仍直接返回菜单。
- KR2015 及其衍生版本第二次进入教学关卡时，不再显示“[GM]xxx 成为房主”的提示。gspGotoRoom 已经设置房主（kr-812 sub_68EAD0 -> sub_689F80，与 502 处理函数 kr-812 sub_7FD460 写入同一位置），而在切换到房间画面之后到达的 gspRoomSetMasterSlotIdx 会显示房主提示（kr-812 sub_7FD460 -> sub_8E2A80），因此教学开始时不再发送它（builds.yaml `play.tutorial.master_slot_packet: false`）。CN2012 与 CN2011 仍发送：cn-324 sub_6ED2D0 只从该包取得房主座位。
- 所有 legacy 版本的教学房间现在会标记玩家已通关的关卡，并在旁边显示最佳时间。以 gcpReqEndStage 成功结束的关卡，会根据该版本自己的 ServerShared/MapMeta.xml 解析出 MapMeta stageNum，从回合开始计时，并按玩家保存（新的 `ITutorialStageStore`：MySQL 迁移 `0026_tutorial_stage_clears`、SQLite 存档 schema 3、内存）。gspResSingleStageInfo 现在以关卡、最佳数量、最佳秒数列出这些记录：kr-812 sub_824200 以关卡为键保存，教学房间按“行号 + 10 × 难度页”查找（kr-812 sub_6B3AC0），存在时标记为已通关（kr-812 sub_6B50E0），sub_6B4110 显示数量或时间。任务关卡通关不再被当作完成初始教学。首次通关奖励尚未发放。
- 教学房间关卡结束后，现在会先保留通关或失败画面，再送回菜单。此前对关卡结束（kr-812 sub_701050 发出的 gcpReqEndStage）同时回复游戏结束和返回菜单，SinglePlayGameStage 来不及显示结果就被替换；现在返回菜单前等待 `tutorial_result_ms`（legacy bundle 中为 11000，即客户端为这些关卡设定的 EndGameNotifyResultTick）。
- ID2014 及其他基于 KR2015 的版本在通关海盗战（horde 模式 101）后现在显示胜利。这些版本的结算界面按记分板的队伍总分判定胜者，与 cn-324 sub_63B230 -> sub_455ED0 相同；此前即使 gcpReqEndStage 报告成功、gspGameEndGame 带有胜利结果，所有队伍总分仍为 0。现在所有 legacy 版本使用 cooperative_score_outcome_modes [101]，通关时玩家队伍总分更高（失败时对方更高）；CN2012 保持自己的设置。
- 教学房间中的其他关卡现在可以正常开始，不再导致客户端崩溃。只有序章运行单人海盗波次；所有 sin_mission_* 关卡（KR2015 共 27 个，MapMeta 的 stageNum 均大于 0，部分关卡没有任何 NPC）现在以模式 15 开始，客户端为该模式创建 SinglePlayGameStage（kr-812 sub_8E1240），该场景从关卡的 MapMeta 行读取任务目标（kr-812 sub_6FF040）。此前所有关卡都发送模式 103，会创建波次游戏场景，而任务地图无法运行它。配置项为 builds.yaml play.tutorial.wave_map_key（rel_prologue_01，所有 legacy 版本继承）。
- KR2015 玩家在僵尸 PvE 中死亡后现在会以僵尸身份复活，CN2015 与 CN2014 0.2.51.002 同样如此。这些客户端自己计时复活（play.wire.server_revives 为 false），而回合只在服务器计时复活时才把座位记为死亡，因此死亡后的转换请求（kr-812 sub_78BCC0，与 cn-324 sub_68AE20 相同的 gcpRoomChangeTeam）被忽略。现在座位从死亡报告起到复活被接受为止也算作死亡；kr-812 sub_78D340 与 cn-324 sub_68C290 一样在队伍 15 时完成转换。
- KR2015 及其衍生版本进入广场时，现在会在 gspGotoLobby 之后等待 150 毫秒再发送其余数据包。离开房间会删除客户端自己的角色（kr-812 sub_7FDE50 -> sub_6CBF40），切换到大厅场景发生在之后的帧（kr-812 sub_8E1240、sub_A8B050），而大厅只在创建时构建一次角色（kr-812 sub_672110 -> sub_6C7450 -> sub_6CC6F0）；原来一次性发送的数据包与场景切换竞争，导致离开者看不到自己的角色。配置项为 builds.yaml play.wire.plaza_entry_settle_ms；CN2012 和 CN2011 保持 0。
- 在 KR2015 及其衍生版本中离开房间时，服务器现在发送以下离开数据包：离开者的 gspRoomSetSession 保持主菜单类型，但会复述刚离开的房间（标题、开放座位数及其 GameSetting，房间号为 0，只有离开者本人）；广场的 gspRepNotifyChangeLevelIcon 携带该成员的主菜单记录（closed、seated 以及 ready 之后的三个标志均为 1）及其本人等级。CN2012 和 CN2011 保持原来的简单会话（builds.yaml play.wire.restate_left_room）。目标是修复离开房间后广场中看不到自己角色的问题。
- 登录回复 +76 处的字在所有旧版本上恢复为 0（KR2015 曾为 30）。地图选择窗口并不读取它：窗口按 UserStat 中的角色等级选择地图列表（kr-812 sub_69F610 -> sub_6A2310），5 级及以下时客户端自带的 ServerShared/Default.ini（HideThemes=1）只显示“全部”。这是游戏本身的新手列表，不是服务器问题；ID、CN2014 和 CN2015 客户端没有 HideThemes，任何等级都显示主题。

- 客户端数据现在直接来自各版本客户端自己的封包，并按该客户端的方式挂载。`client-data/<版本>/data/` 存放服务器需要的封包（`.bfz`、任一格式的 `.pkn`），保持客户端中的原始路径和大小写；`client-data/<版本>/dataraw/` 按客户端路径存放散文件，优先读取；`sources.tsv` 记录每个文件的来源。`.bfz` 客户端从文件最近一级同名文件夹的封包读取（kr-812 sub_A4B600），`.pkn` 客户端从 `Pack/` 或 `Packs/` 中包含该文件的包读取（cn-279 sub_8A18B0）；在所有系统上查找都不区分大小写，内存中只保留封包索引。每个 legacy 版本都有自己的 `clientdata` 行，商店、背包显示和奖励卡通过它读取 `Config/ShopItem2.xml`（`shop_item_client_path`、`item_locations_client_path`、`lottery_workbook_client_path`）；`*_SHOP_XML` 变量已移除。`python tools/client-data/bfdata.py` 从已安装的客户端生成某个版本的目录（交互菜单列出已知版本），整包复制服务器数据封包（Config、ServerShared、StringTable、Lub、Mapbound），并从其余封包中解出地图、NPC 和场景定义。发布包不再包含客户端数据：Docker 以只读方式挂载主机的 `client-data/`（`BFF_CLIENT_DATA_HOST`）。
- `bffarchive` 和封包库用同一种方式打开所有格式：PKN 格式从文件本身识别，`--pkn-layout` 选项和 `pkn_layout` 行配置已移除。BFZ 成员名按韩文 CP949 解码。新增 `index`、`unpack` 命令、`--from` 列表选项、无参数时的交互菜单，所有写文件的命令都会生成 `sources.tsv` 来源记录；`copy` 现在按封包原始路径写入 `<版本>/data`。只处理客户端会挂载的封包（跳过 `Packs/old/` 之类的备份和 `Pkg/` 之类的补丁暂存目录）。

- CN2014 0.2.51.002 商店现在可以打开，穿戴位置也来自其自己的道具表。其客户端发送与 ID2014 相同的开店请求（只有缓存标记，cn-706 sub_656D60），读取的商品目录与 ID2015 完全一致：读取、商品卡、库存、分类与分页筛选函数（cn-706 sub_7A17F0、sub_481A40、sub_481810、sub_79DEE0、sub_64C780、sub_79FCF0、sub_8FA020）与 ID2015 一一对应，因此目录按 ID2015 格式发送。部署包带上 `client-data/cn-706/Config/ShopItem2.xml`（`CN706_SHOP_XML`）。

- 启动器登录在启动器重启、服务器重启和访问凭据到期后都能保持。登录返回短期访问凭据（`accessToken`、`accessExpiresAt`，默认 30 分钟）和刷新凭据（`refreshToken`、`refreshExpiresAt`，默认 30 天，`refresh_session_seconds`）。新增的 `POST /v1/auth/refresh` 原子轮换刷新凭据（并发刷新只有一个成功，旧凭据随即失效）；`POST /v1/auth/logout` 仅凭刷新凭据即可调用，只撤销该会话及其未使用的启动票据，可重复调用。刷新会话由持久化插件行通过 `ILauncherSessionStore` 保存，只存 SHA-256 哈希：MySQL 迁移 `0025_launcher_sessions` 新增 `launcher_sessions` 表，SQLite 存档升级到第 2 版，内存存储保存在进程内。
- 游戏登录不再根据启动器票据创建角色。KR2015 登录时携带了未加 `bff-ticket:` 标记的 64 位票据，服务器把它当作新账号名，因此每次启动都创建新角色，而不是载入已登录账号的角色。现在未加标记的票据与带标记的票据一样兑换为启动器账号，无法兑换时拒绝登录。所有内置 Lobby 行都从 `BFF_LAUNCHER_TICKETS`（默认 false）读取 `launcher_tickets`，启用启动器的部署可一次为所有版本开启票据登录。

- KR2015、CN2015、CN2013、ID2014、ID2015 与 CN2014 0.2.51.002 新建的房间现在显示全随机地图，而不是客户端的启动默认地图（`rel_Village01_Byway`）。每个建房请求都会复制客户端 `Config/Lambient.xml` 中的启动地图（kr-812 sub_480700），因此这张地图从来不是玩家的选择；服务器以前因为地图键非空而原样转发。现在在房主修改设置之前，房间显示全随机条目（键 `Random`，主题 `All`），海盗房间（模式 101）显示第一难度的随机条目 `Random_Hot_Dif1`。房主客户端开始回合时会自行把这两种条目解析为实际地图（kr-812 sub_5C3F80）。CN2012、CN2011 与 CN2013 的客户端没有难度标记，所有模式都显示 `Random`。在未修改的房间里由服务器开始的回合仍使用启动地图。

- ID2015 商店现在可以打开。其客户端发送与 ID2014 相同的开店请求（只有缓存标记，id-1809 sub_676990），读取 ID2014 的商品目录并多读一个计数向量（读取函数 id-1809 sub_7BEFF0）；其分页筛选 id-1809 sub_90DD50 与 id-400 sub_8E4C00 一样按子分类的页面类型分支，因此目录带上 ID2014 的主分页默认子分类与页面类型 2。

- 自动开始房间在第一回合之后可以再次开始。此前服务器在回合结束返回房间的瞬间启动倒计时，此时客户端仍在离开结算画面而忽略了它，房间之后不再启动。现在返回房间后先等待 `auto_start_rematch_hold_ms`（20 秒），之后在房间没有空位且除房主外所有玩家都已准备时开始。

- ID2014、ID2015 与 CN2014 0.2.51.002 的结算画面不再把完成回合的玩家显示为断线。其计分行在 +164 照搬了客户端空行的初始值 0，而客户端只会为回合中的座位设置该值（id-400 sub_78EE50），结算画面需要它才会列出玩家（id-400 sub_6DE480）；现在有人的行写 1，与 KR2015 一致。ID2014 与 CN2013 进入房间时的计分块现在按空座位写出，与客户端自身的空计分块一致。

- CN2013 玩家死亡后可以复活：其复活封包的发送函数（cn-406 sub_42DF70）没有任何调用者自行计时，因此与 ID2014、CN2012 一样由回合让玩家复活。CN2014 0.2.51.002 保留客户端自行计时的复活，其客户端与 KR2015 相同（cn-706 sub_5BDC90）；KR2015 与 CN2015 在海盗回合中本来就会自行复活。

- ID2014 与 ID2015 玩家在海盗回合中死亡后可以复活。其客户端在海盗死亡时发送 gcpPveUseRevival，随后等待回合处理（id-400 sub_592B00、sub_499640），因此服务器现在会在死亡时间结束后用 gspGameRespawnCharacter 让玩家复活，与 CN2012 一致。CN2013 与 CN2014 0.2.51.002 见上文。

- 自动开始房间按官方方式开始：当房间不再有空位（每个座位都有玩家或被房主关闭）时，运行原生五秒倒计时，由房主客户端开始回合。适用于 CN2012/CN2011 的 Boss 房间，并新增 ID2014/ID2015 没有开始按钮的海盗（部落）房间。`ingame.auto_start_mode_id` 改为列表 `auto_start_mode_ids`；从房间打开起的一分钟倒计时改为可选的 `auto_start_timer`，默认关闭。

- 服务器按房间自身的地图键开始的海盗回合（例如在没有房主开始封包的 ID2014 与 ID2015 上使用 `!start` 命令）现在进入房间所选的关卡（GameSetting current_map，与客户端自身的开始流程 kr-812 sub_816300 一致）。此前服务器把任务的 NPC 表键（`npc_...`）当作关卡发送，导致客户端的关卡脚本失败并弹出资源文件损坏对话框（id-400 sub_85CFE0）。

- ID2014 与 ID2015 的教程在房间打开后立即开始，与 KR2015 一致：服务器直接发送 gspRoomEnterGame（id-400 sub_786480 无需自动开始即可接受）。此前服务器发送 gspRepAutoStart，会先播放房间自动开始的五秒倒计时及其音乐，然后才进入教程。

- 协作模式 16、101 与 106 现在在所有旧版中都按房主上报的原生关卡结果判定胜负，与 CN2012 一致：KR2015 在清除所有波次后发送相同的结束关卡字段（kr-812 sub_A09190）。协作失败不会再因某队分数领先而算作胜利。Boss 房间自动开始、立即显示计分板与旧教程地图仍为 CN2012/CN2011 的差异，并在旁边注明 KR2015 的证据。

- 离开或完成教程后返回主菜单时，座位显示玩家实际穿戴的角色。此前主菜单房间使用 Dao 占位角色（道具 300001），因此在所有旧版中，选择其他角色的玩家在教程结束后都会看到 Dao。

- ID2014 与 ID2015 的序章教程现在以 KR2015 的部落教程模式（103）开始，不再使用单人模式（15）。模式 15 会让客户端建立单人关卡，因此序章地图播放错误的音乐且没有教程脚本。CN2013 与 CN2014 0.2.51.002 在其序章流程核对前仍使用模式 15。
- 击杀 NPC 掉落 Lucci 对所有客户端带有此功能的旧版开启（KR2015、CN2015、CN2014 0.2.51.002、ID2014、ID2015、CN2013、CN2012、CN2011）：击杀者客户端的掉落请求（C2S 736，CN2011 为 733）由服务端编号后向全房间广播物品（S2C 720，CN2011 为 717），拾取（C2S 735，CN2011 为 732）只对第一个报告者生效并通知全员移除（S2C 721，CN2011 为 718）；会发送奖励请求的三个版本（C2S 2021，CN2014 为 2020）按配置生成奖励物品。拾取到的 Lucci 并入该座位的对局奖励。新增 `ingame` 选项 `max_dropped_lucci`、`lucci_object_id_base`、`horde_award_kind`、`lucci_kind_values`、`max_round_lucci`，旧版房间方言新增 `lucci_drops` 开关，封包结构见 `packets/lucci-drops.yaml`。

- ID2014 与 ID2015 的创建角色通过新的 `creation_character_types` 大厅设置接受其自有角色 Leo（11）与 Lavi（12），其他版本仍为 Dao 与 Dizni。ID2014 系列（ID2014、ID2015、CN2014 0.2.51.002）的现金查询（C2S 825）现在会收到 S2C 105 回复，与 KR2015 一样刷新商店现金对话框。

- 原生创建角色对所有已核对创建流程的旧版开启：KR2015、CN2015、ID2014、ID2015 与 CN2012、CN2011 一致。新账号以及尚无创建记录的旧账号在进入游戏前先选角色，钱包与背包保留。CN2013 与 CN2014 0.2.51.002 在自身流程核对前保持关闭。`play_smoke` 现在会应答原生创建，并新增 `--accounts` 与 `--login-only`。

- `--dump-config` 现在输出合法 YAML：纯量写法会被误读的字符串（含 `: ` 或 ` #`、以指示符开头、空串、`~`、换行）加引号，其他值保持原样。

- 旧版商店购买规则改在 `shop` 行上声明，不再在代码里按客户端版本选择：`reject_repeat_function_10001` 与 `permanent_day_stock_types`，默认取 KR2015 的规则，CN2012/CN2011 各自声明差异。旧版现金查询与穿戴结果开关移入 `packets/builds.yaml` 的 capabilities。CN2012 与 ID2015 现在直接继承 KR2015，与封包表的基准一致，行为不变。

- 将已核对的原生改名卡接入 KR2015 基准配置，供 CN2013、CN2015、ID2014、ID2015 共享，并保留 CN2012 行为。登记 CN2013 自身 opcode，明确物品资源及研究范围例外，移除不再使用的公告服务依赖。

- 修正 legacy 审计的封包结构提取，避免动态布局参数后的字段名被误报为缺失结构，并支持嵌套调用及具名常量参数。

- 根据 CN2011 原生五字布局和独立核对的游戏 opcode，接入同一套人物引用普通泡封与击爆积分逻辑。

- 为 ID2014/CN2013 接入普通泡封与击爆上报，共享人物引用解码和已认证击杀者校验；八字阵营辅助字段保留未知含义，拒绝未支持事件及自我归因。

- 根据 CN2013 自身八字上报路径和已核对的游戏 opcode，接入共享的人物引用直接击杀积分；普通事件仍独立审核。

- 核对 ID2015 原生上报路径后，通过共享 legacy 逻辑启用普通泡封和击爆积分；保留事件类型过滤和独立的生存模式直接击杀路径。

- 为 KR2015、CN2015、ID2014、ID2015 接入已核对的生存模式直接击杀报告，复用共享得分流程。从人物引用解析玩家而非使用阵营字节，并校验已认证击杀者与配置的模式范围；保留 CN2012 原有处理，条件触发的连击事件不计为普通击杀。

- 将已启用的 legacy 积分上报解码迁入封包表，保留 KR2015/CN2015 混合宽度及 CN2012 八字格式；记录 ID2014、ID2015、CN2013、CN2011 的原生布局差异，布局覆盖不自动启用尚未审核的事件语义。

- KR2015、CN2015 的窄格式积分上报在进入共享击杀统计前过滤尚未支持的事件类型，保留已审核的泡封和击爆处理；通过实际挂载路由验证所有单字节事件类型及错误长度，避免非击杀事件被误计分。

- 为 KR2015、CN2015、ID2014、ID2015 启用回血、火箭筒、轰炸三个合作模式计数道具栏；加速和补手雷只在该模式下拾取立即生效。保留普通双栏及 CN2012 五栏规则，核对各版本原生列表分发器和有界拾取、使用路径。

- 奖池资源校验兼容 KR2015、CN2015 原始工作表中有界的空白尾列，同时继续拒绝超出支持列的有效数据、非法索引及不匹配的奖励编号。

- 移除 CN2016 对未挂载房间及对局插件行的无效覆盖，恢复独立配置和全版本配置的组合加载，保留该版本当前的登录及主菜单范围。

- CN2013 接入共享 legacy 地图补给、双道具槽及独立补弹效果。核对本版本的掉落、切换序列化函数和 opcode 编号，以本版本资源验证允许道具，并保留客户端本地效果处理，避免重复回送造成效果叠加。

- 按各自地图任务组和提示资源编号，为 KR2015、CN2015、ID2014 和 ID2015 接入共享 Boss 拯救任务。遵守零任务地图、KR2015 Frontal 地图差异及原生 Boss 出现时间，保留 CN2012 调度配置，并验证请求宽度、任务索引和卸载清理。

- 通过已有共享房间方言启用 CN2013 普通对局及结算，保留其原生封包布局和未知玩家槽默认值，接入已审核的计分板与奖励消费路径。验证两人加载、出生、结算、返回房间及再次开局；教程和额外模式继续逐项迁移。

- 为 KR2015、CN2015、ID2014 和 ID2015 接入共享模式 16 的 Boss 积分，保留 CN2012 已验收分值。按原生资源的积分单位编号使用可配置的录像分值，通过已有房间槽初始化姓名，只向会写入实时积分的客户端发送排名快照；拒绝变成僵尸后的积分请求和未经审核的原生适配挂载。

- 为 KR2015、CN2015、ID2014 和 ID2015 接入共享的僵尸 Boss 阵营转换与僵尸复活，保留 CN2012 已验收行为。核对各客户端 LifePoint 耗尽请求与转换函数，验证 CN2015/ID2014 阵营通知编号；早期和研究版本保持关闭。拯救任务、计数道具槽及积分栏初始化仍按独立能力继续审核。

- 为 KR2015、CN2015、CN2012、ID2014 和 ID2015 接入已审核的原生海盗翻牌奖励流程。每个版本使用自己的商品资源身份，缺少的物品直接从该版本奖池删除；十张牌的向量和从结算到翻牌的时序保持有界，早期或尚未审核的版本继续关闭。补充原生读取函数、事件阶段及限制重复数量的分组抽取说明。

- 为 KR2015、CN2015、ID2014 和 ID2015 接入共享地图补给、原生双道具槽及独立补弹效果，保留 CN2012 已验收的设置。记录 CN2011 较短的原生结构，复用有边界的通用封包读取器，并根据 ID2015 自身发送函数修正地图报告与积分优化包的 opcode 冲突。尚未审核的早期和研究版本仍明确关闭对应适配，等待其原生路径审核。

- 类型化结算发送器只注册了一部分时，在加载阶段明确报错，避免静默跳过计分板和奖励阶段。验证各 legacy 插件在并发结束请求下只有一个结算流程，并明确 KR final 独立的清理路径；已验收的 legacy 发包顺序不变。

- 为 KR2015、CN2012、CN2013、CN2015、ID2014 和 ID2015 增加已审核的原生中继种子表结构，将 relay 4/5/6 与 19/20/21 按版本提升为 `verified` opcode；种子数值继续使用未知字段，不臆造含义，并明确关闭尚未证明的 CN2011 与研究版本路径。运行时种子处理仍保持可选且未挂载。

- 增加可选的 `dialect.legacy.scene-seeds` 原生种子表接收器，覆盖六个已有独立种子表证据的版本。通过 `packets/relay-seeds.yaml` 解码 C2S 19/20/21，每个中继连接只保留有限数量的最新表并在断线时清理；在场景初始化顺序完成审核前，不广播这些数据，也不在默认 profile 中启用。

- 保留原生关键种子表的向量边界，并为可选种子行发布按版本选择结构的 `IMessageSender<NativeSceneSeedTable>` 编码器。编码器只能由调用方显式选择目标并调用，会校验目标版本的向量形状，不会自动广播或接入场景启动流程。

- 将可选原生中继端点邀请接入共享 legacy 地图加载流程，凭据长度由方言封包表提供，邀请失败时精确撤销；倒计时等待当前中继完成认证、初始化及游戏侧确认。独立审核七个版本的游戏连接 601，并完整保留 ID2014 的双标志报告。原生场景初始化和离场仍在审核，现有 profile 继续使用已验收的游戏连接路径。

- 为七个迁移版本增加可选原生中继成功回复发送器，根据各客户端消费路径审核空的加入与场景成功信号。宿主测试将真实请求和回复适配器接入共享生命周期，覆盖 CN2011 直接成功、提供者重载及连接清理。默认配置仍不启用监听器、场景种子初始化和离场切换。

- 为七个支持的 legacy 版本增加可单独挂载的原生比赛请求方言插件，按共享封包表完整读取加入与验证报告，发布限定协议的类型化入口，拒绝损坏或跨版本请求，卸载时移除全部路由。审核补齐十三个原生中继编号，不改变游戏连接流量；现有 profile 尚未启用中继监听、成功回包与场景切换。

- 共享比赛连接插件可按协议目录挂载原生凭据认证入口，缺少或重复的方言服务在加载时明确报错。默认保留携带账号的认证；直接成功握手无需挂载场景报告入口或发送器。现有 legacy profile 仍未启用原生中继封包适配器或监听器。

- 让各 legacy 版本共用房间签发的原生比赛凭据与人物身份接入：凭据限定签发版本和当前对局，重开后旧凭据失效，共用有上限的超时、替换及卸载清理。每个房间插件发布可独立卸载的协议目录；原生回复和场景切换审核完成前，继续不启用中继协议入口或监听器。

- 按各 legacy 客户端原生路径区分中继验证包的人物身份与登录账号身份；完整校验后再投影人物 ID 与昵称，按版本保留原生入场凭据长度和端点格式，暂不挂载中继接入。修正 CN2015 中继离场 20008 被误登记到游戏连接的问题，并固定 KR2015、ID2015 各自原生的中继离场编号。

- 房间退出和比赛连接替换时立即清理共享的连接关闭索引，避免反复进退房间遗留连接记录；延迟到达的旧连接关闭事件不会影响当前座位。

- 在共享封包表中补齐原生装备属性、期限列表及中继校验结构，保留 CN2012 的字节列表尾部和 CN2015 额外布尔字段。现有装备与房间出站字节保持一致；区分 CN2011 直接报告连接成功的原生握手顺序，尚不启用中继接入。

- 增加按共享封包表读取嵌套结构、计数列表及固定数组的能力，各 legacy 版本的房间列表地图设置组合也改为复用此读取器，删除独立维护的字段类型解码并保持输出字节一致。在接收前拒绝异常计数、过深嵌套及非空的服务端占位列表；本次基础设施修改不启用中继握手。

- 将 legacy 原生中继地址、会话 key、加入请求及连接报告整理为共享封包表，逐版本固定早期与后期布局差异；尚未审核完整的握手继续不启用，已验收的游戏连接发包保持不变。

- 将 CN2013 的共享即时复活物品配置与未启用的原生协议保持一致，并验证其预备普通对局 codec；线上仅广场与等待房间的入口继续保留。

- 启动器目录可按实例提供经过校验的公共客户端下载推荐；未配置时省略该字段，保留可移植预设及内置下载源默认值。

- 补齐 CN2011 共享踢人和离房回复链中的原生 S2C 501 房间重置及 S2C 511 移除座位，避免踢人后因缺少编号而关闭房主连接。

- 按原生调用链补齐 CN2011 等待房间操作的已审核编号，区分加入房间 403 与快速匹配 111，并在不加载 catalog 候选的情况下验证共享操作。审核 CN2013 复活、结束和奖励阶段路径，在封包表修正其较短的复活确认；其比赛启用开关继续关闭。

- 让仅等待房间与完整 legacy 对局共用同一组类型化房间操作。按已审核原生路径接入 CN2013 加入房间、准备、设置、换队、聊天、封锁座位和踢人，修正其没有尾部字段的加入请求；比赛入口继续关闭，其他版本封包结构不变。
- 通过共享计分板投影填入 CN2011 已确认的结算经验条字段、已占用玩家行显示开关及原生升级动画标志；尚未确认含义的本局奖励数值列继续保留未知字段。

- 根据已审核的 722/725/726 原生入口，将 CN2011 结算输出接入共享结算及奖励保存流程，保留其封包结构和未启用的击杀上报接收开关。

- 将原生结算输出与击杀统计接收分开。ID2014、ID2015 根据已审核的原生 reader 挂载共享结算表、奖励阶段及奖励保存流程，尚未审核的积分上报 codec 继续关闭；保留 CN2012 已验收的结算顺序及 CN2011/CN2013 当前启用范围。
- KR2015、CN2015、ID2014、CN2013 单独运行的 profile 默认使用部署包内原生资源表路径，保留环境变量覆盖和各版本商品售卖的启用范围。
- 将 KR2015、CN2015、ID2014 和 ID2015 的原生游戏内 Esc 请求接入共享的类型化离开房间流程，保留 CN2012 自身的请求编号及教程离开接管。审核 CN2013 对应的 C2S 506，保持当前仅广场和等待房间的启用范围。

- 修正 ID2014 和 ID2015 开局座位的位置字节：开始发包中的已占用座位现在携带原生 `spawn_index`，等待房间记录仍保留空位哨兵值。此前共享封包表遗漏了这个值，导致两个版本进入加载时位置未初始化。

- 为早期国服及 ID2014/CN2013 的原生进入地图计分板填入当前房间成员，复用共享 ScoreData 表，保留纯标量入口的发包顺序、教程默认值和结算路由。

- 按 CN2011 自身的原生分发器补上完整计分板 S2C 722，区别于后续版本的 729 和 catalog 冲突候选；保留独立的积分上报启用条件及原有计分板字节。

- 通过共享构建数据将原生刷怪组的本地执行去重同步至 ID2014 和 CN2013，避免房主重复执行并保留向其他玩家转发；保留 KR2015、CN2015、ID2015、CN2011 的网络回送和 CN2013 当前仅等待房间的启用范围。

- 核对 CN2013 自身的开局、加载和结束编号，将 catalog 中冲突的完整计分板编号确定为 S2C 729；通过共享封包表差异修正其完整实时及结算计分板，保留其他 legacy 版本的字节回归和当前仅广场/等待房间的启用范围。

- 按 CN2013 自身原生 reader 修正进入游戏封包：十六个积分阵营、八个较短的默认玩家行和一个尾部整数。将五标志开局请求、两字段加载与开始回复纳入共享 legacy 回归；战斗启用仍待其余生命周期审计完成。

- 增加需明确选择的有界 SQLite 本机存档：统一大厅身份、创角教程状态、物品期限、钱包经验与原子购买，并持久化启动器凭据、检查存档版本和释放停服资源；原有 MySQL 与临时模式继续保留明确含义。

- 将普通 legacy NPC 波次绑定与单人教程入口分开，为 ID2014、ID2015 挂载已审核的波次交换，保留原有教程启动方式和早期国服发包行为。
- 修正 KR2015 地图主题的证据：游戏设置第三个字符串与 Lua 地图目录、波次前缀不同；保留兼容字段名及封包字节。

- 新增默认关闭的 Host `--managed-stdio` 重定向控制：完整启动后输出一次就绪标记，限制停止指令长度，并在 Windows/Linux 收到 EOF 时优雅退出。

- KR2015、CN2015、ID2014、ID2015 从共享基线启用普通快速加入，使用各自原生封包布局；保留早期国服字节，并区分专用队列和再次进入队列。
- 修正 CN2013 较短的匹配请求，通过原生 RTTI 和分发表审核请求及返回包编号；保留当前仅广场与等待房间的战斗边界。

- 共享 legacy 更换房间设置时保留玩家的地图选择，与实际场景目录分开处理，并完整保留原始设置封包。

- 共享 legacy 房间在重复开局时保留等待房间的地图选择，入场和救援初始化使用本局解析地图，回房时清理临时地图绑定。

- 将共享 legacy 对局倒计时默认设为四秒，显示原生 3/2/1/开始序列并消除开头空白阶段；保留 CN2012 已有时长和可配置覆盖。

- CN2013、ID2015 从各自原生资源表启用服装和饰品互斥，复用共享装备选择服务；未审核的商品目录和战斗路径继续保持原有开关。

- 增加默认关闭的共享登录票据桥，通过现有登录账号字符串兑换限定版本、单次使用的 Launcher 票据；无效票据不会回退到创建账号。

- CN2011、CN2015、ID2014 从各自客户端资源表加载服装和饰品互斥部位；早期本地化表可配置原生分类名，无法产生穿戴部位的表会明确报错。

- legacy 登录回复后立即发送已加载的昵称、等级和钱包，通过共享 UserStat 编码器在背包与主菜单之前完成初始化。
- 将已审核的 CN2013 登录、背包、广场与房间浏览 profile 加入多版本服务，端口为 7230；战斗功能仍按能力开关限制。
- 通过共享绑定解析原生与早期国服的角色名称检查别名，补齐 KR2015、CN2015 和印尼 legacy 版本的原生空包创建通知证据。

- 从继承的包构建数据解析 legacy 地图修改请求字段映射，保留未知字段偏移名称并移除按标量出现次序选择的逻辑；在挂载类型化房间前校验映射。

- 将 legacy 房间地图设置修改改为按字段名及已证明的地图语义定位，保留受支持版本的字节行为，拒绝缺少已核对字段映射的地图修改。

- 将 Mid-CN 独立菜单的手写编码替换为共享包表和按字段名投影，保留现有房间/菜单封包字节，避免跨插件依赖具体实现。

- 将 legacy 广场位置上报、广播、入场移动及商店购买请求迁入共享包表；保留浮点原始位和购买布尔参数的严格校验。

- 将 legacy 装备和预设请求、购买回复及阵营转换接入协议表，保留原生长度、结果码与能力开关。

- 将 legacy 主菜单和广场场景切换接入同一张已审核的四字节布尔协议表，保留无提示封包内容与发送顺序。

- 将 legacy 广场独立玩家记录和空地图设置默认值接入共享协议表编码，保留原生字节及可选封包的发送条件。

- 将 legacy 完整房间进入与教程地图设置推送改为嵌套协议表编码，保留主菜单和选关字段清理、全部座位以及早期国服独立地图设置。

- 将主菜单座位和独立房间设置接入共享协议表编码，保留主菜单默认值，并将按位置设置准备标志改为使用已审核字段名。

- 将 legacy 房间列表回复接入共享协议表，保留七版本嵌套设置、分页行为及十二字节空列表回复。

- 将 legacy 房间、广场和教程的座位更新及离座封包改为嵌套协议表编码，保留七个版本的原生记录布局与空位、离座字段值。

- 将 legacy 场景切换、可选会话初始化和加入结果迁入封包表；保留原生标量封包及发送顺序。

- 将 legacy 房间列表与加入请求改为封包表解码；移除加入请求尾部开关，保留七版本原生长度及有符号字段。

- 将 legacy 房间聊天、席位开关和踢人消息迁入封包表；恢复 ID2014 较短的原生聊天尾部，移除代码布局开关。

- 将 legacy 实时积分包及统计向量迁入封包表；保留四个已核对版本的封包字节和旧版本整张记分板更新路径。

- 通过嵌套封包表编码完整 legacy 入场包，移除版本布局开关和预编码计分块，保留各版本原生组合顺序。

- 移除未使用的四字段入场分支，仅保留已核对的原生标量与嵌套计分入场路径。

- 将 legacy 复活请求与回应迁入封包表；保留其他六个版本的封包字节，恢复 CN2011 原生双字段复活格式。

- 将 ID2014、ID2015、CN2014 客户端驱动教程在原生自动开始回调时接入共享隐藏房间；保留选定地图与单人模式，共用加载、结束和清理路径。

- 将已核对的教程选关列表及记录迁入包结构表；保留七版本空列表回复，并拒绝尚未核实的 CN2011 非空记录。

- 已核实服务端入场路径的 legacy 教程接入共享隐藏房间、加载屏障、可取消倒计时、复活与补给状态；保留原生教程结束路径，不触发普通奖励结算。

- 教程与普通对局共用 legacy 开局发送路径，通过显式教程标志保留原生加载门控、时钟字段和倒计时包顺序。

- 将完整 legacy 教程开始请求迁入包结构表，移除代码中的 MapMeta 布尔字段数量开关，保留七个版本的原生布局和严格帧校验。

- 教程与普通对局共用表驱动的 legacy 加载报告读取；移除未使用的重复匹配形状编码器，保留各版本原生匹配封包测试。

- 将 legacy 教程刷怪组和入站波次、完成标志迁入公共包结构表，保留原有字节、严格校验和队员转发行为。

- legacy 普通房间与教程共用表驱动的房主槽位及波次回复，保持七版本四字节封包和现有开始行为。

- 恢复 ID2014、ID2015 和 CN2014 原生积分行宽度、固定行数、哨兵默认值与尾部；
  ID2014 超时结算改用嵌套积分块，CN2014 明确保留两字节队伍比分。

- 按 CN2015 原生读取顺序，将胜方与标志直接放在队伍数量后，移除两个不属于该版本的列表计数；
  保留 CN2016 的现有布局。

- 将 legacy 完整积分块的头部、队伍块、行数及尾部迁入封包表，移除五个布局开关，
  保持七个版本当前结算字节不变。

- 将 ID2014 初始积分块迁入封包表，支持固定结构数组；按原生重置函数恢复全一位值，
  修正此前将槽位及行内哨兵误写成浮点 NaN 的问题。

- legacy 积分行统一使用共享封包编码器，保持七个版本的空座及含奖励积分表字节不变；
  原生布局差异继续单独核对和修正。

- legacy 回合结束标志和标量超时比分共用表驱动编码，保持七个版本的现有结果计算、
  结算顺序及原生封包字节。

- 将 legacy 进入游戏封包的标量头尾迁入共享表，保留现有积分块字节，
  修正 CN2014 错误继承 ID2014 嵌套 ScoreData 布局的问题。

- 普通房间与教程共用表驱动的加载完成、加载标志、倒计时和开始游戏封包。
  保持六个版本的原有字节，按 CN2014 原生布局补回第三个加载标志和开始字段，
  避免继承 ID2014 的较短封包。

- 将准备、进入游戏与地图设置变更请求迁入共享 legacy 封包表，核对全部七个版本。
  修正 CN2014 错误继承印尼2014五个 MapMeta 布尔字段的问题，按其原生布局读取六个；
  保持 CN2011/CN2012 请求字节及现有开局时序。

- 将 legacy 房间未选地图的显示初始化提升到 KR2015 基线，保留后续客户端的 NPC 波次表字符串，
  并保持 CN2011/CN2012 已验收的地图选择显示方式。

- 普通房间与教程共用封包表驱动的自动开始编码器，七个 legacy 版本均保持原生四字节开关。

- 开启 KR2015 基线的序章 NPC 模式及加载/波次处理，保持已核对的派生版本行为，
  并把教学频道回复迁移到共享封包编码器。

- 新增可选的完整解码封包记录：独立、有容量上限的 JSON Lines 后台写入器记录双向 payload，
  明确报告溢出，不改变线上封包字节。

- 新增 Linux 框架依赖型发布脚本及 Debian 13 连接已有 MariaDB 的 Docker Compose 模板。
  保留 Windows 发布方式、全版本监听端口、持久化数据包日志与有容量上限的 Docker 运行日志。

- 将物品使用请求及回复布局提升到 KR2015 基线，核对七个 legacy 版本。使用共享、有边界检查的
  标量读包器，解除物品使用对创建角色绑定的依赖，保持 CN2012 发包字节不变。
  未核对的请求编号和物品资源不会自动启用。

- 新增 KR2015、CN2011、CN2012、印尼2014及印尼2015 共用登入器，在 `%LOCALAPPDATA%/FantasyTale/BubbleFighter` 記憶同一
  帳號、伺服器及各版本端口，適配原生認證輸入；兩份 JSON 格式版本與客戶端版本分開。
- 国服 GameWidget 替身在提权启动的客户端拿不到启动器环境变量时，改读用户目录的启动器设置；
  此前会退回默认账号，导致已有玩家被当作新账号。
- 启动账号只保存在用户目录，通过子进程环境交给更新后的认证 DLL，不再写入待分发客户端。
  NMCO 日志改放用户目录；印尼版本使用游戏内登录，不再在外部询问账号。

- 按原生 RTTI 和实际构造的封包对象修正 KR2015 匹配及离开队伍请求名称：匹配使用 112，
  离开队伍使用 202 空请求。

- 将武器预设栏回包迁入基准封包表，kr-812、cn-717、id-400、id-1809、cn-706 保持现有的
  47 字节列表。挂载时拒绝缺少已审核预设栏能力的路由。核对六个 legacy 版本的即时复活
  请求布局，未核对的消耗道具资源不会自动启用。

- 将 legacy 背包槽、装备列表和登录回包迁入共享封包表，以 kr-812 为基准，保留原有封包字节。
  通用序列化在分配超大包体之前限制资源；现代登录回包继续独立于 legacy 表的继承关系。
- 核对七个 legacy 版本的原生公告，通过有验证的继承能力声明启用共享发送器。九项通用调度
  和翻牌参数移入 legacy bundle，模式、资源和奖励编号仍保留版本差异。
- 新增可重复运行的 legacy 依赖审计，读取 Host 实际合成的配置及封包表，区分静态依赖齐全
  与客户端实测通过，并正确识别原生空包，不掩盖缺少布局的问题。

- Update40 让 CN2012 由服务端自行建立的房间（快速加入）带上客户端手动建房时的 GameSetting 值：+32 为 KillLimit 20，+36..+48 为 TimeLimit 240000，两个布尔为真，+68 为 1，+72 为 -1（cn-324 sub_582C00、sub_57D470）。+68 为 0 时海盗波次脚本收到难度 0，触发原生 Lua 退出提示。建房请求现在优先采用所选地图键而不是地图目录。

- Update39 修正 CN2012 海盗及新版教学重复生成 NPC 组的问题：客户端已将自己的请求放入本地执行队列，服务端只转发给房间内其他成员。已核对 Lua 退出提示及本地队列路径，实际游戏验收仍待确认。

- Update38 支持为没有原生售卖条目的旧版按天物品配置服务端售卖 ID 和期限，并按配置方案限定范围。CN2012 试上架僵尸大锤（部下）207201 和黄金棉花糖狙击枪 L3-HC 204912，均为 1000 金币、30 天。最长期限开关同时压缩普通商品期限选项和重复礼包，核对后的 CN2012 目录可完整发送 462 件商品，包长 57800 字节。两把新增武器的原生客户端显示与使用效果待实测。

- legacy 广场断线成员保留到串行离场通知发送，避免残留角色及过早复用座位。独立教程选关返回菜单时重新安装本人的菜单会话和装备，再创建角色模型。
- Windows x64 部署发布改为依赖服务器运行时。发布脚本拒绝旧文件混入并检查未打包共享运行时；服务器需安装 x64 ASP.NET Core Runtime 10.0。

- Update33 将 CN2012 海盗翻牌移到普通结算原本返回房间的节点，并用原生 S2C 736 空通知先关闭翻牌再恢复房间。旧版结算展示计时不再占用房间执行器，实际阶段仍串行执行；关闭房间取消后续阶段。

- Legacy 登录与背包一起初始化并读取持久化钱包，修正自动创建的新账号缺少钱包导致结算奖励事务失败。

- Update32 修正CN2012翻牌显示编号为原生Stock编号，实际发奖仍用装备编号，并启动校验商品数量和期限。结算开始10秒后打开抽奖，以12秒为展示截止时间，返回房间前补发原生结算结束通知。

- Update31 还原 CN2012 海盗通关的原生翻牌流程，校验每人一次选择，事务保存道具，揭示未选牌并支持超时自动选择。模式、奖池、权重和时序均可配置，初始奖池是暂定值。纠正成功回应编号为742；原生界面仍需实测。

- Update30 保存 CN2012 原生两个道具槽并回应换槽请求。满槽拾取不再覆盖已有道具，使用时消耗当前槽并发送对应效果。死亡清空持有道具，僵尸 Boss 计数道具保持独立。

- Update29 在 CN2012 普通结算流程前补齐原生海盗通关／失败通知，海盗管理器据此选择结算横幅和文字。停用 Update28 临时团队总分投影，僵尸 Boss 结算编码保持不变。非玩家单位的生命周期请求不再误报玩家复活警告。

- Legacy 教学加载时通过教学会话状态接收已校验的地图补给报告，避免误入多人房间查询而断开教学玩家；普通多人补给处理保持原逻辑。

- 补充上架支持原生 StockSet 礼包、沿用客户端原表货币，以及显式跳过缺少定义的商品。已核验版本补充指定的僵尸斯坦部件、狸猫团套装及 HC 武器、黑火推进器；不凭空创建缺失的售卖期限。

- Update27 在 CN2012 原生 Boss 出场前停止拯救任务调度，全员转成僵尸时结束合作回合。快速加入立即检查房间，并在等待窗口内持续匹配。异步包观察者按连接收包顺序等待转发完成，避免产生未等待的转发任务；Boss 可见性仍需多人实测验收。

- Update26 新增独立配置的经验、金币倍率，部署 bundle 两者均默认 100 倍，并提供首版积分奖励近似模型。CN2012 经原生积分包上报的成功营救增加两点个人积分。字母评价、勋章及海盗结算仍在调查和验收。
- CN2012 海盗模式 101 恢复直接胜负编码：原生阵营分支判断的是模式 10，游戏场景构造函数保留实际模式号。僵尸 Boss 编码和结束包第二数据字保持不变。

- Update25 将包和连接诊断移入有界后台写入队列，异步写文件、批量刷新、明确记录溢出数量，并在正常关闭时清空队列。Windows 启动器恢复启动完成及警告/错误的可见提示。CN2012 僵尸 Boss 在加载屏障与原生开始后立即初始化排名，避免等到首次击杀；不发送会虚增客户端击杀次数的零积分奖励。

- Update24 默认关闭逐包控制台镜像，避免控制台队列阻塞网络线程，完整 packet 文件仍保留；Windows 启动器窗口回显改为可配置。连接事件及运行日志继续保留。CN2012 积分配置增加十六类 NPC 资源名称注释，分值已采用用户提供的同代视频统计。

- Update23 恢复 CN2012 结束包第二个数据字中的结算标志，并按接收者原生阵营编码海盗胜负；僵尸 Boss 保留原有胜负语义。结算行排名装饰字段保持未知字段默认值。命令兼容 ASCII `!` 和中文全角 `！`；PvE 复活已获实测确认。
- CN2012 僵尸 Boss 新增原生积分与排名消息。积分表可配置，目前每个 NPC 计分单位暂给 1 分；原始分值和实际 UI 展示仍需验收。Contracts 2.24 提供接收者上下文及积分类型消息。
- 现有解析器请求的运行 opcode 名称缺失时，可采用方向和连接一致的唯一研究候选；每次采用均明确记录来源警告，运行 YAML 不变。Abstractions 8.12 新增可选候选查找接口。

- 数据包日志可从研究 catalog 显示带 `?` 的未审核候选名；运行 opcode 表与路由独立。同步修正 catalog 数值排序及配对候选工具的行号写入。
- Legacy update21 修正 CN2012 十二字节匹配请求、道具使用确认中的实际种类、海盗未选图展示和提前复活策略。原生客户端仍待验收。

- 远程插件：新增的`loader.remote`插件启动说JSON行stdio协议（v1）的子进程，把它们描述的插件发布为有归属的定义来源，并通过执行后端接缝为这些定义的行提供服务。配置按所描述的schema校验，处理器注册作为效果由行持有，行长度、未完成请求和会话数都有上限，请求超时和进程丢失会使相关行故障。同时提供纯标准库的Python SDK `bff_plugin`（`sdk/python`）、echo示例、`tests/fixtures/remote-protocol`下共用的一致性fixture及运行器；详见`docs/remote-plugins.zh.md`。内核、Abstractions和Contracts均未改动；重启崩溃的进程不属于该加载器。

- 新增`webui`插件行：内嵌预构建的Vue 3管理Web界面（en、ko、zh-Hans、zh-Hant界面语言；hash路由页面含overview、players、rooms、plugins、opcodes、logs、settings），以`IAdminWebContent`发布界面内容，并以`IBuildReleaseLabels`发布各构建的发布名；`admin.http`在有行发布内容时于其监听根路径提供该页面，禁用`webui`则只保留API。`admin.http`新增`GET /api/overview`、`GET`与`PUT /api/settings`（唯一的`crossVersionPlay`字段读取为null且PUT返回409，因为尚不支持跨版本对局）、由新配置键`log_buffer`决定大小的有界环形缓冲支撑的`GET /api/logs`（默认2000，范围16-100000）、`GET /api/opcodes`的`profile`过滤、`GET /api/rooms`与`POST /api/rooms/{id}/start`，以及`GET /api/sessions`新增的`player`与`matchBound`字段。`net.tcp`监听行发布`IListenerEndpoint`，overview据此输出各构建的端口及监听状态。Contracts 2.16.0新增`Admin.IAdminWebContent`、`Admin.IBuildReleaseLabels`与`Listeners.IListenerEndpoint`。

- 管理Web界面增加令牌登录并修复若干问题。`admin.http`现在无需bearer令牌即可提供界面文件，而每个`/api`端点仍需令牌（`/health`保持开放）；客户端在每次API调用中携带令牌，仅保存在sessionStorage中，收到401时显示登录视图，并提供“退出登录”按钮。操作码表与端口表按数值排序，日志页头在窄屏上重新排布且换行的日志行带悬挂缩进，插件行指标统计活动行并分别报告已停用与故障行（`GET /api/overview`的`rows`新增`active`与`disabled`），插件版本显示短提交号，宽表格在右侧边缘渐隐。`webui`行把`cn-706`标为“CN2014 0.2.51.002”。

- Abstractions 8.11.0增加由确切注册句柄授权的`ReplaceAsync`，原子发布定义来源。候选在获取前预留旧加新的来源/定义容量；非法候选保留旧修订，迟到的前任释放不能删除后继id。隔离契约实际身份决定scope重建及激活隔离。来源生产者准备独立资源；相同key生命周期冲突在回调前拒绝生产者，新工厂等待较早同key批次清理，独立key仍可继续。发布不等于消费者收敛或业务回滚；保留reader及清理失败继续持有归属、容量与诊断，不承诺自动重试或物理GC。

- 服务`run`先启动普通bootstrap行，再通过配置协调接纳组合后的游戏profile。通用不可变`PluginStartupPlan`以一次固定启动配额保留初始及延后条目的身份预留；未解析的延后定义与隔离契约在来源发布后校验。`--bootstrap-dir`选择发布或自定义的`run/profile.yaml`；`--dump-config`仍在加载前退出。启动成功要求协调收敛且所有启用行均为Active；失败或取消始终等待关闭并保留原始错误。该服务启动切片本身未增加来源替换；生产collectible加载和远程SDK仍需分别推进。

- Abstractions 8.10.0增加`IPluginExecutionBindingFactory`显式同意接口及`IPluginExecutionBinding.BoundFactory`，复用已解析修订的不可变工厂而不替换透明包装工厂的策略，并提供隐藏已撤销来源的primitive `IPluginHost.Definitions`快照。原生加载移入仅引用Abstractions的独立库，普通`loader.native`行发布有归属的定义批次。`plugins list`使用真实的YAML配置Host bootstrap，提供`--bootstrap-dir`、可重复的`--plugin-root`、经校验的来源预算和显式seed模块文件名排除清单。最初仅面向清单的切片曾为游戏`run`保留兼容发现路径，现由上一条分阶段启动实现接续。生产原生上下文仍不可回收；该加载器提取本身未增加来源替换、远程SDK或物理回收。

- Abstractions 8.9.0增加复制的后端中立定义、有归属的来源批次及注册诊断。宿主管理的激活在生产者运行前预留来源/定义容量及协调key；真实宿主查找和激活消费原子接纳的批次。撤销阻止获取、排空消费者并清除被撤销的已提交缓存，批次真实清理成功后才释放容量。观察者取消不遗弃归属。经校验的启动`definition_source_reserve`及`definition_reserve`均默认0，并出现在配置dump中。尚未交付来源替换、bootstrap迁移或物理回收；撤销使已配置隔离契约失效时，可能失败并保留预算/资源，不保证自动重试。unknown或process-lifetime代码逻辑退役不证明物理回收。

- Abstractions 8.8.0公开可选的`IPluginModuleSource`及`PluginModuleCode`，供独立进程内后端工厂实现。定义捕获模块身份、不透明对象的引用身份、来源说明、可选强引用保活对象及声明的生命周期能力；账本检查不调用后端来源getter。来源必须对每个工厂保持稳定：获取时发生漂移会被拒绝，已返回的执行器按归属契约清理；不同代码修订需要绑定新的工厂，等价快照仍合法。自定义相等比较和刷新计数不代表代码身份，collectible能力不证明GC完成。未提供来源的工厂仍为unattributed。本次不增加wire IDL、SDK、动态catalog或bootstrap loader。

- 原生加载持续持有每个新上下文，直至整个操作的校验与选择提交。失败按逆序处理本批次新建上下文，保留原始错误，并通过弱关联诊断及CLI报告保留、已请求卸载、清理失败或未返回状态。路径/解析器准备先于上下文登记；成功代码保活及借用程序集不受回滚影响。生产加载仍不可回收，卸载请求不证明已回收。

- 原生模块发现支持同一程序集中多个经过验证的插件定义。刷新按目标定义id选择，不再假定每包只有一个类型；兄弟条目可继续持有旧修订。单定义加载API仍保留恰好一项的契约，缺失或重复身份明确失败。本次不增加动态目录接纳，也不改变代码回收策略。

- Abstractions 8.7.0新增经过验证的逐行代码刷新策略，以及独立的代码生命周期诊断。默认允许原有刷新行为；`restart_required`在清理旧实例之前拒绝涉及代码刷新的生命周期操作，包括受影响依赖行。仅修改配置的收敛仍可执行。实例快照区分操作策略与Unknown、ProcessLifetime、Collectible代码能力；可回收不等于已回收，需要进程退出才能回收也不代表服务故障。

- 组配置中的`rows`显式null或标量现在会以准确的文件和字段诊断被拒绝。省略行列表或使用空列表仍合法，bundle/group名称列表原有的null行为保持不变。

- 原生定义现在显式保活其加载上下文，已解析工厂保留所选加载器。释放单个激活不再卸载仍可能被定义缓存或兄弟实例持有的代码。内部collectible加载路径用于有界生命周期验证；普通目录加载仍保持noncollectible，生产策略另行实施。

- 宿主模块归属按不可变代码修订记账，与调度键及刷新计数分离。原生修订使用实际加载的程序集身份，无法提供代码来源的后端明确标记为unattributed。待完成获取、激活持有、保留清理及独立的已提交定义缓存共享有界记账，目录固定引用单独列出。关机保留可复用缓存，退役条目在真实排空后释放缓存归属。本次未启用collectible加载，也不证明程序集已回收。

- 独立验证工具明确区分报告与验收证据。TRX汇总使用有界不可变输入快照，核对运行级诊断及计数，并警示重复输入。夹具探针改为读取运行时列出的实际上下文数量，自动构建夹具，并明确不证明生产重载成本或collectible回收。两工具均补充双语使用说明及独立验证步骤。

- 生命周期准入在发布配置或调度工作前，原子预留完整模块集合。重载取消只释放本次激活实际取得的槽位，记账错误不再让关机观察者永久等待，已捕获的协调键保持定义原有的校验契约。停用条目在旧激活归属排空后显示已接纳的新定义。监督任务在effect释放边界发布公开完成结果，owner清理结束时其任务终态必定已经可见。

- 宿主维护内部模块账本：每条被接纳的生命周期执行按预留范围内的coordination key持有scheduled租约，模块代次仅在显式reload成功解析时推进。这是为后续回收工作准备的归属记账，不声称任何collectible回收。

- 配置组合新增可选的`groups`：每个`profiles/groups/<name>.yaml`在顶层bundle之后、补丁之前按名单顺序深度优先展开（先`bundles`，再嵌套`groups`，后内联`rows`），贡献的行在`--dump-config`中来源记为`group:<name>`；组环（报出链路）、嵌套超过16层、未知键、缺失组或组内bundle文件、`rows`不是列表都会以`ConfigException`使组合失败，环境变量替换与补丁语义保持不变。

- 定义与目标状态重绑定在不改变契约的情况下落地8.6协调器。保留Entry可在保持身份的前提下切换plugin、版本约束、client build、lineage、disabled及effect capacity。按row id的定义缓存只服务身份一致的descriptor，失败重绑定保持旧activation可诊断并向新desired定义重试；启动预留仍固定；归属期间按declared与owned effect capacity的较大者计费直至真实排空。移除被其他保留行Overrides或parent引用的Entry的候选整体拒绝；启用plugin缺失的驻留行立即报错，不再毒化后续协调。

- Abstractions 8.6.0增加显式启动Entry/元数据预留与可见退役。完整候选可新增已有catalog定义的条目或移除旧身份；已接纳及退役条目共用固定容量，真实排空前不能复用身份。保留的失败后端不会提前释放供新执行器使用的协调key。

- Abstractions 8.5.0增加runtime父Entry和按服务名称隔离。既有Entry可通过配置协调移动或改变隔离，依赖规划和生命周期预留使用实际realm key，子清理结束前保留父资源。Profile解析保留带引号的null字符串，同时正确解释YAML空值。动态成员增删及模块回收仍待完成。

- Abstractions 8.4.0通过`IPluginHost`增加固定Entry配置协调。配置变更共用一次依赖执行，保留手动目标状态，排空后再替换；相同提交在仍有待完成或失败工作时不能假报应用成功。配置revision独立于activation generation，暂不支持树拓扑及realm变更。

- Abstractions 8.3.0增加已验证的每Entry配置捕获预算。Host及独立Context/配置输入转为有界只读快照，调用方后续修改不再改变激活配置。动态配置reconcile仍是独立阶段。

- Abstractions 8.2.0新增归属执行工厂，Host定义可不包含CLR实现类型；原生激活也遵循统一的后端生命周期、声明和协调检查。配置树reconcile及collectible模块回收仍是独立阶段。

- Abstractions 8.1.0使独立的严格生命周期命令和目标请求共用范围协调，关机会等待两条路径排空；缺失依赖及带消费者卸载仍明确报错。

- Contracts 2.15.0新增共享服务接口`BubbleFighter.Fantasy.Contracts.Fighters.IFighterRoster`，在插件隔离重载之间保持稳定类型身份。
- Fighters 2.0.0移除`BubbleFighter.Fantasy.Fighters.Data.IFighterRoster`。使用者须改为共享契约命名空间并重新构建；角色存储实现和包格式保持现有行为。
- 架构测试检查两次独立catalog发现中的服务声明身份，并在两种行顺序下验证真实功能图的目标重载。

### 海盗奖池（update35）

- CN2012 原生金币、消耗品、服装、完整套装翻牌奖励，事务保存真实发奖。
- 预留服装/金币数量，按期限权重抽取，同物品最多三张且降低重复概率，原生资源校验。
- 保留已确认正常的结算及十二秒翻牌时序。

- 补充上架猛虎HC套装、指定HC/龙鳞枪械系列及棉花糖颜色变种；缺少原生售卖资源的商品跳过。

### 原生商店商品补充（update36）

- 上架永久封测纪念T恤、鞋子，以及粘稠雷、糖果雷、雪人雷、龙鳞雷、超级红五星雷，保留原生货币与售卖期限/数量。

### 原生近战商品补充（update37）

- 将CN2012全部26款有原生售卖条目的近战武器加入补充配置，包括步行僵尸铲、毒袋僵尸大锤；已有在售商品保留原价格和数量/期限。
- 无原生售卖条目的商品等待服务端自定义Stock客户端验证。

## 尚未发布

- 新增可选公共启动器 API 插件，支持有界账号会话、绑定版本的启动票据、客户端预设和服务端访问策略。游戏协议中的票据消费仍待接入。
