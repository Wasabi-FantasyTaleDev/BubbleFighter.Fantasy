using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Match;

/// <summary>Publishes the saved successful completion of a build's supplementary practice.</summary>
/// <param name="Cleared">Whether the authenticated player successfully completed the practice.</param>
public sealed record TutorialPracticeProgress(bool Cleared) : IMessage;
