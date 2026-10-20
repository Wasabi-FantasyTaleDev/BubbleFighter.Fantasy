using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Rooms;

/// <summary>
/// A client asks to open a waiting room and take its first seat. Build codecs decode it from
/// their own create request; request fields a build cannot prove stay in the codec.
/// </summary>
/// <param name="Name">The room title the player typed; may be empty.</param>
/// <param name="Password">The join password, or empty for an open room.</param>
/// <param name="ModeId">The game mode id from the client's mode table.</param>
/// <param name="MapKey">The selected map key, or a random-selection marker the client resolves itself.</param>
/// <param name="BuildSettings">The creating client's own settings block, kept for clients of the same lineage.</param>
public sealed record RoomCreateRequested(string Name, string Password, int ModeId, string MapKey,
    RoomBuildSettings? BuildSettings = null) : IMessage
{
    /// <summary>Gets whether a repeated request may update the waiting-room master's title.</summary>
    public bool UpdateExistingTitle { get; init; }
}

/// <summary>
/// A settings block exactly as one client lineage encodes it. The room keeps it unread and hands it
/// back to members of the same lineage, so every option the creator's dialog chose survives without
/// the contract naming fields no build has proven. Codecs of another lineage ignore it.
/// </summary>
/// <param name="Lineage">The profile id of the layout the bytes follow, such as <c>kr-812</c>.</param>
/// <param name="Bytes">The encoded block, or empty when only a change has been seen.</param>
/// <param name="Change">The lineage's latest encoded settings change, which its codec applies over <paramref name="Bytes"/>; empty when there was none.</param>
public sealed record RoomBuildSettings(string Lineage, ReadOnlyMemory<byte> Bytes, ReadOnlyMemory<byte> Change = default)
{
    /// <summary>Gets the latest selections for the three native round-map controls, retained across room refreshes.</summary>
    public IReadOnlyList<RoomRoundSelection> RoundSelections { get; init; } = [];

    /// <summary>
    /// Gets the creator's encoded session prefix when its dialect must retain fields outside the game-setting
    /// block. Other lineages leave it empty; room logic stores it without interpreting its layout.
    /// </summary>
    public ReadOnlyMemory<byte> SessionPrefix { get; init; }

    /// <summary>
    /// Gets the game mode the server set on the room after the client's own block, or null while the block's mode
    /// stands. A codec writes it into the mode word of the block it sends, so every member's client shows the room's mode.
    /// </summary>
    public int? Mode { get; init; }
}

/// <summary>The room master changes the room's settings.</summary>
/// <param name="ModeId">The game mode id now selected.</param>
/// <param name="MapKey">The map key now selected, or empty to keep the room's.</param>
/// <param name="BuildSettings">The change as the master's client encoded it, in <see cref="RoomBuildSettings.Change"/>.</param>
public sealed record RoomSettingsChangeRequested(int ModeId, string MapKey, RoomBuildSettings? BuildSettings = null) : IMessage;

/// <summary>Sent to every member after the master changed the room's settings.</summary>
/// <param name="Room">The room with the new settings.</param>
public sealed record RoomSettingsChanged(RoomView Room) : IMessage;

/// <summary>A room master changes one of the three native round-map controls.</summary>
/// <param name="Index">The zero-based native control index.</param>
/// <param name="Lineage">The packet lineage that decoded the two strings.</param>
/// <param name="Bytes">The complete native selection body, validated by its dialect and retained without interpreting its fields.</param>
public sealed record RoomRoundSettingsChangeRequested(int Index, string Lineage, ReadOnlyMemory<byte> Bytes) : IMessage;

/// <summary>Retains one committed round-map selection independently of the creator's settings block.</summary>
/// <param name="Index">The zero-based native control index.</param>
/// <param name="Bytes">The encoded selector body whose otherwise opaque values remain owned by the dialect.</param>
public sealed record RoomRoundSelection(int Index, ReadOnlyMemory<byte> Bytes);

/// <summary>A seated player asks to delegate room ownership to the named member.</summary>
/// <param name="Nickname">The target nickname from the authenticated player's native room control.</param>
public sealed record RoomMasterChangeRequested(string Nickname) : IMessage;

