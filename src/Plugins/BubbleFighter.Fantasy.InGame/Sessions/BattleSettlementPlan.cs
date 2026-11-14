using BubbleFighter.Fantasy.Contracts.Progress;
using BubbleFighter.Fantasy.Contracts.Titles;
using BubbleFighter.Fantasy.InGame.Data;

namespace BubbleFighter.Fantasy.InGame.Sessions;

// A terminal snapshot owns its prepared amounts and original seat even after a result-screen departure.
internal sealed record BattleSettlementPlan(RoomSeat Seat, RoundReward Reward, RoundBattleRecord? Record,
    Exception? PreparationFailure = null)
{
    internal TitleMedalGrantCommand? TitleMedals { get; init; }

    private int _levelAnnouncementStarted;
    private RoundReward? _settledReward;

    internal RoundReward? SettledReward => Volatile.Read(ref _settledReward);

    internal RoundReward RememberSettledReward(RoundReward reward) =>
        Interlocked.CompareExchange(ref _settledReward, reward, null) ?? reward;

    internal bool TryBeginLevelAnnouncement() => Interlocked.Exchange(ref _levelAnnouncementStarted, 1) == 0;
}

// Preparation runs outside the room gate over inputs copied together while the terminal state was current.
internal sealed record BattleSettlementCandidate(RoomSeat Seat, RoundReward Reward, RoundBattleRecord? Record,
    RoundRewardPreparing Preparation, RoundTitleMedalsPreparing MedalPreparation);
