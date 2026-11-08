using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.InGame.Sessions;

namespace BubbleFighter.Fantasy.InGame.Handlers;

internal sealed partial class RoundFlow
{
    // kr-1256 sub_1135D70 registers Rhea as 29; kr-1256 sub_4BD3E0 selects its transformed type 2000.
    private const int RheaCharacterItem = 300029;

    /// <summary>Confirms the equipped Rhea owner's form request for its current scene incarnation.</summary>
    internal async ValueTask HandleCharacterFormAsync(ISession session, RoundCharacterFormRequested request,
        IMessageSender<RoundCharacterFormChanged> sender, CancellationToken ct)
    {
        if (FindGameReportRoom(session) is not { } room) return;
        RoomSeat member;
        int round;
        long life;
        lock (room.Gate)
        {
            if (room.Phase != RoomPhase.Playing || room.RoundCancelled || room.IsStopping
                || room.FindByGame(session.Id) is not { } seat || !ReferenceEquals(seat.Game, session)
                || session.Connection.IsClosed || request.Actor != RoomRegistry.SceneEntity(seat.Slot)
                || seat.CharacterId != RheaCharacterItem
                || request.Active && seat.Life.State != SeatLife.Alive) return;
            member = seat;
            round = room.StartGeneration;
            life = seat.Life.Generation;
        }
        await room.Executor.ExecuteAsync(async operationCt =>
        {
            ISession[] targets;
            lock (room.Gate)
            {
                if (room.Phase != RoomPhase.Playing || room.RoundCancelled || room.IsStopping
                    || room.StartGeneration != round || member.Life.Generation != life
                    || !ReferenceEquals(room.FindByGame(session.Id), member) || session.Connection.IsClosed
                    || member.CharacterId != RheaCharacterItem) return;
                if (request.Active && (member.Life.State != SeatLife.Alive || member.Life.CharacterFormActive)) return;
                var changed = member.Life.CharacterFormActive != request.Active;
                member.Life.CharacterFormActive = request.Active;
                // kr-1256 sub_7055D0 clears the owner's pending latch even for false; repeating it resets charge.
                // A duplicate end therefore acknowledges only its owner, without restarting every peer's effects.
                targets = changed
                    ? [.. room.Seats.Select(seat => seat.Game).Where(game => !game.Connection.IsClosed)]
                    : [session];
            }
            // kr-1256 sub_4FF320 gates charge locally; kr-1256 sub_B07150 requests false at eight seconds.
            // The reply itself applies the form on every client (kr-1256 sub_7055D0); it emits no peer effect.
            var changedForm = new RoundCharacterFormChanged(request.Actor, request.Active);
            foreach (var target in targets)
                await sender.SendAsync(target, changedForm, operationCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }
}
