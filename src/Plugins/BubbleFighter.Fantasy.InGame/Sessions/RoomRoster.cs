using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.InGame.Data;
using BubbleFighter.Fantasy.InGame.Dialects;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.InGame.Sessions;

/// <summary>
/// Publishes the rooms to rows that act on in-round traffic without owning the rooms: read-only
/// roster views, and the team points of the running round, which stay on the room so the result
/// screen and the rewards read the same totals.
/// </summary>
/// <param name="registry">The room registry.</param>
/// <param name="logger">The row logger.</param>
/// <param name="lifetime">Cancellation when the owning plugin unloads.</param>
internal sealed class RoomRoster(RoomRegistry registry, ILogger logger, CancellationToken lifetime) : IRoomRoster, IRoundScoreboard
{
    /// <inheritdoc />
    public RoomRosterView? FindBySession(long sessionId) => View(registry.TryGet(sessionId));

    /// <inheritdoc />
    public RoomRosterView? Find(int roomId) => View(registry.Find(roomId));

    private static RoomRosterView? View(PlayRoom? room)
    {
        if (room is null)
            return null;
        lock (room.Gate)
        {
            RoomStage? stage = room.Phase switch
            {
                RoomPhase.Waiting => RoomStage.Waiting,
                RoomPhase.Starting => RoomStage.Starting,
                RoomPhase.Playing => RoomStage.Playing,
                RoomPhase.Result => RoomStage.Result,
                _ => null,
            };
            if (stage is null)
                return null;
            var seats = room.Seats
                .Select(seat => new RoomSeatView(seat.Slot, seat.Team, seat.UserNum, seat.Game, seat.Relay, seat.RelayInitialized))
                .ToArray();
            var clientRunsNpcs = GameMode.Resolve(room.ModeId).Family == GameModeFamily.Horde;
            return new RoomRosterView(room.Id, stage.Value, room.StartGeneration, room.ModeId, seats, clientRunsNpcs);
        }
    }

    /// <inheritdoc />
    public async ValueTask AddTeamPointAsync(int roomId, int generation, byte team, CancellationToken ct)
    {
        if (team is not (1 or 2))
            throw new ArgumentOutOfRangeException(nameof(team), team, "Only teams 1 and 2 score.");
        if (registry.Find(roomId) is not { } room)
            return;
        RoomSeat[] recipients;
        lock (room.Gate)
        {
            if (room.StartGeneration != generation || room.Phase != RoomPhase.Playing) return;
            if (team == 1) room.BlueScore = checked((ushort)(room.BlueScore + 1));
            else room.RedScore = checked((ushort)(room.RedScore + 1));
            recipients = room.Seats.ToArray();
            logger.LogInformation("Room {Room} team totals {Blue}:{Red} in generation {Generation}.",
                room.Id, room.BlueScore, room.RedScore, generation);
        }
        foreach (var seat in recipients)
        {
            for (byte scored = 1; scored <= 2; scored++)
            {
                ct.ThrowIfCancellationRequested();
                lifetime.ThrowIfCancellationRequested();
                try
                {
                    ValueTask send;
                    lock (room.Gate)
                    {
                        if (room.StartGeneration != generation || room.Phase != RoomPhase.Playing) return;
                        if (!room.Seats.Contains(seat) || seat.Game.Connection.IsClosed) break;
                        // Read the latest total before enqueueing, so concurrent points cannot publish an older score.
                        send = seat.Game.SendAsync(MatchLife1256.ScoreUpdate,
                            MatchLife1256.TeamScore(scored, scored == 1 ? room.BlueScore : room.RedScore), ct);
                    }
                    await send.ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && seat.Game.Connection.IsClosed)
                {
                    logger.LogDebug(ex, "Score recipient {Session} disconnected.", seat.Game.Id);
                    break;
                }
            }
        }
    }
}
