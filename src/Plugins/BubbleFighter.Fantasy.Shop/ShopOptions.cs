using System.ComponentModel.DataAnnotations;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Presents;

namespace BubbleFighter.Fantasy.Shop;

/// <summary>
/// Configuration for the <see cref="ShopPlugin"/> row. Protocol name and
/// <see cref="VersionMajor"/> are supplied by the profile; this plugin does not
/// hard-code a client region in its id. Catalog paths are interpolated
/// (<c>${VAR}</c> / <c>${VAR:-default}</c>) by the host.
///
/// </summary>
public sealed class ShopOptions : IValidatableOptions
{
    /// <summary>Gets or sets whether this row handles inventory entry only, without loading a sale catalog or registering purchases.</summary>
    public bool InventoryOnly { get; set; }

    /// <summary>
    /// Gets or sets the ShopItem stock types whose day duration of -1 the client sells as permanent.
    /// kr-812 and the later builds use type 9; cn-279's older workbook also uses type 1.
    ///
    /// </summary>
    public ushort[] PermanentDayStockTypes { get; set; } = [9];

    /// <summary>
    /// Gets or sets whether repeat purchase of an owned function-10001 item is refused, as the
    /// kr-812, cn-717 and id-400 clients do; cn-324 and cn-279 predate that ownership gate.
    ///
    /// </summary>
    public bool RejectRepeatFunction10001 { get; set; } = true;

    /// <summary>
    /// Gets or sets whether a ShopItem with <c>durationType</c> endurance sells as a life-counted item, its stock
    /// duration being the life granted (kr-812 sub_993080 gives it item kind 3). Off, such stock is refused before
    /// charging. Needs the build's <c>life_items</c> capability. YAML key <c>life_items</c>.
    ///
    /// </summary>
    public bool LifeItems { get; set; }

    /// <summary>
    /// Gets or sets whether the gift box is served: buying a shop item for another player, listing the received and sent
    /// gifts, taking, throwing away and removing them, and the new-gift icon. Needs the build's <c>present_box</c>
    /// capability, the dialect row's <c>present_box</c> and a persistence row that provides the gift store; the receiver
    /// takes a gift through the purchase path, so <see cref="GrantPurchasedItems"/> must be on. YAML key
    /// <c>present_box</c>.
    ///
    /// </summary>
    public bool PresentBox { get; set; }

    /// <summary>
    /// Gets or sets how many gifts either gift list of one player may hold at once, counting what the player has not
    /// yet removed: received gifts, taken or not, and sent gifts. A send that would exceed it is refused before any
    /// charge. YAML key <c>max_presents_per_player</c>.
    ///
    /// </summary>
    [Range(1, 4096)]
    public int MaxPresentsPerPlayer { get; set; } = 100;

    /// <summary>
    /// Gets or sets the longest gift message in characters; a longer one is refused with the client's "inappropriate
    /// message" result. The client's own edit-control limit is not proven. YAML key <c>max_present_message_chars</c>.
    ///
    /// </summary>
    [Range(1, 1024)]
    public int MaxPresentMessageChars { get; set; } = 200;

    /// <summary>
    /// Gets or sets whether an item priced in cash can be given as a gift. Off, such a gift is refused with the client's
    /// "cash items are not supported" result. YAML key <c>cash_presents</c>.
    ///
    /// </summary>
    public bool CashPresents { get; set; } = true;

    /// <summary>
    /// Gets or sets overrides for gifts at attained levels, keyed by level. Empty retains only the native guide's rewards.
    /// Login and level rises recover unclaimed eligible rewards, including levels attained before enablement. A level may
    /// appear once. Needs <see cref="PresentBox"/>; on a row without it the table is ignored with a warning, since a
    /// build that has no gift box cannot deliver it. Every stock key must have grant contents in the build's workbook. YAML key
    /// <c>level_rewards</c>, each entry <c>level</c> and <c>stock_keys</c>.
    ///
    /// </summary>
    public ShopLevelReward[] LevelRewards { get; set; } = [];

