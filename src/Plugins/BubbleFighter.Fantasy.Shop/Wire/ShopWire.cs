using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Wallet;
using System.Buffers;
using System.IO.Compression;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Shop.Data;

namespace BubbleFighter.Fantasy.Shop.Wire;

/// <summary>
/// Server-to-client shop payloads derived from the Korean 1256 readers. Catalog rows
/// come from <c>Config/ShopItem2.xml</c> via <see cref="Data.ShopCatalog"/>. Unproven
/// scalars stay numeric zeros and are still named <c>unknownN</c> in docs.
///
/// </summary>
public static class ShopWire
{
    /// <summary>
    /// Byte length of a default-constructed <c>ShopItemCardInfo</c> on the wire
    /// (kr-1256 <c>sub_11400D0</c> defaults through kr-1256 <c>sub_1140210</c> / kr-1256 <c>sub_1140350</c>).
    ///
    /// </summary>
    public const int EmptyItemCardBytes = 74;

    /// <summary>
    /// Wire size of one S2C 803 container-2 kr-1256 <c>sub_10D8370</c> record with an empty
    /// nested vector: <c>i64</c> + <c>i32</c> + <c>i32</c> + <c>i32</c>-as-bool plus the
    /// nested vector count (kr-1256 <c>sub_10D8370</c>).
    ///
    /// </summary>
    public const int ArticleSlotBytes = 24;

    /// <summary>
    /// Byte length of the plain S2C 803 body carrying exactly one container-2
    /// record: the three container counts plus <see cref="ArticleSlotBytes"/>.
    ///
    /// </summary>
    public const int EquipHoldSingleRecordBytes = (3 * 4) + ArticleSlotBytes;

    /// <summary>Byte length of <see cref="RepEnterShopEmpty"/> when the token is any <c>i64</c>.</summary>
    public const int RepEnterShopEmptyBytes = 40;

    /// <summary>Byte length of <see cref="RepEnterTCShopEmpty"/> when the token is any <c>i64</c>.</summary>
    public const int RepEnterTCShopEmptyBytes = 20;

    /// <summary>Byte length of <see cref="RepShopBuyRefused"/>.</summary>
    public const int RepShopBuyRefusedBytes = 25;

    /// <summary>Byte length of <see cref="RepShopItemCardInfoRefused"/>.</summary>
    public const int RepShopItemCardInfoRefusedBytes = 4 + EmptyItemCardBytes;

    /// <summary>Byte length of <see cref="RepRealCouponUseEmpty"/>.</summary>
    public const int RepRealCouponUseEmptyBytes = 12;

    /// <summary>Byte length of <see cref="RepCheckCashEmpty"/>.</summary>
    public const int RepCheckCashEmptyBytes = 12;

    /// <summary>
    /// Empty-catalog pin of <c>gspRepEnterShop</c> (S2C 800). Same layout as
    /// <see cref="RepEnterShop"/> via parser kr-1256 <c>sub_99B9A0</c>: result, has-data,
    /// token, empty cards, <c>unknown32</c>, empty categories, empty counted
    /// <c>i32</c> vector, trailing <c>mainCategory</c> 0. Pin-only; never sent.
    ///
    /// </summary>
    /// <param name="token">Token echoed when has-data is set.</param>
    public static byte[] RepEnterShopEmpty(long token)
    {
        var w = PacketWriter.Create();
        w.WriteInt32(1);
        w.WriteBool32(true);
        w.WriteInt64(token);
        // Empty card vector, the i64 between the vectors, an empty category
        // vector, the counted vector that precedes the trailing i32, and that
        // trailing i32 - see RepEnterShop for why all four are required.
        w.WriteUInt32(0);
        w.WriteInt64(0);
        w.WriteUInt32(0);
        w.WriteUInt32(0);
        w.WriteInt32(0);
        return Written(w);
    }

    /// <summary>
    /// Empty-catalog pin of <c>gspRepEnterTCShop</c> (S2C 801). Same layout as
    /// <see cref="RepEnterTCShop"/> via handler kr-1256 <c>sub_9AE0D0</c>: <c>unknown0</c>,
    /// has-data, token, empty card vector. Pin-only; never sent.
    ///
    /// </summary>
    /// <param name="token">Token echoed when has-data is set.</param>
    public static byte[] RepEnterTCShopEmpty(long token)
    {
        var w = PacketWriter.Create();
        w.WriteInt32(0);
        w.WriteBool32(true);
        w.WriteInt64(token);
        w.WriteUInt32(0);
        return Written(w);
    }

