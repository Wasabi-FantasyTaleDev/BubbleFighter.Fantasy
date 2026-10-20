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
    string protocol, string lineage, RoomSchema schema, PlayBuild play,
    IOpcodeTableRegistry tables, ILogger logger, PacketTables packetTables)
    : IInboundMessageBinding<RoomListRequested>, IInboundMessageBinding<RoomCreateRequested>,
      IInboundMessageBinding<RoomJoinRequested>, IInboundMessageBinding<RoomLeaveRequested>,
      IInboundMessageBinding<RoomReadyRequested>, IInboundMessageBinding<RoomTeamChangeRequested>,
      IInboundMessageBinding<RoomStartRequested>, IInboundMessageBinding<RoomSettingsChangeRequested>,
      IMessageSender<RoomSettingsChanged>, IInboundMessageBinding<RoomRoundSettingsChangeRequested>,
      IInboundMessageBinding<RoomMasterChangeRequested>, IMessageSender<RoomMasterChangeReplied>,
      IInboundMessageBinding<RoomChatSent>, IMessageSender<RoomChatReceived>, IInboundMessageBinding<RoomSeatLockRequested>,
      IMessageSender<RoomSeatLockChanged>, IInboundMessageBinding<RoomKickRequested>, IMessageSender<RoomMemberKicked>,
      IInboundMessageBinding<RoundLoadReported>, IInboundMessageBinding<RoundFeedSent>,
      IInboundMessageBinding<RoundRespawnRequested>, IInboundMessageBinding<RoundSpawnAccepted>,
      IInboundMessageBinding<RoundDeathReported>, IInboundMessageBinding<RoundKillCreditReported>,
      IInboundMessageBinding<RoundFinishingBlowReported>, IInboundMessageBinding<RoundStarReported>,
      IMessageSender<RoundSpawnOffered>, IMessageSender<RoundPlayerRespawned>,
      IInboundMessageBinding<RoundTauntReported>, IMessageSender<RoundRespawnWaitChanged>,
      IMessageSender<RoundPlayerLoaded>, IMessageSender<RoundCountdownStarted>, IMessageSender<RoundStarted>,
      IMessageSender<RoundFeedReceived>, IInboundMessageBinding<RoundEndRequested>, IMessageSender<RoundEnded>,
      IMessageSender<RoundScoreUpdated>, IMessageSender<RoundScoreboard>, IMessageSender<ResultStageAdvanced>,
      IMessageSender<RoomListSnapshot>, IMessageSender<RoomEntered>, IMessageSender<RoomSeatUpdated>,
      IMessageSender<RoomSeatVacated>, IMessageSender<RoomMasterChanged>, IMessageSender<RoomJoinRefused>,
      IMessageSender<RoomLeft>, IMessageSender<RoomMatchStarting>, IMessageSender<RoundWaveChanged>, IRoundResultSequencePolicy
{
    private RoomRoundSettingFields? _roundSettingFields;
    internal bool EchoDirectItemEffects { get; init; }
    /// <summary>Gets whether the row reads the clients' star-count reports (the <c>star_items</c> option and capability).</summary>
    internal bool StarReports { get; init; }
    internal Func<ISession, RoomReadyRequested, CancellationToken, ValueTask<bool>>? ReadyRequestFirst { get; init; }
    // The session item that remembers how many rows the client's last list request asked for.
    private const string PageSizeItem = "legacy.room.list.page-size";

    // The first number of the peer gameplay feed.
    private const ushort FeedBase = 0x8000;

    public string Protocol => protocol;

    /// <inheritdoc />
    public bool EndBeforeScoreboard => Score.TimeOverEnd == TimeOverEndBody.Scoreboard;

    /// <summary>
    /// Gets what sees a load report before the round does, or null: a build whose one-seat tutorial
    /// answers the report itself takes the ones that belong to a tutorial.
    ///
    /// </summary>
    internal Func<ISession, InboundPacket, CancellationToken, ValueTask<bool>>? LoadReportFirst { get; init; }

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
        _ = packetTables.Resolve(lineage, "TutorialWave");
        MountWaitingControls(context);
        var services = context.Services;
        var messages = services.Get<IMessageRouter>();
        var router = services.Get<IPacketRouter>();
        var table = tables.ForProtocol(protocol);
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
            context.Effect(services.RegisterMany<IMessageSender<RoundWaveChanged>>(this));
        context.Effect(services.RegisterMany<IInboundMessageBinding<RoomListRequested>>(this));
        context.Effect(services.RegisterMany<IInboundMessageBinding<RoomCreateRequested>>(this));
        context.Effect(services.RegisterMany<IInboundMessageBinding<RoomLeaveRequested>>(this));
        context.Effect(services.RegisterMany<IInboundMessageBinding<RoomStartRequested>>(this));
        context.Effect(services.RegisterMany<IInboundMessageBinding<RoundLoadReported>>(this));
        context.Effect(services.RegisterMany<IInboundMessageBinding<RoundFeedSent>>(this));
        context.Effect(services.RegisterMany<IMessageSender<RoundPlayerLoaded>>(this));
        context.Effect(services.RegisterMany<IMessageSender<RoundCountdownStarted>>(this));
        context.Effect(services.RegisterMany<IMessageSender<RoundStarted>>(this));
        context.Effect(services.RegisterMany<IMessageSender<RoundFeedReceived>>(this));
        context.Effect(services.RegisterMany<IInboundMessageBinding<RoundEndRequested>>(this));
        context.Effect(services.RegisterMany<IMessageSender<RoundEnded>>(this));
        context.Effect(services.RegisterMany<IInboundMessageBinding<RoundRespawnRequested>>(this));
        context.Effect(services.RegisterMany<IInboundMessageBinding<RoundSpawnAccepted>>(this));
        context.Effect(services.RegisterMany<IInboundMessageBinding<RoundDeathReported>>(this));
        context.Effect(services.RegisterMany<IMessageSender<RoundSpawnOffered>>(this));
        context.Effect(services.RegisterMany<IMessageSender<RoundPlayerRespawned>>(this));
        // The taunt only moves a wait the round itself times, and only where the build can be told.
        var tauntEnd = play.Wire.ServerRevives && table.Find(PacketDirection.ServerToClient, "gspGameExtendRespawn") is not null
            ? table.Find(PacketDirection.ClientToServer, "gppHumiliationEnd")
            : null;
        if (tauntEnd is not null)
        {
            context.Effect(services.RegisterMany<IInboundMessageBinding<RoundTauntReported>>(this));
            context.Effect(services.RegisterMany<IMessageSender<RoundRespawnWaitChanged>>(this));
        }
        context.Effect(services.RegisterMany<IMessageSender<RoomListSnapshot>>(this));
        context.Effect(services.RegisterMany<IMessageSender<RoomEntered>>(this));
        context.Effect(services.RegisterMany<IMessageSender<RoomSeatUpdated>>(this));
        context.Effect(services.RegisterMany<IMessageSender<RoomSeatVacated>>(this));
        context.Effect(services.RegisterMany<IMessageSender<RoomMasterChanged>>(this));
        context.Effect(services.RegisterMany<IMessageSender<RoomLeft>>(this));
        context.Effect(services.RegisterMany<IMessageSender<RoomMatchStarting>>(this));

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


        On("gcpReqListSession", (session, body) =>
        {
            var (page, pageSize) = PlazaRoomWire.ReadListSession(packetTables, lineage, body);
            session.Items[PageSizeItem] = (int)pageSize;
            return new RoomListRequested(page);
        });
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
        OnWhereNamed("gcpRespawn", (_, body) =>
        {
            return ReadSpawnAcceptance(packetTables, lineage, body);
        });
        // The peer gameplay feed (gpp*, numbers from 0x8000) arrives on the game connection when no
        // relay is offered (demux sub_80D4C0); the round flow passes it on unread. Numbers a row
        // registered a handler for are that handler's. Awaiting this observer preserves arrival order
        // and applies transport backpressure without accumulating detached forwarding tasks.
        context.Effect(context.Events.OnAsync<PacketReceivedEvent>(async (received, ct) =>
        {
            if (received.HasHandler || received.ProtocolName != protocol || received.Packet.Opcode < FeedBase)
                return;
            await messages.DispatchAsync(protocol, received.Session,
                new RoundFeedSent(received.Packet.Opcode, received.Packet.Payload.ToArray())
                {
                    // cn-324 sub_47BCA0 sends only; sub_47BD30 applies the item to the receiving character.
                    IncludeSender = EchoDirectItemEffects && received.Packet.Opcode == table.C2S("gppUseItemDirect"),
                }, ct).ConfigureAwait(false);
            // gppHumiliationEnd (cn-324 sender sub_46B190): u32 the taunter's handle, u32 the dead
            // player's. It is passed on like the rest of the feed and also moves the dead player's wait.
            if (received.Packet.Opcode == tauntEnd && received.Packet.Payload.Length == 8)
            {
                await messages.DispatchAsync(protocol, received.Session,
                    new RoundTauntReported(BitConverter.ToInt32(received.Packet.Payload.Span[4..])), ct).ConfigureAwait(false);
            }
        }));
        if (play.HostStart)
        {
            On("gcpUserHostStart", (_, body) =>
            {
                PlazaRoomWire.ReadUserHostStart(body);
                return new RoomStartRequested();
            });
        }
        MountPlaza(context);
        MountResult(context);
    }

    /// <summary>
    /// <c>gspRepListSession</c> (reader <c>sub_680780</c>): the page number, whether another page
    /// follows, then one <c>SessionSetting</c> per room on this page.
    ///
    /// </summary>
    public ValueTask SendAsync(ISession session, RoomListSnapshot message, CancellationToken ct)
    {
        var pageSize = session.Items.TryGetValue(PageSizeItem, out var stored) && stored is int size && size > 0 ? size : message.Rooms.Count;
        var skip = Math.Max(0, message.Cursor) * Math.Max(1, pageSize);
        var page = message.Rooms.Skip(skip).Take(Math.Max(1, pageSize)).ToArray();
        var rows = page.Select(room => RoomCodec.PlaySessionValues(schema,
            new RoomValues(RoomCodec.PlayRoomKind, [])
            {
                // kr-812 sub_679670 distinguishes waiting (2) from active matches (4).
                Play = true, RoomId = room.RoomId, Title = room.Name, Mode = room.ModeId,
                SessionState = room.MatchInProgress ? 4 : 2, MasterNickname = room.MasterNickname,
                Occupants = room.Occupants, OpenSeats = room.Capacity, HasPassword = room.HasPassword,
                GameSetting = GameSetting(room.BuildSettings),
            })).ToArray();
        var body = PacketCodec.Write(packetTables, lineage, "RoomListReply", new Dictionary<string, object?>
        {
            ["page"] = message.Cursor,
            ["more"] = message.Rooms.Count > skip + page.Length,
            ["rooms"] = rows,
        });
        return session.SendAsync(S2C(session, "gspRepListSession"), body, ct);
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
            message.Seat.CharacterItemId, message.Seat.EquippedItemIds.Count > 0 ? message.Seat.EquippedItemIds : [message.Seat.CharacterItemId]) { ColorIndex = message.Seat.ColorIndex };
        await session.SendAsync(S2C(session, "gspRoomSetSlot"), PlazaRoomWire.WriteSetSlot(own, packetTables, lineage, schema, slot: 0, ready: false), ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The start burst, as recorded at s2c#152-153: every seated player's slot with its start position
    /// index set, then <c>gspRoomEnterGame</c> (reader <c>sub_80AE90</c>) with the round's map key. The
    /// third u32 after the key is 3; the client stores it as the session state.
    ///
    /// </summary>
    public async ValueTask SendAsync(ISession session, RoomMatchStarting message, CancellationToken ct)
    {
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
        var scoreboard = EntryScoreboardValues(packetTables, lineage, message.Room);
        await session.SendAsync(S2C(session, "gspRoomEnterGame"),
            TutorialWire.WriteRoomEnterGame(packetTables, lineage, unknown0: 0, unknown1: mapKey, unknown4: 3,
                scoreboardValues: scoreboard), ct).ConfigureAwait(false);
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
        return ScoreboardValues(tables, build, new RoundScoreboard(room.RoomId, 0, [0, 0], rows));
    }

    /// <summary><c>gspGameMapLoadingComplete</c> (reader <c>sub_80A910</c>): the seat index of the player who loaded.</summary>
    public ValueTask SendAsync(ISession session, RoundPlayerLoaded message, CancellationToken ct) =>
        session.SendAsync(S2C(session, "gspGameMapLoadingComplete"), TutorialWire.WriteMapLoadingComplete(packetTables, lineage, message.Slot), ct);

    /// <summary>Sends the shared loading gates and countdown using this build's native packet sequence.</summary>
    public ValueTask SendAsync(ISession session, RoundCountdownStarted message, CancellationToken ct) =>
        RoundStartSender.SendCountdownAsync(packetTables, lineage, tables.Get(session.Profile.Id),
            play.Wire.PreCountDownFirst, session, message, ct);

    /// <summary>Sends the shared match clock and native start words using this build's packet table.</summary>
    public ValueTask SendAsync(ISession session, RoundStarted message, CancellationToken ct) =>
        RoundStartSender.SendStartedAsync(packetTables, lineage, tables.Get(session.Profile.Id), session, message, ct);

    /// <summary>
    /// <c>gspGameEndGame</c> (reader <c>kr-812 sub_6DB1B0</c>): <c>u32 unused, u32 outcome</c>. The handler
    /// <c>kr-812 sub_803DF0</c> reads the second word, stores it as the result stage's gate and sets the
    /// win flag from it; a win is <c>00 00 00 00 03 00 00 00</c>.
    ///
    /// </summary>
    public async ValueTask SendAsync(ISession session, RoundEnded message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        var shape = play.Score ?? new ScoreShape();
        if (HordeOutcomeNotification(shape, message) is { } notification)
            await session.SendAsync(S2C(session, notification), ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);
        await session.SendAsync(S2C(session, "gspGameEndGame"),
            WriteGameEndForRecipient(packetTables, lineage, shape, message), ct).ConfigureAwait(false);
    }

    internal static string? HordeOutcomeNotification(ScoreShape shape, RoundEnded message)
    {
        // cn-324 sub_438500 sets HordeModeMgr+5328 from these empty notifications.
        // cn-324 sub_43A4C0 / sub_43B560 / sub_43C810 consume it instead of team totals.
        if (!shape.HordeStageOutcome || (GameModeId)message.ModeId is not (GameModeId.Horde or GameModeId.HordeZhe or GameModeId.HordeTutor))
            return null;
        return message.Outcome == RoundOutcome.Won ? "gspHordeStageCleared_" : "gspHordeStageFailed_";
    }

    /// <summary>
    /// Writes the two-word <c>gspGameEndGame</c> body. The build table selects the outcome word:
    /// kr-812 and cn-324 use the second; cn-324 sub_408DF0 ret 4 changes ESP before 0x6F1AE0 reads object +8.
    ///
    /// </summary>
    /// <param name="tables">Audited packet layouts.</param>
    /// <param name="build">Native client build.</param>
    /// <param name="outcome">The recipient's outcome.</param>
    /// <param name="outcomeFirst">Whether the outcome is the first word.</param>
    /// <returns>The packet body.</returns>
    internal static byte[] WriteGameEnd(PacketTables tables, string build, RoundOutcome outcome, bool outcomeFirst = false)
    {
        return WriteEndWord(tables, build, OutcomeWord(outcome), outcomeFirst);
    }

    internal static byte[] WriteGameEndForRecipient(PacketTables tables, string build, ScoreShape shape, RoundEnded message)
    {
        var word = OutcomeWord(message.Outcome);
        // cn-324 sub_63B230 / kr-812 sub_5ACB20 compare result 2 with local team in native horde stage kind 10.
        if (shape.NpcTeamOutcome && (GameModeId)message.ModeId is GameModeId.Npc or GameModeId.Horde or GameModeId.HordeZhe or GameModeId.HordeTutor && message.Outcome != RoundOutcome.Draw)
        {
            var team = message.RecipientTeam == 0 ? 2 : message.RecipientTeam;
            if (team is not (1 or 2)) throw new InvalidOperationException("Native horde results require a red or blue team.");
            var blueWins = message.Outcome == RoundOutcome.Won ? team == 2 : team == 1;
            word = blueWins ? 2 : 3;
        }
        return WriteEndWord(tables, build, word, shape.OutcomeFirst);
    }

    private static byte[] WriteEndWord(PacketTables tables, string build, int word, bool outcomeFirst) =>
        PacketCodec.Write(tables, build, "RoundEndGame", new Dictionary<string, object?>
        {
            ["unknown_4"] = outcomeFirst ? word : 0,
            ["unknown_8"] = outcomeFirst ? 0 : word,
        });

    /// <summary>Sends the room chat line using the client's audited string and suffix layout.</summary>
    public ValueTask SendAsync(ISession session, RoomChatReceived message, CancellationToken ct) =>
        session.SendAsync(S2C(session, "gspRoomReceiveChatModuleMsg"),
            WriteChat(packetTables, lineage, message.Slot, message.Nickname, message.Channel, message.Text), ct);

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

    /// <summary>Writes a room chat line with the build's native unknown suffix defaults.</summary>
    internal static byte[] WriteChat(PacketTables tables, string build, int slot, string nickname, int channel, string text) =>
        PacketCodec.Write(tables, build, "RoomChatReceived", new Dictionary<string, object?>
        {
            ["slot"] = checked((ushort)slot),
            ["nickname"] = nickname,
            ["channel"] = channel,
            ["text"] = text,
        });

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
    /// <c>gspRespawn</c> confirms the actor's revival (kr-812 sub_503530 / kr-812 sub_51E2C0);
    /// the native peer consumer completes its local state transition (kr-812 sub_51E440).
    /// CN2011 revives without a packet flag (cn-279 sub_81B470); the table supplies that difference.
    ///
    /// </summary>
    public ValueTask SendAsync(ISession session, RoundPlayerRespawned message, CancellationToken ct) =>
        session.SendAsync(S2C(session, "gspRespawn"), WriteRespawn(packetTables, lineage, message.Actor, message.SpawnIndex), ct);

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

    /// <summary>Writes the build's actor/index confirmation and any native revival fields.</summary>
    internal static byte[] WriteRespawn(PacketTables tables, string build, int handle, int spawnIndex) =>
        WriteSpawn(tables, build, "RoundSpawnConfirmation", handle, spawnIndex);

    /// <summary>Writes the actor reference for a native client-selected respawn prompt.</summary>
    internal static byte[] WriteRespawnPrompt(PacketTables tables, string build, int handle) =>
        PacketCodec.Write(tables, build, "RoundRespawnPrompt", new Dictionary<string, object?> { ["actor"] = handle });

    /// <summary>Reads a complete native spawn acceptance, preserving signed actor and index bits.</summary>
    internal static RoundSpawnAccepted ReadSpawnAcceptance(PacketTables tables, string build, ReadOnlySpan<byte> body)
    {
        var fields = PacketCodec.ReadScalars(tables, build, "RoundSpawnAcceptance", body);
        var handle = (int)fields["actor"];
        return new RoundSpawnAccepted(handle, (int)fields["spawn_index"]) { ActorSeat = PlayerSeatOf(handle) };
    }

    private static byte[] WriteSpawn(PacketTables tables, string build, string structure, int handle, int spawnIndex) =>
        PacketCodec.Write(tables, build, structure, new Dictionary<string, object?>
        {
            ["actor"] = handle, ["spawn_index"] = spawnIndex,
        });

    /// <summary>A feed packet goes out under the number it came in with.</summary>
    public ValueTask SendAsync(ISession session, RoundFeedReceived message, CancellationToken ct) =>
        session.SendAsync(message.Code, message.Payload, ct);

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
            Slot(seat) with { Occupied = false, Departed = true, EquippedItemIds = [], Ready = false }, leaving: true);

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
            TutorialWire.WriteStartNewWave(packetTables, lineage, message.Wave), ct);

    private static RoomSlotValues Slot(RoomMemberView seat) =>
        new(true, false, seat.UserNum, seat.Nickname, seat.CharacterItemId,
            seat.EquippedItemIds.Count > 0 ? seat.EquippedItemIds : [seat.CharacterItemId], seat.Ready)
        {
            Team = seat.Team == 0 ? 2 : seat.Team, Level = seat.Level, ColorIndex = seat.ColorIndex,
        };
}
