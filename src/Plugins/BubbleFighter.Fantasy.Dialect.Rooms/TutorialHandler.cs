using System.Collections.Concurrent;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.Packets;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>
/// Tutorial select scene and one-seat start path for legacy builds. CreateTutorial opens
/// <c>TutorialRoomStage</c> (GotoRoom kind 5); the stage then asks for
/// <c>gspResSingleStageInfo</c>. PlayTutorial seats the player in a one-seat play room
/// (GotoRoom kind 2) then starts play: kr-812 / cn-717 send <c>gspRoomEnterGame</c> with the
/// PlayTutorial map key; id-400 sends <c>gspRepAutoStart</c> true.
/// cn-717 then waits for <c>gcpGameReportMapLoadingComplete</c> and replies with the
/// MapLoading / EverybodyLoading / CountDown / StartGame chain (RoomStage dispatcher
/// <c>sub_87EFE0</c> cases 0x2BD–0x2C0). Login is unchanged: it still uses kind 1 plus
/// <c>gspGotoMenu</c>.
///
/// </summary>
internal sealed class TutorialHandler : IDisposable
{
    private const int DefaultCharacterItemId = 300001;

    /// <summary>
    /// <c>wgtHordeTutor</c> = 103. Prologue MapMeta sets <c>isSinglePlayTutorial</c>, so
    /// kr-812 <c>sub_7FF340</c> takes SetStage(5). The stage-5 factory
    /// kr-812 <c>sub_8E1240</c> uses the normal game stage for mode 103;
    /// kr-812 <c>sub_660D50</c> builds <c>NpcGameContext</c> for 9/101/102/103. Mode 15 builds
    /// <c>SinglePlayGameStage</c> instead and never arms the horde/prologue NPC context.
    ///
    /// </summary>
    private const int HordeTutorMode = (int)GameModeId.HordeTutor;

    /// <summary>
    /// <c>wgtSinglePlay</c> = 15. Retained for builds without an audited one-seat horde path.
    /// kr-812 <c>sub_8E1240</c> reserves this mode for SinglePlayGameStage.
    ///
    /// </summary>
    private const int SinglePlayTutorialMode = (int)GameModeId.SinglePlay;

    /// <summary>
    /// Room+12 value <c>gspGameStartGame</c> writes after a successful start (cn-717
    /// <c>sub_886D80</c>). EnterGame copies <c>unknown4</c> there before the stage move.
    ///
    /// </summary>
    private const int EnterGameRoomState = 4;

    /// <summary>
    /// One-seat tutorial GotoRoom my-slot / UserSlot seat index. S2C MapLoadingComplete
    /// marks room+16664+seat; must match the seat byte written on the kind-2 UserSlot.
    ///
    /// </summary>
    private const int TutorialSeat = 0;

    /// <summary>
    /// Pre-round countdown ms for <c>gspGameCountDown</c>. Same literal
    /// <c>sub_886D80</c> passes to <c>sub_B20AA0(4000)</c> on several non-tutorial modes;
    /// the CountDown handler arms the display timer with this packet field.
    ///
    /// </summary>
    private const int TutorialCountDownMs = 4000;

    private readonly RoomSchema _room;
    private readonly PlayTutorialShape _play;
    private readonly TutorialMatchStart _matchStart;
    private readonly IReadOnlyList<PacketField> _scoreRowFields;
    private readonly PlayValues.ScoreValues? _score;
    private readonly IOpcodeTableRegistry _tables;
    private readonly PacketTables _packetTables;
    private readonly string _build;
    private readonly ILogger _logger;
    private readonly IMessageRouter? _messages;
    private readonly IInventoryRepository? _inventory;
    private readonly IEventBus? _events;

    /// <summary>
    /// Sessions that finished PlayTutorial EnterGame and still need the one-seat load-wait
    /// release. Value is the StartGame match-clock ms from PlayTutorial's MapMeta
    /// TimeAttackTimeLimit. Keyed by session id so a second MapLoadingComplete is ignored.
    ///
    /// </summary>
    private readonly ConcurrentDictionary<long, int> _awaitingLoadComplete = new();
    private readonly Lock _tutorialGate = new();
    // Active tutorial sessions: whether each runs the one-seat waves or is a native single stage, and its map.
    private readonly record struct TutorialEntry(bool Waves, bool Old, string MapKey);
    private readonly Dictionary<long, TutorialEntry> _tutorialSessions = [];
    private readonly Dictionary<long, TutorialRoundOpened> _pendingClientStarts = [];
    private readonly int _maxTutorialSessions;

    /// <summary>Whether <c>gspGameEndGame</c> carries the outcome in its first word instead of its second.</summary>
    private readonly bool _outcomeFirst;

    /// <summary>Creates the handler for one legacy game protocol.</summary>
    // Whether this build's one-seat tutorial runs its own waves (the horde tutorial): it needs the
    // time limit and the difficulty from the request, and its own tutorial mode.
    private readonly bool _oneSeatWaves;

    private readonly bool _sharedLifecycle;

    // How long a finished single-play stage keeps its clear or fail panel before the return to the tutorial room.
    private readonly int _resultMs;

    // How long a finished single-play stage's time-over cue runs before its clear or fail panel.
    private readonly int _scoreMs;

    // The players' cleared tutorial-room stages; without it the stage list is sent empty.
    private readonly BubbleFighter.Fantasy.Contracts.Progress.ITutorialStageStore? _stages;
    private readonly bool _skipStart;
    private readonly bool _useOldMap;
    private readonly bool _spawnGroupAppliesLocally;

    public TutorialHandler(
        PacketTables packetTables,
        string build,
        RoomSchema room,
        PlayTutorialShape play,
        TutorialMatchStart matchStart,
        PlayRoomWireShape wire,
        IOpcodeTableRegistry tables,
        ILogger logger,
        bool oneSeatWaves = false,
        bool outcomeFirst = false,
        IReadOnlyList<PacketField>? scoreRowFields = null,
        PlayValues.ScoreValues? score = null,
        bool tutorialSkipStart = false,
        bool tutorialUseOldMap = false,
        int maxTutorialSessions = 64,
        IMessageRouter? messages = null,
        IInventoryRepository? inventory = null,
        IEventBus? events = null,
        bool sharedLifecycle = false,
        int resultMs = 0,
        int scoreMs = 0,
        BubbleFighter.Fantasy.Contracts.Progress.ITutorialStageStore? stages = null)
    {
        _stages = stages;
        _sharedLifecycle = sharedLifecycle;
        _resultMs = resultMs;
        _scoreMs = scoreMs;
        _packetTables = packetTables;
        _build = build;
        if (matchStart == TutorialMatchStart.RepAutoStart)
            _ = packetTables.Resolve(build, "gspRepAutoStart");
        _ = _packetTables.Resolve(_build, "RoundMapLoadingComplete");
        _ = _packetTables.Resolve(_build, "RoundMapLoadingReport");
        _ = _packetTables.Resolve(_build, "RoundEverybodyLoadingComplete");
        _ = _packetTables.Resolve(_build, "RoundCountdown");
        _ = _packetTables.Resolve(_build, "RoundStartGame");
        _ = _packetTables.Resolve(_build, "TutorialMasterSlot");
        _ = _packetTables.Resolve(_build, "TutorialWave");
        _ = _packetTables.Resolve(_build, "TutorialPlayRequest");
        _ = _packetTables.Resolve(_build, "TutorialSpawnGroup");
        _ = _packetTables.Resolve(_build, "TutorialEndStage");
        _ = _packetTables.Resolve(_build, "TutorialStageInfo");
        _ = _packetTables.Resolve(_build, "RoomEnterGame");
        _ = _packetTables.Resolve(_build, "RoomEnterGameHeader");
        _ = _packetTables.Resolve(_build, "RoomEnterGameTail");
        _ = packetTables.Resolve(build, "RoundEndGame");
        _room = room;
        _oneSeatWaves = oneSeatWaves;
        _outcomeFirst = outcomeFirst;
        _play = play;
        _matchStart = matchStart;
        _scoreRowFields = scoreRowFields ?? [];
        _score = score;
        _skipStart = tutorialSkipStart;
        _useOldMap = tutorialUseOldMap;
        _spawnGroupAppliesLocally = wire.SpawnGroupAppliesLocally;
        _maxTutorialSessions = maxTutorialSessions;
        _tables = tables;
        _logger = logger;
        _messages = messages;
        _inventory = inventory;
        _events = events;
    }

