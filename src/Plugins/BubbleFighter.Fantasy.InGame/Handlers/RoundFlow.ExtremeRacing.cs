using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.InGame.Sessions;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.InGame.Handlers;

/// <summary>The build's ExtremeRacing encoders; all three come from the same mounted exchange.</summary>
/// <param name="Timer">Starts and stops the race clocks.</param>
/// <param name="Round">Moves the stage to the next race round.</param>
/// <param name="Records">Updates the race boards.</param>
internal sealed record ExtremeRacingSenders(
    IMessageSender<RoundExtremeRacingTimerChanged> Timer,
    IMessageSender<RoundExtremeRacingRoundChanged> Round,
    IMessageSender<RoundExtremeRacingRecordsUpdated> Records);

internal sealed partial class RoundFlow
{
    private ExtremeRacingSenders? _extremeRacing;

    /// <summary>
    /// Gets or sets the build's ExtremeRacing encoders; null leaves mode 24 an ordinary round. The client lays out its
    /// own track, markers and respawn points (kr-1256 sub_D136A0, kr-1256 sub_515790); the round decides which race round runs,
    /// when its clocks start and stop, and how its finishers score.
    /// </summary>
    internal ExtremeRacingSenders? ExtremeRacing
    {
        get => _extremeRacing;
        set
        {
            if (value is not null)
                ValidateExtremeRacingOptions(_options);
            _extremeRacing = value;
        }
    }

    private bool IsExtremeRacingMode(PlayRoom room) =>
        _extremeRacing is not null && room.Tutorial is null && room.ModeId == (int)GameModeId.ExtremeRacing;

    private static void ValidateExtremeRacingOptions(InGameOptions options)
    {
        if (options.ExtremeRacingRounds is < 1 or > 5)
            throw new ConfigException("extreme_racing_rounds must be between 1 and 5, the rounds the client accepts.");
        if (options.ExtremeRacingRoundMs <= 0 || options.ExtremeRacingFinishCountdownMs <= 0)
            throw new ConfigException("extreme_racing_round_ms and extreme_racing_finish_countdown_ms must be positive.");
        if (options.ExtremeRacingIntermissionMs <= 1000)
            throw new ConfigException("extreme_racing_intermission_ms must exceed 1000, the client's time-over lead.");
        if (options.ExtremeRacingPlacePoints is not { Length: > 0 and <= 16 } points
            || points.Any(value => value < 0 || value > int.MaxValue / options.ExtremeRacingRounds))
            throw new ConfigException("extreme_racing_place_points must list 1 to 16 non-negative values whose match totals fit a signed word.");
    }

    // Called on the executor once the round plays: the stage already starts on round 1 (kr-1256 sub_CE54F0), so only
    // the race clock is needed to let markers and goals count.
    private async ValueTask StartExtremeRacingAsync(PlayRoom room, CancellationToken ct)
    {
        if (!IsExtremeRacingMode(room)) return;
        var previous = room.ExtremeRacingClock;
        if (previous is not null)
        {
            previous.Cancel();
            await previous.Completion.ConfigureAwait(false);
        }
        lock (room.Gate)
        {
            if (room.IsStopping || room.RoundCancelled || room.Phase != RoomPhase.Playing) return;
            room.ExtremeRacingClock = new(room.Executor, _lifetime, failure =>
                _logger.LogError(failure, "Room {Room}: a scheduled race step failed.", room.Id));
        }
        await BeginExtremeRacingRoundAsync(room, 1, ct).ConfigureAwait(false);
    }

