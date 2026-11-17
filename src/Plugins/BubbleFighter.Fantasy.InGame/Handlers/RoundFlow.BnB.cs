using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.InGame.Data;
using BubbleFighter.Fantasy.InGame.Sessions;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.InGame.Handlers;

internal sealed record BnBSenders(IMessageSender<RoundBnBPlacementPublished> Placement,
    IMessageSender<RoundBnBExplosionPublished> Explosion, IMessageSender<RoundBnBPickupPublished> Pickup);

internal sealed partial class RoundFlow
{
    private IReadOnlyDictionary<string, BnBMapData>? _bnbMaps;
    private BnBRoundRules? _bnbRules;
    private BnBSenders? _bnbGame;
    private BnBSenders? _bnbRelay;
    internal Func<long> BnBMilliseconds { get; set; } = static () => Environment.TickCount64;

    /// <summary>Whether a decided BnB elimination ends the round; tests of a dead actor's later reports turn it off.</summary>
    internal bool BnBEndsOnElimination { get; set; } = true;

    internal void ConfigureBnB(IClientDataStore store, BnBSenders game, BnBSenders relay)
    {
        if (!_options.BnB) throw new ConfigException("BnB bindings require bnb on the in-game row.");
        if (_options.ValidateBnB().FirstOrDefault() is { } invalid) throw new ConfigException(invalid.ErrorMessage!);
        _bnbRules = BnBRoundRules.Load(store, _options.BnBMaximumFileBytes);
        _bnbMaps = _options.BnBStages.ToDictionary(stage => stage.StageDirectory,
            stage => BnBMapData.Load(store, stage.StageDirectory, _options.BnBMaximumFileBytes, _options.BnBMaximumCells,
                new(stage.TileLayerMinimumX!.Value, stage.TileLayerMaximumZ!.Value)), StringComparer.OrdinalIgnoreCase);
        _bnbGame = game;
        _bnbRelay = relay;
        _registry.ParticipantConnectionClosed = OnBnBParticipantConnectionClosed;
    }

    private bool IsBnBMode(PlayRoom room) => _options.BnB && room.Tutorial is null && room.ModeId == (int)GameModeId.BnB;

    private sealed record BnBParticipantStamp(BnBRuntimeState State, RoomSeat Seat, ISession Match, long Life);

    private BnBParticipantStamp? CaptureBnBParticipant(PlayRoom room, ISession session, bool gameOnly)
    {
        lock (room.Gate)
        {
            if (room.BnBRound is not { } state || !BnBIsCurrent(room, state, requirePlaying: true)) return null;
            var seat = state.Members.FirstOrDefault(member => ReferenceEquals(member.Game, session)
                || !gameOnly && ReferenceEquals(MatchLink(room, member), session));
            return seat is not null && MatchLink(room, seat) is { } match
                ? new(state, seat, match, seat.Life.Generation) : null;
        }
    }

    private bool BnBParticipantIsCurrent(PlayRoom room, BnBParticipantStamp stamp) =>
        BnBIsCurrent(room, stamp.State, requirePlaying: true)
        && ReferenceEquals(MatchLink(room, stamp.Seat), stamp.Match) && stamp.Seat.Life.Generation == stamp.Life;