    /// <summary>
    /// Gets or sets the optional native level guide path read through clientdata. Empty disables resource rewards.
    /// Missing guides and keys without grant contents are reported; explicit level_rewards override matching levels.
    /// YAML key <c>level_reward_client_path</c>.
    ///
    /// </summary>
    public string LevelRewardClientPath { get; set; } = "";

    /// <summary>
    /// Gets or sets the sender name the gift box shows for a level reward. YAML key <c>level_reward_sender</c>.
    ///
    /// </summary>
    [Required, StringLength(PresentLimits.MaxNicknameChars, MinimumLength = 1)]
    public string LevelRewardSender { get; set; } = "System";

    /// <summary>
    /// Gets or sets the message of a level reward gift; <c>{level}</c> is replaced by the level reached. YAML key
    /// <c>level_reward_message</c>.
    ///
    /// </summary>
    [Required]
    public string LevelRewardMessage { get; set; } = "Level {level} reward";

    /// <summary>Gets or sets the maximum attempts to replan after an explicit pre-commit inventory conflict.</summary>
    [Range(1, 16)]
    public int MaxPurchaseAttempts { get; set; } = 3;

    /// <summary>Gets or sets the game-connection protocol name this row handles.</summary>
    [Required]
    public string Protocol { get; set; } = "kr-1256/game";

    /// <summary>Gets or sets the client profile id that selected this dialect.</summary>
    [Required]
    public string ClientProfile { get; set; } = "kr-1256";

    /// <summary>Gets or sets the client major version this row serves; it is logged, and layouts come from the tables.</summary>
    [Range(1, int.MaxValue)]
    public int VersionMajor { get; set; } = 1256;

    /// <summary>
    /// Gets or sets the packet-table directory. A <c>legacy</c> catalog takes the build's request and
    /// catalog layouts from the <c>shop</c> values of its <c>builds.yaml</c> entry (build = <see cref="ClientProfile"/>).
    ///
    /// </summary>
    public string PacketsDirectory { get; set; } = "packets";

    /// <summary>
    /// Gets or sets the workbook's client path (for example <c>Config/ShopItem2.xml</c>), read from the build's
    /// client data through <c>IClientDataStore</c>. Required unless <see cref="InventoryOnly"/> is set.
    ///
    /// </summary>
    public string ShopItemClientPath { get; set; } = "";

    /// <summary>Gets or sets the inclusive catalog file-size cap in bytes.</summary>
    [Range(1, 200_000_000)]
    public long MaxShopItemBytes { get; set; } = 134_217_728;

    /// <summary>
    /// Gets or sets the verified workbook column schema of <see cref="ShopItemClientPath"/>.
    /// <c>kr1256</c> (default) reads the 1256 workbook strictly; <c>legacy</c> reads
    /// the shared 812/717/400/324 workbook (nameKr display column, no secondary cash
    /// columns, category and stock references the client leaves unresolved at 0,
    /// cashCN money types on the CN clients).
    ///
    /// </summary>
    [RegularExpression("kr1256|legacy")]
    public string CatalogSchema { get; set; } = "kr1256";

    /// <summary>
    /// Gets or sets the category-name column the catalog sends as the category tree's
    /// display text. The kr-812 catalog carries the nameKr strings;
    /// the CN and ID clients localize through name. Empty picks the schema default
    /// (displayName for kr1256, nameKr for legacy).
    ///
    /// </summary>
    [RegularExpression("^(|displayName|nameKr|name)$")]
    public string CatalogDisplayColumn { get; set; } = "";

    /// <summary>
    /// Gets or sets the inclusive S2C 800 payload budget for legacy dialects. Their
    /// framing uses a 16-bit payload length (maximum 65535 bytes); 65000 leaves room
    /// below that wire limit but is a server policy, not a client-requested size.
    /// Cards beyond the budget (or beyond
    /// <see cref="Wire.ShopWire.LegacyEnterShopCardCap"/>) are omitted in
    /// deterministic emit order — mains 1000 and 500 first — and logged.
    /// The kr-1256 dialect ignores this setting.
    ///
    /// </summary>
    [Range(4096, 65535)]
    public int MaxEnterShopFrameBytes { get; set; } = 65000;

