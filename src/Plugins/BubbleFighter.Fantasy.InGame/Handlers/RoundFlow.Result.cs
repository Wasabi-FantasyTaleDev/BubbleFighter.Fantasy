using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Progress;
using BubbleFighter.Fantasy.Contracts.Wallet;
using BubbleFighter.Fantasy.InGame.Data;
using BubbleFighter.Fantasy.InGame.Sessions;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.InGame.Handlers;

/// <summary>
/// The round's score and its result sequence, driven by typed messages only: the clients'
/// knock-down and finishing-blow reports become the round's counters and the live score every
/// member sees, and the round then ends in timed stages - scoreboard, end, the scoreboard again
/// with the rewards, the reward stage - before the room comes back. Which packet carries each
/// stage belongs to the build's dialect.
/// </summary>
internal sealed partial class RoundFlow
{
    private readonly RoundResultSenders? _result;
    private readonly Func<long, CancellationToken, ValueTask<PlayerWallet?>>? _readWallet;
    private readonly LevelTable? _levels;
    private readonly IPlayerProgressRepository? _progress;
    private readonly IEventBus? _events;

    /// <summary>
    /// Resolves what the result sequence needs beyond the round itself: the build's result
    /// encoders, the level table the rewards are computed from and the wallet the balance refresh
    /// reports. A build whose dialect publishes no result encoder keeps the plain end path.
    /// </summary>
    /// <param name="context">The row's context.</param>
    /// <param name="protocol">The game protocol the row serves.</param>
    /// <param name="options">The row configuration.</param>
    /// <returns>The resolved result senders and reward inputs, or <see langword="null"/> when the build has no result surface.</returns>
    private static RoundResultSurface? ResolveResult(IPluginContext context, string protocol, InGameOptions options)
    {
        var score = PlaySenders.Optional<RoundScoreUpdated>(context, protocol);
        var scoreboard = PlaySenders.Optional<RoundScoreboard>(context, protocol);
        var ended = PlaySenders.Optional<RoundEnded>(context, protocol);
        var stage = PlaySenders.Optional<ResultStageAdvanced>(context, protocol);
        if (score is null && scoreboard is null && stage is null)
            return null;
        if (score is null || scoreboard is null || ended is null || stage is null)
            throw new ConfigException($"Round results on '{protocol}' require score, scoreboard, end and reward-stage senders; the dialect publishes an incomplete result surface.");
        var wallets = context.Services.TryGet<IPlayerWalletRepository>(out var wallet) ? wallet : null;
        var levels = options.LevelExpThresholds.Count > 0 ? new LevelTable(options.LevelExpThresholds, options.MaxLevel) : null;
        return new RoundResultSurface(
            new RoundResultSenders(score, scoreboard, ended, stage,
                PlaySenders.Optional<RoundPlayerWalletChanged>(context, protocol)),
            levels,
            wallets is null ? null : (userNum, ct) => wallets.FindByUserNumAsync(userNum, ct),
            context.Services.TryGet<IPlayerProgressRepository>(out var progress) ? progress : null);
    }

