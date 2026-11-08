using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Match;

/// <summary>The installed specification and native weapon slot carried by an audited peer report.</summary>
/// <param name="Actor">The reporting character's native scene identity.</param>
/// <param name="Slot">The native weapon slot, independent of preset page indexes and weapon families.</param>
/// <param name="Specification">The effective specification after the client's mode replacement.</param>
public sealed record RoundCriticalWeaponState(int Actor, int Slot, int Specification);

/// <summary>Revises a running seat's critical inputs after an accepted equipment, weapon or timed-effect transition.</summary>
/// <param name="RoomId">The room owning the accepted transition.</param>
/// <param name="Generation">The round generation whose opening tables were issued.</param>
/// <param name="EntityId">The same scene actor as the opening issuance.</param>
/// <param name="Revision">The consecutive positive input revision within that issuance.</param>
/// <param name="Equipment">The authoritative worn selection and room weapon overrides.</param>
public sealed record MatchCriticalSeedUpdate(int RoomId, int Generation, int EntityId, long Revision,
    MatchSeedEquipment Equipment) : IMessage
{
    /// <summary>Gets slot zero's installed native specification, already resolved by the client's mode replacement.</summary>
    public int? EffectivePrimarySpecification { get; init; }

    /// <summary>Gets slot one's installed native specification, already resolved by the client's mode replacement.</summary>
    public int? EffectiveSidearmSpecification { get; init; }

    /// <summary>Gets whether the last accepted native trigger's timer is active, even if later equipment no longer qualifies to restart it.</summary>
    public bool TimedEffectActive { get; init; }
}

/// <summary>Validates resource inputs and publishes consecutive critical revisions on one build's native match connection.</summary>
public interface IMatchCriticalSeedAuthority : IMessageSender<MatchCriticalSeedUpdate>
{
    /// <summary>Reconciles published equipment attributes and installed weapons using the build's audited in-round view behavior.</summary>
    /// <param name="current">The last accepted critical inputs, including any installed weapon reports.</param>
    /// <param name="equipment">The equipment snapshot whose local view has finished sending.</param>
    /// <returns>The candidate inputs with the existing revision; the room assigns the next revision after validating ownership.</returns>
    MatchCriticalSeedUpdate WithPublishedEquipment(MatchCriticalSeedUpdate current, MatchSeedEquipment equipment);

    /// <summary>Resolves the native timed-effect duration, returning zero when the worn set does not meet its resource conditions.</summary>
    /// <param name="equipment">The accepted worn selection whose ordered item effects are evaluated.</param>
    /// <returns>The eligible duration in milliseconds; this does not activate an effect or send a table.</returns>
    int TimedEffectDuration(MatchSeedEquipment equipment);

    /// <summary>Rejects unsupported critical revision inputs before the room accepts or publishes their transition.</summary>
    /// <param name="update">The complete candidate critical input revision.</param>
    void Validate(MatchCriticalSeedUpdate update);
}
