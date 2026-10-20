using BubbleFighter.Fantasy.Contracts.Fighters;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Movement;
using BubbleFighter.Fantasy.Contracts.Sessions;
using System.Net;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Progress;
using BubbleFighter.Fantasy.InGame.Data;
using BubbleFighter.Fantasy.InGame.Dialects;
using BubbleFighter.Fantasy.InGame.Sessions;
using BubbleFighter.Fantasy.InGame.Wire;
using Microsoft.Extensions.Logging;
using BubbleFighter.Fantasy.Contracts.Rooms;

namespace BubbleFighter.Fantasy.InGame.Handlers;

/// <summary>
/// The round end: the round clock, the client's game-over and statistics reports, the round
/// rewards and the timed result stages (S2C 612, 614, 615, 576 and 620) back to the room.
///
/// </summary>
internal sealed class MatchResultFlow
{
    private readonly InGameOptions _options;
    private readonly RoomRegistry _registry;
    private readonly ILogger _logger;
    private readonly CancellationToken _lifetime;
    private readonly IPlayerProgressRepository? _progress;
    private readonly LevelTable? _levels;
    private readonly IEventBus? _events;

    /// <summary>Builds the round end for one activated row.</summary>
    /// <param name="options">The row configuration.</param>
    /// <param name="registry">The room registry.</param>
    /// <param name="logger">The row logger.</param>
    /// <param name="lifetime">Cancellation when the owning plugin unloads.</param>
    /// <param name="progress">The level / experience store round rewards are recorded in; none records no rewards.</param>
    /// <param name="events">The bus a level rise is announced on; none announces nothing.</param>
    public MatchResultFlow(InGameOptions options, RoomRegistry registry, ILogger logger,
        CancellationToken lifetime = default, IPlayerProgressRepository? progress = null, IEventBus? events = null)
    {
        _events = events;
        _options = options;
        _registry = registry;
        _logger = logger;
        _lifetime = lifetime;
        _progress = progress;
        _levels = options.LevelExpThresholds.Count > 0 ? new LevelTable(options.LevelExpThresholds, options.MaxLevel) : null;
    }

    public async ValueTask HandleGameOverAsync(ISession session, InboundPacket packet, CancellationToken ct)
    {
        EmptyPayload.Parse(packet.Payload.Span, "C2S 625");
        // 0xB38850 emits this for an individual death as well as elimination.
        if (_registry.TryGet(session.Id) is not { } room)
        {
            _logger.LogDebug(
                "Session {Id} C2S 625 game over with no room; ignoring.", session.Id);
            return;
        }
        lock (room.Gate)
        {
            // Mode 1 uses the timed score rules (0x711210), so one death cannot finish it.
            if (room.ModeId == (int)GameModeId.Pvp)
            {
                _logger.LogInformation("Session {Id} C2S 625 does not end timed-score room {Room}.", session.Id, room.Id);
                return;
            }
        }
        await OpenResultAsync(room, ct).ConfigureAwait(false);
    }