    /// <summary>
    /// Handles empty <c>gcpReqCreateTutorialSession</c>: replies with <c>gspGotoRoom</c> kind 5
    /// so <c>TutorialRoomStage</c> loads. The stage itself then sends <c>gcpReqSingleStageInfo</c>.
    ///
    /// </summary>
    public async ValueTask HandleCreateTutorialAsync(ISession session, InboundPacket packet, CancellationToken ct)
    {
        if (packet.Payload.Length != 0)
            throw new PacketFormatException($"gcpReqCreateTutorialSession must be empty; got {packet.Payload.Length} byte(s).", 0);
        await ClearTutorialStateAsync(session, ct).ConfigureAwait(false);
        var player = await ResolvePlayerAsync(session, ct).ConfigureAwait(false);
        await SendTutorialSelectAsync(session, player, ct).ConfigureAwait(false);
    }

    // Opens the tutorial select scene: gspGotoRoom of the build's select kind with no tutorial mode or map.
    private async ValueTask SendTutorialSelectAsync(ISession session, PlayerContext player, CancellationToken ct)
    {
        var table = _tables.Get(session.Profile.Id);
        // WriteTutorialSelect forces mode 0 and empty map strings so PlayTutorial's mode 103 /
        // Directory / CurrentMap / key never ride on the select GotoRoom. The kind is the build's
        // statement: 5 opens the select scene; 8 opens the dedicated tutorial-room stage, which
        // needs its map key in the room (the stage crashed on an empty one in the live test).
        var selectMap = _play.SelectKind == RoomCodec.TutorialSelectKind ? "" : _play.OldMapKey;
        var body = RoomCodec.WriteSingleRoomEnvelope(
            player.Room, _packetTables, _build, _room, _play.SelectKind, mode: 0, mapName: selectMap);
        await session.SendAsync(table.S2C("gspGotoRoom"), body, ct).ConfigureAwait(false);
        player.Session.MoveTo(SessionPhase.Room);
        _logger.LogInformation(
            "Session {Id} tutorial select: S2C gspGotoRoom kind {Kind} ({Bytes} bytes).",
            session.Id, _play.SelectKind, body.Length);
    }

    /// <summary>
    /// Handles empty <c>gcpReqSingleStageInfo</c>: replies with the player's cleared tutorial-room stages, which the
    /// TutorialRoomStage marks and shows the best record of (kr-812 <c>sub_824200</c>, <c>sub_6B50E0</c>).
    ///
    /// </summary>
    public async ValueTask HandleSingleStageInfoAsync(ISession session, InboundPacket packet, CancellationToken ct)
    {
        if (packet.Payload.Length != 0)
            throw new PacketFormatException($"gcpReqSingleStageInfo must be empty; got {packet.Payload.Length} byte(s).", 0);
        var player = RequirePlayer(session);
        var table = _tables.Get(session.Profile.Id);
        var cleared = _stages is null || _packetTables.Resolve(_build, "TutorialStageInfo").Single().Type == "empty_list"
            ? [] : (await _stages.ListAsync(player.Room.UserNumber, ct).ConfigureAwait(false))
                .Select(clear => new SingleStageClearInfo(clear.Stage, clear.BestCount, clear.BestSeconds)).ToArray();
        var body = TutorialWire.WriteSingleStageInfo(_packetTables, _build, cleared);
        await session.SendAsync(table.S2C("gspResSingleStageInfo"), body, ct).ConfigureAwait(false);
        _logger.LogInformation(
            "Session {Id} tutorial stage list: S2C gspResSingleStageInfo with {Count} cleared stage(s) ({Bytes} bytes).",
            session.Id, cleared.Length, body.Length);
    }

