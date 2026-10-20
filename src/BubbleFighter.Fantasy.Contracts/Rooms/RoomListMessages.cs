using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Rooms;

/// <summary>
/// A client asks for the lobby's open-room list. Build codecs decode it from their own request
/// (kr-1256 C2S 401); fields whose meaning is unproven stay in the codec's log, not here.
/// </summary>
/// <param name="Cursor">The page cursor the client sent; the reply echoes it.</param>
public sealed record RoomListRequested(int Cursor) : IMessage;

/// <summary>One open room as the lobby list shows it.</summary>
/// <param name="RoomId">The room number players join by.</param>
/// <param name="Name">The room title, already resolved to the display text.</param>
/// <param name="MasterNickname">The room master's nickname.</param>
/// <param name="ModeId">The game mode id from the client's mode table.</param>
/// <param name="Occupants">Seated players.</param>
/// <param name="Capacity">Joinable seats: the room's seats minus closed ones.</param>
/// <param name="HasPassword">Whether joining requires a password.</param>
/// <param name="BuildSettings">The creator's own settings block, when its client sent one.</param>
public sealed record RoomListEntry(int RoomId, string Name, string MasterNickname, ushort ModeId,
    int Occupants, int Capacity, bool HasPassword, RoomBuildSettings? BuildSettings = null)
{
    /// <summary>Gets whether the room has left the waiting phase and must display its active-match state.</summary>
    public bool MatchInProgress { get; init; }
}

/// <summary>The lobby's open-room list sent in answer to <see cref="RoomListRequested"/>.</summary>
/// <param name="Cursor">The request's page cursor, echoed.</param>
/// <param name="Rooms">The open rooms, in listing order.</param>
public sealed record RoomListSnapshot(int Cursor, IReadOnlyList<RoomListEntry> Rooms) : IMessage;
