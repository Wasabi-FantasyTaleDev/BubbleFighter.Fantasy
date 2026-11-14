using System.Linq;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Messages;

/// <summary>
/// Publishes a build's bag through the packets its opcode table names and in the order its
/// <c>inventory</c> values state (<c>packets/builds.yaml</c>): the whole bag, the slot containers, the
/// owned Article table and the empty cash inventory. Legacy builds rank the stored rows by item id and
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
    private readonly IEventBus? _events;
    private readonly ushort? _bagOpcode;
    private readonly ushort? _slotsOpcode;
    private readonly ushort? _articleOpcode;
    private readonly ushort? _cashOpcode;

    /// <summary>
    /// Creates a mirrored-row publication without a shared clock. Timed rows require the plugin row's
    /// inventory time origin; timeless mirrored rows can use this constructor directly.
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
    /// <param name="baselines">The row's per-connection inventory time origins; required for ranked rows and modern display timers.</param>
    /// <param name="events">Resolves feature-owned modifiers and display timers for the inventory snapshot.</param>
    internal InventoryBinding(string protocol, IOpcodeTable opcodes, PacketTables tables, string build,
        InventoryBaselineScope? baselines, IEventBus? events = null)
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
        _events = events;
        _wire = new InventoryWire(tables, build);
        _bagOpcode = _values.Uses("bag") ? opcodes.S2C("gspInventoryUpdateAllInventorySlot") : null;
        _slotsOpcode = _values.Uses("slots") || _values.Uses("changed") || _values.Uses("equipped")
            ? opcodes.S2C("gspInventoryUpdateInventorySlots")
            : null;
        _articleOpcode = _values.Uses("articles") ? opcodes.S2C("gspUpdateAllArticleSlot") : null;
        _cashOpcode = _values.Uses("cash") ? opcodes.S2C("gspCashInvenUpdateAllInventorySlot") : null;
        if (_values.OrdinaryArticlesAdmitted && (!_wire.SupportsArticles || _articleOpcode is null))
            throw new ConfigException($"Build '{build}' admits ordinary Articles without a complete native Article publication.");
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
    /// against it); admitted mirrored builds project ordinary expiry against the fresh origin too.
    /// </summary>
    /// <param name="session">The session receiving the sequence.</param>
    /// <param name="message">The stored and equipped bags.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The completed sequence.</returns>
    public ValueTask SendAsync(ISession session, InventoryReplaced message, CancellationToken ct) =>
        PublishAsync(session, _values.Replaced, message.CharacterBag, message.EquippedBag, changed: null,
            filterBag: true, resetBaseline: true, message.IsCompleteSnapshot, loginBagOnly: false, ct);

    /// <summary>Publishes the post-purchase bag.</summary>
    /// <param name="session">The destination session.</param>
    /// <param name="message">The stored bag after the purchase.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The completed sequence.</returns>
    public ValueTask SendAsync(ISession session, InventoryPurchaseApplied message, CancellationToken ct) =>
        PublishAsync(session, _values.Purchase, message.CharacterBag, [], changed: null,
            filterBag: false, resetBaseline: false, isComplete: false, loginBagOnly: false, ct);

    /// <summary>Publishes changed slots against the connection's standing publication.</summary>
    /// <param name="session">The destination session.</param>
    /// <param name="message">The changed slots.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The completed sequence.</returns>
    public ValueTask SendAsync(ISession session, InventorySlotsUpdated message, CancellationToken ct) =>
        PublishAsync(session, _values.Changed, [], [], message.Slots,
            filterBag: false, resetBaseline: false, isComplete: false, loginBagOnly: false, ct);

    /// <summary>Publishes the login burst's whole bag and re-anchors the connection's time origin.</summary>
    /// <param name="session">The session receiving the login sequence.</param>
    /// <param name="characterBag">The seeded or stored character bag.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The publication.</returns>
    internal ValueTask SendLoginAsync(ISession session, IReadOnlyList<InventorySlotRecord> characterBag, CancellationToken ct) =>
        PublishAsync(session, ["bag"], characterBag, [], changed: null, filterBag: true, resetBaseline: true,
            isComplete: false, loginBagOnly: true, ct);

    private async ValueTask PublishAsync(ISession session, IReadOnlyList<string> steps,
        IReadOnlyList<InventorySlotRecord> character, IReadOnlyList<InventorySlotRecord> equipped,
        IReadOnlyList<InventorySlotRecord>? changed, bool filterBag, bool resetBaseline, bool isComplete,
        bool loginBagOnly, CancellationToken ct)
    {
        CheckPublicationBounds(steps, character, equipped, changed);
        ct.ThrowIfCancellationRequested();
        using var publication = _baselines is null ? null : await _baselines.EnterAsync(session.Connection, ct).ConfigureAwait(false);
        ct = publication?.CancellationToken ?? ct;
        var omitEmpty = isComplete;
        if (_events is not null)
        {
            var userNum = PublicationOwner(session, character.Concat(equipped).Concat(changed ?? []));
            var snapshot = await _events.WaterfallAsync<InventoryPublicationPreparing, InventoryReplaced?>(
                new(_protocol, userNum, character, equipped, changed, isComplete),
                static _ => ValueTask.FromResult<InventoryReplaced?>(null), ct).ConfigureAwait(false);
            if (snapshot is not null)
            {
                if (snapshot.CharacterBag.Concat(snapshot.EquippedBag).Any(row => row.UserNum != userNum))
                    throw new InvalidDataException("Prepared inventory belongs to another player.");
                character = snapshot.CharacterBag;
                equipped = snapshot.EquippedBag;
                changed = null;
                omitEmpty = true;
                isComplete = true;
                filterBag = true;
                resetBaseline = true;
                // kr-1256 sub_804FC0 replaces all Articles; cn-774 sub_BADC50 has no Article delta list.
                // A bag-only login step retains its existing installation point for the later Article table.
                if (!loginBagOnly) steps = _values.Replaced;
            }
        }
        CheckPublicationBounds(steps, character, equipped, changed);
        var supplied = character.Concat(equipped).Concat(changed ?? []).ToArray();
        if (!isComplete && !loginBagOnly && supplied.Any(row => PublishedArticleIdentity(row) is not null))
            throw new InvalidOperationException("Article publication requires a complete prepared inventory snapshot.");
        var presentations = new Dictionary<InventorySlotRecord, InventoryItemPresentation>();
        if (_events is not null)
        {
            foreach (var slot in supplied.Distinct())
                presentations.Add(slot,
                    await _events.WaterfallAsync<InventoryPresentationRequested, InventoryItemPresentation>(
                        new(_protocol, slot), _ => ValueTask.FromResult(InventoryItemPresentation.Empty), ct).ConfigureAwait(false));
        }
        var articles = ArticleValues(character, equipped, presentations);
        // kr-1256 sub_804220, cn-774 sub_9A4550 and tw-454 sub_C02AF0 reset the ordinary origin on every full bag.
        // Their delta consumers preserve it, so a purchase containing a full bag must select a fresh origin too.
        if (_values.OrdinaryExpiryAdmitted)
            resetBaseline = steps.Contains("bag", StringComparer.Ordinal);
        var baseline = publication is null ? (DateTimeOffset?)null
            : resetBaseline ? publication.PrepareReset()
            : _values.Rows == InventoryValues.Ranked ? publication.Require()
            : publication.Find();
        var frames = new List<(ushort Opcode, byte[] Payload)>(steps.Count);
        foreach (var step in steps)
        {
            var (opcode, body) = step switch
            {
                "bag" => (_bagOpcode!.Value, Body(character, baseline, filterBag, presentations, true, omitEmpty)),
                "slots" => (_slotsOpcode!.Value, Body(character, baseline, false, presentations, false, omitEmpty)),
                "changed" => (_slotsOpcode!.Value, Body(changed ?? [], baseline, false, presentations, false, omitEmpty)),
                "equipped" => (_slotsOpcode!.Value, Body(equipped, baseline, false, presentations, false, omitEmpty)),
                "articles" => (_articleOpcode!.Value, _wire.RepArticleSlots(articles)),
                "cash" => (_cashOpcode!.Value, _wire.RepCashInventory()),
                _ => throw new InvalidOperationException($"Unknown inventory publication entry '{step}'."),
            };
            // kr-1256 sub_10D8150 reads cash counts directly; the other publication wrappers are table values.
            var envelope = step switch
            {
                "cash" => InventoryValues.Plain,
                "articles" => _values.ArticleEnvelope,
                _ => _values.Envelope,
            };
            var payload = envelope == InventoryValues.Zlib ? ZlibEnvelope.Wrap(body) : body;
            frames.Add((opcode, payload));
        }
        ct.ThrowIfCancellationRequested();
        publication?.ThrowIfInvalid();
        if (resetBaseline && publication is not null)
            publication.Reset(baseline!.Value);
        foreach (var frame in frames)
        {
            if (publication is null) await session.SendAsync(frame.Opcode, frame.Payload, ct).ConfigureAwait(false);
            else await publication.SendAsync(session, frame.Opcode, frame.Payload).ConfigureAwait(false);
        }
    }

    private static void CheckPublicationBounds(IReadOnlyList<string> steps, IReadOnlyList<InventorySlotRecord> character,
        IReadOnlyList<InventorySlotRecord> equipped, IReadOnlyList<InventorySlotRecord>? changed)
    {
        if (steps.Count > InventoryValues.MaxPublicationFrames
            || character.Count > InventoryLimits.MaxInventorySlotsPerBag
            || equipped.Count > InventoryLimits.MaxInventorySlotsPerBag
            || changed is { Count: > InventoryLimits.MaxInventorySlotsPerBag })
            throw new InvalidDataException("Inventory publication exceeds its bounded frame or row count.");
    }

    private static long PublicationOwner(ISession session, IEnumerable<InventorySlotRecord> rows)
    {
        var owners = rows.Select(row => row.UserNum).Where(owner => owner > 0).Distinct().Take(2).ToArray();
        if (owners.Length > 1)
            throw new InvalidDataException("Inventory publication contains multiple owners.");
        if (session.TryFeature<IPlayerSession>(out var player))
        {
            if (owners.Length != 0 && owners[0] != player.Identity.UserNum)
                throw new InvalidDataException("Inventory publication belongs to another player.");
            return player.Identity.UserNum;
        }
        return owners.Length == 0 ? 0 : owners[0];
    }

    private IReadOnlyDictionary<string, object?>[] ArticleValues(IReadOnlyList<InventorySlotRecord> character,
        IReadOnlyList<InventorySlotRecord> equipped,
        IReadOnlyDictionary<InventorySlotRecord, InventoryItemPresentation> presentations)
    {
        if (!_wire.SupportsArticles) return [];
        // Admission changes the published table, never the validity of stored identities or their mirrors.
        var owned = character.Where(row => InventoryArticleIdentityResolver.Of(row) is not null).ToArray();
        var mirrors = equipped.Where(row => InventoryArticleIdentityResolver.Of(row) is not null || owned.Any(owner => owner.ItemId == row.ItemId)).ToArray();
        if (owned.Length > InventoryLimits.MaxInventorySlotsPerBag
            || owned.GroupBy(row => row.ItemId).Any(group => group.Count() != 1)
            || owned.GroupBy(row => InventoryArticleIdentityResolver.Of(row)!.EntitlementId).Any(group => group.Count() != 1)
            || owned.GroupBy(row => InventoryArticleIdentityResolver.Of(row)!.ArticleKey).Any(group => group.Count() != 1)
            || mirrors.GroupBy(row => row.ItemId).Any(group => group.Count() != 1))
            throw new InvalidDataException("Article ownership is ambiguous or exceeds the inventory bound.");
        foreach (var row in owned.Concat(mirrors))
        {
            if (InventoryArticleIdentityResolver.Of(row) is null)
                throw new InvalidDataException("An equipped mirror has no durable Article identity.");
            InventoryArticleIdentityResolver.Of(row)!.Validate();
            if (!PetArticleKeys.IsAddressable(InventoryArticleIdentityResolver.Of(row)!.ArticleKey))
                throw new InvalidDataException("The stored Article key is outside the client's signed lookup range.");
        }
        foreach (var mirror in mirrors)
        {
            var owner = owned.SingleOrDefault(row => InventoryArticleIdentityResolver.Of(row) == InventoryArticleIdentityResolver.Of(mirror));
            if (owner is null || owner.ItemId != mirror.ItemId || owner.UserNum != mirror.UserNum
                || PublishedArticleIdentity(owner) != PublishedArticleIdentity(mirror)
                || !Equals(owner.Pet, mirror.Pet) || !Equals(owner.Enhancements, mirror.Enhancements)
                || !Equals(owner.Modifications, mirror.Modifications) || !Equals(owner.Favorites, mirror.Favorites)
                || (owner.Flags & InventorySlotRecord.WornFlagBit) != (mirror.Flags & InventorySlotRecord.WornFlagBit))
                throw new InvalidDataException("An equipped Article mirror disagrees with its ownership row.");
        }
        return owned.Where(row => row.Quantity > 0 && PublishedArticleIdentity(row) is not null).Select(row => (IReadOnlyDictionary<string, object?>)
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["article_key"] = unchecked((long)PublishedArticleIdentity(row)!.ArticleKey),
                ["item_id"] = row.ItemId,
                ["state"] = 1,
                ["equipped"] = (row.Flags & InventorySlotRecord.WornFlagBit) != 0,
                ["attributes"] = ItemAttributeWire.Values(
                    (presentations.GetValueOrDefault(row) ?? InventoryItemPresentation.Empty).Attributes),
            }).ToArray();
    }

    private InventoryArticleIdentity? PublishedArticleIdentity(InventorySlotRecord row)
    {
        if (!_wire.SupportsArticles) return null;
        var identity = InventoryArticleIdentityResolver.Of(row);
        return row.PetIdentity is not null || _values.OrdinaryArticlesAdmitted ? identity : null;
    }

    private byte[] Body(IReadOnlyList<InventorySlotRecord> rows, DateTimeOffset? baseline, bool filter,
        IReadOnlyDictionary<InventorySlotRecord, InventoryItemPresentation> presentations, bool all, bool omitEmpty)
    {
        IEnumerable<InventorySlotRecord> selected = omitEmpty || filter && _values.Rows == InventoryValues.Ranked
            ? rows.Where(slot => slot.Quantity > 0) : rows;
        // kr-1256 sub_8072A0, cn-774 sub_9A6F50 and tw-454 sub_C057A0 enumerate ordinary and Article rows separately.
        selected = selected.Where(slot => PublishedArticleIdentity(slot) is null);
        if (_values.Rows == InventoryValues.Ranked) selected = selected.OrderBy(slot => slot.ItemId);
        var values = selected.Select(slot =>
        {
            var presentation = presentations.GetValueOrDefault(slot) ?? InventoryItemPresentation.Empty;
            var fields = _values.Rows == InventoryValues.Mirror ? ModernSlot(slot) : InventoryWire.ToValues(Map(slot, baseline!.Value));
            fields[_values.Rows == InventoryValues.Mirror ? "attributes" : "unknown_32"] =
                ItemAttributeWire.Values(presentation.Attributes);
            if (presentation.RemainingUntil is { } deadline)
            {
                if (baseline is null) throw new InvalidOperationException("An inventory display timer requires the connection's time origin.");
                var days = (deadline - baseline.Value).TotalDays;
                if (_values.Rows == InventoryValues.Mirror) fields["unknown_8"] = BitConverter.DoubleToInt64Bits(days);
                else fields["remaining_days"] = days;
            }
            else if (_values.OrdinaryExpiryAdmitted && slot.Lifetime.Kind == InventoryLifetimeKind.ExpiresAt)
            {
                if (baseline is null) throw new InvalidOperationException("An inventory ownership timer requires the connection's time origin.");
                // kr-1256 sub_804160, cn-774 sub_9A44B0 and tw-454 sub_C09140 subtract elapsed days from these raw double bits.
                fields["unknown_8"] = BitConverter.DoubleToInt64Bits(InventoryTime.ExpiringDaysAtBaseline(slot.Lifetime, baseline.Value));
            }
            return (IReadOnlyDictionary<string, object?>)fields;
        }).ToArray();
        return all ? _wire.RepInventoryAllValues(values) : _wire.RepInventorySlotsValues(values);
    }

    // kr-1256 sub_10D8550 reads this base row; Body adds projected attributes and any display deadline.
    // kr-1256 sub_804160 and kr-1256 sub_EE2790 consume that deadline as days relative to the inventory time origin.
    private static Dictionary<string, object?> ModernSlot(InventorySlotRecord slot) =>
        new(StringComparer.Ordinal)
        {
            ["item_id"] = slot.ItemId,
            ["quantity"] = slot.Quantity,
            ["unknown_8"] = slot.SlotKey,
            ["flags"] = slot.Flags,
        };

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
