using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.InGame.Sessions;

namespace BubbleFighter.Fantasy.InGame.Handlers;

internal sealed partial class RoundFlow
{
    private bool UsesNativeLife(PlayRoom room) => _options.NativeLifeModeIds.Contains(room.ModeId)
        || _options.AiModeIds.Contains(room.ModeId);

    private static LifeParticipant? LifeActor(PlayRoom room, int actor)
    {
        var human = room.Seats.FirstOrDefault(seat => RoomRegistry.SceneEntity(seat.Slot) == actor);
        if (human is not null) return new(human, null);
        var bot = room.Bots.FirstOrDefault(bot => RoomRegistry.SceneEntity(bot.Appearance.Slot) == actor);
        return bot is null ? null : new(null, bot);
    }

    // kr-1256 sub_6D5010 can request a reset for a controlled character; its life belongs to that actor.
    private sealed class LifeParticipant(RoomSeat? human, RoomBot? bot)
    {
        internal int Slot => human?.Slot ?? bot!.Appearance.Slot;
        internal int Team => human?.Team ?? bot!.Appearance.Team;
        internal SeatLife Life
        {
            get => human?.Life ?? bot!.Life;
            set { if (human is not null) human.Life = value; else bot!.Life = value; }
        }
        internal bool DeathCleaned
        {
            get => human?.DeathCleaned ?? bot!.DeathCleaned;
            set { if (human is not null) human.DeathCleaned = value; else bot!.DeathCleaned = value; }
        }
        internal ISession? ResetRelay
        {
            get => human is not null ? human.ResetRelay : bot!.ResetRelay;
            set { if (human is not null) human.ResetRelay = value; else bot!.ResetRelay = value; }
        }
        internal void ClearMovement()
        {
            if (human is not null) human.Movement = null; else bot!.Movement = null;
        }
    }

    private static RoomMemberView HumanScoreMember(RoomSeat seat) => new(seat.Slot, seat.UserNum,
        seat.Nickname, seat.Level, seat.Team, seat.Ready, seat.CharacterId, seat.EquippedItemIds ?? []);

    private static RoomMemberView? ScoreMember(PlayRoom room, int slot) =>
        room.Seats.FirstOrDefault(seat => seat.Slot == slot) is { } human
            ? HumanScoreMember(human) : room.BotAt(slot)?.Appearance;

    private static RoomMemberView? ScoreReporter(PlayRoom room, RoomSeat controller, int reportedSlot, bool requiresOwnership)
    {
        if (!requiresOwnership || reportedSlot == controller.Slot)
            return HumanScoreMember(controller);
        return room.BotAt(reportedSlot)?.Appearance is { Ai: { } ai } bot
            && ai.ControllerSlot == controller.Slot && ai.ControllerUserNum == controller.UserNum ? bot : null;
    }
}
