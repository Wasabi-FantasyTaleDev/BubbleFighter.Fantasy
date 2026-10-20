using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Messages;

internal sealed class CenterDirectoryBinding(string protocol, IOpcodeTable opcodes, PacketTables tables, string build)
    : IInboundMessageBinding<CenterStatusRequested>, IInboundMessageBinding<CenterAvailabilityRequested>,
      IMessageSender<CenterStatusReplied>, IMessageSender<CenterAvailabilityReplied>
{
    public string Protocol => protocol;
    internal void Bind(IPluginContext context, IMessageRouter messages)
    {
        ValidateLayouts(tables, build);
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<CenterStatusRequested>>(this));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<CenterAvailabilityRequested>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<CenterStatusReplied>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<CenterAvailabilityReplied>>(this));
        var router = context.Services.Get<IPacketRouter>();
        // cn-324 sub_7001F0 sends a GPK marker; kr-812 sub_813060 sends the center query directly.
        var marker = opcodes.All.SingleOrDefault(entry => entry.Direction == PacketDirection.ClientToServer && entry.Name == "gcpStartGPK");
        if (marker is not null)
            context.Effect(router.Register(protocol, marker.Value, (_, packet, _) =>
            {
                if (!packet.Payload.IsEmpty) throw new PacketFormatException("GPK marker must be empty.", 0);
                return ValueTask.CompletedTask;
            }));
        context.Effect(router.Register(protocol, opcodes.C2S("gcpReqCenterUserCnt"), async (session, packet, ct) =>
        {
            if (!packet.Payload.IsEmpty) throw new PacketFormatException("Center status request must be empty.", 0);
            if (!await messages.DispatchAsync(protocol, session, new CenterStatusRequested(), ct).ConfigureAwait(false))
                throw new InvalidOperationException("Center status has no typed handler.");
        }));
        context.Effect(router.Register(protocol, opcodes.C2S("gcpReqUniquenessCheck"), async (session, packet, ct) =>
        {
            var request = ReadAvailabilityRequest(tables, build, packet.Payload.Span);
            if (!await messages.DispatchAsync(protocol, session, request, ct).ConfigureAwait(false))
                throw new InvalidOperationException("Center availability has no typed handler.");
        }));
    }
    public ValueTask SendAsync(ISession session, CenterStatusReplied message, CancellationToken ct) =>
        session.SendAsync(opcodes.S2C("gspRepCenterUserCnt"), WriteStatus(tables, build, message), ct);
    public ValueTask SendAsync(ISession session, CenterAvailabilityReplied message, CancellationToken ct) =>
        session.SendAsync(opcodes.S2C("gspRepUniquenessCheck"), WriteAvailability(tables, build, message), ct);
    internal static CenterAvailabilityRequested ReadAvailabilityRequest(PacketTables tables, string build, ReadOnlySpan<byte> payload) =>
        new((int)PacketCodec.ReadScalars(tables, build, "CenterAvailabilityRequest", payload)["user_num"]);

    internal static byte[] WriteStatus(PacketTables tables, string build, CenterStatusReplied message) =>
        PacketCodec.Write(tables, build, "CenterStatus", new Dictionary<string, object?>
        {
            ["center_id"] = message.CenterId, ["name"] = message.Name,
            ["users"] = message.Users, ["capacity"] = message.Capacity,
        });

    internal static byte[] WriteAvailability(PacketTables tables, string build, CenterAvailabilityReplied message) =>
        PacketCodec.Write(tables, build, "CenterAvailabilityReply", new Dictionary<string, object?>
        {
            ["unavailable"] = message.Unavailable,
        });

    private static void ValidateLayouts(PacketTables tables, string build)
    {
        // The native shapes this binding writes or reads; the named and unnamed center forms differ in one field.
        PacketLayoutContract.RequireSignature(tables, build, "CenterStatus", "Center directory",
            "center_id:u32,name:wstr,users:u32,capacity:u32",
            "center_id:u32,users:u32,capacity:u32");
        PacketLayoutContract.RequireSignature(tables, build, "CenterAvailabilityRequest", "Center directory",
            "user_num:u32");
        PacketLayoutContract.RequireSignature(tables, build, "CenterAvailabilityReply", "Center directory",
            "unavailable:bool");
    }
}
