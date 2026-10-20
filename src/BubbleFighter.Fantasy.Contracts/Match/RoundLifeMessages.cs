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

    /// <summary>The sender's character completed the reset the server granted, on the relay of that grant.</summary>
    ResetConfirmed,
}

/// <summary>
/// A life report the dialect read from a gameplay feed packet; the packet itself still goes to the other players.
/// </summary>
/// <param name="Event">What happened.</param>
/// <param name="Actor">The character it happened to.</param>
public sealed record RoundLifeReported(RoundLifeEvent Event, int Actor) : IMessage
{
    /// <summary>Gets the character that popped the bubble, for <see cref="RoundLifeEvent.Finished"/>.</summary>
    public int? Initiator { get; init; }
}

/// <summary>A dead player asks for its character to be reset into the round.</summary>
/// <param name="Actor">The character the request names; it must be the sender's own.</param>
/// <param name="Request">The request body, which the grant repeats.</param>
public sealed record RoundResetRequested(int Actor, ReadOnlyMemory<byte> Request) : IMessage;

/// <summary>The server grants a reset; the client then resets its character and confirms on the relay.</summary>
/// <param name="Request">The request body being granted.</param>
public sealed record RoundResetGranted(ReadOnlyMemory<byte> Request) : IMessage;
