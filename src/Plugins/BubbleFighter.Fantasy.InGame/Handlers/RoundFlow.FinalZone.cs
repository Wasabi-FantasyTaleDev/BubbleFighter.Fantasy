using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.InGame.Sessions;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.InGame.Handlers;

internal sealed partial class RoundFlow
{
    /// <summary>Gets or sets the native Area32 elimination encoder; omission leaves FinalZone disabled.</summary>
    internal IMessageSender<RoundFinalZonePlayerEliminated>? FinalZoneEliminated { get; set; }

    // kr-1256 sub_9E1710 selects GameArea32Strategy for all four modes; kr-1256 sub_CD7DE0 uses the shared hub.
    private bool IsFinalZoneMode(PlayRoom room) => FinalZoneEliminated is not null && room.Tutorial is null
        && (GameModeId)room.ModeId is GameModeId.Area32 or GameModeId.SuddenSurvival
            or GameModeId.FinalZone or GameModeId.ZombieSurvival;

    private static FinalZoneRoundState CreateFinalZoneState(PlayRoom room) =>
        new(room.StartGeneration, room.Seats, room.ModeId == (int)GameModeId.SuddenSurvival);

    private static bool FinalZoneReportIsCurrent(PlayRoom room, ISession session, int generation) =>
        room.Phase == RoomPhase.Playing && !room.RoundCancelled && !room.IsStopping
        && room.StartGeneration == generation && room.FinalZone is { Finished: false } state
        && state.Generation == generation && !session.Connection.IsClosed
        && room.Seats.Any(seat => ReferenceEquals(seat.Game, session) || ReferenceEquals(seat.Relay, session));

    private ValueTask HandleFinalZoneDeathReportedAsync(PlayRoom room, ISession session, RoundDeathReported report,
        CancellationToken ct)
    {
        int generation;
        FinalZoneRoundState? state;
        lock (room.Gate)
        {
            generation = room.StartGeneration;
            state = room.FinalZone;
            if (state is null) return ValueTask.CompletedTask;
        }
        return new ValueTask(ExecuteFinalZoneGuardedAsync(room, state, async operationCt =>
        {
            lock (room.Gate)
            {
                if (!FinalZoneReportIsCurrent(room, session, generation) || !ReferenceEquals(room.FinalZone, state)
                    || room.Seats.FirstOrDefault(seat => ReferenceEquals(seat.Game, session)) is not { } seat
                    || report.Actor != RoomRegistry.SceneEntity(seat.Slot)
                    || !OwnsActor(seat, report.Actor, report.ActorSeat)) return;
                seat.Life.RecordDeath(report.Actor, serverTimed: false);
            }
            await SynchronizeFinalZoneAsync(room, operationCt).ConfigureAwait(false);
        }, ct));
    }

    /// <summary>Captures the opening participants on the room executor without duplicating their life state.</summary>
    internal void InitializeFinalZone(PlayRoom room)
    {
        lock (room.Gate)
        {
            if (!IsFinalZoneMode(room) || room.Phase != RoomPhase.Playing || room.RoundCancelled || room.IsStopping) return;
            if (!room.FinalZoneCleanupTask.IsCompleted)
                throw new InvalidOperationException("FinalZone cannot start while its previous connection cleanup is pending.");
            if (room.FinalZone?.Generation == room.StartGeneration) return;
            room.FinalZone = CreateFinalZoneState(room);
        }
    }

