using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Match;

/// <summary>A native stage outcome whose meaning is selected by the active tutorial or round.</summary>
/// <param name="Outcome">The native stage result word, restricted to zero or one by the dialect.</param>
public sealed record RoundStageResultReported(int Outcome) : IMessage;