    /// <summary>
    /// Handles <c>gcpReqPlayTutorial</c>: installs a one-seat play room (GotoRoom kind 2), then
    /// sends the build's match-start S2C so the client leaves the lobby without a ready click.
    ///
    /// </summary>
    public async ValueTask HandlePlayTutorialAsync(ISession session, InboundPacket packet, CancellationToken ct)
    {
        PlayTutorialRequest request;
        try
        {
            request = TutorialWire.ReadPlayTutorial(_packetTables, _build, packet.Payload.Span);
        }
        catch (PacketFormatException ex)
        {
            _logger.LogError(
                "Session {Id} gcpReqPlayTutorial does not match the expected layout ({Reason}); closing.",
                session.Id, ex.Message);
            await session.Connection.CloseAsync("malformed gcpReqPlayTutorial").ConfigureAwait(false);
            return;
        }

        var oldTutorial = false;
        if (_matchStart == TutorialMatchStart.RoomEnterGame
            && ((_useOldMap && RequirePlayer(session).Session.Phase == SessionPhase.MainMenu)
                || string.IsNullOrEmpty(request.MapKey)
                || (_play.OldMapKey.Length != 0 && request.MapKey == _play.OldMapKey)))
        {
            if (_play.OldMapKey.Length == 0)
            {
                _logger.LogError(
                    "Session {Id} gcpReqPlayTutorial map key (third wstr) is empty; closing.",
                    session.Id);
                await session.Connection.CloseAsync("gcpReqPlayTutorial missing map key").ConfigureAwait(false);
                return;
            }
            // cn-279 sub_6F8B20's Shift branch sends no map. The profile can select the
            // same native TutorialStage path for requests from the automatic notice.
            request = request with { MapKey = _play.OldMapKey, Unknown0 = _play.OldMapKey };
            oldTutorial = true;
            _logger.LogInformation(
                "Session {Id} old tutorial selected: {MapKey}.",
                session.Id, _play.OldMapKey);
        }

        // Only the build's wave map runs the one-seat waves; the tutorial room's numbered stages are single-play
        // stages (MapMeta stageNum above 0, mode 15).
        var waves = _oneSeatWaves && !oldTutorial
            && (_play.WaveMapKey.Length == 0 || string.Equals(request.MapKey, _play.WaveMapKey, StringComparison.OrdinalIgnoreCase));
        if (waves
            && request.TimeAttackTimeLimitMs <= 0)
        {
            // StartGame field0=0 leaves GC+1744 idle (sub_B20B70 false), so sub_6BE640 never
            // clears stage+2248 letterbox on mode 103. Client must send MapMeta TimeAttack ms.
            _logger.LogError(
                "Session {Id} gcpReqPlayTutorial TimeAttackTimeLimit (MapMeta u32[8]) is {Ms}; closing.",
                session.Id, request.TimeAttackTimeLimitMs);
            await session.Connection.CloseAsync("gcpReqPlayTutorial missing TimeAttackTimeLimit").ConfigureAwait(false);
            return;
        }

        if (waves
            && request.Difficulty <= 0)
        {
            // GameSetting+164 / room+252 = 0 leaves wave Lua TableWave2Level[0] nil (no groups).
            // The old-tutorial entry (cn-279's Shift-click) legitimately sends no difficulty; its
            // map's stage scripts are not the horde WaveTest chain, so 1 is a placeholder.
            _logger.LogError(
                "Session {Id} gcpReqPlayTutorial difficulty is {Difficulty}; closing.",
                session.Id, request.Difficulty);
            await session.Connection.CloseAsync("gcpReqPlayTutorial missing difficulty").ConfigureAwait(false);
            return;
        }

        // RoomEnterGame path: seed GameSetting mode + Directory (+16/room+104) + CurrentMap
        // (+44/room+132) + map key (+100/room+188) + difficulty (+164/room+252). Empty CurrentMap
        // under mode 103 AVs in sub_B1DFE0 after sub_6EB170; isSinglePlayTutorial (sub_88A250)
        // still takes SetStage(5) and bare sub_6B9310 builds NpcGameContext for mode 103. Builds with
        // one-seat waves use wgtHordeTutor; the rest keep wgtSinglePlay. Never kind 1 / 5.
        // id-400 sub_858ED0, id-1809 sub_87F800 and cn-706 sub_869FE0 select
        // SinglePlayGameStage for mode 15; an empty mode/map cannot enter that scene.
        var mapForRoom = request.MapKey;
        if (string.IsNullOrEmpty(mapForRoom))
            throw new PacketFormatException("Tutorial start requires a selected map.", 0);
        var directoryForRoom = string.IsNullOrEmpty(mapForRoom) ? "" : request.Unknown0;
        var currentForRoom = mapForRoom;
        // Retain the accepted tutorial's third string until its own theme projection is audited.
        // kr-812 sub_A128A0 obtains the Lua script map from currentForRoom, not this compatibility slot.
        var npcTableForRoom = mapForRoom;
        var modeForRoom = 0;
        var difficultyForRoom = 0;
        if (!string.IsNullOrEmpty(mapForRoom))
        {
            modeForRoom = waves ? HordeTutorMode : SinglePlayTutorialMode;
            difficultyForRoom = request.Difficulty;
        }
        // cn-279 sub_401730 means NOT equal. cn-279 sub_7091E0 selects TutorialStage
        // (kind 6) when the room key is not "Random" and cn-279 sub_706750 finds isTutorial.
        var tutorialMap = "";
        if (oldTutorial)
        {
            tutorialMap = mapForRoom;
            directoryForRoom = "";
            currentForRoom = "";
            npcTableForRoom = "";
            modeForRoom = _play.OldMode;
            difficultyForRoom = 0;
        }
        // The old map uses TutorialMgr. cn-324 sub_45C480 needs a team mode to preserve
        // its team and cn-324 sub_45C3F0 selects #spawn_T; the build table supplies that mode.
        var modeForStart = modeForRoom;
        var player = await ResolvePlayerAsync(session, ct).ConfigureAwait(false);
        await ClearTutorialStateAsync(session, ct).ConfigureAwait(false);
        var table = _tables.Get(session.Profile.Id);
        _logger.LogInformation(
            "Session {Session} player {User} starts tutorial old={Old} map={Map} mode={Mode} character={Character} equipment=[{Equipment}].",
            session.Id, player.Room.UserNumber, oldTutorial, mapForRoom, modeForRoom,
            player.Room.CharacterItemId, string.Join(',', player.Room.EquippedItemIds));
        // cn-279 sub_820F20 assigns the first team-2 actor spawn 21; sub_75EA50
        // resolves it to #spawn_T21. Team 0 leaves the tutorial spawn index at -1.
        var tutorialTeam = oldTutorial ? 2 : 0;
        lock (_tutorialGate)
        {
            if (!_tutorialSessions.ContainsKey(session.Id) && _tutorialSessions.Count >= _maxTutorialSessions)
                throw new InvalidOperationException("Tutorial sessions reached their configured limit.");
            _tutorialSessions[session.Id] = new(waves, oldTutorial, mapForRoom);
        }
        var admission = new TutorialRoundOpened(modeForRoom, mapForRoom,
                player.Room.CharacterItemId, player.Room.EquippedItemIds, checked((byte)tutorialTeam),
                TutorialCountDownMs, request.TimeAttackTimeLimitMs, !waves);
        if (!_skipStart && !_play.SkipEnter)
        {
            if (_matchStart == TutorialMatchStart.RepAutoStart)
            {
                lock (_tutorialGate) _pendingClientStarts[session.Id] = admission;
            }
            else if (_sharedLifecycle)
                await DispatchLifecycleAsync(session, admission, ct).ConfigureAwait(false);
        }
        var tutorialSlot = new RoomSlotValues(true, false,
            checked((int)player.Room.UserNumber), player.Room.Nickname,
            player.Room.CharacterItemId, player.Room.EquippedItemIds, false)
        {
            Team = tutorialTeam,
        };
        var room = RoomCodec.WriteRoomEnvelope(_packetTables, _build, _room,
            new RoomValues(RoomCodec.PlayRoomKind, [tutorialSlot])
            {
                Play = oldTutorial,
                Mode = modeForRoom, MapName = mapForRoom,
                MapDirectory = directoryForRoom, CurrentMap = currentForRoom,
                Difficulty = difficultyForRoom, NpcTableKey = npcTableForRoom,
            });
        await session.SendAsync(table.S2C("gspGotoRoom"), room, ct).ConfigureAwait(false);

        // The start puts
        // gspRoomChangeGameSetting before the seat push and the enter; it initializes the native
        // settings before the NPC context is created, including current map and difficulty.
        if (table.Find(PacketDirection.ServerToClient, "gspRoomChangeGameSetting") is { } changeSetting)
        {
            var settingBody = RoomCodec.WriteGameSettingEnvelope(_packetTables, _build, _room, new RoomValues(
                RoomCodec.PlayRoomKind, [tutorialSlot])
            {
                // Keep the tutorial key: cn-279 sub_7091E0 requires it to differ from
                // "Random", then cn-279 sub_706750 tests its isTutorial property.
                Mode = modeForStart, MapName = mapForRoom,
                MapDirectory = directoryForRoom, CurrentMap = currentForRoom,
                Difficulty = difficultyForRoom, NpcTableKey = npcTableForRoom,
            });
            await session.SendAsync(changeSetting, settingBody, ct).ConfigureAwait(false);
        }

        // The start burst pushes every seat before gspRoomEnterGame. The client takes its
        // own seat index from this packet, and the test that lets the host build its spawn group
        // (cn-717 sub_741980) compares that seat with the room master's; a one-seat room that never
        // receives it is not treated as its own host, so no spawn group and no mobs.
        if (table.Find(PacketDirection.ServerToClient, "gspRoomSetSlot") is not null)
        {
            // cn-279 sub_4499F0 constructs UserSlot through cn-279 sub_449420; its
            // reader cn-279 sub_4499B0 delegates to that object, not SessionSetting.
            var slotBody = RoomCodec.WritePlaySlot(player.Room, _packetTables, _build, _room, TutorialSeat, tutorialTeam);
            await session.SendAsync(table.S2C("gspRoomSetSlot"), slotBody, ct).ConfigureAwait(false);
        }

        // On cn-324 the master slot reaches the client only through this packet (handler cn-324 sub_6ED2D0
        // stores it at room+136); without it the one-seat tutorial is not its own host and the wave manager
        // never asks to spawn. kr-812's gspGotoRoom already set it, so its builds skip the packet and the
        // room-master notice it raises (play.tutorial.master_slot_packet).
        if (_play.MasterSlotPacket && table.Find(PacketDirection.ServerToClient, "gspRoomSetMasterSlotIdx") is not null)
        {
            await session.SendAsync(
                table.S2C("gspRoomSetMasterSlotIdx"),
                TutorialWire.WriteMasterSlot(_packetTables, _build, TutorialSeat), ct).ConfigureAwait(false);
        }

        if (_skipStart)
        {
            // Live-experiment switch: stop after the play room to bisect a client crash between
            // the room body and the match-start packet.
            player.Session.MoveTo(SessionPhase.Room);
            _logger.LogInformation("Session {Id} tutorial experiment: stopped after gspGotoRoom kind {Kind}.", session.Id, RoomCodec.PlayRoomKind);
            return;
        }

        if (_play.SkipEnter)
        {
            // Live bisection: the room, the change and the seats go out; the enter stays behind
            // so a client crash splits between the seat push and the enter packet.
            player.Session.MoveTo(SessionPhase.Room);
            _logger.LogInformation("Session {Id} tutorial experiment: stopped before gspRoomEnterGame.", session.Id);
            return;
        }

        string startName;
        byte[] startBody;
        switch (_matchStart)
        {
            case TutorialMatchStart.RoomEnterGame:
                // gspUserHostStart (991) returns before cn-717 sub_60A490 unless room+532 is set; that
                // byte is the host-ready UI checkbox (cn-717 sub_8FB7B0), never armed here.
                // gspRoomEnterGame case 0 does not check +532 (cn-717 sub_882D40).
                startName = "gspRoomEnterGame";
                startBody = TutorialWire.WriteRoomEnterGame(_packetTables, _build,
                    unknown0: 0,
                    unknown1: oldTutorial ? tutorialMap : request.MapKey,
                    unknown4: EnterGameRoomState,
                    scoreboardValues: TutorialWire.EntryNeedsScoreboard(_packetTables, _build) ? DefaultScoreboardData() : null);
                break;
            case TutorialMatchStart.RepAutoStart:
                startName = "gspRepAutoStart";
                startBody = AutoStartCodec.Encode(_packetTables, _build, enabled: true);
                break;
            default:
                throw new InvalidOperationException($"Unhandled tutorial match start {_matchStart}.");
        }

        await session.SendAsync(table.S2C(startName), startBody, ct).ConfigureAwait(false);
        if (_matchStart == TutorialMatchStart.RoomEnterGame)
            _awaitingLoadComplete[session.Id] = request.TimeAttackTimeLimitMs;
        player.Session.MoveTo(SessionPhase.Room);
        _logger.LogInformation(
            "Session {Id} tutorial start: S2C gspGotoRoom kind {Kind} ({RoomBytes} bytes), {StartName} ({StartBytes} bytes, mapKey={MapKey}, unknown0={Unknown0}, unknown1={Unknown1}, difficulty={Difficulty}, timeAttackMs={TimeAttackMs}).",
            session.Id, RoomCodec.PlayRoomKind, room.Length, startName, startBody.Length,
            request.MapKey, request.Unknown0, request.Unknown1, request.Difficulty, request.TimeAttackTimeLimitMs);
    }

