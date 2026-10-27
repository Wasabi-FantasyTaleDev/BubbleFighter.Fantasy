using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.InGame.Sessions;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.InGame.Handlers;

/// <summary>
/// The zombie battle: humans against a zombie king and the zombinis it makes. The client keeps no rules of its
/// own (kr-812 ZombieModeMgr): every member's client treats the first seat the server moves to the zombie team
/// as the zombie king and every later one as a zombini (kr-812 sub_776870 counts the zombie team in
/// sub_776770 and plays the king's transformation when it holds one player), and a seat already on the zombie
/// team that is moved there again revives as a zombie.
/// </summary>
internal sealed partial class RoomFlow
{
    /// <summary>The humans' team: kr-812 sub_7694F0 counts the living players of team 1 as the last humans.</summary>
    internal const byte ZombiePvpHumanTeam = 1;

    /// <summary>The zombies' team, king and zombinis alike: kr-812 sub_776870 acts on team 14 only.</summary>
    internal const byte ZombiePvpZombieTeam = 14;

    internal bool IsZombiePvp(PlayRoom room) => _options?.ZombiePvpModeIds.Contains(room.ModeId) == true;

    /// <summary>
    /// Turns a random human into the zombie king once the configured opening of the round has passed. The wait
    /// runs on <see cref="PlayRoom.ZombieKingTask"/> so its end is observable; the delay delegate is the one the
    /// room flow was built with, which is the real-time wait outside tests.
    /// </summary>
    /// <param name="room">The room whose round just started.</param>
    /// <param name="generation">The round's start generation; a later round cancels this one's pick.</param>
    internal void ScheduleZombieKing(PlayRoom room, int generation)
    {
        if (!IsZombiePvp(room))
            return;
        room.ZombieKingTask = BackgroundWork.Detached(() => PickZombieKingAsync(room, generation));
    }

    private async Task PickZombieKingAsync(PlayRoom room, int generation)
    {
        try
        {
            await _zombieKingDelay(TimeSpan.FromMilliseconds(_options!.ZombieKingDelayMs), _lifetime).ConfigureAwait(false);
            await room.Executor.ExecuteAsync(async operationCt =>
            {
                RoomSeat[] members;
                RoomTeamConverted change;
                lock (room.Gate)
                {
                    // A player who died before the opening ended already became the king.
                    if (room.Phase != RoomPhase.Playing || room.StartGeneration != generation || room.ZombieKingSlot is not null)
                        return;
                    var humans = room.Seats.Where(seat => seat.Team == ZombiePvpHumanTeam && !seat.Game.Connection.IsClosed).ToArray();
                    if (humans.Length == 0)
                        return;
                    var king = humans[Random.Shared.Next(humans.Length)];
                    king.Team = ZombiePvpZombieTeam;
                    room.ZombieKingSlot = king.Slot;
                    change = new(king.Slot, ZombiePvpZombieTeam);
                    members = [.. room.Seats];
                }
                _logger.LogInformation("Room {Room}: seat {Seat} is the zombie king.", room.Id, change.Slot);
                await PublishZombieChangeAsync(room, members, change, operationCt).ConfigureAwait(false);
            }, _lifetime).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException)
        {
            // The room closed during the opening.
        }
        catch (Exception failure)
        {
            _logger.LogError(failure, "Room {Room}: choosing the zombie king failed.", room.Id);
        }
    }

    /// <summary>
    /// Answers the death report of a zombie battle: kr-812 sub_76C090 sends gcpRoomChangeTeam on every death of
    /// the local player. A dead human becomes a zombini (or the king, when it is the first), a dead zombini comes
    /// back without limit, and a dead king gives the humans the round.
    /// </summary>
    private Task HandleZombiePvpDeathAsync(PlayRoom room, ISession session, CancellationToken ct) =>
        room.Executor.ExecuteAsync(async operationCt =>
        {
            RoomSeat[] members;
            RoomTeamConverted? change = null;
            lock (room.Gate)
            {
                if (room.Phase != RoomPhase.Playing || room.DecidedWinnerTeam is not null
                    || room.FindByGame(session.Id) is not { } seat)
                    return;
                if (seat.Team == ZombiePvpZombieTeam && seat.Slot == room.ZombieKingSlot)
                {
                    room.DecidedWinnerTeam = ZombiePvpHumanTeam;
                    _logger.LogInformation("Room {Room}: the zombie king (seat {Seat}) was defeated.", room.Id, seat.Slot);
                }
                else if (seat.Team is ZombiePvpHumanTeam or ZombiePvpZombieTeam)
                {
                    // The report names no killer (kr-812 sub_76C090 sends an empty body). Humans share one team,
                    // so only a zombie can finish one off; a human who falls to the map turns as well.
                    if (seat.Team == ZombiePvpHumanTeam && room.ZombieKingSlot is null)
                        room.ZombieKingSlot = seat.Slot;
                    seat.Team = ZombiePvpZombieTeam;
                    change = new(seat.Slot, ZombiePvpZombieTeam);
                }
                else
                    return;
                seat.AwaitingRespawn = false;
                seat.InstantRevivalPending = false;
                seat.LastDeathActor = null;
                members = [.. room.Seats];
            }
            if (change is { } converted)
                await PublishZombieChangeAsync(room, members, converted, operationCt).ConfigureAwait(false);
            else if (TeamConvertedDuringRound is { } decided)
                await decided(room, operationCt).ConfigureAwait(false);
        }, ct);

    private async Task PublishZombieChangeAsync(PlayRoom room, RoomSeat[] members, RoomTeamConverted change, CancellationToken ct)
    {
        foreach (var member in members)
            await _teamConverted!.SendAsync(member.Game, change, ct).ConfigureAwait(false);
        if (TeamConvertedDuringRound is { } converted)
            await converted(room, ct).ConfigureAwait(false);
    }
}