    /// <summary>
    /// <c>gspRepEnterShop</c> (S2C 800) with the lucci / item-shop catalog.
    /// Parser kr-1256 <c>sub_99B9A0</c>: result, has-data, token, cards, <c>unknown32</c>,
    /// categories, the extra counted <c>i32</c> vector, then the trailing
    /// <c>mainCategory</c>.
    ///
    /// </summary>
    /// <param name="token">Token echoed to <c>dword_18E56E8+37</c> when has-data is set.</param>
    /// <param name="cards">On-sale non-cash stock sets.</param>
    /// <param name="categories">Shop-visible categories.</param>
    /// <param name="mainCategory">
    /// The main category the dialog opens on. The parser reads it at object +68
    /// (kr-1256 <c>sub_99B9A0</c>) straight after the counted vector, and the reply
    /// handler hands it to the fill as the selected category, whose own literals
    /// for this dialog are 500 (kr-1256 <c>sub_9ADA00</c>, <c>:112</c>). It must name a
    /// category the reply itself carried: kr-1256 <c>sub_64DAB0</c> returns at its first
    /// test when the key matches no spec row, which is a tab bar with nothing
    /// under it and no crash.
    /// </param>
    public static byte[] RepEnterShop(
        long token, IReadOnlyList<ShopCardRow> cards, IReadOnlyList<ShopCategoryRow> categories, int mainCategory)
    {
        var w = PacketWriter.Create();
        w.WriteInt32(1);
        w.WriteBool32(true);
        w.WriteInt64(token);
        WriteCards(w, cards);
        // An i64 sits between the card vector and the category vector: kr-1256 sub_99B9A0 builds the card vector at
        // a1+16,:51 reads eight more bytes into a1+32, and:53 builds the category vector. Removing it shifts
        // the categories eight bytes early, the count reads as garbage, and the client's element walk runs off
        // the buffer - which is the crash.
        w.WriteInt64(0);
        WriteCategories(w, categories);
        // The parser then builds a counted vector<i32> at a1+56 through
        // kr-1256 sub_998FB0 -> kr-1256 sub_999C00 (kr-1256 sub_99B9A0), which consumes a u32 element
        // count and that many i32 ids, and only then reads the i32 at a1+68
        // (kr-1256 sub_99B9A0). Omitting the count made the value below be read as the
        // count, left a1+68 past the body so it kept zero, and the fill returned
        // without drawing a single card.
        w.WriteUInt32(0);
        w.WriteInt32(mainCategory);
        return Written(w);
    }

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

    /// <summary>
    /// The legacy <c>gspRepEnterShop</c> form described by <paramref name="layout"/>: result 0, then (when
    /// <see cref="LegacyCatalogLayout.Token"/>) has-data true and the request's cache token, the card vector, an i64,
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
    public static byte[] RepEnterShopLegacy(LegacyCatalogLayout layout, long token, IReadOnlyList<ShopCardRow> cards,
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
    /// Builds the tab tree by SubCategory id range: a page belongs to the main whose id is its own id rounded down to
    /// the hundred (kr-1256 ShopItem2: 1xx under Clothes 100, 4xx under Characters 400, 10xx under Main 1000). The
    /// workbook has no parent column and its page names repeat (801 and 1003 are both "Packages"), so a tree linked
    /// by card names puts pages under the wrong tab. A main is published only with at least one card on one of its
    /// pages; a page is kept when the sheet shows it or a card uses it; cards on unpublished pages are dropped.
    /// kr-1256 sub_646600 builds one tab per published main and kr-1256 sub_64DAB0 walks that main's page list.
    /// </summary>
    /// <param name="catalog">The loaded catalog.</param>
    /// <param name="cards">The cards to place.</param>
    /// <returns>The published mains and the cards on their pages.</returns>
    public static (IReadOnlyList<ShopCategoryRow> Categories, IReadOnlyList<ShopCardRow> Cards) BuildRangeTree(
        ShopCatalog catalog, IReadOnlyList<ShopCardRow> cards)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(cards);
        var mains = new List<ShopCategoryRow>();
        var placed = new List<ShopCardRow>();
        foreach (var main in catalog.ShopVisibleMains)
        {
            var mainCards = cards.Where(card => card.MainCategoryId == main.Id).ToList();
            var pages = catalog.AllSubcategories
                .Where(sub => sub.Id / 100 * 100 == main.Id
                    && (sub.ShopView != 0 || mainCards.Any(card => card.SubCategoryId == sub.Id)))
                .ToList();
            var pageIds = pages.Select(sub => sub.Id).ToHashSet();
            var onPages = mainCards.Where(card => pageIds.Contains(card.SubCategoryId)).ToList();
            if (onPages.Count == 0)
                continue;
            mains.Add(main with { Subcategories = pages });
            placed.AddRange(onPages);
        }
        return (mains, placed);
    }

