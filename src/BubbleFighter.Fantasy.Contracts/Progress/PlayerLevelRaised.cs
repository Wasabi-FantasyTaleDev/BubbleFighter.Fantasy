using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.Contracts.Progress;

/// <summary>
/// A player's stored level went up because a round recorded its experience. Raised once per recording that crossed one
/// or more levels, after the new level is stored, so a subscriber can grant what each crossed level is worth without
/// the round knowing what that is. A subscriber must tolerate the same crossing arriving twice (a retried recording)
/// and decide for itself what makes a grant happen only once.
/// </summary>
/// <param name="Protocol">The game connection protocol of the round's build, for example the one a shop row serves.</param>
/// <param name="UserNum">Login <c>players.user_num</c> of the player.</param>
/// <param name="OldLevel">The level stored before the recording, at least 1.</param>
/// <param name="NewLevel">The level stored after the recording, greater than <paramref name="OldLevel"/>.</param>
public sealed record PlayerLevelRaised(string Protocol, long UserNum, int OldLevel, int NewLevel) : IEvent;
