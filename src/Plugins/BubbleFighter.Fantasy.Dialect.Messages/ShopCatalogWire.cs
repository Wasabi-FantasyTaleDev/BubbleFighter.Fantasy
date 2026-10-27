using System.Buffers;
using System.IO.Compression;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Shop;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Messages;

/// <summary>Which category tree a legacy <c>gspRepEnterShop</c> carries.</summary>
public enum LegacyCategoryTree
{
    /// <summary>The workbook's card-linked mains, written in <c>displayOrder</c> so the open main leads.</summary>
    Ordered,

    /// <summary>The card-linked mains plus the open main 1000 when it has no card.</summary>
    Landing,

    /// <summary>As <see cref="Landing"/>, plus each main's default subcategory rows.</summary>
    LandingDefaults,
}

/// <summary>What a legacy <c>gspRepEnterShop</c> carries after its category tree.</summary>
public enum LegacyCatalogTrailer
{
    /// <summary>Nothing.</summary>
    None,

    /// <summary>An empty counted <c>u32</c> vector.</summary>
    Ids,

    /// <summary>The empty counted vector, then the echoed main category.</summary>
    IdsMain,
}

/// <summary>
/// A legacy client's <c>gspRepEnterShop</c> layout as structural choices, read from the build's
/// <c>shop.catalog</c> values in <c>packets/builds.yaml</c> so that a new build states only how its reader differs.
/// </summary>
/// <param name="Token">Whether a has-data <c>bool32</c> and the request's cache token follow the result.</param>
/// <param name="WideStockType">Whether each stock's type field is a <c>u32</c> (28-byte stocks) instead of a <c>u16</c>.</param>
/// <param name="Tree">The category tree the reply carries.</param>
/// <param name="MainPageType">Whether every subcategory is written with page mode 2 (cards of the selected main) instead of its workbook mode.</param>
/// <param name="Trailer">What follows the category tree.</param>
public sealed record EnterShopReplyLayout(
    bool Token, bool WideStockType, LegacyCategoryTree Tree, bool MainPageType, LegacyCatalogTrailer Trailer);

/// <summary>
/// Reads a legacy build's <c>shop</c> values from <c>packets/builds.yaml</c> and writes its
/// <c>gspRepEnterShop</c> catalog reply. The per-build differences stay data: a new build states its
/// <c>shop.catalog</c> values and inherits the rest, and the writer below selects on nothing else.
/// </summary>
internal static class ShopCatalogWire
{
    /// <summary>
    /// Sale-window constants every kr-812 card carries: a 221-card catalog
    /// (35256-byte frame) writes the same
    /// far-apart pair on each card, an early bound and a far-future bound that the
    /// dialog treats as permanently on sale.
    ///
    /// </summary>
    public const double LegacySaleFrom = 39814.0;

    /// <summary>The kr-812 far-future sale bound; see <see cref="LegacySaleFrom"/>.</summary>
    public const double LegacySaleTo = 58441.0;

    /// <summary>
    /// Default deployment card-count limit for legacy <c>gspRepEnterShop</c> selection.
    /// A 221-card catalog fits a 35256-byte frame.
    /// It is configurable independently of the 16-bit frame limit; that
    /// card count does not establish a client limit.
    ///
    /// </summary>
    public const int LegacyEnterShopCardCap = 221;

    /// <summary>
    /// Main category the kr-812 / cn-717 dialog opens on for a normal shop click.
    /// C2S <c>gcpEnterShop</c> writers pass 1000 (kr-812 <c>sub_6C3A20</c>); the reply
    /// echoes it; handler kr-812 <c>sub_820CA0</c> (cn-717 <c>sub_8A7B50</c>) hands that
    /// trailing field to kr-812 <c>sub_96DB90</c> / cn-717 <c>sub_A16B90</c> except the special
    /// 500 and cash-check branches.
    ///
    /// </summary>
    public const int LegacyEnterShopMainCategory = 1000;

    /// <summary>
    /// Default subcategory under <see cref="LegacyEnterShopMainCategory"/>:
    /// workbook <c>MainCategory.shopDefaultSubCategory</c> <c>NewItems</c> (id 1002).
    /// kr-812 <c>sub_9720E0</c> reads that id from the client's main-category row and
    /// stores it at dialog +740 before the pageType fill.
    ///
    /// </summary>
    public const int LegacyEnterShopDefaultSubCategory = 1002;

