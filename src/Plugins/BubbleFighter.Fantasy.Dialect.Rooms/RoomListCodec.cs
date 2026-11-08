using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Packets;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>
/// Every build's room list, kr-812's classes: <c>gcpReqListSession</c> (kr-812 <c>RoomListRequest</c>, kr-1256 builder
/// sub_858E70) to <see cref="RoomListRequested"/>, and <see cref="RoomListSnapshot"/> to <c>gspRepListSession</c> (kr-812
/// reader sub_680780, kr-1256 lobby stage sub_853CF0). Both layouts are the packet tables' <c>RoomListRequest</c> and
/// <c>RoomListReply</c> as the build has them; whether the reply pages is builds.yaml <c>room_list_paging</c>.
/// The optional row-settings function supplies the native game setting a row shows; without one a row shows its mode.
/// </summary>
internal sealed class RoomListCodec(string protocol, ushort request, ushort reply, PacketTables tables, string build, ILogger logger,
    Func<RoomListEntry, ReadOnlyMemory<byte>>? rowSettings = null)
    : IInboundMessageBinding<RoomListRequested>, IMessageSender<RoomListSnapshot>
{
    /// <summary>The main-menu session kind a room-list row carries (kr-1256 sub_9AC490's kind switch).</summary>
    internal const int ListRowKind = RoomCodec.PlayRoomKind;

    // The session item that remembers how many rows the client's last list request asked for.
    private const string PageSizeItem = "room.list.page-size";

    public string Protocol => protocol;

    internal void Mount(IPluginContext context)
    {
        // The reply header words and the row vector are stated exactly as the writer sizes them: kr-1256
        // sub_853CF0 reads the three header words on the lobby stage, and each row is the SessionSetting the
        // list builder kr-1256 sub_84F550 consumes with its map name and master label.
        PacketLayoutContract.RequireFields(tables, build, "RoomListRequest", "Room list",
            ("unknown_4", "u16"), ("unknown_6", "u16"));
        RequireReplyLayout(tables, build);
        _ = Paging(tables, build);
        var messages = context.Services.Get<IMessageRouter>();
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<RoomListRequested>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<RoomListSnapshot>>(this));
        context.Effect(context.Services.Get<IPacketRouter>().Register(Protocol, request, async (session, packet, ct) =>
        {
            var fields = PacketCodec.ReadScalars(tables, build, "RoomListRequest", packet.Payload.Span);
            // kr-812 sends the page and its row count; kr-1256's builder sends a literal 10 as the second word.
            var cursor = Convert.ToUInt16(fields["unknown_4"]);
            var pageSize = Convert.ToUInt16(fields["unknown_6"]);
            session.Items[PageSizeItem] = (int)pageSize;
            logger.LogDebug("Session {Id} C2S {Opcode} cursor={Cursor} page size={PageSize}.", session.Id, request, cursor, pageSize);
            if (!await messages.DispatchAsync(Protocol, session, new RoomListRequested(cursor), ct).ConfigureAwait(false))
                throw new InvalidOperationException($"Room list request has no typed handler on '{Protocol}'.");
        }));
    }

    public ValueTask SendAsync(ISession session, RoomListSnapshot message, CancellationToken ct)
    {
        var pageSize = session.Items.TryGetValue(PageSizeItem, out var stored) && stored is int size ? size : 0;
        return session.SendAsync(reply, Write(tables, build, message, pageSize, rowSettings), ct);
    }

    /// <summary>
    /// Writes the reply. The page form (kr-812 sub_680780) is the page number, whether another page follows, then one
    /// session setting per room; a paging build answers the requested page of <paramref name="pageSize"/> rows, any
    /// other build every room on one page. The counted form (kr-1256 sub_853CF0) is the cursor, a word sent as 1
    /// (meaning unverified), the row count, then the rows. A row carries the waiting state 2 (the join gate and the
    /// row colour key on it) or 4 for a match in progress (kr-812 sub_679670), and the occupants over the capacity.
    /// </summary>
    internal static byte[] Write(PacketTables tables, string build, RoomListSnapshot message, int pageSize = 0,
        Func<RoomListEntry, ReadOnlyMemory<byte>>? rowSettings = null)
    {
        RequireReplyLayout(tables, build);
        if (UsesPageHeader(tables, build))
        {
            var schema = RoomTables.Load(tables).Of(build).Schema;
            var rooms = message.Rooms;
            var skip = 0;
            if (Paging(tables, build))
            {
                var size = pageSize > 0 ? pageSize : Math.Max(1, rooms.Count);
                skip = Math.Max(0, message.Cursor) * size;
                rooms = [.. rooms.Skip(skip).Take(size)];
            }
            return PacketCodec.Write(tables, build, "RoomListReply", new Dictionary<string, object?>
            {
                ["page"] = message.Cursor,
                ["more"] = message.Rooms.Count > skip + rooms.Count,
                ["rooms"] = rooms.Select(room => RoomCodec.PlaySessionValues(schema, new RoomValues(ListRowKind, [])
                {
                    Play = true, RoomId = room.RoomId, Title = room.Name, MasterNickname = room.MasterNickname,
                    SessionState = room.MatchInProgress ? 4 : 2, Mode = room.ModeId,
                    Occupants = room.Occupants, OpenSeats = room.Capacity, HasPassword = room.HasPassword,
                    GameSetting = rowSettings?.Invoke(room) ?? default,
                })).ToArray(),
            });
        }
        return PacketCodec.Write(tables, build, "RoomListReply", new Dictionary<string, object?>
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
    }

    private static bool UsesPageHeader(PacketTables tables, string build) =>
        tables.Resolve(build, "RoomListReply").Any(field => field.Name == "page");

    private static bool Paging(PacketTables tables, string build) =>
        (tables.ValuesOf(build).GetValueOrDefault("room_list_paging") as string) switch
        {
            "true" => true,
            "false" => false,
            _ => throw new ConfigException($"Build '{build}' must set room_list_paging to true or false in builds.yaml."),
        };

    private static void RequireReplyLayout(PacketTables tables, string build)
    {
        if (UsesPageHeader(tables, build))
        {
            PacketLayoutContract.RequireFields(tables, build, "RoomListReply", "Room list",
                ("page", "u32"), ("more", "bool"), ("rooms", "list<SessionSetting>"));
            _ = RoomTables.Load(tables).Of(build);
        }
        else
        {
            PacketLayoutContract.RequireFields(tables, build, "RoomListReply", "Room list",
                ("unknown_0", "u32"), ("unknown_4", "u32"), ("unknown_8", "u32"), ("rooms", "list<SessionSetting>"));
            PacketLayoutContract.RequireFields(tables, build, "SessionSetting", "Room list",
                ("room_id", "u16"), ("session_state", "u32"), ("title", "wstr"), ("occupants", "u16"),
                ("open_seats", "u16"), ("has_password", "bool"), ("master_nickname", "wstr"), ("kind", "u32"),
                ("game_setting", "GameSetting"));
        }
    }

    private static Dictionary<string, object?> Mode(PacketTables tables, string build, int mode)
    {
        var game = new Dictionary<string, object?>();
        RoomProjectionFields.Load(tables, build).WriteMode(game, mode);
        return game;
    }
}
