using BubbleFighter.Fantasy.Contracts.Progress;

namespace BubbleFighter.Fantasy.InGame;

internal sealed class PreparedRoundRewards(string protocol) : IPreparedRoundRewards
{
    /// <summary>Gets the game protocol whose terminal settlements prepare rewards before persistence.</summary>
    public string Protocol { get; } = protocol;
}
