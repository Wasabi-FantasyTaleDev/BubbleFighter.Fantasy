using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.InGame.Data;
using BubbleFighter.Fantasy.InGame.Sessions;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.InGame.Handlers;

/// <summary>
/// A round driven by typed messages only, from the loading barrier to the running round: each
/// player's load report is shown to the room, the countdown starts once every seated player has
/// loaded, and the round starts when it runs out. While a round is starting or running the
/// build's gameplay feed is passed between its players unread.
/// </summary>
internal sealed partial class RoundFlow
{
    private readonly RoomRegistry _registry;
    private readonly InGameOptions _options;
    private readonly ILogger _logger;
    private readonly CancellationToken _lifetime;
    private readonly IMessageSender<RoundPlayerLoaded>? _playerLoaded;
    private readonly IMessageSender<RoundCountdownStarted> _countdown;
    private readonly IMessageSender<RoundStarted> _started;
    private readonly IMessageSender<RoundFeedReceived>? _feed;
    private readonly IMessageSender<RoundEnded>? _ended;
    private readonly IMessageSender<RoundSpawnOffered>? _spawnOffered;
    private readonly IMessageSender<RoundRespawnWaitChanged>? _waitChanged;
    private readonly IMessageSender<RoundPlayerRespawned>? _respawned;
    private readonly Func<PlayRoom, CancellationToken, ValueTask> _returnToRoom;
    private readonly IInventoryRepository? _revivalInventory;
    private readonly IInventoryPushService? _revivalInventoryPush;
    private readonly IMatchCapabilityOfferSender? _capabilityOffers;

    /// <summary>Builds the flow over the build's round encoders.</summary>
    /// <param name="registry">The room registry.</param>
    /// <param name="options">The row configuration: countdown length, round time limit and result delays.</param>
    /// <param name="senders">The build's encoders for what the flow sends.</param>
    /// <param name="logger">The row logger.</param>
    /// <param name="lifetime">Cancelled when the row unloads; pending countdowns stop with it.</param>
    /// <param name="returnToRoom">Sends every member back into the waiting room; runs on the room's executor.</param>
    /// <param name="context">The owning row's context, which the result surface is resolved from; null runs the round without one.</param>
    /// <param name="capabilityOffers">Native offer encoder; when omitted an enabled native relay policy resolves it from the row's services.</param>
    public RoundFlow(RoomRegistry registry, InGameOptions options, RoundSenders senders, ILogger logger, CancellationToken lifetime,
        Func<PlayRoom, CancellationToken, ValueTask> returnToRoom, IPluginContext? context = null,
        IMatchCapabilityOfferSender? capabilityOffers = null)
    {
        _returnToRoom = returnToRoom;
        _ended = senders.Ended;
        _registry = registry;
        _options = options;
        _logger = logger;
        _lifetime = lifetime;
        _playerLoaded = senders.PlayerLoaded;
        _countdown = senders.Countdown;
        _started = senders.Started;
        _feed = senders.Feed;
        _spawnOffered = senders.SpawnOffered;
        _waitChanged = senders.WaitChanged;
        _respawned = senders.Respawned;
        if (options.NativeMatchRelay)
        {
            var matches = capabilityOffers is null
                ? context?.Services.GetAll<IMatchCapabilityOfferSender>().Where(sender => sender.Protocol == options.GameProtocol).ToArray() ?? []
                : [capabilityOffers];
            if (matches.Length != 1 || matches[0].Protocol != options.GameProtocol
                || matches[0].TicketLength < 1 || matches[0].TicketLength > options.MaxMatchCapabilityBytes)
                throw new ConfigException("Native match relay requires exactly one owning game offer sender with a bounded key length.");
            _capabilityOffers = matches[0];
        }
        if (options.InstantRevivalItemId > 0)
        {
            if (context is null || !context.Services.TryGet<IInventoryRepository>(out _revivalInventory)
                || !context.Services.TryGet<IInventoryPushService>(out _revivalInventoryPush)
                || _spawnOffered is null || _respawned is null)
                throw new ConfigException("Consumable revival requires inventory storage, inventory push and native respawn senders.");
        }
        var surface = context is null ? null : ResolveResult(context, options.GameProtocol, options);
        _result = surface?.Senders;
        _levels = surface?.Levels;
        _readWallet = surface?.ReadWallet;
        _progress = surface?.Progress;
        _events = context?.Events;
        ConfigureLottery(context);
        _supplyCreated = senders.SupplyCreated;
        _supplyPicked = senders.SupplyPicked;
        _lucciCreated = senders.LucciCreated;
        _lucciPicked = senders.LucciPicked;
        _starCreated = senders.StarCreated;
        _starPicked = senders.StarPicked;
        _objectAdded = senders.ObjectAdded;
        _itemUsed = senders.ItemUsed;
        _itemSlotChanged = senders.ItemSlotChanged;
        if (options.NativeItemSlotCount > 1 && _itemSlotChanged is null)
            throw new ConfigException("Multiple native item slots require a slot-switch sender.");
        _countedItemUsed = senders.CountedItemUsed;
        _pvePoints = senders.PvePoints;
        if (options.PvePointModeId > 0 && _pvePoints is null)
            throw new ConfigException("Cooperative points require a native point/rank sender.");
        _rescuePrepared = senders.RescuePrepared;
        _rescueStarted = senders.RescueStarted;
        _rescueFailed = senders.RescueFailed;
        _rescueSucceeded = senders.RescueSucceeded;
        if (options.RescueModeId > 0 && (_rescuePrepared is null || _rescueStarted is null || _rescueFailed is null || _rescueSucceeded is null))
            throw new ConfigException("Cooperative rescue missions require timing, start, failure and success senders.");
        if (options.CountedItemModeId > 0 && _countedItemUsed is null)
            throw new ConfigException("Counted cooperative items require a use sender.");
        if (options.ImmediateSupplyItemKinds.Length > 0 && _countedItemUsed is null)
            throw new ConfigException("Immediate supply effects require a native direct-use sender.");
        if (options.ZombieKingUltraItemKind > 0 && _countedItemUsed is null)
            throw new ConfigException("zombie_king_ultra_item_kind requires the build's direct item-effect sender (gspPveItemUsed).");
    }

