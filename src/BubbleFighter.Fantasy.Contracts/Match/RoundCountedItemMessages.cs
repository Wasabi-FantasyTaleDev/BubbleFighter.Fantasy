using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Match;

/// <summary>Installs the ordered native cooperative item slots before the round begins.</summary>
public sealed record RoundCountedItemsPrepared(IReadOnlyList<int> Kinds) : IMessage;

/// <summary>Requests consumption of one counted item through its native slot index.</summary>
public sealed record RoundCountedItemUseRequested(int ItemSlot) : IMessage;

/// <summary>
/// Confirms an immediate item effect by the player controlling the specified seat. Some native clients clear the
/// currently held item while applying a fixed pickup effect; the optional retained pair lets that dialect restore
/// the prior slot after the effect without changing the shared room state.
/// </summary>
public sealed record RoundCountedItemUsed(int Seat, int Kind) : IMessage
{
    /// <summary>Gets the occupied native slot to restore after the effect, paired with <see cref="RetainedItemKind"/>.</summary>
    public int? RetainedItemSlot { get; init; }

    /// <summary>Gets the held item kind to restore; null means the effect has no held-item restoration.</summary>
    public int? RetainedItemKind { get; init; }
}