    /// <summary>Reads the catalog layout a build's merged <c>builds.yaml</c> values state.</summary>
    /// <param name="build">The packet-table build, for messages.</param>
    /// <param name="values">The build's values laid over its bases'.</param>
    /// <returns>The layout.</returns>
    /// <exception cref="ConfigException">Thrown when a value is missing or unknown.</exception>
    public static EnterShopReplyLayout LayoutOf(string build, IReadOnlyDictionary<string, object?> values)
    {
        if (!values.TryGetValue("shop", out var raw) || raw is not IReadOnlyDictionary<string, object?> shop)
            throw new ConfigException($"Build '{build}' must define shop.catalog in packets/builds.yaml.");
        if (!shop.TryGetValue("catalog", out var rawCatalog) || rawCatalog is not IReadOnlyDictionary<string, object?> catalog)
            throw new ConfigException($"Build '{build}' must define the map shop.catalog in packets/builds.yaml.");
        return new EnterShopReplyLayout(
            Token: Text(catalog, "token", "shop.catalog.token") switch
            {
                "true" => true,
                "false" => false,
                var other => throw Unknown(build, "token", other),
            },
            WideStockType: Text(catalog, "stock_type", "shop.catalog.stock_type") switch
            {
                "u16" => false,
                "u32" => true,
                var other => throw Unknown(build, "stock_type", other),
            },
            Tree: Text(catalog, "tree", "shop.catalog.tree") switch
            {
                "ordered" => LegacyCategoryTree.Ordered,
                "landing" => LegacyCategoryTree.Landing,
                "landing_defaults" => LegacyCategoryTree.LandingDefaults,
                var other => throw Unknown(build, "tree", other),
            },
            MainPageType: Text(catalog, "page_type", "shop.catalog.page_type") switch
            {
                "sheet" => false,
                "main" => true,
                var other => throw Unknown(build, "page_type", other),
            },
            Trailer: Text(catalog, "trailer", "shop.catalog.trailer") switch
            {
                "none" => LegacyCatalogTrailer.None,
                "ids" => LegacyCatalogTrailer.Ids,
                "ids_main" => LegacyCatalogTrailer.IdsMain,
                var other => throw Unknown(build, "trailer", other),
            });

        string Text(IReadOnlyDictionary<string, object?> map, string key, string name) =>
            map.TryGetValue(key, out var value) && value is string text ? text
                : throw new ConfigException($"Build '{build}' must define {name} in packets/builds.yaml.");

        ConfigException Unknown(string buildId, string key, string value) =>
            new($"Build '{buildId}' has unknown shop.catalog.{key} '{value}'.");
    }

    /// <summary>Whether the build's merged values state a <c>shop</c> block at all.</summary>
    /// <param name="values">The build's values laid over its bases'.</param>
    public static bool HasShopValues(IReadOnlyDictionary<string, object?> values) =>
        values.TryGetValue("shop", out _);

