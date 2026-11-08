using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Match;

/// <summary>What a player's client reported about a character's life in a build whose clients decide it.</summary>
public enum RoundLifeEvent
{
    /// <summary>The sender's own character was trapped in a bubble.</summary>
    Bubbled,

    /// <summary>The sender's own character died without a bubble.</summary>
    Died,

    /// <summary>A bubbled character (<see cref="RoundLifeReported.Actor"/>) was released alive.</summary>
    Released,

    /// <summary>The sender's own bubble ran out and its character died.</summary>
    BubbleExpired,

    /// <summary>
    /// A bubbled character (<see cref="RoundLifeReported.Actor"/>) was popped by
    /// <see cref="RoundLifeReported.Initiator"/>; it dies and the initiator's team scores.
    /// </summary>
    Finished,

    /// <summary>The sender's dead character finished its death presentation, so it may ask for a reset.</summary>
    DeathCleaned,

    /// <summary>
    /// The sender's character completed the respawn the server granted (<see cref="RoundPlayerRespawned"/>), on the
    /// match link of that grant.
    /// </summary>
    ResetConfirmed,
}

/// <summary>
/// A life report the dialect read from a gameplay feed packet; the packet itself still goes to the other players.
/// </summary>
/// <param name="Event">What happened.</param>
/// <param name="Actor">The character it happened to.</param>
public sealed record RoundLifeReported(RoundLifeEvent Event, int Actor) : IMessage
{
    /// <summary>
    /// Gets the source character for a bubble or direct death, the rescuer for a release, or the popper for a finish,
    /// when the native report names one. A generic life notification carries no attribution.
    /// </summary>
    public int? Initiator { get; init; }
}
