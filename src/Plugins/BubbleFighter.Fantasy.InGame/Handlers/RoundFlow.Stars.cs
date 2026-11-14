using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.InGame.Data;
using BubbleFighter.Fantasy.InGame.Sessions;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.InGame.Handlers;

/// <summary>
/// The star modes: every player for themselves (client mode 5) or by team (mode 6). The clients keep the stars,
/// their physics and the drop rules (kr-812 Config/StarMode.xml); the server places the map's stars, hands out star
/// ids, removes a star for the first player who claims it, adds up each client's reports of its own count and
/// decides the winner when the round ends. A star appears only when the server's creation arrives, so the dropping
/// client gets it too.
/// </summary>
internal sealed partial class RoundFlow
{
    private readonly IMessageSender<RoundStarCreated>? _starCreated;
    private readonly IMessageSender<RoundStarPicked>? _starPicked;
    private readonly IMessageSender<RoundStarFeverStarted>? _starFever;
    private IClientDataStore? _starMapData;

    // The only star kinds the clients build: kr-812 sub_51AB40 makes GoStarItem for 1 and GoBigStarItem for 2.
    private const int SmallStarKind = 1, BigStarKind = 2;

    // The creation's last word picks the star's look: the kr-812 and kr-1256 star constructors give 3 the map star's
    // model and spawn effect, 1, 2, 5, 6 and 7 the dropped star's, and load no model for any other value.
    private const int MapStarTag = 3;

    private bool IsStarMode(PlayRoom room) =>
        _options.StarModeIds.Contains(room.ModeId) || _options.StarTeamModeIds.Contains(room.ModeId);

    internal void ConfigureStarMapFallback(IClientDataStore data) => _starMapData = data;

    // Call under the room's gate.
    private RoundStarState CurrentStars(PlayRoom room)
    {
        if (room.Stars is not { } state || state.Generation != room.StartGeneration)
            room.Stars = state = new RoundStarState(room.StartGeneration, _options.StarObjectIdBase);
        return state;
    }