    /// <summary>
    /// Handles <c>gcpGameReportMapLoadingComplete</c> on the one-seat tutorial path: marks the
    /// sole seat loaded, signals everybody-loaded, arms the countdown, waits that duration, then
    /// sends <c>gspGameStartGame</c>. Multi-seat wait is not implemented (one seat only).
    ///
    /// </summary>
    /// <summary>
    /// Answers the load report when this session is waiting in a one-seat tutorial; otherwise leaves
    /// it to the caller, whose round the report belongs to.
    ///
    /// </summary>
    /// <returns>Whether the report was this tutorial's.</returns>
    public async ValueTask<bool> TryHandleMapLoadingCompleteAsync(ISession session, InboundPacket packet, CancellationToken ct)
    {
        if (!_awaitingLoadComplete.ContainsKey(session.Id))
            return false;
        await HandleMapLoadingCompleteAsync(session, packet, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>Admits a client-driven tutorial at its native auto-start expiry request.</summary>
    internal async ValueTask<bool> TryHandleEnterGameAsync(ISession session, InboundPacket packet, CancellationToken ct)
    {
        TutorialRoundOpened? admission;
        lock (_tutorialGate)
        {
            if (_matchStart != TutorialMatchStart.RepAutoStart || !_tutorialSessions.ContainsKey(session.Id))
                return false;
            _pendingClientStarts.TryGetValue(session.Id, out admission);
        }
        var request = PlazaRoomWire.ReadRoomReqEnterGame(_packetTables, _build, packet.Payload.Span);
        if (admission is null) return true;
        if (!string.Equals(request.MapKey, admission.MapKey, StringComparison.Ordinal))
            throw new PacketFormatException("Tutorial enter map differs from its selected map.", 0);
        lock (_tutorialGate) _pendingClientStarts.Remove(session.Id);
        if (_sharedLifecycle)
            await DispatchLifecycleAsync(session, admission, ct).ConfigureAwait(false);
        // id-400 sub_61D490 -> sub_620B20 and id-1809 sub_63E610 -> sub_641BC0
        // emit this request after the native five-second timer, not when it is armed.
        await session.SendAsync(_tables.Get(session.Profile.Id).S2C("gspRoomEnterGame"),
            TutorialWire.WriteRoomEnterGame(_packetTables, _build,
                0, admission.MapKey, unknown4: EnterGameRoomState,
                scoreboardValues: TutorialWire.EntryNeedsScoreboard(_packetTables, _build) ? DefaultScoreboardData() : null), ct).ConfigureAwait(false);
        _awaitingLoadComplete[session.Id] = admission.TimeLimitMs;
        _logger.LogInformation("Session {Id} client-driven tutorial entered on {Map} through its native callback.",
            session.Id, admission.MapKey);
        return true;
    }

    /// <summary>Returns a client-driven tutorial to the menu even before its hidden room exists.</summary>
    internal async ValueTask<bool> TryHandleLeaveAsync(ISession session, InboundPacket packet, CancellationToken ct)
    {
        bool pending;
        lock (_tutorialGate)
        {
            if (_matchStart != TutorialMatchStart.RepAutoStart || !_tutorialSessions.ContainsKey(session.Id))
                return false;
            pending = _pendingClientStarts.ContainsKey(session.Id);
        }
        if (packet.Payload.Length != 0)
            throw new PacketFormatException("Tutorial leave request must be empty.", 0);
        if (!pending)
        {
            await HandleEndStageAsync(session, new(packet.Opcode, new byte[4]), ct).ConfigureAwait(false);
            return true;
        }
        await ClearTutorialStateAsync(session, ct).ConfigureAwait(false);
        // The menu seat shows the worn character (UserSlot character_type, kr-812 sub_6CBFF0).
        var player = await ResolvePlayerAsync(session, ct).ConfigureAwait(false);
        var table = _tables.Get(session.Profile.Id);
        await session.SendAsync(table.S2C("gspGotoRoom"), RoomCodec.WriteMainMenuEnvelope(player.Room, _packetTables, _build, _room), ct)
            .ConfigureAwait(false);
        await session.SendAsync(table.S2C("gspGotoMenu"), RoomCodec.WriteSceneTransition(_packetTables, _build), ct).ConfigureAwait(false);
        player.Session.MoveTo(SessionPhase.Plaza);
        player.Session.MoveTo(SessionPhase.MainMenu);
        return true;
    }

    internal ValueTask<bool> TryHandleMapSuppliesAsync(ISession session, RoundSuppliesReported report, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_tutorialGate)
            if (!_tutorialSessions.ContainsKey(session.Id))
                return ValueTask.FromResult(false);
        if (_sharedLifecycle)
        {
            lock (_tutorialGate)
                if (_tutorialSessions.TryGetValue(session.Id, out var entry) && entry.Waves)
                    return ValueTask.FromResult(false);
        }
        // cn-324 TutorialStage owns its native tutorial objects.
        // Its native tutorial objects must not start the multiplayer supply timer.
        _logger.LogInformation("Session {Id} tutorial map supply report accepted ({Points} points); multiplayer supply scheduling is not used.",
            session.Id, report.Points.Count);
        return ValueTask.FromResult(true);
    }

    internal ValueTask<bool> TryHandleReadyAsync(ISession session, RoomReadyRequested request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_tutorialGate)
            if (!_tutorialSessions.ContainsKey(session.Id))
                return ValueTask.FromResult(false);
        // A one-seat tutorial starts through its load barrier, not multiplayer readiness.
        _logger.LogInformation("Session {Id} tutorial readiness report accepted; the tutorial load barrier retains start authority.", session.Id);
        return ValueTask.FromResult(true);
    }