    private async ValueTask RemoveBnBActorAsync(ISession session, PlayRoom room, RoundSpawnAccepted request,
        int? spawnIndex, CancellationToken ct)
    {
        BnBParticipantStamp stamp;
        lock (room.Gate)
        {
            if (!UsesNativeLife(room) || CaptureBnBParticipant(room, session, gameOnly: true) is not { } current
                || request.Actor != RoomRegistry.SceneEntity(current.Seat.Slot)
                || !current.Seat.Life.CanGrantReset) return;
            stamp = current;
        }
        try
        {
            await room.Executor.ExecuteAsync(async operationCt =>
            {
                lock (room.Gate)
                {
                    if (!BnBParticipantIsCurrent(room, stamp) || !stamp.Seat.Life.TryRemoveActor()) return;
                }
                // kr-1256 sub_6F5E50 unregisters a BnB actor and skips the ordinary match reset confirmation.
                var removal = new RoundPlayerRespawned(room.Id, stamp.Seat.Slot, request.Actor, spawnIndex ?? stamp.Seat.Slot)
                    { Request = request.Request };
                await _respawned!.SendAsync(session, removal, operationCt).ConfigureAwait(false);
                operationCt.ThrowIfCancellationRequested();
                lock (room.Gate)
                    if (!BnBParticipantIsCurrent(room, stamp))
                        throw new InvalidOperationException("BnB actor changed during terminal removal publication.");
            }, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            await CancelBnBAsync(room, stamp.State).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Whether this server holds the per-map data the room's mode needs on <paramref name="stage"/>.</summary>
    internal bool HostsModeMap(PlayRoom room, string stage) =>
        (!IsBnBMode(room) || _bnbMaps?.ContainsKey(stage) == true)
        && (!IsExtremeRacingMode(room) || !_options.ExtremeRacingInitialization || _racingMaps?.ContainsKey(stage) == true);

    internal void ValidateModeMap(PlayRoom room, string stage)
    {
        if (!room.BnBCleanupTask.IsCompleted)
            throw new InvalidOperationException("A match cannot start while its previous BnB connection cleanup is pending.");
        if (!room.FinalZoneCleanupTask.IsCompleted)
            throw new InvalidOperationException("A match cannot start while its previous FinalZone connection cleanup is pending.");
        if (IsBnBMode(room) && (room.Bots.Count != 0 || _bnbMaps?.ContainsKey(stage) != true))
            throw new InvalidOperationException($"BnB requires native players and a configured stage: '{stage}'.");
        ValidateExtremeRacingMap(room, stage);
    }

    // RoomFlow calls this under its gate before any recipient starts loading the selected map.
    internal int? PrepareBnBRound(PlayRoom room)
    {
        if (!IsBnBMode(room)) return null;
        var stage = room.RoundStageKey.Length > 0 ? room.RoundStageKey : room.RoundMapKey;
        ValidateModeMap(room, stage);
        var seed = Random.Shared.Next();
        var members = room.Seats.ToArray();
        var ledger = new BnBRoundState(_bnbMaps![stage], _bnbRules!, seed,
            members.Select(member => RoomRegistry.SceneEntity(member.Slot)).ToArray(), _options.MaxPlayersPerMatch);
        room.BnBRound = new(room.StartGeneration, ledger, members);
        return seed;
    }

    // BnB has no respawn: the round ends when at most one side still has a member alive or bubbled (a bubbled
    // member can still be released), or when the other sides left. Seats on different teams are sides; when every seat shares one team each
    // seat is its own side. The surviving side wins; nobody surviving is a draw.
    private ValueTask BnBOutcomeAsync(PlayRoom room, CancellationToken ct)
    {
        lock (room.Gate)
        {
            if (!BnBEndsOnElimination || !IsBnBMode(room) || room.Phase != RoomPhase.Playing || room.RoundCancelled || room.IsStopping
                || room.DecidedWinnerTeam is not null || room.DecidedWinnerSlots is not null || room.Seats.Count == 0)
                return ValueTask.CompletedTask;
            static bool Standing(RoomSeat seat) => seat.Life.State is SeatLife.Alive or SeatLife.Bubble;
            var teams = room.Seats.Select(seat => seat.Team).Distinct().Count() > 1;
            var sides = room.Seats.GroupBy(seat => teams ? seat.Team : seat.Slot).ToArray();
            var standing = sides.Where(side => side.Any(Standing)).ToArray();
            // A round started alone never decides itself; one that started contested is won by whoever stays.
            var contested = sides.Length > 1 || room.BnBRound?.Actors.Length > 1;
            if (standing.Length > 1 || standing.Length == 1 && !contested)
                return ValueTask.CompletedTask;
            if (teams && standing.Length == 1)
                room.DecidedWinnerTeam = standing[0].Key;
            else
                room.DecidedWinnerSlots = standing.Length == 1 ? new HashSet<int> { standing[0].Key } : new HashSet<int>();
            _logger.LogInformation("Room {Room}: BnB round decided; {Outcome}.", room.Id,
                standing.Length == 1 ? (teams ? $"team {standing[0].Key}" : $"slot {standing[0].Key}") + " is the last standing" : "nobody is left standing");
        }
        return EndRoundAsync(room, RoundEndReason.WinCondition, ct);
    }

    internal ValueTask HandleBnBPlacementAsync(ISession session, RoundBnBPlacementReported report, CancellationToken ct) =>
        ExecuteBnBAsync(session, report.Actor, static life => life.State == SeatLife.Alive, async (room, state, seat, operationCt) =>
        {
            BnBRoundState candidate;
            lock (room.Gate)
            {
                if (!BnBPositionMatches(state.Ledger.Map, seat, report.GridIndex)) return;
                candidate = state.Ledger.Clone();
                if (!candidate.TryPlace(report.Actor, report.GridIndex, BnBMilliseconds(), out _)) return;
            }
            await PublishBnBAsync(room, state, candidate, senders => senders.Placement,
                new RoundBnBPlacementPublished(report.Actor, report.GridIndex), operationCt).ConfigureAwait(false);
        }, ct);

    internal ValueTask HandleBnBExplosionAsync(ISession session, RoundBnBExplosionReported report, CancellationToken ct) =>
        ExecuteBnBAsync(session, report.Actor, static _ => true, async (room, state, _, operationCt) =>
        {
            if (report.Bombs.Count == 0 || report.Bombs.Count > state.Ledger.CellCount) return;
            var bombs = report.Bombs.Select(bomb => new BnBBombIdentity(bomb.Generation, bomb.GridIndex)).ToArray();
            await ApplyBnBExplosionAsync(room, state, report.Actor, bombs, BnBMilliseconds(), operationCt).ConfigureAwait(false);
        }, ct);

    // kr-1256 sub_8E1E10 sends bubble before pickup in one update; kr-1256 sub_6FC240 also accepts a bubbled actor.
    internal ValueTask HandleBnBPickupAsync(ISession session, RoundBnBPickupReported report, CancellationToken ct) =>
        ExecuteBnBAsync(session, report.Actor, static life => !life.ActorRemovalSent && life.State is SeatLife.Alive or SeatLife.Bubble,
            async (room, state, seat, operationCt) =>
        {
            BnBRoundState candidate;
            lock (room.Gate)
            {
                if (!state.Ledger.Map.Contains(report.GridIndex))
                    throw new InvalidOperationException("BnB pickup names an out-of-map cell.");
                candidate = state.Ledger.Clone();
                candidate.AdvanceTimers(BnBMilliseconds());
                if (candidate.HasAvailableItem(report.GridIndex)
                    && !BnBPositionMatches(candidate.Map, seat, report.GridIndex))
                    throw new InvalidOperationException("BnB pickup is outside the player's admitted position.");
                _ = candidate.TryPickup(report.Actor, report.GridIndex, BnBMilliseconds(), out _);
            }
            // kr-1256 sub_6FC240 clears the actor's pending latch even after another player has taken this item.
            await PublishBnBAsync(room, state, candidate, senders => senders.Pickup,
                new RoundBnBPickupPublished(report.Actor, report.GridIndex), operationCt).ConfigureAwait(false);
        }, ct);

    private bool BnBPositionMatches(BnBMapData map, RoomSeat seat, int requested)
    {
        if (!map.Contains(requested)) return false;
        // The kr-1256 client reports no movement on the relay during mode 41 (capture 2026-11-14: only pings and
        // placements), so a seat without a report is bounded by the map alone rather than refused.
        if (seat.Movement is not { } movement) return true;
        var current = map.PositionToGrid(movement.X, movement.Y, movement.Z);
        return current >= 0 && Math.Abs(current % map.Width - requested % map.Width)
            + Math.Abs(current / map.Width - requested / map.Width) <= _options.BnBMaximumPositionCellDistance;
    }

    private async ValueTask ExecuteBnBAsync(ISession session, int actor, Func<ParticipantLife, bool> acceptsLife,
        Func<PlayRoom, BnBRuntimeState, RoomSeat, CancellationToken, ValueTask> operation, CancellationToken ct)
    {
        if (_registry.TryGet(session.Id) is not { } room || !IsBnBMode(room)) return;
        BnBRuntimeState state;
        RoomSeat seat;
        long life;
        lock (room.Gate)
        {
            if (room.BnBRound is not { } current || !BnBIsCurrent(room, current, requirePlaying: true)
                || current.Members.FirstOrDefault(member => ReferenceEquals(MatchLink(room, member), session)) is not { } reporter
                || actor != RoomRegistry.SceneEntity(reporter.Slot)
                || !acceptsLife(reporter.Life)) return;
            state = current;
            seat = reporter;
            life = reporter.Life.Generation;
        }
        try
        {
            await room.Executor.ExecuteAsync(async operationCt =>
            {
                lock (room.Gate)
                    if (!BnBIsCurrent(room, state, requirePlaying: true) || !ReferenceEquals(MatchLink(room, seat), session)
                        || seat.Life.Generation != life || !acceptsLife(seat.Life)) return;
                await operation(room, state, seat, operationCt).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            await CancelBnBAsync(room, state).ConfigureAwait(false);
            throw;
        }
    }

    private bool BnBIsCurrent(PlayRoom room, BnBRuntimeState state, bool requirePlaying) =>
        ReferenceEquals(room.BnBRound, state) && state.Generation == room.StartGeneration
        && IsBnBMode(room) && !room.RoundCancelled && !room.IsStopping
        && (requirePlaying ? room.Phase == RoomPhase.Playing : room.Phase is RoomPhase.Starting or RoomPhase.Playing)
        && room.Seats.Count == state.Members.Length && state.Members.All(member =>
            ReferenceEquals(room.FindByGame(member.Game.Id), member) && !member.Game.Connection.IsClosed
            && (!requirePlaying || member.SceneReady && (!_options.NativeMatchRelay || NativeMatchReady(room, member))));

    private async ValueTask PublishBnBAsync<T>(PlayRoom room, BnBRuntimeState state, BnBRoundState candidate,
        Func<BnBSenders, IMessageSender<T>> select, T message, CancellationToken ct) where T : IMessage
    {
        var original = state.Ledger;
        try
        {
            foreach (var member in state.Members)
            {
                ISession target;
                lock (room.Gate)
                {
                    if (!BnBIsCurrent(room, state, requirePlaying: true) || !ReferenceEquals(state.Ledger, original))
                        throw new InvalidOperationException("BnB round changed during ordered publication.");
                    target = MatchLink(room, member) ?? throw new InvalidOperationException("BnB participant lost its match transport.");
                }
                var senders = ReferenceEquals(target, member.Game) ? _bnbGame : _bnbRelay;
                await select(senders!).SendAsync(target, message, ct).ConfigureAwait(false);
                lock (room.Gate)
                    if (!ReferenceEquals(MatchLink(room, member), target))
                        throw new InvalidOperationException("BnB match transport changed while an event was sent.");
            }
            ct.ThrowIfCancellationRequested();
            lock (room.Gate)
            {
                if (!BnBIsCurrent(room, state, requirePlaying: true) || !ReferenceEquals(state.Ledger, original))
                    throw new InvalidOperationException("BnB round changed before publication completed.");
                state.Ledger = candidate;
                ScheduleBnB(room, state);
            }
        }
        catch (Exception)
        {
            await CancelBnBAsync(room, state).ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask ApplyBnBExplosionAsync(PlayRoom room, BnBRuntimeState state, int actor,
        IReadOnlyList<BnBBombIdentity> bombs, long now, CancellationToken ct)
    {
        var candidate = state.Ledger.Clone();
        if (candidate.TryExplode(actor, bombs, now) != BnBExplosionResult.Applied) return;
        var message = new RoundBnBExplosionPublished(actor,
            bombs.Select(bomb => new RoundBnBBomb(bomb.Generation, bomb.GridIndex)).ToArray());
        await PublishBnBAsync(room, state, candidate, senders => senders.Explosion, message, ct).ConfigureAwait(false);
    }

    private async ValueTask StartBnBAsync(PlayRoom room)
    {
        if (!IsBnBMode(room) || room.BnBRound is not { } state) return;
        var previous = room.BnBClock;
        if (previous is not null)
        {
            previous.Cancel();
            await previous.Completion.ConfigureAwait(false);
        }
        lock (room.Gate)
        {
            if (!BnBIsCurrent(room, state, requirePlaying: true)) return;
            room.BnBClock = new(room.Executor, _lifetime, (Func<Exception, ValueTask>)(async failure =>
            {
                _logger.LogError(failure, "Room {Room}: BnB deadline failed.", room.Id);
                await CancelBnBAsync(room, state).ConfigureAwait(false);
            }));
            ScheduleBnB(room, state);
        }
    }

    // The clock retains one replaceable deadline; all transitions rejoin the room executor.
    private void ScheduleBnB(PlayRoom room, BnBRuntimeState state)
    {
        var retirement = state.Ledger.NextTimerDeadlineMilliseconds;
        var report = state.Ledger.NextBombReportDeadlineMilliseconds(_options.BnBExplosionReportGraceMs);
        var next = retirement is null ? report : report is null ? retirement : Math.Min(retirement.Value, report.Value);
        if (next is null || room.BnBClock is not { } clock) return;
        clock.Schedule((int)Math.Clamp(next.Value - BnBMilliseconds(), 0, int.MaxValue), async ct =>
        {
            lock (room.Gate) if (!BnBIsCurrent(room, state, requirePlaying: true)) return;
            try
            {
                var now = BnBMilliseconds();
                foreach (var actor in state.Actors)
                {
                    var due = state.Ledger.DueBombs(actor, now, _options.BnBExplosionReportGraceMs);
                    if (due.Count > 0) await ApplyBnBExplosionAsync(room, state, actor, due, now, ct).ConfigureAwait(false);
                }
                lock (room.Gate)
                {
                    if (!BnBIsCurrent(room, state, requirePlaying: true)) return;
                    state.Ledger.AdvanceTimers(now);
                    ScheduleBnB(room, state);
                }
            }
            catch (Exception)
            {
                await CancelBnBAsync(room, state).ConfigureAwait(false);
                throw;
            }
        });
    }

    // The registry calls under its gate before a closed relay can lose its seat and connection indexes.
    private void OnBnBParticipantConnectionClosed(PlayRoom room, ISession session)
    {
        if (!IsBnBMode(room) || room.Phase is not (RoomPhase.Starting or RoomPhase.Playing)
            || room.BnBRound is not { } state || state.Generation != room.StartGeneration
            || !session.Connection.IsClosed || !state.Members.Any(member => ReferenceEquals(member.Game, session)
                || ReferenceEquals(member.Relay, session) && member.RelayGeneration == state.Generation
                    && !UsesGameLink(room, member))) return;
        _ = BeginBnBCancellation(room, state);
    }

    private ValueTask CancelBnBAsync(PlayRoom room, BnBRuntimeState state) => new(BeginBnBCancellation(room, state));

    // A seat that leaves is no longer a BnB member; the others keep playing and the elimination check decides the
    // round. Cancelling here closed every remaining player's connection, so leaving a BnB round disconnected them.
    private bool ReleaseDepartedBnBMembers(PlayRoom room, BnBRuntimeState state)
    {
        lock (room.Gate)
        {
            if (!ReferenceEquals(room.BnBRound, state) || state.Generation != room.StartGeneration || room.RoundCancelled)
                return false;
            state.Members = [.. state.Members.Where(member => ReferenceEquals(room.FindByGame(member.Game.Id), member))];
            return state.Members.Length > 0;
        }
    }

    private Task BeginBnBCancellation(PlayRoom room, BnBRuntimeState state)
    {
        lock (room.Gate)
        {
            if (!ReferenceEquals(room.BnBRound, state) || room.StartGeneration != state.Generation) return Task.CompletedTask;
            if (state.CleanupTask is { } existing) return existing;
            if (room.IsStopping) return Task.CompletedTask;
            room.RoundCancelled = true;
            IConnection[] connections = [.. room.Seats.SelectMany(seat => new[] { seat.Game.Connection, seat.Relay?.Connection })
                .OfType<IConnection>().Distinct<IConnection>(ReferenceEqualityComparer.Instance)];
            // Close events may run under match-service locks. One detached task owns this bounded connection set.
            return room.BnBCleanupTask = state.CleanupTask = BackgroundWork.Detached(() => CloseBnBConnectionsAsync(room, state, connections));
        }
    }

    private async Task CloseBnBConnectionsAsync(PlayRoom room, BnBRuntimeState state, IConnection[] connections)
    {
        foreach (var connection in connections)
        {
            try
            {
                ValueTask closing;
                lock (room.Gate)
                {
                    if (!ReferenceEquals(room.BnBRound, state) || room.StartGeneration != state.Generation) return;
                    if (connection.IsClosed || !room.Seats.Any(seat => ReferenceEquals(seat.Game.Connection, connection)
                        || ReferenceEquals(seat.Relay?.Connection, connection))) continue;
                    closing = connection.CloseAsync("BnB state could not be synchronized; round cancelled.");
                }
                await closing.ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                _logger.LogError(failure, "Room {Room}: connection {Connection} failed to close after BnB cancellation.", room.Id, connection.Id);
            }
        }
    }
}
