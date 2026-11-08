using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.InGame.Sessions;

namespace BubbleFighter.Fantasy.InGame.Handlers;

internal sealed class GameSessionStateReader(RoomRegistry registry, CancellationToken lifetime,
    Func<bool> canDepart) : IGameSessionStateReader
{
    public GameSessionState? Describe(ISession session)
    {
        if (lifetime.IsCancellationRequested || session.Connection.IsClosed || registry.TryGet(session.Id) is not { } room)
            return null;
        lock (room.Gate)
        {
            if (!ReferenceEquals(room.FindByGame(session.Id)?.Game, session)) return null;
            if (!session.TryFeature<IPlayerSession>(out var player) || player.Phase == SessionPhase.Migrating)
                return GameSessionState.Unavailable;
            if (room.Phase == RoomPhase.Closed || room.RoundCancelled) return GameSessionState.Unavailable;
            if (room.Tutorial is not null) return new("tutorial", false, false, true, room.ModeId);
            if (room.ModeId == RoomRegistry.TrainingModeId) return new("training", false, false, false, room.ModeId);
            if (!room.IsMatchRoom) return GameSessionState.Unavailable;
            if (room.Phase == RoomPhase.Playing)
                return player.Phase is SessionPhase.Room or SessionPhase.Match
                    ? new("match", true, canDepart(), false, room.ModeId) : GameSessionState.Unavailable;
            var state = room.Phase switch
            {
                RoomPhase.Waiting => "room_waiting",
                RoomPhase.Starting when room.CountdownGeneration == room.StartGeneration => "room_countdown",
                RoomPhase.Starting => "room_loading",
                RoomPhase.Result => "result",
                _ => "unknown",
            };
            return new(state, false, false, false, room.ModeId);
        }
    }
}