    public async ValueTask HandleMapLoadingCompleteAsync(ISession session, InboundPacket packet, CancellationToken ct)
    {
        MapLoadingCompleteReport report;
        try
        {
            report = TutorialWire.ReadMapLoadingComplete(_packetTables, _build, packet.Payload.Span);
        }
        catch (PacketFormatException ex)
        {
            _logger.LogError(
                "Session {Id} gcpGameReportMapLoadingComplete does not match the expected layout ({Reason}); closing.",
                session.Id, ex.Message);
            await session.Connection.CloseAsync("malformed gcpGameReportMapLoadingComplete").ConfigureAwait(false);
            return;
        }

        if (!_awaitingLoadComplete.TryRemove(session.Id, out var matchClockMs))
        {
            _logger.LogDebug(
                "Session {Id} gcpGameReportMapLoadingComplete ignored (no pending one-seat tutorial load).",
                session.Id);
            return;
        }

        RequirePlayer(session);
        if (_sharedLifecycle)
        {
            await DispatchLifecycleAsync(session, new RoundLoadReported(), ct).ConfigureAwait(false);
            return;
        }
        var table = _tables.Get(session.Profile.Id);

        // Seat 0: matches GotoRoom my-slot default and the Cn717 UserSlot seat-index byte.
        var seatBody = TutorialWire.WriteMapLoadingComplete(_packetTables, _build, TutorialSeat);
        await session.SendAsync(table.S2C("gspGameMapLoadingComplete"), seatBody, ct).ConfigureAwait(false);

        // cn-717 sub_886D80 requires the first loading gate; the tutorial has no pre-countdown.
        await RoundStartSender.SendCountdownAsync(_packetTables, _build, table, false, session,
            new RoundCountdownStarted(0, TutorialCountDownMs) { IsTutorial = true }, ct)
            .ConfigureAwait(false);
        _logger.LogInformation(
            "Session {Id} tutorial load-wait: S2C gspGameMapLoadingComplete seat {Seat}, gspGameEverybodyLoadingComplete, gspGameCountDown {Ms} ms (C2S unknown1={Unknown1}).",
            session.Id, TutorialSeat, TutorialCountDownMs, report.Unknown1);

        // Same pattern as kr-1256 MatchStartFlow: arm the countdown, then start after it elapses.
        await Task.Delay(TutorialCountDownMs, ct).ConfigureAwait(false);

        // Field0 arms GC+1744 via sub_B20AA0 (sub_886D80). Non-zero is required so
        // sub_B20B70(+1744) is true and sub_6BE640 clears stage+2248 letterbox on mode 103/15.
        // Prologue MapMeta TimeAttackTimeLimit (typically 99999000) is near-unlimited; mode 103
        // also sets stage+4324=0 while the clock runs (hides the +4320 prologue widget). Field1
        // stays 0 (dword_FF93D8); field2 mirrors field0 into session+4620.
        await RoundStartSender.SendStartedAsync(_packetTables, _build, table, session,
            new RoundStarted(0, matchClockMs) { IsTutorial = true }, ct).ConfigureAwait(false);
        _logger.LogInformation("Session {Id} tutorial load-wait: shared start fields {Clock}/0/{MirroredClock}.",
            session.Id, matchClockMs, matchClockMs);
    }

    /// <summary>
    /// Relays host <c>gppCreateMobsFromSpawnGroup</c> without duplicating native local execution.
    /// CN2012 queues the request itself (cn-324 sub_5E2CD0, sub_465EA0, sub_465F90);
    /// returning it to that sender would execute the same spawn group twice.
    ///
    /// </summary>
    public async ValueTask HandleCreateMobsFromSpawnGroupAsync(ISession session, InboundPacket packet, CancellationToken ct)
    {
        int spawnGroup;
        try
        {
            spawnGroup = TutorialWire.ReadCreateMobsFromSpawnGroup(_packetTables, _build, packet.Payload.Span);
        }
        catch (PacketFormatException ex)
        {
            _logger.LogError(
                "Session {Id} gppCreateMobsFromSpawnGroup does not match the expected layout ({Reason}); closing.",
                session.Id, ex.Message);
            await session.Connection.CloseAsync("malformed gppCreateMobsFromSpawnGroup").ConfigureAwait(false);
            return;
        }

        var table = _tables.Get(session.Profile.Id);
        var body = TutorialWire.WriteCreateMobsFromSpawnGroup(_packetTables, _build, spawnGroup);
        if (_spawnGroupAppliesLocally)
        {
            await TryDispatchRoomFeedAsync(session, table.S2C("gppCreateMobsFromSpawnGroup"), body, ct).ConfigureAwait(false);
            _logger.LogInformation(
                "Session {Id} CreateMobs: forwarded spawn group {Group} to peers; sender executes its native local queue.",
                session.Id, spawnGroup);
            return;
        }
        await session.SendAsync(table.S2C("gppCreateMobsFromSpawnGroup"), body, ct).ConfigureAwait(false);
        _logger.LogInformation(
            "Session {Id} tutorial CreateMobs: echoed gppCreateMobsFromSpawnGroup group {Group}.",
            session.Id, spawnGroup);
    }

    /// <summary>
    /// Answers host <c>gcpReqStartNewWave</c> (903) with <c>gspRepStartNewWave</c> (904).
    /// Without 904 the wave mgr stays latched (<c>sub_ABD9A0</c> never runs), the NPC
    /// counter stays 0, and the host never emits C2S 908 / spawn-group frames.
    ///
    /// </summary>
    public async ValueTask HandleStartNewWaveAsync(ISession session, InboundPacket packet, CancellationToken ct)
    {
        int wave;
        try
        {
            wave = TutorialWire.ReadStartNewWave(_packetTables, _build, packet.Payload.Span);
        }
        catch (PacketFormatException ex)
        {
            _logger.LogError(
                "Session {Id} gcpReqStartNewWave does not match the expected layout ({Reason}); closing.",
                session.Id, ex.Message);
            await session.Connection.CloseAsync("malformed gcpReqStartNewWave").ConfigureAwait(false);
            return;
        }

        var table = _tables.Get(session.Profile.Id);
        var body = TutorialWire.WriteStartNewWave(_packetTables, _build, wave);
        if (await TryDispatchRoomWaveAsync(session, wave, true, ct).ConfigureAwait(false)) return;
        await session.SendAsync(table.S2C("gspRepStartNewWave"), body, ct).ConfigureAwait(false);
        _logger.LogInformation(
            "Session {Id} tutorial wave: S2C gspRepStartNewWave wave {Wave}.",
            session.Id, wave);
    }