    internal async ValueTask HandleStarDropAsync(ISession session, RoundStarDropRequested request, CancellationToken ct)
    {
        if (request.Kind is not (SmallStarKind or BigStarKind))
            throw new InvalidOperationException($"Star kind {request.Kind} is not a small or big star.");
        if (FindGameReportRoom(session) is not { } room) return;
        await room.Executor.ExecuteAsync(async operationCt =>
        {
            RoomSeat[] members = [];
            RoundStarCreated? created = null;
            var refused = false;
            lock (room.Gate)
            {
                if (room.Phase != RoomPhase.Playing || !IsStarMode(room) || room.FindByGame(session.Id) is null) return;
                var state = CurrentStars(room);
                if (state.DroppedCount >= _options.MaxDroppedStars)
                {
                    refused = !state.RefusalLogged;
                    state.RefusalLogged = true;
                }
                else
                {
                    var id = checked(++state.LastId);
                    state.Items.Add(id, request.Kind);
                    created = new RoundStarCreated(id, request.Kind, request.Point, request.Tag);
                    members = [.. room.Seats];
                }
            }
            if (refused)
                _logger.LogWarning("Room {Room}: {Limit} dropped stars are unconsumed; further drops this round are ignored.",
                    room.Id, _options.MaxDroppedStars);
            foreach (var member in members)
                await _starCreated!.SendAsync(member.Game, created!, operationCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    // The claim only removes the star: the eater's client reports its new count on its own (kr-812 sub_75CE60
    // sends gcpEatStar and then the score report), and gspStarEaten changes no counter (kr-812 sub_5153D0).
    internal async ValueTask HandleStarPickupAsync(ISession session, RoundStarPickupRequested request, CancellationToken ct)
    {
        if (FindGameReportRoom(session) is not { } room) return;
        await room.Executor.ExecuteAsync(async operationCt =>
        {
            RoomSeat[] members;
            RoundStarPicked picked;
            lock (room.Gate)
            {
                if (room.Phase != RoomPhase.Playing || room.FindByGame(session.Id) is null
                    || room.Stars is not { } state || state.Generation != room.StartGeneration
                    || !state.Items.Remove(request.ObjectId))
                    return;
                state.Release(request.ObjectId);
                picked = new RoundStarPicked(request.CharacterObjectId, request.ObjectId);
                members = [.. room.Seats];
            }
            foreach (var member in members)
                await _starPicked!.SendAsync(member.Game, picked, operationCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Takes the map star points the host's map report lists. Only the room master's report counts, once per round,
    /// and only while no points were read from the map file instead.
    /// </summary>
    internal async ValueTask HandleStarPointsReportedAsync(ISession session, RoundStarPointsReported report, CancellationToken ct)
    {
        if (FindGameReportRoom(session) is not { } room) return;
        await room.Executor.ExecuteAsync(operationCt =>
        {
            lock (room.Gate)
            {
                if (room.Phase is not (RoomPhase.Starting or RoomPhase.Playing) || !IsStarMode(room)
                    || room.FindByGame(session.Id) is not { } reporter || reporter.Slot != room.MasterSlot)
                    return ValueTask.CompletedTask;
                var state = CurrentStars(room);
                if (state.MapReported || state.MapPoints.Length > 0)
                    return ValueTask.CompletedTask;
                if (report.Points.Count > _options.MaxSupplyPoints)
                    throw new InvalidOperationException("Map star point count exceeds the configured bound.");
                foreach (var point in report.Points)
                    if (point.Kind is not (SmallStarKind or BigStarKind) || point.FirstMs < 0 || point.PeriodMs < 0
                        || !float.IsFinite(point.X) || !float.IsFinite(point.Y) || !float.IsFinite(point.Z))
                        throw new InvalidOperationException("Map star point is invalid.");
                state.SetMapPoints(report.Points, reported: true);
            }
            StartMapStars(room);
            return ValueTask.CompletedTask;
        }, ct).ConfigureAwait(false);
    }

    // Called on the room executor when play begins and when the host's points arrive. A point first gets its star
    // FirstMs after play began, then checks every PeriodMs and replaces a claimed star (a point whose star is still
    // there keeps it). A round whose host listed no points reads the map file's star helpers instead.
    private void StartMapStars(PlayRoom room)
    {
        if (_starCreated is null || !IsStarMode(room)) return;
        string? stage = null;
        lock (room.Gate)
        {
            if (room.Phase != RoomPhase.Playing) return;
            var state = CurrentStars(room);
            if (state.PlayStartedAt is null) state.PlayStartedAt = Environment.TickCount64;
            if (state.MapPoints.Length == 0 && !state.FallbackRead && _starMapData is not null)
            {
                state.FallbackRead = true;
                stage = string.IsNullOrEmpty(room.RoundStageKey) ? room.RoundMapKey : room.RoundStageKey;
            }
        }
        var fallback = stage is null ? null : ReadMapStarFallback(room, stage);
        lock (room.Gate)
        {
            if (room.Phase != RoomPhase.Playing || room.Stars is not { } state || state.Generation != room.StartGeneration
                || state.Running)
                return;
            if (fallback is { Count: > 0 } && state.MapPoints.Length == 0)
                state.SetMapPoints(fallback, reported: false);
            if (state.MapPoints.Length == 0) return;
            state.Running = true;
            foreach (var point in state.MapPoints)
                point.NextAt = state.PlayStartedAt!.Value + point.Point.FirstMs;
            state.Worker = BackgroundWork.Detached(() => RunMapStarsAsync(room, state));
        }
    }

    private IReadOnlyList<RoundStarPoint>? ReadMapStarFallback(PlayRoom room, string stage)
    {
        try
        {
            var points = StarMapPoints.Load(_starMapData!, stage, _options.ItemModeFallbackMaximumFileBytes,
                _options.ItemModeFallbackMaximumCandidates, _options.MaxSupplyPoints);
            _logger.LogInformation("Room {Room}: the host reported no star points; {Count} come from {Stage}/track.wb.",
                room.Id, points.Count, stage);
            return points;
        }
        catch (Exception failure) when (failure is InvalidDataException or IOException)
        {
            _logger.LogWarning(failure, "Room {Room}: the host reported no star points and {Stage}/track.wb gave none.",
                room.Id, stage);
            return null;
        }
    }

    private async Task RunMapStarsAsync(PlayRoom room, RoundStarState state)
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                await Task.Delay(_options.SupplyPollMs, _lifetime).ConfigureAwait(false);
                var keepRunning = false;
                await room.Executor.ExecuteAsync(async operationCt =>
                {
                    RoomSeat[] members;
                    var created = new List<RoundStarCreated>();
                    lock (room.Gate)
                    {
                        if (room.Phase != RoomPhase.Playing || room.RoundCancelled || room.IsStopping
                            || room.StartGeneration != state.Generation || !ReferenceEquals(room.Stars, state)) return;
                        keepRunning = true;
                        members = [.. room.Seats];
                        var now = Environment.TickCount64;
                        foreach (var point in state.MapPoints)
                        {
                            if (now < point.NextAt) continue;
                            var period = point.Point.PeriodMs;
                            point.NextAt = period > 0 ? point.NextAt + period * (1 + (now - point.NextAt) / period) : long.MaxValue;
                            if (point.ActiveId != 0) continue;
                            point.ActiveId = checked(++state.LastId);
                            state.Items.Add(point.ActiveId, point.Point.Kind);
                            created.Add(new RoundStarCreated(point.ActiveId, point.Point.Kind,
                                new RoundLucciPoint(point.Point.X, point.Point.Y, point.Point.Z, 0, 0, 0), MapStarTag));
                        }
                    }
                    foreach (var star in created)
                        foreach (var member in members)
                            await _starCreated!.SendAsync(member.Game, star, operationCt).ConfigureAwait(false);
                }, _lifetime).ConfigureAwait(false);
                if (!keepRunning) return;
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (ObjectDisposedException) { }
        catch (Exception failure)
        {
            _logger.LogError(failure, "Room {Room}: map star refresh failed.", room.Id);
        }
    }

    // Called on the room executor right after the round started, as the time limit's own countdown is.
    private void StartStarFever(PlayRoom room, int timeLimit)
    {
        var lead = _options.StarFeverLeadMs;
        if (_starFever is null || lead <= 0 || timeLimit <= lead || !IsStarMode(room)) return;
        lock (room.Gate)
        {
            if (room.Phase != RoomPhase.Playing) return;
            var state = CurrentStars(room);
            if (state.Fever is not null) return;
            state.Fever = BackgroundWork.Detached(() => RunStarFeverAsync(room, state, timeLimit - lead));
        }
    }

    private async Task RunStarFeverAsync(PlayRoom room, RoundStarState state, int delayMs)
    {
        try
        {
            await Task.Delay(delayMs, _lifetime).ConfigureAwait(false);
            await room.Executor.ExecuteAsync(async operationCt =>
            {
                RoomSeat[] members;
                lock (room.Gate)
                {
                    if (room.Phase != RoomPhase.Playing || room.RoundCancelled || room.IsStopping
                        || room.StartGeneration != state.Generation || !ReferenceEquals(room.Stars, state)) return;
                    members = [.. room.Seats];
                }
                var fever = new RoundStarFeverStarted();
                foreach (var member in members)
                    await _starFever!.SendAsync(member.Game, fever, operationCt).ConfigureAwait(false);
            }, _lifetime).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (ObjectDisposedException) { }
        catch (Exception failure)
        {
            _logger.LogError(failure, "Room {Room}: the star fever failed.", room.Id);
        }
    }

    /// <summary>
    /// Adds one client report of its own star count. Every client reports its starting stars while the map
    /// loads (kr-812 sub_661AF0), so reports count from the start of the round; the live count is shown once
    /// play begins. An audited named seat must match the authenticated sender; other layouts retain sender attribution.
    /// </summary>
    internal async ValueTask HandleStarReportedAsync(ISession session, RoundStarReported report, CancellationToken ct)
    {
        if (_registry.TryGet(session.Id) is not { } room)
            return;
        await room.Executor.ExecuteAsync(async operationCt =>
        {
            RoundScoreUpdated? update = null;
            lock (room.Gate)
            {
                if (room.Phase is not (RoomPhase.Starting or RoomPhase.Playing) || !IsStarMode(room)
                    || room.FindByGame(session.Id) is not { } seat || !ReferenceEquals(seat.Game, session)
                    || !ReferenceEquals(_registry.TryGet(session.Id), room))
                    return;
                // kr-1256 sub_9E5210 emits losses for the departing seat, which must not debit its surviving observer.
                if (report.NamedReporterSlot is { } namedSlot && namedSlot != seat.Slot)
                    return;
                var change = report.Change switch
                {
                    RoundStarChange.SmallGained => 1,
                    RoundStarChange.BigGained => _options.BigStarValue,
                    RoundStarChange.SmallLost => -1,
                    RoundStarChange.BigLost => -_options.BigStarValue,
                    _ => 0,
                };
                if (room.Scoring.AddStars(seat.Slot, change, _options.MaxRoundStars) is not { } stats)
                    return;
                if (room.Phase == RoomPhase.Playing)
                    update = new RoundScoreUpdated(room.Id, seat.Team, LiveTeamTotals(room), [stats], LiveScoreboard(room));
            }
            if (update is not null)
                await SendScoreAsync(room, update, operationCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    // The starting stars were reported while the map loaded, before the live score could be shown.
    private async ValueTask PushStarCountsAsync(PlayRoom room, CancellationToken ct)
    {
        RoundScoreUpdated[] updates;
        lock (room.Gate)
        {
            if (room.Phase != RoomPhase.Playing || !IsStarMode(room) || !room.Scoring.CountsStars)
                return;
            var totals = LiveTeamTotals(room);
            updates = [.. room.Seats.Select(seat => new RoundScoreUpdated(room.Id, seat.Team,
                totals, [room.Scoring.Stats(seat.Slot)], LiveScoreboard(room)))];
        }
        foreach (var update in updates)
            await SendScoreAsync(room, update, ct).ConfigureAwait(false);
    }

    // Call under the room's gate. A star round is decided on the stars its seats hold, and the clients read the
    // result's winner off the team totals they are sent, so a star round's live score and result carry each team's
    // stars as its total; every other round carries its combat totals.
    private int[] LiveTeamTotals(PlayRoom room)
    {
        if (!IsStarMode(room) || !room.Scoring.CountsStars)
            return [.. room.Scoring.TeamTotals];
        var totals = new int[2];
        foreach (var seat in room.Seats)
            if (seat.Team is 1 or 2)
                totals[seat.Team - 1] = checked(totals[seat.Team - 1] + room.Scoring.Stars(seat.Slot));
        return totals;
    }

    /// <summary>
    /// Decides a star round under the room's gate, once: by seat in the individual mode (the seats holding the
    /// most stars win; a seat left alone wins, and when two or more seats all hold the same it is a draw) and by
    /// team in the team mode (the team whose seats hold more stars wins; equal totals are a draw).
    /// </summary>
    private void DecideStarRound(PlayRoom room)
    {
        if (room.DecidedWinnerTeam is not null || room.DecidedWinnerSlots is not null)
            return;
        if (_options.StarModeIds.Contains(room.ModeId))
        {
            var counts = room.Seats.Select(seat => (seat.Slot, Stars: room.Scoring.Stars(seat.Slot))).ToArray();
            var most = counts.Length == 0 ? 0 : counts.Max(seat => seat.Stars);
            room.DecidedWinnerSlots = counts.Length > 1 && counts.All(seat => seat.Stars == most)
                ? new HashSet<int>()
                : counts.Where(seat => seat.Stars == most).Select(seat => seat.Slot).ToHashSet();
        }
        else if (_options.StarTeamModeIds.Contains(room.ModeId))
        {
            var totals = LiveTeamTotals(room);
            room.DecidedWinnerTeam = totals[0] > totals[1] ? 1 : totals[1] > totals[0] ? 2 : 0;
        }
    }
}

/// <summary>One round's stars: the unclaimed ones by object id and the map's star points.</summary>
internal sealed class RoundStarState(int generation, int objectIdBase)
{
    /// <summary>Gets the start generation; a state from an earlier round is discarded.</summary>
    internal int Generation { get; } = generation;

    /// <summary>Gets or sets the last object id handed out.</summary>
    internal int LastId { get; set; } = objectIdBase;

    /// <summary>Gets the unclaimed stars, dropped and map ones, by object id with their kind.</summary>
    internal Dictionary<int, int> Items { get; } = [];

    /// <summary>Gets how many unclaimed stars players dropped.</summary>
    internal int DroppedCount => Items.Count - MapPoints.Count(point => point.ActiveId != 0);

    /// <summary>Gets or sets whether the dropped-star bound was already reported this round.</summary>
    internal bool RefusalLogged { get; set; }

    /// <summary>Gets the map's star points.</summary>
    internal MapStarPoint[] MapPoints { get; private set; } = [];

    /// <summary>Gets whether the points came from the host's map report.</summary>
    internal bool MapReported { get; private set; }

    /// <summary>Gets or sets whether the map file was already read for points this round.</summary>
    internal bool FallbackRead { get; set; }

    /// <summary>Gets or sets when play began, in <see cref="Environment.TickCount64"/> milliseconds.</summary>
    internal long? PlayStartedAt { get; set; }

    /// <summary>Gets or sets whether the map star worker runs.</summary>
    internal bool Running { get; set; }

    /// <summary>Gets or sets the map star worker.</summary>
    internal Task Worker { get; set; } = Task.CompletedTask;

    /// <summary>Gets or sets the closing fever's timer, or null when none was started this round.</summary>
    internal Task? Fever { get; set; }

    /// <summary>Replaces the map's star points.</summary>
    internal void SetMapPoints(IReadOnlyList<RoundStarPoint> points, bool reported)
    {
        MapPoints = [.. points.Select(point => new MapStarPoint(point))];
        MapReported = reported;
    }

    /// <summary>Frees the map point whose star was claimed, so its next check puts a new one there.</summary>
    internal void Release(int objectId)
    {
        foreach (var point in MapPoints)
            if (point.ActiveId == objectId)
                point.ActiveId = 0;
    }
}

/// <summary>One map star point and the star it holds.</summary>
internal sealed class MapStarPoint(RoundStarPoint point)
{
    /// <summary>Gets the point.</summary>
    internal RoundStarPoint Point { get; } = point;

    /// <summary>Gets or sets when the point is next checked, in <see cref="Environment.TickCount64"/> milliseconds.</summary>
    internal long NextAt { get; set; }

    /// <summary>Gets or sets the object id of the point's unclaimed star, or zero.</summary>
    internal int ActiveId { get; set; }
}
