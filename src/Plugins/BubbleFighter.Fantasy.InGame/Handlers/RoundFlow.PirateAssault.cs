using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.InGame.Sessions;

namespace BubbleFighter.Fantasy.InGame.Handlers;

internal sealed partial class RoundFlow
{
    internal bool PirateAssaultTerminalReports { get; set; }

    private static bool IsPirateAssaultWaveSource(PlayRoom room, ISession session, bool peer) =>
        room.ModeId == (int)GameModeId.PirateAssaultSeason3 && room.Tutorial is null
        && room.Phase is RoomPhase.Starting or RoomPhase.Playing && !room.RoundCancelled && !room.IsStopping
        && !session.Connection.IsClosed && room.Seats.Any(seat => seat.Slot == room.MasterSlot && !room.IsRefereeSeat(seat)
            && !seat.Game.Connection.IsClosed && (ReferenceEquals(session, seat.Game) || peer && ReferenceEquals(session, MatchLink(room, seat))));

    private static bool IsPirateAssaultMaster(PlayRoom room, RoomSeat master, int generation) =>
        room.Phase == RoomPhase.Playing && !room.IsStopping && !room.RoundCancelled && room.Tutorial is null
        && room.ModeId == (int)GameModeId.PirateAssaultSeason3 && room.StartGeneration == generation
        && room.MasterSlot == master.Slot && ReferenceEquals(room.FindByGame(master.Game.Id), master)
        && !master.Game.Connection.IsClosed && !room.IsRefereeSeat(master);

    private static bool IsPirateAssaultResultCurrent(PlayRoom room, PirateAssaultWaveResult result) =>
        ReferenceEquals(room.PirateAssaultResult, result) && IsPirateAssaultMaster(room, result.Master, result.Generation);

    // kr-1256 sub_7BAC80 sends 905 then kr-1256 sub_6E5840(1); kr-1256 sub_7B9820 sends 906 then kr-1256 sub_6E5840(0).
    private async ValueTask RememberPirateAssaultResultAsync(ISession session, RoundNpcReported report,
        IMessageSender<RoundNpcAcknowledged> reply, CancellationToken ct)
    {
        if (!report.Payload.IsEmpty || report.PeerFeed is not null || _registry.TryGame(session) is not { } participant
            || !ReferenceEquals(participant.Seat.Game, session)) return;
        var (room, master) = participant;
        int generation, masterRevision;
        lock (room.Gate)
        {
            generation = room.StartGeneration;
            masterRevision = room.PirateAssaultMasterRevision;
            if (!IsPirateAssaultMaster(room, master, generation)) return;
        }
        await room.Executor.ExecuteAsync(_ =>
        {
            lock (room.Gate)
            {
                if (room.PirateAssaultMasterRevision == masterRevision && IsPirateAssaultMaster(room, master, generation)
                    && (room.PirateAssaultResult is not { } existing || !IsPirateAssaultResultCurrent(room, existing)))
                    room.PirateAssaultResult = new(generation, master, report.Operation, reply);
            }
            return ValueTask.CompletedTask;
        }, ct).ConfigureAwait(false);
    }

    internal ValueTask HandleMultiplayerStageResultAsync(ISession session, RoundStageResultReported report, CancellationToken ct)
    {
        if (_registry.TryGame(session) is { } participant)
        {
            var (room, seat) = participant;
            lock (room.Gate)
            {
                if (room.ModeId == (int)GameModeId.PirateAssaultSeason3)
                {
                    if (room.PirateAssaultResult is not { } pending
                        || report.Outcome != (pending.Operation == RoundNpcOperation.CompleteWaves ? 1 : 0)
                        || !ReferenceEquals(seat.Game, session) || !ReferenceEquals(pending.Master, seat)
                        || !IsPirateAssaultResultCurrent(room, pending)) return ValueTask.CompletedTask;
                    return new(room.Executor.ExecuteAsync(token => FinishPirateAssaultResultAsync(room, pending, token), ct));
                }
            }
        }
        return HandleStageResultAsync(session, report, ct);
    }

    private async ValueTask FinishPirateAssaultResultAsync(PlayRoom room, PirateAssaultWaveResult pending, CancellationToken ct)
    {
        RoomSeat[] members;
        lock (room.Gate)
        {
            if (!IsPirateAssaultResultCurrent(room, pending)) return;
            members = [.. room.Seats];
        }
        foreach (var seat in members)
        {
            lock (room.Gate)
            {
                if (!IsPirateAssaultResultCurrent(room, pending)) return;
                if (seat.Game.Connection.IsClosed || !room.Seats.Contains(seat) || pending.Notified.Contains(seat)) continue;
            }
            // kr-1256 sub_7ED5C0 writes success/failure locally; kr-1256 sub_7EFA10 reads it on each seat for result audio.
            await pending.Reply.SendAsync(seat.Game, new(pending.Operation, ReadOnlyMemory<byte>.Empty), ct).ConfigureAwait(false);
            lock (room.Gate) pending.Notified.Add(seat);
        }
        lock (room.Gate)
            if (!IsPirateAssaultResultCurrent(room, pending)) return;
        await EndRoundAsync(room, pending.Operation == RoundNpcOperation.CompleteWaves
            ? RoundEndReason.WavesCleared : RoundEndReason.CooperativeFailed, ct).ConfigureAwait(false);
    }
}
