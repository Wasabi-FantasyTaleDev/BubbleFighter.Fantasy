using BubbleFighter.Fantasy.Contracts.Progress;
using BubbleFighter.Fantasy.InGame.Data;

namespace BubbleFighter.Fantasy.InGame.Sessions;

// A terminal snapshot owns the original seat identity even if the room later detaches that seat.
internal sealed record BattleSettlementPlan(RoomSeat Seat, RoundReward Reward, RoundBattleRecord? Record)
{
    private int _levelAnnouncementStarted;

    internal bool TryBeginLevelAnnouncement() => Interlocked.Exchange(ref _levelAnnouncementStarted, 1) == 0;
}
