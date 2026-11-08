using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Rooms;

namespace BubbleFighter.Fantasy.Contracts.Match;

/// <summary>Describes the next playable round within a match without exposing native packet fields.</summary>
/// <param name="RoundIndex">Zero-based round number within the current match.</param>
/// <param name="AttackingTeam">The team assigned the attacking role in this round.</param>
public sealed record RoundSeriesEntry(int RoundIndex, int AttackingTeam);

/// <summary>Advertises the modes whose native entry path supports continuing a multi-round match.</summary>
public interface IRoundSeriesEntrySender : IMessageSender<RoomMatchStarting>
{
    /// <summary>Gets the native match's required wins; existing senders retain their best-of-three format.</summary>
    int RoundsToWin => 2;

    /// <summary>Checks whether this sender has an audited round-index and role projection for the supplied mode.</summary>
    /// <param name="modeId">The native mode selected by the room.</param>
    /// <returns>True only when the complete series entry projection is available.</returns>
    bool SupportsMode(int modeId);
}