    private async ValueTask BeginExtremeRacingRoundAsync(PlayRoom room, int round, CancellationToken ct)
    {
        RoomSeat[] members;
        int generation;
        lock (room.Gate)
        {
            if (room.Phase != RoomPhase.Playing || room.RoundCancelled || room.IsStopping || !room.ExtremeRacing.Begin(round)) return;
            generation = room.StartGeneration;
            members = [.. room.Seats];
            if (round > 1)
                foreach (var seat in members)
                    room.ExtremeRacing.ArmRoundReset(seat.Slot, seat.Life.Generation);
        }
        if (round > 1)
            await SendExtremeRacingAsync(members, member => _extremeRacing!.Round.SendAsync(member, new(round), ct)).ConfigureAwait(false);
        var clock = _options.ExtremeRacingRoundMs;
        await SendExtremeRacingAsync(members, member => _extremeRacing!.Timer.SendAsync(member,
            new(RoundExtremeRacingTimer.RaceRunning, clock, true), ct)).ConfigureAwait(false);
        _logger.LogInformation("Room {Room}: race round {Round} started for {Count} member(s).", room.Id, round, members.Length);
        ScheduleExtremeRacing(room, generation, clock, operationCt => CloseExtremeRacingRoundAsync(room, generation, round, operationCt));
    }

    private sealed record ExtremeRacingResetStamp(int Generation, ExtremeRacingRoundState Race,
        int Round, RoomSeat Seat, long Life);

    private ExtremeRacingResetStamp? CaptureExtremeRacingReset(PlayRoom room, ISession session, RoundSpawnAccepted request)
    {
        lock (room.Gate)
        {
            // kr-1256 sub_54BE50 case 401 and kr-1256 sub_67E8B0 both call the same reset sender and latch it.
            if (!IsExtremeRacingMode(room) || room.Phase != RoomPhase.Playing || room.RoundCancelled || room.IsStopping
                || room.FindByGame(session.Id) is not { } seat || !ReferenceEquals(seat.Game, session)
                || request.Actor != RoomRegistry.SceneEntity(seat.Slot) || session.Connection.IsClosed
                || !CanResetInRace(room, seat)) return null;
            return new(room.StartGeneration, room.ExtremeRacing, room.ExtremeRacing.Round, seat, seat.Life.Generation);
        }
    }

    private static bool ExtremeRacingResetIsCurrent(PlayRoom room, RoomSeat seat, ExtremeRacingResetStamp? stamp) =>
        stamp is not null && room.Phase == RoomPhase.Playing && !room.RoundCancelled && !room.IsStopping
        && room.StartGeneration == stamp.Generation && ReferenceEquals(room.ExtremeRacing, stamp.Race)
        && room.ExtremeRacing.Round == stamp.Round && ReferenceEquals(seat, stamp.Seat)
        && seat.Life.Generation == stamp.Life && CanResetInRace(room, seat);

    private static bool CanResetInRace(PlayRoom room, RoomSeat seat) => room.ExtremeRacing.IsActive
        && seat.Life.State != SeatLife.ResetPending
        && (seat.Life.State == SeatLife.Alive || room.ExtremeRacing.HasRoundReset(seat.Slot, seat.Life.Generation));

    /// <summary>
    /// Counts a player's goal of the current race round once. The first finisher starts the finish countdown; the round
    /// closes when it runs out, when the race clock runs out or when every connected player has finished.
    /// </summary>
    internal async ValueTask HandleExtremeRacingGoalAsync(ISession session, RoundExtremeRacingGoalReported report, CancellationToken ct)
    {
        if (FindGameReportRoom(session) is not { } room || !IsExtremeRacingMode(room)) return;
        int generation;
        RoomSeat reporter;
        lock (room.Gate)
        {
            if (room.Phase != RoomPhase.Playing || room.RoundCancelled || room.IsStopping
                || room.FindByGame(session.Id) is not { } seat || !ReferenceEquals(seat.Game, session)) return;
            generation = room.StartGeneration;
            reporter = seat;
        }
        await room.Executor.ExecuteAsync(async operationCt =>
        {
            RoomSeat[] members;
            ExtremeRacingFinish finish;
            bool everyone;
            int place;
            lock (room.Gate)
            {
                if (room.Phase != RoomPhase.Playing || room.RoundCancelled || room.IsStopping || room.StartGeneration != generation
                    || !ReferenceEquals(room.FindByGame(session.Id), reporter))
                    return;
                finish = room.ExtremeRacing.Finish(reporter.Slot, report.Round);
                if (finish == ExtremeRacingFinish.Rejected)
                {
                    _logger.LogWarning("Session {Id} goal of race round {Round} rejected in room {Room}: current round {Current}, phase {Phase}.",
                        session.Id, report.Round, room.Id, room.ExtremeRacing.Round, room.ExtremeRacing.Phase);
                    return;
                }
                everyone = EveryoneFinished(room);
                place = room.ExtremeRacing.Finishers.Count;
                members = [.. room.Seats];
            }
            _logger.LogInformation("Room {Room}: seat {Seat} finished race round {Round} in place {Place}.",
                room.Id, reporter.Slot, report.Round, place);
            if (everyone)
            {
                await CloseExtremeRacingRoundAsync(room, generation, report.Round, operationCt).ConfigureAwait(false);
                return;
            }
            if (finish != ExtremeRacingFinish.First) return;
            var countdown = (int)Math.Clamp(_options.ExtremeRacingRoundMs - (Environment.TickCount64 - room.ExtremeRacing.StartedAt),
                1L, _options.ExtremeRacingFinishCountdownMs);
            await SendExtremeRacingAsync(members, member => _extremeRacing!.Timer.SendAsync(member,
                new(RoundExtremeRacingTimer.FinishCountdown, countdown, true), operationCt)).ConfigureAwait(false);
            ScheduleExtremeRacing(room, generation, countdown, closeCt => CloseExtremeRacingRoundAsync(room, generation, report.Round, closeCt));
        }, ct).ConfigureAwait(false);
    }