    /// <summary>Gets or sets the legacy catalog card limit; zero uses only the encoded frame budget.</summary>
    [Range(0, 10000)]
    public int MaxEnterShopCards { get; set; } = Wire.ShopWire.LegacyEnterShopCardCap;

    /// <summary>Gets or sets whether identical legacy grant contents retain only their longest sale duration.</summary>
    public bool PreferLongestDuration { get; set; } = true;

    /// <summary>Gets or sets whether gun offers retain only the longest duration or largest quantity.</summary>
    public bool PreferLongestGuns { get; set; } = true;

    /// <summary>Gets or sets whether clothing offers retain only the longest duration.</summary>
    public bool PreferLongestClothes { get; set; } = true;

    /// <summary>Gets or sets whether grenade offers retain only the longest duration or largest quantity.</summary>
    public bool PreferLongestGrenades { get; set; } = true;

    /// <summary>Gets or sets whether hammer offers retain only the longest duration.</summary>
    public bool PreferLongestHammers { get; set; } = true;

    /// <summary>Gets or sets whether packages with identical grant items retain only their longest terms.</summary>
    public bool PreferLongestPackages { get; set; } = true;

    /// <summary>Gets or sets native item identities that always retain their longest term, overriding category switches.</summary>
    public int[] LongestTermItemIds { get; set; } = [];

    /// <summary>Gets or sets existing client items to offer only when none of their stocks is on sale.</summary>
    public ShopSupplementOffer[] SupplementalOffers { get; set; } = [];

    /// <summary>
    /// Gets or sets the in-plugin lucci seed used when a session first opens the
    /// item shop. Deployment tunable; not a silent catalog default.
    ///
    /// </summary>
    [Range(0, int.MaxValue)]
    public int StartingLucci { get; set; } = 100_000;

    /// <summary>
    /// Gets or sets the in-plugin cash seed returned by S2C 834 and spent on
    /// cash-priced stock sets. Unused when the persistence row is active; the
    /// database wallet is the store then.
    ///
    /// </summary>
    [Range(0, int.MaxValue)]
    public int StartingCash { get; set; }

    /// <summary>
    /// Gets or sets whether a successful purchase is charged to the stored
    /// wallet, logged in the purchase table, granted into the stored inventory
    /// and pushed back to the client. Defaults to <see langword="true"/>;
    /// activation fails loud when the persistence row or the lobby inventory
    /// push service is missing while this is set.
    ///
    /// </summary>
    public bool GrantPurchasedItems { get; set; } = true;

    /// <summary>
    /// Gets or sets whether a successful purchase also queues one S2C 803
    /// container-2 equip record for the buying player, flushed when that client
    /// sends its own C2S 803 (the inventory-dialog open). Defaults to
    /// <see langword="false"/>, which keeps every purchase reply byte-identical
    /// to the pre-hold behaviour and leaves C2S 803 unregistered. Turning it on
    /// is required for the hold path: the client establishes the my-fighter
    /// weapon category only when the player opens the inventory dialog
    /// (<c>sub_932E10</c> -> <c>sub_5F3C50</c> -> <c>sub_5F7C90</c> ->
    /// <c>sub_5F8C30(300, 0)</c>), so an equip record sent earlier is skipped by
    /// the handler's own guards. See
    /// <c>src/Plugins/BubbleFighter.Fantasy.Shop/Data/EquipHoldStore.cs</c>.
    ///
    /// </summary>
    public bool HoldEquipUntilInventoryOpen { get; set; }

