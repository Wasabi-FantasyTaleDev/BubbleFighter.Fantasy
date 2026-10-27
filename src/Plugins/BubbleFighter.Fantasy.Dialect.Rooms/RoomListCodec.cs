using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Packets;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>
/// The modern family's room list, kr-812's classes: <c>gcpReqListSession</c> (kr-1256 builder sub_858E70) to
/// <see cref="RoomListRequested"/>, and <see cref="RoomListSnapshot"/> to <c>gspRepListSession</c> (read on the
/// lobby stage by kr-1256 sub_853CF0). Both layouts are the packet tables' <c>RoomListRequest</c> and
/// <c>RoomListReply</c> as the build has them.
/// </summary>
internal sealed class RoomListCodec(string protocol, ushort request, ushort reply, PacketTables tables, string build, ILogger logger)
    : IInboundMessageBinding<RoomListRequested>, IMessageSender<RoomListSnapshot>
{
    /// <summary>The main-menu session kind a room-list row carries (kr-1256 sub_9AC490's kind switch).</summary>
    internal const int ListRowKind = 2;

    public string Protocol => protocol;

    internal void Mount(IPluginContext context)
    {
        // The reply header words and the row vector are stated exactly as the writer sizes them: kr-1256
        // sub_853CF0 reads the three header words on the lobby stage, and each row is the SessionSetting the
        // list builder kr-1256 sub_84F550 consumes with its map name and master label.
        PacketLayoutContract.RequireFields(tables, build, "RoomListRequest", "Room list",
            ("unknown_4", "u16"), ("unknown_6", "u16"));
        PacketLayoutContract.RequireFields(tables, build, "RoomListReply", "Room list",
            ("unknown_0", "u32"), ("unknown_4", "u32"), ("unknown_8", "u32"), ("rooms", "list<SessionSetting>"));
        PacketLayoutContract.RequireFields(tables, build, "SessionSetting", "Room list",
            ("room_id", "u16"), ("session_state", "u32"), ("title", "wstr"), ("occupants", "u16"),
            ("open_seats", "u16"), ("has_password", "bool"), ("master_nickname", "wstr"), ("kind", "u32"),
            ("game_setting", "GameSetting"));
        var messages = context.Services.Get<IMessageRouter>();
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<RoomListRequested>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<RoomListSnapshot>>(this));
        context.Effect(context.Services.Get<IPacketRouter>().Register(Protocol, request, async (session, packet, ct) =>
        {
            var fields = PacketCodec.ReadScalars(tables, build, "RoomListRequest", packet.Payload.Span);
            // The second word is a literal 10 from the builder; its meaning is unproven, so it stays here.
            var cursor = (ushort)fields["unknown_4"];
            logger.LogDebug("Session {Id} C2S {Opcode} cursor={Cursor} unknown_6={Unknown6}.", session.Id, request, cursor, fields["unknown_6"]);
            if (!await messages.DispatchAsync(Protocol, session, new RoomListRequested(cursor), ct).ConfigureAwait(false))
                logger.LogWarning("Session {Id} C2S {Opcode} room list request has no handler on {Protocol}.", session.Id, request, Protocol);
        }));
    }

    public ValueTask SendAsync(ISession session, RoomListSnapshot message, CancellationToken ct) =>
        session.SendAsync(reply, Write(tables, build, message), ct);

    /// <summary>
    /// Writes the reply: the cursor, a word sent as 1 (meaning unverified), the row count,
    /// then one session setting per room. A row carries the waiting state 2 (the join gate and the row colour key on
    /// it) or 4 for a match in progress, the occupants over the capacity (the count widget draws +32 - +34 over +36),
    /// and the mode in the game setting with its gate words clear.
    /// </summary>
    internal static byte[] Write(PacketTables tables, string build, RoomListSnapshot message) =>
        PacketCodec.Write(tables, build, "RoomListReply", new Dictionary<string, object?>
        {
            ["unknown_0"] = message.Cursor,
            ["unknown_4"] = 1,
            ["unknown_8"] = message.Rooms.Count,
            ["rooms"] = message.Rooms.Select(room => new Dictionary<string, object?>
            {
                ["room_id"] = checked((ushort)room.RoomId),
                ["session_state"] = room.MatchInProgress ? 4 : 2,
                ["title"] = room.Name,
                ["occupants"] = checked((ushort)room.Occupants),
                ["open_seats"] = checked((ushort)room.Capacity),
                ["has_password"] = room.HasPassword,
                ["master_nickname"] = room.MasterNickname,
                ["kind"] = ListRowKind,
                ["game_setting"] = Mode(tables, build, room.ModeId),
            }).ToArray(),
        });

    private static Dictionary<string, object?> Mode(PacketTables tables, string build, int mode)
    {
        var game = new Dictionary<string, object?>();
        RoomProjectionFields.Load(tables, build).WriteMode(game, mode);
        return game;
    }
}
