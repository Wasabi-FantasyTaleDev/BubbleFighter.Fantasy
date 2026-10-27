using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Packets;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>Adapts the first-run tutorial selection to the configured native solo scene.</summary>
internal sealed class TutorialEntryBinding(string protocol, IOpcodeTable table, PacketTables packets, string build,
    InitialTutorialRoute route, ILogger logger)
    : IInboundMessageBinding<InitialTutorialRequested>, IInboundMessageBinding<InitialTutorialExitRequested>,
      IMessageSender<InitialTutorialEntered>
{
    private IReadOnlyDictionary<string, ushort> _s2c = new Dictionary<string, ushort>();

    public string Protocol => protocol;

    internal void Mount(IPluginContext context)
    {
        PacketLayoutContract.RequireFields(packets, build, "TutorialPlayRequest", "Initial tutorial",
            ("map_key", "wstr"), ("map_directory", "wstr"), ("map_name", "wstr"),
            ("unknown_52", "u8"), ("difficulty", "u32"), ("time_attack_time_limit_ms", "u32"));
        PacketLayoutContract.RequireFields(packets, build, "TutorialEndStage", "Initial tutorial exit",
            ("unknown_0", "u32"), ("unknown_4", "bool"));
        PacketLayoutContract.RequireFields(packets, build, "GameSetting", "Initial tutorial game setting",
            ("mode", "u16"), ("unknown_44", "wstr"), ("unknown_68", "wstr"), ("unknown_92", "wstr"), ("unknown_116", "wstr"),
            ("unknown_140", "u32"));
        PacketLayoutContract.RequireFields(packets, build, "SessionSetting", "Initial tutorial session",
            ("room_id", "u16"), ("session_state", "u32"), ("occupants", "u16"), ("open_seats", "u16"),
            ("master_nickname", "wstr"), ("kind", "u32"), ("game_setting", "GameSetting"));
        PacketLayoutContract.RequireFields(packets, build, "SessionSignatureData", "Initial tutorial signature", ("unknown_4", "u16"));
        PacketLayoutContract.RequireFields(packets, build, "RoomEnterGame", "Initial tutorial scene",
            ("map_key", "wstr"), ("stage_key", "wstr"), ("map_selection", "wstr"), ("room_state", "u32"));
        _s2c = RoomRoutes.Preflight(table, packets, build,
            ["TutorialPlayRequest", "TutorialEndStage", "gspGotoRoom", "RoomSeatUpdate", "SessionSignatureData", "RoomEnterGame"],
            ["gspGotoRoom", "gspRoomSetSlot", "gspSessionSignatureData", "gspRoomEnterGame"]);
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<InitialTutorialRequested>>(this));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<InitialTutorialExitRequested>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<InitialTutorialEntered>>(this));
        RoomRoutes.Bind(context, protocol, table, logger, "gcpReqPlayTutorial", Read);
        RoomRoutes.Bind(context, protocol, table, logger, "gcpReqEndStage", body =>
        {
            var fields = PacketCodec.ReadScalars(packets, build, "TutorialEndStage", body);
            // kr-1256 sub_7BAC80 exits with 1; kr-1256 sub_7B9820 exits with 0 after failure, both via kr-1256 sub_6E5840.
            if ((int)fields["unknown_0"]! is not (0 or 1) || (bool)fields["unknown_4"]!)
                throw new NotSupportedException("The initial tutorial exit requires a native completion or failure confirmation.");
            return new InitialTutorialExitRequested();
        });
    }

    private InitialTutorialRequested Read(ReadOnlySpan<byte> body)
    {
        var fields = PacketCodec.ReadScalars(packets, build, "TutorialPlayRequest", body);
        if ((string)fields["map_key"]! != route.RequestMap || (string)fields["map_directory"]! != route.RequestMap
            || ((byte)fields["unknown_52"]! & 0x10) == 0 || (int)fields["difficulty"]! != 1)
            throw new NotSupportedException("Only the initial tutorial map is enabled.");
        var limit = (int)fields["time_attack_time_limit_ms"]!;
        if (limit <= 0)
            throw new PacketFormatException("Initial tutorial requires a positive map clock.", 32);
        return new(route.Mode, route.Map, route.TimeLimitMs, body.ToArray()) { Team = route.PlayerTeam };
    }

    public async ValueTask SendAsync(ISession session, InitialTutorialEntered message, CancellationToken ct)
    {
        var room = message.Room;
        var seat = room.Seats.Single();
        var game = new Dictionary<string, object?>
        {
            ["mode"] = checked((ushort)room.ModeId),
            ["unknown_44"] = room.MapKey, ["unknown_68"] = room.MapKey,
            ["unknown_92"] = room.MapKey, ["unknown_116"] = room.MapKey,
            // kr-1256 sub_9E5E70 publishes GameSetting to event 287; kr-1256 sub_7EEB80 copies +140 to Lua difficulty.
            ["unknown_140"] = route.Difficulty,
        };
        // kr-1256 sub_9AC490 / kr-1256 sub_11511C0 keep native solo modes out of the waiting-room stage;
        // kr-1256 sub_9D9930 case 0x1FE then enters the map through kr-1256 sub_9E5E70.
        var setting = new Dictionary<string, object?>
        {
            ["room_id"] = checked((ushort)room.RoomId), ["session_state"] = 3,
            ["occupants"] = (ushort)1, ["open_seats"] = (ushort)1,
            ["master_nickname"] = seat.Nickname, ["kind"] = 2, ["game_setting"] = game,
        };
        await session.SendAsync(_s2c["gspGotoRoom"], RoomRecords.Install(packets, build, setting, [seat], seat.Slot, seat.Slot), ct).ConfigureAwait(false);
        await session.SendAsync(_s2c["gspRoomSetSlot"], RoomRecords.SeatUpdate(packets, build, seat, [seat]), ct).ConfigureAwait(false);
        await session.SendAsync(_s2c["gspSessionSignatureData"], PacketCodec.Write(packets, build, "SessionSignatureData",
            new Dictionary<string, object?> { ["unknown_4"] = checked((ushort)room.ModeId) }), ct).ConfigureAwait(false);
        await session.SendAsync(_s2c["gspRoomEnterGame"], PacketCodec.Write(packets, build, "RoomEnterGame",
            new Dictionary<string, object?>
            {
                ["map_key"] = room.MapKey, ["stage_key"] = room.MapKey,
                ["map_selection"] = room.MapKey, ["room_state"] = 3,
            }), ct).ConfigureAwait(false);
    }
}