    /// <summary>
    /// Gets or sets whether C2S 801 is answered with the S2C 800 catalog reply.
    /// Defaults to <see langword="false"/>.
    ///
    /// </summary>
    /// <remarks>
    /// The reply is disabled by deployment profiles while the native shop parser
    /// crash remains unresolved. The envelope and nested records have been checked
    /// against kr-1256 sub_99B9A0, sub_1140350, sub_1140210, sub_1141B00,
    /// sub_1141910, sub_113EF70 and sub_11404D0; these checks do not establish
    /// successful native-client rendering. The dialog also depends on static slot
    /// resources refreshed by kr-1256 sub_645B60. Enabling the reply requires a
    /// real-client test, including category changes and purchase display.
    /// <para>
    /// The optional <c>catalog_card_limit</c> bounds cards for parser diagnosis;
    /// reducing it does not by itself establish that the reply is safe to enable.
    /// </para>
    /// </remarks>
    public bool SendShopCatalog { get; set; }

    /// <summary>
    /// Gets or sets a diagnostic cap on how many catalog cards the reply carries.
    /// Zero means all of them. Used to bisect a client-side crash against the
    /// card vector: send none, and if the crash persists the fault is in the
    /// envelope or the categories rather than in a card.
    ///
    /// </summary>
    public int CatalogCardLimit { get; set; }

    /// <summary>
    /// Gets or sets the main category the S2C 800 reply tells the dialog to open
    /// on: the trailing <c>i32</c> the parser reads at object +68
    /// (<c>sub_99B9A0</c>), which the reply handler hands to the fill as the
    /// selected category. The handler's own literals for this dialog are 500
    /// (<c>sub_9ADA00</c>, <c>:112</c>). Naming a category the same reply does not
    /// carry is a silent blank page under a built tab bar rather than a crash, so
    /// the plugin logs a warning whenever the id is absent from the loaded catalog.
    ///
    /// </summary>
    public int EnterShopMainCategory { get; set; } = 500;

    /// <summary>
    /// Gets or sets how the S2C 800 tab tree is built. <c>card_names</c> (default) links pages to mains through the
    /// cards' sub-category names and sends <see cref="EnterShopMainCategory"/>. <c>id_ranges</c> groups every page under
    /// the main its id falls in, echoes the requested category when published and sends a content revision (see
    /// <c>ShopWire.BuildRangeTree</c>); for workbooks whose page names repeat. YAML key <c>catalog_tree</c>.
    /// </summary>
    public string CatalogTree { get; set; } = "card_names";

    /// <summary>
    /// Gets or sets the maximum number of live connections whose shop-open state
    /// this row may remember at once. Defaults to 512 to match the usual game
    /// listener <c>max_connections</c>; overflow refuses a new open rather than
    /// growing without bound.
    ///
    /// </summary>
    [Range(1, int.MaxValue)]
    public int MaxOpenSessions { get; set; } = 512;

    /// <summary>The most entries <see cref="LevelRewards"/> may hold.</summary>
    public const int MaxLevelRewardEntries = 1024;

