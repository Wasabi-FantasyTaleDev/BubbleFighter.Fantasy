using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Shop;
using BubbleFighter.Fantasy.Contracts.Wallet;
using BubbleFighter.Fantasy.Dialect.Messages;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Shop.Data;

namespace BubbleFighter.Fantasy.Shop.Tests;

/// <summary>
/// The test-side surface of the former in-plugin shop writer, kept so every pin written against it keeps its
/// exact hex through the S6 move: each member delegates to the dialect writer that now owns the bytes
/// (<see cref="ShopCatalogBinding"/> through the packet tables for the modern bodies, <see cref="ShopCatalogWire"/>
/// for the legacy per-build forms) or to the logic-side tree builder. No production code owns this class.
/// </summary>
internal static class ShopWire
{
    /// <summary>The modern build the pin writers write for; the recording senders share it.</summary>
    internal const string Modern = "kr-1256";

    /// <summary>The repository's packet tables, loaded once for the pin writers.</summary>
    internal static readonly PacketTables Tables = LoadTables();

    /// <summary>Byte length of a default-constructed <c>ShopItemCardInfo</c> on the wire (kr-1256 <c>sub_11400D0</c>).</summary>
    public const int EmptyItemCardBytes = 74;

    /// <summary>Wire size of one S2C 803 container-2 record with an empty nested vector (kr-1256 <c>sub_10D8370</c>).</summary>
    public const int ArticleSlotBytes = 24;

    /// <summary>Byte length of the plain S2C 803 body carrying exactly one container-2 record.</summary>
    public const int EquipHoldSingleRecordBytes = (3 * 4) + ArticleSlotBytes;

    /// <summary>Byte length of the empty <c>gspRepEnterShop</c> body.</summary>
    public const int RepEnterShopEmptyBytes = 40;

    /// <summary>Byte length of the empty <c>gspRepEnterTCShop</c> body.</summary>
    public const int RepEnterTCShopEmptyBytes = 20;

    /// <summary>Byte length of the refused <c>gspRepShopBuy</c> body.</summary>
    public const int RepShopBuyRefusedBytes = 25;

    /// <summary>Byte length of the missing-row <c>gspRepShopItemCardInfo</c> body.</summary>
    public const int RepShopItemCardInfoRefusedBytes = 4 + EmptyItemCardBytes;

    /// <summary>Byte length of the empty <c>gspRepRealCouponUse</c> body.</summary>
    public const int RepRealCouponUseEmptyBytes = 12;

    /// <summary>Byte length of the empty <c>gspRepCheckCash</c> body.</summary>
    public const int RepCheckCashEmptyBytes = 12;

    /// <summary>Early sale-window bound every legacy card carries; the value is pinned by the per-build tests.</summary>
    public const double LegacySaleFrom = ShopCatalogWire.LegacySaleFrom;

    /// <summary>Far-future sale bound; see <see cref="LegacySaleFrom"/>.</summary>
    public const double LegacySaleTo = ShopCatalogWire.LegacySaleTo;

    /// <summary>Default deployment card-count limit of the legacy catalog selection.</summary>
    public const int LegacyEnterShopCardCap = ShopCatalogWire.LegacyEnterShopCardCap;

    /// <summary>Main category the kr-812 / cn-717 dialog opens on.</summary>
    public const int LegacyEnterShopMainCategory = ShopCatalogWire.LegacyEnterShopMainCategory;

    /// <summary>Default subcategory under the landing main.</summary>
    public const int LegacyEnterShopDefaultSubCategory = ShopCatalogWire.LegacyEnterShopDefaultSubCategory;

    /// <summary>Empty-catalog pin of <c>gspRepEnterShop</c> (S2C 800), written through the tables.</summary>
    /// <param name="token">Token echoed when has-data is set.</param>
    public static byte[] RepEnterShopEmpty(long token) =>
        ShopCatalogBinding.WriteEnterShopReply(Tables, Modern, token, 0, [], [], false);

    /// <summary><c>gspRepEnterShop</c> (S2C 800) with the lucci / item-shop catalog, written through the tables.</summary>
    /// <param name="token">Token echoed to <c>dword_18E56E8+37</c> when has-data is set.</param>
    /// <param name="cards">On-sale non-cash stock sets.</param>
    /// <param name="categories">Shop-visible categories.</param>
    /// <param name="mainCategory">The main category the dialog opens on.</param>
    public static byte[] RepEnterShop(
        long token, IReadOnlyList<ShopCardRow> cards, IReadOnlyList<ShopCategoryRow> categories, int mainCategory) =>
        ShopCatalogBinding.WriteEnterShopReply(Tables, Modern, token, mainCategory, cards, categories, false);

