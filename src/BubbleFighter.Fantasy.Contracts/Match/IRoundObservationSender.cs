using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Match;

/// <summary>
/// Encodes dead-player observation and identifies the modes whose client consumers support it.
/// Publishing this service does not disable respawning; the round policy must request that separately.
/// </summary>
public interface IRoundObservationSender : IMessageSender<RoundPlayerObserved>
{
    /// <summary>Reports whether this protocol's audited client consumer accepts observation in the given mode.</summary>
    /// <param name="modeId">The native game mode selected by the room.</param>
    /// <returns>True only when the sender can put a dead player into observation in that mode.</returns>
    bool SupportsMode(int modeId);
}