    /// <summary>
    /// Counts one knock-down report and publishes the seat's new counters to every member of the
    /// round. Only the reporter's side moves: what a knock-down writes on the victim is not proven
    /// for this build, so the victim is remembered for the finishing blow and nothing more.
    /// </summary>
    /// <param name="session">The reporting session.</param>
    /// <param name="report">The decoded report.</param>
    /// <param name="ct">Cancellation token.</param>
    public async ValueTask HandleKillCreditReportedAsync(ISession session, RoundKillCreditReported report, CancellationToken ct)
    {
        if (_result is null || _registry.TryGet(session.Id) is not { } room || room.Tutorial is not null)
            return;
        await room.Executor.ExecuteAsync(async operationCt =>
        {
            RoundScoreUpdated update;
            string nickname;
            int knockdowns;
            lock (room.Gate)
            {
                if (room.Phase != RoomPhase.Playing || room.FindByGame(session.Id) is not { } controller)
                    return;
                var reporter = ScoreReporter(room, controller, report.ReporterSlot, report.RequiresReporterOwnership);
                if (reporter is null) return;
                if (report.RequiresReporterOwnership && (report.ReporterSlot != reporter.Slot || report.Victim == reporter.Slot))
                    return;
                if (report.IsRescue && (report.ReporterSlot != reporter.Slot
                    || report.Victim is not { } rescuedSlot || rescuedSlot == reporter.Slot
                    || ScoreMember(room, rescuedSlot) is not { } rescued
                    || rescued.Team != reporter.Team))
                    return;
                if (!report.IsRescue && report.Victim is { } victim)
                    room.Scoring.RememberVictim(reporter.Slot, victim);
                if ((report.IsRescue ? room.Scoring.AddRescue(reporter.Slot) : room.Scoring.AddKnockdown(reporter.Slot)) is not { } stats)
                    return;
                knockdowns = stats.Knockdowns;
                nickname = reporter.Nickname;
                update = new RoundScoreUpdated(room.Id, reporter.Team, [.. room.Scoring.TeamTotals], [stats], LiveScoreboard(room));
            }
            await SendScoreAsync(room, update, operationCt).ConfigureAwait(false);
            _logger.LogInformation(
                "Room {Room} seat {Slot} ({Nick}) reported score credit (rescue={Rescue}), knock-downs {Knockdowns}, victim {Victim}, combo {Combo}.",
                room.Id, report.ReporterSlot, nickname, report.IsRescue, knockdowns, report.Victim, report.ComboCount);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Counts one finishing blow as a kill for the reporting seat and a point for its team, and
    /// publishes the new live score. A victim omitted by the dialect comes from the outstanding
    /// knock-down; an explicit NPC reference never borrows that victim. A seated victim is counted
    /// one death and travels in the same update.
    /// </summary>
    /// <param name="session">The reporting session.</param>
    /// <param name="report">The decoded report.</param>
    /// <param name="ct">Cancellation token.</param>
    public async ValueTask HandleFinishingBlowReportedAsync(ISession session, RoundFinishingBlowReported report, CancellationToken ct)
    {
        if (_result is null || _registry.TryGet(session.Id) is not { } room || room.Tutorial is not null)
            return;
        await room.Executor.ExecuteAsync(async operationCt =>
        {
            RoundScoreUpdated update;
            string nickname;
            int kills;
            int? victim;
            lock (room.Gate)
            {
                if (room.Phase != RoomPhase.Playing || room.FindByGame(session.Id) is not { } controller)
                    return;
                var reporter = ScoreReporter(room, controller, report.ReporterSlot,
                    report.RequiresReporterOwnership || report.RequiresDirectKillMode);
                if (reporter is null) return;
                if (report.RequiresReporterOwnership && (report.ReporterSlot != reporter.Slot
                    || !report.HasVictimReference || report.Victim == reporter.Slot))
                    return;
                if (report.RequiresDirectKillMode && (!_options.DirectKillModes.Contains(room.ModeId)
                    || report.ReporterSlot != reporter.Slot || !report.HasVictimReference
                    || report.Victim == reporter.Slot))
                    return;
                var pairedVictim = room.Scoring.TakeVictim(reporter.Slot);
                victim = report.HasVictimReference ? report.Victim : pairedVictim;
                var seatedVictim = victim is { } slot && slot != reporter.Slot && ScoreMember(room, slot) is not null ? victim : null;
                if (room.Scoring.AddKill(reporter.Slot, reporter.Team, seatedVictim) is not { } stats)
                    return;
                kills = stats.Kills;
                nickname = reporter.Nickname;
                update = new RoundScoreUpdated(room.Id, reporter.Team, [.. room.Scoring.TeamTotals],
                    seatedVictim is { } died ? [stats, room.Scoring.Stats(died)] : [stats], LiveScoreboard(room));
            }
            await SendScoreAsync(room, update, operationCt).ConfigureAwait(false);
            _logger.LogInformation(
                "Room {Room} seat {Slot} ({Nick}) confirmed kill {Kills} over {Victim}; team totals {Totals}.",
                room.Id, report.ReporterSlot, nickname, kills, victim, string.Join(':', update.TeamTotals));
        }, ct).ConfigureAwait(false);
    }

    // The live score goes to every member of the running round; the counters were read under the gate.
    private async ValueTask SendScoreAsync(PlayRoom room, RoundScoreUpdated update, CancellationToken ct)
    {
        if (_result is not { } result)
            return;
        RoomSeat[] members;
        lock (room.Gate)
        {
            if (room.Phase != RoomPhase.Playing)
                return;
            members = [.. room.Seats];
        }
        foreach (var member in members)
        {
            ct.ThrowIfCancellationRequested();
            await result.Score.SendAsync(member.Game, update, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The result sequence a build with a result surface runs instead of the bare end: the
    /// scoreboard, the end message, the scoreboard again carrying the rewards, the move to the
    /// reward stage, the balance refresh, and finally the return to the room. Every step is timed
    /// by the row's options and nothing is gated on what a client sends.
    /// </summary>
    /// <param name="room">The room whose round finished.</param>
    /// <param name="reason">Why the round ended.</param>
    /// <param name="generation">The round generation, so a stale sequence cannot touch a new round.</param>
    /// <param name="ct">Cancellation token for the retained result sequence.</param>
    private async ValueTask RunTypedResultAsync(PlayRoom room, RoundEndReason reason, int generation, CancellationToken ct)
    {
        if (_result is not { } result) return;
        string? lifeChecks;
        lock (room.Gate)
            lifeChecks = room.LifeChecks.Any ? room.LifeChecks.Summary() : null;
        if (lifeChecks is not null)
            _logger.LogInformation("Room {Room} match {Match}: ignored life reports {Checks}.", room.Id, room.MatchId, lifeChecks);
        var rewards = ComputeRewards(room, generation, reason);
        var rewardStartedAt = Environment.TickCount64;
        var outcome = OutcomeOf(room, generation, reason);
        var scoreDelay = 0;
        if (!await TypedResultStepAsync(room, generation, async token =>
        {
            if (reason == RoundEndReason.TimeLimit)
            {
                scoreDelay = Math.Min(_options.ResultTimeOverDelayMs, _options.ResultRewardDelayMs);
                return await SendToMembersAsync(room, generation, (seat, sendCt) =>
                    result.Ended.SendAsync(seat.Game, new RoundEnded(room.Id, outcome(seat), reason)
                        { ModeId = room.ModeId, RecipientTeam = seat.Team }, sendCt), token).ConfigureAwait(false);
            }
            var endFirst = result.Ended is IRoundResultSequencePolicy { EndBeforeScoreboard: true };
            if (!endFirst && !await SendScoreboardAsync(room, generation, rewards, false, token, reason).ConfigureAwait(false))
                return false;
            if (!await SendToMembersAsync(room, generation, (seat, sendCt) =>
                result.Ended.SendAsync(seat.Game, new RoundEnded(room.Id, outcome(seat), reason)
                    { ModeId = room.ModeId, RecipientTeam = seat.Team }, sendCt), token).ConfigureAwait(false))
                return false;
            if (endFirst) scoreDelay = Math.Min(_options.ResultScoreboardDelayMs, _options.ResultRewardDelayMs);
            return true;
        }, ct).ConfigureAwait(false)) return;

        // A single-scoreboard client gets the rewards granted before its one scoreboard, which carries them.
        var single = result.Ended is IRoundResultSequencePolicy { EndBeforeScoreboard: true, SingleScoreboard: true };
        if (reason == RoundEndReason.TimeLimit || result.Ended is IRoundResultSequencePolicy { EndBeforeScoreboard: true })
        {
            await Task.Delay(scoreDelay, ct).ConfigureAwait(false);
            if (!await TypedResultStepAsync(room, generation, async token =>
            {
                if (single) rewards = await GrantRewardsAsync(room, generation, reason, token).ConfigureAwait(false);
                return await SendScoreboardAsync(room, generation, rewards, single, token, reason,
                    timeOver: reason == RoundEndReason.TimeLimit).ConfigureAwait(false);
            }, ct).ConfigureAwait(false)) return;
        }

        // Result intervals are preserved; only each stage owns the executor.
        if (!single && !await TypedResultStepAsync(room, generation, async token =>
        {
            rewards = await GrantRewardsAsync(room, generation, reason, token).ConfigureAwait(false);
            return true;
        }, ct).ConfigureAwait(false)) return;
        var remainingDelay = Math.Max(0, _options.ResultRewardDelayMs - (Environment.TickCount64 - rewardStartedAt));
        await Task.Delay(TimeSpan.FromMilliseconds(remainingDelay), ct).ConfigureAwait(false);
        if (!await TypedResultStepAsync(room, generation, async token =>
        {
            if (!single && !await SendScoreboardAsync(room, generation, rewards, true, token, reason).ConfigureAwait(false)) return false;
            if (!await SendToMembersAsync(room, generation, (seat, sendCt) =>
                result.Stage.SendAsync(seat.Game, new ResultStageAdvanced(room.Id, ResultStage.Reward), sendCt), token)
                .ConfigureAwait(false)) return false;
            await SendWalletsAsync(room, generation, rewards, token).ConfigureAwait(false);
            lock (room.Gate)
                foreach (var seat in room.Seats)
                    if (rewards.TryGetValue(seat.Slot, out var reward)) seat.Level = reward.Level;
            return true;
        }, ct).ConfigureAwait(false)) return;

        if (LotteryAllowed(_options, room.ModeId, reason))
        {
            var openDelay = _options.LotteryOpenDelayMs == 0 ? _options.ResultCloseGraceMs
                : Math.Max(0, _options.LotteryOpenDelayMs - (Environment.TickCount64 - rewardStartedAt));
            await Task.Delay(TimeSpan.FromMilliseconds(openDelay), ct).ConfigureAwait(false);
        }
        var lotteryOpened = false;
        if (!await TypedResultStepAsync(room, generation, async token =>
        {
            lotteryOpened = await BeginLotteryAsync(room, generation, reason, rewards, token).ConfigureAwait(false);
            return true;
        }, ct).ConfigureAwait(false) || lotteryOpened) return;
        await Task.Delay(_options.ResultCloseGraceMs, ct).ConfigureAwait(false);
        await TypedResultStepAsync(room, generation, async token =>
        {
            lock (room.Gate) room.ResetToWaiting();
            await _returnToRoom(room, token).ConfigureAwait(false);
            return true;
        }, ct).ConfigureAwait(false);
    }

    private async Task RunTypedResultDetachedAsync(PlayRoom room, RoundEndReason reason, int generation,
        CancellationTokenSource cancellation)
    {
        try
        {
            await RunTypedResultAsync(room, reason, generation, cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested || _lifetime.IsCancellationRequested) { }
        catch (ObjectDisposedException) { }
        catch (Exception failure)
        {
            _logger.LogError(failure, "Room {Room}: typed result sequence failed.", room.Id);
            throw;
        }
        finally
        {
            await room.ReleaseResultSequenceAsync(cancellation).ConfigureAwait(false);
        }
    }

    private async ValueTask<bool> TypedResultStepAsync(PlayRoom room, int generation,
        Func<CancellationToken, ValueTask<bool>> step, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var proceed = false;
        await room.Executor.ExecuteAsync(async token =>
        {
            lock (room.Gate)
                if (room.Phase != RoomPhase.Result || room.StartGeneration != generation || ct.IsCancellationRequested)
                    return;
            proceed = await step(token).ConfigureAwait(false);
        }, _lifetime).ConfigureAwait(false);
        return proceed;
    }

    // Pass 1 shows the experience the seat came in with and no gain; pass 2 carries the rewards.
    // The client replaces its whole table with
    // each scoreboard, so both passes repeat every row.
    private async ValueTask<bool> SendScoreboardAsync(PlayRoom room, int generation,
        IReadOnlyDictionary<int, RoundReward> rewards, bool withRewards, CancellationToken ct, RoundEndReason reason, bool timeOver = false)
    {
        RoundScoreboard scoreboard;
        lock (room.Gate)
        {
            if (room.Phase != RoomPhase.Result || room.StartGeneration != generation)
                return false;
            scoreboard = Scoreboard(room, rewards, withRewards, reason) with { TimeOver = timeOver, ModeId = room.ModeId };
        }
        return await SendToMembersAsync(room, generation,
            (seat, token) => _result!.Scoreboard.SendAsync(seat.Game, scoreboard, token), ct).ConfigureAwait(false);
    }

    // The table of a round as it stands; call under the room's gate.
    private RoundScoreboard Scoreboard(PlayRoom room, IReadOnlyDictionary<int, RoundReward> rewards, bool withRewards, RoundEndReason? reason = null)
    {
        var rows = new List<RoundScoreboardRow>(room.Seats.Count);
        var objectiveScores = _options.CooperativeScoreOutcomeModes.Contains(room.ModeId);
        foreach (var seat in room.Seats)
        {
            var reward = rewards.GetValueOrDefault(seat.Slot);
            rows.Add(new RoundScoreboardRow(seat.Slot, seat.Nickname, objectiveScores && seat.Team == 0 ? 2 : seat.Team,
                withRewards && reward is not null ? reward.Level : Math.Max(1, seat.Level), room.Scoring.Stats(seat.Slot),
                RewardRow(Math.Max(1, seat.Level), reward, withRewards))
            {
                AwardDisplayId = LotteryAwardItem(room.Id, room.StartGeneration, seat.Slot),
                UserNumber = seat.UserNum,
                Outcome = reason is { } ended ? OutcomeOf(room, room.StartGeneration, ended)(seat) : null,
            });
        }
        foreach (var bot in room.Bots.Select(bot => bot.Appearance).OrderBy(bot => bot.Slot))
            rows.Add(new RoundScoreboardRow(bot.Slot, bot.Nickname, bot.Team, bot.Level, room.Scoring.Stats(bot.Slot), null)
            {
                NicknameKey = bot.Ai?.NicknameKey ?? "",
                UserNumber = bot.UserNum,
                Outcome = reason is null ? null : WinnerTeam(room) == 0 ? RoundOutcome.Draw
                    : WinnerTeam(room) == bot.Team ? RoundOutcome.Won : RoundOutcome.Lost,
            });
        var totals = room.Scoring.TeamTotals.ToArray();
        var winner = WinnerTeam(room);
        if (objectiveScores)
        {
            // cn-324 sub_63B230 uses sub_455ED0 for mode 101; it compares ScoreData team totals.
            // Unassigned native slots are blue. Objective totals never overwrite personal kill statistics.
            totals = [0, 0];
            foreach (var row in rows.Where(row => row.Team is 1 or 2))
                totals[row.Team - 1] = checked(totals[row.Team - 1] + row.Stats.Kills);
            if (reason is RoundEndReason.WavesCleared or RoundEndReason.CooperativeFailed)
            {
                var teams = rows.Select(row => row.Team).Distinct().ToArray();
                if (teams.Length != 1 || teams[0] is not (1 or 2))
                    throw new InvalidOperationException("Cooperative objective score presentation requires one player team.");
                var players = teams[0];
                winner = reason == RoundEndReason.WavesCleared ? players : 3 - players;
                totals[winner - 1] = Math.Max(totals[winner - 1], checked(totals[2 - winner] + 1));
            }
        }
        return new RoundScoreboard(room.Id, winner, totals, rows, withRewards);
    }

    // The table a live score update carries along, with the experience each seat came in with; call
    // under the room's gate.
    private RoundScoreboard LiveScoreboard(PlayRoom room) =>
        Scoreboard(room, ComputeRewards(room, room.StartGeneration, RoundEndReason.StageReported), withRewards: false);

    // One fan-out per stage: the roster is read once, a seat that left is skipped rather than
    // failing the stage for everyone else, and the sequence stops when the round is gone.
    private async ValueTask<bool> SendToMembersAsync(PlayRoom room, int generation,
        Func<RoomSeat, CancellationToken, ValueTask> send, CancellationToken ct)
    {
        RoomSeat[] members;
        lock (room.Gate)
        {
            if (room.Phase != RoomPhase.Result || room.StartGeneration != generation)
                return false;
            members = [.. room.Seats];
        }
        foreach (var seat in members)
        {
            ct.ThrowIfCancellationRequested();
            lock (room.Gate)
            {
                if (room.StartGeneration != generation || !ReferenceEquals(room.FindByGame(seat.Game.Id), seat))
                    continue;
            }
            try
            {
                await send(seat, ct).ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                bool departed;
                lock (room.Gate)
                    departed = !ReferenceEquals(room.FindByGame(seat.Game.Id), seat) || seat.Game.Connection.IsClosed;
                if (!departed)
                    throw;
                _logger.LogInformation(failure, "Room {Room} result stage skipped for departed session {Session}.", room.Id, seat.Game.Id);
            }
        }
        lock (room.Gate)
            return room.Phase == RoomPhase.Result && room.StartGeneration == generation;
    }

    // The balance refresh goes out with the return; the amounts come from the store the row's
    // persistence publishes, so a deployment without one sends nothing rather than a made-up zero.
    private async ValueTask SendWalletsAsync(PlayRoom room, int generation,
        IReadOnlyDictionary<int, RoundReward> rewards, CancellationToken ct)
    {
        if (_result is not { } result || result.Wallet is not { } wallet || _readWallet is null)
            return;
        foreach (var seat in Snapshot(room, generation))
        {
            if (seat.UserNum <= 0)
                continue;
            var reward = rewards.GetValueOrDefault(seat.Slot);
            try
            {
                var balance = await _readWallet(seat.UserNum, ct).ConfigureAwait(false);
                await wallet.SendAsync(seat.Game, new RoundPlayerWalletChanged(seat.UserNum,
                    balance?.Lucci ?? 0, balance?.Cash ?? 0, reward?.Level ?? seat.Level), ct).ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                _logger.LogError(failure, "Room {Room} seat {Slot}: the balance refresh could not be sent.", room.Id, seat.Slot);
            }
        }
    }

    private RoomSeat[] Snapshot(PlayRoom room, int generation)
    {
        lock (room.Gate)
            return room.Phase == RoomPhase.Result && room.StartGeneration == generation ? [.. room.Seats] : [];
    }

    // The team with the strictly higher total wins and equal totals are a draw - the same rule the
    // client applies to the totals it is sent.
    private static int WinnerTeam(PlayRoom room)
    {
        if (room.DecidedWinnerTeam is { } decided)
            return decided;
        var totals = room.Scoring.TeamTotals;
        if (totals[0] > totals[1])
            return 1;
        return totals[1] > totals[0] ? 2 : 0;
    }

    // Resolution and reading happen under one gate, so a roster change between them cannot pair a
    // seat with a round that has already been replaced.
    private Func<RoomSeat, RoundOutcome> OutcomeOf(PlayRoom room, int generation, RoundEndReason reason)
    {
        int winner;
        IReadOnlySet<int>? winners;
        lock (room.Gate)
        {
            var current = room.StartGeneration == generation;
            winner = current ? WinnerTeam(room) : 0;
            winners = current ? room.DecidedWinnerSlots : null;
        }
        return seat => reason == RoundEndReason.WavesCleared ? RoundOutcome.Won
            : reason == RoundEndReason.CooperativeFailed ? RoundOutcome.Lost
            : winners is not null ? (winners.Count == 0 ? RoundOutcome.Draw : winners.Contains(seat.Slot) ? RoundOutcome.Won : RoundOutcome.Lost)
            : winner == 0 ? RoundOutcome.Draw : seat.Team == winner ? RoundOutcome.Won : RoundOutcome.Lost;
    }

    /// <summary>
    /// Computes independent experience and Lucci awards using the configured score model or
    /// the original deployment formula, then applies each currency's final factor. Live updates
    /// use this estimate without granting it.
    /// </summary>
    internal Dictionary<int, RoundReward> ComputeRewards(PlayRoom room, int generation, RoundEndReason reason)
    {
        var rewards = new Dictionary<int, RoundReward>();
        if (_levels is null)
            return rewards;
        RoomSeat[] seats;
        int winner;
        IReadOnlySet<int>? winners;
        lock (room.Gate)
        {
            if (room.StartGeneration != generation)
                return rewards;
            seats = [.. room.Seats];
            winner = WinnerTeam(room);
            winners = room.DecidedWinnerSlots;
        }
        foreach (var seat in seats)
        {
            if (seat.UserNum <= 0)
                continue;
            var stats = room.Scoring.Stats(seat.Slot);
            var won = reason == RoundEndReason.WavesCleared
                || reason != RoundEndReason.CooperativeFailed
                    && (winners is not null ? winners.Contains(seat.Slot) : winner != 0 && seat.Team == winner);
            var level = Math.Max(1, seat.Level);
            var reward = RoundReward.ComputeConfigured(_levels, _levels.Start(level), won,
                stats.Kills, stats.Score, _options);
            // Lucci picked up from NPC drops are coins as they are, after the deployment multiplier.
            rewards[seat.Slot] = reward with { LucciEarned = checked(reward.LucciEarned + room.Scoring.Lucci(seat.Slot)) };
        }
        return rewards;
    }

    internal async ValueTask<Dictionary<int, RoundReward>> GrantRewardsAsync(PlayRoom room, int generation,
        RoundEndReason reason, CancellationToken ct)
    {
        var rewards = ComputeRewards(room, generation, reason);
        if (_progress is null || _levels is null) return rewards;
        var matchId = room.MatchId;
        var committed = await Task.WhenAll(Snapshot(room, generation).Where(seat => rewards.ContainsKey(seat.Slot)).Select(async seat =>
        {
            var estimate = rewards[seat.Slot];
            try
            {
                var settled = await RoundSettler.SettleAsync(_progress, _levels, matchId, seat.UserNum, estimate.ExpEarned,
                    estimate.LucciEarned, _options.SettlementAttempts, _logger, ct).ConfigureAwait(false);
                var after = settled.After;
                var reward = estimate with
                {
                    Exp = after.Exp, Level = after.Level, LevelStart = _levels.Start(after.Level),
                    NextLevelStart = _levels.Start(after.Level + 1), LucciEarned = settled.LucciEarned,
                };
                _logger.LogInformation("Room {Room} seat {Slot}: {Outcome} {Exp} experience and {Lucci} Lucci for match {Match}; balance {Balance}.",
                    room.Id, seat.Slot, settled.Outcome, reward.ExpEarned, reward.LucciEarned, matchId, settled.LucciBalance);
                await LevelRaisedPublisher.PublishAsync(_events, _logger, _options.GameProtocol, seat.UserNum,
                    Math.Max(1, settled.Before.Level), after.Level, ct).ConfigureAwait(false);
                return new KeyValuePair<int, RoundReward>?(new(seat.Slot, reward));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // One seat's store failure leaves that seat without a reward row; the others' results still go out.
                _logger.LogError(exception, "Room {Room} seat {Slot} ({User}): match {Match} could not be settled.",
                    room.Id, seat.Slot, seat.UserNum, matchId);
                return null;
            }
        })).ConfigureAwait(false);
        return committed.OfType<KeyValuePair<int, RoundReward>>().ToDictionary(pair => pair.Key, pair => pair.Value);
    }

    // The reward row the scoreboard shows: before the rewards, the experience the seat came in with
    // and no gain; with them, the new experience and what was earned. The badge list stays empty: the
    // recorded badges are UI asset paths whose selection rule is not recovered, so none is invented.
    private RoundRewardRow? RewardRow(int levelBefore, RoundReward? reward, bool withRewards)
    {
        if (reward is null || _levels is null)
            return null;
        return withRewards
            ? new RoundRewardRow(reward.Level, reward.LevelStart, reward.Exp, reward.NextLevelStart,
                reward.ExpEarned, reward.LucciEarned, reward.Level > levelBefore, [])
            : new RoundRewardRow(levelBefore, _levels.Start(levelBefore), reward.Exp - reward.ExpEarned,
                _levels.Start(levelBefore + 1), 0, 0, false, []);
    }
}

/// <summary>What the round needs to run its result: the build's encoders and the reward inputs.</summary>
/// <param name="Senders">The build's result encoders.</param>
/// <param name="Levels">The level table the rewards are computed from; null records no experience.</param>
/// <param name="ReadWallet">Reads a player's balances; null when the deployment publishes no wallet store.</param>
/// <param name="Progress">Records earned experience and Lucci atomically when a progress store is published.</param>
internal sealed record RoundResultSurface(
    RoundResultSenders Senders,
    LevelTable? Levels,
    Func<long, CancellationToken, ValueTask<PlayerWallet?>>? ReadWallet,
    IPlayerProgressRepository? Progress = null);

/// <summary>The encoders a build needs for the typed result sequence; the balance refresh is optional.</summary>
/// <param name="Score">The live score update.</param>
/// <param name="Scoreboard">The final scoreboard, sent once before the rewards and once with them.</param>
/// <param name="Ended">The round's end, with the outcome for each recipient.</param>
/// <param name="Stage">The move to the reward stage.</param>
/// <param name="Wallet">The balance refresh after the return; null on a build whose wallet packet the dialect does not carry.</param>
internal sealed record RoundResultSenders(
    IMessageSender<RoundScoreUpdated> Score,
    IMessageSender<RoundScoreboard> Scoreboard,
    IMessageSender<RoundEnded> Ended,
    IMessageSender<ResultStageAdvanced> Stage,
    IMessageSender<RoundPlayerWalletChanged>? Wallet = null);
