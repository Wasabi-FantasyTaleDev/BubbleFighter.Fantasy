using System.Text.Json;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.InGame.Sessions;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.InGame.Handlers;

/// <summary>
/// The star modes: every player for themselves (client mode 5) or by team (mode 6). The clients keep the stars,
/// their physics and the drop rules (kr-812 Config/StarMode.xml); the server hands out star ids, removes a star
/// for the first player who claims it, adds up each client's reports of its own count and decides the winner
/// when the round ends. A star appears only when the server's creation arrives, so the dropping client gets it too.
/// </summary>
internal sealed partial class RoundFlow
{
    private readonly IMessageSender<RoundStarCreated>? _starCreated;
    private readonly IMessageSender<RoundStarPicked>? _starPicked;
    private readonly IMessageSender<RoundStarFeverStarted>? _starFever;

    // The only star kinds the clients build: kr-812 sub_51AB40 makes GoStarItem for 1 and GoBigStarItem for 2.
    private const int SmallStarKind = 1, BigStarKind = 2;

    private bool IsStarMode(PlayRoom room) =>
        _options.StarModeIds.Contains(room.ModeId) || _options.StarTeamModeIds.Contains(room.ModeId);

    internal async ValueTask HandleStarDropAsync(ISession session, RoundStarDropRequested request, CancellationToken ct)
    {
        if (request.Kind is not (SmallStarKind or BigStarKind))
            throw new InvalidOperationException($"Star kind {request.Kind} is not a small or big star.");
        var (room, _) = _registry.RequireGame(session);
        await room.Executor.ExecuteAsync(async operationCt =>
        {
            RoomSeat[] members = [];
            RoundStarCreated? created = null;
            var refused = false;
            lock (room.Gate)
            {
                if (room.Phase != RoomPhase.Playing || !IsStarMode(room) || room.FindByGame(session.Id) is null) return;
                var state = room.Stars;
                if (state is null || state.Generation != room.StartGeneration)
                    room.Stars = state = new RoundStarState(room.StartGeneration, _options.StarObjectIdBase);
                if (state.Items.Count >= _options.MaxDroppedStars)
                {
                    refused = !state.RefusalLogged;
                    state.RefusalLogged = true;
                }
                else
                {
                    var id = checked(++state.LastId);
                    state.Items.Add(id, request.Kind);
                    // A map-generated first wave is already frozen. Otherwise client requests form it until T-45.
                    if (!state.FirstWaveFrozen)
                        state.FirstWave.Add(new RoundStarSeed(request.Kind, request.Point, request.Tag));
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
        var (room, _) = _registry.RequireGame(session);
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
                picked = new RoundStarPicked(request.CharacterObjectId, request.ObjectId);
                members = [.. room.Seats];
            }
            foreach (var member in members)
                await _starPicked!.SendAsync(member.Game, picked, operationCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Adds one client report of its own star count. Every client reports its starting stars while the map
    /// loads (kr-812 sub_661AF0), so reports count from the start of the round; the live count is shown once
    /// play begins. The authenticated seat is counted, whatever seat the report names.
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
                    || room.FindByGame(session.Id) is not { } seat)
                    return;
                var change = report.Change switch
                {
                    RoundStarChange.SmallGained => 1,
                    RoundStarChange.BigGained => _options.BigStarValue,
                    RoundStarChange.SmallLost => -1,
                    RoundStarChange.BigLost => -_options.BigStarValue,
                    _ => 0,
                };
                if (room.Scoring.AddStars(seat.Slot, change, _options.MaxRoundStars, seat.Team,
                    trackTeamTotal: _options.StarTeamModeIds.Contains(room.ModeId)) is not { } stats)
                    return;
                if (room.Phase == RoomPhase.Playing)
                    update = new RoundScoreUpdated(room.Id, seat.Team, [.. room.Scoring.StarTeamTotals], [stats], LiveScoreboard(room));
            }
            if (update is not null)
                await SendScoreAsync(room, update, operationCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    // The starting stars were reported while the map loaded, before the live score could be shown.
    /// <summary>Creates the map's first wave and freezes its exact seeds for the T-45 copy.</summary>
    internal async ValueTask SpawnInitialStarWaveAsync(PlayRoom room, int generation, CancellationToken ct)
    {
        IReadOnlyList<RoundStarSeed>? seeds;
        try
        {
            var map = room.RoundMapKey.Length > 0 ? room.RoundMapKey : room.MapKey;
            seeds = Data.StarDropPointsCatalog.Load(_options.StarDropPointsDirectory, map);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            var map = room.RoundMapKey.Length > 0 ? room.RoundMapKey : room.MapKey;
            _logger.LogError(failure, "Room {Room}: cannot read star drop points for map {Map}.", room.Id, map);
            return;
        }
        if (seeds is null)
        {
            if (_options.StarDropPointsDirectory.Length > 0)
            {
                var map = room.RoundMapKey.Length > 0 ? room.RoundMapKey : room.MapKey;
                _logger.LogWarning("Room {Room}: no star drop points for map {Map}; the first wave stays client-driven.", room.Id, map);
            }
            return;
        }

        RoomSeat[] members;
        List<RoundStarCreated> created = [];
        lock (room.Gate)
        {
            if (room.Phase != RoomPhase.Playing || room.StartGeneration != generation || !IsStarMode(room))
                return;
            if (room.Stars is { } existing && existing.Generation == generation && existing.FirstWaveFrozen)
                return;
            var state = room.Stars;
            if (state is null || state.Generation != generation)
                room.Stars = state = new RoundStarState(generation, _options.StarObjectIdBase);
            foreach (var seed in seeds)
            {
                if (state.Items.Count >= _options.MaxDroppedStars)
                {
                    _logger.LogWarning("Room {Room}: first star wave stopped at the {Limit}-star active-object bound.",
                        room.Id, _options.MaxDroppedStars);
                    break;
                }
                var id = checked(++state.LastId);
                state.Items.Add(id, seed.Kind);
                state.FirstWave.Add(seed);
                created.Add(new RoundStarCreated(id, seed.Kind, seed.Point, seed.Tag));
            }
            state.FirstWaveFrozen = true;
            members = [.. room.Seats];
        }
        foreach (var star in created)
            foreach (var member in members)
                await _starCreated!.SendAsync(member.Game, star, ct).ConfigureAwait(false);
        var createdMap = room.RoundMapKey.Length > 0 ? room.RoundMapKey : room.MapKey;
        _logger.LogInformation("Room {Room}: first star wave created {Count} item(s) from map {Map} ({Small} small, {Big} big).",
            room.Id, created.Count, createdMap, created.Count(star => star.Kind == SmallStarKind),
            created.Count(star => star.Kind == BigStarKind));
    }

    private async ValueTask PushStarCountsAsync(PlayRoom room, CancellationToken ct)
    {
        RoundScoreUpdated[] updates;
        lock (room.Gate)
        {
            if (room.Phase != RoomPhase.Playing || !IsStarMode(room) || !room.Scoring.CountsStars)
                return;
            updates = [.. room.Seats.Select(seat => new RoundScoreUpdated(room.Id, seat.Team,
                [.. room.Scoring.StarTeamTotals], [room.Scoring.Stats(seat.Slot)], LiveScoreboard(room)))];
        }
        foreach (var update in updates)
            await SendScoreAsync(room, update, ct).ConfigureAwait(false);
    }

    // kr-1256 sub_6ECD60 has the empty S2C 634 body; in modes 5/6 it plays star-spawn-big and m-star-1m.
    private void ScheduleStarFever(PlayRoom room, int generation, int timeLimitMs)
    {
        if (_starFever is null || timeLimitMs <= 0)
            return;
        var leadMs = room.RoundRules is { FeverTimeDuration: > 0 } rules ? rules.FeverTimeDuration : _options.StarFeverLeadMs;
        var delayMs = Math.Max(0, timeLimitMs - leadMs);
        var source = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
        lock (room.Gate)
        {
            if (room.Phase != RoomPhase.Playing || room.StartGeneration != generation)
            {
                source.Dispose();
                return;
            }
            room.CancelStarFever();
            room.StarFeverCancellation = source;
            room.StarFeverTask = BackgroundWork.Detached(() => RunStarFeverAsync(room, generation, delayMs, source));
        }
    }

    private async Task RunStarFeverAsync(PlayRoom room, int generation, int delayMs, CancellationTokenSource source)
    {
        var token = source.Token;
        try
        {
            await Task.Delay(delayMs, token).ConfigureAwait(false);
            await room.Executor.ExecuteAsync(async operationCt =>
            {
                RoomSeat[] members;
                RoundStarState state;
                lock (room.Gate)
                {
                    if (room.Phase != RoomPhase.Playing || room.StartGeneration != generation || !IsStarMode(room))
                        return;
                    if (room.Stars is null || room.Stars.Generation != generation)
                        room.Stars = new RoundStarState(generation, _options.StarObjectIdBase);
                    state = room.Stars;
                    if (state.FeverSent)
                        return;
                    state.FeverSent = true;
                    members = [.. room.Seats];
                }
                // The empty Fever/Hurry packet must precede the whole second-wave batch.
                foreach (var member in members)
                    await _starFever!.SendAsync(member.Game, new RoundStarFeverStarted(), operationCt).ConfigureAwait(false);

                var secondWave = new List<RoundStarCreated>();
                var refused = false;
                lock (room.Gate)
                {
                    if (room.Phase != RoomPhase.Playing || room.StartGeneration != generation || state.SecondWaveSent)
                        return;
                    state.SecondWaveSent = true;
                    foreach (var seed in state.FirstWave.ToArray())
                    {
                        if (state.Items.Count >= _options.MaxDroppedStars)
                        {
                            refused = true;
                            break;
                        }
                        var id = checked(++state.LastId);
                        state.Items.Add(id, seed.Kind);
                        secondWave.Add(new RoundStarCreated(id, seed.Kind, seed.Point, seed.Tag));
                    }
                }
                if (refused)
                    _logger.LogWarning("Room {Room}: second star wave stopped at the {Limit}-star active-object bound.",
                        room.Id, _options.MaxDroppedStars);
                _logger.LogInformation(
                    "Room {Room}: second star wave created {Count} item(s) ({Small} small, {Big} big).",
                    room.Id, secondWave.Count, secondWave.Count(star => star.Kind == SmallStarKind),
                    secondWave.Count(star => star.Kind == BigStarKind));
                foreach (var created in secondWave)
                    foreach (var member in members)
                        await _starCreated!.SendAsync(member.Game, created, operationCt).ConfigureAwait(false);
            }, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException)
        {
            // The room closed during the delay.
        }
        catch (Exception failure)
        {
            _logger.LogError(failure, "Room {Room}: the T-45 star Fever/second wave failed.", room.Id);
        }
        finally
        {
            lock (room.Gate)
            {
                if (ReferenceEquals(room.StarFeverCancellation, source))
                    room.StarFeverCancellation = null;
            }
            source.Dispose();
        }
    }

    /// <summary>
    /// Decides a star round under the room's gate, once: by seat in the individual mode (the seats holding the
    /// most stars win; when every seat holds the same it is a draw) and by team in the team mode (the team whose
    /// seats hold more stars wins; equal totals are a draw).
    /// </summary>
    private void DecideStarRound(PlayRoom room)
    {
        if (room.DecidedWinnerTeam is not null || room.DecidedWinnerSlots is not null)
            return;
        var counts = room.Seats.Select(seat => (seat.Slot, seat.Team, Stars: room.Scoring.Stars(seat.Slot))).ToArray();
        if (_options.StarModeIds.Contains(room.ModeId))
        {
            var most = counts.Length == 0 ? 0 : counts.Max(seat => seat.Stars);
            room.DecidedWinnerSlots = counts.Length == 1
                ? new HashSet<int> { counts[0].Slot }
                : counts.All(seat => seat.Stars == most)
                    ? new HashSet<int>()
                    : counts.Where(seat => seat.Stars == most).Select(seat => seat.Slot).ToHashSet();
        }
        else if (_options.StarTeamModeIds.Contains(room.ModeId))
        {
            var red = counts.Where(seat => seat.Team == 1).Sum(seat => (long)seat.Stars);
            var blue = counts.Where(seat => seat.Team == 2).Sum(seat => (long)seat.Stars);
            room.DecidedWinnerTeam = red > blue ? 1 : blue > red ? 2 : 0;
        }
    }
}

/// <summary>One first-wave star seed copied into the T-45 second wave.</summary>
/// <param name="Kind">The client star kind: 1 small, 2 big.</param>
/// <param name="Point">The position and launch velocity.</param>
/// <param name="Tag">The owner tag echoed in the creation body.</param>
internal sealed record RoundStarSeed(int Kind, RoundLucciPoint Point, int Tag);

/// <summary>One round's active stars and the frozen first-wave seeds used by T-45.</summary>
/// <param name="generation">The room's start generation the stars belong to.</param>
/// <param name="objectIdBase">The id handed out before the first star.</param>
internal sealed class RoundStarState(int generation, int objectIdBase)
{
    /// <summary>Gets the start generation; a state from an earlier round is discarded.</summary>
    internal int Generation { get; } = generation;

    /// <summary>Gets or sets the last star object id handed out.</summary>
    internal int LastId { get; set; } = objectIdBase;

    /// <summary>Gets the active stars by object id with their client star kind.</summary>
    internal Dictionary<int, int> Items { get; } = [];

    /// <summary>Gets the first-wave seeds frozen when the Fever/second-wave operation begins.</summary>
    internal List<RoundStarSeed> FirstWave { get; } = [];

    /// <summary>Gets or sets whether the first-wave seed list has been frozen.</summary>
    internal bool FirstWaveFrozen { get; set; }

    /// <summary>Gets or sets whether the empty Fever/Hurry packet was sent this round.</summary>
    internal bool FeverSent { get; set; }

    /// <summary>Gets or sets whether the second wave was allocated this round.</summary>
    internal bool SecondWaveSent { get; set; }

    /// <summary>Gets or sets whether the active-object bound was already reported this round.</summary>
    internal bool RefusalLogged { get; set; }
}
