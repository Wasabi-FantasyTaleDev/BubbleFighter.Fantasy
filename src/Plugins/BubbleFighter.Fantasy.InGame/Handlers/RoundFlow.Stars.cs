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
                    room.Stars = state = new RoundLucciState(room.StartGeneration, _options.StarObjectIdBase);
                if (state.Items.Count >= _options.MaxDroppedStars)
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
                if (room.Scoring.AddStars(seat.Slot, change, _options.MaxRoundStars) is not { } stats)
                    return;
                if (room.Phase == RoomPhase.Playing)
                    update = new RoundScoreUpdated(room.Id, Math.Max(1, (int)seat.Team), [.. room.Scoring.TeamTotals], [stats], LiveScoreboard(room));
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
            updates = [.. room.Seats.Select(seat => new RoundScoreUpdated(room.Id, Math.Max(1, (int)seat.Team),
                [.. room.Scoring.TeamTotals], [room.Scoring.Stats(seat.Slot)], LiveScoreboard(room)))];
        }
        foreach (var update in updates)
            await SendScoreAsync(room, update, ct).ConfigureAwait(false);
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
            room.DecidedWinnerSlots = counts.All(seat => seat.Stars == most)
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