    /// <summary>Empty-catalog pin of <c>gspRepEnterTCShop</c> (S2C 801), written through the tables.</summary>
    /// <param name="token">Token echoed when has-data is set.</param>
    public static byte[] RepEnterTCShopEmpty(long token) =>
        ShopCatalogBinding.WriteCashShopReply(Tables, Modern, token, []);

    /// <summary><c>gspRepEnterTCShop</c> (S2C 801) with the cash catalog, written through the tables.</summary>
    /// <param name="token">Token echoed to <c>dword_18E56E8+38</c> when has-data is set.</param>
    /// <param name="cards">On-sale cash stock sets.</param>
    public static byte[] RepEnterTCShop(long token, IReadOnlyList<ShopCardRow> cards)
    {
        // The sender refuses an empty cash catalog before it writes, as the live row does.
        if (cards.Count == 0)
            throw new InvalidOperationException("S2C 801 cannot be written with zero cash-shop cards.");
        return ShopCatalogBinding.WriteCashShopReply(Tables, Modern, token, cards);
    }

    /// <summary><c>gspRepShopBuy</c> (S2C 816), written through the tables.</summary>
    /// <param name="itemId">Stock or StockSet request key echoed at object +0.</param>
    /// <param name="result">1 when the purchase succeeds; any other value is failure.</param>
    /// <param name="name">Reply text; empty on success.</param>
    public static byte[] RepShopBuy(int itemId, int result, string name) =>
        ShopCatalogBinding.WriteModernReply(Tables, Modern, new(itemId, result != 0) { ReplyText = name });

    /// <summary>Legacy <c>gspRepShopBuy</c> (S2C 813): the two-word kr-812 reply, written through the tables.</summary>
    /// <param name="key">The C2S 808 key, echoed.</param>
    /// <param name="result">1 when the key is an on-sale catalog row.</param>
    public static byte[] RepLegacyShopBuy(int key, int result) =>
        PacketCodec.Write(Tables, "kr-812", "ShopPurchaseReply", new Dictionary<string, object?>
        {
            ["key"] = key,
            ["result"] = result,
        });

    /// <summary>Failure pin of <c>gspRepShopBuy</c> (S2C 816).</summary>
    /// <param name="itemId">The echoed request id.</param>
    public static byte[] RepShopBuyRefused(int itemId) => RepShopBuy(itemId, 0, string.Empty);

    /// <summary>Missing-row pin of <c>gspRepShopItemCardInfo</c> (S2C 817).</summary>
    public static byte[] RepShopItemCardInfoRefused() => RepShopItemCardInfo(-1, null);

    /// <summary><c>gspRepShopItemCardInfo</c> (S2C 817), written through the tables.</summary>
    /// <param name="result">1 when <paramref name="card"/> is present; otherwise -1.</param>
    /// <param name="card">The catalog row, or null for the empty card.</param>
    public static byte[] RepShopItemCardInfo(int result, ShopCardRow? card) =>
        ShopCatalogBinding.WriteCardInfoReply(Tables, Modern, new(result, card));

    /// <summary><c>gspRepRealCouponUse</c> (S2C 833), written through the tables.</summary>
    /// <param name="unknown0">Result / branch. 1 is success; -1 is a listed error.</param>
    public static byte[] RepRealCouponUse(int unknown0) =>
        PacketCodec.Write(Tables, Modern, "RealCouponUseReply", new Dictionary<string, object?>
        {
            ["result"] = unknown0,
            ["unknown_8"] = 0,
            ["unknown_12"] = 0,
        });

    /// <summary>Pin form of <c>gspRepRealCouponUse</c> (S2C 833).</summary>
    public static byte[] RepRealCouponUseEmpty() => RepRealCouponUse(0);

    /// <summary><c>gspRepCheckCash</c> (S2C 834), written through the tables.</summary>
    /// <param name="unknown0">First dword; meaning unproven. Written 0.</param>
    /// <param name="cash">Second dword: current cash.</param>
    /// <param name="unknown8">Third dword; consumed and unused. Written 0.</param>
    public static byte[] RepCheckCash(int unknown0, int cash, int unknown8) =>
        PacketCodec.Write(Tables, Modern, "CheckCashReply", new Dictionary<string, object?>
        {
            ["unknown_0"] = unknown0,
            ["cash"] = cash,
            ["unknown_8"] = unknown8,
        });

