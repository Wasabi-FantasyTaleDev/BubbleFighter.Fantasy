using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Match;

/// <summary>
/// The teaching-only NPC operations of the initial tutorial. Ordinary waves and spawns are
/// <see cref="RoundNpcOperation"/>, which the tutorial consumes like any other round.
/// </summary>
public enum InitialTutorialNpcOperation
{
    /// <summary>Ends the scripted sequence after failure.</summary>
    FailWaves,
    /// <summary>Changes whether a scripted NPC stays at its spawn point.</summary>
    SetSpawnPointReplay,
}

/// <summary>A dialect-validated teaching-only NPC operation from the authenticated solo tutorial host.</summary>
/// <param name="Operation">The native operation to acknowledge.</param>
/// <param name="Payload">The complete validated body, preserved for the native consuming path.</param>
public sealed record InitialTutorialNpcReported(InitialTutorialNpcOperation Operation,
    ReadOnlyMemory<byte> Payload) : IMessage;

/// <summary>Applies a validated teaching-only NPC operation to the player who owns the initial tutorial.</summary>
/// <param name="Operation">The native operation to acknowledge.</param>
/// <param name="Payload">The original body without reserialization of unknown fields.</param>
public sealed record InitialTutorialNpcAcknowledged(InitialTutorialNpcOperation Operation,
    ReadOnlyMemory<byte> Payload) : IMessage;