    /// <summary>
    /// The selected category of the reply: the one the client asked for in C2S 801 when the published tree carries it
    /// as a main or a page, otherwise the first main by display order. The fill selects it through kr-1256 sub_64DAB0,
    /// which returns without drawing when the key is not in the dialog's category map.
    /// </summary>
    /// <param name="requested">The category the client sent.</param>
    /// <param name="categories">The published mains.</param>
    /// <returns>The category to send.</returns>
    public static int SelectEnterShopCategory(int requested, IReadOnlyList<ShopCategoryRow> categories)
    {
        ArgumentNullException.ThrowIfNull(categories);
        if (categories.Count == 0)
            return requested;
        if (categories.Any(main => main.Id == requested || main.Subcategories.Any(sub => sub.Id == requested)))
            return requested;
        return categories.OrderBy(main => main.DisplayOrder).ThenBy(main => main.Id).First().Id;
    }

    /// <summary>
    /// The catalog revision the reply carries in its <c>i64</c> after has-data, stored by kr-1256 sub_9ADA00 and sent
    /// back in the next C2S 801: the first eight bytes of the SHA-256 of the body written with revision 0, never 0,
    /// so it changes only when the content does.
    /// </summary>
    /// <param name="body">The reply body written with revision 0.</param>
    /// <returns>The revision.</returns>
    public static long CatalogRevision(ReadOnlySpan<byte> body)
    {
        var digest = System.Security.Cryptography.SHA256.HashData(body);
        var revision = BitConverter.ToInt64(digest, 0);
        return revision == 0 ? 1 : revision;
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
    /// Writes one cn-279 <c>Stock</c> (reader cn-279 <c>0x44CC70</c>): the type field is a
    /// full <c>u32</c> where every other build reads a <c>u16</c>, making the stock
    /// 28 wire bytes. <c>writeWideStockType</c> selects it over
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
    /// <c>gspRepEnterTCShop</c> (S2C 801) with the cash catalog. Handler
    /// kr-1256 <c>sub_9AE0D0</c>: <c>unknown0</c>, has-data, token, card vector.
    ///
    /// </summary>
    /// <param name="token">Token echoed to <c>dword_18E56E8+38</c> when has-data is set.</param>
    /// <param name="cards">On-sale cash stock sets.</param>
    public static byte[] RepEnterTCShop(long token, IReadOnlyList<ShopCardRow> cards)
    {
        if (cards.Count == 0)
            throw new InvalidOperationException("S2C 801 cannot be written with zero cash-shop cards.");
        var w = PacketWriter.Create();
        w.WriteInt32(0);
        w.WriteBool32(true);
        w.WriteInt64(token);
        WriteCards(w, cards);
        return Written(w);
    }

    /// <summary>
    /// Legacy <c>gspRepShopBuy</c> (S2C 813). kr-812 reader <c>sub_6DB1B0</c> and handler
    /// kr-812 <c>sub_827550</c>: the purchase key and a result code. Result 1 opens the success
    /// dialog; any other value takes a failure path. The reply does not carry a price,
    /// a duration, or a bag update.
    ///
    /// </summary>
    /// <param name="key">The C2S 808 key, echoed.</param>
    /// <param name="result">1 when the key is an on-sale catalog row.</param>
    public static byte[] RepLegacyShopBuy(int key, int result)
    {
        var w = PacketWriter.Create();
        w.WriteInt32(key);
        w.WriteInt32(result);
        return Written(w);
    }

    /// <summary>
    /// Failure pin of <c>gspRepShopBuy</c> (S2C 816). Parser kr-1256 <c>sub_99B260</c>:
    /// purchase key, result 0, empty prefixed name, <c>u8</c>, three trailing
    /// <c>i32</c>. Result non-1 is the failure path in kr-1256 <c>sub_9B6730</c>.
    ///
    /// </summary>
    /// <param name="itemId">The echoed request id.</param>
    public static byte[] RepShopBuyRefused(int itemId) => RepShopBuy(itemId, 0, string.Empty);

    /// <summary>
    /// Missing-row pin of <c>gspRepShopItemCardInfo</c> (S2C 817). Handler
    /// kr-1256 <c>sub_9B7AE0</c>: result <c>-1</c> plus an empty <c>ShopItemCardInfo</c>
    /// (reader kr-1256 <c>sub_1140350</c>); the <c>-1</c> branch is not a success update.
    ///
    /// </summary>
    public static byte[] RepShopItemCardInfoRefused() => RepShopItemCardInfo(-1, null);

    /// <summary>
    /// <c>gspRepShopBuy</c> (S2C 816). Parser kr-1256 <c>sub_99B260</c>: purchase key, result,
    /// prefixed name, <c>u8</c>, three trailing <c>i32</c>. Result <c>1</c> is the only
    /// success value in kr-1256 <c>sub_9B6730</c>. Inventory grant packets are not appended.
    ///
    /// </summary>
    /// <param name="itemId">Stock or StockSet request key echoed at object +0; not an equipment item id.</param>
    /// <param name="result">1 when the purchase succeeds; any other value is failure.</param>
    /// <param name="name">Reply text; empty on success.</param>
    /// <param name="unknown40">
    /// Second trailing <c>i32</c> (object +40), passed through kr-1256 <c>sub_9B6730</c>
    /// to constructor kr-1256 <c>sub_E4D6A0</c> and stored at dialog +1672. Its meaning
    /// remains unknown; the recorded success keeps it zero. The first reply
    /// field, stored at dialog +1660, selects the purchased Stock or StockSet.
    /// </param>
    public static byte[] RepShopBuy(int itemId, int result, string name, int unknown40 = 0)
    {
        var w = PacketWriter.Create();
        w.WriteInt32(itemId);
        w.WriteInt32(result);
        w.WriteWideStringPrefixed(name);
        w.WriteByte(0);           // +32: selects the normal-buy dialog variant
        w.WriteInt32(0);          // +36
        w.WriteInt32(unknown40);  // +40: the value DlgResponseBuy actually consumes
        w.WriteInt32(0);          // +44: written, never read by the handler
        return Written(w);
    }

    /// <summary>
    /// <c>gspRepShopItemCardInfo</c> (S2C 817). Result <c>1</c> updates a known card
    /// (kr-1256 <c>sub_9B7AE0</c> / kr-1256 <c>sub_5F52D0</c>). Result <c>-1</c> is the missing-row path.
    ///
    /// </summary>
    /// <param name="result">1 when <paramref name="card"/> is present; otherwise -1.</param>
    /// <param name="card">The catalog row, or <see langword="null"/> for the empty card.</param>
    public static byte[] RepShopItemCardInfo(int result, ShopCardRow? card)
    {
        var w = PacketWriter.Create();
        w.WriteInt32(result);
        if (card is null)
            WriteEmptyItemCard(w);
        else
            WriteItemCard(w, card);
        return Written(w);
    }

    /// <summary>
    /// <c>gspRepRealCouponUse</c> (S2C 833). Case kr-1256 <c>0x9D4E88</c> always consumes three
    /// <c>i32</c>. <c>unknown0 == 1</c> is the only success branch. <c>-1</c> is a proven
    /// error-dialog code. ShopItem2.xml has no coupon sheet, so success is not written.
    ///
    /// </summary>
    /// <param name="unknown0">Result / branch. 1 is success; -1 is a listed error.</param>
    public static byte[] RepRealCouponUse(int unknown0)
    {
        var w = PacketWriter.Create();
        w.WriteInt32(unknown0);
        w.WriteInt32(0);
        w.WriteInt32(0);
        return Written(w);
    }

    /// <summary>
    /// Pin form of <c>gspRepRealCouponUse</c> (S2C 833). Dispatcher case
    /// kr-1256 <c>0x9D4E88</c> always consumes three <c>i32</c>; zeros are the documented
    /// empty body before handing off to kr-1256 <c>sub_6489C0</c>.
    ///
    /// </summary>
    public static byte[] RepRealCouponUseEmpty() => RepRealCouponUse(0);

    /// <summary>
    /// <c>gspRepCheckCash</c> (S2C 834). Case kr-1256 <c>0x9D4C62</c> always consumes three
    /// <c>i32</c>. The second dword is stored at <c>dword_18E56E8+0x50</c> and is the
    /// in-plugin cash balance.
    ///
    /// </summary>
    /// <param name="unknown0">First dword; meaning unproven. Written 0.</param>
    /// <param name="cash">Second dword: current cash.</param>
    /// <param name="unknown8">Third dword; consumed and unused. Written 0.</param>
    public static byte[] RepCheckCash(int unknown0, int cash, int unknown8)
    {
        var w = PacketWriter.Create();
        w.WriteInt32(unknown0);
        w.WriteInt32(cash);
        w.WriteInt32(unknown8);
        return Written(w);
    }

    /// <summary>
    /// Pin form of <c>gspRepCheckCash</c> (S2C 834). Dispatcher case
    /// kr-1256 <c>0x9D4C62</c> always consumes three <c>i32</c>; zeros leave
    /// <c>dword_18E56E8+0x50</c> cleared.
    ///
    /// </summary>
    public static byte[] RepCheckCashEmpty() => RepCheckCash(0, 0, 0);

    /// <summary>
    /// <c>gspUserMoneyStat</c> (S2C 106) with a stored wallet snapshot: the six
    /// proven <c>i32</c> fields in wire order. Inline case kr-1256 <c>0x9D3328</c> writes
    /// field 5 to <c>dword_18E56E8+0x4C</c> and field 6 to <c>+0x50</c>. The login
    /// burst already carries the all-zero form; this row re-sends it after a
    /// wallet change because the S2C 834 <c>gspRepCheckCash</c> reply only writes
    /// the cash dword, and the client pre-checks the cached lucci dword before
    /// sending a buy.
    ///
    /// </summary>
    /// <param name="money">The stored money snapshot.</param>
    public static byte[] RepUserMoneyStat(PlayerWallet money)
    {
        var w = PacketWriter.Create();
        w.WriteInt32(0);
        w.WriteInt32(0);
        w.WriteInt32(0);
        w.WriteInt32(0);
        w.WriteInt32(money.Lucci);
        w.WriteInt32(money.Cash);
        return Written(w);
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
    /// <returns>The plain body; wrap it with <see cref="InventoryUpdateSlotsEquipHoldEnvelope"/> before sending.</returns>
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
    public static byte[] InventoryUpdateSlotsEquipHoldEnvelope(IReadOnlyList<EquippedSlotRecord> container2) =>
        CompressedEnvelope(InventoryUpdateSlotsEquipHold(container2));

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
    /// Wraps a plain body in the compressed envelope the inventory family reads:
    /// <c>u32</c> length followed by one zlib stream, the same primitive the
    /// S2C 1500 config blobs use. Client side the header value only bounds the
    /// copy into the zlib stream, so writing the plain length stays correct under
    /// any strict output-size interpretation.
    ///
    /// </summary>
    /// <param name="plain">The uncompressed body.</param>
    private static byte[] CompressedEnvelope(byte[] plain)
    {
        using var target = new MemoryStream();
        using (var zlib = new ZLibStream(target, CompressionLevel.Fastest))
            zlib.Write(plain);
        var w = PacketWriter.Create();
        w.WriteUInt32(checked((uint)plain.Length));
        w.WriteBytes(target.ToArray());
        return Written(w);
    }

    /// <summary>
    /// Writes a counted <c>ShopItemCardInfo</c> vector (kr-1256 <c>sub_10D3780</c>).
    ///
    /// </summary>
    /// <param name="w">The destination writer.</param>
    /// <param name="cards">Catalog rows.</param>
    /// <param name="unknown56">First per-card <c>i64</c>; the kr-812 catalog carries a sale-window bound pair here.</param>
    /// <param name="unknown64">Second per-card <c>i64</c>; see <paramref name="unknown56"/>.</param>
    /// <param name="vectorOnlyStocks">Pass true for the id-400 card reader id-400 <c>sub_45FB30</c>.</param>
    public static void WriteCards(
        PacketWriter w, IReadOnlyList<ShopCardRow> cards, long unknown56 = 0, long unknown64 = 0,
        bool vectorOnlyStocks = false)
    {
        w.WriteUInt32((uint)cards.Count);
        foreach (var card in cards)
        {
            WriteItemCard(w, card, unknown56, unknown64, vectorOnlyStocks);
        }
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
    /// Writes a default-constructed <c>ShopItemCardInfo</c>: scalars 0, empty name,
    /// default <c>Stock</c> (<c>u16</c> at Stock+14 is 1 per kr-1256 <c>sub_11400D0</c>), empty
    /// extra-stock vector. Serializer kr-1256 <c>sub_1140210</c>, reader kr-1256 <c>sub_1140350</c>.
    ///
    /// </summary>
    /// <param name="w">The destination writer.</param>
    public static void WriteEmptyItemCard(PacketWriter w)
    {
        w.WriteInt32(0);
        w.WriteInt32(0);
        w.WriteWideStringPrefixed(string.Empty);
        w.WriteInt32(0);
        w.WriteInt32(0);
        w.WriteInt32(0);
        w.WriteBool32(false);
        w.WriteInt64(0);
        w.WriteInt64(0);
        WriteEmptyStock(w);
        w.WriteUInt32(0);
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

    internal static byte[] Written(PacketWriter w) => ((ArrayBufferWriter<byte>)w.Buffer).WrittenSpan.ToArray();
}
