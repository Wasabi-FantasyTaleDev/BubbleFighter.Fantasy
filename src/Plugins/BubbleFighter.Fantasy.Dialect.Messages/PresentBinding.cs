using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Presents;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Messages;

/// <summary>
/// Adapts the legacy gift box ("Present") packets to the shared gift messages. The byte layouts are the structures of
/// packets/present.yaml; the opcodes come from the build's own table. Requests: the gift purchase (kr-812 sub_95C1C0),
/// the two page requests (kr-812 sub_840530), the four single-gift actions (kr-812 sub_844A30) and the empty new-gift
/// query that the client sends when it closes the box (kr-812 sub_962090). Replies are the matching gspRepPresent*
/// classes, dispatched by kr-812 sub_837170 cases 815 to 828.
/// </summary>
internal sealed class PresentBinding(string protocol, IOpcodeTable opcodes, PacketTables tables, string build)
    : IInboundMessageBinding<PresentSendRequested>, IInboundMessageBinding<PresentListRequested>,
      IInboundMessageBinding<PresentActionRequested>, IInboundMessageBinding<PresentNoticeRequested>,
      IMessageSender<PresentSendReplied>, IMessageSender<PresentListReplied>, IMessageSender<PresentActionReplied>,
      IMessageSender<PresentNoticeSent>
{
    // The result words of gspRepPresentSend that kr-812 sub_829EC0 (and cn-324's equivalent switch) turns into
    // messages: 1 sent, -1 no such fighter, -2 sender information invalid, -3 not enough money, -15 not giftable,
    // -17 the receiver owns it already, -18 gift to oneself, -21 inappropriate message, -51 cash items unsupported.
    // The two box-full codes name no message in the client, so they fall to its generic "gifting failed (code: n)"
    // line and are this server's own.
    internal const int ResultSent = 1;
    internal const int ResultReceiverMissing = -1;
    internal const int ResultSenderInvalid = -2;
    internal const int ResultInsufficientFunds = -3;
    internal const int ResultNotGiftable = -15;
    internal const int ResultReceiverOwnsItem = -17;
    internal const int ResultSelfGift = -18;
    internal const int ResultMessageRejected = -21;
    internal const int ResultReceiverBoxFull = -22;
    internal const int ResultSenderBoxFull = -23;
    internal const int ResultCurrencyRefused = -51;

    // The status word every list, delete and throw reply carries: 1 is success, anything else fails (kr-812
    // sub_828C20 and sub_82AD40 act only on 1); the receive reply fails below zero (kr-812 sub_82A9A0).
    internal const int StatusOk = 1;
    internal const int StatusFailed = 0;
    internal const int ReceiveFailed = -1;

    private ushort _noticeOpcode;
    private ushort _sendReplyOpcode;
    private ushort _receivedListOpcode;
    private ushort _sentListOpcode;
    private ushort _receiveReplyOpcode;
    private ushort _throwReplyOpcode;
    private ushort _deleteReceivedReplyOpcode;
    private ushort _deleteSentReplyOpcode;

    public string Protocol => protocol;

    internal void Bind(IPluginContext context, IMessageRouter messages)
    {
        foreach (var structure in new[]
        {
            "PresentReceive", "PresentSend", "PresentNotice", "PresentReceiveSubList", "PresentSendSubList",
            "PresentSendReply", "PresentReceiveReply", "PresentThrowReply", "PresentDeleteSentReply",
            "PresentDeleteReceivedReply", "PresentSendRequest", "PresentListRequest", "PresentIdRequest",
        })
            if (tables.Resolve(build, structure).Count == 0)
                throw new ConfigException($"The present box requires a packet layout '{structure}' for {build}.");

        var noticeRequest = opcodes.C2S("gcpReqPresentNewReceive");
        var sendRequest = opcodes.C2S("gcpReqPresentSend");
        var receivedListRequest = opcodes.C2S("gcpReqPresentReceiveSubList");
        var sentListRequest = opcodes.C2S("gcpReqPresentSendSubList");
        var receiveRequest = opcodes.C2S("gcpReqPresentReceive");
        var throwRequest = opcodes.C2S("gcpReqPresentThrow");
        var deleteSentRequest = opcodes.C2S("gcpReqPresentDeleteSend");
        var deleteReceivedRequest = opcodes.C2S("gcpReqPresentDeleteReceive");
        _noticeOpcode = opcodes.S2C("gspRepPresentNewReceive");
        _sendReplyOpcode = opcodes.S2C("gspRepPresentSend");
        _receivedListOpcode = opcodes.S2C("gspRepPresentReceiveSubList");
        _sentListOpcode = opcodes.S2C("gspRepPresentSendSubList");
        _receiveReplyOpcode = opcodes.S2C("gspRepPresentReceive");
        _throwReplyOpcode = opcodes.S2C("gspRepPresentThrow");
        _deleteReceivedReplyOpcode = opcodes.S2C("gspRepPresentDeleteReceive");
        _deleteSentReplyOpcode = opcodes.S2C("gspRepPresentDeleteSend");

        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<PresentSendRequested>>(this));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<PresentListRequested>>(this));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<PresentActionRequested>>(this));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<PresentNoticeRequested>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<PresentSendReplied>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<PresentListReplied>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<PresentActionReplied>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<PresentNoticeSent>>(this));

        var packets = context.Services.Get<IPacketRouter>();
        context.Effect(packets.Register(protocol, noticeRequest, (session, packet, ct) =>
        {
            if (!packet.Payload.IsEmpty)
                throw new PacketFormatException("The new-gift query must be empty.", packet.Payload.Length);
            return Dispatch(messages, session, new PresentNoticeRequested(), ct);
        }));
        context.Effect(packets.Register(protocol, sendRequest, (session, packet, ct) =>
            Dispatch(messages, session, ReadSend(tables, build, packet.Payload.Span), ct)));
        context.Effect(packets.Register(protocol, receivedListRequest, (session, packet, ct) =>
            Dispatch(messages, session, ReadList(tables, build, PresentBox.Received, packet.Payload.Span), ct)));
        context.Effect(packets.Register(protocol, sentListRequest, (session, packet, ct) =>
            Dispatch(messages, session, ReadList(tables, build, PresentBox.Sent, packet.Payload.Span), ct)));
        foreach (var (opcode, action) in new[]
        {
            (receiveRequest, PresentAction.Receive), (throwRequest, PresentAction.Throw),
            (deleteReceivedRequest, PresentAction.DeleteReceived), (deleteSentRequest, PresentAction.DeleteSent),
        })
            context.Effect(packets.Register(protocol, opcode, (session, packet, ct) =>
                Dispatch(messages, session, ReadAction(tables, build, action, packet.Payload.Span), ct)));
    }

    private async ValueTask Dispatch<T>(IMessageRouter messages, ISession session, T message, CancellationToken ct)
        where T : IMessage
    {
        if (!await messages.DispatchAsync(protocol, session, message, ct).ConfigureAwait(false))
            throw new InvalidOperationException($"{typeof(T).Name} has no typed handler on {protocol}.");
    }

    internal static PresentSendRequested ReadSend(PacketTables tables, string build, ReadOnlySpan<byte> payload)
    {
        // The recipient is a nickname (at most 64 characters) and the message is bounded by the store; a longer
        // string is a malformed packet, not an over-long gift.
        var fields = PacketCodec.ReadScalars(tables, build, "PresentSendRequest", payload, PresentLimits.MaxMessageChars);
        return new((string)fields["receiver_nickname"], (int)fields["item_key"], (string)fields["message"]);
    }

    internal static PresentListRequested ReadList(PacketTables tables, string build, PresentBox box, ReadOnlySpan<byte> payload)
    {
        var fields = PacketCodec.ReadScalars(tables, build, "PresentListRequest", payload);
        var start = (int)fields["start"];
        var count = (int)fields["count"];
        if (start < 0 || count is < 1 or > PresentLimits.MaxPageCount)
            throw new PacketFormatException($"A gift list request needs a start of at least 0 and a count of 1 to {PresentLimits.MaxPageCount}.", 0);
        return new(box, start, count);
    }

    internal static PresentActionRequested ReadAction(PacketTables tables, string build, PresentAction action, ReadOnlySpan<byte> payload) =>
        new(action, unchecked((uint)(int)PacketCodec.ReadScalars(tables, build, "PresentIdRequest", payload)["id"]));

    public ValueTask SendAsync(ISession session, PresentSendReplied message, CancellationToken ct) =>
        session.SendAsync(_sendReplyOpcode, WriteSendReply(tables, build, message), ct);

    public ValueTask SendAsync(ISession session, PresentListReplied message, CancellationToken ct) =>
        session.SendAsync(message.Box == PresentBox.Received ? _receivedListOpcode : _sentListOpcode,
            WriteList(tables, build, message), ct);

    public ValueTask SendAsync(ISession session, PresentActionReplied message, CancellationToken ct)
    {
        var (opcode, body) = message.Action switch
        {
            PresentAction.Receive => (_receiveReplyOpcode, PacketCodec.Write(tables, build, "PresentReceiveReply", new Dictionary<string, object?>
            {
                ["result"] = message.Succeeded ? StatusOk : ReceiveFailed, ["id"] = Word(message.Id),
                ["item_key"] = message.StockKey, ["pending_count"] = message.PendingCount,
            })),
            PresentAction.Throw => (_throwReplyOpcode, PacketCodec.Write(tables, build, "PresentThrowReply", new Dictionary<string, object?>
            {
                ["status"] = message.Succeeded ? StatusOk : StatusFailed, ["id"] = Word(message.Id),
            })),
            PresentAction.DeleteReceived => (_deleteReceivedReplyOpcode, PacketCodec.Write(tables, build, "PresentDeleteReceivedReply", new Dictionary<string, object?>
            {
                ["status"] = message.Succeeded ? StatusOk : StatusFailed, ["id"] = Word(message.Id),
                ["pending_count"] = message.PendingCount,
            })),
            PresentAction.DeleteSent => (_deleteSentReplyOpcode, PacketCodec.Write(tables, build, "PresentDeleteSentReply", new Dictionary<string, object?>
            {
                ["status"] = message.Succeeded ? StatusOk : StatusFailed, ["id"] = Word(message.Id),
            })),
            _ => throw new ArgumentOutOfRangeException(nameof(message), message.Action, "Unknown gift action."),
        };
        return session.SendAsync(opcode, body, ct);
    }

    public ValueTask SendAsync(ISession session, PresentNoticeSent message, CancellationToken ct) =>
        session.SendAsync(_noticeOpcode, WriteNotice(tables, build, message), ct);

    internal static byte[] WriteNotice(PacketTables tables, string build, PresentNoticeSent message) =>
        PacketCodec.Write(tables, build, "PresentNotice", new Dictionary<string, object?>
        {
            ["has_new"] = message.HasNew ? 1 : 0, ["pending_count"] = Math.Max(0, message.PendingCount),
        });

    internal static byte[] WriteSendReply(PacketTables tables, string build, PresentSendReplied message) =>
        PacketCodec.Write(tables, build, "PresentSendReply", new Dictionary<string, object?>
        {
            ["result"] = SendResult(message.Status),
        });

    internal static int SendResult(PresentSendStatus status) => status switch
    {
        PresentSendStatus.Sent => ResultSent,
        PresentSendStatus.ReceiverMissing => ResultReceiverMissing,
        PresentSendStatus.SenderInvalid => ResultSenderInvalid,
        PresentSendStatus.InsufficientFunds => ResultInsufficientFunds,
        PresentSendStatus.NotGiftable => ResultNotGiftable,
        PresentSendStatus.ReceiverOwnsItem => ResultReceiverOwnsItem,
        PresentSendStatus.SelfGift => ResultSelfGift,
        PresentSendStatus.MessageRejected => ResultMessageRejected,
        PresentSendStatus.CurrencyRefused => ResultCurrencyRefused,
        PresentSendStatus.ReceiverBoxFull => ResultReceiverBoxFull,
        PresentSendStatus.SenderBoxFull => ResultSenderBoxFull,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown gift send status."),
    };

    internal static byte[] WriteList(PacketTables tables, string build, PresentListReplied message)
    {
        var records = message.Records.Select(record => (object?)(message.Box == PresentBox.Received
            ? ReceivedRecord(record) : SentRecord(record))).ToArray();
        return message.Box == PresentBox.Received
            ? PacketCodec.Write(tables, build, "PresentReceiveSubList", new Dictionary<string, object?>
            {
                ["status"] = StatusOk, ["total"] = message.Total, ["pending_count"] = message.PendingCount,
                ["presents"] = records,
            })
            : PacketCodec.Write(tables, build, "PresentSendSubList", new Dictionary<string, object?>
            {
                ["status"] = StatusOk, ["total"] = message.Total, ["presents"] = records,
            });
    }

    private static Dictionary<string, object?> ReceivedRecord(PresentRecord record) => new()
    {
        ["id"] = Word(record.Id), ["sent_date"] = OleDate(record.SentAtUnixMilliseconds),
        ["sender_nickname"] = record.SenderNickname, ["item_key"] = record.StockKey,
        ["opened"] = record.State == PresentState.Accepted ? 1 : 0, ["message"] = record.Message,
    };

    private static Dictionary<string, object?> SentRecord(PresentRecord record) => new()
    {
        ["id"] = Word(record.Id), ["sent_date"] = OleDate(record.SentAtUnixMilliseconds),
        ["receiver_nickname"] = record.ReceiverNickname, ["item_key"] = record.StockKey,
        ["opened"] = record.State == PresentState.Accepted ? 1 : 0, ["message"] = record.Message,
    };

    // The dates are shown as year, month and day (kr-812 sub_44C0B0 -> VariantTimeToSystemTime) in the player's
    // local time, as the login time is (packets/login.yaml server_time).
    private static double OleDate(long unixMilliseconds) =>
        DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds).ToLocalTime().DateTime.ToOADate();

    // Gift ids travel as unsigned 32-bit words; one that does not fit would name another gift.
    private static uint Word(long id) =>
        id is >= 0 and <= PresentLimits.MaxId ? (uint)id : throw new ArgumentOutOfRangeException(nameof(id), id, "A gift id must fit 32 bits.");
}
