using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Messages;

internal sealed class MigrationBinding(string protocol, IOpcodeTable opcodes, PacketTables tables, string build)
    : IMessageSender<ChannelMoveOffered>, IMessageSender<MoveIntoReplied>, IMessageSender<PendingConnectionPromoted>,
      IInboundMessageBinding<PendingPromotionAcknowledged>, IMessageSender<PendingPromotionCompleted>
{
    public string Protocol => protocol;
    private ushort _promote;
    private ushort? _complete;

    internal void Bind(IPluginContext context, IMessageRouter messages)
    {
        PacketLayoutContract.RequireSignature(tables, build, "ChannelMoveOffer", "Native migration",
            "result:u32,ipv4_word:u32,port:u16", "result:u32,ipv4_word:u32,port:u16,unknown_10:u32");
        _ = opcodes.S2C("gspRepMoveChannel");
        PacketLayoutContract.RequireSignature(tables, build, "gspRepMoveInto", "Native migration",
            "result:u32,channel_id:u32,unknown_12:u32");
        PacketLayoutContract.RequireSignature(tables, build, "PromotePendingConnection", "Native migration", "");
        _ = opcodes.S2C("gspRepMoveInto");
        var acknowledge = opcodes.Find(PacketDirection.ClientToServer, "gcpAcknowledgePendingPromotion_");
        // kr-812 sub_81C550 and cn-324 sub_705980 close the source during promotion; kr-1256 sub_9A6BA0 retains it until completion.
        _promote = opcodes.S2C(acknowledge is null ? "gspDisconnectMovedUser" : "gspDisconnectingMovedUser");
        if (acknowledge is { } ack)
        {
            _complete = opcodes.S2C("gspDisconnectMovedUser");
            PacketLayoutContract.RequireSignature(tables, build, "PendingPromotionAcknowledgement", "Native migration", "");
            PacketLayoutContract.RequireSignature(tables, build, "CompletePendingPromotion", "Native migration", "");
            context.Effect(context.Services.RegisterMany<IInboundMessageBinding<PendingPromotionAcknowledged>>(this));
            context.Effect(context.Services.RegisterMany<IMessageSender<PendingPromotionCompleted>>(this));
            context.Effect(context.Services.Get<IPacketRouter>().Register(protocol, ack, async (session, packet, ct) =>
            {
                if (!packet.Payload.IsEmpty) throw new PacketFormatException("Promotion acknowledgement must be empty.", 0);
                if (!await messages.DispatchAsync(protocol, session, new PendingPromotionAcknowledged(), ct).ConfigureAwait(false))
                    throw new InvalidOperationException("Promotion acknowledgement requires a migration handler.");
            }));
        }
        context.Effect(context.Services.RegisterMany<IMessageSender<MoveIntoReplied>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<ChannelMoveOffered>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<PendingConnectionPromoted>>(this));
    }

    public ValueTask SendAsync(ISession session, ChannelMoveOffered message, CancellationToken ct)
    {
        if (message.Ipv4.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || message.Port == 0)
            throw new ArgumentException("Native migration requires an IPv4 address and a nonzero port.", nameof(message));
        // kr-812 sub_81AB20 and kr-1256 sub_9A6440 accept result zero before opening pending.
        return session.SendAsync(opcodes.S2C("gspRepMoveChannel"), PacketCodec.Write(tables, build, "ChannelMoveOffer",
            new Dictionary<string, object?>
            {
                ["result"] = 0u, ["ipv4_word"] = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(message.Ipv4.GetAddressBytes()),
                ["port"] = message.Port, ["unknown_10"] = 0u,
            }), ct);
    }

    public ValueTask SendAsync(ISession session, MoveIntoReplied message, CancellationToken ct) =>
        session.SendAsync(opcodes.S2C("gspRepMoveInto"), PacketCodec.Write(tables, build, "gspRepMoveInto",
            new Dictionary<string, object?>
            {
                ["result"] = message.Accepted ? 1 : 0, ["channel_id"] = message.Channel, ["unknown_12"] = 0,
            }), ct);

    public ValueTask SendAsync(ISession session, PendingConnectionPromoted message, CancellationToken ct) =>
        session.SendAsync(_promote, ReadOnlyMemory<byte>.Empty, ct);

    public ValueTask SendAsync(ISession session, PendingPromotionCompleted message, CancellationToken ct) =>
        session.SendAsync(_complete ?? throw new InvalidOperationException("This build completes promotion without an acknowledgement."),
            ReadOnlyMemory<byte>.Empty, ct);
}
