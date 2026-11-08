using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Rooms;

namespace BubbleFighter.Fantasy.Contracts.Match;

/// <summary>A native script's requested zombie character and weapon for one room seat.</summary>
/// <param name="Seat">The room seat, not the scene entity handle.</param>
/// <param name="ZombieKind">The native zombie character kind.</param>
/// <param name="WeaponItemId">The weapon item installed by the conversion.</param>
public sealed record ScriptZombieConversion(int Seat, int ZombieKind, int WeaponItemId);

/// <summary>Authorizes a script conversion and updates the complete native team panel.</summary>
/// <param name="Conversion">The accepted target, character kind and weapon.</param>
/// <param name="Members">The current room members after the allegiance change.</param>
public sealed record RoundScriptZombieTeamChanged(ScriptZombieConversion Conversion,
    IReadOnlyList<RoomMemberView> Members) : IMessage;

/// <summary>The owning client's peer operation that applies a previously authorized zombie character.</summary>
/// <param name="Seat">The room seat whose client applies the conversion.</param>
/// <param name="Team">The native allegiance.</param>
/// <param name="Operation">Zero applies a team change, one restores the base form, and two activates its native ULTRA.</param>
/// <param name="ZombieKind">The native character kind, absent only when the native packet omits it.</param>
/// <param name="WeaponItemId">The conversion's weapon item.</param>
public sealed record RoundScriptZombieConversionReported(int Seat, int Team, int Operation,
    int? ZombieKind, int WeaponItemId) : IMessage
{
    /// <summary>Gets the original peer feed only when the build declares pass-through outside the enabled script mode.</summary>
    public RoundFeedSent? OtherModeFeed { get; init; }
}
