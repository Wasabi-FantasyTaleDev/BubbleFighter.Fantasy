using System.Buffers;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Packets;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>
/// The legacy waiting room as typed messages: decodes the list, create, join, ready, map pick,
/// host start and leave requests and encodes the room, seat, master, refusal and start packets
/// from the build's <see cref="RoomSchema"/>. It holds no room state; the play row does.
/// Opcode numbers come from the session profile's table. Packet order follows
/// the client's consuming paths; every layout is the kr-812 reader named beside it.
///
/// </summary>
internal sealed partial class PlayRoomCodec(
    string protocol, string lineage, RoomSchema schema, PlayValues play,
    IOpcodeTableRegistry tables, ILogger logger, PacketTables packetTables)
    : IInboundMessageBinding<RoomCreateRequested>,
      IInboundMessageBinding<RoomJoinRequested>, IInboundMessageBinding<RoomLeaveRequested>,
      IInboundMessageBinding<RoomReadyRequested>, IInboundMessageBinding<RoomTeamChangeRequested>,
      IInboundMessageBinding<RoomStartRequested>, IInboundMessageBinding<RoomSettingsChangeRequested>,
      IMessageSender<RoomSettingsChanged>, IInboundMessageBinding<RoomRoundSettingsChangeRequested>,
      IInboundMessageBinding<RoomMasterChangeRequested>, IMessageSender<RoomMasterChangeReplied>,
      IInboundMessageBinding<RoomChatSent>, IMessageSender<RoomChatReceived>, IInboundMessageBinding<RoomSeatLockRequested>,
      IInboundMessageBinding<RoomSessionChatSent>, IMessageSender<RoomSessionChatReceived>,
      IMessageSender<RoomSeatLockChanged>, IInboundMessageBinding<RoomKickRequested>, IMessageSender<RoomMemberKicked>,
      IInboundMessageBinding<RoundLoadReported>,
      IInboundMessageBinding<RoundRespawnRequested>,
      IInboundMessageBinding<RoundDeathReported>, IInboundMessageBinding<RoundKillCreditReported>,
      IInboundMessageBinding<RoundFinishingBlowReported>, IInboundMessageBinding<RoundStarReported>,
      IMessageSender<RoundSpawnOffered>,
      IMessageSender<RoundRespawnWaitChanged>,
      IMessageSender<RoundPlayerLoaded>, IMessageSender<RoundCountdownStarted>, IMessageSender<RoundStarted>,
      IInboundMessageBinding<RoundEndRequested>,
      IMessageSender<RoomEntered>, IMessageSender<RoomSeatUpdated>,
      IMessageSender<RoomSeatVacated>, IMessageSender<RoomMasterChanged>, IMessageSender<RoomJoinRefused>,
      IMessageSender<RoomLeft>, IRoundSeriesEntrySender, IMessageSender<RoundWaveChanged>,
      IInboundMessageBinding<RoundNpcReported>, IMessageSender<RoundNpcAcknowledged>,
      IInboundMessageBinding<TutorialScoreReported>, IMessageSender<TutorialScoreUpdated>
{
    private readonly RoundSeriesFields? _seriesFields = RoundSeriesFields.Load(packetTables, lineage);
    public int RoundsToWin => _seriesFields?.RoundsToWin ?? 2;
    private readonly RoundResultSender _resultSender = new(protocol, packetTables, lineage,
        session => tables.Get(session.Profile.Id), play.Score);
    private RoomRoundSettingFields? _roundSettingFields;
    internal string UdpProtocol { get; init; } = string.Empty;
    internal int UdpKeepaliveMs { get; init; } = 1000;
    internal bool EchoDirectItemEffects { get; init; }
    /// <summary>Gets whether an ordinary round's loading gate selects the match relay for seats still on it.</summary>
    internal bool SelectMatchRelay { get; init; }
    /// <summary>Gets whether the row reads the clients' star-count reports (the <c>star_items</c> option and capability).</summary>
    internal bool StarReports { get; init; }
    /// <summary>Gets whether the row reads the Captain-kill report kind (the <c>captain_mode</c> option and capability).</summary>
    internal bool CaptainKills { get; init; }
    internal Func<ISession, RoomReadyRequested, CancellationToken, ValueTask<bool>>? ReadyRequestFirst { get; init; }

    // The first number of the peer gameplay feed.

    public string Protocol => protocol;

    /// <summary>Reports the independently audited bomb series entry capability, not merely bomb-state support.</summary>
    public bool SupportsMode(int modeId) => modeId == (int)GameModeId.Bomb && _seriesFields is not null;

    /// <summary>
    /// Gets what sees a load report before the round does, or null: a build whose one-seat tutorial
    /// answers the report itself takes the ones that belong to a tutorial.
    ///
    /// </summary>
    internal Func<ISession, InboundPacket, CancellationToken, ValueTask<bool>>? LoadReportFirst { get; init; }

    /// <summary>
    /// Gets the channel the plaza's scene move places the entrant in: the row's
    /// <see cref="RoomsOptions.PlayerChannel"/>, 60 ("Free") unless the row says otherwise.
    /// </summary>
    internal int PlayerChannel { get; init; } = RoomsOptions.FreeChannel;

    /// <summary>Gets the native tutorial's stage-end handler, consulted before the typed multiplayer round.</summary>
    internal Func<ISession, InboundPacket, CancellationToken, ValueTask<bool>>? EndStageFirst { get; init; }

    /// <summary>Gets the tutorial auto-start callback, consulted before ordinary host-start admission.</summary>
    internal Func<ISession, InboundPacket, CancellationToken, ValueTask<bool>>? StartRequestFirst { get; init; }

    /// <summary>Gets the tutorial departure handler, including pending native auto-start admission.</summary>
    internal Func<ISession, InboundPacket, CancellationToken, ValueTask<bool>>? LeaveRequestFirst { get; init; }

    /// <summary>Gets the cleanup called when a player creates or joins a multiplayer room.</summary>
    internal Func<ISession, CancellationToken, ValueTask>? MultiplayerRoomEntering { get; init; }

    /// <summary>Registers the request opcodes and publishes the bindings and encoders.</summary>
    /// <param name="context">The dialect row's context.</param>
    internal void Mount(IPluginContext context)
    {
        _ = packetTables.Resolve(lineage, "RoundMapLoadingComplete");
        _ = packetTables.Resolve(lineage, "RoundMapLoadingReport");
        _ = packetTables.Resolve(lineage, "RoundEverybodyLoadingComplete");
        _ = packetTables.Resolve(lineage, "RoundCountdown");
        _ = packetTables.Resolve(lineage, "RoundStartGame");
        _ = packetTables.Resolve(lineage, "TutorialMasterSlot");
        _ = packetTables.Resolve(lineage, "NpcWave");
        MountWaitingControls(context);
        var services = context.Services;
        var messages = services.Get<IMessageRouter>();
        var router = services.Get<IPacketRouter>();
        var table = tables.ForProtocol(protocol);
        if (play.Score.HordeStageOutcome)
        {
            _ = table.S2C("gspHordeStageCleared_");
            _ = table.S2C("gspHordeStageFailed_");
        }
        _ = packetTables.Resolve(lineage, "RoundEndGame");
        _ = packetTables.Resolve(lineage, "RoundTimeOverTotals");
        _ = packetTables.Resolve(lineage, "RoomEnterGame");
        _ = packetTables.Resolve(lineage, "RoomEnterGameHeader");
        _ = packetTables.Resolve(lineage, "RoomEnterGameTail");
        foreach (var structure in new[] { "RoundSpawnOffer", "RoundSpawnAcceptance", "RoundSpawnConfirmation", "RoundRespawnWaitChange", "RoundRespawnPrompt", "RoundDeathReport", "RoundSpawnIndexRequest" })
            _ = packetTables.Resolve(lineage, structure);
        foreach (var structure in new[] { "RoomListReply", "RoomSeatUpdate", "RoomSeatLeave", "RoomListRequest", "RoomJoinRequest", "RoomMoveInto", "RoomSessionInit", "RoomJoinResult", "RoomChatReceived", "RoomChatRequest", "RoomSeatLock", "RoomKickRequest", "RoomMemberKicked" })
            _ = packetTables.Resolve(lineage, structure);
        _ = packetTables.Resolve(lineage, "RoomReadyRequest");
        _ = packetTables.Resolve(lineage, "RoomEnterGameRequest");
        _ = packetTables.Resolve(lineage, "RoomGameSettingChangeRequest");
        _ = SettingChangeFields.Require(packetTables, lineage);
        if (play.NpcWaves)
        {
            _ = table.S2C("Unknown908");
            _ = table.S2C("gspRepWaveAllSpawnMobs");
            context.Effect(services.RegisterMany<IMessageSender<RoundWaveChanged>>(this));
            context.Effect(services.RegisterMany<IInboundMessageBinding<RoundNpcReported>>(this));
            context.Effect(services.RegisterMany<IMessageSender<RoundNpcAcknowledged>>(this));
        }
        context.Effect(services.RegisterMany<IInboundMessageBinding<RoomCreateRequested>>(this));
        context.Effect(services.RegisterMany<IInboundMessageBinding<RoomLeaveRequested>>(this));
        context.Effect(services.RegisterMany<IInboundMessageBinding<RoomStartRequested>>(this));
        context.Effect(services.RegisterMany<IInboundMessageBinding<RoundLoadReported>>(this));
        context.Effect(services.RegisterMany<IMessageSender<RoundPlayerLoaded>>(this));
        context.Effect(services.RegisterMany<IMessageSender<RoundCountdownStarted>>(this));
        context.Effect(services.RegisterMany<IMessageSender<RoundStarted>>(this));
        context.Effect(services.RegisterMany<IInboundMessageBinding<RoundEndRequested>>(this));
        context.Effect(services.RegisterMany<IMessageSender<RoundEnded>>(_resultSender));
        context.Effect(services.RegisterMany<IInboundMessageBinding<RoundRespawnRequested>>(this));
        var respawn = new RoundRespawnBinding(protocol, table, packetTables, lineage, tables);
        respawn.Mount(context);
        context.Effect(services.RegisterMany<IInboundMessageBinding<RoundDeathReported>>(this));
        context.Effect(services.RegisterMany<IMessageSender<RoundSpawnOffered>>(this));
        // The taunt only moves a wait the round itself times, and only where the build can be told.
        var tauntEnd = play.Wire.ServerRevives && table.Find(PacketDirection.ServerToClient, "gspGameExtendRespawn") is not null
            ? table.Find(PacketDirection.ClientToServer, "gppHumiliationEnd")
            : null;
        if (tauntEnd is not null)
            context.Effect(services.RegisterMany<IMessageSender<RoundRespawnWaitChanged>>(this));
        // The gameplay feed is the shared RoundFeedBinding; this build's data selects how it is read.
        var relayProtocol = protocol[..protocol.LastIndexOf('/')] + "/relay";
        new RoundFeedBinding(protocol, relayProtocol, table, tables.ForProtocol(relayProtocol), packetTables, lineage)
        {
            EchoDirectItemEffects = EchoDirectItemEffects, AcceptRelayFrames = SelectMatchRelay, TauntEnd = tauntEnd,
            UdpProtocol = UdpProtocol, UdpKeepaliveMs = UdpKeepaliveMs,
        }.Mount(context);
        context.Effect(services.RegisterMany<IMessageSender<RoomEntered>>(this));
        context.Effect(services.RegisterMany<IMessageSender<RoomSeatUpdated>>(this));
        context.Effect(services.RegisterMany<IMessageSender<RoomSeatVacated>>(this));
        context.Effect(services.RegisterMany<IMessageSender<RoomMasterChanged>>(this));
        context.Effect(services.RegisterMany<IMessageSender<RoomLeft>>(this));
        context.Effect(services.RegisterMany<IMessageSender<RoomMatchStarting>>(this));
        // The room list is the shared codec's; a legacy row shows the room's native game setting. A table that does
        // not name the list yet leaves it unbound and says so, as for the other play-room requests.
        if (table.Find(PacketDirection.ClientToServer, "gcpReqListSession") is { } listRequest
            && table.Find(PacketDirection.ServerToClient, "gspRepListSession") is { } listReply)
            new RoomListCodec(protocol, listRequest, listReply, packetTables, lineage, logger,
                room => GameSetting(room.BuildSettings)).Mount(context);
        else
            logger.LogWarning("dialect.rooms: {Protocol} does not name the room list; its rooms cannot list until the table does.", protocol);

        // A request every build with play rooms has. A table that does not name it yet leaves that
        // one request unbound and says so loudly; it must not keep the other builds from starting.
        void On<T>(string name, Func<ISession, ReadOnlySpan<byte>, T> decode) where T : IMessage
        {
            if (table.Find(PacketDirection.ClientToServer, name) is { } opcode)
                BindRequest(context, opcode, name, decode);
            else
                logger.LogWarning("dialect.rooms: {Protocol} does not name {Name}; its rooms cannot use it until the table does.", protocol, name);
        }

        // A request some builds do not have (no such packet class in the client): bound where the table names it.
        void OnWhereNamed<T>(string name, Func<ISession, ReadOnlySpan<byte>, T> decode) where T : IMessage
        {
            if (table.Find(PacketDirection.ClientToServer, name) is { } opcode)
                BindRequest(context, opcode, name, decode);
            else
                logger.LogInformation("dialect.rooms: {Protocol} has no {Name}; not bound.", protocol, name);
        }


        On("gcpReqCreateSession", (session, body) =>
        {
            var request = PlazaRoomWire.ReadCreateRoom(body, schema);
            return new RoomCreateRequested(request.Title, request.Password, request.Mode, request.MapKey,
                new RoomBuildSettings(lineage, request.GameSetting));
        });
        On("gcpReqLeaveSession", (_, body) =>
        {
            if (body.Length != 0)
                throw new PacketFormatException($"gcpReqLeaveSession must be empty; got {body.Length} byte(s).", 0);
            return new RoomLeaveRequested();
        });
        // kr-812 sub_6711B0 and id-400 sub_4AC990 send an empty in-game leave request. It shares lobby
        // departure and tutorial cleanup.
        OnWhereNamed("gcpRoomReqLeaveGame", (_, body) =>
        {
            if (body.Length != 0)
                throw new PacketFormatException($"gcpRoomReqLeaveGame must be empty; got {body.Length} byte(s).", 0);
            return new RoomLeaveRequested();
        });
        // The master's start click: the request carries the map its client resolved, and the answer is
        // gspRoomEnterGame to the room.
        On("gcpRoomReqEnterGame", (_, body) =>
            new RoomStartRequested(PlazaRoomWire.ReadRoomReqEnterGame(packetTables, lineage, body).MapKey));
        On("gcpGameReportMapLoadingComplete", (_, body) =>
        {
            TutorialWire.ReadMapLoadingComplete(packetTables, lineage, body);
            return new RoundLoadReported();
        });
        On("gcpReqEndStage", (_, body) => ReadStageEnd(body));
        // The respawn chain of a two-team round: at death the client sends gcpGameReqRespawn (sender sub_50E7B0) with its
        // character handle and the time it stays dead (3200 there), sends gcpRespawn by itself when
        // that time is over, and comes back when gspRespawn answers.
        OnWhereNamed("gcpGameReqRespawn", (_, body) =>
        {
            var fields = PacketCodec.ReadScalars(packetTables, lineage, "RoundDeathReport", body);
            var handle = (int)fields["actor"];
            var delayMs = (int)fields["delay_ms"];
            return new RoundDeathReported(handle, delayMs, play.Wire.ServerRevives) { ActorSeat = PlayerSeatOf(handle) };
        });
        // gcpRequestRespawnIdx (sender sub_511AB0) asks for the spawn point alone; its second u32 is unknown1.
        OnWhereNamed("gcpRequestRespawnIdx", (_, body) =>
        {
            var handle = (int)PacketCodec.ReadScalars(packetTables, lineage, "RoundSpawnIndexRequest", body)["actor"];
            return new RoundRespawnRequested(handle) { ActorSeat = PlayerSeatOf(handle) };
        });
        // The shared respawn binding reads the body; this family's route keeps its malformed-body handling.
        OnWhereNamed("gcpRespawn", (_, body) => respawn.Read(body));
        if (play.HostStart)
        {
            On("gcpUserHostStart", (_, body) =>
            {
                PlazaRoomWire.ReadUserHostStart(body);
                return new RoomStartRequested();
            });
        }
        MountPlaza(context);
        MountScoreReports(context);
    }

    /// <summary>Decodes the waiting selection while retaining the master's complete native settings change.</summary>
    internal static RoomSettingsChangeRequested ReadSettingsChange(PacketTables tables, string build, ReadOnlySpan<byte> body)
    {
        var change = PlazaRoomWire.ReadChangeGameSetting(tables, build, body);
        // kr-812 sub_5C2B80 copies session+188 separately from the stage Directory at session+132.
        return new RoomSettingsChangeRequested(change.Mode, change.MapKey,
            new RoomBuildSettings(build, default, body.ToArray()));
    }

    /// <summary><c>gspRoomChangeGameSetting</c> (reader <c>sub_80A4F0</c>): the room's <c>GameSetting</c>.</summary>
    public ValueTask SendAsync(ISession session, RoomSettingsChanged message, CancellationToken ct)
    {
        var settings = GameSetting(message.Room.BuildSettings);
        if (settings.IsEmpty)
            settings = PlazaRoomWire.ApplyGameSettingMode(schema,
                PlazaRoomWire.ApplyGameSettingChange(schema, packetTables, lineage, default, default), message.Room.ModeId);
        return session.SendAsync(S2C(session, "gspRoomChangeGameSetting"), settings, ct);
    }

    /// <summary><c>gspGotoRoom</c> kind 2 (reader <c>sub_692A40</c>) with every slot of the room.</summary>
    public ValueTask SendAsync(ISession session, RoomEntered message, CancellationToken ct) =>
        session.SendAsync(S2C(session, "gspGotoRoom"), RoomCodec.WriteRoomEnvelope(packetTables, lineage, schema,
            Values(message.Room, sessionState: 2) with { LocalSlot = message.YourSlot, PartyTransition = message.PartyTransition }), ct);

    /// <summary><c>gspRoomSetSlot</c> (reader <c>sub_80C1B0</c>): slot index, <c>UserSlot</c>, <c>EquippedItemList</c>.</summary>
    public ValueTask SendAsync(ISession session, RoomSeatUpdated message, CancellationToken ct)
    {
        var body = RoomCodec.WriteSeatEnvelope(packetTables, lineage, schema, message.Seat.Slot, Slot(message.Seat));
        return session.SendAsync(S2C(session, "gspRoomSetSlot"), body, ct);
    }

    /// <summary><c>gspRoomLeaveSlot</c> (reader <c>sub_8104E0</c>): slot index and the leaving player's <c>UserSlot</c>.</summary>
    public ValueTask SendAsync(ISession session, RoomSeatVacated message, CancellationToken ct) =>
        session.SendAsync(S2C(session, "gspRoomLeaveSlot"), LeaveSlot(message.Seat), ct);

    /// <summary><c>gspRoomSetMasterSlotIdx</c> (reader <c>sub_80A910</c>): the new master's slot index.</summary>
    public ValueTask SendAsync(ISession session, RoomMasterChanged message, CancellationToken ct)
    {
        var body = TutorialWire.WriteMasterSlot(packetTables, lineage, message.MasterSlot);
        return session.SendAsync(S2C(session, "gspRoomSetMasterSlotIdx"), body, ct);
    }

    /// <summary>Echoes the target nickname and native delegation result through the audited reply table.</summary>
    public ValueTask SendAsync(ISession session, RoomMasterChangeReplied message, CancellationToken ct) =>
        session.SendAsync(S2C(session, "gspRoomRepChangeMaster"),
            PacketCodec.Write(packetTables, lineage, "RoomMasterChangeReply", new Dictionary<string, object?>
            { ["nickname"] = message.Nickname, ["result"] = message.Result }), ct);

    /// <summary><c>gspRepJoinSession</c> (reader <c>sub_80A910</c>): the result code the lobby switches on.</summary>
    public ValueTask SendAsync(ISession session, RoomJoinRefused message, CancellationToken ct)
    {
        var body = PacketCodec.Write(packetTables, lineage, "RoomJoinResult", new Dictionary<string, object?>
        {
            ["result"] = PlazaRoomWire.JoinResultCode(message.Reason),
        });
        return session.SendAsync(S2C(session, "gspRepJoinSession"), body, ct);
    }

    /// <summary>
    /// The leaver's own packets, as recorded at s2c#1768-1769: their slot leaves and the session
    /// falls back to the main-menu kind. The plaza burst that follows (s2c#1770-1800) is the
    /// <see cref="PlazaEntered"/> encoder's, sent by the flow once the leaver is back in the plaza.
    ///
    /// </summary>
    public async ValueTask SendAsync(ISession session, RoomLeft message, CancellationToken ct)
    {
        await session.SendAsync(S2C(session, "gspRoomLeaveSlot"), LeaveSlot(message.Seat), ct).ConfigureAwait(false);
        var values = play.Wire.RestateLeftRoom && message.Room is { } left
            // The room just left under the main-menu kind: no number, no state, no master, the leaver alone.
            ? Values(left, sessionState: 0) with
            {
                Kind = RoomCodec.MainMenuKind, Slots = [Slot(message.Seat)], RoomId = 0, MasterSlot = 0,
            }
            : new RoomValues(RoomCodec.MainMenuKind, [Slot(message.Seat)]) { Play = true };
        var body = RoomCodec.WriteSessionEnvelope(packetTables, lineage, schema, values);
        await session.SendAsync(S2C(session, "gspRoomSetSession"), body, ct).ConfigureAwait(false);
        if (_plazaMounted)
            return;
        // A build whose plaza is not bound still needs the move to the lobby and its own slot
        // (s2c#2114-2117), which the plaza burst carries elsewhere.
        await session.SendAsync(S2C(session, "gspGotoLobby"), RoomCodec.WriteSceneTransition(packetTables, lineage), ct).ConfigureAwait(false);
        var own = new BubbleFighter.Fantasy.Contracts.Lobby.MainMenuRoomOpened(message.Seat.UserNum, message.Seat.Nickname,
            message.Seat.CharacterItemId, message.Seat.EquippedItemIds.Count > 0 ? message.Seat.EquippedItemIds : [message.Seat.CharacterItemId])
        {
            ColorIndex = message.Seat.ColorIndex,
            ItemAttributes = message.Seat.ItemAttributes,
        };
        await session.SendAsync(S2C(session, "gspRoomSetSlot"), PlazaRoomWire.WriteSetSlot(own, packetTables, lineage, schema, slot: 0, ready: false), ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Installs every seated player's start position before <c>gspRoomEnterGame</c>
    /// (reader <c>kr-812 sub_80AE90</c>) with the round's map key. The
    /// third u32 after the key is 3; the client stores it as the session state.
    ///
    /// </summary>
    public async ValueTask SendAsync(ISession session, RoomMatchStarting message, CancellationToken ct)
    {
        if (message.Series is { } series)
        {
            if (!SupportsMode(message.Room.ModeId))
                throw new ConfigException("Round-series entry is not supported for this room's mode.");
            _seriesFields!.Validate(series);
            if (series.RoundIndex == 0)
            {
                var settings = SettingsAsSent(message.Room.BuildSettings);
                if (settings.IsEmpty)
                    settings = PlazaRoomWire.ApplyGameSettingMode(schema,
                        PlazaRoomWire.ApplyGameSettingChange(schema, packetTables, lineage, default, default), message.Room.ModeId);
                await session.SendAsync(S2C(session, "gspRoomChangeGameSetting"),
                    PlazaRoomWire.ApplyGameSettingSeries(schema, settings.Span, _seriesFields!, series.RoundIndex), ct).ConfigureAwait(false);
            }
        }
        foreach (var seat in message.Room.Seats)
        {
            var body = RoomCodec.WriteSeatEnvelope(packetTables, lineage, schema, seat.Slot,
                Slot(seat) with { SpawnIndex = seat.Slot });
            await session.SendAsync(S2C(session, "gspRoomSetSlot"), body, ct).ConfigureAwait(false);
        }
        var mapKey = message.MapKey.Length > 0 ? message.MapKey : PlazaRoomWire.DefaultMapKey;
        // The client's own start (kr-812 sub_816300) enters the stage at session+132, GameSetting current_map. The
        // room's key at session+188 names a pirate mission's NPC table, which the stage loader cannot open (the
        // client's Lua alert, id-400 sub_85CFE0), so a round the server starts on the room's key enters its stage.
        if (mapKey == message.Room.MapKey && StageOf(message.Room) is { Length: > 0 } stage)
            mapKey = stage;
        // The relay join (kr-812 sub_810C20 case 1) compares the signature's mode word, stored by kr-812 sub_8071A0 at
        // GameInfo+21064, with the round mode at GameInfo+96; a mismatch arms kr-812 sub_8E51A0, which quits silently
        // within ten seconds. Without this packet the word keeps its constructor value 0xFFFF.
        await SendWhereNamedAsync(session, "gspSetSessionSigData",
            () => RoundEntryWire.WriteSignature(packetTables, lineage, message.Room.ModeId), ct)
            .ConfigureAwait(false);
        var scoreboard = EntryScoreboardValues(packetTables, lineage, message.Room);
        await session.SendAsync(S2C(session, "gspRoomEnterGame"),
            RoundEntryWire.Write(packetTables, lineage, unknown0: 0, mapKey: mapKey, roomState: 3,
                scoreboardValues: scoreboard, series: message.Series, mapSeed: message.MapSeed), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The stage the room's settings select (GameSetting current_map), or empty without this build's settings.
    /// It is read before the unchosen map is presented, so a round the server starts in a room whose map was never
    /// chosen keeps the creator's startup map rather than a random marker the server cannot load.
    ///
    /// </summary>
    private string StageOf(RoomView room)
    {
        var block = SettingsAsSent(room.BuildSettings);
        return block.IsEmpty ? "" : PlazaRoomWire.ReadGameSettingCurrentMap(block.Span, schema);
    }

    /// <summary>Supplies occupied starting rows only when the entry table carries a native score object.</summary>
    internal static IReadOnlyDictionary<string, object?>? EntryScoreboardValues(PacketTables tables, string build, RoomView room)
    {
        if (!tables.Resolve(build, "RoomEnterGame").Any(field => field.Type == "ScoreData"))
            return null;
        // cn-324 sub_6EED20 and id-400 sub_786480 install the received rows rather than deriving a roster from slots.
        var rows = room.Seats.Select(seat => new RoundScoreboardRow(seat.Slot, seat.Nickname,
            seat.Team == 0 ? 2 : seat.Team, seat.Level, new RoundSeatStats(seat.Slot, 0, 0, 0, 0), null)).ToArray();
        return RoundResultWire.ScoreboardValues(tables, build, new RoundScoreboard(room.RoomId, 0, [0, 0], rows));
    }

    /// <summary><c>gspGameMapLoadingComplete</c> (reader <c>sub_80A910</c>): the seat index of the player who loaded.</summary>
    public ValueTask SendAsync(ISession session, RoundPlayerLoaded message, CancellationToken ct) =>
        session.SendAsync(S2C(session, "gspGameMapLoadingComplete"), TutorialWire.WriteMapLoadingComplete(packetTables, lineage, message.Slot), ct);

    /// <summary>Sends the shared loading gates and countdown using this build's native packet sequence.</summary>
    public ValueTask SendAsync(ISession session, RoundCountdownStarted message, CancellationToken ct) =>
        RoundStartSender.SendCountdownAsync(packetTables, lineage, tables.Get(session.Profile.Id),
            play.Wire.PreCountDownFirst, SelectMatchRelay, session, message, ct);

    /// <summary>Sends the shared match clock and native start words using this build's packet table.</summary>
    public ValueTask SendAsync(ISession session, RoundStarted message, CancellationToken ct) =>
        RoundStartSender.SendStartedAsync(packetTables, lineage, tables.Get(session.Profile.Id), session, message, ct);

    /// <summary>Sends the room chat line using the client's audited string and suffix layout.</summary>
    public ValueTask SendAsync(ISession session, RoomChatReceived message, CancellationToken ct) =>
        session.SendAsync(S2C(session, "gspRoomReceiveChatModuleMsg"),
            RoomChatBinding.Write(packetTables, lineage, message), ct);

    /// <summary>
    /// Sends a waiting-room chat line as S2C gspRoomReceiveChatMsg (packets/room-control.yaml RoomSessionChatReceived);
    /// the client names the speaker by the slot and colours a team-only line itself (cn-717 sub_885700).
    /// </summary>
    public ValueTask SendAsync(ISession session, RoomSessionChatReceived message, CancellationToken ct) =>
        session.SendAsync(S2C(session, "gspRoomReceiveChatMsg"), WriteSessionChat(packetTables, lineage, message), ct);

    /// <summary>Writes one RoomSessionChatReceived body; the unproven string, colour word and flag keep their defaults.</summary>
    internal static byte[] WriteSessionChat(PacketTables packetTables, string build, RoomSessionChatReceived message) =>
        PacketCodec.Write(packetTables, build, "RoomSessionChatReceived", new Dictionary<string, object?>
        {
            ["slot"] = checked((ushort)message.Slot), ["team_only"] = message.TeamOnly, ["text"] = message.Text,
        });

    /// <summary>Sends the native slot word and closed bool32 (kr-812 sub_93C4E0).</summary>
    public ValueTask SendAsync(ISession session, RoomSeatLockChanged message, CancellationToken ct) =>
        session.SendAsync(S2C(session, "gspRoomOpenCloseSlot"),
            PacketCodec.Write(packetTables, lineage, "RoomSeatLock", new Dictionary<string, object?>
            {
                ["slot"] = message.Slot,
                ["closed"] = message.Closed,
            }), ct);

    /// <summary>Announces removal with the two native nicknames and success word (kr-812 sub_80AE00).</summary>
    public ValueTask SendAsync(ISession session, RoomMemberKicked message, CancellationToken ct) =>
        session.SendAsync(S2C(session, "gspRepKickFromSession"),
            PacketCodec.Write(packetTables, lineage, "RoomMemberKicked", new Dictionary<string, object?>
            {
                ["by_nickname"] = message.ByNickname,
                ["nickname"] = message.Nickname,
                ["result"] = 0,
            }), ct);

    /// <summary>
    /// <c>gspRequestRespawnIdx</c> (reader <c>kr-812 sub_503AD0</c>, handler <c>kr-812 sub_51E4F0</c>): the handle,
    /// the spawn index and a flag. With the flag set the client answers <c>gcpRespawn</c> carrying
    /// the same two values, which is what makes this packet an offer.
    ///
    /// </summary>
    public ValueTask SendAsync(ISession session, RoundSpawnOffered message, CancellationToken ct)
    {
        // A build whose round brings the player back has gspGameRespawnCharacter (cn-324 handler
        // sub_472D60): it carries the handle alone and the client answers gcpRespawn with the spawn
        // point it picks for the player's team, so the offered index is not sent.
        if (play.Wire.ServerRevives && !message.IndexRequested
            && tables.Get(session.Profile.Id).Find(PacketDirection.ServerToClient, "gspGameRespawnCharacter") is { } prompt)
            return session.SendAsync(prompt, WriteRespawnPrompt(packetTables, lineage, message.Actor), ct);
        return session.SendAsync(S2C(session, "gspRequestRespawnIdx"),
            WriteRespawnIndexRequest(packetTables, lineage, message.Actor, message.SpawnIndex), ct);
    }

    /// <summary>
    /// <c>gspGameExtendRespawn</c> (cn-324 handler <c>sub_472CA0</c>): the handle and a signed number
    /// of milliseconds added to the start of the character's death timer (<c>sub_8A3E50</c>), so a
    /// negative value shortens the wait.
    ///
    /// </summary>
    public ValueTask SendAsync(ISession session, RoundRespawnWaitChanged message, CancellationToken ct) =>
        session.SendAsync(S2C(session, "gspGameExtendRespawn"), WriteExtendRespawn(packetTables, lineage, message.Actor, message.DeltaMs), ct);

    /// <summary>Writes the audited actor and signed respawn timer adjustment.</summary>
    internal static byte[] WriteExtendRespawn(PacketTables tables, string build, int handle, int deltaMs) =>
        PacketCodec.Write(tables, build, "RoundRespawnWaitChange", new Dictionary<string, object?>
        {
            ["actor"] = handle, ["delta_ms"] = deltaMs,
        });

    /// <summary>Writes an actor/index offer with the native request flag set.</summary>
    internal static byte[] WriteRespawnIndexRequest(PacketTables tables, string build, int handle, int spawnIndex) =>
        WriteSpawn(tables, build, "RoundSpawnOffer", handle, spawnIndex);

    /// <summary>Writes the actor reference for a native client-selected respawn prompt.</summary>
    internal static byte[] WriteRespawnPrompt(PacketTables tables, string build, int handle) =>
        PacketCodec.Write(tables, build, "RoundRespawnPrompt", new Dictionary<string, object?> { ["actor"] = handle });

    private static byte[] WriteSpawn(PacketTables tables, string build, string structure, int handle, int spawnIndex) =>
        PacketCodec.Write(tables, build, structure, new Dictionary<string, object?>
        {
            ["actor"] = handle, ["spawn_index"] = spawnIndex,
        });


    // The creator's own GameSetting goes back as sent when it follows this build's layout, with the
    // master's latest change applied; empty leaves the block to the build's table. Until the master changes
    // the settings, its map is the client's startup default and is shown as the all-random entry.
    private ReadOnlyMemory<byte> GameSetting(RoomBuildSettings? settings)
    {
        var block = SettingsAsSent(settings);
        if (block.IsEmpty)
            return block;
        return PlazaRoomWire.PresentUnchosenMap(schema, block.Span, play.Wire.UnchosenMapKey, play.Wire.UnchosenMapTheme,
            createRequest: settings!.Change.IsEmpty, play.Wire.UnchosenDifficultyMode, play.Wire.UnchosenDifficultyMapKey);
    }

    // The creator's GameSetting with the master's latest change, before the unchosen map is presented.
    private ReadOnlyMemory<byte> SettingsAsSent(RoomBuildSettings? settings)
    {
        if (settings is null || settings.Lineage != lineage)
            return default;
        var block = settings.Change.IsEmpty
            ? settings.Bytes
            : PlazaRoomWire.ApplyGameSettingChange(schema, packetTables, lineage, settings.Bytes.Span, settings.Change.Span);
        if (settings.RoundSelections.Count > 0)
        {
            var projection = _roundSettingFields
                ?? throw new InvalidOperationException("Round selections require an audited packet-field projection.");
            if (block.IsEmpty)
                block = PlazaRoomWire.ApplyGameSettingChange(schema, packetTables, lineage, default, default);
            foreach (var selection in settings.RoundSelections)
            {
                var decoded = projection.Read(selection.Bytes.Span);
                if (decoded.Index != selection.Index)
                    throw new InvalidOperationException("Retained round selection index disagrees with its native body.");
                block = projection.Apply(block.Span, selection.Index, decoded.First, decoded.Second);
            }
        }
        // A mode the server set itself lies over what the creator's block and the master's change say.
        return settings.Mode is { } mode && !block.IsEmpty
            ? PlazaRoomWire.ApplyGameSettingMode(schema, block.Span, mode)
            : block;
    }

    private ushort S2C(ISession session, string name) => tables.Get(session.Profile.Id).S2C(name);

    internal static RoundEndRequested ReadStageEnd(ReadOnlySpan<byte> body)
    {
        // cn-324 sub_4138E0 reports successful waves as 1; sub_413970 and sub_52DEA0 report failure as 0.
        if (body.Length != 4)
            throw new PacketFormatException($"gcpReqEndStage must be 4 bytes; got {body.Length}.", 0);
        var result = BitConverter.ToUInt32(body);
        return new RoundEndRequested { StageSucceeded = result <= 1 ? result == 1 : null };
    }

    private byte[] LeaveSlot(RoomMemberView seat) =>
        RoomCodec.WriteSeatEnvelope(packetTables, lineage, schema, seat.Slot,
            Slot(seat) with { Occupied = false, Departed = true, EquippedItemIds = [], ItemAttributes = [], Ready = false }, leaving: true);

    private RoomValues Values(RoomView room, int sessionState)
    {
        var slots = new RoomSlotValues[room.MaxSeats];
        for (var i = 0; i < slots.Length; i++)
            slots[i] = RoomSlotValues.Empty(room.ClosedSlots.Contains(i));
        foreach (var seat in room.Seats)
            slots[seat.Slot] = Slot(seat);
        var settings = GameSetting(room.BuildSettings);
        return new RoomValues(RoomCodec.PlayRoomKind, slots)
        {
            Play = true, RoomId = room.RoomId, Title = room.Name, SessionState = sessionState,
            OpenSeats = room.MaxSeats - room.ClosedSlots.Count, Mode = room.ModeId, MasterSlot = room.MasterSlot,
            HasPassword = room.HasPassword,
            MapName = room.MapKey, CurrentMap = room.MapKey, GameSetting = settings,
        };
    }

    // A seat with no team assignment carries 2; the client compares the byte with 1 only.
    /// <summary>Writes the wave index consumed by cn-324 sub_413A00 / cn-324 sub_413E20.</summary>
    public ValueTask SendAsync(ISession session, RoundWaveChanged message, CancellationToken ct) =>
        session.SendAsync(S2C(session, message.Started ? "gspRepStartNewWave" : "gspRepEndWave"),
            NpcWire.WriteStartNewWave(packetTables, lineage, message.Wave), ct);

    private static RoomSlotValues Slot(RoomMemberView seat) =>
        new(true, false, seat.UserNum, seat.Nickname, seat.CharacterItemId,
            seat.EquippedItemIds.Count > 0 ? seat.EquippedItemIds : [seat.CharacterItemId], seat.Ready)
        {
            Team = seat.Team == 0 ? 2 : seat.Team, Level = seat.Level, ColorIndex = seat.ColorIndex,
            ControllerSlot = seat.Slot,
            InventorySelection = seat.InventorySelection,
            ItemAttributes = seat.ItemAttributes,
        };
}