    /// <summary>
    /// Answers host <c>gcpReqEndWave</c> with <c>gspRepEndWave</c> (S2C 905). The client's handler
    /// (cn-717 <c>sub_ABDF10</c>, case <c>0x389</c>) stores the wave, clears its wave-in-progress
    /// byte and fires the NPC-wave signal the tutorial's next step hangs off; a cleared wave whose
    /// report is never answered leaves the round inside that wave with nothing following it.
    ///
    /// </summary>
    /// <param name="session">The session that reported the wave end.</param>
    /// <param name="packet">The one-u32 request.</param>
    /// <param name="ct">Cancellation for the reply.</param>
    public async ValueTask HandleEndWaveAsync(ISession session, InboundPacket packet, CancellationToken ct)
    {
        int wave;
        try
        {
            wave = TutorialWire.ReadStartNewWave(_packetTables, _build, packet.Payload.Span);
        }
        catch (PacketFormatException ex)
        {
            _logger.LogError(
                "Session {Id} gcpReqEndWave does not match the expected layout ({Reason}); closing.",
                session.Id, ex.Message);
            await session.Connection.CloseAsync("malformed gcpReqEndWave").ConfigureAwait(false);
            return;
        }

        var table = _tables.Get(session.Profile.Id);
        if (await TryDispatchRoomWaveAsync(session, wave, false, ct).ConfigureAwait(false)) return;
        await session.SendAsync(table.S2C("gspRepEndWave"), TutorialWire.WriteStartNewWave(_packetTables, _build, wave), ct)
            .ConfigureAwait(false);
        _logger.LogInformation(
            "Session {Id} tutorial wave: S2C gspRepEndWave wave {Wave}.",
            session.Id, wave);
    }

    /// <summary>
    /// Returns the host's bounded opaque wave table to the room and its sender. kr-812 sub_A07E90
    /// clears host wave-manager+172 or installs the peer's groups. The preceding empty S2C908 invokes kr-812
    /// sub_A12410's actor cleanup; its adjacent ordering is retained compatibility behavior.
    /// </summary>
    public async ValueTask HandleWaveAllSpawnMobsAsync(ISession session, InboundPacket packet, CancellationToken ct)
    {
        try
        {
            TutorialWire.CheckWaveAllSpawnMobs(packet.Payload.Span);
        }
        catch (PacketFormatException ex)
        {
            _logger.LogError(
                "Session {Id} gcpReqWaveAllSpawnMobs does not match the expected layout ({Reason}); closing.",
                session.Id, ex.Message);
            await session.Connection.CloseAsync("malformed gcpReqWaveAllSpawnMobs").ConfigureAwait(false);
            return;
        }

        var table = _tables.Get(session.Profile.Id);
        if (await TryDispatchRoomFeedAsync(session, table.S2C("Unknown908"), ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false))
        {
            // RoundFeedSent excludes its sender; the host still needs the direct reply below.
            await TryDispatchRoomFeedAsync(session, table.S2C("gspRepWaveAllSpawnMobs"), packet.Payload, ct).ConfigureAwait(false);
        }
        await session.SendAsync(table.S2C("Unknown908"), ReadOnlyMemory<byte>.Empty, ct)
            .ConfigureAwait(false);
        await session.SendAsync(table.S2C("gspRepWaveAllSpawnMobs"), packet.Payload, ct)
            .ConfigureAwait(false);
        _logger.LogInformation(
            "Session {Id} tutorial wave: S2C Unknown908 empty + gspRepWaveAllSpawnMobs ({C2SBytes} bytes).",
            session.Id, packet.Payload.Length);
    }

    /// <summary>
    /// Relays host <c>gppCreateAIObject</c> (32892). Wave tick <c>sub_69E9D0</c> emits one
    /// per ChrSpawner after +172 clears; apply is S2C case <c>0x807C</c> →
    /// <c>sub_54F2D0</c>. Body bytes (ids / pose) are echoed unchanged.
    ///
    /// </summary>
    public async ValueTask HandleCreateAIObjectAsync(ISession session, InboundPacket packet, CancellationToken ct)
    {
        try
        {
            TutorialWire.CheckMobCreateBody(packet.Payload.Span, "gppCreateAIObject");
        }
        catch (PacketFormatException ex)
        {
            _logger.LogError(
                "Session {Id} gppCreateAIObject does not match the expected layout ({Reason}); closing.",
                session.Id, ex.Message);
            await session.Connection.CloseAsync("malformed gppCreateAIObject").ConfigureAwait(false);
            return;
        }

        var table = _tables.Get(session.Profile.Id);
        if (await TryDispatchRoomFeedAsync(session, table.S2C("gppCreateAIObject"), packet.Payload, ct).ConfigureAwait(false)) return;
        await session.SendAsync(table.S2C("gppCreateAIObject"), packet.Payload, ct).ConfigureAwait(false);
        _logger.LogInformation(
            "Session {Id} tutorial CreateAIObject: echoed gppCreateAIObject ({Bytes} bytes, {Hex}).",
            session.Id, packet.Payload.Length, Convert.ToHexString(packet.Payload.Span));
    }

    /// <summary>
    /// Ends the round when the host reports every wave done (<c>gcpReqEndAllWave</c>, 905, empty).
    /// The host reports the per-wave end with <c>gcpReqEndWave</c> and the stage with
    /// <c>gcpReqEndStage</c>, but a one-seat tutorial has no typed round for those reports to reach,
    /// so it answers this one: the round's end, then the empty <c>gspGameShowResult_</c> that opens
    /// the result scene, then <c>gspGotoRoom</c> back to the lobby.
    ///
    /// </summary>
    /// <param name="session">The session that reported the end.</param>
    /// <param name="packet">The report, which carries nothing.</param>
    /// <param name="ct">Cancellation for the replies.</param>
    public async ValueTask HandleEndAllWaveAsync(ISession session, InboundPacket packet, CancellationToken ct)
    {
        if (packet.Payload.Length != 0)
            throw new PacketFormatException("gcpReqEndAllWave must be empty.", 0);
        bool tutorial;
        lock (_tutorialGate) tutorial = _tutorialSessions.ContainsKey(session.Id);
        if (!tutorial)
        {
            if (_messages is null || !await _messages.DispatchAsync(session.Profile.Id + "/game", session,
                    new RoundEndRequested { Reason = RoundEndReason.WavesCleared }, ct).ConfigureAwait(false))
                throw new InvalidOperationException("A multiplayer NPC round end has no round handler.");
            return;
        }
        if (_sharedLifecycle)
            await DispatchLifecycleAsync(session, new TutorialRoundClosed(), ct).ConfigureAwait(false);
        var table = _tables.Get(session.Profile.Id);
        await session.SendAsync(
            table.S2C("gspGameEndGame"),
            PlayRoomCodec.WriteGameEnd(_packetTables, _build, RoundOutcome.Won, _outcomeFirst),
            ct).ConfigureAwait(false);
        // No result scene follows a tutorial: it ends straight into the lobby, and the empty
        // gspGameShowResult_ that opens a played round's result scene leaves cn-324's scene without
        // the scoreboard its only starter, gspGameTimeOverEnd, carries, which ends its game on a
        // resource error.
        // The end alone only sets the result stage's gate, so the return below is what leaves the
        // stage.
        // The menu seat shows the worn character (UserSlot character_type, kr-812 sub_6CBFF0).
        var player = await ResolvePlayerAsync(session, ct).ConfigureAwait(false);
        await session.SendAsync(
            table.S2C("gspGotoRoom"),
            RoomCodec.WriteMainMenuEnvelope(player.Room, _packetTables, _build, _room),
            ct).ConfigureAwait(false);
        lock (_tutorialGate)
        {
            _tutorialSessions.Remove(session.Id);
            _pendingClientStarts.Remove(session.Id);
        }
        _awaitingLoadComplete.TryRemove(session.Id, out _);
        player.Session.MoveTo(SessionPhase.Plaza);
        player.Session.MoveTo(SessionPhase.MainMenu);
        _logger.LogInformation("Session {Id} tutorial stage end: S2C gspGameEndGame win + gspGotoRoom main menu.", session.Id);
    }

