using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Match;

/// <summary>A map item point and its client-reported regeneration interval.</summary>
/// <param name="X">The point's x coordinate.</param>
/// <param name="Y">The point's y coordinate.</param>
/// <param name="Z">The point's z coordinate.</param>
/// <param name="Kind">The item kind fixed by the map point, or zero for a random box whose kind the eater's client resolves.</param>
/// <param name="MinimumMs">The shortest regeneration interval.</param>
/// <param name="MaximumMs">The longest regeneration interval.</param>
/// <param name="Team">
/// The team the point belongs to as the host's map names it: 0 for any team, otherwise the value the clients compare
/// with the eating character's team and show as a coloured box (kr-812 sub_50C840 classifies the point name, sub_636F20
/// picks the model, sub_6378A0 refuses another team). Always 0 for a build that has no team boxes and for thrown boxes.
/// </param>
public sealed record RoundSupplyPoint(float X, float Y, float Z, int Kind, int MinimumMs, int MaximumMs, int Team = 0);

/// <summary>Reports the host's map supply points for the current round.</summary>
public sealed record RoundSuppliesReported(IReadOnlyList<RoundSupplyPoint> Points) : IMessage
{
    /// <summary>
    /// Gets whether the build's report states a regeneration interval for each point. A build that states none
    /// (cn-279 sub_8205F0) reports zero for both bounds and the round applies its configured default interval.
    /// </summary>
    public bool IntervalsStated { get; init; } = true;
}

/// <summary>Requests authoritative consumption of a currently active supply object.</summary>
public sealed record RoundSupplyPickupRequested(int ObjectId, int Kind) : IMessage;

/// <summary>Requests a dropped item box at the position reported by a round member.</summary>
public sealed record RoundSupplyDropRequested(RoundSupplyPoint Point, int CreationClass) : IMessage;

/// <summary>Creates one supply object at a reported map point.</summary>
public sealed record RoundSupplyCreated(int ObjectId, RoundSupplyPoint Point) : IMessage
{
    /// <summary>The creation classification checked by the client's item-box handler.</summary>
    public int CreationClass { get; init; } = 10000;
}

/// <summary>Confirms one player's successful supply pickup to every round member.</summary>
public sealed record RoundSupplyPicked(int Seat, int ObjectId) : IMessage
{
    /// <summary>Gets the confirmed kind to place in the player's item slot, or zero for an immediate supply.</summary>
    public int ItemKind { get; init; }
    /// <summary>Gets the native slot receiving the confirmed held item.</summary>
    public int ItemSlot { get; init; }
}

/// <summary>Requests use of the item currently held in the player's native item slot.</summary>
public sealed record RoundItemUseRequested : IMessage;

/// <summary>Requests selection of the other occupied native item slot.</summary>
public sealed record RoundItemSlotSwitchRequested : IMessage;

/// <summary>Confirms a player's selected item slot and its effect kind, or rejects the switch.</summary>
public sealed record RoundItemSlotChanged(int Seat, int ItemSlot, int Kind, bool Rejected = false) : IMessage;

/// <summary>Runs the native item-use path for the confirmed player seat.</summary>
/// <param name="Seat">The occupied room seat using its held item.</param>
public sealed record RoundItemUsed(int Seat) : IMessage
{
    /// <summary>Gets the confirmed held item kind passed to the native effect sender.</summary>
    public int Kind { get; init; }
    /// <summary>Gets the native slot consumed by this use.</summary>
    public int ItemSlot { get; init; }
}
