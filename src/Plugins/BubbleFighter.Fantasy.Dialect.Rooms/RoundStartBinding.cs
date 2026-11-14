using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Packets;
using Microsoft.Extensions.Logging;
using BubbleFighter.Fantasy.Dialect.Rooms;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>
/// The opt-in modern round-start binding: the start request, the loading reports and the countdown and start
/// packets, all through packet tables and names matched to kr-812's classes. It is mounted with the typed
/// room binding. The relay offer and the game-side connection report moved to dialect.match-offers; the
/// round's session signature is written through SessionSignatureData like the main-menu probe's reply.
/// </summary>
internal sealed class RoundStartBinding(string protocol, IOpcodeTable table, PacketTables packets, string build,
    IReadOnlyList<int> loadingFlags, ILogger logger)
    : IInboundMessageBinding<RoomStartRequested>, IInboundMessageBinding<RoundObjectAddRequested>,
      IInboundMessageBinding<RoundLoadReported>,
      IRoundSeriesEntrySender, IMessageSender<RoundCountdownStarted>, IMessageSender<RoundStarted>,
      IMessageSender<RoundObjectAddAcknowledged>
{
    private static readonly string[] Structures =
    [
        "RoomEnterGameRequest", "RoundObjectAddRequest", "RoundMapLoadingReport",
        "RoomEnterGame", "RoundEverybodyLoadingComplete", "RoundCountdown",
        "RoundStartGame", "RoundClockUnbounded", "SessionSignatureData",
    ];

    private static readonly string[] ServerNames =
    [
        "gspRoomSetSlot", "gspSessionSignatureData", "gspRoomEnterGame",
        "gspGameEverybodyLoadingComplete", "gspGameCountDown", "gspGameStartGame", "gspGameClockUnbounded_",
    ];

    // The S2C 510 stage record's room state for an ordinary map (kr-1256 sub_9D9930 case 0x1FE copies it to
    // session+12); a mission names its mode there instead.
    private const int MapRoomState = 3;

    private IReadOnlyDictionary<string, ushort> _s2c = new Dictionary<string, ushort>();
    private IGameText? _texts;
    private readonly RoundSeriesFields? _seriesFields = RoundSeriesFields.Load(packets, build);

    public int RoundsToWin => _seriesFields?.RoundsToWin ?? 2;

    public bool SupportsMode(int modeId) => modeId == (int)GameModeId.Bomb && _seriesFields is not null;

    internal bool MapReportBound { get; init; }

    public string Protocol => protocol;

    internal void Mount(IPluginContext context)
    {
        _texts = context.Services.TryGet<IGameText>(out var texts) ? texts : null;
        _s2c = RoomRoutes.Preflight(table, packets, build, Structures, ServerNames);
        // The start request states its own contract; the round records this binding writes or reads directly are
        // checked here with the exact types it sizes them as: the object word (kr-1256 sub_6DDE20), the loading
        // report's four words (kr-1256 sub_6DC970), the signature's mode word (reader kr-1256 sub_1155B00), the
        // stage record's three strings and room state (kr-1256 sub_10D1D70), the loading gates' three flags
        // (kr-1256 sub_9E1EB0 case 0x249), the countdown word (kr-1256 sub_9E7080) and the start words
        // (kr-1256 sub_9E7570).
        RoomRequests.ValidateLayouts(packets, build);
        PacketLayoutContract.RequireFields(packets, build, "RoundObjectAddRequest", "Round start", ("object_id", "u32"));
        PacketLayoutContract.RequireFields(packets, build, "RoundMapLoadingReport", "Round start",
            ("unknown_4", "u32"), ("unknown_8", "u32"), ("unknown_12", "u32"), ("unknown_16", "u32"));
        PacketLayoutContract.RequireFields(packets, build, "SessionSignatureData", "Round start", ("unknown_4", "u16"));
        PacketLayoutContract.RequireFields(packets, build, "RoomEnterGame", "Round start",
            ("map_key", "wstr"), ("stage_key", "wstr"), ("map_selection", "wstr"), ("room_state", "u32"));
        PacketLayoutContract.RequireFields(packets, build, "RoundEverybodyLoadingComplete", "Round start",
            ("unknown_4", "bool"), ("unknown_5", "bool"), ("unknown_6", "bool"));
        PacketLayoutContract.RequireFields(packets, build, "RoundCountdown", "Round start", ("milliseconds", "u32"));
        PacketLayoutContract.RequireFields(packets, build, "RoundStartGame", "Round start",
            ("unknown_4", "u32"), ("unknown_8", "u32"));
        PacketLayoutContract.RequireEmpty(packets, build, "RoundClockUnbounded", "Round start");
        if (loadingFlags.Count != 3)
            throw new ConfigException("round_loading_flags must hold the three S2C gspGameEverybodyLoadingComplete words.");
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<RoomStartRequested>>(this));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<RoundObjectAddRequested>>(this));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<RoundLoadReported>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<RoomMatchStarting>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<RoundCountdownStarted>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<RoundStarted>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<RoundObjectAddAcknowledged>>(this));

        Bind(context, "gcpRoomReqEnterGame", ReadStart);
        // kr-1256 sub_755330 sends the map report and then the local character's object, as kr-812 sub_661AF0 does.
        Bind(context, "gcpReqAddObject", body =>
            new RoundObjectAddRequested((int)PacketCodec.ReadScalars(packets, build, "RoundObjectAddRequest", body)["object_id"]!));
        // kr-1256 sub_6DC970 writes the same four words as kr-812 sub_50E670 (C2S 701), once the stage loop's
        // one-second wait is over; the words are checked, not interpreted.
        Bind(context, "gcpGameReportMapLoadingComplete", body =>
        {
            _ = PacketCodec.ReadScalars(packets, build, "RoundMapLoadingReport", body);
            return new RoundLoadReported();
        });
        // The game-side relay report (C2S 571 gcpReportRelayConnected) belongs to dialect.match-offers now.
        // The supply or star binding owns the audited map report when enabled; neither report advances loading.
        if (!MapReportBound)
            RoomRoutes.LogOnly(context, protocol, table, logger, "gcpGameReportMapData");
        RoomRoutes.LogOnly(context, protocol, table, logger, "gcpReqRecheckMap");
    }

    private void Bind<T>(IPluginContext context, string name, Func<ReadOnlySpan<byte>, T> decode) where T : class, IMessage =>
        RoomRoutes.Bind(context, protocol, table, logger, name, decode);

    // kr-1256 sub_9D7570 writes the resolved map key, the map record's name and the master's raw selection
    // (possibly a random marker), then the map's rule record.
    private RoomStartRequested ReadStart(ReadOnlySpan<byte> body)
    {
        var fields = PacketCodec.ReadScalars(packets, build, "RoomEnterGameRequest", body);
        return new RoomStartRequested((string)fields["map_key"]!)
        {
            MapName = (string)fields["unknown_36"]!,
            MapSelection = (string)fields["unknown_64"]!,
            TimeLimits = new RoomRoundTimeLimits((int)fields["death_match_time_limit_ms"]!, (int)fields["time_attack_time_limit_ms"]!),
        };
    }

    /// <summary>
    /// Sends one member the start roster, the session signature and the stage enter: every seat's record first,
    /// so kr-1256 sub_C80220 can pick team spawns, then the signature kr-1256 sub_9616A9 compares with the room
    /// mode before the relay join, then the stage record.
    /// </summary>
    public async ValueTask SendAsync(ISession session, RoomMatchStarting message, CancellationToken ct)
    {
        var room = message.Room;
        if (message.Series is { } series)
        {
            if (!SupportsMode(room.ModeId))
                throw new ConfigException("Round-series entry is not supported for this room's mode.");
            _seriesFields!.Validate(series);
            if (series.RoundIndex == 0)
            {
                // kr-1256 sub_9E5E70, cn-774 sub_8DD8D0 and tw-454 sub_A5D750 gate the entry index on bit 1.
                var setting = RoomProjection.Map(RoomProjection.Session(packets, build, room)["game_setting"]);
                setting[_seriesFields.SettingsFlags] = Convert.ToInt32(setting[_seriesFields.SettingsFlags]) | 1;
                await session.SendAsync(table.S2C("gspRoomChangeGameSetting"),
                    PacketCodec.Write(packets, build, "GameSetting", setting), ct).ConfigureAwait(false);
            }
        }
        var profiles = RoomProjection.Members(room);
        foreach (var seat in profiles)
            await session.SendAsync(S2C("gspRoomSetSlot"), RoomRecords.SeatUpdate(packets, build, seat, profiles, session, _texts), ct).ConfigureAwait(false);
        await session.SendAsync(S2C("gspSessionSignatureData"),
            RoundEntryWire.WriteSignature(packets, build, room.ModeId), ct).ConfigureAwait(false);
        var stage = message.StageKey.Length > 0 ? message.StageKey : message.MapKey;
        await session.SendAsync(S2C("gspRoomEnterGame"), RoundEntryWire.Write(packets, build,
            unknown0: 0, mapKey: message.MapKey, roomState: message.IsMission ? room.ModeId : MapRoomState,
            stageKey: stage, mapSelection: message.MapKey, series: message.Series, mapSeed: message.MapSeed), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends the loading gates and the countdown (kr-1256 sub_9E1EB0 cases 0x249 and 0x25F). Case 0x249 stores the
    /// first word at +1708 (relay selected) and the second at +1709 (peer selected); the third is the packet
    /// optimizer switch the debug overlay prints as "Optimize Packet" (kr-1256 sub_7B5A00). A seat playing over its
    /// game connection gets both selections cleared, so kr-1256 sub_95B910 sends its round packets there.
    /// </summary>
    public ValueTask SendAsync(ISession session, RoundCountdownStarted message, CancellationToken ct) =>
        RoundStartSender.SendCountdownAsync(packets, build, table, loadingFlags, session, message, ct);

    /// <summary>
    /// Sends the round start (kr-1256 sub_9E7570 reads two words, the first the round length); a round without a
    /// limit carries the client's own no-limit length and then its empty unlimited-clock notification.
    /// </summary>
    public ValueTask SendAsync(ISession session, RoundStarted message, CancellationToken ct) =>
        RoundStartSender.SendStartedWithClockNotificationAsync(packets, build, table, session, message, ct);

    /// <summary>Acknowledges the requester's own character add with a zero countdown (kr-1256 sub_9E7080).</summary>
    public ValueTask SendAsync(ISession session, RoundObjectAddAcknowledged message, CancellationToken ct) =>
        Send(session, "gspGameCountDown", "RoundCountdown", new() { ["milliseconds"] = 0 }, ct);

    private ushort S2C(string name) => _s2c[name];

    private ValueTask Send(ISession session, string name, string structure, Dictionary<string, object?> values, CancellationToken ct) =>
        session.SendAsync(S2C(name), PacketCodec.Write(packets, build, structure, values), ct);
}
