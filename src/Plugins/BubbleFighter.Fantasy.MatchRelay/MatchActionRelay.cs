using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.InGame.Dialects;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.MatchRelay;

/// <summary>Forwards verified actions only between initialized transports in the same live round.</summary>
/// <param name="roster">The rooms row's roster.</param>
/// <param name="scoreboard">The room-owned team points a kill adds to.</param>
/// <param name="logger">The row logger.</param>
/// <param name="lifetime">Cancellation when the owning plugin unloads.</param>
/// <param name="life">The life tracking of the timed-score mode; none tracks no lives.</param>
internal sealed class MatchActionRelay(IRoomRoster roster, IRoundScoreboard scoreboard, ILogger logger,
    CancellationToken lifetime, MatchLifeFlow? life = null)
{
    internal async ValueTask HandleAsync(ISession session, InboundPacket packet, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lifetime.ThrowIfCancellationRequested();
        var action = MatchAction1256.Read(packet.Opcode, packet.Payload.Span);
        var view = roster.FindBySession(session.Id);
        if (view is null)
        {
            logger.LogDebug("Session {Session} action {Opcode} has no current room.", session.Id, packet.Opcode);
            return;
        }

        var sender = view.Seats.FirstOrDefault(s => ReferenceEquals(s.Relay, session));
        if (view.Stage != RoomStage.Playing || sender is null || !IsInitialized(sender, session))
        {
            logger.LogDebug("Session {Session} action {Opcode} arrived outside an initialized playing seat.", session.Id, packet.Opcode);
            return;
        }
        var npcAction = false;
        if (!action.IsReportedBy(packet.Payload.Span, checked(1000000 + 100000 * sender.Slot)))
        {
            if (view.Seats.Any(s => action.IsReportedBy(packet.Payload.Span, checked(1000000 + 100000 * s.Slot))))
                throw new PacketFormatException($"C2S Unknown{packet.Opcode} actor does not own the match connection.", action.ActorOffset);
            // 0x712B90 assigns NPC control dynamically. In the horde family the host's client runs the NPCs and
            // reports their actions on its own match connection (C2S
            // 38281 / 38280 / 38273 naming entities 1000004, 1000008,...), which the peers must receive;
            // elsewhere an unregistered entity proves no ownership.
            if (!view.ClientRunsNpcs)
            {
                logger.LogWarning("Session {Session} action {Opcode} rejected: reporting entity is not a registered player in room {Room}; NPC ownership is unsupported.",
                    session.Id, packet.Opcode, view.RoomId);
                return;
            }
            npcAction = true;
        }
        var scoringTeam = npcAction ? (byte)0 : life?.Observe(view, sender, packet) ?? 0;
        var recipients = view.Seats
            .Where(s => (s.Slot != sender.Slot || action.EchoSender) && s.Relay is { } relay && IsInitialized(s, relay))
            .ToArray();

        foreach (var seat in recipients)
        {
            ct.ThrowIfCancellationRequested();
            lifetime.ThrowIfCancellationRequested();
            // A preceding asynchronous send may span leave, rebind or a new round.
            var now = roster.FindBySession(session.Id);
            if (now is null || now.Generation != view.Generation || now.Stage != RoomStage.Playing
                || now.Seats.FirstOrDefault(s => s.Slot == sender.Slot) is not { } senderNow || !IsInitialized(senderNow, session)) break;
            if (now.Seats.FirstOrDefault(s => s.Slot == seat.Slot) is not { } current
                || !ReferenceEquals(current.Relay, seat.Relay) || !IsInitialized(current, seat.Relay!)) continue;
            var transport = seat.Relay!;
            try
            {
                await transport.SendAsync(action.OutboundOpcode, packet.Payload, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && transport.Connection.IsClosed)
            {
                logger.LogDebug(ex, "Match transport {Session} closed during action {Opcode} delivery.", transport.Id, packet.Opcode);
            }
        }
        logger.LogDebug("Session {Session} action {Opcode} forwarded in room {Room}.", session.Id, packet.Opcode, view.RoomId);
        if (scoringTeam != 0)
            await scoreboard.AddTeamPointAsync(view.RoomId, view.Generation, scoringTeam, ct).ConfigureAwait(false);
    }

    private static bool IsInitialized(RoomSeatView seat, ISession transport) =>
        ReferenceEquals(seat.Relay, transport) && seat.RelayInitialized
        && !seat.Game.Connection.IsClosed && !transport.Connection.IsClosed;
}
