using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Shop;
using BubbleFighter.Fantasy.Contracts.Wallet;
using BubbleFighter.Fantasy.Packets;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Dialect.Messages;

/// <summary>
/// The shop catalog dialog adapters between wire opcodes and the shared shop contracts. The modern family
/// (whose packet tables state <c>EnterShopReply</c>) reads and writes every body through the tables, and the
/// same mount owns the modern balance check (C2S <c>Unknown827</c>) with its wallet answer; a legacy build
/// instead uses its <c>shop.catalog</c> values in <c>packets/builds.yaml</c> through
/// <see cref="ShopCatalogWire"/>, and only its catalog entry is bound because the purchase requests ride the
/// purchases block. The logic row supplies the catalog content; this class never decides what is on sale.
/// </summary>
internal sealed class ShopCatalogBinding(
    string protocol, IOpcodeTable table, PacketTables tables, string build, ILogger logger) :
    IInboundMessageBinding<ShopCatalogRequested>, IInboundMessageBinding<ShopCashCatalogRequested>,
    IInboundMessageBinding<ShopCardInfoRequested>, IInboundMessageBinding<ShopCouponUseRequested>,
    IInboundMessageBinding<ShopCashCheckRequested>, IInboundMessageBinding<ShopBalanceRequested>,
    IMessageSender<ShopCatalogPublished>, IMessageSender<ShopCashCatalogPublished>,
    IMessageSender<ShopCardInfoReplied>, IMessageSender<ShopCouponUseReplied>,
    IMessageSender<ShopCashCheckReplied>, IMessageSender<ShopPurchaseReplied>,
    IMessageSender<EquippedArticlesFlushed>, IMessageSender<ShopBalanceUpdated>
{
    /// <summary>Whether the tables state the modern catalog reply for this build.</summary>
    private readonly bool _modern = tables.Resolve(build, "EnterShopReply").Count > 0;

    /// <summary>The typed router the mounted request handlers dispatch through; set by the mounting row.</summary>
    private IMessageRouter? router;

    /// <summary>Gets the connection protocol scope in which this adapter is installed.</summary>
    public string Protocol => protocol;

    /// <summary>Reads C2S <c>gcpEnterShop</c> into the catalog request; the build's row states the body it sends.</summary>
    /// <param name="tables">The packet tables.</param>
    /// <param name="build">The client build.</param>
    /// <param name="body">The decrypted payload.</param>
    /// <returns>The typed request; a build without a token or main word carries zeros.</returns>
    internal static ShopCatalogRequested ReadEnterShop(PacketTables tables, string build, ReadOnlySpan<byte> body)
    {
        var values = PacketCodec.ReadScalars(tables, build, "gcpEnterShop", body);
        return new(values.TryGetValue("token", out var token) ? (long)token : 0,
            values.TryGetValue("unknown_16", out var main) ? (int)main : 0);
    }

    /// <summary>Reads C2S <c>gcpEnterTCShop_</c>; kr-1256 builder <c>sub_64DAB0</c> writes the token alone.</summary>
    /// <param name="tables">The packet tables.</param>
    /// <param name="build">The client build.</param>
    /// <param name="body">The decrypted payload.</param>
    internal static ShopCashCatalogRequested ReadCashShopEntry(PacketTables tables, string build, ReadOnlySpan<byte> body) =>
        new((long)PacketCodec.ReadScalars(tables, build, "gcpEnterTCShop_", body)["token"]);

    /// <summary>Reads C2S <c>gcpReqShopItemCardInfo</c>; kr-1256 builder <c>sub_814490</c> writes one key.</summary>
    /// <param name="tables">The packet tables.</param>
    /// <param name="build">The client build.</param>
    /// <param name="body">The decrypted payload.</param>
    internal static ShopCardInfoRequested ReadCardInfo(PacketTables tables, string build, ReadOnlySpan<byte> body)
    {
        var values = PacketCodec.ReadScalars(tables, build, "gcpReqShopItemCardInfo", body);
        return new(values.TryGetValue("unknown_0", out var key) ? (int)key
            : values.TryGetValue("unknown_4", out var legacyKey) ? (int)legacyKey : 0);
    }

    /// <summary>Reads C2S <c>gcpReqRealCouponUse</c>; kr-1256 builder <c>sub_E30E70</c> writes one wide string.</summary>
    /// <param name="tables">The packet tables.</param>
    /// <param name="build">The client build.</param>
    /// <param name="body">The decrypted payload.</param>
    internal static ShopCouponUseRequested ReadCouponUse(PacketTables tables, string build, ReadOnlySpan<byte> body)
    {
        var values = PacketCodec.ReadScalars(tables, build, "gcpReqRealCouponUse", body);
        return new((string)values.GetValueOrDefault("unknown_0", values.GetValueOrDefault("unknown_4", string.Empty)));
    }

    /// <summary>Reads C2S 834; kr-1256 builder <c>sub_E30E70</c> writes one wide string on feature 1018.</summary>
    /// <param name="tables">The packet tables.</param>
    /// <param name="build">The client build.</param>
    /// <param name="body">The decrypted payload.</param>
    internal static ShopCashCheckRequested ReadCashCheck(PacketTables tables, string build, ReadOnlySpan<byte> body) =>
        new((string)PacketCodec.ReadScalars(tables, build, "Unknown834", body)["unknown_0"]);

    /// <summary>
    /// Reads the modern C2S purchase request: kr-1256 builder <c>sub_E1D720</c> writes the key and a
    /// zero-extended byte whose live call sites write 0, so the ordinary-purchase flag is that word being zero.
    /// </summary>
    /// <param name="tables">The packet tables.</param>
    /// <param name="build">The client build.</param>
    /// <param name="body">The decrypted payload.</param>
    internal static ShopPurchaseRequested ReadModernPurchase(PacketTables tables, string build, ReadOnlySpan<byte> body)
    {
        PacketLayoutContract.RequireFields(tables, build, "ShopPurchaseRequest", "Shop purchase",
            ("key", "u32"), ("unknown_4", "u32"));
        var values = PacketCodec.ReadScalars(tables, build, "ShopPurchaseRequest", body);
        return new((int)values["key"], (int)values["unknown_4"] == 0);
    }

    /// <summary>
    /// The catalog revision the modern reply carries in its token, stored by kr-1256 <c>sub_9ADA00</c> and sent
    /// back in the next C2S 801: the first eight bytes of the SHA-256 of the body written with revision 0, never 0,
    /// so it changes only when the content does.
    /// </summary>
    /// <param name="body">The reply body written with revision 0.</param>
    /// <returns>The revision.</returns>
    internal static long CatalogRevision(ReadOnlySpan<byte> body)
    {
        var digest = System.Security.Cryptography.SHA256.HashData(body);
        var revision = BitConverter.ToInt64(digest, 0);
        return revision == 0 ? 1 : revision;
    }

    /// <summary>Mounts the modern family's shop dialogs: every request the kr-1256 shop row answered raw.</summary>
    /// <param name="context">The dialect row's activation context.</param>
    /// <param name="messageRouter">The typed message router.</param>
    internal void MountModern(IPluginContext context, IMessageRouter messageRouter)
    {
        // Shop balance replies also run without the login binding; validate their consumed fields here.
        PacketLayoutContract.RequireFields(tables, build, "UserMoneyStat", "Shop balance",
            ("unknown_16", "u32"), ("unknown_20", "u32"));
        router = messageRouter;
        var packets = context.Services.Get<IPacketRouter>();
        context.Effect(context.Services.RegisterMany<IMessageSender<ShopCatalogPublished>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<ShopCashCatalogPublished>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<ShopCardInfoReplied>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<ShopCouponUseReplied>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<ShopCashCheckReplied>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<ShopPurchaseReplied>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<ShopBalanceUpdated>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<EquippedArticlesFlushed>>(this));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<ShopCatalogRequested>>(this));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<ShopCashCatalogRequested>>(this));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<ShopCardInfoRequested>>(this));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<ShopCouponUseRequested>>(this));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<ShopCashCheckRequested>>(this));
        context.Effect(packets.Register(protocol, table.C2S("gcpEnterShop"), Dispatch(
            body => ReadEnterShop(tables, build, body), "catalog entry")));
        if (table.Find(PacketDirection.ClientToServer, "gcpEnterTCShop_") is { } cashEntry)
            context.Effect(packets.Register(protocol, cashEntry, Dispatch(
                body => ReadCashShopEntry(tables, build, body), "cash catalog entry")));
        context.Effect(packets.Register(protocol, table.C2S("gcpReqShopBuy"), Dispatch(
            body => ReadModernPurchase(tables, build, body), "purchase")));
        context.Effect(packets.Register(protocol, table.C2S("gcpReqShopItemCardInfo"), Dispatch(
            body => ReadCardInfo(tables, build, body), "card info")));
        context.Effect(packets.Register(protocol, table.C2S("gcpReqRealCouponUse"), Dispatch(
            body => ReadCouponUse(tables, build, body), "coupon use")));
        if (table.Find(PacketDirection.ClientToServer, "Unknown834") is { } cashCheck)
            context.Effect(packets.Register(protocol, cashCheck, Dispatch(
                body => ReadCashCheck(tables, build, body), "cash check")));
        // The balance refresh kr-1256 stamps as C2S 827 with no fields (builders kr-1256 sub_56EB50 and
        // kr-1256 sub_56E860); the wallet answer enables the waiting purchase dialog (kr-1256 sub_648810 ->
        // kr-1256 sub_E1D5C0). Mounted only where the build's table names the request; cn-774 and tw-454 do not.
        if (table.Find(PacketDirection.ClientToServer, "Unknown827") is { } balanceRequest)
        {
            context.Effect(context.Services.RegisterMany<IInboundMessageBinding<ShopBalanceRequested>>(this));
            context.Effect(packets.Register(protocol, balanceRequest, Dispatch(
                ShopBinding.ReadBalanceRequest, "cash balance")));
        }
        // Fire-and-forget: kr-1256 sub_92F6C0 latches at dword_18E566C+984 and sends once, and no reply path
        // exists, so the row only accepts the body. The shop entry sub_9328B0 sends it as (0x0D, 0).
        if (table.Find(PacketDirection.ClientToServer, "Unknown2059") is { } sceneBootstrap)
            context.Effect(packets.Register(protocol, sceneBootstrap, (session, packet, _) =>
            {
                var values = PacketCodec.ReadScalars(tables, build, "Unknown2059", packet.Payload.Span);
                logger.LogInformation(
                    "Session {Id} Unknown2059 unknown0=0x{Unknown0:X2} unknown4={Unknown4}; fire-and-forget, no reply.",
                    session.Id, values["unknown_0"], values["unknown_4"]);
                return ValueTask.CompletedTask;
            }));
        // The client's own inventory-dialog open: kr-1256 sub_932E10 writes an empty body. The body is logged and
        // ignored rather than parsed, because no reader shape exists for it.
        if (table.Find(PacketDirection.ClientToServer, "gcpEnterInventory") is { } enterInventory)
            context.Effect(packets.Register(protocol, enterInventory, async (session, packet, ct) =>
            {
                if (packet.Payload.Length != 0)
                    logger.LogError(
                        "Session {Id} gcpEnterInventory carries {Bytes} body byte(s); the client builder writes an empty Lambda::TcpPacket body (kr-1256 sub_932E10). The body is ignored, not parsed.",
                        session.Id, packet.Payload.Length);
                if (!await router.DispatchAsync(protocol, session, new ShopInventoryRequested(), ct).ConfigureAwait(false))
                    throw new InvalidOperationException("The inventory message has no handler.");
            }));
    }

    /// <summary>Mounts a legacy build's catalog entry; the purchase requests ride the purchases block instead.</summary>
    /// <param name="context">The dialect row's activation context.</param>
    /// <param name="messageRouter">The typed message router.</param>
    internal void MountLegacy(IPluginContext context, IMessageRouter messageRouter)
    {
        router = messageRouter;
        var packets = context.Services.Get<IPacketRouter>();
        _ = table.S2C("gspRepEnterShop");
        context.Effect(context.Services.RegisterMany<IMessageSender<ShopCatalogPublished>>(this));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<ShopCatalogRequested>>(this));
        context.Effect(packets.Register(protocol, table.C2S("gcpEnterShop"), Dispatch(
            body => ReadEnterShop(tables, build, body), "catalog entry")));
    }

    private PacketHandler Dispatch<T>(
        Func<ReadOnlySpan<byte>, T> read, string what) where T : IMessage =>
        (session, packet, ct) =>
        {
            var request = read(packet.Payload.Span);
            return RouteAsync(session, request, what, ct);
        };

    private async ValueTask RouteAsync<T>(ISession session, T request, string what, CancellationToken ct)
        where T : IMessage
    {
        var typed = router ?? throw new InvalidOperationException("The shop binding was mounted without its router.");
        if (!await typed.DispatchAsync(protocol, session, request, ct).ConfigureAwait(false))
            throw new InvalidOperationException($"The {what} message has no handler.");
    }

    /// <summary>Encodes and sends the item-shop catalog for one session.</summary>
    /// <param name="session">The destination session.</param>
    /// <param name="message">The logic row's catalog selection.</param>
    /// <param name="ct">Cancellation for this send.</param>
    public ValueTask SendAsync(ISession session, ShopCatalogPublished message, CancellationToken ct)
    {
        if (!_modern)
            return SendLegacyAsync(session, message, ct);
        var cards = message.Cards;
        if (message.CardLimit > 0 && message.CardLimit < cards.Count)
            cards = cards.Take(message.CardLimit).ToList();
        var (body, sent) = WriteBudgetedCatalog(tables, build, message.Token, message.MainCategory, cards,
            message.Categories, message.ContentRevisionToken, message.ByteBudget);
        if (sent < cards.Count)
            logger.LogWarning("Shop catalog on {Protocol} sends {Sent} of {Total} cards within {Budget} bytes; the remaining cards are not exposed.",
                protocol, sent, cards.Count, message.ByteBudget);
        return session.SendAsync(table.S2C("gspRepEnterShop"), body, ct);
    }

    /// <summary>Encodes and sends the cash-shop catalog for one session.</summary>
    /// <param name="session">The destination session.</param>
    /// <param name="message">The on-sale cash stock sets.</param>
    /// <param name="ct">Cancellation for this send.</param>
    public ValueTask SendAsync(ISession session, ShopCashCatalogPublished message, CancellationToken ct)
    {
        if (message.Cards.Count == 0)
            throw new InvalidOperationException("S2C 801 cannot be written with zero cash-shop cards.");
        return session.SendAsync(table.S2C("gspRepEnterTCShop"), WriteCashShopReply(tables, build, message.Token, message.Cards), ct);
    }

    /// <summary>Encodes and sends one card-info reply.</summary>
    /// <param name="session">The destination session.</param>
    /// <param name="message">The result and card.</param>
    /// <param name="ct">Cancellation for this send.</param>
    public ValueTask SendAsync(ISession session, ShopCardInfoReplied message, CancellationToken ct) =>
        session.SendAsync(table.S2C("gspRepShopItemCardInfo"), WriteCardInfoReply(tables, build, message), ct);

    /// <summary>Encodes and sends one coupon reply.</summary>
    /// <param name="session">The destination session.</param>
    /// <param name="message">The result word.</param>
    /// <param name="ct">Cancellation for this send.</param>
    public ValueTask SendAsync(ISession session, ShopCouponUseReplied message, CancellationToken ct) =>
        session.SendAsync(table.S2C("gspRepRealCouponUse"), PacketCodec.Write(tables, build, "RealCouponUseReply",
            new Dictionary<string, object?>
            {
                ["result"] = message.Result,
                ["unknown_8"] = 0,
                ["unknown_12"] = 0,
            }), ct);

    /// <summary>
    /// Encodes and sends the coupon dialog's cash probe answer: the cash word always, then the six-word money
    /// snapshot when the logic row supplied a wallet, because the probe's reply only writes the cash dword while
    /// the buy pre-check reads the cached Lucci dword of the money packet.
    /// </summary>
    /// <param name="session">The destination session.</param>
    /// <param name="message">The cash balance and optional wallet snapshot.</param>
    /// <param name="ct">Cancellation for this send.</param>
    public async ValueTask SendAsync(ISession session, ShopCashCheckReplied message, CancellationToken ct)
    {
        await session.SendAsync(table.S2C("gspRepCheckCash"), PacketCodec.Write(tables, build, "CheckCashReply",
            new Dictionary<string, object?>
            {
                ["unknown_0"] = 0,
                ["cash"] = message.Cash,
                ["unknown_8"] = 0,
            }), ct).ConfigureAwait(false);
        if (message.MoneyStat is { } money)
            await session.SendAsync(table.S2C("gspUserMoneyStat"), MoneyStat(tables, build, money), ct).ConfigureAwait(false);
    }

    /// <summary>Encodes and sends the modern purchase reply.</summary>
    /// <param name="session">The destination session.</param>
    /// <param name="message">The echoed key and outcome.</param>
    /// <param name="ct">Cancellation for this send.</param>
    public ValueTask SendAsync(ISession session, ShopPurchaseReplied message, CancellationToken ct) =>
        session.SendAsync(table.S2C("gspRepShopBuy"), WriteModernReply(tables, build, message), ct);

    /// <summary>Encodes and sends one equip-hold flush as the compressed S2C 803 burst.</summary>
    /// <param name="session">The destination session.</param>
    /// <param name="message">The held container-2 records.</param>
    /// <param name="ct">Cancellation for this send.</param>
    public ValueTask SendAsync(ISession session, EquippedArticlesFlushed message, CancellationToken ct) =>
        session.SendAsync(table.S2C("gspInventoryUpdateInventorySlots"),
            ShopCatalogWire.EquipHoldEnvelope(message.Articles), ct);

    /// <summary>
    /// Writes <c>gspRepEnterShop</c> (S2C 800) through the tables: result 1, has-data, token, cards, the i64
    /// between the vectors, categories, the counted vector before the trailing main category. When the token is
    /// the content revision, the body is written once with revision 0 and hashed.
    /// </summary>
    /// <param name="tables">The packet tables.</param>
    /// <param name="build">The client build.</param>
    /// <param name="token">The token to echo, or 0 when the revision derives it.</param>
    /// <param name="mainCategory">The main tab the reply selects.</param>
    /// <param name="cards">The on-sale cards.</param>
    /// <param name="categories">The category tree.</param>
    /// <param name="revisionToken">Whether the token is the content revision of the body itself.</param>
    /// <returns>The body.</returns>
    internal static byte[] WriteEnterShopReply(PacketTables tables, string build, long token, int mainCategory,
        IReadOnlyList<ShopCardRow> cards, IReadOnlyList<ShopCategoryRow> categories, bool revisionToken)
    {
        var body = WriteEnterShopValues(tables, build, token, mainCategory, cards, categories);
        return revisionToken ? WriteEnterShopValues(tables, build, CatalogRevision(body), mainCategory, cards, categories) : body;
    }

    /// <summary>Fits a deterministic card prefix to the configured frame budget using the build's real encoded records.</summary>
    internal static (byte[] Body, int Sent) WriteBudgetedCatalog(PacketTables tables, string build, long token,
        int mainCategory, IReadOnlyList<ShopCardRow> cards, IReadOnlyList<ShopCategoryRow> categories,
        bool revisionToken, int byteBudget)
    {
        if (byteBudget < 0)
            throw new ArgumentOutOfRangeException(nameof(byteBudget));
        var selected = cards;
        if (byteBudget > 0)
        {
            var bytes = WriteEnterShopValues(tables, build, 0, mainCategory, [], categories).Length;
            if (bytes > byteBudget)
                throw new InvalidOperationException($"Shop category tree needs {bytes} bytes, exceeding the {byteBudget}-byte payload budget.");
            var count = 0;
            foreach (var card in cards)
            {
                var size = WriteUnbounded(tables, build, "ShopItemCardInfo", CardValues(card)).Length;
                if (size > byteBudget - bytes)
                    break;
                bytes += size;
                count++;
            }
            selected = cards.Take(count).ToArray();
        }
        return (WriteEnterShopReply(tables, build, token, mainCategory, selected, categories, revisionToken), selected.Count);
    }

    private static byte[] WriteEnterShopValues(PacketTables tables, string build, long token, int mainCategory,
        IReadOnlyList<ShopCardRow> cards, IReadOnlyList<ShopCategoryRow> categories) =>
        // The dialog layout does not imply a frame width: the caller applies the selected payload budget
        // before publishing, while the writer also supports the long-frame client's full catalog.
        WriteUnbounded(tables, build, "EnterShopReply", new Dictionary<string, object?>
        {
            ["result"] = 1,
            ["has_data"] = true,
            ["token"] = token,
            ["cards"] = cards.Select(CardValues).ToList(),
            ["unknown_32"] = 0L,
            ["categories"] = categories.Select(CategoryValues).ToList(),
            ["unknown_56"] = new List<object?>(),
            ["main_category"] = mainCategory,
        });

    /// <summary>Writes one structure without the native 16-bit body bound, for the modern family's long frames.</summary>
    /// <param name="tables">The packet tables.</param>
    /// <param name="build">The client build.</param>
    /// <param name="structure">The structure name.</param>
    /// <param name="values">The field values.</param>
    /// <returns>The body.</returns>
    private static byte[] WriteUnbounded(PacketTables tables, string build, string structure,
        IReadOnlyDictionary<string, object?> values)
    {
        var w = PacketWriter.Create();
        PacketCodec.Write(w, tables, build, structure, values);
        return ShopCatalogWire.Written(w);
    }

    /// <summary>Writes <c>gspRepEnterTCShop</c> (S2C 801): unknown word, has-data, token, card vector.</summary>
    /// <param name="tables">The packet tables.</param>
    /// <param name="build">The client build.</param>
    /// <param name="token">The echoed token.</param>
    /// <param name="cards">The on-sale cash cards.</param>
    /// <returns>The body.</returns>
    internal static byte[] WriteCashShopReply(PacketTables tables, string build, long token, IReadOnlyList<ShopCardRow> cards) =>
        WriteUnbounded(tables, build, "CashShopReply", new Dictionary<string, object?>
        {
            ["unknown_0"] = 0,
            ["has_data"] = true,
            ["token"] = token,
            ["cards"] = cards.Select(CardValues).ToList(),
        });

    /// <summary>
    /// Writes <c>gspRepShopItemCardInfo</c> (S2C 817): the result and the whole card, or the default-constructed
    /// card the failure path still carries.
    /// </summary>
    /// <param name="tables">The packet tables.</param>
    /// <param name="build">The client build.</param>
    /// <param name="message">The result and card.</param>
    /// <returns>The body.</returns>
    internal static byte[] WriteCardInfoReply(PacketTables tables, string build, ShopCardInfoReplied message) =>
        PacketCodec.Write(tables, build, "ShopItemCardInfoReply", new Dictionary<string, object?>
        {
            ["result"] = message.Result,
            ["card"] = message.Card is { } card ? CardValues(card) : EmptyCardValues(),
        });

    /// <summary>
    /// Writes the modern <c>gspRepShopBuy</c> (S2C 816): key, result, the reply text, the dialog-variant byte and
    /// the three trailing words. Result 1 is the only success value in kr-1256 <c>sub_9B6730</c>.
    /// </summary>
    /// <param name="tables">The packet tables.</param>
    /// <param name="build">The client build.</param>
    /// <param name="message">The echoed key and outcome.</param>
    /// <returns>The body.</returns>
    internal static byte[] WriteModernReply(PacketTables tables, string build, ShopPurchaseReplied message) =>
        PacketCodec.Write(tables, build, "ShopPurchaseReply", new Dictionary<string, object?>
        {
            ["key"] = message.Key,
            ["result"] = message.Succeeded ? 1 : 0,
            ["unknown_8"] = message.ReplyText,
            ["unknown_32"] = (byte)0,
            ["unknown_36"] = 0,
            ["unknown_40"] = 0,
            ["unknown_44"] = 0,
        });

    /// <summary>
    /// Encodes and sends the modern balance refresh's wallet answer: S2C <c>gspUserMoneyStat</c> carrying the
    /// six-word money snapshot, whose lucci and cash words land in the user-state counters (kr-1256
    /// sub_9D2FF0 case 106) that enable the waiting purchase dialog (kr-1256 sub_648810 -> kr-1256
    /// sub_E1D5C0).
    /// </summary>
    /// <param name="session">The destination session.</param>
    /// <param name="message">The player and its stored wallet.</param>
    /// <param name="ct">Cancellation for this send.</param>
    public ValueTask SendAsync(ISession session, ShopBalanceUpdated message, CancellationToken ct)
    {
        if (message.Wallet.UserNum != message.Player.UserNum)
            throw new InvalidOperationException("Balance publication contains another player's wallet.");
        return session.SendAsync(table.S2C("gspUserMoneyStat"), MoneyStat(tables, build, message.Wallet), ct);
    }

    /// <summary>
    /// Writes the six-word money snapshot of <c>gspUserMoneyStat</c> (S2C 106) through the tables
    /// (<c>UserMoneyStat</c> in <c>packets/login.yaml</c>): four UI switch words the server sends as 0, then
    /// the stored lucci and cash, the words kr-1256 sub_9D2FF0 case 106 copies to user info +76 and +80. The
    /// login burst's <see cref="LoginBurstWire"/> writer produces the same body for the same balances.
    /// </summary>
    /// <param name="tables">The row's packet tables.</param>
    /// <param name="build">The client build whose table is written.</param>
    /// <param name="money">The stored wallet snapshot.</param>
    /// <returns>The 24-byte body.</returns>
    internal static byte[] MoneyStat(PacketTables tables, string build, PlayerWallet money) =>
        LoginBurstWire.WriteUserMoneyStat(tables, build, money.Lucci, money.Cash);

    private async ValueTask SendLegacyAsync(ISession session, ShopCatalogPublished message, CancellationToken ct)
    {
        var layout = ShopCatalogWire.LayoutOf(build, tables.ValuesOf(build));
        var categories = layout.Tree switch
        {
            LegacyCategoryTree.Ordered => message.Categories,
            LegacyCategoryTree.Landing => ShopCatalogWire.EnsureEnterShopLandingMain(message.Categories, message.ShopVisibleMains),
            _ => ShopCatalogWire.MergeMainClickDefaults(
                ShopCatalogWire.EnsureEnterShopLandingMain(message.Categories, message.ShopVisibleMains),
                message.ShopVisibleSubcategories, message.MainDefaultSubcategoryIds),
        };
        // The tree alone costs this many bytes; the card vector gets whatever budget remains.
        var fixedBytes = ShopCatalogWire.RepEnterShopLegacy(layout, 0, [], categories).Length;
        if (fixedBytes > message.ByteBudget)
            throw new InvalidOperationException(
                $"Legacy shop category tree needs {fixedBytes} bytes, exceeding the {message.ByteBudget}-byte payload budget.");
        var (cards, dropped) = ShopCatalogWire.SelectBoundedCards(message.Cards, message.ByteBudget - fixedBytes,
            layout.WideStockType, maxCards: message.CardCap);
        if (dropped > 0)
            logger.LogWarning(
                "Legacy shop catalog sends {Sent} of {Total} cards within {Budget} bytes; {Dropped} cards are not exposed.",
                cards.Count, message.Cards.Count, message.ByteBudget, dropped);
        var main = layout.Trailer == LegacyCatalogTrailer.IdsMain
            ? ShopCatalogWire.ResolveEnterShopMainCategory(message.MainCategory, cards, message.Categories)
            : ShopCatalogWire.LegacyEnterShopMainCategory;
        var body = ShopCatalogWire.RepEnterShopLegacy(layout, message.Token, cards, categories, main);
        logger.LogInformation(
            "Legacy shop catalog: {Sent} sent, {Dropped} omitted; payload {Bytes}/{Budget} bytes.",
            cards.Count, dropped, body.Length, message.ByteBudget);
        await session.SendAsync(table.S2C("gspRepEnterShop"), body, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Maps one catalog card to its table field values: the card's purchase terms, which for a StockSet card are
    /// the set's own identity and prices laid over its first stock, never the raw Stocks list.
    /// </summary>
    /// <param name="card">A catalog row.</param>
    /// <returns>The field values of <c>ShopItemCardInfo</c>.</returns>
    internal static Dictionary<string, object?> CardValues(ShopCardRow card)
    {
        var terms = card.PurchaseTerms;
        return new Dictionary<string, object?>
        {
            ["unknown_4"] = card.CardId,
            ["unknown_8"] = card.CardKind,
            ["unknown_12"] = card.Name,
            ["unknown_36"] = card.DisplayOrder,
            ["unknown_40"] = card.MainCategoryId,
            ["unknown_44"] = card.SubCategoryId,
            ["unknown_48"] = card.IsOnSale,
            ["unknown_56"] = 0L,
            ["unknown_64"] = 0L,
            ["stock"] = terms.Count == 0 ? EmptyStockValues() : StockValues(terms[0]),
            ["extra_stocks"] = terms.Select(StockValues).ToList(),
        };
    }

    /// <summary>Maps one stock row to its table field values.</summary>
    /// <param name="stock">A stock row.</param>
    /// <returns>The field values of <c>Stock</c>.</returns>
    internal static Dictionary<string, object?> StockValues(ShopStockRow stock) => new()
    {
        ["unknown_4"] = stock.StockId,
        ["unknown_8"] = stock.ItemId,
        ["unknown_12"] = stock.IsOnSale,
        ["unknown_14"] = stock.Type,
        ["unknown_16"] = stock.Duration,
        ["unknown_20"] = stock.ShowPrice,
        ["unknown_24"] = stock.SellPrice,
    };

    /// <summary>
    /// The default-constructed stock: every field zero except the <c>u16</c> at +14, whose constructor default
    /// is 1 (kr-1256 <c>sub_11400D0</c>), which is why an empty card is 74 bytes rather than 72.
    /// </summary>
    /// <returns>The field values of the default <c>Stock</c>.</returns>
    internal static Dictionary<string, object?> EmptyStockValues() => new() { ["unknown_14"] = (ushort)1 };

    internal static Dictionary<string, object?> EmptyCardValues() => new()
    {
        ["stock"] = EmptyStockValues(),
        ["extra_stocks"] = new List<object?>(),
    };

    /// <summary>Maps one category with its subcategories to its table field values.</summary>
    /// <param name="category">A category row.</param>
    /// <returns>The field values of <c>ShopMainCategory</c>.</returns>
    internal static Dictionary<string, object?> CategoryValues(ShopCategoryRow category) => new()
    {
        ["unknown_4"] = category.Id,
        ["unknown_8"] = category.Name,
        ["unknown_32"] = category.DisplayOrder,
        ["subcategories"] = category.Subcategories.Select(SubcategoryValues).ToList(),
    };

    private static Dictionary<string, object?> SubcategoryValues(ShopSubcategoryRow sub) => new()
    {
        ["unknown_4"] = sub.Id,
        ["unknown_8"] = sub.Name,
        ["unknown_32"] = sub.DisplayOrder,
        ["page_type"] = sub.PageType,
    };
}
