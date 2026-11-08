using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.InGame.Sessions;

namespace BubbleFighter.Fantasy.InGame.Handlers;

internal sealed partial class RoundFlow
{
    /// <summary>Gets or sets whether this row enables the native Zombie Escape rules.</summary>
    internal bool ZombieEscapeEnabled { get; set; }
    private bool IsZombieEscapeMode(PlayRoom room) => ZombieEscapeEnabled
        && ScriptZombieTeams is not null && ScriptZombieConversions is not null
        && room.Tutorial is null && room.ModeId == (int)GameModeId.ZombieEscape;

    internal void InitializeZombieEscape(PlayRoom room)
    {
        if (!IsZombieEscapeMode(room)) return;
        room.ZombieEscape = new(room.StartGeneration,
            room.Seats.Single(seat => seat.Slot == room.MasterSlot).Game, room.Seats);
        foreach (var member in room.Seats) member.Team = 1;
    }

    private bool ZombieEscapeReportIsCurrent(PlayRoom room, ISession session, int generation) =>
        IsZombieEscapeMode(room) && room.Phase == RoomPhase.Playing && !room.RoundCancelled && !room.IsStopping
        && room.StartGeneration == generation && room.ZombieEscape is { } state && state.Generation == generation
        && !session.Connection.IsClosed
        && room.Seats.Any(seat => ReferenceEquals(seat.Game, session) || ReferenceEquals(seat.Relay, session))
        && room.Seats.Any(seat => seat.Slot == room.MasterSlot && ReferenceEquals(seat.Game, state.Master));

    internal async ValueTask HandleZombieEscapeConversionAsync(ISession session, ScriptZombieConversion request, CancellationToken ct)
    {
        if (_registry.TryGame(session) is not { } current) return;
        var room = current.Room;
        int generation;
        lock (room.Gate) generation = room.StartGeneration;
        await room.Executor.ExecuteAsync(async operationCt =>
        {
            RoomSeat[] members;
            RoundScriptZombieTeamChanged change;
            lock (room.Gate)
            {
                if (!ZombieEscapeReportIsCurrent(room, session, generation)
                    || room.Seats.FirstOrDefault(seat => ReferenceEquals(seat.Game, session)) is not { } requester
                    || room.Seats.FirstOrDefault(seat => seat.Slot == request.Seat && !seat.Game.Connection.IsClosed) is not { } target
                    || !room.ZombieEscape!.Select(requester, target, request, room.Seats.Count(seat => !seat.Game.Connection.IsClosed))) return;
                target.Team = 34;
                members = [.. room.Seats];
                change = new(request, members.Select(HumanScoreMember).ToArray());
            }
            foreach (var member in members)
                if (!member.Game.Connection.IsClosed)
                    await ScriptZombieTeams!.SendAsync(member.Game, change, operationCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    internal async ValueTask HandleZombieEscapePeerAsync(ISession session, RoundScriptZombieConversionReported report, CancellationToken ct)
    {
        if (_registry.TryGet(session.Id) is not { } room) return;
        int generation;
        lock (room.Gate) generation = room.StartGeneration;
        await room.Executor.ExecuteAsync(async operationCt =>
        {
            RoomSeat[] members;
            lock (room.Gate)
            {
                if (!ZombieEscapeReportIsCurrent(room, session, generation)
                    || room.Seats.FirstOrDefault(seat => ReferenceEquals(seat.Game, session) || ReferenceEquals(seat.Relay, session)) is not { } owner
                    || !room.ZombieEscape!.Confirm(owner, report)) return;
                members = [.. room.Seats];
            }
            // kr-1256 sub_6E0DA0 applies conversion and clears the local pending latch only on receipt.
            foreach (var member in members)
                if (!member.Game.Connection.IsClosed)
                    await ScriptZombieConversions!.SendAsync(member.Game, report, operationCt).ConfigureAwait(false);
            await ZombieEscapeOutcomeAsync(room, operationCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    internal async ValueTask HandleStageResultAsync(ISession session, RoundStageResultReported report, CancellationToken ct)
    {
        if (_registry.TryGame(session) is not { } current) return;
        var room = current.Room;
        int generation;
        lock (room.Gate) generation = room.StartGeneration;
        await room.Executor.ExecuteAsync(operationCt =>
        {
            lock (room.Gate)
            {
                if (!ZombieEscapeReportIsCurrent(room, session, generation)
                    || !ReferenceEquals(room.ZombieEscape!.Master, session) || room.ZombieEscape.InitialZombies == 0
                    || report.Outcome is not (0 or 1))
                    return ValueTask.CompletedTask;
                if (report.Outcome == 0 && !room.Seats.Any(seat => seat.Team == 1 && !seat.Game.Connection.IsClosed))
                    return ValueTask.CompletedTask;
                room.DecidedWinnerTeam = report.Outcome == 0 ? 1 : 34;
            }
            return EndRoundAsync(room, RoundEndReason.WinCondition, operationCt);
        }, ct).ConfigureAwait(false);
    }

    private ValueTask ZombieEscapeOutcomeAsync(PlayRoom room, CancellationToken ct)
    {
        lock (room.Gate)
        {
            if (!IsZombieEscapeMode(room) || room.Phase != RoomPhase.Playing || room.RoundCancelled
                || room.ZombieEscape is not { InitialZombies: > 0 } state || state.HasPendingConversions(room.Seats)
                || !room.Seats.Any(seat => !seat.Game.Connection.IsClosed)
                || room.Seats.Any(seat => seat.Team == 1 && !seat.Game.Connection.IsClosed))
                return ValueTask.CompletedTask;
            room.DecidedWinnerTeam = 34;
        }
        return EndRoundAsync(room, RoundEndReason.WinCondition, ct);
    }
}
