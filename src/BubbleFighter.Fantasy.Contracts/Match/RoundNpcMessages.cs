using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Match;

/// <summary>The host-driven NPC operations a round's native wave manager and spawners exchange with the server.</summary>
public enum RoundNpcOperation
{
    /// <summary>Starts the next wave.</summary>
    StartWave,
    /// <summary>Clears the current wave.</summary>
    ClearWave,
    /// <summary>Completes the wave sequence.</summary>
    CompleteWaves,
    /// <summary>Replaces the wave's NPC spawn descriptions.</summary>
    SpawnTable,
    /// <summary>Activates a group of NPC spawns.</summary>
    SpawnGroup,
    /// <summary>Creates one member of a spawn group.</summary>
    SpawnMember,
    /// <summary>Creates one preset NPC actor.</summary>
    SpawnActor,
    /// <summary>Applies a recorded movement replay to an NPC.</summary>
    ReplayActor,
    /// <summary>Assigns a wave NPC to its native spawn group.</summary>
    AssignSpawnGroup,
}

/// <summary>A dialect-validated NPC operation from a round's host.</summary>
/// <param name="Operation">The native operation to acknowledge.</param>
/// <param name="Payload">The complete validated body, preserved for the native consuming path.</param>
public sealed record RoundNpcReported(RoundNpcOperation Operation, ReadOnlyMemory<byte> Payload) : IMessage
{
    /// <summary>
    /// Gets the dialect-validated peer form when ordinary rounds transport this operation over their match links.
    /// Solo tutorials still acknowledge the operation on their game connection.
    /// </summary>
    public RoundFeedSent? PeerFeed { get; init; }
}

/// <summary>Applies a validated NPC operation to one member of the host's round.</summary>
/// <param name="Operation">The native operation to acknowledge.</param>
/// <param name="Payload">The original body without reserialization of unknown fields.</param>
public sealed record RoundNpcAcknowledged(RoundNpcOperation Operation, ReadOnlyMemory<byte> Payload) : IMessage;