    private async ValueTask HandleExtremeRacingCheckpointAsync(PlayRoom room, ISession session, RoundFeedSent feed, CancellationToken ct)
    {
        if (!IsExtremeRacingMode(room) || feed.ExtremeRacingCheckpoint is not { } checkpoint) return;
        RoomSeat reporter;
        int generation;
        lock (room.Gate)
        {
            if (room.Phase != RoomPhase.Playing || room.RoundCancelled || room.IsStopping
                || room.Seats.FirstOrDefault(seat => ReferenceEquals(seat.Game, session)
                    || ReferenceEquals(seat.Relay, session)) is not { } seat) return;
            reporter = seat;
            generation = room.StartGeneration;
        }
        await room.Executor.ExecuteAsync(async operationCt =>
        {
            lock (room.Gate)
            {
                if (room.Phase != RoomPhase.Playing || room.RoundCancelled || room.IsStopping
                    || room.StartGeneration != generation || !room.Seats.Contains(reporter)
                    || !ReferenceEquals(reporter.Game, session) && !ReferenceEquals(reporter.Relay, session)) return;
                if (!room.ControlsActor(reporter, checkpoint.Actor)
                    || !room.ExtremeRacing.Checkpoint(reporter.Slot, checkpoint.Round, checkpoint.Marker))
                {
                    _logger.LogWarning("Room {Room} session {Session}: rejected race checkpoint {Round}/{Marker} for actor {Actor}.",
                        room.Id, session.Id, checkpoint.Round, checkpoint.Marker, checkpoint.Actor);
                    return;
                }
            }
            await ForwardFeedAsync(room, session, feed, operationCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    // Runs on the executor after a departure: the remaining players must not wait for a goal that can no longer come.
    private async ValueTask OnExtremeRacingSeatLeftAsync(PlayRoom room, CancellationToken ct)
    {
        if (!IsExtremeRacingMode(room)) return;
        int generation, round;
        lock (room.Gate)
        {
            if (room.Phase != RoomPhase.Playing || room.RoundCancelled || room.ExtremeRacing.Phase != ExtremeRacingPhase.Finishing
                || !EveryoneFinished(room)) return;
            generation = room.StartGeneration;
            round = room.ExtremeRacing.Round;
        }
        await CloseExtremeRacingRoundAsync(room, generation, round, ct).ConfigureAwait(false);
    }

    private static bool EveryoneFinished(PlayRoom room) =>
        room.Seats.Where(seat => !seat.Game.Connection.IsClosed).All(seat => room.ExtremeRacing.HasFinished(seat.Slot));

    // Closes one race round once: stops the countdown, shows the time-over pause and the boards, then begins the next
    // round or, after the last, ends the match through the shared result once the pause has run.
    private async ValueTask CloseExtremeRacingRoundAsync(PlayRoom room, int generation, int round, CancellationToken ct)
    {
        RoomSeat[] members;
        IReadOnlyDictionary<int, int> awarded;
        bool countdown, last;
        RoundExtremeRacingRecordsUpdated records;
        lock (room.Gate)
        {
            if (room.Phase != RoomPhase.Playing || room.RoundCancelled || room.IsStopping || room.StartGeneration != generation) return;
            countdown = room.ExtremeRacing.Phase == ExtremeRacingPhase.Finishing;
            if (room.ExtremeRacing.Close(round, _options.ExtremeRacingPlacePoints, _options.ExtremeRacingRounds) is not { } points)
                return;
            awarded = points;
            last = room.ExtremeRacing.Phase == ExtremeRacingPhase.Over;
            members = [.. room.Seats];
            records = new([.. members.Select(seat => new RoundExtremeRacingRecord(seat.Slot,
            [
                new(RoundExtremeRacingRecordField.RoundFirst, awarded.GetValueOrDefault(seat.Slot)),
                new(RoundExtremeRacingRecordField.TotalFirst, room.ExtremeRacing.TotalOf(seat.Slot)),
            ]))]);
            if (last) DecideExtremeRacingMatch(room);
        }
        if (countdown)
            await SendExtremeRacingAsync(members, member => _extremeRacing!.Timer.SendAsync(member,
                new(RoundExtremeRacingTimer.FinishCountdown, 0, false), ct)).ConfigureAwait(false);
        var pause = _options.ExtremeRacingIntermissionMs;
        await SendExtremeRacingAsync(members, member => _extremeRacing!.Timer.SendAsync(member,
            new(RoundExtremeRacingTimer.RoundOver, pause, true), ct)).ConfigureAwait(false);
        await SendExtremeRacingAsync(members, member => _extremeRacing!.Records.SendAsync(member, records, ct)).ConfigureAwait(false);
        _logger.LogInformation("Room {Room}: race round {Round} closed with {Count} finisher(s).", room.Id, round, awarded.Count);
        ScheduleExtremeRacing(room, generation, pause, last
            ? operationCt => EndRoundAsync(room, RoundEndReason.WinCondition, operationCt)
            : operationCt => BeginExtremeRacingRoundAsync(room, round + 1, operationCt));
    }

    // Two teams that both have seats compare their summed points; otherwise the seats with the most points win.
    // Equal results are a draw, as the shared result reads them.
    private static void DecideExtremeRacingMatch(PlayRoom room)
    {
        var totals = room.Seats.Select(seat => (seat.Slot, seat.Team, Points: room.ExtremeRacing.TotalOf(seat.Slot))).ToArray();
        if (totals.Any(seat => seat.Team == 1) && totals.Any(seat => seat.Team == 2)
            && totals.All(seat => seat.Team is 1 or 2))
        {
            var first = totals.Where(seat => seat.Team == 1).Sum(seat => (long)seat.Points);
            var second = totals.Where(seat => seat.Team == 2).Sum(seat => (long)seat.Points);
            room.DecidedWinnerTeam = first > second ? 1 : second > first ? 2 : 0;
            return;
        }
        var most = totals.Length == 0 ? 0 : totals.Max(seat => seat.Points);
        room.DecidedWinnerSlots = totals.All(seat => seat.Points == most)
            ? new HashSet<int>()
            : totals.Where(seat => seat.Points == most).Select(seat => seat.Slot).ToHashSet();
    }

    private static async ValueTask SendExtremeRacingAsync(RoomSeat[] members, Func<ISession, ValueTask> send)
    {
        foreach (var member in members)
            if (!member.Game.Connection.IsClosed)
                await send(member.Game).ConfigureAwait(false);
    }

    // Replacing the deadline cancels the old wait; the queued callback rechecks the match before touching its state.
    private void ScheduleExtremeRacing(PlayRoom room, int generation, int delayMs, Func<CancellationToken, ValueTask> step) =>
        room.ExtremeRacingClock!.Schedule(delayMs, operationCt =>
        {
            lock (room.Gate)
            {
                if (room.StartGeneration != generation || room.Phase != RoomPhase.Playing || room.RoundCancelled || room.IsStopping)
                    return ValueTask.CompletedTask;
            }
            return step(operationCt);
        });
}
