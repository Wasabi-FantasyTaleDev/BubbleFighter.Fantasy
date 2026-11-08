using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.InGame.Sessions;

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
        lock (room.Gate) generation = room.StartGeneration;
        return new ValueTask(room.Executor.ExecuteAsync(async operationCt =>
        {
            lock (room.Gate)
            {
                if (!FinalZoneReportIsCurrent(room, session, generation)
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
        lock (room.Gate)
        {
            if (!IsFinalZoneMode(room) || room.Phase != RoomPhase.Playing || room.RoundCancelled || room.IsStopping
                || room.FinalZone is not { } state || state.Generation != room.StartGeneration || state.Finished) return;
            eliminated = state.Synchronize(room.Seats);
            recipients = [.. room.Seats];
            finished = state.Finished;
            if (finished)
                room.DecidedWinnerSlots = state.WinnerSlots;
        }
        // kr-1256 sub_CDC4A0 starts the dead player's backpack report and survivor HUD before the result scene.
        foreach (var notice in eliminated)
            foreach (var member in recipients)
                if (!member.Game.Connection.IsClosed)
                    await FinalZoneEliminated!.SendAsync(member.Game, notice, ct).ConfigureAwait(false);
        if (finished) await EndRoundAsync(room, RoundEndReason.WinCondition, ct).ConfigureAwait(false);
    }
}
