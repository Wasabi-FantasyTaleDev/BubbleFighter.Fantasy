using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.InGame.Data;
using BubbleFighter.Fantasy.InGame.Sessions;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.InGame.Handlers;

internal sealed partial class RoundFlow
{
    internal Func<string, ZombiePveBoss?>? ZombiePveBossFor { get; set; }
    private bool IsModernZombiePve(PlayRoom room) => ZombiePveBossFor is not null && room.Tutorial is null
        && !room.IsUserHost && room.ModeId == (int)GameModeId.ZombiePve;

    /// <summary>
    /// Answers a horde round host's wave manager. Every member receives the wave and table replies (the host alone
    /// the all-waves reply outside the audited assault): the host's
    /// latches wait for them (kr-1256 sub_7BAD80 clears +151 on 904, kr-1256 sub_7B90D0 clears +152 on 909) and
    /// the other members install the same table from 909. A member's 904 does not make it send a table: kr-1256
    /// sub_7BAD80 builds one only when kr-1256 sub_9EBF60 finds the local seat is the host. Audited concrete
    /// spawns use the match feed; their master already executes its own queue (kr-1256 sub_7AB190).
    /// </summary>
    internal async ValueTask HandleNpcAsync(ISession session, RoundNpcReported report,
        IMessageSender<RoundNpcAcknowledged> sender, CancellationToken ct)
    {
        // The wave-failure notice is answered only in Pirate Assault; the result handler drops it in every other
        // mode. The all-waves notice keeps its host-only route outside Pirate Assault.
        if (report.Operation == RoundNpcOperation.FailWaves || (report.Operation == RoundNpcOperation.CompleteWaves
            && _registry.TryGame(session) is { } participant && participant.Room.ModeId == (int)GameModeId.PirateAssaultSeason3))
        {
            await RememberPirateAssaultResultAsync(session, report, sender, ct).ConfigureAwait(false);
            return;
        }
        var room = report.PeerFeed is null ? FindGameReportRoom(session) : _registry.TryGet(session.Id);
        if (room is null) return;
        (int Generation, int MasterRevision)? assault = null;
        int generation;
        lock (room.Gate)
        {
            generation = room.StartGeneration;
            if (room.ModeId == (int)GameModeId.PirateAssaultSeason3)
            {
                if (!IsPirateAssaultWaveSource(room, session, report.PeerFeed is not null)) return;
                assault = (room.StartGeneration, room.PirateAssaultMasterRevision);
            }
        }
        await room.Executor.ExecuteAsync(async operationCt =>
        {
            RoomSeat[] members;
            lock (room.Gate)
            {
                if (assault is { } stamp)
                {
                    if (room.StartGeneration != stamp.Generation || room.PirateAssaultMasterRevision != stamp.MasterRevision
                        || !IsPirateAssaultWaveSource(room, session, report.PeerFeed is not null)) return;
                }
                else if (room.ModeId == (int)GameModeId.PirateAssaultSeason3) return;
                var reporter = room.FindByGame(session.Id)
                    ?? (report.PeerFeed is not null
                        ? room.Seats.FirstOrDefault(seat => ReferenceEquals(MatchLink(room, seat), session)) : null);
                // kr-812 sub_660D50 builds the NPC context for 9/101/102/103; kr-812 sub_A087F0 runs mode 102's waves.
                var waveTable = report.Operation == RoundNpcOperation.SpawnTable
                    && (GameModeId)room.ModeId is GameModeId.Npc or GameModeId.HordeZhe;
                // The map's master spawner creates concrete actors locally: kr-1256 sub_9027F0,
                // cn-774 sub_AF65B0 and tw-454 sub_85EA70. This is not a horde wave/table exchange.
                var zombieActor = (GameModeId)room.ModeId == GameModeId.ZombiePve
                    && report.Operation == RoundNpcOperation.SpawnActor && report.PeerFeed is not null
                    && !room.RoundCancelled && room.StartGeneration == generation
                    && !session.Connection.IsClosed && reporter is not null
                    && (ReferenceEquals(reporter.Game, session) || ReferenceEquals(MatchLink(room, reporter), session));
                if (room.Phase is not (RoomPhase.Starting or RoomPhase.Playing) || reporter?.Slot != room.MasterSlot
                    || (GameMode.Resolve(room.ModeId).Family != GameModeFamily.Horde && !waveTable && !zombieActor))
                {
                    _logger.LogWarning("Session {Id} NPC {Operation} rejected in room {Room}: phase={Phase}, mode={Mode}.",
                        session.Id, report.Operation, room.Id, room.Phase, room.ModeId);
                    return;
                }
                if (zombieActor && room.Phase == RoomPhase.Playing && IsModernZombiePve(room)
                    && ZombiePveBossFor?.Invoke(room.RoundMapKey) is { } boss && report.MapPropertyIndex == boss.Property)
                    room.ZombiePveBossSpawn = (generation, reporter!.Game, boss.EarliestMapTimeMs);
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
            // Other modes retain the host-only completion route.
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
