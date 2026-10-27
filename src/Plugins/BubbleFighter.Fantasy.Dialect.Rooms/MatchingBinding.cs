using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>
/// Serves the <c>quick_matching</c> capability, whose audited selectors and result are kr-812
/// <c>sub_5B3640</c> and <c>sub_687640</c>: the request carries the menu mode to +4 (kr-812
/// <c>sub_420EE0</c> writes, <c>sub_420F60</c> reads; the dedicated-queue flags belong to
/// <c>sub_68E450</c>/<c>sub_68E640</c>/<c>sub_68E9A0</c> and the retry queue to <c>sub_5B3750</c>),
/// and the notification result opens the waiting state for 0 and refuses for 1 (kr-812
/// <c>sub_8B9660</c> reads; cn-717, id-400, id-1809 and cn-406 agree) — packets/room-matching.yaml.
/// </summary>
internal sealed class MatchingBinding(string protocol, IOpcodeTable table, PacketTables tables, string build)
    : IInboundMessageBinding<RoomMatchRequested>, IMessageSender<RoomMatchNotified>
{
    public string Protocol => protocol;

    internal void Mount(IPluginContext context, IMessageRouter messages)
    {
        if (!tables.CapabilityOf(build, "quick_matching"))
            throw new ConfigException("Native quick matching has not been audited for this build.");
        var requestFields = tables.Resolve(build, "RoomMatchRequest");
        var replyFields = tables.Resolve(build, "RoomMatchNotification");
        Validate(requestFields, replyFields);
        // The binding casts the mode selector and writes the notification result; the contract fails the row at
        // load when a build's table keeps those names but changes their types. The two optional queue flags stay
        // checked by Validate, which accepts them only when the table states them as bool.
        PacketLayoutContract.RequireFields(tables, build, "RoomMatchRequest", "Quick matching", ("mode_id", "u32"));
        PacketLayoutContract.RequireFields(tables, build, "RoomMatchNotification", "Quick matching", ("result", "u32"));
        _ = table.S2C("gspNotifyMatch");
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<RoomMatchRequested>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<RoomMatchNotified>>(this));
        context.Effect(context.Services.Get<IPacketRouter>().Register(protocol, table.C2S("gcpReqMatch"), async (session, packet, ct) =>
        {
            if (!await messages.DispatchAsync(protocol, session, Read(tables, build, packet.Payload.Span), ct).ConfigureAwait(false))
                throw new InvalidOperationException("No room matching handler is mounted.");
        }));
        _ = Write(tables, build, new RoomMatchNotified(true));
    }

    internal static void Validate(IReadOnlyList<PacketField> request, IReadOnlyList<PacketField> reply)
    {
        // kr-812 sub_5B3640 and kr-812 sub_687640 prove the named selectors; tables retain each native tail.
        if (request.Count == 0 || request[0].Name != "mode_id" || request[0].Type != "u32"
            || request.Skip(1).Any(field => field.Name is "dedicated_queue" or "retry_queue"
                ? field.Type != "bool"
                : !field.Name.StartsWith("unknown_", StringComparison.Ordinal)
                    || field.Type is not ("u32" or "u16" or "wstr" or "bool")))
            throw new ConfigException("Matching request selectors have not been audited for this layout.");
        if (reply.Count == 0 || reply[0].Name != "result" || reply[0].Type != "u32"
            || reply.Skip(1).Any(field => !field.Name.StartsWith("unknown_", StringComparison.Ordinal)
                || field.Type is not ("u32" or "wstr")))
            throw new ConfigException("Matching notification result has not been audited for this layout.");
    }

    internal static RoomMatchRequested Read(PacketTables tables, string build, ReadOnlySpan<byte> payload)
    {
        var values = PacketCodec.ReadScalars(tables, build, "RoomMatchRequest", payload);
        var mode = (int)values["mode_id"];
        if (mode is < 0 or > ushort.MaxValue)
            throw new PacketFormatException("Invalid room matching mode.", 0);
        return new RoomMatchRequested(mode,
            values.TryGetValue("dedicated_queue", out var dedicated) && (bool)dedicated,
            values.TryGetValue("retry_queue", out var retry) && (bool)retry);
    }

    internal static byte[] Write(PacketTables tables, string build, RoomMatchNotified message) =>
        PacketCodec.Write(tables, build, "RoomMatchNotification", new Dictionary<string, object?>
        {
            ["result"] = message.Searching ? 0 : 1,
        });

    public ValueTask SendAsync(ISession session, RoomMatchNotified message, CancellationToken ct) =>
        session.SendAsync(table.S2C("gspNotifyMatch"), Write(tables, build, message), ct);
}