    /// <summary>
    /// The legacy <c>gspRepEnterShop</c> form described by <paramref name="layout"/>: result 0, then (when
    /// <see cref="EnterShopReplyLayout.Token"/>) has-data true and the request's cache token, the card vector, an i64,
    /// the category tree and the trailer. Every card carries the <see cref="LegacySaleFrom"/> /
    /// <see cref="LegacySaleTo"/> pair and an inline stock before its counted stock vector (id-400 <c>sub_45FB30</c>
    /// reads it at this+18; omitting it makes the vector count read a stock field and crash). <paramref name="cards"/>
    /// must already be bounded with <see cref="SelectBoundedCards"/>: the legacy frame caps a payload at 65535 bytes.
    /// An <see cref="LegacyCategoryTree.Ordered"/> tree is written in <c>displayOrder</c> so main 1000 is index 0, as
    /// tab widgets are created in packet order (kr-812 <c>sub_96DB90</c>).
    ///
    /// </summary>
    /// <param name="layout">The build's reply layout.</param>
    /// <param name="token">The request's cache-revision token, echoed when the layout carries it.</param>
    /// <param name="cards">The bounded on-sale card list.</param>
    /// <param name="categories">The category tree, already given its landing main and default rows when the layout asks for them.</param>
    /// <param name="mainCategory">The main category echoed by an <see cref="LegacyCatalogTrailer.IdsMain"/> trailer.</param>
    /// <returns>The payload.</returns>
    public static byte[] RepEnterShopLegacy(EnterShopReplyLayout layout, long token, IReadOnlyList<ShopCardRow> cards,
        IReadOnlyList<ShopCategoryRow> categories, int mainCategory = LegacyEnterShopMainCategory)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var w = PacketWriter.Create();
        w.WriteInt32(0);
        if (layout.Token)
        {
            w.WriteBool32(true);
            w.WriteInt64(token);
        }
        var from = BitConverter.DoubleToInt64Bits(LegacySaleFrom);
        var to = BitConverter.DoubleToInt64Bits(LegacySaleTo);
        if (layout.WideStockType)
            WriteCards279(w, cards, from, to);
        else
            WriteCards(w, cards, from, to);
        w.WriteInt64(0);
        WriteCategories(w, layout.Tree == LegacyCategoryTree.Ordered ? OrderEnterShopCategories(categories) : categories,
            layout.MainPageType ? 2 : null);
        if (layout.Trailer != LegacyCatalogTrailer.None)
            w.WriteUInt32(0);
        if (layout.Trailer == LegacyCatalogTrailer.IdsMain)
            w.WriteInt32(mainCategory);
        return Written(w);
    }

    /// <summary>
    /// Writes one cn-279 <c>Stock</c> (reader cn-279 <c>0x44CC70</c>): the type field is a
    /// full <c>u32</c> where every other build reads a <c>u16</c>, making the stock
    /// 28 wire bytes. <c>WideStockType</c> selects it over
    /// <see cref="WriteStock"/>.
    ///
    /// </summary>
    /// <param name="w">The destination writer.</param>
    /// <param name="stock">A stock row.</param>
    public static void WriteStock279(PacketWriter w, ShopStockRow stock)
    {
        w.WriteInt32(stock.StockId);
        w.WriteInt32(stock.ItemId);
        w.WriteBool32(stock.IsOnSale);
        w.WriteInt32(stock.Type);
        w.WriteInt32(stock.Duration);
        w.WriteInt32(stock.ShowPrice);
        w.WriteInt32(stock.SellPrice);
    }

    /// <summary>
    /// Writes a counted <c>ShopItemCardInfo</c> vector with the cn-279 stock width.
    ///
    /// </summary>
    /// <param name="w">The destination writer.</param>
    /// <param name="cards">Catalog rows.</param>
    /// <param name="unknown56">First per-card <c>i64</c>.</param>
    /// <param name="unknown64">Second per-card <c>i64</c>.</param>
    public static void WriteCards279(
        PacketWriter w, IReadOnlyList<ShopCardRow> cards, long unknown56, long unknown64)
    {
        w.WriteUInt32((uint)cards.Count);
        foreach (var card in cards)
        {
            WriteCardHead(w, card, unknown56, unknown64);
            var terms = card.PurchaseTerms;
            if (terms.Count == 0)
            {
                WriteStock279(w, new ShopStockRow(0, 0, false, 0, 0, 0, 0, 0, 0));
                w.WriteUInt32(0);
                continue;
            }
            WriteStock279(w, terms[0]);
            w.WriteUInt32((uint)terms.Count);
            foreach (var stock in terms)
                WriteStock279(w, stock);
        }
    }

    /// <summary>
    /// Writes a counted <c>ShopItemCardInfo</c> vector (kr-1256 <c>sub_10D3780</c>) with the u16 stock width.
    ///
    /// </summary>
    /// <param name="w">The destination writer.</param>
    /// <param name="cards">Catalog rows.</param>
    /// <param name="unknown56">First per-card <c>i64</c>; the legacy catalog carries a sale-window bound pair here.</param>
    /// <param name="unknown64">Second per-card <c>i64</c>; see <paramref name="unknown56"/>.</param>
    public static void WriteCards(
        PacketWriter w, IReadOnlyList<ShopCardRow> cards, long unknown56, long unknown64)
    {
        w.WriteUInt32((uint)cards.Count);
        foreach (var card in cards)
        {
            WriteItemCard(w, card, unknown56, unknown64);
        }
    }

    /// <summary>Writes the card fields ahead of the inline stock, shared by the stock-width variants.</summary>
    private static void WriteCardHead(PacketWriter w, ShopCardRow card, long unknown56, long unknown64)
    {
        w.WriteInt32(card.CardId);
        w.WriteInt32(card.CardKind);
        w.WriteWideStringPrefixed(card.Name);
        w.WriteInt32(card.DisplayOrder);
        w.WriteInt32(card.MainCategoryId);
        w.WriteInt32(card.SubCategoryId);
        w.WriteBool32(card.IsOnSale);
        w.WriteInt64(unknown56);
        w.WriteInt64(unknown64);
    }

    /// <summary>
    /// Greedily selects cards that fit both the wire budget and
    /// <see cref="LegacyEnterShopCardCap"/>. Order is deterministic: landing main
    /// <see cref="LegacyEnterShopMainCategory"/>, its default sub
    /// <see cref="LegacyEnterShopDefaultSubCategory"/>, then the rest of that main,
    /// then main 500, then remaining mains ascending, and within a group by
    /// subcategory, display order, then card id. kr-812 <c>sub_9720E0</c> appends matching
    /// cards to the grid in vector order, so the open tab must lead the vector.
    /// Selection stops at the first card that does not fit (no skipping). Returns
    /// the selected prefix and how many cards did not fit.
    /// <paramref name="wideStockType"/> selects the cn-279 stock width (28 wire bytes)
    /// for the size measurement.
    ///
    /// </summary>
    /// <param name="cards">All on-sale cards of the catalog.</param>
    /// <param name="budget">Inclusive encoded-size budget for the card vector alone.</param>
    /// <param name="wideStockType">Measure with the cn-279 28-byte stock layout.</param>
    /// <param name="vectorOnlyStocks">
    /// Measure the card with no inline stock. id-400 production still writes the inline
    /// stock (id-400 <c>sub_45FB30</c> at this+18); this flag is only for size experiments.
    /// </param>
    /// <param name="maxCards">Maximum selected cards, or zero to use only the byte budget.</param>
    public static (IReadOnlyList<ShopCardRow> Selected, int Dropped) SelectBoundedCards(
        IReadOnlyList<ShopCardRow> cards, int budget, bool wideStockType = false, bool vectorOnlyStocks = false,
        int maxCards = LegacyEnterShopCardCap)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(budget);
        ArgumentOutOfRangeException.ThrowIfNegative(maxCards);
        // Open tab is main 1000 / sub 1002 (kr-812 sub_820CA0 -> kr-812 sub_96DB90 / kr-812 sub_9720E0), not
        // the first workbook main. Ascending main alone sorted 1000 last and truncated it.
        var ordered = cards
            .OrderByDescending(c => c.SupplementalPriority)
            .ThenBy(c => EnterShopMainEmitOrder(c.MainCategoryId))
            .ThenBy(c => c.MainCategoryId)
            .ThenBy(c => EnterShopSubEmitOrder(c.MainCategoryId, c.SubCategoryId))
            .ThenBy(c => c.SubCategoryId)
            .ThenBy(c => c.DisplayOrder)
            .ThenBy(c => c.CardId)
            .ToArray();
        var selected = new List<ShopCardRow>(Math.Min(ordered.Length, maxCards == 0 ? ordered.Length : maxCards));
        var used = 0;
        if (maxCards == 0)
        {
            // Reserve one card per category before filling the remaining frame budget.
            // Otherwise a large clothing category can remove every character card.
            var chosen = new HashSet<ShopCardRow>();
            foreach (var card in ordered.Where(c => c.SupplementalPriority)
                .Concat(ordered.GroupBy(c => (c.MainCategoryId, c.SubCategoryId)).Select(group => group.First()))
                .Concat(ordered))
            {
                if (chosen.Contains(card)) continue;
                var size = ItemCardBytes(card, wideStockType, vectorOnlyStocks);
                if (size > budget - used) continue;
                chosen.Add(card);
                used += size;
            }
            selected.AddRange(ordered.Where(chosen.Contains));
            return (selected, ordered.Length - selected.Count);
        }
        foreach (var card in ordered)
        {
            if (maxCards != 0 && selected.Count >= maxCards)
                break;
            var size = ItemCardBytes(card, wideStockType, vectorOnlyStocks);
            if (size > budget - used)
                break;
            selected.Add(card);
            used += size;
        }
        return (selected, ordered.Length - selected.Count);
    }

    /// <summary>
    /// Orders shop-visible mains for the kr-812 / cn-717 enter-shop reply: ascending
    /// <c>displayOrder</c> then id so main 1000 (displayOrder 1000) is index 0. Tab
    /// buttons are created in packet order inside kr-812 <c>sub_96DB90</c>.
    ///
    /// </summary>
    /// <param name="categories">Workbook shop-visible categories.</param>
    public static IReadOnlyList<ShopCategoryRow> OrderEnterShopCategories(
        IReadOnlyList<ShopCategoryRow> categories)
    {
        ArgumentNullException.ThrowIfNull(categories);
        return categories
            .OrderBy(c => c.DisplayOrder)
            .ThenBy(c => c.Id)
            .ToArray();
    }

    /// <summary>
    /// Emit priority for a main category id: 0 = landing 1000, 1 = main 500, 2 = others.
    ///
    /// </summary>
    /// <param name="mainCategoryId">Workbook main category id on the card.</param>
    private static int EnterShopMainEmitOrder(int mainCategoryId) => mainCategoryId switch
    {
        LegacyEnterShopMainCategory => 0,
        500 => 1,
        _ => 2,
    };

    /// <summary>
    /// Within the landing main, emit the default sub (NewItems / 1002) before others
    /// so kr-812 <c>sub_9720E0</c>'s vector walk paints that page first.
    ///
    /// </summary>
    /// <param name="mainCategoryId">Card main category id.</param>
    /// <param name="subCategoryId">Card subcategory id.</param>
    private static int EnterShopSubEmitOrder(int mainCategoryId, int subCategoryId) =>
        mainCategoryId == LegacyEnterShopMainCategory
            && subCategoryId == LegacyEnterShopDefaultSubCategory
            ? 0
            : 1;

    /// <summary>Encoded wire size of one card, matching the writer selected by the flags.</summary>
    /// <param name="card">The catalog row being measured.</param>
    /// <param name="wideStockType">Measure the cn-279 28-byte stock.</param>
    /// <param name="vectorOnlyStocks">
    /// Measure without the inline stock. Production id-400 keeps that stock.
    /// </param>
    private static int ItemCardBytes(ShopCardRow card, bool wideStockType = false, bool vectorOnlyStocks = false)
    {
        var w = PacketWriter.Create();
        if (wideStockType)
            WriteCard279(w, card);
        else
            WriteItemCard(w, card, vectorOnlyStocks: vectorOnlyStocks);
        var length = ((ArrayBufferWriter<byte>)w.Buffer).WrittenSpan.Length;
        return length;
    }

    /// <summary>Writes one full card in the cn-279 stock width.</summary>
    private static void WriteCard279(PacketWriter w, ShopCardRow card)
    {
        WriteCardHead(w, card,
            BitConverter.DoubleToInt64Bits(LegacySaleFrom), BitConverter.DoubleToInt64Bits(LegacySaleTo));
        var terms = card.PurchaseTerms;
        if (terms.Count == 0)
        {
            WriteStock279(w, new ShopStockRow(0, 0, false, 0, 0, 0, 0, 0, 0));
            w.WriteUInt32(0);
            return;
        }
        WriteStock279(w, terms[0]);
        w.WriteUInt32((uint)terms.Count);
        foreach (var stock in terms)
            WriteStock279(w, stock);
    }

    /// <summary>
    /// Writes one <c>Stock</c> from the <c>Stock</c> sheet. Reader kr-1256 <c>sub_113EF70</c>.
    /// The <c>u16</c> is the sheet <c>type</c> column verbatim; only the
    /// default-constructed card pins that slot to 1 (kr-1256 <c>sub_11400D0</c>).
    ///
    /// </summary>
    /// <param name="w">The destination writer.</param>
    /// <param name="stock">A stock row.</param>
    public static void WriteStock(PacketWriter w, ShopStockRow stock)
    {
        w.WriteInt32(stock.StockId);
        w.WriteInt32(stock.ItemId);
        w.WriteBool32(stock.IsOnSale);
        w.WriteUInt16(stock.Type);
        w.WriteInt32(stock.Duration);
        w.WriteInt32(stock.ShowPrice);
        w.WriteInt32(stock.SellPrice);
    }

    /// <summary>
    /// Writes a populated <c>ShopItemCardInfo</c>. kr-1256 <c>sub_5F52D0</c> looks up
    /// object +4 as a ShopItem or StockSet key; +8 selects its resolver.
    /// The name is followed by <c>i32 displayOrder</c> (+36), main category (+40),
    /// subcategory (+44), and <c>bool32</c> visibility (+48). Two unknown
    /// <c>i64</c> fields (+56/+64) remain zero. One inline Stock and a counted
    /// purchase-choice vector use kr-1256 <c>0x113EF70</c>; a set's gifts are separate.
    ///
    /// </summary>
    /// <param name="w">The destination writer.</param>
    /// <param name="card">A catalog row.</param>
    /// <param name="unknown56">First per-card <c>i64</c>; zero keeps the 1256 form.</param>
    /// <param name="unknown64">Second per-card <c>i64</c>; zero keeps the 1256 form.</param>
    /// <param name="vectorOnlyStocks">
    /// When set, omit the inline stock. id-400 <c>sub_45FB30</c> reads the purchase
    /// terms only as the counted vector id-400 <c>sub_460B20</c>.
    /// </param>
    public static void WriteItemCard(PacketWriter w, ShopCardRow card, long unknown56 = 0, long unknown64 = 0,
        bool vectorOnlyStocks = false)
    {
        // kr-1256 0x6463AE selects the StockSet map at registry+0x11C only when kind is 1.
        w.WriteInt32(card.CardId);
        w.WriteInt32(card.CardKind);
        w.WriteWideStringPrefixed(card.Name);
        // kr-1256 0x651A80 modes 1/2 sort +36; price sorting separately reads +96.
        w.WriteInt32(card.DisplayOrder);
        // kr-1256 sub_64E210 compares +40/+44 with the selected category; prices cannot match those keys.
        w.WriteInt32(card.MainCategoryId);
        w.WriteInt32(card.SubCategoryId);
        w.WriteBool32(card.IsOnSale);
        w.WriteInt64(unknown56);
        w.WriteInt64(unknown64);
        var terms = card.PurchaseTerms;
        if (!vectorOnlyStocks)
        {
            if (terms.Count == 0)
            {
                WriteEmptyStock(w);
                w.WriteUInt32(0);
                return;
            }

            WriteStock(w, terms[0]);
        }

        // kr-1256 0xE157D0 selects purchase terms from the vector, including the displayed stock.
        // id-400 has no inline stock; id-400 sub_460B20 reads this count as the whole list.
        w.WriteUInt32((uint)terms.Count);
        foreach (var stock in terms)
            WriteStock(w, stock);
    }

    /// <summary>
    /// Writes a default-constructed <c>Stock</c>. Reader kr-1256 <c>sub_113EF70</c>: two
    /// <c>i32</c>, <c>i32</c>-as-bool, <c>u16</c>, three <c>i32</c>. Ctor default for the
    /// <c>u16</c> is 1.
    ///
    /// </summary>
    /// <param name="w">The destination writer.</param>
    public static void WriteEmptyStock(PacketWriter w)
    {
        w.WriteInt32(0);
        w.WriteInt32(0);
        w.WriteBool32(false);
        w.WriteUInt16(1);
        w.WriteInt32(0);
        w.WriteInt32(0);
        w.WriteInt32(0);
    }

    /// <summary>
    /// Writes a counted <c>ShopMainCategory</c> vector (kr-1256 <c>sub_10D3AA0</c> /
    /// kr-1256 <c>sub_1141B00</c> / kr-1256 <c>sub_1141910</c>).
    ///
    /// </summary>
    /// <param name="w">The destination writer.</param>
    /// <param name="categories">Shop-visible categories.</param>
    /// <param name="pageType">
    /// When set, every subcategory is written with this page mode instead of the workbook
    /// value. 2 selects the client's "cards of the selected main category" filter.
    /// </param>
    public static void WriteCategories(PacketWriter w, IReadOnlyList<ShopCategoryRow> categories, int? pageType = null)
    {
        w.WriteUInt32((uint)categories.Count);
        foreach (var category in categories)
        {
            // A category is read by kr-1256 sub_1141B00 as an i32 id at +4, a
            // std::wstring at +8, an i32 at +32, and a nested vector at +36. The
            // nested vector's elements are ShopSubCategory records, read by
            // kr-1256 sub_1141910 as an i32 id, a std::wstring, and two i32 - 40 bytes in
            // memory per kr-1256 sub_1141C00 and kr-1256 sub_1141EE0. That is
            // the shape written here.
            w.WriteInt32(category.Id);
            w.WriteWideStringPrefixed(category.Name);
            w.WriteInt32(category.DisplayOrder);
            w.WriteUInt32((uint)category.Subcategories.Count);
            foreach (var sub in category.Subcategories)
            {
                w.WriteInt32(sub.Id);
                w.WriteWideStringPrefixed(sub.Name);
                w.WriteInt32(sub.DisplayOrder);
                // kr-1256 sub_1141910 stores this at +36; kr-1256 sub_64E210 selects the page algorithm from it.
                // cn-279 sub_66AB50 and id-400 sub_8E4C00: mode 1 ignores the card vector.
                w.WriteInt32(pageType ?? sub.PageType);
            }
        }
    }

    /// <summary>
    /// Picks the main category the kr-812 / cn-717 <c>gspRepEnterShop</c> reply echoes. The
    /// dialog fills its grid on open only from cards whose main equals the echoed one
    /// (kr-812 <c>sub_9720E0</c>); the workbook has no card under
    /// the landing main 1000, so echoing it opens an empty grid until a tab is clicked.
    /// The requested main is kept when a sent card carries it, otherwise the first main of the
    /// tree, in the order the tree is sent, that has a sent card is used.
    ///
    /// </summary>
    /// <param name="requested">The request's main category.</param>
    /// <param name="cards">The bounded card list that will be sent.</param>
    /// <param name="categories">The category tree that will be sent.</param>
    /// <returns>The main to echo; the request's own when no tree main has a card.</returns>
    public static int ResolveEnterShopMainCategory(
        int requested, IReadOnlyList<ShopCardRow> cards, IReadOnlyList<ShopCategoryRow> categories)
    {
        ArgumentNullException.ThrowIfNull(cards);
        ArgumentNullException.ThrowIfNull(categories);
        var withCards = new HashSet<int>(cards.Select(card => card.MainCategoryId));
        if (withCards.Contains(requested))
            return requested;
        foreach (var main in OrderEnterShopCategories(categories))
        {
            if (withCards.Contains(main.Id))
                return main.Id;
        }
        return requested;
    }

    /// <summary>
    /// Reattaches the main-tab default subcategories that card-linked trees omit.
    /// id-400 <c>sub_8E4C00</c> stores the selected main, then id-400 <c>sub_8E4BB0</c> looks
    /// up the subcategory whose id equals the client's
    /// <c>shopDefaultSubCategory</c> (or, when that cell is empty, the main id itself
    /// - <c>All_Clothes</c> 100 under Clothes 100). Missing that row leaves pageType
    /// at dialog init (0) and mode 0 compares card+48 to the default sub id, so every
    /// filter misses. kr-812 / cn-717 keep their card-linked trees unchanged.
    ///
    /// </summary>
    /// <param name="categories">Card-linked shop-visible mains.</param>
    /// <param name="shopVisibleSubs">Every SubCategory with <c>shopView != 0</c>.</param>
    /// <param name="mainDefaultSubcategoryIds">
    /// Optional resolved <c>shopDefaultSubCategory</c> ids keyed by main id.
    /// </param>
    public static IReadOnlyList<ShopCategoryRow> MergeMainClickDefaults(
        IReadOnlyList<ShopCategoryRow> categories,
        IReadOnlyList<ShopSubcategoryRow> shopVisibleSubs,
        IReadOnlyDictionary<int, int> mainDefaultSubcategoryIds)
    {
        ArgumentNullException.ThrowIfNull(categories);
        ArgumentNullException.ThrowIfNull(shopVisibleSubs);
        ArgumentNullException.ThrowIfNull(mainDefaultSubcategoryIds);
        if (shopVisibleSubs.Count == 0 && mainDefaultSubcategoryIds.Count == 0)
            return categories;

        var byId = new Dictionary<int, ShopSubcategoryRow>(shopVisibleSubs.Count);
        foreach (var sub in shopVisibleSubs)
            byId.TryAdd(sub.Id, sub);

        var merged = new List<ShopCategoryRow>(categories.Count);
        foreach (var category in categories)
        {
            var subs = new Dictionary<int, ShopSubcategoryRow>();
            foreach (var sub in category.Subcategories)
                subs[sub.Id] = sub;
            // Empty shopDefaultSubCategory: client falls back to the main id (All_Clothes).
            if (byId.TryGetValue(category.Id, out var allOfMain))
                subs[allOfMain.Id] = allOfMain;
            if (mainDefaultSubcategoryIds.TryGetValue(category.Id, out var defaultId)
                && byId.TryGetValue(defaultId, out var namedDefault))
            {
                subs[namedDefault.Id] = namedDefault;
            }

            merged.Add(category with
            {
                Subcategories = subs.Values
                    .OrderBy(s => s.DisplayOrder)
                    .ThenBy(s => s.Id)
                    .ToList(),
            });
        }

        return merged;
    }

    /// <summary>
    /// Ensures the client's open-tab main is present in the enter-shop category tree.
    /// id-400 <c>sub_7A9A90</c>, cn-279 <c>sub_66B390</c>, and cn-324 <c>sub_7087A0</c>
    /// all select main 1000 (cn-324 may pick 500 when a dialog flag is set). The
    /// select helpers write that id to dialog+324 / +736 before the packet-map
    /// lookup, so a missing Main row still leaves the current id at 1000. The
    /// tab-click handlers - id-400 <c>sub_8E1F40</c>, cn-279 <c>sub_66B830</c>,
    /// cn-324 <c>sub_7F82B0</c> - then require the current id to appear in the
    /// parallel tab id list before they will call the filter again, so every later
    /// click is a no-op when Main was omitted because it has no on-sale cards.
    ///
    /// </summary>
    /// <param name="categories">Card-linked shop-visible mains.</param>
    /// <param name="shopVisibleMains">
    /// Every workbook main with <c>shopView != 0</c> (empty-sub stubs).
    /// </param>
    /// <param name="landingMainId">
    /// The client's hardcoded open main (<see cref="LegacyEnterShopMainCategory"/>).
    /// </param>
    public static IReadOnlyList<ShopCategoryRow> EnsureEnterShopLandingMain(
        IReadOnlyList<ShopCategoryRow> categories,
        IReadOnlyList<ShopCategoryRow> shopVisibleMains,
        int landingMainId = LegacyEnterShopMainCategory)
    {
        ArgumentNullException.ThrowIfNull(categories);
        ArgumentNullException.ThrowIfNull(shopVisibleMains);
        if (categories.Any(c => c.Id == landingMainId))
            return categories;

        var stub = shopVisibleMains.FirstOrDefault(c => c.Id == landingMainId)
            ?? throw new InvalidOperationException(
                $"Shop workbook has no shop-visible MainCategory id {landingMainId}; "
                + "id-400/cn-279/cn-324 open on that main and the tab-click path requires it in the packet.");
        var withLanding = new List<ShopCategoryRow>(categories.Count + 1) { stub };
        withLanding.AddRange(categories);
        return withLanding;
    }

    /// <summary>
    /// <c>gspInventoryUpdateInventorySlots</c> (S2C 803) equip-hold burst, plain
    /// (uncompressed) body: container 1 count 0, container 2 = the held records,
    /// container 3 count 0. Container 1 stays empty because the burst changes no
    /// bag slot - the grant already travelled in the purchase push - and container
    /// 3 is a removal list whose ids erase bag nodes
    /// (kr-1256 <c>sub_933190</c> -> kr-1256 <c>sub_804E60</c>), so it must never carry a value.
    /// The client reads container 2 only when a my-fighter object exists
    /// (kr-1256 <c>sub_9AEB10</c>), which is why the row holds the records until the
    /// client's own C2S 803 proves the inventory dialog is open.
    ///
    /// </summary>
    /// <param name="container2">Held container-2 records, at least one.</param>
    /// <returns>The plain body; wrap it with <see cref="EquipHoldEnvelope"/> before sending.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="container2"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="container2"/> is empty.</exception>
    public static byte[] InventoryUpdateSlotsEquipHold(IReadOnlyList<EquippedSlotRecord> container2)
    {
        ArgumentNullException.ThrowIfNull(container2);
        if (container2.Count == 0)
            throw new ArgumentException("An S2C 803 equip-hold burst needs at least one container-2 record.", nameof(container2));
        var w = PacketWriter.Create();
        w.WriteUInt32(0);                       // container 1: no bag-slot change
        w.WriteUInt32((uint)container2.Count);  // container 2: the held equip records
        foreach (var record in container2)
            WriteArticleSlot(w, record);
        w.WriteUInt32(0);                       // container 3: removal list, must stay empty
        return Written(w);
    }

    /// <summary>
    /// The on-the-wire form of <see cref="InventoryUpdateSlotsEquipHold"/>: the
    /// compressed envelope the S2C 803 stream binder reads. The client binds the
    /// stream with kr-1256 <c>sub_FA5520</c> before any container reader runs
    /// (kr-1256 <c>sub_9AEB10</c>), so a plain body would be misread as the length
    /// prefix. The <c>u32</c> header carries the plain length, which is correct
    /// under both the plain-length and the compressed-length reading.
    ///
    /// </summary>
    /// <param name="container2">Held container-2 records, at least one.</param>
    public static byte[] EquipHoldEnvelope(IReadOnlyList<EquippedSlotRecord> container2) =>
        PlainLengthEnvelope(InventoryUpdateSlotsEquipHold(container2));

    /// <summary>
    /// Writes one kr-1256 <c>sub_10D8370</c> container-2 record: <c>i64 unknown10</c>
    /// (identity), <c>i32 unknown11</c> (item id), <c>i32 unknown12</c>
    /// (1 = equip / upsert), <c>i32</c>-as-bool <c>unknown13</c>, then the nested
    /// vector as count 0, which is a legal empty vector
    /// (kr-1256 <c>sub_10D91D0</c>).
    ///
    /// </summary>
    /// <param name="w">The destination writer.</param>
    /// <param name="article">One held record.</param>
    public static void WriteArticleSlot(PacketWriter w, EquippedSlotRecord article)
    {
        w.WriteInt64(article.Unknown10);
        w.WriteInt32(article.Unknown11);
        w.WriteInt32(article.Unknown12);
        w.WriteBool32(article.Unknown13);
        w.WriteUInt32(0);
    }

    /// <summary>
    /// Wraps a plain body in the compressed envelope with the plain length in its
    /// <c>u32</c> header: one zlib stream, the same primitive the S2C 1500 config
    /// blobs use. Client side the header value only bounds the copy into the zlib
    /// stream, so writing the plain length stays correct under any strict
    /// output-size interpretation. The bag publications use the compressed length
    /// instead (<see cref="ZlibEnvelope"/>); this form is the equip-hold burst's own.
    ///
    /// </summary>
    /// <param name="plain">The uncompressed body.</param>
    private static byte[] PlainLengthEnvelope(byte[] plain)
    {
        using var target = new MemoryStream();
        using (var zlib = new ZLibStream(target, CompressionLevel.Fastest))
            zlib.Write(plain);
        var w = PacketWriter.Create();
        w.WriteUInt32(checked((uint)plain.Length));
        w.WriteBytes(target.ToArray());
        return Written(w);
    }

    internal static byte[] Written(PacketWriter w) => ((ArrayBufferWriter<byte>)w.Buffer).WrittenSpan.ToArray();
}
