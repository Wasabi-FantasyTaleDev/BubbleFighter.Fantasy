using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Rooms;

/// <summary>One player present in the plaza, as a seat record shows them.</summary>
/// <param name="Slot">The plaza seat the player holds while they stay.</param>
/// <param name="UserNumber">The player's identity number; every client matches itself by it.</param>
/// <param name="Nickname">The display name.</param>
/// <param name="Level">The player's level.</param>
/// <param name="CharacterItemId">The equipped character item.</param>
/// <param name="EquippedItemIds">The worn items shown in the seat record.</param>
/// <param name="EntrySeed">
/// A value fixed per plaza entry, so every receiver of the member's spawn derives the same start
/// position from it; a new entry gets a new value.
/// </param>
public sealed record PlazaMember(int Slot, int UserNumber, string Nickname, int Level, int CharacterItemId,
    IReadOnlyList<int> EquippedItemIds, int EntrySeed = 0)
{
    /// <summary>Gets the player's committed avatar material color.</summary>
    public ushort ColorIndex { get; init; }
}

/// <summary>A client asks to enter the plaza from the main menu, or after leaving a room.</summary>
public sealed record PlazaEnterRequested : IMessage;

/// <summary>
/// Sent to a player who just entered: the player's own plaza seat and the players already present,
/// whose seat records and positions follow in the build's own packets.
/// </summary>
/// <param name="You">The entrant's own seat.</param>
/// <param name="Members">The players already present, in seat order.</param>
public sealed record PlazaEntered(PlazaMember You, IReadOnlyList<PlazaMember> Members) : IMessage
{
    /// <summary>Gets whether this entry should display the native party transition notice.</summary>
    public bool PartyTransition { get; init; }

    /// <summary>Gets occupied slot indexes in membership order, including the entrant, when the dialect retains profile order.</summary>
    public IReadOnlyList<int>? MemberOrder { get; init; }
}

/// <summary>Sent to every player already present when another player enters the plaza.</summary>
/// <param name="Member">The player who entered.</param>
public sealed record PlazaMemberEntered(PlazaMember Member) : IMessage;

/// <summary>
/// Sent to every player still present when a player leaves the plaza: into a room, to the main
/// menu, or because the connection closed.
/// </summary>
/// <param name="Member">The player who left, as their seat record showed them.</param>
public sealed record PlazaMemberLeft(PlazaMember Member) : IMessage;

/// <summary>A client asks to leave the plaza for the main menu (its plaza exit control).</summary>
public sealed record PlazaMenuReturnRequested : IMessage
{
    /// <summary>Gets whether the shared scene owner is returning this player with their party.</summary>
    public bool PartyTransition { get; init; }

    /// <summary>Gets the authoritative appearance resolved by the shared owner for the main-menu install.</summary>
    public PlazaMember? You { get; init; }
}

/// <summary>
/// A client reports its position while a plaza session is open. The body is the build's own
/// position upload, opaque to the logic: it is kept unread and echoed inside
/// <see cref="PlazaPositionReceived"/>.
/// </summary>
/// <param name="Body">The position upload's body, verbatim.</param>
public sealed record PlazaPositionReported(ReadOnlyMemory<byte> Body) : IMessage;

/// <summary>
/// One member's position, sent to the players present. The body is the reported position with the
/// build's own identity and sequence fields added by the codec; the logic reads neither.
/// </summary>
/// <param name="UserNumber">The moving player's identity number.</param>
/// <param name="Sequence">The per-member position sequence, starting at 1 on each plaza entry.</param>
/// <param name="Body">The position the member reported, verbatim.</param>
/// <param name="EntrySeed">The member's <see cref="PlazaMember.EntrySeed"/>, used to place a member who has not reported.</param>
public sealed record PlazaPositionReceived(int UserNumber, int Sequence, ReadOnlyMemory<byte> Body, int EntrySeed = 0) : IMessage;