    /// <summary>Pin form of <c>gspRepCheckCash</c> (S2C 834).</summary>
    public static byte[] RepCheckCashEmpty() => RepCheckCash(0, 0, 0);

    /// <summary><c>gspUserMoneyStat</c> (S2C 106) with a stored wallet snapshot.</summary>
    /// <param name="money">The stored money snapshot.</param>
    public static byte[] RepUserMoneyStat(PlayerWallet money) => ShopCatalogBinding.MoneyStat(Tables, Modern, money);

    /// <summary>The catalog revision the reply carries in its token.</summary>
    /// <param name="body">The reply body written with revision 0.</param>
    /// <returns>The revision.</returns>
    public static long CatalogRevision(byte[] body) => ShopCatalogBinding.CatalogRevision(body);

    /// <summary>The legacy <c>gspRepEnterShop</c> form described by <paramref name="layout"/>.</summary>
    /// <param name="layout">The build's reply layout.</param>
    /// <param name="token">The request's cache-revision token.</param>
    /// <param name="cards">The bounded on-sale card list.</param>
    /// <param name="categories">The category tree.</param>
    /// <param name="mainCategory">The main category echoed by an <see cref="LegacyCatalogTrailer.IdsMain"/> trailer.</param>
    public static byte[] RepEnterShopLegacy(EnterShopReplyLayout layout, long token, IReadOnlyList<ShopCardRow> cards,
        IReadOnlyList<ShopCategoryRow> categories, int mainCategory = LegacyEnterShopMainCategory) =>
        ShopCatalogWire.RepEnterShopLegacy(layout, token, cards, categories, mainCategory);

    /// <summary>Writes one <c>Stock</c> with the u16 type width.</summary>
    /// <param name="w">The destination writer.</param>
    /// <param name="stock">A stock row.</param>
    public static void WriteStock(PacketWriter w, ShopStockRow stock) => ShopCatalogWire.WriteStock(w, stock);

    /// <summary>Writes one cn-279 <c>Stock</c> with the u32 type width.</summary>
    /// <param name="w">The destination writer.</param>
    /// <param name="stock">A stock row.</param>
    public static void WriteStock279(PacketWriter w, ShopStockRow stock) => ShopCatalogWire.WriteStock279(w, stock);

    /// <summary>Writes a counted card vector with the u16 stock width.</summary>
    /// <param name="w">The destination writer.</param>
    /// <param name="cards">Catalog rows.</param>
    /// <param name="unknown56">First per-card <c>i64</c>.</param>
    /// <param name="unknown64">Second per-card <c>i64</c>.</param>
    /// <param name="vectorOnlyStocks">Pass true for the id-400 card reader.</param>
    public static void WriteCards(
        PacketWriter w, IReadOnlyList<ShopCardRow> cards, long unknown56 = 0, long unknown64 = 0,
        bool vectorOnlyStocks = false)
    {
        w.WriteUInt32((uint)cards.Count);
        foreach (var card in cards)
            ShopCatalogWire.WriteItemCard(w, card, unknown56, unknown64, vectorOnlyStocks);
    }

    /// <summary>Writes a counted card vector with the cn-279 stock width.</summary>
    /// <param name="w">The destination writer.</param>
    /// <param name="cards">Catalog rows.</param>
    /// <param name="unknown56">First per-card <c>i64</c>.</param>
    /// <param name="unknown64">Second per-card <c>i64</c>.</param>
    public static void WriteCards279(
        PacketWriter w, IReadOnlyList<ShopCardRow> cards, long unknown56, long unknown64) =>
        ShopCatalogWire.WriteCards279(w, cards, unknown56, unknown64);

    /// <summary>Writes a populated <c>ShopItemCardInfo</c>.</summary>
    /// <param name="w">The destination writer.</param>
    /// <param name="card">A catalog row.</param>
    /// <param name="unknown56">First per-card <c>i64</c>; zero keeps the 1256 form.</param>
    /// <param name="unknown64">Second per-card <c>i64</c>; zero keeps the 1256 form.</param>
    /// <param name="vectorOnlyStocks">When set, omit the inline stock.</param>
    public static void WriteItemCard(PacketWriter w, ShopCardRow card, long unknown56 = 0, long unknown64 = 0,
        bool vectorOnlyStocks = false) =>
        ShopCatalogWire.WriteItemCard(w, card, unknown56, unknown64, vectorOnlyStocks);

