using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.InGame.Sessions;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.InGame.Handlers;

internal sealed partial class RoundFlow
{
    // The host's client waits 2000 ms after the goal before it sends the final score (kr-812 sub_513DE0 arms
    // kr-812 sub_A5FCB0(2000) and then kr-812 sub_52D9E0), so the round ends after the same pause.
    private const int BfRunnerFinishDelayMs = 2000;

    // The speed pad's kind (kr-812 sub_580E10 sends 101 for a region of type 2; ItemSpecBFRunner names item 101 booster).
    private const int BfRunnerSpeedPad = 101;

    // A runner holds one item. Mode 21 turns the slot switch off in the client: kr-812 sub_58AE60 returns before it sends
    // the switch request and kr-1256 sub_B037D0 skips its sender when the game mode is 21, so an item granted to the
    // second slot could never be selected or used. A box therefore only fills slot 0, and only while it is empty.
    private const int BfRunnerHeldSlots = 1;

    /// <summary>Gets or sets the build's checkpoint notice encoder; null leaves mode 21 an ordinary team round.</summary>
    internal IMessageSender<RoundBfRunnerCheckpointReached>? BfRunnerCheckpointReached { get; set; }

    /// <summary>Gets or sets the build's race-over notice encoder; set together with the checkpoint notice.</summary>
    internal IMessageSender<RoundBfRunnerFinished>? BfRunnerFinished { get; set; }

    /// <summary>Gets or sets the item box's slot grant encoder; set with the race notices.</summary>
    internal IMessageSender<RoundBfRunnerItemGranted>? BfRunnerItemGranted { get; set; }

    /// <summary>Gets or sets the items an item box can hold, from the build's own client data.</summary>
    internal Data.BfRunnerItemPool? BfRunnerItems { get; set; }

    private bool IsBfRunnerMode(PlayRoom room) => BfRunnerFinished is not null && room.ModeId == (int)GameModeId.BfRunner;

