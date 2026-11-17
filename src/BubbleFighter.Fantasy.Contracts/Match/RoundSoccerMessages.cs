using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Match;

/// <summary>
/// The soccer host reports a goal on the full score route (C2S <c>gcpGameUpdateScore</c>): kr-1256 sub_6E6F90 finds
/// the ball inside one of the two goal boxes and sends the box index with the last player who touched the ball
/// (kr-1256 sub_6DDBA0); cn-774 sub_8A75F0 and tw-454 sub_71A9A0 do the same through their own score builders.
/// </summary>
/// <param name="GoalIndex">The goal box the ball entered: 0 is the first box, 1 the second.</param>
/// <param name="ScorerSlot">The seat that touched the ball last, or a negative value when none did.</param>
public sealed record RoundSoccerGoalReported(int GoalIndex, int ScorerSlot) : IMessage;
