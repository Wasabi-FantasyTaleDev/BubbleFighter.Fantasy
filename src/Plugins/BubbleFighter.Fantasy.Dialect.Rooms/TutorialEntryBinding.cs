using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Packets;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>Adapts direct main-menu tutorial selection and the separately configured first-run route.</summary>
internal sealed class TutorialEntryBinding(string protocol, IOpcodeTable table, PacketTables packets, string build,
    InitialTutorialRoute? route, ILogger logger, bool stageMain = false)
    : IInboundMessageBinding<InitialTutorialRequested>,
      IMessageSender<InitialTutorialEntered>
{
    private IReadOnlyDictionary<string, ushort> _s2c = new Dictionary<string, ushort>();
    private readonly RoomProjectionFields _projection = RoomProjectionFields.Load(packets, build);
    private readonly RoomSettingChangeMap _maps = RoomSettingChangeMap.Load(packets, build)
        ?? throw new ConfigException("Solo entry requires the build's native map and difficulty fields.");

    public string Protocol => protocol;

    internal void Mount(IPluginContext context)
    {
        PacketLayoutContract.RequireFields(packets, build, "TutorialPlayRequest", "Initial tutorial",
            ("map_key", "wstr"), ("map_directory", "wstr"), ("map_name", "wstr"),
            ("unknown_52", "u8"), ("difficulty", "u32"), ("time_attack_time_limit_ms", "u32"));
        PacketLayoutContract.RequireFields(packets, build, "SessionSetting", "Initial tutorial session",
            ("room_id", "u16"), ("session_state", "u32"), ("occupants", "u16"), ("open_seats", "u16"),
            ("master_nickname", "wstr"), ("kind", "u32"), ("game_setting", "GameSetting"));
        PacketLayoutContract.RequireFields(packets, build, "SessionSignatureData", "Initial tutorial signature", ("unknown_4", "u16"));
        PacketLayoutContract.RequireFields(packets, build, "RoomEnterGame", "Initial tutorial scene",
            ("map_key", "wstr"), ("stage_key", "wstr"), ("map_selection", "wstr"), ("room_state", "u32"));
        _s2c = RoomRoutes.Preflight(table, packets, build,
            ["TutorialPlayRequest", "gspGotoRoom", "RoomSeatUpdate", "SessionSignatureData", "RoomEnterGame"],
            ["gspGotoRoom", "gspRoomSetSlot", "gspSessionSignatureData", "gspRoomEnterGame"]);
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<InitialTutorialRequested>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<InitialTutorialEntered>>(this));
        RoomRoutes.Bind(context, protocol, table, logger, "gcpReqPlayTutorial", Read);
    }

    private InitialTutorialRequested Read(ReadOnlySpan<byte> body)
    {
        var fields = PacketCodec.ReadScalars(packets, build, "TutorialPlayRequest", body);
        var key = (string)fields["map_key"]!;
        var directory = (string)fields["map_directory"]!;
        var difficulty = (int)fields["difficulty"]!;
        var limit = (int)fields["time_attack_time_limit_ms"]!;
        var firstRun = ((byte)fields["unknown_52"]! & 0x10) != 0;
        // The default map ORs 0x10 into the map rules; its sender does not clear pre-existing rule bits.
        // Numbered stages are authenticated by their selected key and MapMeta row, independently of this bit.
        if (stageMain && key.StartsWith("sin_mission_", StringComparison.Ordinal)
            && difficulty is >= 1 and <= 3 && limit > 0)
            return new((int)GameModeId.SinglePlay, key, limit, body.ToArray())
                { StageDirectory = directory, Difficulty = difficulty };
        if (route is null || key != route.RequestMap || directory != route.RequestMap || !firstRun || difficulty != 1)
            throw new NotSupportedException("The selection does not match an enabled tutorial route.");
        if (limit <= 0)
            throw new PacketFormatException("Initial tutorial requires a positive map clock.", 32);
        return new(route.Mode, route.Map, route.TimeLimitMs, body.ToArray())
            { Team = route.PlayerTeam, StageDirectory = route.Map, Difficulty = route.Difficulty };
    }

    public async ValueTask SendAsync(ISession session, InitialTutorialEntered message, CancellationToken ct)
    {
        var room = message.Room;
        var seat = room.Seats.Single();
        var game = new Dictionary<string, object?>();
        _projection.WriteMode(game, room.ModeId);
        _maps.ApplyTutorial(game, room.MapKey, message.StageDirectory, message.Difficulty);
        // Native goto-room bypasses the waiting room for mode 15: kr-1256 sub_11511C0,
        // cn-774 sub_47F1C0 and tw-454 sub_471110. RoomEnterGame then installs the selected scene.
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
                ["map_key"] = room.MapKey, ["stage_key"] = message.StageDirectory,
                ["map_selection"] = room.MapKey, ["room_state"] = 3,
            }), ct).ConfigureAwait(false);
    }
}
