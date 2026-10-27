using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Match;

/// <summary>The native NPC operation an initial tutorial host asks to apply locally.</summary>
public enum InitialTutorialNpcOperation
{
    /// <summary>Starts the next scripted wave.</summary>
    StartWave,
    /// <summary>Clears the current scripted wave.</summary>
    ClearWave,
    /// <summary>Completes the scripted wave sequence.</summary>
    CompleteWaves,
    /// <summary>Ends the scripted sequence after failure.</summary>
    FailWaves,
    /// <summary>Replaces the wave's NPC spawn descriptions.</summary>
    SpawnTable,
    /// <summary>Activates a group of scripted NPC spawns.</summary>
    SpawnGroup,
    /// <summary>Creates one member of a scripted spawn group.</summary>
    SpawnMember,
    /// <summary>Creates one preset NPC actor.</summary>
    SpawnActor,
    /// <summary>Applies a scripted movement replay to a tutorial NPC.</summary>
    ReplayActor,
    /// <summary>Assigns a wave NPC to its native spawn group.</summary>
    AssignSpawnGroup,
    /// <summary>Changes whether a scripted NPC stays at its spawn point.</summary>
    SetSpawnPointReplay,
}

/// <summary>A dialect-validated NPC operation from the authenticated solo tutorial host.</summary>
/// <param name="Operation">The native operation to acknowledge.</param>
/// <param name="Payload">The complete validated body, preserved for the native consuming path.</param>
public sealed record InitialTutorialNpcReported(InitialTutorialNpcOperation Operation,
    ReadOnlyMemory<byte> Payload) : IMessage;

/// <summary>Applies a validated NPC operation to the player who owns the initial tutorial.</summary>
/// <param name="Operation">The native operation to acknowledge.</param>
/// <param name="Payload">The original body without reserialization of unknown fields.</param>
public sealed record InitialTutorialNpcAcknowledged(InitialTutorialNpcOperation Operation,
    ReadOnlyMemory<byte> Payload) : IMessage;