    /// <summary>Writes a counted <c>ShopMainCategory</c> vector.</summary>
    /// <param name="w">The destination writer.</param>
    /// <param name="categories">Shop-visible categories.</param>
    /// <param name="pageType">When set, every subcategory's page mode.</param>
    public static void WriteCategories(PacketWriter w, IReadOnlyList<ShopCategoryRow> categories, int? pageType = null) =>
        ShopCatalogWire.WriteCategories(w, categories, pageType);

    /// <summary>Writes a default-constructed <c>ShopItemCardInfo</c> through the tables.</summary>
    /// <param name="w">The destination writer.</param>
    public static void WriteEmptyItemCard(PacketWriter w) =>
        PacketCodec.Write(w, Tables, Modern, "ShopItemCardInfo", ShopCatalogBinding.EmptyCardValues());

    /// <summary>Writes a default-constructed <c>Stock</c>.</summary>
    /// <param name="w">The destination writer.</param>
    public static void WriteEmptyStock(PacketWriter w) => ShopCatalogWire.WriteEmptyStock(w);

    /// <summary>Greedily selects cards that fit the wire budget and card cap.</summary>
    /// <param name="cards">All on-sale cards of the catalog.</param>
    /// <param name="budget">Inclusive encoded-size budget for the card vector alone.</param>
    /// <param name="wideStockType">Measure with the cn-279 28-byte stock layout.</param>
    /// <param name="vectorOnlyStocks">Measure without the inline stock.</param>
    /// <param name="maxCards">Maximum selected cards, or zero to use only the byte budget.</param>
    /// <returns>The selected prefix and the dropped count.</returns>
    public static (IReadOnlyList<ShopCardRow> Selected, int Dropped) SelectBoundedCards(
        IReadOnlyList<ShopCardRow> cards, int budget, bool wideStockType = false, bool vectorOnlyStocks = false,
        int maxCards = LegacyEnterShopCardCap) =>
        ShopCatalogWire.SelectBoundedCards(cards, budget, wideStockType, vectorOnlyStocks, maxCards);

    /// <summary>Orders shop-visible mains for the legacy enter-shop reply.</summary>
    /// <param name="categories">Workbook shop-visible categories.</param>
    public static IReadOnlyList<ShopCategoryRow> OrderEnterShopCategories(
        IReadOnlyList<ShopCategoryRow> categories) =>
        ShopCatalogWire.OrderEnterShopCategories(categories);

    /// <summary>Reattaches the main-tab default subcategories that card-linked trees omit.</summary>
    /// <param name="categories">Card-linked shop-visible mains.</param>
    /// <param name="shopVisibleSubs">Every SubCategory with <c>shopView != 0</c>.</param>
    /// <param name="mainDefaultSubcategoryIds">Resolved <c>shopDefaultSubCategory</c> ids keyed by main id.</param>
    public static IReadOnlyList<ShopCategoryRow> MergeMainClickDefaults(
        IReadOnlyList<ShopCategoryRow> categories,
        IReadOnlyList<ShopSubcategoryRow> shopVisibleSubs,
        IReadOnlyDictionary<int, int> mainDefaultSubcategoryIds) =>
        ShopCatalogWire.MergeMainClickDefaults(categories, shopVisibleSubs, mainDefaultSubcategoryIds);

    /// <summary>Ensures the client's open-tab main is present in the category tree.</summary>
    /// <param name="categories">Card-linked shop-visible mains.</param>
    /// <param name="shopVisibleMains">Every workbook main with <c>shopView != 0</c>.</param>
    /// <param name="landingMainId">The client's hardcoded open main.</param>
    public static IReadOnlyList<ShopCategoryRow> EnsureEnterShopLandingMain(
        IReadOnlyList<ShopCategoryRow> categories,
        IReadOnlyList<ShopCategoryRow> shopVisibleMains,
        int landingMainId = LegacyEnterShopMainCategory) =>
        ShopCatalogWire.EnsureEnterShopLandingMain(categories, shopVisibleMains, landingMainId);

    /// <summary>Picks the main category the legacy reply echoes.</summary>
    /// <param name="requested">The request's main category.</param>
    /// <param name="cards">The bounded card list that will be sent.</param>
    /// <param name="categories">The category tree that will be sent.</param>
    public static int ResolveEnterShopMainCategory(
        int requested, IReadOnlyList<ShopCardRow> cards, IReadOnlyList<ShopCategoryRow> categories) =>
        ShopCatalogWire.ResolveEnterShopMainCategory(requested, cards, categories);