/// <summary>Acknowledges one room ownership request before any new-master announcement.</summary>
/// <param name="Nickname">The target nickname echoed by the native reply.</param>
/// <param name="Result">Native result: zero accepted, one target unavailable, two requester not master, three already master.</param>
public sealed record RoomMasterChangeReplied(string Nickname, int Result) : IMessage;

/// <summary>The room master asks to start the round.</summary>
/// <param name="MapKey">The map the master's client resolved for the round, or empty to use the room's.</param>
public sealed record RoomStartRequested(string MapKey = "") : IMessage
{
    /// <summary>Gets whether an authorized server command bypasses ready marks while retaining host and phase checks.</summary>
    public bool Force { get; init; }

    /// <summary>Gets the map name the start request carries beside the key, or empty when the build sends none.</summary>
    public string MapName { get; init; } = "";

    /// <summary>Gets the selection the master made, possibly a random-selection marker, or empty when the build sends none.</summary>
    public string MapSelection { get; init; } = "";

    /// <summary>Gets the round lengths the start request carries, or null when the build's start carries no rule record.</summary>
    public RoomRoundTimeLimits? TimeLimits { get; init; }
}

/// <summary>The round lengths a start request carries in its rule record, per kind of mode.</summary>
/// <param name="DeathMatchMs">The length of an ordinary round, in milliseconds.</param>
/// <param name="TimeAttackMs">The length of a time-attack round (the zombie and pirate families), in milliseconds.</param>
public sealed record RoomRoundTimeLimits(int DeathMatchMs, int TimeAttackMs);

/// <summary>
/// A dialect validated a native start request on an explicitly room-only binding. The shared room
/// owner records the unavailable operation once without beginning a round or emitting start packets.
/// </summary>
public sealed record RoomStartUnavailableRequested : IMessage;

/// <summary>Arms the client's native waiting-room automatic-start countdown.</summary>
public sealed record RoomAutoStartNotified : IMessage;

/// <summary>A seated player asks to move to the other team.</summary>
public sealed record RoomTeamChangeRequested : IMessage;

/// <summary>Changes a seated character's native allegiance, including cooperative zombie conversion.</summary>
/// <param name="Slot">The player's room seat.</param>
/// <param name="Team">The native allegiance identifier.</param>
public sealed record RoomTeamConverted(int Slot, int Team) : IMessage;

/// <summary>Sent to every member when the round starts loading.</summary>
/// <param name="Room">The room as the round starts, with every seated player.</param>
/// <param name="MapKey">The map the round is played on.</param>
public sealed record RoomMatchStarting(RoomView Room, string MapKey) : IMessage
{
    /// <summary>Gets the stage the map loads when the server drew the map, or empty when the stage is the map itself.</summary>
    public string StageKey { get; init; } = "";

    /// <summary>Gets whether the drawn map is a mission whose stage differs from its key (the pirate battle).</summary>
    public bool IsMission { get; init; }
}

/// <summary>A client asks to take a seat in a listed room.</summary>
/// <param name="RoomId">The room number from the list row.</param>
/// <param name="Password">The password the player typed, or empty.</param>
public sealed record RoomJoinRequested(int RoomId, string Password) : IMessage;

/// <summary>A client leaves the room it is seated in and returns to the lobby.</summary>
public sealed record RoomLeaveRequested : IMessage;

/// <summary>A seated client sets or clears its ready mark.</summary>
/// <param name="Ready">Whether the player is ready to start.</param>
public sealed record RoomReadyRequested(bool Ready) : IMessage
{
    /// <summary>Gets whether the seated player is browsing the shop instead of being ready.</summary>
    public bool InShop { get; init; }

    /// <summary>Gets whether the seated player is browsing the inventory instead of being ready.</summary>
    public bool InInventory { get; init; }

}

/// <summary>Why a join was refused.</summary>
public enum RoomJoinRefusal
{
    /// <summary>The room closed or never existed.</summary>
    NoRoom,
    /// <summary>Every open seat is taken.</summary>
    Full,
    /// <summary>The room has a password and the request carried a different one.</summary>
    WrongPassword,
    /// <summary>The room's round is already running.</summary>
    Started,
}

/// <summary>The answer to a <see cref="RoomJoinRequested"/> that did not seat the player.</summary>
/// <param name="RoomId">The room the player asked for.</param>
/// <param name="Reason">Why no seat was given.</param>
public sealed record RoomJoinRefused(int RoomId, RoomJoinRefusal Reason) : IMessage;

