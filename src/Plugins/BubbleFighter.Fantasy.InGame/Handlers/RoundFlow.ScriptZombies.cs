using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.InGame.Sessions;

namespace BubbleFighter.Fantasy.InGame.Handlers;

internal sealed partial class RoundFlow
{
    internal IMessageSender<RoundScriptZombieTeamChanged>? ScriptZombieTeams { get; set; }
    internal IMessageSender<RoundScriptZombieConversionReported>? ScriptZombieConversions { get; set; }

    private bool IsScriptZombieMode(PlayRoom room) => _options.UsesScriptZombieMode(room.ModeId)
        && ScriptZombieTeams is not null && ScriptZombieConversions is not null
        && room.Tutorial is null;

    internal void InitializeScriptZombies(PlayRoom room)
    {
        if (!IsScriptZombieMode(room)) return;
        room.ScriptZombies = new(room.StartGeneration,
            room.Seats.Single(seat => seat.Slot == room.MasterSlot).Game, room.Seats, room.ModeId);
        foreach (var member in room.Seats) member.Team = ScriptZombieRoundState.HumanTeam;
    }

    private bool ScriptZombieReportIsCurrent(PlayRoom room, ISession session, int generation) =>
        IsScriptZombieMode(room) && room.Phase == RoomPhase.Playing && !room.RoundCancelled && !room.IsStopping
        && room.StartGeneration == generation && room.ScriptZombies is { } state && state.Generation == generation
        && state.ModeId == room.ModeId
        && !session.Connection.IsClosed
        && room.Seats.Any(seat => ReferenceEquals(seat.Game, session) || ReferenceEquals(seat.Relay, session))
        && room.Seats.Any(seat => seat.Slot == room.MasterSlot && ReferenceEquals(seat.Game, state.Master));

    internal async ValueTask HandleScriptZombieConversionAsync(ISession session, ScriptZombieConversion request, CancellationToken ct)
    {
        if (_registry.TryGame(session) is not { } current) return;
        var room = current.Room;
        if (IsZombieEscapeMode(room))
        {
            await HandleZombieEscapeConversionAsync(session, request, ct).ConfigureAwait(false);
            return;
        }
        int generation;
        lock (room.Gate) generation = room.StartGeneration;
        await room.Executor.ExecuteAsync(async operationCt =>
        {
            RoomSeat[] members;
            RoundScriptZombieTeamChanged change;
            lock (room.Gate)
            {
                if (!ScriptZombieReportIsCurrent(room, session, generation)
                    || room.Seats.FirstOrDefault(seat => ReferenceEquals(seat.Game, session)) is not { } requester
                    || room.Seats.FirstOrDefault(seat => seat.Slot == request.Seat && !seat.Game.Connection.IsClosed) is not { } target
                    || !room.ScriptZombies!.Select(requester, target, request, room.Seats.Count(seat => !seat.Game.Connection.IsClosed))) return;
                target.Team = ScriptZombieRoundState.ZombieTeam;
                members = [.. room.Seats];
                change = new(request, members.Select(HumanScoreMember).ToArray());
            }
            foreach (var member in members)
                if (!member.Game.Connection.IsClosed)
                    await ScriptZombieTeams!.SendAsync(member.Game, change, operationCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    internal async ValueTask HandleScriptZombiePeerAsync(ISession session, RoundScriptZombieConversionReported report, CancellationToken ct)
    {
        if (_registry.TryGet(session.Id) is not { } room) return;
        if (IsZombieEscapeMode(room))
        {
            await HandleZombieEscapePeerAsync(session, report, ct).ConfigureAwait(false);
            return;
        }
        int generation;
        lock (room.Gate) generation = room.StartGeneration;
        await room.Executor.ExecuteAsync(async operationCt =>
        {
            bool otherMode;
            lock (room.Gate) otherMode = !_options.UsesScriptZombieMode(room.ModeId) && room.ModeId != (int)GameModeId.ZombieEscape;
            if (otherMode)
            {
                if (_feed is not null && report.OtherModeFeed is { } feed)
                    await ForwardFeedAsync(room, session, feed, operationCt, () =>
                        !_options.UsesScriptZombieMode(room.ModeId) && room.ModeId != (int)GameModeId.ZombieEscape && room.StartGeneration == generation
                        && !room.RoundCancelled && !room.IsStopping && !session.Connection.IsClosed
                        && room.Seats.Any(seat => ReferenceEquals(seat.Game, session) || ReferenceEquals(seat.Relay, session)))
                        .ConfigureAwait(false);
                return;
            }
            RoomSeat[] members;
            lock (room.Gate)
            {
                if (!ScriptZombieReportIsCurrent(room, session, generation)
                    || room.Seats.FirstOrDefault(seat => ReferenceEquals(seat.Game, session) || ReferenceEquals(seat.Relay, session)) is not { } owner
                    || !room.ScriptZombies!.Confirm(owner, report)) return;
                members = [.. room.Seats];
            }
            // kr-1256 sub_6E0DA0 / cn-774 sub_8A2250 / tw-454 sub_715220 apply and clear the
            // pending latch on receipt, including for the producer of the native peer operation.
            foreach (var member in members)
                if (!member.Game.Connection.IsClosed)
                    await ScriptZombieConversions!.SendAsync(member.Game, report, operationCt).ConfigureAwait(false);
            await ScriptZombieOutcomeAsync(room, operationCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    private ValueTask ScriptZombieOutcomeAsync(PlayRoom room, CancellationToken ct)
    {
        lock (room.Gate)
        {
            if (!IsScriptZombieMode(room) || room.Phase != RoomPhase.Playing || room.RoundCancelled
                || room.ScriptZombies is not { InitialZombies: > 0 } state || state.Generation != room.StartGeneration
                || state.ModeId != room.ModeId || !room.Seats.Any(seat => !seat.Game.Connection.IsClosed))
                return ValueTask.CompletedTask;
            // kr-1256 sub_6F6900 -> kr-1256 sub_AC5110 and cn-774 sub_8B6250 -> cn-774 sub_CA7D50 end on the
            // original king's death cleanup. Ordinary mode 8 has no king and cannot take this result.
            if (state.KingDeathIsFinal(room.Seats))
                room.DecidedWinnerTeam = ScriptZombieRoundState.HumanTeam;
            else
            {
                if (state.HasPendingConversions(room.Seats)
                    || room.Seats.Any(seat => seat.Team == ScriptZombieRoundState.HumanTeam && !seat.Game.Connection.IsClosed))
                    return ValueTask.CompletedTask;
                room.DecidedWinnerTeam = ScriptZombieRoundState.ZombieTeam;
            }
        }
        return EndRoundAsync(room, RoundEndReason.WinCondition, ct);
    }
}