    /// <summary>Consumes native single-stage tutorial completion, leaving wave rounds to their owner.</summary>
    public async ValueTask<bool> TryHandleEndStageAsync(ISession session, InboundPacket packet, CancellationToken ct)
    {
        lock (_tutorialGate)
        {
            if (!_tutorialSessions.TryGetValue(session.Id, out var entry) || entry.Waves)
                return false;
        }
        await HandleEndStageAsync(session, packet, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>Finishes a native single-stage tutorial on its completion word and returns to the menu.</summary>
    public async ValueTask HandleEndStageAsync(ISession session, InboundPacket packet, CancellationToken ct)
    {
        var completed = TutorialWire.ReadEndStage(_packetTables, _build, packet.Payload.Span);
        TutorialEntry entry;
        lock (_tutorialGate)
        {
            if (!_tutorialSessions.TryGetValue(session.Id, out entry) || entry.Waves)
                throw new InvalidOperationException("Stage-end request requires an active native single-stage tutorial.");
        }
        // Only the old tutorial is the initial tutorial; a tutorial-room stage is recorded as a stage clear instead.
        if (completed && entry.Old && _messages is not null
            && !await _messages.DispatchAsync(session.Profile.Id + "/game", session, new InitialTutorialCompleted(), ct).ConfigureAwait(false))
            throw new InvalidOperationException("Initial tutorial completion has no persistence handler.");
        // The clear is reported while the round still runs, so its time is measured from the round's start.
        if (completed && !entry.Old && _sharedLifecycle)
            await DispatchLifecycleAsync(session, new TutorialStageCleared(entry.MapKey), ct).ConfigureAwait(false);
        if (_sharedLifecycle)
            await DispatchLifecycleAsync(session, new TutorialRoundClosed(), ct).ConfigureAwait(false);
        lock (_tutorialGate)
        {
            _tutorialSessions.Remove(session.Id);
            _pendingClientStarts.Remove(session.Id);
        }
        _awaitingLoadComplete.TryRemove(session.Id, out _);
        var table = _tables.Get(session.Profile.Id);
        // The menu seat shows the worn character (UserSlot character_type, kr-812 sub_6CBFF0).
        var player = await ResolvePlayerAsync(session, ct).ConfigureAwait(false);
        // cn-279 sub_705DF0 consumes the stage end; cn-279 sub_6FF6C0 maps
        // GotoRoom kind 1 back to MainMenuStage without a multiplayer result scene.
        await session.SendAsync(table.S2C("gspGameEndGame"),
            PlayRoomCodec.WriteGameEnd(_packetTables, _build, completed ? RoundOutcome.Won : RoundOutcome.Lost, _outcomeFirst), ct)
            .ConfigureAwait(false);
        // gspGameEndGame alone shows the time-over (GAME OVER) cue in mode 15 (kr-812 sub_803DF0). gspGameTimeOverEnd then
        // moves the result object to its stage step, where mode 15 shows the stage's own clear or fail panel from its
        // clear flag (kr-812 sub_804B10 -> sub_5ACB20). gspGameShowResult_ is not sent: it opens the 3D result ceremony
        // (kr-812 sub_5AD030). The old tutorial maps straight back to the menu without either (cn-279 sub_6FF6C0).
        if (!entry.Old)
        {
            if (_scoreMs > 0)
                await Task.Delay(_scoreMs, ct).ConfigureAwait(false);
            if (table.Find(PacketDirection.ServerToClient, "gspGameTimeOverEnd") is { } timeOverEnd)
                await session.SendAsync(timeOverEnd, PlayRoomCodec.WriteScoreboard(_packetTables, _build,
                    StageScoreboard(player.Room.Nickname, completed)), ct).ConfigureAwait(false);
        }
        if (_resultMs > 0)
            await Task.Delay(_resultMs, ct).ConfigureAwait(false);
        if (!entry.Old)
        {
            // A tutorial-room stage returns to the room it was chosen in, whose stage then asks for the cleared list
            // again (gcpReqSingleStageInfo from the TutorialRoomStage constructor, kr-812 sub_6B0BE0).
            await SendTutorialSelectAsync(session, player, ct).ConfigureAwait(false);
            _logger.LogInformation("Session {Id} tutorial stage {Map} ended: completed={Completed}, returned to the tutorial room.",
                session.Id, entry.MapKey, completed);
            return;
        }
        await session.SendAsync(table.S2C("gspGotoRoom"), RoomCodec.WriteMainMenuEnvelope(player.Room, _packetTables, _build, _room), ct)
            .ConfigureAwait(false);
        await session.SendAsync(table.S2C("gspGotoMenu"), RoomCodec.WriteSceneTransition(_packetTables, _build), ct).ConfigureAwait(false);
        player.Session.MoveTo(SessionPhase.Plaza);
        player.Session.MoveTo(SessionPhase.MainMenu);
        _logger.LogInformation("Session {Id} old tutorial ended: completed={Completed}, returned to main menu.", session.Id, completed);
    }

    // The one-seat stage's scoreboard: the player alone on team 1, the winner on a clear and the loser otherwise, so
    // the result scene, which names the winner from the team totals (cn-324 sub_455ED0), agrees with the stage end.
    private static RoundScoreboard StageScoreboard(string nickname, bool completed) =>
        new(0, completed ? 1 : 2, completed ? [1, 0] : [0, 1],
            [new RoundScoreboardRow(TutorialSeat, nickname, 1, 1, new RoundSeatStats(TutorialSeat, 0, 0, 0, 0), null)]);

    /// <summary>Releases pending tutorial state when its connection closes.</summary>
    public void OnConnectionClosed(ConnectionClosedEvent closed)
    {
        lock (_tutorialGate)
        {
            _tutorialSessions.Remove(closed.Connection.Id);
            _pendingClientStarts.Remove(closed.Connection.Id);
        }
        _awaitingLoadComplete.TryRemove(closed.Connection.Id, out _);
    }

    /// <summary>Releases one-seat state before the same player enters a multiplayer room.</summary>
    internal async ValueTask ClearTutorialStateAsync(ISession session, CancellationToken ct)
    {
        bool active;
        lock (_tutorialGate) active = _tutorialSessions.ContainsKey(session.Id);
        if (active && _sharedLifecycle)
            await DispatchLifecycleAsync(session, new TutorialRoundClosed(), ct).ConfigureAwait(false);
        lock (_tutorialGate)
        {
            _tutorialSessions.Remove(session.Id);
            _pendingClientStarts.Remove(session.Id);
        }
        _awaitingLoadComplete.TryRemove(session.Id, out _);
    }

    private async ValueTask DispatchLifecycleAsync<T>(ISession session, T message, CancellationToken ct) where T : IMessage
    {
        if (_messages is null || !await _messages.DispatchAsync(session.Profile.Id + "/game", session, message, ct).ConfigureAwait(false))
            throw new InvalidOperationException($"Tutorial lifecycle has no handler for {typeof(T).Name}.");
    }

    /// <summary>Clears the row's bounded tutorial state when it unloads.</summary>
    public void Dispose()
    {
        lock (_tutorialGate)
        {
            _tutorialSessions.Clear();
            _pendingClientStarts.Clear();
        }
        _awaitingLoadComplete.Clear();
    }

    /// <summary>
    /// Relays host <c>gppLifePoint</c> (32920). The client applies the carried points to the named
    /// mob (case <c>0x8098</c> -> cn-717 <c>sub_544FA0</c>); a mob whose points never arrive spends
    /// none of them, so its hp reaching zero kills it outright instead of bubbling it. Body bytes
    /// are echoed unchanged.
    ///
    /// </summary>
    /// <param name="session">The session that announced the points.</param>
    /// <param name="packet">The host's body.</param>
    /// <param name="ct">Cancellation for the relay.</param>
    public async ValueTask HandleLifePointAsync(ISession session, InboundPacket packet, CancellationToken ct)
    {
        var table = _tables.Get(session.Profile.Id);
        if (await TryDispatchRoomFeedAsync(session, table.S2C("gppLifePoint"), packet.Payload, ct).ConfigureAwait(false)) return;
        await session.SendAsync(table.S2C("gppLifePoint"), packet.Payload, ct).ConfigureAwait(false);
        _logger.LogInformation(
            "Session {Id} tutorial LifePoint: echoed gppLifePoint ({Bytes} bytes, {Hex}).",
            session.Id, packet.Payload.Length, Convert.ToHexString(packet.Payload.Span));
    }

    /// <summary>
    /// Relays host <c>gppCreateSpawnGroupMemberMob</c> (32862). <c>sub_744160</c> emits one
    /// per spawn-group member after S2C 32857; apply is case <c>0x805E</c> →
    /// <c>sub_7479E0</c>. Body bytes are echoed unchanged.
    ///
    /// </summary>
    public async ValueTask HandleCreateSpawnGroupMemberMobAsync(ISession session, InboundPacket packet, CancellationToken ct)
    {
        try
        {
            TutorialWire.CheckMobCreateBody(packet.Payload.Span, "gppCreateSpawnGroupMemberMob");
        }
        catch (PacketFormatException ex)
        {
            _logger.LogError(
                "Session {Id} gppCreateSpawnGroupMemberMob does not match the expected layout ({Reason}); closing.",
                session.Id, ex.Message);
            await session.Connection.CloseAsync("malformed gppCreateSpawnGroupMemberMob").ConfigureAwait(false);
            return;
        }

        var table = _tables.Get(session.Profile.Id);
        if (await TryDispatchRoomFeedAsync(session, table.S2C("gppCreateSpawnGroupMemberMob"), packet.Payload, ct).ConfigureAwait(false)) return;
        await session.SendAsync(table.S2C("gppCreateSpawnGroupMemberMob"), packet.Payload, ct)
            .ConfigureAwait(false);
        _logger.LogInformation(
            "Session {Id} tutorial CreateSpawnGroupMemberMob: echoed ({Bytes} bytes, {Hex}).",
            session.Id, packet.Payload.Length, Convert.ToHexString(packet.Payload.Span));
    }

    private async ValueTask<bool> TryDispatchRoomFeedAsync(ISession session, ushort code, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        lock (_tutorialGate)
            if (_tutorialSessions.ContainsKey(session.Id)) return false;
        if (_messages is null || !await _messages.DispatchAsync(session.Profile.Id + "/game", session,
                new RoundFeedSent(code, payload.ToArray()), ct).ConfigureAwait(false))
            throw new InvalidOperationException("A multiplayer NPC update has no round handler.");
        return true;
    }

    private async ValueTask<bool> TryDispatchRoomWaveAsync(ISession session, int wave, bool started, CancellationToken ct)
    {
        lock (_tutorialGate)
            if (_tutorialSessions.ContainsKey(session.Id)) return false;
        if (_messages is null || !await _messages.DispatchAsync(session.Profile.Id + "/game", session,
                new RoundWaveReported(wave, started), ct).ConfigureAwait(false))
            throw new InvalidOperationException("A multiplayer NPC wave has no round handler.");
        return true;
    }

    private sealed record PlayerContext(MainMenuRoomOpened Room, IPlayerSession Session);

    private async ValueTask<PlayerContext> ResolvePlayerAsync(ISession session, CancellationToken ct)
    {
        var player = RequirePlayer(session);
        if (_inventory is null) return player;
        var bag = await _inventory.ListSlotsAsync(player.Room.UserNumber, InventoryBag.CharacterBag,
            InventoryLimits.MaxInventorySlotsPerBag, ct).ConfigureAwait(false);
        // Reference-only login profiles publish no stored bag; their scene uses the login starter snapshot.
        if (bag.Count == 0) return player;
        if (_events is not null)
            bag = await _events.WaterfallAsync<EquipmentSelectionReadRequested, IReadOnlyList<InventorySlotRecord>>(
                new(session.Profile.Id + "/game", player.Room.UserNumber), _ => ValueTask.FromResult(bag), ct).ConfigureAwait(false);
        var worn = bag.Where(row => (row.Flags & InventorySlotRecord.WornFlagBit) != 0)
            .Select(row => row.ItemId).Distinct().ToArray();
        var character = worn.Single(id => id is >= 300001 and <= 300031);
        return player with { Room = player.Room with { CharacterItemId = character, EquippedItemIds = worn } };
    }

    /// <summary>
    /// The default <c>ScoreData</c> the scoreboard-data EnterGame shapes carry: the build's row
    /// table with every seat vacant and zero totals, the same body the round's scoreboard sends.
    ///
    /// </summary>
    private IReadOnlyDictionary<string, object?>? DefaultScoreboardData()
    {
        if (_scoreRowFields.Count == 0 || _score is null)
            return null;
        var board = new RoundScoreboard(0, 0, [0, 0], []);
        return PlayRoomCodec.ScoreboardValues(_packetTables, _build, board);
    }

    private static PlayerContext RequirePlayer(ISession session)
    {
        if (!session.TryFeature<IPlayerSession>(out var player))
            throw new InvalidOperationException($"Session {session.Id} has no IPlayerSession; login must complete before tutorial.");
        // Four-arg MainMenuRoomOpened only. Mode / map key are WriteRoom parameters — never
        // optional ctor args on this message (those made Room.dll require a six-arg ctor the
        // host Contracts assembly lacked; CreateTutorial then MissingMethodException-closed).
        var room = new MainMenuRoomOpened(
            player.Identity.UserNum,
            player.Identity.Nickname,
            DefaultCharacterItemId,
            [DefaultCharacterItemId]) { ColorIndex = player.Identity.ColorIndex };
        return new PlayerContext(room, player);
    }
}