    /// <summary>
    /// Offers a player the spawn point its client asked for. The round has no map knowledge yet, so
    /// the offer is the player's start position, which every build's start burst sets to the seat index.
    /// </summary>
    public async ValueTask HandleRespawnRequestedAsync(ISession session, RoundRespawnRequested request, CancellationToken ct)
    {
        if (_spawnOffered is null || _registry.TryGet(session.Id) is not { } room)
            return;
        await OfferSpawnAsync(session, room, request.Actor, ct, request.ActorSeat, indexRequested: true).ConfigureAwait(false);
    }

    /// <summary>Broadcasts the host's NPC wave transition on the same executor as the round.</summary>
    internal async ValueTask HandleWaveReportedAsync(ISession session, RoundWaveReported report,
        IMessageSender<RoundWaveChanged> sender, CancellationToken ct)
    {
        var (room, _) = _registry.RequireGame(session);
        await room.Executor.ExecuteAsync(async operationCt =>
        {
            RoomSeat[] members;
            lock (room.Gate)
            {
                var reporter = room.FindByGame(session.Id);
                if (room.Phase != RoomPhase.Playing || reporter?.Slot != room.MasterSlot)
                {
                    _logger.LogWarning("Session {Id} NPC wave {Wave} rejected in room {Room}: phase={Phase}, mode={Mode}.",
                        session.Id, report.Wave, room.Id, room.Phase, room.ModeId);
                    return;
                }
                members = [.. room.Seats];
            }
            foreach (var member in members)
                if (!member.Game.Connection.IsClosed)
                    await sender.SendAsync(member.Game, new(report.Wave, report.Started), operationCt).ConfigureAwait(false);
            _logger.LogInformation("Room {Room} NPC wave {Wave} started={Started} announced to {Count} members.",
                room.Id, report.Wave, report.Started, members.Length);
        }, ct).ConfigureAwait(false);
    }

    private Task OfferSpawnAsync(ISession session, PlayRoom room, int actor, CancellationToken ct, int? actorSeat = null, bool indexRequested = false) =>
        room.Executor.ExecuteAsync(async operationCt =>
        {
            RoomSeat? seat;
            lock (room.Gate)
            {
                seat = room.Phase == RoomPhase.Playing ? room.FindByGame(session.Id) : null;
            }
            if (seat is null || !OwnsActor(seat, actor, actorSeat))
                return;
            await _spawnOffered!.SendAsync(seat.Game, new RoundSpawnOffered(actor, seat.Slot) { IndexRequested = indexRequested }, operationCt).ConfigureAwait(false);
        }, ct);

