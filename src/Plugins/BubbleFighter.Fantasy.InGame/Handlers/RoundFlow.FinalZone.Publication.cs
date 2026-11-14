using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.InGame.Sessions;

namespace BubbleFighter.Fantasy.InGame.Handlers;

internal sealed partial class RoundFlow
{
    private sealed record FinalZonePublication(FinalZoneRoundState State, FinalZoneItemLedger? Ledger,
        RoomSeat[] Recipients, RoomSeat? Actor = null, ISession? Requester = null, long LifeGeneration = 0)
    {
        internal bool PublicationStarted { get; set; }
    }

    private static bool OwnsFinalZonePublication(PlayRoom room, FinalZonePublication publication) =>
        ReferenceEquals(room.FinalZone, publication.State) && room.StartGeneration == publication.State.Generation
        && ReferenceEquals(publication.State.Items, publication.Ledger);

    private static bool FinalZonePublicationActorIsCurrent(PlayRoom room, FinalZonePublication publication) =>
        publication.Actor is not { } actor || room.Seats.Contains(actor) && actor.Life.Generation == publication.LifeGeneration
        && (ReferenceEquals(actor.Game, publication.Requester) || ReferenceEquals(actor.Relay, publication.Requester));

    private void RequireFinalZonePublication(PlayRoom room, FinalZonePublication publication, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!IsFinalZoneMode(room) || !OwnsFinalZonePublication(room, publication)
            || !FinalZonePublicationActorIsCurrent(room, publication)
            || room.Phase != RoomPhase.Playing || room.RoundCancelled || room.IsStopping || publication.State.Finished
            || room.Seats.Count != publication.Recipients.Length
            || publication.Recipients.Any(member => !room.Seats.Contains(member) || member.Game.Connection.IsClosed))
            throw new InvalidOperationException("FinalZone state or participants changed during publication.");
    }

    private ValueTask CancelFinalZonePublicationAsync(PlayRoom room, FinalZonePublication publication)
    {
        lock (room.Gate)
        {
            if (!OwnsFinalZonePublication(room, publication)
                || !publication.PublicationStarted && !FinalZonePublicationActorIsCurrent(room, publication))
                return ValueTask.CompletedTask;
            return CancelFinalZonePublicationAsync(room, publication.State, publication);
        }
    }

    private async ValueTask PublishFinalZoneAsync<T>(PlayRoom room, FinalZonePublication publication,
        IMessageSender<T> sender, T message, CancellationToken ct) where T : IMessage
    {
        // kr-1256 sub_4627C0 consumes one matching entry; a partial exchange cannot be repaired by replaying it.
        publication.PublicationStarted = true;
        lock (room.Gate) RequireFinalZonePublication(room, publication, ct);
        foreach (var member in publication.Recipients)
        {
            lock (room.Gate) RequireFinalZonePublication(room, publication, ct);
            await sender.SendAsync(member.Game, message, ct).ConfigureAwait(false);
            lock (room.Gate) RequireFinalZonePublication(room, publication, ct);
        }
    }
}
