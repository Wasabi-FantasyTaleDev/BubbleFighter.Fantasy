using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>
/// Serves the <c>quick_matching</c> capability for the modern builds whose request is the typed
/// family in packets/room-matching.yaml: kr-1256 sub_E651C0 stamps C2S gcpReqMatch and the request
/// object's serializer kr-1256 sub_114E2E0 writes a u8 type, a u32 and a per-type tail, while the
/// stage gates read the shared RoomMatchNotification (kr-1256 sub_84F6C0; result 0 opens DlgWaiting
/// through kr-1256 sub_8926C0). The start (type 1) and its five-second keepalive (type 2) drive the
/// shared room matcher with the configured default mode, because no modern request carries a mode
/// word. The party join (type 3) and the listing entry (type 4) have no mounted flow and are refused.
/// </summary>
internal sealed class TypedMatchingBinding(string protocol, IOpcodeTable table, PacketTables tables, string build,
    int defaultModeId) : IInboundMessageBinding<RoomMatchRequested>, IMessageSender<RoomMatchNotified>
{
    public string Protocol => protocol;

    internal void Mount(IPluginContext context, IMessageRouter messages)
    {
        if (!tables.CapabilityOf(build, "quick_matching"))
            throw new ConfigException("Native quick matching has not been audited for this build.");
        // kr-1256 sub_114E2E0 always writes the type byte and the u32; the pair is the family's base.
        PacketLayoutContract.RequireFields(tables, build, "RoomMatchRequest", "Quick matching", ("type", "u8"), ("unknown_8", "u32"));
        Validate(tables.Resolve(build, "RoomMatchRequest"));
        _ = table.S2C("gspNotifyMatch");
        _ = Write(tables, build, new RoomMatchNotified(false));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<RoomMatchRequested>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<RoomMatchNotified>>(this));
        context.Effect(context.Services.Get<IPacketRouter>().Register(protocol, table.C2S("gcpReqMatch"),
            async (session, packet, ct) =>
            {
                var type = ReadType(tables, build, packet.Payload.Span);
                if (type is 1 or 2 && defaultModeId > 0)
                {
                    // The matcher deduplicates per session, so the five-second keepalive re-request
                    // lands as a no-op while the start is already queued.
                    if (!await messages.DispatchAsync(protocol, session, new RoomMatchRequested(defaultModeId, false), ct).ConfigureAwait(false))
                        throw new InvalidOperationException("No room matching handler is mounted.");
                    return;
                }
                // An unset default mode, the party join and the listing entry take the native
                // generic refusal (result 1; kr-1256 sub_8926C0 case 1 shows the retry notice).
                await SendAsync(session, new RoomMatchNotified(false), ct).ConfigureAwait(false);
            }));
    }

    internal static int ReadType(PacketTables tables, string build, ReadOnlySpan<byte> body)
    {
        if (body.IsEmpty)
            throw new PacketFormatException("Quick matching is missing its request type.", 0);
        // kr-1256 sub_114E2E0 writes different tails for types 2, 3 and 4; the base reader alone
        // would reject the client's valid keepalive and listing requests as trailing bytes.
        var structure = body[0] switch
        {
            1 => "RoomMatchRequest",
            2 => "RoomMatchKeepAlive",
            3 => "RoomMatchPartyJoin",
            4 => "RoomMatchListingEnter",
            _ => throw new PacketFormatException("Quick matching has an unknown request type.", 0),
        };
        return Convert.ToInt32(PacketCodec.ReadScalars(tables, build, structure, body)["type"]);
    }

    internal static void Validate(IReadOnlyList<PacketField> requestFields)
    {
        // kr-1256 sub_114E2E0 always writes the type byte and the u32; a layout without that leading
        // pair is not the audited modern family.
        if (requestFields.Count < 2 || requestFields[0].Name != "type" || requestFields[0].Type != "u8"
            || requestFields[1].Name != "unknown_8" || requestFields[1].Type != "u32")
            throw new ConfigException("The modern quick-match request selectors have not been audited for this layout.");
    }

    internal static byte[] Write(PacketTables tables, string build, RoomMatchNotified message) =>
        PacketCodec.Write(tables, build, "RoomMatchNotification", new Dictionary<string, object?>
        {
            ["result"] = message.Searching ? 0 : 1,
        });

    public ValueTask SendAsync(ISession session, RoomMatchNotified message, CancellationToken ct) =>
        session.SendAsync(table.S2C("gspNotifyMatch"), Write(tables, build, message), ct);
}