    /// <summary>
    /// Takes note of a death. A client that times its own respawn needs nothing; one that waits is
    /// brought back at its start position when the time it reported is over.
    /// </summary>
    public ValueTask HandleDeathReportedAsync(ISession session, RoundDeathReported report, CancellationToken ct)
    {
        if (_registry.TryGet(session.Id) is not { } room)
        {
            _logger.LogDebug("Session {Id} died; its client respawns in {Ms} ms.", session.Id, report.RespawnDelayMs);
            return ValueTask.CompletedTask;
        }
        int generation;
        lock (room.Gate)
        {
            // One timer per dead player: the client repeats its report while it waits.
            if (room.Phase != RoomPhase.Playing || room.FindByGame(session.Id) is not { } seat)
                return ValueTask.CompletedTask;
            if (!OwnsActor(seat, report.Actor, report.ActorSeat))
                return ValueTask.CompletedTask;
            if (seat.InstantRevivalPending) return ValueTask.CompletedTask;
            seat.LastDeathActor = report.Actor;
            room.Supplies?.HeldItems.Remove(seat.Slot);
            seat.AwaitingRespawn = report.ServerRevives;
            if (seat.PveRevivalRequested && report.ServerRevives)
            {
                seat.PveRevivalRequested = false;
                return HandlePveRevivalAsync(session, new(), ct);
            }
            if (!report.ServerRevives || _respawned is null || _reviving.ContainsKey(session.Id))
                return ValueTask.CompletedTask;
            generation = room.StartGeneration;
        }
        var delay = Math.Clamp(report.RespawnDelayMs, 0, _options.RespawnDelayMaxMs);
        var wait = new DeathWait(session, report.Actor, Environment.TickCount64 + delay);
        if (!_reviving.TryAdd(session.Id, wait))
            return ValueTask.CompletedTask;
        _logger.LogInformation("Session {Id} died; the round brings it back in {Ms} ms.", session.Id, delay);
        _ = BackgroundWork.Detached(() => ReviveAfterAsync(session, room, generation, wait));
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Takes the configured amount off the wait of the dead player a taunt was aimed at, and tells
    /// that player so its own countdown agrees. A taunt at a player the round is not timing is ignored.
    /// </summary>
    public async ValueTask HandleTauntReportedAsync(ISession session, RoundTauntReported report, CancellationToken ct)
    {
        var reduction = _options.RespawnTauntReductionMs;
        if (reduction <= 0 || _registry.TryGet(session.Id) is not { } room)
            return;
        DeathWait? wait;
        lock (room.Gate)
        {
            // cn-324 sub_46B190 emits the in-round humiliation-end report. A bound relay
            // or a room that has left play cannot act as the reporting game seat.
            if (room.Phase != RoomPhase.Playing || room.FindByGame(session.Id) is null)
                return;
            wait = room.Seats.Select(seat => _reviving.GetValueOrDefault(seat.Game.Id))
                .FirstOrDefault(candidate => candidate is not null && candidate.Actor == report.VictimActor);
            if (wait is null)
                return;
            wait.Shorten(reduction);
        }
        _logger.LogInformation("Session {Id} was taunted; its wait is {Ms} ms shorter.", wait.Session.Id, reduction);
        if (_waitChanged is not null)
            await _waitChanged.SendAsync(wait.Session, new RoundRespawnWaitChanged(wait.Actor, -reduction), ct).ConfigureAwait(false);
    }

    // The dead players the round is about to bring back; bounded by the players seated in live rounds.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, DeathWait> _reviving = new();

    // One dead player's wait: when it ends, and a signal that the end moved.
    private sealed class DeathWait(ISession session, int actor, long deadline)
    {
        private long _deadline = deadline;
        private TaskCompletionSource _moved = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ISession Session => session;

        public int Actor => actor;

        public void Shorten(int milliseconds)
        {
            Interlocked.Add(ref _deadline, -milliseconds);
            Interlocked.Exchange(ref _moved, new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
        }

        public async Task WaitAsync(CancellationToken ct)
        {
            while (true)
            {
                var moved = Volatile.Read(ref _moved).Task;
                var remaining = Interlocked.Read(ref _deadline) - Environment.TickCount64;
                if (remaining <= 0)
                    return;
                await Task.WhenAny(Task.Delay(TimeSpan.FromMilliseconds(remaining), ct), moved).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
            }
        }
    }

    private async Task ReviveAfterAsync(ISession session, PlayRoom room, int generation, DeathWait wait)
    {
        var actor = wait.Actor;
        try
        {
            await wait.WaitAsync(_lifetime).ConfigureAwait(false);
            lock (room.Gate)
            {
                if (room.StartGeneration != generation || room.FindByGame(session.Id) is not { AwaitingRespawn: true })
                    return;
                if (!_reviving.TryGetValue(session.Id, out var current) || !ReferenceEquals(current, wait)) return;
            }
            // With an offer the client answers with the spawn point it takes, and that answer revives it.
            if (_spawnOffered is not null)
                await OfferSpawnAsync(session, room, actor, _lifetime).ConfigureAwait(false);
            else
                await ReviveAsync(session, room, actor, spawnIndex: null, _lifetime).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException)
        {
            // The room closed while the player was dead.
        }
        catch (Exception failure)
        {
            _logger.LogError(failure, "Room {Room}: bringing session {Id} back failed.", room.Id, session.Id);
        }
        finally
        {
            _reviving.TryRemove(new KeyValuePair<long, DeathWait>(session.Id, wait));
        }
    }

    /// <summary>Brings the player back at the spawn point its client took, and shows it to the whole round.</summary>
    public async ValueTask HandleSpawnAcceptedAsync(ISession session, RoundSpawnAccepted request, CancellationToken ct)
    {
        if (_respawned is null || _registry.TryGet(session.Id) is not { } room)
            return;
        await ReviveAsync(session, room, request.Actor, request.SpawnIndex, ct, request.ActorSeat).ConfigureAwait(false);
    }

    // Shows the whole round the player coming back; without a spawn point from the client it is
    // the player's start position, which every build's start burst sets to the seat index.
    private Task ReviveAsync(ISession session, PlayRoom room, int actor, int? spawnIndex, CancellationToken ct, int? actorSeat = null) =>
        room.Executor.ExecuteAsync(async operationCt =>
        {
            RoomSeat[] members;
            int slot;
            lock (room.Gate)
            {
                if (room.Phase != RoomPhase.Playing || room.FindByGame(session.Id) is not { } seat)
                    return;
                if (!OwnsActor(seat, actor, actorSeat))
                    return;
                seat.AwaitingRespawn = false;
                seat.InstantRevivalPending = false;
                seat.LastDeathActor = null;
                seat.PveRevivalRequested = false;
                slot = seat.Slot;
                members = [.. room.Seats];
            }
            var respawned = new RoundPlayerRespawned(room.Id, slot, actor, spawnIndex ?? slot);
            foreach (var member in members)
                await _respawned!.SendAsync(member.Game, respawned, operationCt).ConfigureAwait(false);
        }, ct);

    private bool OwnsActor(RoomSeat seat, int actor, int? actorSeat)
    {
        if (actorSeat is null || actorSeat == seat.Slot)
            return true;
        if (actorSeat < 0)
        {
            _logger.LogDebug("Session {Id} non-player actor {Actor} lifecycle report excluded from player revival.",
                seat.Game.Id, actor);
            return false;
        }
        _logger.LogWarning("Session {Id} respawn actor {Actor} maps to seat {ActorSeat}, not its own seat {Seat}; player revival rejected.",
            seat.Game.Id, actor, actorSeat, seat.Slot);
        return false;
    }

    /// <summary>Marks the player loaded, shows it to the room and starts the countdown when everyone has loaded.</summary>
    public async ValueTask HandleLoadReportedAsync(ISession session, RoundLoadReported report, CancellationToken ct)
    {
        if (_options.NativeMatchRelay) _registry.RequireGame(session);
        if (_registry.TryGet(session.Id) is not { } room)
        {
            _logger.LogInformation("Session {Id} reported a loaded map but is seated in no room.", session.Id);
            return;
        }
        await room.Executor.ExecuteAsync(async operationCt =>
        {
            RoomSeat[] members;
            RoomSeat loadedSeat;
            int slot;
            lock (room.Gate)
            {
                var seat = room.FindByGame(session.Id);
                if (seat is null || room.Phase != RoomPhase.Starting || seat.SceneReady)
                    return;
                seat.SceneReady = true;
                loadedSeat = seat;
                slot = seat.Slot;
                members = [.. room.Seats];
            }
            var loaded = new RoundPlayerLoaded(room.Id, slot);
            if (_playerLoaded is not null)
                foreach (var member in members)
                    await _playerLoaded.SendAsync(member.Game, loaded, operationCt).ConfigureAwait(false);
            if (_capabilityOffers is not null && !UsesGameLink(room, loadedSeat))
                await OfferNativeMatchAsync(room, loadedSeat, operationCt).ConfigureAwait(false);
            await StartCountdownWhenAllLoadedAsync(room, operationCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Re-checks the loading barrier after a player left a starting room: the players who stay must
    /// not wait for a report that can no longer come. Runs on the room's executor.
    /// </summary>
    public async ValueTask OnSeatLeftAsync(PlayRoom room, CancellationToken ct)
    {
        await StartCountdownWhenAllLoadedAsync(room, ct).ConfigureAwait(false);
        await OnTeamConvertedAsync(room, ct).ConfigureAwait(false);
    }

    /// <summary>Gets or sets what runs when a room's round starts playing, with the round's start generation.</summary>
    internal Action<PlayRoom, int>? RoundPlaying { get; set; }

    // Called on the room executor after the native team notification reaches every member.
    internal ValueTask OnTeamConvertedAsync(PlayRoom room, CancellationToken ct)
    {
        if (_options.ZombiePvpModeIds.Contains(room.ModeId))
            return ZombiePvpOutcomeAsync(room, ct);
        lock (room.Gate)
        {
            if (_options.ZombieConversionModeId <= 0 || room.ModeId != _options.ZombieConversionModeId
                || room.Phase != RoomPhase.Playing || room.Seats.Count == 0
                || room.Seats.Any(seat => seat.Team != 15))
                return ValueTask.CompletedTask;
        }
        return EndRoundAsync(room, RoundEndReason.CooperativeFailed, ct);
    }

    /// <summary>
    /// Ends a running round when a player's client reports the stage over: every member is shown
    /// the result, and after the result time the room returns to waiting with everyone still seated.
    /// </summary>
    public async ValueTask HandleEndRequestedAsync(ISession session, RoundEndRequested request, CancellationToken ct)
    {
        if (_registry.TryGet(session.Id) is not { } room)
            return;
        await room.Executor.ExecuteAsync(operationCt =>
        {
            var reason = request.Reason;
            lock (room.Gate)
            {
                if (room.Tutorial is not null)
                {
                    _logger.LogDebug("Room {Room} tutorial awaits its native completion packet; ordinary stage report ignored.", room.Id);
                    return ValueTask.CompletedTask;
                }
                // The server decides the zombie battle; the last human's client still reports its fall
                // (kr-812 sub_7694F0 sends gcpReqEndStageRoundOverReady).
                if (_options.ZombiePvpModeIds.Contains(room.ModeId))
                    return ValueTask.CompletedTask;
                // A native-life round is timed: a client sends its stage report on every death, so the clock ends it.
                if (_options.NativeLifeModeIds.Contains(room.ModeId))
                    return ValueTask.CompletedTask;
                if (room.FindByGame(session.Id) is not { } reporter
                    || request.Reason == RoundEndReason.WavesCleared && reporter.Slot != room.MasterSlot)
                    return ValueTask.CompletedTask;
                if (request.StageSucceeded is { } succeeded && _options.CooperativeStageResultModes.Contains(room.ModeId))
                {
                    if (reporter.Slot != room.MasterSlot)
                        return ValueTask.CompletedTask;
                    reason = succeeded ? RoundEndReason.WavesCleared : RoundEndReason.CooperativeFailed;
                }
                else if (request.Reason == RoundEndReason.WavesCleared && _options.CooperativeStageResultModes.Contains(room.ModeId))
                {
                    // gcpReqEndAllWave is only the first half of the client's clear: every audited sender is
                    // followed at once by gcpReqEndStage(1) (kr-812 sub_A09190, id-400 sub_986A00, cn-324 sub_4138E0),
                    // while a failure sends gcpReqGameOver and gcpReqEndStage(0) (kr-812 sub_A09220). The reward
                    // dialog opens only for a clear, so the stage word alone ends these modes.
                    _logger.LogDebug("Room {Room}: gcpReqEndAllWave in a cooperative stage mode waits for the stage word.", room.Id);
                    return ValueTask.CompletedTask;
                }
            }
            return EndRoundAsync(room, reason, operationCt);
        }, ct).ConfigureAwait(false);
    }

    // The zombie battle ends when no human is left (the zombies win) or when the king is gone: it fell (decided
    // by the death report) or left the room (the humans win). Zombinis come back without limit, so losing one
    // decides nothing. A room of one keeps playing after its only player turned, so a lone player can try the
    // mode until the time limit, which the humans win.
    private ValueTask ZombiePvpOutcomeAsync(PlayRoom room, CancellationToken ct)
    {
        lock (room.Gate)
        {
            if (room.Phase != RoomPhase.Playing)
                return ValueTask.CompletedTask;
            if (room.DecidedWinnerTeam is null && room.ZombieKingSlot is { } king)
            {
                if (!room.Seats.Any(seat => seat.Slot == king && seat.Team == RoomFlow.ZombiePvpZombieTeam))
                    room.DecidedWinnerTeam = RoomFlow.ZombiePvpHumanTeam;
                else if (room.Seats.Count >= 2 && !room.Seats.Any(seat => seat.Team == RoomFlow.ZombiePvpHumanTeam))
                    room.DecidedWinnerTeam = RoomFlow.ZombiePvpZombieTeam;
            }
            if (room.DecidedWinnerTeam is null)
                return ValueTask.CompletedTask;
        }
        return EndRoundAsync(room, RoundEndReason.WinCondition, ct);
    }

    // Runs on the room's executor. The first caller ends the round; later ones find it already ended.
    private async ValueTask EndRoundAsync(PlayRoom room, RoundEndReason reason, CancellationToken ct)
    {
        if (_ended is null)
            return;
        RoomSeat[] members;
        int generation;
        lock (room.Gate)
        {
            if (room.Phase != RoomPhase.Playing)
                return;
            // The humans of a zombie battle win when its time runs out.
            if (reason == RoundEndReason.TimeLimit && _options.ZombiePvpModeIds.Contains(room.ModeId))
                room.DecidedWinnerTeam ??= RoomFlow.ZombiePvpHumanTeam;
            if (IsStarMode(room))
                DecideStarRound(room);
            room.MarkResult();
            generation = room.StartGeneration;
            members = [.. room.Seats];
        }
        // A build with a result surface runs the whole staged sequence; a build without one keeps
        // the single end message and the plain return.
        if (_result is not null)
        {
            _logger.LogInformation("Room {Room}: round ended ({Reason}); running the result sequence.", room.Id, reason);
            CancellationTokenSource cancellation;
            lock (room.Gate)
            {
                if (!room.ResultTask.IsCompleted)
                    throw new InvalidOperationException($"Room {room.Id} still owns a previous result sequence.");
                cancellation = room.BeginResultSequence(_lifetime);
                room.ResultTask = BackgroundWork.Detached(() => RunTypedResultDetachedAsync(room, reason, generation, cancellation));
            }
            return;
        }
        var outcome = OutcomeOf(room, generation, reason);
        foreach (var member in members)
            await _ended.SendAsync(member.Game, new RoundEnded(room.Id, outcome(member), reason) { ModeId = room.ModeId, RecipientTeam = member.Team }, ct).ConfigureAwait(false);
        _logger.LogInformation("Room {Room}: round ended; back to the room in {Ms} ms.", room.Id, _options.ResultCloseGraceMs);
        room.CountdownTask = BackgroundWork.Detached(() => ReturnAfterResultAsync(room, generation));
    }

    // The client's own match timer ends nothing: when the time limit runs out the server ends the round.
    private async Task EndAtTimeLimitAsync(PlayRoom room, int generation, int timeLimitMs)
    {
        try
        {
            await Task.Delay(timeLimitMs, _lifetime).ConfigureAwait(false);
            await room.Executor.ExecuteAsync(operationCt =>
            {
                lock (room.Gate)
                {
                    if (room.StartGeneration != generation)
                        return ValueTask.CompletedTask;
                }
                return EndRoundAsync(room, RoundEndReason.TimeLimit, operationCt);
            }, _lifetime).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException)
        {
            // The room closed during the round.
        }
        catch (Exception failure)
        {
            _logger.LogError(failure, "Room {Room}: ending the round at its time limit failed.", room.Id);
        }
    }

    private async Task ReturnAfterResultAsync(PlayRoom room, int generation)
    {
        try
        {
            await Task.Delay(_options.ResultCloseGraceMs, _lifetime).ConfigureAwait(false);
            await room.Executor.ExecuteAsync(async operationCt =>
            {
                lock (room.Gate)
                {
                    if (room.Phase != RoomPhase.Result || room.StartGeneration != generation)
                        return;
                }
                room.ResetToWaiting();
                await _returnToRoom(room, operationCt).ConfigureAwait(false);
            }, _lifetime).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException)
        {
            // The room closed during the result.
        }
        catch (Exception failure)
        {
            _logger.LogError(failure, "Room {Room}: the return to the room after the result failed.", room.Id);
        }
    }

    /// <summary>Passes one gameplay feed packet to the other players of the sender's round.</summary>
    public async ValueTask HandleFeedAsync(ISession session, RoundFeedSent packet, CancellationToken ct)
    {
        if (_feed is null || _registry.TryGet(session.Id) is not { } room)
            return;
        ISession[] targets;
        lock (room.Gate)
        {
            if (room.Phase is not (RoomPhase.Starting or RoomPhase.Playing))
                return;
            var sender = room.Seats.FirstOrDefault(seat => seat.Game.Id == session.Id || seat.Relay?.Id == session.Id);
            if (sender is null)
                return;
            if (packet.ReportedActors is { Count: > 0 } actors && !actors.Any(actor => room.ControlsActor(sender, actor)))
            {
                if (room.Seats.Any(seat => actors.Contains(RoomRegistry.SceneEntity(seat.Slot))))
                    throw new InvalidOperationException($"Feed packet {packet.Code} from slot {sender.Slot} acts for another seat's character.");
                // In the horde modes the host's client runs the NPCs and reports their actions as its own; anywhere
                // else a character that belongs to no seat proves nothing.
                if (GameMode.Resolve(room.ModeId).Family != GameModeFamily.Horde
                    && room.Tutorial is not { GameConnectionOnly: true })
                {
                    _logger.LogWarning("Room {Room} slot {Slot} feed packet {Code} names no character of this room; dropped.",
                        room.Id, sender.Slot, packet.Code);
                    return;
                }
            }
            // A relay packet goes to each seat's match link of this round: its relay connection, or its game
            // connection after its relay failed. A seat with neither gets nothing rather than a stale copy.
            targets = packet.OnRelay
                ? [.. room.Seats.Where(seat => packet.IncludeSender || seat != sender)
                    .Select(seat => MatchLink(room, seat)).OfType<ISession>()]
                : [.. room.Seats.Where(seat => packet.IncludeSender || seat != sender).Select(seat => seat.Game)];
        }
        var received = new RoundFeedReceived(packet.Code, packet.Payload);
        foreach (var target in targets)
            await _feed.SendAsync(target, received, ct).ConfigureAwait(false);
    }

    // Runs on the room's executor.
    private async ValueTask StartCountdownWhenAllLoadedAsync(PlayRoom room, CancellationToken ct)
    {
        RoomSeat[] members;
        int generation;
        lock (room.Gate)
        {
            if (room.Phase != RoomPhase.Starting || room.CountdownGeneration == room.StartGeneration
                || room.Seats.Count == 0 || room.Seats.Any(seat => !seat.SceneReady
                    || (_options.NativeMatchRelay && !NativeMatchReady(room, seat))))
                return;
            generation = room.CountdownGeneration = room.StartGeneration;
            members = [.. room.Seats];
        }

        var countdownMs = room.Tutorial?.CountdownMs ?? _options.StartTimerMs;
        var countdown = new RoundCountdownStarted(room.Id, countdownMs) { IsTutorial = room.Tutorial is not null };
        foreach (var member in members)
        {
            bool overGame;
            lock (room.Gate) overGame = UsesGameLink(room, member);
            await _countdown.SendAsync(member.Game, countdown with { MatchOverGame = overGame }, ct).ConfigureAwait(false);
        }
        _logger.LogInformation("Room {Room}: all {Count} player(s) loaded; the round starts in {Ms} ms.",
            room.Id, members.Length, countdownMs);
        room.CountdownTask = BackgroundWork.Detached(() => FinishCountdownAsync(room, generation));
    }

    // The rule record of a typed start sets the length: the time-attack modes (the zombie and pirate families) play
    // TimeAttackTime, the others DeathMatchTime (kr-1256 sub_AADD50 fills record +28 and +32 from those keys).
    // Null without a rule record or when the record's value is not a length.
    private static int? RuleRecordLength(PlayRoom room)
    {
        if (room.RoundTimeLimits is not { } limits)
            return null;
        var family = GameMode.Resolve(room.ModeId).Family;
        var length = family is GameModeFamily.Zombie or GameModeFamily.Horde ? limits.TimeAttackMs : limits.DeathMatchMs;
        return length > 0 ? length : null;
    }

    private async Task FinishCountdownAsync(PlayRoom room, int generation)
    {
        using var countdownLifetime = CancellationTokenSource.CreateLinkedTokenSource(_lifetime,
            room.TutorialCancellation?.Token ?? CancellationToken.None);
        var token = countdownLifetime.Token;
        try
        {
            await Task.Delay(room.Tutorial?.CountdownMs ?? _options.StartTimerMs, token).ConfigureAwait(false);
            await room.Executor.ExecuteAsync(async operationCt =>
            {
                RoomSeat[] members;
                lock (room.Gate)
                {
                    // A room that emptied or restarted meanwhile no longer owns this countdown.
                    if (room.Phase != RoomPhase.Starting || room.StartGeneration != generation)
                        return;
                    if (_options.NativeMatchRelay && room.Seats.Any(seat => !NativeMatchReady(room, seat)))
                    {
                        room.CountdownGeneration = 0;
                        throw new InvalidOperationException("Native match transport was lost during the round countdown.");
                    }
                    room.MarkPlaying();
                    members = [.. room.Seats];
                }
                var timeLimit = room.Tutorial?.TimeLimitMs ?? (_options.ZombieConversionModeId > 0 && room.ModeId == _options.ZombieConversionModeId
                    ? _options.ZombieRoundTimeLimitMs
                    : _options.ZombiePvpModeIds.Contains(room.ModeId) ? _options.ZombiePvpRoundTimeLimitMs
                    : IsStarMode(room) ? _options.StarRoundTimeLimitMs
                    : RuleRecordLength(room) ?? _options.RoundUnknown0);
                var unlimited = room.Tutorial is null && room.RoundTimeLimits is not null && room.ModeId == RoomRegistry.TrainingModeId;
                if (unlimited) timeLimit = 0;
                var started = new RoundStarted(room.Id, timeLimit) { IsTutorial = room.Tutorial is not null, Unlimited = unlimited };
                foreach (var member in members)
                    await _started.SendAsync(member.Game, started, operationCt).ConfigureAwait(false);
                if (room.Tutorial is null)
                    await InitializePvePointsAsync(room, members, operationCt).ConfigureAwait(false);
                _logger.LogInformation("Room {Room}: round started for {Count} player(s).", room.Id, members.Length);
                if (room.Tutorial?.NativeSupplies != true) StartSupplies(room);
                if (room.Tutorial is null) StartRescues(room);
                if (room.Tutorial is null && _ended is not null && timeLimit > 0)
                    room.CountdownTask = BackgroundWork.Detached(() => EndAtTimeLimitAsync(room, generation, timeLimit));
                if (room.Tutorial is null) RoundPlaying?.Invoke(room, generation);
                if (room.Tutorial is null) await PushStarCountsAsync(room, operationCt).ConfigureAwait(false);
            }, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException)
        {
            // The room closed during the countdown.
        }
        catch (Exception failure)
        {
            _logger.LogError(failure, "Room {Room}: the round start after the countdown failed.", room.Id);
        }
    }
}

/// <summary>The encoders a build's dialect publishes for the round flow.</summary>
/// <param name="PlayerLoaded">Tells a member that one player finished loading; null on a build whose client has no such packet.</param>
/// <param name="Countdown">Starts the countdown on a member's client.</param>
/// <param name="Started">Starts the round on a member's client.</param>
/// <param name="Feed">Hands a member another player's feed packet; null on a build whose feed the server does not carry.</param>
/// <param name="Ended">Shows a member the round's result; null on a build whose round end is not bound.</param>
/// <param name="SpawnOffered">Offers a dead member a spawn point; null on a build whose respawn requests are not bound.</param>
/// <param name="Respawned">Shows the round a member's revive; null on a build whose respawn requests are not bound.</param>
/// <param name="WaitChanged">Tells a dead member its wait changed; null on a build that has no such packet.</param>
/// <param name="SupplyCreated">Creates a map supply box; null when supply packets are not bound.</param>
/// <param name="SupplyPicked">Shows an accepted map supply pickup; null when supply packets are not bound.</param>
/// <param name="ItemUsed">Runs a confirmed held-item use; null when the native item-use path is not bound.</param>
/// <param name="RescuePrepared">Refreshes mission timing before the next native alarm.</param>
/// <param name="RescueStarted">Activates a native cooperative rescue.</param>
/// <param name="RescueFailed">Fails a native cooperative rescue.</param>
/// <param name="RescueSucceeded">Completes a native cooperative rescue.</param>
/// <param name="PvePoints">Publishes cooperative score deltas and ranks when this capability is bound.</param>
/// <param name="ItemSlotChanged">Acknowledges native held-item slot switches when this capability is bound.</param>
/// <param name="CountedItemUsed">Runs a confirmed counted cooperative item use; null when this capability is absent.</param>
/// <param name="LucciCreated">Creates a dropped Lucci or horde-award item; null when the Lucci packets are not bound.</param>
/// <param name="LucciPicked">Removes a picked-up Lucci item; null when the Lucci packets are not bound.</param>
/// <param name="StarCreated">Creates a star of the star modes; null when the star packets are not bound.</param>
/// <param name="StarPicked">Removes a claimed star; null when the star packets are not bound.</param>
/// <param name="ObjectAdded">Acknowledges a player's own character add request; null on a build whose server does not answer it.</param>
internal sealed record RoundSenders(
    IMessageSender<RoundPlayerLoaded>? PlayerLoaded,
    IMessageSender<RoundCountdownStarted> Countdown,
    IMessageSender<RoundStarted> Started,
    IMessageSender<RoundFeedReceived>? Feed = null,
    IMessageSender<RoundEnded>? Ended = null,
    IMessageSender<RoundSpawnOffered>? SpawnOffered = null,
    IMessageSender<RoundPlayerRespawned>? Respawned = null,
    IMessageSender<RoundRespawnWaitChanged>? WaitChanged = null,
    IMessageSender<RoundSupplyCreated>? SupplyCreated = null,
    IMessageSender<RoundSupplyPicked>? SupplyPicked = null,
    IMessageSender<RoundItemUsed>? ItemUsed = null,
    IMessageSender<RoundCountedItemUsed>? CountedItemUsed = null,
    IMessageSender<RoundRescueMissionStarted>? RescueStarted = null,
    IMessageSender<RoundRescueMissionFailed>? RescueFailed = null,
    IMessageSender<RoundRescueMissionSucceeded>? RescueSucceeded = null,
    IMessageSender<RoundRescueMissionsPrepared>? RescuePrepared = null,
    IMessageSender<RoundPvePointsChanged>? PvePoints = null,
    IMessageSender<RoundItemSlotChanged>? ItemSlotChanged = null,
    IMessageSender<RoundLucciCreated>? LucciCreated = null,
    IMessageSender<RoundLucciPicked>? LucciPicked = null,
    IMessageSender<RoundStarCreated>? StarCreated = null,
    IMessageSender<RoundStarPicked>? StarPicked = null,
    IMessageSender<RoundObjectAddAcknowledged>? ObjectAdded = null)
{
    /// <summary>
    /// Resolves the encoders published for <paramref name="protocol"/>; a missing required one is a configuration error,
    /// and so is a build that answers a loading player neither with a per-player load notice nor with an add acknowledgement.
    /// </summary>
    public static RoundSenders Resolve(IPluginContext context, string protocol)
    {
        var senders = Create(context, protocol);
        if (senders.PlayerLoaded is null && senders.ObjectAdded is null)
            throw new ConfigException($"InGame needs an encoder for {nameof(RoundPlayerLoaded)} or {nameof(RoundObjectAddAcknowledged)} on '{protocol}'; the build's dialect row publishes neither.");
        return senders;
    }

    private static RoundSenders Create(IPluginContext context, string protocol) => new(
        PlaySenders.Optional<RoundPlayerLoaded>(context, protocol),
        PlaySenders.Required<RoundCountdownStarted>(context, protocol),
        PlaySenders.Required<RoundStarted>(context, protocol),
        PlaySenders.Optional<RoundFeedReceived>(context, protocol),
        PlaySenders.Optional<RoundEnded>(context, protocol),
        PlaySenders.Optional<RoundSpawnOffered>(context, protocol),
        PlaySenders.Optional<RoundPlayerRespawned>(context, protocol),
        PlaySenders.Optional<RoundRespawnWaitChanged>(context, protocol),
        PlaySenders.Optional<RoundSupplyCreated>(context, protocol),
        PlaySenders.Optional<RoundSupplyPicked>(context, protocol),
        PlaySenders.Optional<RoundItemUsed>(context, protocol),
        PlaySenders.Optional<RoundCountedItemUsed>(context, protocol),
        PlaySenders.Optional<RoundRescueMissionStarted>(context, protocol),
        PlaySenders.Optional<RoundRescueMissionFailed>(context, protocol),
        PlaySenders.Optional<RoundRescueMissionSucceeded>(context, protocol),
        PlaySenders.Optional<RoundRescueMissionsPrepared>(context, protocol),
        PlaySenders.Optional<RoundPvePointsChanged>(context, protocol),
        PlaySenders.Optional<RoundItemSlotChanged>(context, protocol),
        PlaySenders.Optional<RoundLucciCreated>(context, protocol),
        PlaySenders.Optional<RoundLucciPicked>(context, protocol),
        PlaySenders.Optional<RoundStarCreated>(context, protocol),
        PlaySenders.Optional<RoundStarPicked>(context, protocol),
        PlaySenders.Optional<RoundObjectAddAcknowledged>(context, protocol));
}
