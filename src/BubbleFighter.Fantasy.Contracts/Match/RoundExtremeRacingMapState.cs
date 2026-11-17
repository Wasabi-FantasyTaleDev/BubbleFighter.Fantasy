using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Match;

/// <summary>Shared native bomb patterns, stone target seed and complete vanish route choices for one racing match.</summary>
/// <param name="SecondBombPattern">Pattern index for the second bomb floor, between zero and nine.</param>
/// <param name="ThirdBombPattern">Pattern index for the third bomb floor, between zero and nine.</param>
/// <param name="StoneSeed">Non-negative seed copied into every native stone emitter before its stage starts.</param>
/// <param name="VanishSelections">One zero-based route index for each loaded native vanish group.</param>
public sealed record RoundExtremeRacingMapState(int SecondBombPattern, int ThirdBombPattern, int StoneSeed,
    IReadOnlyList<int> VanishSelections) : IMessage;