    /// <summary>The highest level a level reward may name; the client's level field is far below it.</summary>
    public const int MaxRewardLevel = 9999;

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate()
    {
        if (CatalogTree is not ("card_names" or "id_ranges"))
            yield return new ValidationResult("catalog_tree must be card_names or id_ranges.", [nameof(CatalogTree)]);
        if (InventoryOnly)
            yield break;
        if (PermanentDayStockTypes is null || PermanentDayStockTypes.Length is 0 or > 8
                || PermanentDayStockTypes.Distinct().Count() != PermanentDayStockTypes.Length)
            yield return new ValidationResult("permanent_day_stock_types requires one to eight distinct stock types.", [nameof(PermanentDayStockTypes)]);

        if (LongestTermItemIds.Length > 256 || LongestTermItemIds.Any(id => id <= 0)
            || LongestTermItemIds.Distinct().Count() != LongestTermItemIds.Length)
            yield return new ValidationResult("longest_term_item_ids requires at most 256 distinct positive item identities.", [nameof(LongestTermItemIds)]);
        if (SupplementalOffers.Any(offer => offer.ClientProfiles.Any(string.IsNullOrWhiteSpace)
                || offer.StockId < 0 || (offer.StockId == 0) != (offer.DurationDays == 0)
                || (offer.StockId != 0 && (offer.ItemId <= 0 || offer.StockId is < 1_000_000 or >= 2_000_000
                    || offer.DurationDays is < 1 or > 10_000)))
            || SupplementalOffers.Where(offer => offer.StockId > 0).Select(offer => offer.StockId).Distinct().Count()
                != SupplementalOffers.Count(offer => offer.StockId > 0))
            yield return new ValidationResult("Server stock supplements require distinct stock_id values in [1000000, 2000000), an item_id and duration_days in [1, 10000].", [nameof(SupplementalOffers)]);
        if (SupplementalOffers.Length > 256 || SupplementalOffers.Any(offer => offer.ItemId < 0 || offer.StockSetId < 0
                || (offer.ItemId > 0) == (offer.StockSetId > 0) || offer.Price < 0 || offer.MoneyType is not ("lucci" or "cash" or "client"))
            || SupplementalOffers.Select(offer => (offer.ItemId, offer.StockSetId)).Distinct().Count() != SupplementalOffers.Length)
            yield return new ValidationResult("supplemental_offers requires at most 256 distinct identities, exactly one positive item_id or stock_set_id, and nonnegative prices.", [nameof(SupplementalOffers)]);
        if (CatalogSchema is not ("kr1256" or "legacy"))
        {
            yield return new ValidationResult(
                $"catalog_schema '{CatalogSchema}' is not a known shop dialect; use 'kr1256' or 'legacy'.",
                [nameof(CatalogSchema)]);
        }
        else if (CatalogSchema == "legacy" && string.IsNullOrWhiteSpace(PacketsDirectory))
        {
            yield return new ValidationResult("A legacy catalog requires packets_directory.", [nameof(PacketsDirectory)]);
        }

        if (string.IsNullOrWhiteSpace(ShopItemClientPath))
        {
            yield return new ValidationResult(
                "shop_item_client_path is required: it names Config/ShopItem2.xml in the build's client data.",
                [nameof(ShopItemClientPath)]);
        }

        if (LevelRewards is null || LevelRewards.Length > MaxLevelRewardEntries
            || LevelRewards.Any(entry => entry is null || entry.Level < 2 || entry.Level > MaxRewardLevel
                || entry.StockKeys is null || entry.StockKeys.Length is 0 or > PresentLimits.MaxRewardGifts
                || entry.StockKeys.Any(key => key <= 0))
            || LevelRewards.Select(entry => entry.Level).Distinct().Count() != LevelRewards.Length)
        {
            yield return new ValidationResult(
                $"level_rewards takes at most {MaxLevelRewardEntries} entries with distinct levels in [2, {MaxRewardLevel}], "
                + $"each with one to {PresentLimits.MaxRewardGifts} positive stock_keys.", [nameof(LevelRewards)]);
        }
        else if (LevelRewardMessage is { Length: > 0 }
            && LevelRewardMessage.Replace("{level}", MaxRewardLevel.ToString(System.Globalization.CultureInfo.InvariantCulture),
                StringComparison.Ordinal).Length > MaxPresentMessageChars)
        {
            yield return new ValidationResult(
                "level_reward_message is longer than max_present_message_chars once {level} is filled in.",
                [nameof(LevelRewardMessage), nameof(MaxPresentMessageChars)]);
        }

        if (PresentBox && !GrantPurchasedItems)
        {
            yield return new ValidationResult(
                "present_box is on but grant_purchased_items is off: a receiver takes a gift through the purchase path, "
                + "so no gift could ever be delivered.",
                [nameof(PresentBox), nameof(GrantPurchasedItems)]);
        }

        if (HoldEquipUntilInventoryOpen && !GrantPurchasedItems)
        {
            yield return new ValidationResult(
                "hold_equip_until_inventory_open is on but grant_purchased_items is off: a purchase then grants no bag slot, "
                + "so there is no purchased weapon to publish and no equip record would ever be queued.",
                [nameof(HoldEquipUntilInventoryOpen), nameof(GrantPurchasedItems)]);
        }
    }
}
