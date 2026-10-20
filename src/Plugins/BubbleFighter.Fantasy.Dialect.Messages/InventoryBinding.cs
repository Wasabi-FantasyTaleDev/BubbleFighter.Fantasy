using System.Linq;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Messages;

/// <summary>
/// Publishes a build's bag through the packets its opcode table names and in the order its
/// <c>inventory</c> values state (<c>packets/builds.yaml</c>): the whole bag, the slot containers, the
/// empty article table and the empty cash inventory. Legacy builds rank the stored rows by item id and
/// carry connection-relative day counts; the modern family mirrors the rows and wraps the bodies in the
/// compressed envelope. Every build resolves through the same code; only the data differs.
/// </summary>
public sealed class InventoryBinding : IMessageSender<InventoryReplaced>, IMessageSender<InventorySlotsUpdated>,
    IMessageSender<InventoryPurchaseApplied>
{
    private readonly string _protocol;
    private readonly InventoryValues _values;
    private readonly InventoryWire _wire;
    private readonly InventoryBaselineScope? _baselines;
    private readonly ushort? _bagOpcode;
    private readonly ushort? _slotsOpcode;
    private readonly ushort? _articleOpcode;
    private readonly ushort? _cashOpcode;

    /// <summary>
    /// Creates the publication for a build whose bag rows are mirrored, such as the modern family; a
    /// ranked build needs the row's shared time origin and is created through the plugin row.
    /// </summary>
    /// <param name="protocol">The game connection protocol.</param>
    /// <param name="opcodes">The build's opcode table (game connection).</param>
    /// <param name="tables">The packet tables.</param>
    /// <param name="build">The build whose layouts and values are read.</param>
    /// <exception cref="ConfigException">
    /// Thrown when the build ranks its rows without a time origin, or a stated publication lacks its
    /// packet or structure.
    /// </exception>
    public InventoryBinding(string protocol, IOpcodeTable opcodes, PacketTables tables, string build)
        : this(protocol, opcodes, tables, build, baselines: null)
    {
    }

    /// <summary>Creates the publication over the row's shared inventory time origin.</summary>
    /// <param name="protocol">The game connection protocol.</param>
    /// <param name="opcodes">The build's opcode table (game connection).</param>
    /// <param name="tables">The packet tables.</param>
    /// <param name="build">The build whose layouts and values are read.</param>
    /// <param name="baselines">The row's per-connection inventory time origins; required for ranked rows.</param>
    internal InventoryBinding(string protocol, IOpcodeTable opcodes, PacketTables tables, string build,
        InventoryBaselineScope? baselines)
    {
        ArgumentException.ThrowIfNullOrEmpty(protocol);
        ArgumentNullException.ThrowIfNull(opcodes);
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentException.ThrowIfNullOrEmpty(build);
        _protocol = protocol;
        _values = InventoryValues.Of(tables, build);
        if (_values.Rows == InventoryValues.Ranked && baselines is null)
            throw new ConfigException(
                $"Build '{build}' ranks inventory rows and needs the row's inventory time origin; publish it through the dialect.messages row.");
        _baselines = baselines;
        _wire = new InventoryWire(tables, build);
        _bagOpcode = _values.Uses("bag") ? opcodes.S2C("gspInventoryUpdateAllInventorySlot") : null;
        _slotsOpcode = _values.Uses("slots") || _values.Uses("changed") || _values.Uses("equipped")
            ? opcodes.S2C("gspInventoryUpdateInventorySlots")
            : null;
        _articleOpcode = _values.Uses("articles") ? opcodes.S2C("gspUpdateAllArticleSlot") : null;
        _cashOpcode = _values.Uses("cash") ? opcodes.S2C("gspCashInvenUpdateAllInventorySlot") : null;
        // Fail at activation instead of on the first publication: every stated entry must have its
        // structure in the tables for this build.
        if (_bagOpcode is not null)
            _ = tables.Resolve(build, "InventoryUpdate");
        if (_slotsOpcode is not null)
            _ = tables.Resolve(build, "InventorySlots");
        if (_articleOpcode is not null)
            _ = tables.Resolve(build, "ArticleSlots");
        if (_cashOpcode is not null)
            _ = tables.Resolve(build, "CashInventoryUpdate");
    }

    /// <inheritdoc />
    public string Protocol => _protocol;

    /// <summary>
    /// Restates the whole bag for login and the shop state. Ranked builds rank the rows, drop empty
    /// ones and re-anchor the connection's inventory time origin (kr-812 sub_978920 reads day counts
    /// against it); mirrored builds publish the rows as stored.
    /// </summary>
    /// <param name="session">The session receiving the sequence.</param>
    /// <param name="message">The stored and equipped bags.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The completed sequence.</returns>
    public ValueTask SendAsync(ISession session, InventoryReplaced message, CancellationToken ct) =>
        PublishAsync(session, _values.Replaced, message.CharacterBag, message.EquippedBag, changed: null,
            filterBag: true, resetBaseline: true, ct);

    /// <summary>Publishes the post-purchase bag.</summary>
    /// <param name="session">The destination session.</param>
    /// <param name="message">The stored bag after the purchase.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The completed sequence.</returns>
    public ValueTask SendAsync(ISession session, InventoryPurchaseApplied message, CancellationToken ct) =>
        PublishAsync(session, _values.Purchase, message.CharacterBag, [], changed: null,
            filterBag: false, resetBaseline: false, ct);

    /// <summary>Publishes changed slots against the connection's standing publication.</summary>
    /// <param name="session">The destination session.</param>
    /// <param name="message">The changed slots.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The completed sequence.</returns>
    public ValueTask SendAsync(ISession session, InventorySlotsUpdated message, CancellationToken ct) =>
        PublishAsync(session, _values.Changed, [], [], message.Slots,
            filterBag: false, resetBaseline: false, ct);

    /// <summary>Publishes the login burst's whole bag and re-anchors the connection's time origin.</summary>
    /// <param name="session">The session receiving the login sequence.</param>
    /// <param name="characterBag">The seeded or stored character bag.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The publication.</returns>
    internal ValueTask SendLoginAsync(ISession session, IReadOnlyList<InventorySlotRecord> characterBag, CancellationToken ct) =>
        PublishAsync(session, ["bag"], characterBag, [], changed: null, filterBag: true, resetBaseline: true, ct);

    private async ValueTask PublishAsync(ISession session, IReadOnlyList<string> steps,
        IReadOnlyList<InventorySlotRecord> character, IReadOnlyList<InventorySlotRecord> equipped,
        IReadOnlyList<InventorySlotRecord>? changed, bool filterBag, bool resetBaseline, CancellationToken ct)
    {
        var baseline = _values.Rows == InventoryValues.Ranked
            ? resetBaseline ? _baselines!.Reset(session.Connection.Id) : _baselines!.Require(session.Connection.Id)
            : (DateTimeOffset?)null;
        foreach (var step in steps)
        {
            var (opcode, body) = step switch
            {
                "bag" => (_bagOpcode!.Value, BagBody(character, baseline, filterBag)),
                "slots" => (_slotsOpcode!.Value, SlotsBody(character, baseline, filter: false)),
                "changed" => (_slotsOpcode!.Value, SlotsBody(changed ?? [], baseline, filter: false)),
                "equipped" => (_slotsOpcode!.Value, SlotsBody(equipped, baseline, filter: false)),
                "articles" => (_articleOpcode!.Value, _wire.RepArticleSlots()),
                "cash" => (_cashOpcode!.Value, _wire.RepCashInventory()),
                _ => throw new InvalidOperationException($"Unknown inventory publication entry '{step}'."),
            };
            // The cash inventory is plain in every build read (kr-1256 sub_10D8150 takes the count
            // directly); the bag, slots and article bodies take the stated wrapper.
            var payload = step != "cash" && _values.Envelope == InventoryValues.Zlib ? ZlibEnvelope.Wrap(body) : body;
            await session.SendAsync(opcode, payload, ct).ConfigureAwait(false);
        }
    }

    private byte[] BagBody(IReadOnlyList<InventorySlotRecord> rows, DateTimeOffset? baseline, bool filter) =>
        _values.Rows == InventoryValues.Mirror
            ? _wire.RepInventoryAllValues([.. rows.Select(ModernSlot)])
            : _wire.RepInventoryAll(RankedRows(rows, baseline, filter));

    private byte[] SlotsBody(IReadOnlyList<InventorySlotRecord> rows, DateTimeOffset? baseline, bool filter) =>
        _values.Rows == InventoryValues.Mirror
            ? _wire.RepInventorySlotsValues([.. rows.Select(ModernSlot)])
            : _wire.RepInventorySlots(RankedRows(rows, baseline, filter));

    // One modern slot from the stored row: item id, count, the eight bytes at record +8 and the packed
    // flag byte (kr-1256 sub_10D8550 reads them in that order). The client's use of +8 is not read, so
    // that field is unknown_8; the text at +36 and the attribute vector are always empty and come from
    // the table defaults.
    private static Dictionary<string, object?> ModernSlot(InventorySlotRecord slot) =>
        new(StringComparer.Ordinal)
        {
            ["item_id"] = slot.ItemId,
            ["quantity"] = slot.Quantity,
            ["unknown_8"] = slot.SlotKey,
            ["flags"] = slot.Flags,
        };

    private static IReadOnlyList<InventorySlot> RankedRows(IReadOnlyList<InventorySlotRecord> rows, DateTimeOffset? baseline, bool filter)
    {
        IEnumerable<InventorySlotRecord> selected = filter ? rows.Where(slot => slot.Quantity > 0) : rows;
        return [.. selected.OrderBy(slot => slot.ItemId).Select(slot => Map(slot, baseline!.Value))];
    }

    /// <summary>Maps one stored row to the legacy slot shape.</summary>
    /// <param name="slot">The stored row; unsupported flag bits and unknown lifetime kinds are refused.</param>
    /// <param name="baseline">The connection's inventory time origin.</param>
    /// <returns>The legacy slot.</returns>
    internal static InventorySlot Map(InventorySlotRecord slot, DateTimeOffset baseline)
    {
        if ((slot.Flags & ~InventorySlotRecord.WornFlagBit) != 0)
            throw new InvalidOperationException($"Item {slot.ItemId} has unsupported inventory flags.");
        var days = slot.Lifetime.Kind switch
        {
            InventoryLifetimeKind.ExpiresAt => InventoryTime.ExpiringDaysAtBaseline(slot.Lifetime, baseline),
            // kr-812 0x9784B0 recognizes >10000 days as permanent.
            InventoryLifetimeKind.Permanent => 2958101.0,
            // A life item has no time limit (kr-812 sub_6D5890 and the bag views show it as unlimited with a Life count);
            // its life travels in the slot's +8 count (quantity), which kr-812 sub_978350 and the login notice read for kind 3.
            InventoryLifetimeKind.Life => 2958101.0,
            // Unclassified rows (seeded bags, grants of the kr-1256 raw shop path) never expire on the server
            // (InventoryExpiry, the purchase planner), so the bag shows them without a time limit as well.
            InventoryLifetimeKind.Unspecified => 2958101.0,
            _ => throw new InvalidOperationException($"Item {slot.ItemId} has no established lifetime."),
        };
        // 0x8431C0 initializes the remaining booleans, string and nested vector empty.
        return new(slot.ItemId, slot.Quantity, days, (slot.Flags & InventorySlotRecord.WornFlagBit) != 0,
            false, false, false, false, string.Empty);
    }
}