    /// <summary>Publishes newly final deaths once and ends a contested match when only one participant remains.</summary>
    internal async ValueTask SynchronizeFinalZoneAsync(PlayRoom room, CancellationToken ct)
    {
        RoomSeat[] recipients;
        RoundFinalZonePlayerEliminated[] eliminated;
        bool finished;
        FinalZoneRoundState state;
        lock (room.Gate)
        {
            if (!IsFinalZoneMode(room) || room.Phase != RoomPhase.Playing || room.RoundCancelled || room.IsStopping
                || room.FinalZone is not { } current || current.Generation != room.StartGeneration || current.Finished) return;
            state = current;
            eliminated = state.Synchronize(room.Seats);
            recipients = [.. room.Seats.Where(member => !member.Game.Connection.IsClosed)];
            finished = state.Finished;
            if (finished)
                room.DecidedWinnerSlots = state.WinnerSlots;
        }
        try
        {
            // kr-1256 sub_CDC4A0 starts the dead player's backpack report and survivor HUD before the result scene.
            foreach (var notice in eliminated)
                foreach (var member in recipients)
                {
                    lock (room.Gate) RequireFinalZoneEliminationPublication(room, state, recipients);
                    await FinalZoneEliminated!.SendAsync(member.Game, notice, ct).ConfigureAwait(false);
                    lock (room.Gate) RequireFinalZoneEliminationPublication(room, state, recipients);
                }
            ct.ThrowIfCancellationRequested();
            lock (room.Gate) RequireFinalZoneEliminationPublication(room, state, recipients);
            if (finished) await EndRoundAsync(room, RoundEndReason.WinCondition, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            await CancelFinalZonePublicationAsync(room, state).ConfigureAwait(false);
            throw;
        }
    }

    private static void RequireFinalZoneEliminationPublication(PlayRoom room, FinalZoneRoundState state, RoomSeat[] recipients)
    {
        if (!ReferenceEquals(room.FinalZone, state) || room.StartGeneration != state.Generation
            || room.Phase != RoomPhase.Playing || room.RoundCancelled || room.IsStopping
            || recipients.Any(member => !ReferenceEquals(room.FindByGame(member.Game.Id), member) || member.Game.Connection.IsClosed))
            throw new InvalidOperationException("FinalZone participants changed during elimination publication.");
    }

    private async Task ExecuteFinalZoneGuardedAsync(PlayRoom room, FinalZoneRoundState? state,
        Func<CancellationToken, ValueTask> operation, CancellationToken ct)
    {
        try
        {
            await room.Executor.ExecuteAsync(operation, ct).ConfigureAwait(false);
        }
        catch (Exception) when (state is not null)
        {
            await CancelFinalZonePublicationAsync(room, state).ConfigureAwait(false);
            throw;
        }
    }

    private ValueTask CancelFinalZonePublicationAsync(PlayRoom room, FinalZoneRoundState state, FinalZonePublication? publication = null)
    {
        lock (room.Gate)
        {
            if (!ReferenceEquals(room.FinalZone, state) || room.StartGeneration != state.Generation
                || publication is not null && !OwnsFinalZonePublication(room, publication))
                return ValueTask.CompletedTask;
            if (state.CleanupTask is { } existing) return new(existing);
            if (room.Phase is RoomPhase.Waiting or RoomPhase.Closed || room.IsStopping) return ValueTask.CompletedTask;
            // kr-1256 sub_6FBA90 and kr-1256 sub_4627C0 consume transitions that cannot be replayed after partial publication.
            room.RoundCancelled = true;
            IConnection[] connections = [.. room.Seats.SelectMany(seat => new[] { seat.Game.Connection, seat.Relay?.Connection })
                .OfType<IConnection>().Distinct<IConnection>(ReferenceEqualityComparer.Instance)];
            return new(room.FinalZoneCleanupTask = state.CleanupTask
                = BackgroundWork.Detached(() => CloseFinalZoneConnectionsAsync(room, state, connections, publication)));
        }
    }

    private async Task CloseFinalZoneConnectionsAsync(PlayRoom room, FinalZoneRoundState state, IConnection[] connections,
        FinalZonePublication? publication)
    {
        foreach (var connection in connections)
        {
            try
            {
                ValueTask closing;
                lock (room.Gate)
                {
                    if (!ReferenceEquals(room.FinalZone, state) || room.StartGeneration != state.Generation
                        || publication is not null && !OwnsFinalZonePublication(room, publication)
                        || room.Phase is RoomPhase.Waiting or RoomPhase.Closed) return;
                    if (connection.IsClosed || !room.Seats.Any(seat => ReferenceEquals(seat.Game.Connection, connection)
                        || ReferenceEquals(seat.Relay?.Connection, connection))) continue;
                    closing = connection.CloseAsync("FinalZone publication failed; round cancelled.");
                }
                await closing.ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                _logger.LogError(failure, "Room {Room}: connection {Connection} failed to close after FinalZone publication failed.",
                    room.Id, connection.Id);
            }
        }
    }
}
