using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>
/// Serves the <c>quick_matching</c> capability for the builds whose request is the lobby-state
/// family: cn-774 sub_64DCD0 and tw-454 sub_4DCE50 stamp C2S <c>gcpReqLobbyState_</c> and write
/// one u32 state id, where 80 enters matching and 60 leaves it, and the stage gates read the
/// shared RoomMatchNotification (cn-774 sub_9F3B80; result 0 opens dialog 1608). The start drives
/// the shared room matcher with the configured default mode, because the request carries no mode
/// word; the leave cancels the session's pending search. The channel-pop states (30 and 20) have
/// unproven semantics and are logged without a fabricated reply.
/// </summary>
internal sealed class LobbyStateMatchingBinding(string protocol, IOpcodeTable table, PacketTables tables, string build,
    int defaultModeId) : IInboundMessageBinding<RoomMatchRequested>, IMessageSender<RoomMatchNotified>
{
    public string Protocol => protocol;

    internal void Mount(IPluginContext context, IMessageRouter messages)
    {
        if (!tables.CapabilityOf(build, "quick_matching"))
            throw new ConfigException("Native quick matching has not been audited for this build.");
        // cn-774 sub_64DCD0 always writes the one u32 state id.
        PacketLayoutContract.RequireFields(tables, build, "LobbyStateRequest", "Quick matching", ("state_id", "u32"));
        _ = table.S2C("gspNotifyMatch");
        _ = Write(tables, build, new RoomMatchNotified(false));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<RoomMatchRequested>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<RoomMatchNotified>>(this));
        context.Effect(context.Services.Get<IPacketRouter>().Register(protocol, table.C2S("gcpReqLobbyState_"),
            async (session, packet, ct) =>
            {
                var state = ReadState(tables, build, packet.Payload.Span);
                if (state == 80 && defaultModeId > 0)
                {
                    if (!await messages.DispatchAsync(protocol, session, new RoomMatchRequested(defaultModeId, false), ct).ConfigureAwait(false))
                        throw new InvalidOperationException("No room matching handler is mounted.");
                    return;
                }
                if (state == 60)
                {
                    if (!await messages.DispatchAsync(protocol, session, new RoomMatchCancelled(), ct).ConfigureAwait(false))
                        throw new InvalidOperationException("No room matching handler is mounted.");
                    return;
                }
                // The enter state with no configured default mode, and the channel-pop states whose
                // semantics are unproven, take no fabricated reply; the route logs the state instead.
            }));
    }

    internal static int ReadState(PacketTables tables, string build, ReadOnlySpan<byte> body)
    {
        var values = PacketCodec.ReadScalars(tables, build, "LobbyStateRequest", body);
        return Convert.ToInt32(values["state_id"]);
    }

    internal static byte[] Write(PacketTables tables, string build, RoomMatchNotified message) =>
        PacketCodec.Write(tables, build, "RoomMatchNotification", new Dictionary<string, object?>
        {
            ["result"] = message.Searching ? 0 : 1,
        });

    public ValueTask SendAsync(ISession session, RoomMatchNotified message, CancellationToken ct) =>
        session.SendAsync(table.S2C("gspNotifyMatch"), Write(tables, build, message), ct);
}
