using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.CashInbox;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Messages;

/// <summary>
/// Table-driven typed message adapter for cash item inbox frames (<c>gcpReqCashInven</c>, <c>gcpReqPickup</c>,
/// <c>gcpReqCloseGiftBox</c>, <c>gspCashInvenUpdateAllInventorySlot</c>, and <c>gspRepPickup</c>).
/// </summary>
/// <param name="protocol">Protocol instance name owning the routes and senders.</param>
/// <param name="opcodes">Build opcode table resolved on the game connection.</param>
/// <param name="tables">Packet structure definitions used to validate and encode/decode every build's layout.</param>
/// <param name="build">Target client build name.</param>
internal sealed class CashInboxBinding(string protocol, IOpcodeTable opcodes, PacketTables tables, string build) :
    IInboundMessageBinding<CashInboxOpenRequested>,
    IInboundMessageBinding<CashInboxPickupRequested>,
    IInboundMessageBinding<CashInboxCloseRequested>,
    IMessageSender<CashInventoryPublished>,
    IMessageSender<CashInboxPickupReplied>
{
    private const uint ReplySuccess = 1;
    private const uint ReplyFailed = 2;
    private const uint ReplyAccountMismatch = 7;
    private const uint ReplyAlreadyOwned = 8;

    private ushort _cashInventoryOpcode;
    private ushort _pickupReplyOpcode;

    /// <inheritdoc />
    public string Protocol => protocol;

    internal void Bind(IPluginContext context, IMessageRouter messages)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(messages);

        // Validate every opcode and packet field layout up front so a partial row never half-binds routes.
        var openRequest = opcodes.C2S("gcpReqCashInven");
        var pickupRequest = opcodes.C2S("gcpReqPickup");
        var closeRequest = opcodes.C2S("gcpReqCloseGiftBox");
        _cashInventoryOpcode = opcodes.S2C("gspCashInvenUpdateAllInventorySlot");
        _pickupReplyOpcode = opcodes.S2C("gspRepPickup");

        PacketLayoutContract.RequireEmpty(tables, build, "CashInboxRequest", "Cash inbox binding");
        PacketLayoutContract.RequireFields(tables, build, "CashPickupRequest", "Cash inbox binding",
            ("cash_slot_key", "u32"));
        PacketLayoutContract.RequireEmpty(tables, build, "PresentCloseRequest", "Cash inbox binding");
        _ = InventoryWire.CheckedCashSlotTailField(tables, build);
        ValidatePickupReplyLayout(tables, build);

        var packets = context.Services.Get<IPacketRouter>();
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<CashInboxOpenRequested>>(this));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<CashInboxPickupRequested>>(this));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<CashInboxCloseRequested>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<CashInventoryPublished>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<CashInboxPickupReplied>>(this));

        context.Effect(packets.Register(protocol, openRequest, (session, packet, ct) =>
        {
            if (!packet.Payload.IsEmpty)
                throw new PacketFormatException("Cash inbox open request must be empty.", packet.Payload.Length);
            return Dispatch(messages, session, new CashInboxOpenRequested(), ct);
        }));

        context.Effect(packets.Register(protocol, pickupRequest, (session, packet, ct) =>
            Dispatch(messages, session, ReadPickupRequest(tables, build, packet.Payload.Span), ct)));

        context.Effect(packets.Register(protocol, closeRequest, (session, packet, ct) =>
        {
            if (!packet.Payload.IsEmpty)
                throw new PacketFormatException("Cash inbox close request must be empty.", packet.Payload.Length);
            return Dispatch(messages, session, new CashInboxCloseRequested(), ct);
        }));
    }

    internal static CashInboxPickupRequested ReadPickupRequest(
        PacketTables tables,
        string build,
        ReadOnlySpan<byte> payload)
    {
        var fields = PacketCodec.ReadScalars(tables, build, "CashPickupRequest", payload);
        return new(unchecked((uint)(int)fields["cash_slot_key"]));
    }

    internal static byte[] WritePickupReply(
        PacketTables tables,
        string build,
        CashInboxPickupReplied message)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["cash_slot_id"] = Word(message.CashSlotId),
            ["result"] = ResultWord(message.Status),
        };

        // Populate optional secondary presentation fields whenever the build's CashPickupReply layout declares them.
        foreach (var field in tables.Resolve(build, "CashPickupReply"))
        {
            switch (field.Name)
            {
                case "unknown_8":
                    values["unknown_8"] = unchecked((uint)message.PresentationCode);
                    break;
                case "unknown_12":
                    values["unknown_12"] = message.PresentationText ?? "";
                    break;
            }
        }

        return PacketCodec.Write(tables, build, "CashPickupReply", values);
    }

    /// <inheritdoc />
    public ValueTask SendAsync(ISession session, CashInventoryPublished message, CancellationToken ct) =>
        session.SendAsync(
            _cashInventoryOpcode,
            InventoryWire.WriteCashInventory(tables, build, message.Entries),
            ct);

    /// <inheritdoc />
    public ValueTask SendAsync(ISession session, CashInboxPickupReplied message, CancellationToken ct) =>
        session.SendAsync(_pickupReplyOpcode, WritePickupReply(tables, build, message), ct);

    private static void ValidatePickupReplyLayout(PacketTables tables, string build)
    {
        PacketLayoutContract.RequireFields(tables, build, "CashPickupReply", "Cash inbox binding",
            ("cash_slot_id", "u32"), ("result", "u32"));
        var fields = tables.Resolve(build, "CashPickupReply");
        if (fields.Any(f => f.Name == "unknown_8"))
            PacketLayoutContract.RequireFields(tables, build, "CashPickupReply", "Cash inbox binding", ("unknown_8", "u32"));
        if (fields.Any(f => f.Name == "unknown_12"))
            PacketLayoutContract.RequireFields(tables, build, "CashPickupReply", "Cash inbox binding", ("unknown_12", "wstr"));
    }

    private async ValueTask Dispatch<T>(IMessageRouter messages, ISession session, T message, CancellationToken ct)
        where T : IMessage
    {
        if (!await messages.DispatchAsync(protocol, session, message, ct).ConfigureAwait(false))
            throw new InvalidOperationException($"{typeof(T).Name} has no typed handler on {protocol}.");
    }

    internal static uint ResultWord(CashInboxPickupStatus status) => status switch
    {
        CashInboxPickupStatus.Success => ReplySuccess,
        CashInboxPickupStatus.Failed => ReplyFailed,
        CashInboxPickupStatus.AccountMismatch => ReplyAccountMismatch,
        CashInboxPickupStatus.AlreadyOwned => ReplyAlreadyOwned,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unsupported cash inbox pickup status."),
    };

    private static uint Word(long slotId) =>
        slotId is >= 0 and <= CashInboxLimits.MaxCashSlotId
            ? (uint)slotId
            : throw new ArgumentOutOfRangeException(nameof(slotId), slotId, "Cash inbox slot identifier must fit 32 bits.");
}