    /// <summary>Builds the id-range tab tree of the modern catalog.</summary>
    /// <param name="catalog">The loaded catalog.</param>
    /// <param name="cards">The cards to place.</param>
    public static (IReadOnlyList<ShopCategoryRow> Categories, IReadOnlyList<ShopCardRow> Cards) BuildRangeTree(
        ShopCatalog catalog, IReadOnlyList<ShopCardRow> cards) =>
        ShopRangeTree.BuildRangeTree(catalog, cards);

    /// <summary>The selected category of the modern reply.</summary>
    /// <param name="requested">The category the client sent.</param>
    /// <param name="categories">The published mains.</param>
    public static int SelectEnterShopCategory(int requested, IReadOnlyList<ShopCategoryRow> categories) =>
        ShopRangeTree.SelectEnterShopCategory(requested, categories);

    /// <summary>Writes one container-2 record.</summary>
    /// <param name="w">The destination writer.</param>
    /// <param name="article">One held record.</param>
    public static void WriteArticleSlot(PacketWriter w, EquippedSlotRecord article) =>
        ShopCatalogWire.WriteArticleSlot(w, article);

    /// <summary>The plain S2C 803 equip-hold burst body.</summary>
    /// <param name="container2">Held container-2 records, at least one.</param>
    public static byte[] InventoryUpdateSlotsEquipHold(IReadOnlyList<EquippedSlotRecord> container2) =>
        ShopCatalogWire.InventoryUpdateSlotsEquipHold(container2);

    /// <summary>The compressed on-the-wire form of the equip-hold burst.</summary>
    /// <param name="container2">Held container-2 records, at least one.</param>
    public static byte[] InventoryUpdateSlotsEquipHoldEnvelope(IReadOnlyList<EquippedSlotRecord> container2) =>
        ShopCatalogWire.EquipHoldEnvelope(container2);

    private static PacketTables LoadTables()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "BubbleFighter.Fantasy.sln")))
                return PacketTables.Load(Path.Combine(dir.FullName, "packets"));
        throw new InvalidOperationException("Repository root not found above the test output.");
    }
}

/// <summary>
/// A purchase-reply sender for purchaser tests: writes the real modern body through the binding and hands the
/// frame to the recording session, so payload pins keep asserting the production bytes.
/// </summary>
/// <param name="Opcode">The reply opcode the fake dialect resolved.</param>
internal sealed class RecordingBuyReplySender(ushort Opcode) : IMessageSender<ShopPurchaseReplied>
{
    /// <inheritdoc />
    public string Protocol => "kr-1256/game";

    /// <inheritdoc />
    public ValueTask SendAsync(ISession session, ShopPurchaseReplied message, CancellationToken ct) =>
        session.SendAsync(Opcode, ShopCatalogBinding.WriteModernReply(ShopWire.Tables, "kr-1256", message), ct);
}

/// <summary>
/// A flush sender for equip-hold tests: writes the real compressed S2C 803 burst and hands the frame to the
/// recording session.
/// </summary>
/// <param name="Opcode">The slots update opcode the fake dialect resolved.</param>
internal sealed class RecordingFlushSender(ushort Opcode) : IMessageSender<EquippedArticlesFlushed>
{
    /// <inheritdoc />
    public string Protocol => "kr-1256/game";

    /// <inheritdoc />
    public ValueTask SendAsync(ISession session, EquippedArticlesFlushed message, CancellationToken ct) =>
        session.SendAsync(Opcode, ShopCatalogWire.EquipHoldEnvelope(message.Articles), ct);
}

/// <summary>
/// A balance sender for purchaser tests: writes the real six-word money snapshot and hands the frame to the
/// recording session.
/// </summary>
/// <param name="Opcode">The money opcode the fake dialect resolved.</param>
internal sealed class RecordingBalanceSender(ushort Opcode) : IMessageSender<ShopBalanceUpdated>
{
    /// <inheritdoc />
    public string Protocol => "kr-1256/game";

    /// <inheritdoc />
    public ValueTask SendAsync(ISession session, ShopBalanceUpdated message, CancellationToken ct) =>
        session.SendAsync(Opcode, ShopCatalogBinding.MoneyStat(ShopWire.Tables, ShopWire.Modern, message.Wallet), ct);
}
