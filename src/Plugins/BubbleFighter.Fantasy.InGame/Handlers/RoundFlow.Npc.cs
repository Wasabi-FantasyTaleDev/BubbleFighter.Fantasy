using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.InGame.Data;
using BubbleFighter.Fantasy.InGame.Sessions;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.InGame.Handlers;

internal sealed partial class RoundFlow
{
    /// <summary>
    /// Answers a horde round host's wave manager. Every member receives the wave and table replies (the host alone
    /// the all-waves reply): the host's
    /// latches wait for them (kr-1256 sub_7BAD80 clears +151 on 904, kr-1256 sub_7B90D0 clears +152 on 909) and
    /// the other members install the same table from 909. A member's 904 does not make it send a table: kr-1256
    /// sub_7BAD80 builds one only when kr-1256 sub_9EBF60 finds the local seat is the host. Audited concrete
    /// spawns use the match feed; their master already executes its own queue (kr-1256 sub_7AB190).
    /// </summary>
    internal async ValueTask HandleNpcAsync(ISession session, RoundNpcReported report,
        IMessageSender<RoundNpcAcknowledged> sender, CancellationToken ct)
    {
        var room = report.PeerFeed is null ? FindGameReportRoom(session) : _registry.TryGet(session.Id);
        if (room is null) return;
        await room.Executor.ExecuteAsync(async operationCt =>
        {
            RoomSeat[] members;
            lock (room.Gate)
            {
                var reporter = room.FindByGame(session.Id)
                    ?? (report.PeerFeed is not null
                        ? room.Seats.FirstOrDefault(seat => ReferenceEquals(MatchLink(room, seat), session)) : null);
                // kr-812 sub_660D50 builds the NPC context for 9/101/102/103; kr-812 sub_A087F0 runs mode 102's waves.
                var waveTable = report.Operation == RoundNpcOperation.SpawnTable
                    && (GameModeId)room.ModeId is GameModeId.Npc or GameModeId.HordeZhe;
                if (room.Phase is not (RoomPhase.Starting or RoomPhase.Playing) || reporter?.Slot != room.MasterSlot
                    || (GameMode.Resolve(room.ModeId).Family != GameModeFamily.Horde && !waveTable))
                {
                    _logger.LogWarning("Session {Id} NPC {Operation} rejected in room {Room}: phase={Phase}, mode={Mode}.",
                        session.Id, report.Operation, room.Id, room.Phase, room.ModeId);
                    return;
                }
                members = [.. room.Seats];
            }
            if (report.PeerFeed is { } peer)
            {
                if (_feed is null)
                    throw new InvalidOperationException("NPC peer transport requires the round feed sender.");
                await ForwardFeedAsync(room, session, peer, operationCt,
                    () => room.Seats.Any(seat => seat.Slot == room.MasterSlot
                        && (ReferenceEquals(seat.Game, session) || ReferenceEquals(MatchLink(room, seat), session))))
                    .ConfigureAwait(false);
                return;
            }
            if (report.Operation is not (RoundNpcOperation.StartWave or RoundNpcOperation.ClearWave
                or RoundNpcOperation.CompleteWaves or RoundNpcOperation.SpawnTable))
            {
                _logger.LogInformation("Room {Room} host NPC {Operation} ({Bytes} bytes) on the game connection is not relayed.",
                    room.Id, report.Operation, report.Payload.Length);
                return;
            }
            // Only the host's handling of the all-waves reply is proven; a peer's is not.
            if (report.Operation == RoundNpcOperation.CompleteWaves)
                members = [.. members.Where(member => member.Game.Id == session.Id)];
            var reply = new RoundNpcAcknowledged(report.Operation, report.Payload);
            foreach (var member in members)
                if (!member.Game.Connection.IsClosed)
                    await sender.SendAsync(member.Game, reply, operationCt).ConfigureAwait(false);
            _logger.LogInformation("Room {Room} NPC {Operation} answered to {Count} members.",
                room.Id, report.Operation, members.Length);
            // The host's all-waves report is the cleared battle, as gcpReqEndAllWave is on the legacy path.
            if (report.Operation == RoundNpcOperation.CompleteWaves && _ended is not null)
                await EndRoundAsync(room, RoundEndReason.WavesCleared, operationCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }
}
