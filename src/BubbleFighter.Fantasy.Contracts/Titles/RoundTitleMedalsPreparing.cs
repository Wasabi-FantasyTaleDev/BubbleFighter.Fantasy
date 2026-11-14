using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Match;

namespace BubbleFighter.Fantasy.Contracts.Titles;

/// <summary>
/// Resolves medal credits from a frozen terminal match snapshot before the account settlement is committed.
/// Handlers must only calculate a plan: competing preparations can run before the match adopts one plan.
/// </summary>
/// <param name="Protocol">The game protocol whose title catalogue defines the medal identities.</param>
/// <param name="MatchId">The immutable round identity shared by every settlement retry.</param>
/// <param name="UserNum">The authenticated account whose accepted counters are supplied.</param>
/// <param name="ModeId">The native mode of the completed match.</param>
/// <param name="Outcome">The final outcome accepted for this participant.</param>
/// <param name="Reason">The terminal reason accepted by the match logic.</param>
/// <param name="Stats">The participant's copied terminal counters, never a fresh client report.</param>
/// <param name="ParticipantCount">The number of admitted human participants in the frozen terminal match.</param>
public sealed record RoundTitleMedalsPreparing(string Protocol, string MatchId, long UserNum, int ModeId,
    RoundOutcome Outcome, RoundEndReason Reason, RoundSeatStats Stats, int ParticipantCount) : IEvent;