/// <summary>One occupied seat as every member's client shows it.</summary>
/// <param name="Slot">The seat index inside the room.</param>
/// <param name="UserNum">The player's account number.</param>
/// <param name="Nickname">The player's nickname.</param>
/// <param name="Level">The player's level, at least 1.</param>
/// <param name="Team">The team label: 0 unassigned, 1 blue, 2 red.</param>
/// <param name="Ready">Whether the player marked ready.</param>
/// <param name="CharacterItemId">The equipped character item.</param>
/// <param name="EquippedItemIds">Every worn item, the character included; empty when no inventory is mounted.</param>
public sealed record RoomMemberView(int Slot, int UserNum, string Nickname, int Level, int Team, bool Ready,
    int CharacterItemId, IReadOnlyList<int> EquippedItemIds)
{
    /// <summary>Gets the player's avatar material color independently of team assignment.</summary>
    public ushort ColorIndex { get; init; }

    /// <summary>Gets whether the seated player has the shop open.</summary>
    public bool InShop { get; init; }

    /// <summary>Gets whether the seated player has the inventory open.</summary>
    public bool InInventory { get; init; }
}

/// <summary>A waiting room as a member's client needs it to draw the room.</summary>
/// <param name="RoomId">The room number.</param>
/// <param name="Name">The room title.</param>
/// <param name="ModeId">The game mode id.</param>
/// <param name="MapKey">The selected map key or random-selection marker.</param>
/// <param name="MaxSeats">The number of seats the room was created with.</param>
/// <param name="MasterSlot">The seat index of the room master.</param>
/// <param name="HasPassword">Whether joining requires a password.</param>
/// <param name="ClosedSlots">Empty seats the master closed.</param>
/// <param name="Seats">The occupied seats, in seat order.</param>
/// <param name="BuildSettings">The creator's own settings block, when its client sent one.</param>
public sealed record RoomView(int RoomId, string Name, int ModeId, string MapKey, int MaxSeats, int MasterSlot,
    bool HasPassword, IReadOnlyList<int> ClosedSlots, IReadOnlyList<RoomMemberView> Seats,
    RoomBuildSettings? BuildSettings = null)
{
    /// <summary>
    /// Gets occupied slot indexes in membership insertion order when a dialect's profile vector retains that
    /// order separately from its slot-indexed seat vector. Null uses <see cref="Seats"/> order.
    /// </summary>
    public IReadOnlyList<int>? MemberOrder { get; init; }
}

/// <summary>Sent to a player who was just seated, by create or by join: the whole room and their own seat.</summary>
/// <param name="Room">The room as it stands with the player seated.</param>
/// <param name="YourSlot">The receiving player's seat index.</param>
public sealed record RoomEntered(RoomView Room, int YourSlot) : IMessage
{
    /// <summary>Gets whether the client should describe this entry as a party scene transition.</summary>
    public bool PartyTransition { get; init; }

    /// <summary>Gets why this player receives a full room snapshot, without inferring it from membership count.</summary>
    public RoomEntryReason Reason { get; init; }
}

/// <summary>The operation that requires a player's full waiting-room snapshot.</summary>
public enum RoomEntryReason
{
    /// <summary>The caller uses the dialect's ordinary entry sequence.</summary>
    Unspecified,
    /// <summary>The player created this room.</summary>
    Created,
    /// <summary>The player joined a room occupied by other members.</summary>
    Joined,
    /// <summary>The seated player repeated the creation request and needs their existing room again.</summary>
    Refreshed,
    /// <summary>The player's first room-stage probe requires a synchronization snapshot.</summary>
    ProbeRefresh,
    /// <summary>The round's result is over and the members are back in the waiting room.</summary>
    RoundReturned,
}

/// <summary>A seated player asks for the waiting-room UI to be refreshed after its stage is available.</summary>
public sealed record RoomUiRefreshRequested : IMessage;

/// <summary>The complete waiting-room UI state to show to one member.</summary>
/// <param name="Room">The room snapshot captured before the notification was sent.</param>
public sealed record RoomUiChanged(RoomView Room) : IMessage;