    /// <summary>
    /// Passes a runner's new checkpoint to every player. The reporting client does not store its own region either:
    /// kr-812 sub_580E10 only sends it, and kr-812 sub_5295D0 applies the notice to whichever seat it names.
    /// </summary>
    internal async ValueTask HandleBfRunnerCheckpointAsync(ISession session, RoundBfRunnerCheckpointReported report,
        CancellationToken ct)
    {
        if (FindGameReportRoom(session) is not { } room || !IsBfRunnerMode(room)) return;
        await room.Executor.ExecuteAsync(async operationCt =>
        {
            RoomSeat[] members;
            int slot;
            lock (room.Gate)
            {
                if (room.Phase != RoomPhase.Playing || room.RoundCancelled
                    || room.FindByGame(session.Id) is not { } seat || !ReferenceEquals(seat.Game, session)
                    || !room.BfRunner.TryReach(seat.Slot, report.Region)) return;
                slot = seat.Slot;
                members = [.. room.Seats];
            }
            var reached = new RoundBfRunnerCheckpointReached(slot, report.Region);
            foreach (var member in members)
                if (!member.Game.Connection.IsClosed)
                    await BfRunnerCheckpointReached!.SendAsync(member.Game, reached, operationCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Accepts the host's first goal report for a team that has players in the room, announces the winner to every
    /// player and ends the round for that team after the client's own pause. Only the host's client tests the goal
    /// box (kr-812 sub_513DE0), so a report from another seat is not evidence.
    /// </summary>
    internal async ValueTask HandleBfRunnerGoalAsync(ISession session, RoundBfRunnerGoalReported report, CancellationToken ct)
    {
        if (FindGameReportRoom(session) is not { } room || !IsBfRunnerMode(room)) return;
        await room.Executor.ExecuteAsync(async operationCt =>
        {
            RoomSeat[] members;
            int generation;
            lock (room.Gate)
            {
                if (room.Phase != RoomPhase.Playing || room.RoundCancelled
                    || room.FindByGame(session.Id) is not { } seat || !ReferenceEquals(seat.Game, session)) return;
                if (seat.Slot != room.MasterSlot || report.Team is not (1 or 2)
                    || !room.Seats.Any(member => member.Team == report.Team))
                {
                    _logger.LogWarning("Session {Id} BFRunner goal for team {Team} in room {Room} ignored: not the host's, "
                        + "or no such team in the room.", session.Id, report.Team, room.Id);
                    return;
                }
                if (!room.BfRunner.TryFinish(report.Team)) return;
                room.DecidedWinnerTeam = report.Team;
                generation = room.StartGeneration;
                members = [.. room.Seats];
            }
            _logger.LogInformation("Room {Room}: team {Team} reached the BFRunner goal.", room.Id, report.Team);
            var finished = new RoundBfRunnerFinished(report.Team);
            foreach (var member in members)
                if (!member.Game.Connection.IsClosed)
                    await BfRunnerFinished!.SendAsync(member.Game, finished, operationCt).ConfigureAwait(false);
            _ = BackgroundWork.Detached(() => FinishBfRunnerAsync(room, generation));
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Accepts the progress list every client sends once a second. The race result is the host's goal report; the
    /// round keeps no ranking of its own, so the list only has to come from a player of a running race.
    /// </summary>
    internal ValueTask HandleBfRunnerProgressAsync(ISession session, RoundBfRunnerProgressReported report, CancellationToken ct)
    {
        if (FindGameReportRoom(session) is { } room && IsBfRunnerMode(room))
            _logger.LogTrace("Room {Room}: BFRunner progress for {Count} seats from session {Id}.", room.Id, report.Seats.Count,
                session.Id);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Answers a runner's own track item. An item box (kind 0) puts a weighted item from the client's
    /// ItemSpecBFRunner sheet into the runner's single held slot, if it is empty, and tells every player;
    /// the ordinary item use then spends it. A speed pad (kind 101) applies its effect to the runner on every client
    /// through the direct effect notice, which runs the effect without touching a held slot (kr-812 sub_517D60 ->
    /// kr-812 sub_573730 with its slot flag set).
    /// </summary>
    internal async ValueTask HandleBfRunnerItemAsync(ISession session, RoundBfRunnerItemTouched report, CancellationToken ct)
    {
        if (FindGameReportRoom(session) is not { } room || !IsBfRunnerMode(room)) return;
        await room.Executor.ExecuteAsync(async operationCt =>
        {
            RoomSeat[] members;
            IMessage notice;
            lock (room.Gate)
            {
                if (room.Phase != RoomPhase.Playing || room.RoundCancelled || room.BfRunner.WinnerTeam is not null
                    || room.FindByGame(session.Id) is not { } seat || !ReferenceEquals(seat.Game, session)
                    || seat.Life.State is not (SeatLife.Alive or SeatLife.Bubble)) return;
                switch (report)
                {
                    case { Kind: BfRunnerSpeedPad, Region: -1 } when _countedItemUsed is not null:
                        notice = new RoundCountedItemUsed(seat.Slot, BfRunnerSpeedPad);
                        break;
                    case { Kind: 0, Region: >= 0 } when BfRunnerItems is { } pool && BfRunnerItemGranted is not null
                        && _itemUsed is not null:
                        if (room.Supplies is not { } state || state.Generation != room.StartGeneration)
                            room.Supplies = state = new RoundSupplyState(room.StartGeneration, [], _options.SupplyObjectIdBase);
                        if (!state.HeldItems.TryGetValue(seat.Slot, out var held))
                            state.HeldItems.Add(seat.Slot, held = new(BfRunnerHeldSlots));
                        var itemSlot = held.FirstEmptySlot();
                        if (itemSlot < 0) return;
                        var item = pool.Pick(Random.Shared.NextDouble());
                        held.Add(itemSlot, item);
                        notice = new RoundBfRunnerItemGranted(seat.Slot, item, itemSlot);
                        break;
                    default:
                        _logger.LogDebug("Room {Room}: BFRunner item touch {Kind}/{Region} from session {Id} not answered.",
                            room.Id, report.Kind, report.Region, session.Id);
                        return;
                }
                members = [.. room.Seats];
            }
            foreach (var member in members)
            {
                if (member.Game.Connection.IsClosed) continue;
                if (notice is RoundBfRunnerItemGranted granted)
                    await BfRunnerItemGranted!.SendAsync(member.Game, granted, operationCt).ConfigureAwait(false);
                else
                    await _countedItemUsed!.SendAsync(member.Game, (RoundCountedItemUsed)notice, operationCt).ConfigureAwait(false);
            }
        }, ct).ConfigureAwait(false);
    }

    private async Task FinishBfRunnerAsync(PlayRoom room, int generation)
    {
        try
        {
            await Task.Delay(BfRunnerFinishDelayMs, _lifetime).ConfigureAwait(false);
            await room.Executor.ExecuteAsync(ct =>
            {
                lock (room.Gate)
                {
                    if (room.StartGeneration != generation || room.Phase != RoomPhase.Playing) return ValueTask.CompletedTask;
                }
                return EndRoundAsync(room, RoundEndReason.WinCondition, ct);
            }, _lifetime).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (ObjectDisposedException) { }
        catch (Exception failure) { _logger.LogError(failure, "Room {Room}: the BFRunner round could not end.", room.Id); }
    }
}
