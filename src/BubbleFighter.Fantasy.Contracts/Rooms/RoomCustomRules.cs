using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Rooms;

/// <summary>One native custom-rule selector and its ordered unsigned parameter words.</summary>
/// <param name="ValueId">The native CustomGameMode ValueId, independent of the room's game mode.</param>
/// <param name="Values">The selected parameters; their meaning and allowed domain come from the client's rule catalogue.</param>
public sealed record RoomCustomRuleValue(ushort ValueId, IReadOnlyList<uint> Values);

/// <summary>A room master submits the complete custom-rule vector for the room's current game mode.</summary>
/// <param name="Lineage">The packet lineage that decoded the request and owns its retained settings layout.</param>
/// <param name="MatchesModeDefaults">The client's comparison against the mode's native defaults; this is not an enable switch.</param>
/// <param name="Values">The replacement vector, including native fixed values required by the selected mode.</param>
public sealed record RoomCustomRulesChangeRequested(string Lineage, bool MatchesModeDefaults,
    IReadOnlyList<RoomCustomRuleValue> Values) : IMessage;

/// <summary>Reports custom-rule status after the updated room settings have been published.</summary>
/// <param name="Result">Native result: 0 accepted, 1 failed, 2 invalid values, 4 master changed options, 5 master changed players.</param>
public sealed record RoomCustomRulesReplied(ushort Result) : IMessage;
