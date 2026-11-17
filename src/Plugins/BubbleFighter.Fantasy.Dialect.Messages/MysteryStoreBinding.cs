using System.Globalization;
using System.Text.Json;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.MysteryStore;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Messages;

/// <summary>
/// The Mysterystore request and reply of <c>packets/mystery-store.yaml</c> for a build with the
/// <c>mystery_store</c> capability. The signed reply statuses are the build's <c>mystery_store_statuses</c> in
/// <c>packets/builds.yaml</c>; every body is written by its field names.
/// </summary>
internal sealed class MysteryStoreBinding(string protocol, IOpcodeTable opcodes, PacketTables tables, string build)
    : IInboundMessageBinding<MysteryStoreRequested>, IMessageSender<MysteryStoreReplied>
{
    // The client draws at most this many cards: kr-1256 sub_E32980 binds mrkItemSlot 0..5 and kr-1256 sub_E33390
    // builds six cards, and the purchased mask addresses them by row order.
    private const int MaxCards = 6;
    private readonly IReadOnlyDictionary<MysteryStoreStatus, sbyte> _statuses = ReadStatuses(tables, build);

    /// <summary>Gets the game connection this binding serves.</summary>
    public string Protocol => protocol;

    /// <summary>Checks the layouts and status codes, then registers the request route and the reply sender.</summary>
    internal void Bind(IPluginContext context, IMessageRouter messages)
    {
        RequireLayouts(tables, build);
        _ = opcodes.S2C("gspRepUserMysteryStore");
        var router = context.Services.Get<IPacketRouter>();
        context.Effect(router.Register(protocol, opcodes.C2S("gcpReqUserMysteryStore"), async (session, packet, ct) =>
        {
            var request = Read(packet.Payload.Span);
            if (!await messages.DispatchAsync(protocol, session, request, ct).ConfigureAwait(false))
                throw new InvalidOperationException("gcpReqUserMysteryStore has no typed handler.");
        }));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<MysteryStoreRequested>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<MysteryStoreReplied>>(this));
    }

    /// <summary>Requires every structure this binding reads or writes, with the field types it uses.</summary>
    internal static void RequireLayouts(PacketTables tables, string build)
    {
        PacketLayoutContract.RequireFields(tables, build, "UserMysteryStoreRequest", "Mysterystore open",
            ("type", "u8"), ("token", "u32"));
        PacketLayoutContract.RequireFields(tables, build, "UserMysteryStoreRequestType2", "Mysterystore query",
            ("type", "u8"), ("token", "u32"), ("remaining_seconds", "u16"));
        PacketLayoutContract.RequireFields(tables, build, "UserMysteryStoreRequestType3", "Mysterystore refresh",
            ("type", "u8"), ("token", "u32"), ("refresh_cost", "u16"));
        PacketLayoutContract.RequireFields(tables, build, "UserMysteryStoreRequestType4", "Mysterystore purchase",
            ("type", "u8"), ("token", "u32"), ("stock_or_stock_set_id", "u32"), ("coin_price", "u16"));
        PacketLayoutContract.RequireFields(tables, build, "UserMysteryStoreReply", "Mysterystore reply",
            ("action", "u8"), ("status", "u8"), ("fields", "u8"));
        PacketLayoutContract.RequireFields(tables, build, "MysteryStorePacketConfig", "Mysterystore configuration",
            ("token", "u32"), ("coin_item_id", "u32"), ("refresh_cost", "u16"), ("group_stock_variants", "bool"));
        PacketLayoutContract.RequireFields(tables, build, "MysteryStorePacketUserData", "Mysterystore countdown",
            ("remaining_seconds", "u16"));
        PacketLayoutContract.RequireFields(tables, build, "MysteryStorePacketStock", "Mysterystore card",
            ("unknown_4", "u32"), ("unknown_8", "wstr"), ("stock_or_stock_set_id", "u32"), ("coin_price", "u16"));
        PacketLayoutContract.RequireFields(tables, build, "UserMysteryStorePurchaseTail", "Mysterystore receipt",
            ("reward_stock_or_stock_set_id", "u32"));
    }

    /// <summary>Reads the build's signed status of every reply status; a missing or out-of-range one fails the mount.</summary>
    internal static IReadOnlyDictionary<MysteryStoreStatus, sbyte> ReadStatuses(PacketTables tables, string build)
    {
        if (tables.ValuesOf(build).GetValueOrDefault("mystery_store_statuses") is not IReadOnlyDictionary<string, object?> values)
            throw new ConfigException($"Build '{build}' needs mystery_store_statuses for its Mysterystore replies.");
        var codes = new Dictionary<MysteryStoreStatus, sbyte>();
        foreach (var status in Enum.GetValues<MysteryStoreStatus>())
        {
            var name = JsonNamingPolicy.SnakeCaseLower.ConvertName(status.ToString());
            if (!values.TryGetValue(name, out var raw)
                || !sbyte.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture, out var code))
                throw new ConfigException($"Build '{build}' mystery_store_statuses needs a signed byte for '{name}'.");
            codes.Add(status, code);
        }
        if (codes[MysteryStoreStatus.Done] != 0 || codes.Where(pair => pair.Key != MysteryStoreStatus.Done).Any(pair => pair.Value == 0))
            throw new ConfigException($"Build '{build}' mystery_store_statuses must use 0 for done alone.");
        return codes;
    }

    /// <summary>
    /// Reads a request: the native type byte selects its tail (kr-1256 sub_114E2E0 writes 5, 7, 7 or 11 bytes for types
    /// 1 to 4), and the body must end exactly there.
    /// </summary>
    internal MysteryStoreRequested Read(ReadOnlySpan<byte> body)
    {
        if (body.IsEmpty)
            throw new PacketFormatException("Mysterystore request has no type byte.", 0);
        // The type byte is a protocol constant shared by the audited builds: 1 open (kr-1256 sub_E5F070), 2 query
        // (kr-1256 sub_E64FC0), 3 refresh (kr-1256 sub_E33FD0), 4 purchase (kr-1256 sub_E6CE80).
        switch (body[0])
        {
            case 1:
                return new(MysteryStoreAction.Open, Word(PacketCodec.ReadScalars(tables, build, "UserMysteryStoreRequest", body), "token"), 0, 0, 0);
            case 2:
            {
                var values = PacketCodec.ReadScalars(tables, build, "UserMysteryStoreRequestType2", body);
                return new(MysteryStoreAction.Query, Word(values, "token"), Short(values, "remaining_seconds"), 0, 0);
            }
            case 3:
            {
                var values = PacketCodec.ReadScalars(tables, build, "UserMysteryStoreRequestType3", body);
                return new(MysteryStoreAction.Refresh, Word(values, "token"), 0, Short(values, "refresh_cost"), 0);
            }
            case 4:
            {
                var values = PacketCodec.ReadScalars(tables, build, "UserMysteryStoreRequestType4", body);
                var key = Word(values, "stock_or_stock_set_id");
                if (key is 0 or > int.MaxValue)
                    throw new PacketFormatException("Mysterystore purchase names no Stock or StockSet.", 5);
                return new(MysteryStoreAction.Purchase, Word(values, "token"), 0, Short(values, "coin_price"), (int)key);
            }
            default:
                throw new PacketFormatException($"Mysterystore request type {body[0]} is not one the client sends.", 0);
        }
    }

    /// <summary>
    /// Writes a reply: the common prefix with one section per set bit, then the receipt word, which the reader takes
    /// for a successful purchase whatever the bits are (kr-1256 sub_114E4E0).
    /// </summary>
    internal byte[] Write(MysteryStoreReplied message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var hasReceipt = message.Action == MysteryStoreAction.Purchase && message.Status == MysteryStoreStatus.Done;
        if (hasReceipt != (message.ReceiptNativeKey > 0))
            throw new ArgumentException("Only a successful purchase carries a positive receipt key.", nameof(message));
        if (message.PurchasedMask is not null && message.Offers is null)
            throw new ArgumentException("Purchased bits require the ordered offers they address.", nameof(message));
        if (message.Offers is { Count: > MaxCards })
            throw new ArgumentException($"The client shows at most {MaxCards} Mysterystore cards.", nameof(message));
        var fields = (message.Config is null ? 0 : 1) | (message.RemainingSeconds is null ? 0 : 2)
            | (message.Offers is null ? 0 : 4) | (message.PurchasedMask is null ? 0 : 8);
        var values = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["action"] = ActionByte(message.Action),
            ["status"] = unchecked((byte)_statuses[message.Status]),
            ["fields"] = (byte)fields,
            ["config"] = message.Config is not { } config ? null : new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["token"] = config.Token, ["coin_item_id"] = checked((uint)config.CoinItemId),
                ["refresh_cost"] = config.RefreshCoinCost, ["group_stock_variants"] = config.GroupStockVariants,
            },
            ["user_data"] = message.RemainingSeconds is not { } seconds ? null
                : new Dictionary<string, object?>(StringComparer.Ordinal) { ["remaining_seconds"] = seconds },
            ["offers"] = message.Offers is not { } offers ? null : new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["stocks"] = offers.Select(offer => (object?)new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["unknown_4"] = offer.Unknown4, ["unknown_8"] = offer.UnknownName,
                    ["stock_or_stock_set_id"] = checked((uint)offer.NativeKey), ["coin_price"] = offer.CoinPrice,
                }).ToList(),
            },
            ["purchased_mask"] = message.PurchasedMask,
        };
        var body = PacketCodec.Write(tables, build, "UserMysteryStoreReply", values);
        if (message.ReceiptNativeKey == 0)
            return body;
        var tail = PacketCodec.Write(tables, build, "UserMysteryStorePurchaseTail", new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["reward_stock_or_stock_set_id"] = checked((uint)message.ReceiptNativeKey),
        });
        return [.. body, .. tail];
    }

    /// <inheritdoc />
    public ValueTask SendAsync(ISession session, MysteryStoreReplied message, CancellationToken ct) =>
        session.SendAsync(opcodes.S2C("gspRepUserMysteryStore"), Write(message), ct);

    private static byte ActionByte(MysteryStoreAction action) => action switch
    {
        MysteryStoreAction.Open => 1,
        MysteryStoreAction.Query => 2,
        MysteryStoreAction.Refresh => 3,
        MysteryStoreAction.Purchase => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    private static uint Word(IReadOnlyDictionary<string, object> values, string name) => values[name] switch
    {
        int word => unchecked((uint)word),
        var other => throw new PacketFormatException($"Mysterystore field {name} is {other.GetType().Name}, not a word.", 0),
    };

    private static ushort Short(IReadOnlyDictionary<string, object> values, string name) => values[name] switch
    {
        ushort half => half,
        var other => throw new PacketFormatException($"Mysterystore field {name} is {other.GetType().Name}, not a short.", 0),
    };
}