    public ValueTask HandleResultStatsAsync(ISession session, InboundPacket packet, CancellationToken ct)
    {
        var stats = GameResultStats.Parse(packet.Payload.Span);
        if (_registry.TryGet(session.Id) is not { } room)
            return ValueTask.CompletedTask;
        lock (room.Gate)
        {
            var seat = room.Seats.FirstOrDefault(s =>
                s.Game.Id == session.Id || (s.Relay is not null && s.Relay.Id == session.Id));
            if (seat is not null)
                seat.RoundStats = stats.Values;
        }
        // 0x9E88C0 emits C2S 103 while opening results, not after the reward display ends.
        _logger.LogInformation("Session {Id} C2S 103 stored {Count} result counters without advancing the result stage.",
            session.Id, stats.Values.Length);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Opens the result screen for a finished round: one S2C 612 per seat,
    /// once. The 612 handler (sub_9E88C0, dispatcher case 0x264) tears the
    /// game info down, opens the scoreboard through the mode manager's notify
    /// 296 and advances the session phase 4 to 5; the client answers by
    /// building C2S 103 from inside the same handler (sub_9E80F0), so the
    /// counters must never re-send 612 or advance the timed result stages.
    ///
    /// </summary>
    /// <param name="room">The room whose round finished.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task OpenResultAsync(PlayRoom room, CancellationToken ct)
    {
        try
        {
            await room.Executor.ExecuteAsync(operationCt => OpenResultCoreAsync(room, operationCt), ct).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // The room closed before the round could end; there is no one left to show a result to.
        }
    }

    // Runs on the room's executor: the phase change and the S2C 612 fan-out see one roster, and the
    // later stages are queued by the detached sequence after their display delays.
    private async ValueTask OpenResultCoreAsync(PlayRoom room, CancellationToken ct)
    {
        int generation;
        RoomSeat[] seats;
        CancellationTokenSource cancellation;
        lock (room.Gate)
        {
            if (room.ResultOpenSent || room.Phase == RoomPhase.Closed)
                return;
            ct.ThrowIfCancellationRequested();
            _lifetime.ThrowIfCancellationRequested();
            if (!room.ResultTask.IsCompleted)
                throw new InvalidOperationException($"Room {room.Id} still owns a previous result sequence.");
            room.ResultOpenSent = true;
            room.MarkResult();
            generation = room.StartGeneration;
            seats = room.Seats.ToArray();
            cancellation = room.BeginResultSequence(_lifetime);
        }

        // 612, +1s 614, +6s 615, +16s 576/620.
        bool opened;
        try
        {
            opened = await SendResultStageAsync(room, generation, seats, InGameOpcodes.S2CResultOpen,
                GameToClient.ResultOpen(0, 0)).ConfigureAwait(false);
        }
        catch
        {
            await room.ReleaseResultSequenceAsync(cancellation).ConfigureAwait(false);
            throw;
        }
        if (!opened)
        {
            await room.ReleaseResultSequenceAsync(cancellation).ConfigureAwait(false);
            return;
        }
        var sequence = BackgroundWork.Detached(() => RunResultSequenceAsync(room, generation, seats, cancellation));
        lock (room.Gate)
            room.ResultTask = sequence;
    }

    /// <summary>
    /// Records what each seat earned this round - experience, the level it reaches and Lucci -
    /// and returns the rewards by slot for the S2C 614 rows. A seat whose record fails is logged
    /// and shows no reward rather than holding up the result screen for the others.
    ///
    /// </summary>
    private async Task<Dictionary<int, RoundReward>> GrantRewardsAsync(PlayRoom room, RoomSeat[] seats, CancellationToken ct)
    {
        var rewards = new Dictionary<int, RoundReward>();
        if (_progress is null || _levels is null)
            return rewards;
        int winner;
        string matchId;
        lock (room.Gate)
        {
            winner = room.BlueScore > room.RedScore ? 1 : room.RedScore > room.BlueScore ? 2 : 0;
            matchId = room.MatchId;
        }
        foreach (var seat in seats)
        {
            if (seat.UserNum <= 0)
                continue;
            try
            {
                var stored = await _progress.FindAsync(seat.UserNum, ct).ConfigureAwait(false);
                var level = Math.Max(stored?.Level ?? seat.Level, 1);
                var before = Math.Max(stored?.Exp ?? 0, _levels.Start(level));
                var reward = RoundReward.Compute(_levels, before, winner != 0 && seat.Team == winner, 0,
                    _options.RewardBaseExp, _options.RewardExpPerKill, _options.RewardWinMultiplier, _options.RewardLucciPercent);
                // The earned amounts come from the read above; the store applies them to the progress it holds
                // under the player's lock, once per round start.
                var settled = await RoundSettler.SettleAsync(_progress, _levels, matchId, seat.UserNum, reward.ExpEarned,
                    reward.LucciEarned, _options.SettlementAttempts, _logger, ct).ConfigureAwait(false);
                var after = settled.After;
                reward = reward with
                {
                    Exp = after.Exp, Level = after.Level, LevelStart = _levels.Start(after.Level),
                    NextLevelStart = _levels.Start(after.Level + 1), LucciEarned = settled.LucciEarned,
                };
                rewards[seat.Slot] = reward;
                lock (room.Gate)
                    seat.Level = reward.Level;
                await LevelRaisedPublisher.PublishAsync(_events, _logger, _options.GameProtocol, seat.UserNum,
                    Math.Max(1, settled.Before.Level), reward.Level, ct).ConfigureAwait(false);
                _logger.LogInformation(
                    "Room {Room} slot {Slot} ({User}) {Outcome} {Exp} exp and {Lucci} Lucci for match {Match}: level {Level}, exp {Total}, Lucci balance {Balance}.",
                    room.Id, seat.Slot, seat.UserNum, settled.Outcome, reward.ExpEarned, reward.LucciEarned, matchId, reward.Level,
                    reward.Exp, settled.LucciBalance);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogError(exception, "Room {Room} slot {Slot} ({User}): the round reward could not be recorded.",
                    room.Id, seat.Slot, seat.UserNum);
            }
        }
        return rewards;
    }

    // S2C 614 stat block (wire order +56... +384): +56 is the level, +368..+384 the experience figures and
    // Lucci of RoundReward; the rest stays zero.
    private static int[] RewardStats(RoomSeat seat, RoundReward? reward)
    {
        var stats = new int[ResultScoreboardRow.StatSlotCount];
        stats[0] = reward?.Level ?? seat.Level;
        if (reward is null)
            return stats;
        stats[22] = checked((int)reward.LevelStart);
        stats[23] = checked((int)reward.Exp);
        stats[24] = checked((int)reward.NextLevelStart);
        stats[25] = reward.ExpEarned;
        stats[26] = reward.LucciEarned;
        return stats;
    }

    private async Task RunResultSequenceAsync(PlayRoom room, int generation, RoomSeat[] seats,
        CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(_options.ResultScoreboardDelayMs, cancellation.Token).ConfigureAwait(false);
            var rewards = await GrantRewardsAsync(room, seats, cancellation.Token).ConfigureAwait(false);
            if (room.ModeId == (int)GameModeId.Pvp || _options.SendScoreboardRows)
            {
                var sent = await ResultStepAsync(room, async _ =>
                {
                    byte[] scoreboard;
                    lock (room.Gate)
                    {
                        if (!IsCurrentResult(room, generation)) return false;
                        // 0x9E5E70 seeds row+0 from player id, +52 from team and +500 with presence=1.
                        var rows = seats.Select(seat => ResultScoreboardRow.ForSeat(seat.Slot, seat.Nickname, null) with
                        {
                            UserNumber = seat.UserNum,
                            Unknown52 = checked((byte)seat.Team),
                            Unknown500 = 1,
                            UnknownStatBlock = RewardStats(seat, rewards.GetValueOrDefault(seat.Slot)),
                        });
                        scoreboard = GameToClient.ScoreboardData(room.ModeId == (int)GameModeId.Pvp
                            ? MatchLife1256.Scoreboard(room.BlueScore, room.RedScore, rows)
                            : ResultScoreboard.FromRows(rows));
                    }
                    return await SendResultStageAsync(room, generation, seats, InGameOpcodes.S2CScoreboardData, scoreboard)
                        .ConfigureAwait(false);
                }).ConfigureAwait(false);
                if (!sent)
                    return;
            }
            else if (!IsCurrentResult(room, generation))
                return;

            await Task.Delay(_options.ResultRewardDelayMs, cancellation.Token).ConfigureAwait(false);
            var closed = await ResultStepAsync(room, async _ =>
            {
                if (!await SendResultStageAsync(room, generation, seats, InGameOpcodes.S2CResultClose,
                        GameToClient.ResultClose(0)).ConfigureAwait(false))
                    return false;
                lock (room.Gate)
                {
                    if (!IsCurrentResult(room, generation))
                        return false;
                    room.ResultCloseSent = true;
                }
                return true;
            }).ConfigureAwait(false);
            if (!closed)
                return;

            // 0xC6BA70 enters reward state 3; the native reward stage runs for ten seconds.
            await Task.Delay(_options.ResultCloseGraceMs, cancellation.Token).ConfigureAwait(false);
            await ResultStepAsync(room, async operationCt =>
            {
                if (!await SendResultStageAsync(room, generation, seats, InGameOpcodes.S2CRelayStateClear,
                        GameToClient.RelayStateClear()).ConfigureAwait(false)
                    || !await SendResultStageAsync(room, generation, seats, InGameOpcodes.S2CRoundStageReset,
                        GameToClient.RoundStageReset()).ConfigureAwait(false))
                    return false;
                lock (room.Gate)
                {
                    if (!IsCurrentResult(room, generation))
                        return false;
                    room.ResetToWaiting();
                }
                // Back in the room: the seats vacated during the round are announced now.
                await SeatDeparture.AnnounceDeferredAsync(room, _options.ModelItemIds, _logger, operationCt)
                    .ConfigureAwait(false);
                return true;
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested || _lifetime.IsCancellationRequested)
        {
            // Room removal, a restart or unload is not a failure of a surviving game session.
        }
        catch (ObjectDisposedException)
        {
            // The room was deleted between two stages.
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Room {Room} result sequence failed.", room.Id);
            throw;
        }
        finally
        {
            await room.ReleaseResultSequenceAsync(cancellation).ConfigureAwait(false);
        }
    }

    // Queues one result stage on the room's executor and reports whether the sequence should go on.
    private async Task<bool> ResultStepAsync(PlayRoom room, Func<CancellationToken, Task<bool>> step)
    {
        var proceed = false;
        await room.Executor.ExecuteAsync(async operationCt => proceed = await step(operationCt).ConfigureAwait(false),
            _lifetime).ConfigureAwait(false);
        return proceed;
    }

    private bool IsCurrentResult(PlayRoom room, int generation)
    {
        lock (room.Gate)
            return !_lifetime.IsCancellationRequested && room.StartGeneration == generation
                && room.Phase == RoomPhase.Result && room.ResultOpenSent;
    }

    private async Task<bool> SendResultStageAsync(PlayRoom room, int generation, RoomSeat[] seats, ushort opcode, byte[] body)
    {
        foreach (var seat in seats)
        {
            lock (room.Gate)
            {
                if (!IsCurrentResult(room, generation))
                    return false;
                if (!ReferenceEquals(room.FindByGame(seat.Game.Id), seat))
                    continue;
            }
            _lifetime.ThrowIfCancellationRequested();
            try
            {
                await seat.Game.SendAsync(opcode, body, _lifetime).ConfigureAwait(false);
            }
            catch (Exception exception) when (!_lifetime.IsCancellationRequested)
            {
                bool departed;
                lock (room.Gate)
                    departed = !ReferenceEquals(room.FindByGame(seat.Game.Id), seat) || seat.Game.Connection.IsClosed;
                if (!departed)
                    throw;
                _logger.LogInformation(exception,
                    "Room {Room} result packet {Opcode} skipped for departed session {Session}.",
                    room.Id, opcode, seat.Game.Id);
            }
        }
        return IsCurrentResult(room, generation);
    }

    /// <summary>
    /// Arms the round clock with the same length the S2C 608 body carried, so
    /// the server knows when the round ends. The client sends nothing when its
    /// own copy of the clock expires, so the server's own timer ends the round.
    ///
    /// </summary>
    /// <param name="room">The room whose round is starting.</param>
    /// <param name="roundMs">The round length the S2C 608 body carried.</param>
    internal void ArmRoundClock(PlayRoom room, int roundMs)
    {
        if (roundMs == MatchStartFlow.NoTimeLimitMs)
            return;
        var ends = DateTime.UtcNow.AddMilliseconds(roundMs);
        room.RoundEndsAtUtc = ends;
        _ = BackgroundWork.Detached(() => CompleteRoundWhenDueAsync(room, ends));
    }

    /// <summary>
    /// Completes a timed round at its expiry. The client clock has no outbound
    /// expiry packet, so the server ends the round on its own one-shot task. The
    /// captured end timestamp makes a stale task harmless when a room is reset
    /// and started again.
    ///
    /// </summary>
    /// <param name="room">The room whose round is timed.</param>
    /// <param name="ends">The expiry timestamp captured when the round began.</param>
    private async Task CompleteRoundWhenDueAsync(PlayRoom room, DateTime ends)
    {
        try
        {
            var delay = ends - DateTime.UtcNow;
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, _lifetime).ConfigureAwait(false);

            lock (room.Gate)
            {
                if (room.RoundEndsAtUtc != ends || room.Phase != RoomPhase.Playing || room.ResultOpenSent)
                    return;
            }

            await OpenResultAsync(room, _lifetime).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Room {Room} timed round completion failed.", room.Id);
        }
    }

}