/// <summary>The dialect's opaque challenge reply to an authenticated seated player's room-stage probe.</summary>
/// <param name="Challenge">The seat's established server challenge value; it grants no membership authority.</param>
public sealed record RoomProbeAcknowledged(int Challenge) : IMessage
{
    /// <summary>Gets the authenticated seat whose native ping UI is acknowledged.</summary>
    public int Slot { get; init; }
}

/// <summary>The operation that changed an occupied seat.</summary>
public enum RoomSeatUpdateReason
{
    /// <summary>The seat's ordinary identity, presence or appearance state changed.</summary>
    Unspecified,
    /// <summary>The seat belongs to a newcomer who must be announced to existing members.</summary>
    Joined,
    /// <summary>The seated player changed teams while waiting.</summary>
    TeamChanged,
}

/// <summary>Sent to the members of a room when a seat is taken or its record changes (ready, team, equipment).</summary>
/// <param name="RoomId">The room.</param>
/// <param name="Seat">The seat's current record.</param>
public sealed record RoomSeatUpdated(int RoomId, RoomMemberView Seat) : IMessage
{
    /// <summary>
    /// Gets the room snapshot captured with this seat change, for dialects whose update includes every member's
    /// appearance. Null remains valid for callers that publish only the changed seat.
    /// </summary>
    public RoomView? Room { get; init; }

    /// <summary>Gets the committed operation, for dialects with a dedicated team or newcomer notification.</summary>
    public RoomSeatUpdateReason Reason { get; init; }
}

/// <summary>Sent to the remaining members when a player leaves the room.</summary>
/// <param name="RoomId">The room.</param>
/// <param name="Seat">The record of the player who left; its slot is the seat that is now empty.</param>
public sealed record RoomSeatVacated(int RoomId, RoomMemberView Seat) : IMessage;

/// <summary>Sent to the members when the room master changes, after the old master's seat was vacated.</summary>
/// <param name="RoomId">The room.</param>
/// <param name="MasterSlot">The new master's seat index.</param>
public sealed record RoomMasterChanged(int RoomId, int MasterSlot) : IMessage;

/// <summary>Sent to a player whose <see cref="RoomLeaveRequested"/> was carried out: they are back in the lobby.</summary>
/// <param name="RoomId">The room they left.</param>
/// <param name="Seat">Their record in the seat they gave up.</param>
public sealed record RoomLeft(int RoomId, RoomMemberView Seat) : IMessage
{
    /// <summary>
    /// Gets the room as it stood when they left, for a build whose leave confirmation restates the
    /// room's title and settings; null when the caller has no view of it.
    /// </summary>
    public RoomView? Room { get; init; }
}

/// <summary>A member sends a chat line to their room.</summary>
/// <param name="Channel">The build's own channel selector for the line; the logic hands it back unread.</param>
/// <param name="Text">The line.</param>
public sealed record RoomChatSent(int Channel, string Text) : IMessage;

/// <summary>Sent to every member of a room, the speaker included: a chat line.</summary>
/// <param name="RoomId">The room.</param>
/// <param name="Slot">The speaker's seat.</param>
/// <param name="Nickname">The speaker's nickname.</param>
/// <param name="Channel">The channel selector the speaker's client sent.</param>
/// <param name="Text">The line.</param>
public sealed record RoomChatReceived(int RoomId, int Slot, string Nickname, int Channel, string Text) : IMessage;

/// <summary>The master closes an empty seat, or opens a closed one.</summary>
/// <param name="Slot">The seat index.</param>
/// <param name="Closed">True to close the seat, false to open it.</param>
public sealed record RoomSeatLockRequested(int Slot, bool Closed) : IMessage;

/// <summary>Sent to every member: a seat was closed or opened.</summary>
/// <param name="RoomId">The room.</param>
/// <param name="Slot">The seat index.</param>
/// <param name="Closed">Whether the seat is closed now.</param>
public sealed record RoomSeatLockChanged(int RoomId, int Slot, bool Closed) : IMessage;

/// <summary>The master removes a member from the room.</summary>
/// <param name="Nickname">The member to remove.</param>
public sealed record RoomKickRequested(string Nickname) : IMessage;

/// <summary>Sent to every member, the removed one included: the master removed a member.</summary>
/// <param name="RoomId">The room.</param>
/// <param name="ByNickname">The master who removed the member.</param>
/// <param name="Nickname">The removed member.</param>
public sealed record RoomMemberKicked(int RoomId, string ByNickname, string Nickname) : IMessage;
