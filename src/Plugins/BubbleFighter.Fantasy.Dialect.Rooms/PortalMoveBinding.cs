using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>Translates the native map contact request and its movement authorization through packet tables.</summary>
internal sealed class PortalMoveBinding(string protocol, IOpcodeTable table, PacketTables packets, string build)
    : IInboundMessageBinding<RoundPortalMoveRequested>, IMessageSender<RoundPortalMoved>
{
    public string Protocol => protocol;

    /// <summary>Mounts only the build's audited game request; the companion relay notification grants no movement.</summary>
    internal void Mount(IPluginContext context)
    {
        PacketLayoutContract.RequireSignature(packets, build, "PortalMoveRequest", "Map portal",
            "position_x:f32,position_y:f32,position_z:f32,portal_id:u16");
        PacketLayoutContract.RequireSignature(packets, build, "PortalMoveReply", "Map portal",
            "unknown_4:u32,portal_id:u16,seat:u32");
        var request = table.C2S("gcpPortalMove");
        _ = table.S2C("gspPortalMove");
        var services = context.Services;
        var messages = services.Get<IMessageRouter>();
        context.Effect(services.Get<IPacketRouter>().Register(protocol, request, async (session, packet, ct) =>
        {
            var message = Read(packets, build, packet.Payload.Span);
            if (!await messages.DispatchAsync(protocol, session, message, ct).ConfigureAwait(false))
                throw new InvalidOperationException("No map portal handler is installed.");
        }));
        context.Effect(services.RegisterMany<IInboundMessageBinding<RoundPortalMoveRequested>>(this));
        context.Effect(services.RegisterMany<IMessageSender<RoundPortalMoved>>(this));
    }

    public ValueTask SendAsync(ISession session, RoundPortalMoved message, CancellationToken ct) =>
        session.SendAsync(table.S2C("gspPortalMove"), Write(packets, build, message), ct);

    /// <summary>Reads the contact vector and the region key without permitting truncated or extra data.</summary>
    internal static RoundPortalMoveRequested Read(PacketTables packets, string build, ReadOnlySpan<byte> payload)
    {
        var values = PacketCodec.ReadScalars(packets, build, "PortalMoveRequest", payload);
        return new(Convert.ToUInt16(values["portal_id"]), (float)values["position_x"],
            (float)values["position_y"], (float)values["position_z"]);
    }

    /// <summary>Writes the accepted native path, the region key and the authenticated seat.</summary>
    internal static byte[] Write(PacketTables packets, string build, RoundPortalMoved message) =>
        PacketCodec.Write(packets, build, "PortalMoveReply", new Dictionary<string, object?>
        {
            // cn-706 sub_7C6B60 skips movement only for -1; zero enters its map-kind dispatch.
            ["unknown_4"] = 0,
            ["portal_id"] = message.PortalId,
            ["seat"] = message.Seat,
        });
}
