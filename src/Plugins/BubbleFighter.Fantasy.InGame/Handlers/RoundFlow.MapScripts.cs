using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.InGame.Sessions;

namespace BubbleFighter.Fantasy.InGame.Handlers;

internal sealed partial class RoundFlow
{
    /// <summary>Gets or sets the shared native effect encoder for FinalZone and Zombie Escape scripts.</summary>
    internal IMessageSender<RoundFinalZoneMapEffectChanged>? FinalZoneMapEffects { get; set; }

    /// <summary>Gets or sets the encoder for a script's named gate or other addable object.</summary>
    internal IMessageSender<RoundMapObjectVisibilityChanged>? MapObjectVisibility { get; set; }

    /// <summary>Gets or sets the encoder for a script's named breakable obstacle.</summary>
    internal IMessageSender<RoundBreakableMapObjectCreated>? BreakableMapObjects { get; set; }

    internal ValueTask HandleFinalZoneMapEffectAsync(ISession session, RoundFinalZoneMapEffectChanged report,
        CancellationToken ct) => HandleMapScriptAsync(session, report, FinalZoneMapEffects, effects: true, ct);

    internal ValueTask HandleMapObjectVisibilityAsync(ISession session, RoundMapObjectVisibilityChanged report,
        CancellationToken ct) => HandleMapScriptAsync(session, report, MapObjectVisibility, effects: false, ct);

    internal ValueTask HandleBreakableMapObjectAsync(ISession session, RoundBreakableMapObjectCreated report,
        CancellationToken ct) => HandleMapScriptAsync(session, report, BreakableMapObjects, effects: false, ct);

    private bool MapScriptStateIsCurrent(PlayRoom room, int generation, bool effects)
    {
        if (room.Tutorial is not null) return false;
        if (effects && IsFinalZoneMode(room))
            return room.FinalZone is { Finished: false } final && final.Generation == generation;
        return ZombieEscapeEnabled && room.ModeId == (int)GameModeId.ZombieEscape
            && room.ZombieEscape is { } escape && escape.Generation == generation
            && ReferenceEquals(escape.Master, room.Seats.FirstOrDefault(seat => seat.Slot == room.MasterSlot)?.Game);
    }

    private async ValueTask HandleMapScriptAsync<T>(ISession session, T report, IMessageSender<T>? sender,
        bool effects, CancellationToken ct) where T : IMessage
    {
        if (sender is null || _registry.TryGet(session.Id) is not { } room) return;
        RoomSeat reporter;
        int generation;
        lock (room.Gate)
        {
            if (room.Seats.FirstOrDefault(seat => ReferenceEquals(seat.Game, session)
                || ReferenceEquals(seat.Relay, session)) is not { } seat) return;
            reporter = seat;
            generation = room.StartGeneration;
        }
        await room.Executor.ExecuteAsync(async operationCt =>
        {
            RoomSeat[] recipients;
            lock (room.Gate)
            {
                if (room.Phase != RoomPhase.Playing || room.RoundCancelled || room.IsStopping
                    || room.StartGeneration != generation || !MapScriptStateIsCurrent(room, generation, effects)
                    || !room.Seats.Contains(reporter) || reporter.Slot != room.MasterSlot || session.Connection.IsClosed
                    || !ReferenceEquals(reporter.Game, session) && !ReferenceEquals(reporter.Relay, session)) return;
                recipients = [.. room.Seats];
            }
            // kr-1256 sub_6E0FD0 / kr-1256 sub_6E1150 / kr-1256 sub_6E12D0 install the sequence only on receipt, also for the producer.
            foreach (var member in recipients)
                if (!member.Game.Connection.IsClosed)
                    await sender.SendAsync(member.Game, report, operationCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }
}
