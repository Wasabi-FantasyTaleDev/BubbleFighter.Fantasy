using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Rooms;

/// <summary>Where a room is in its round cycle.</summary>
public enum RoomStage
{
    /// <summary>In the waiting room; no round is loaded.</summary>
    Waiting,

    /// <summary>The stage enter was sent and the clients are loading or counting down.</summary>
    Starting,

    /// <summary>The round is running (after S2C 608).</summary>
    Playing,

    /// <summary>The result screen is open.</summary>
    Result,
}

/// <summary>One seat of a room as it stood when the view was taken.</summary>
/// <param name="Slot">The seat index; the seat's scene entity is <c>1000000 + 100000 * Slot</c>.</param>
/// <param name="Team">The seat's team (1 blue, 2 red in team modes).</param>
/// <param name="UserNumber">The seated account.</param>
/// <param name="Game">The seat's game connection.</param>
/// <param name="Relay">The seat's authenticated match connection, if one is bound.</param>
/// <param name="RelayInitialized">Whether the match connection finished its scene registration.</param>
public sealed record RoomSeatView(int Slot, byte Team, int UserNumber, ISession Game, ISession? Relay, bool RelayInitialized);

/// <summary>A room's roster as it stood when the view was taken; never updated afterwards.</summary>
/// <param name="RoomId">The room id.</param>
/// <param name="Stage">The room's stage.</param>
/// <param name="Generation">The round generation; each start increments it, so state kept per round keys on it.</param>
/// <param name="ModeId">The room's game mode.</param>
/// <param name="Seats">The occupied seats.</param>
/// <param name="ClientRunsNpcs">
/// Whether the mode's NPCs are simulated by a player's client (the horde family: pirate modes 101
/// and 106), so in-round actions may name NPC entities that belong to no seat.
/// </param>
public sealed record RoomRosterView(int RoomId, RoomStage Stage, int Generation, ushort ModeId, IReadOnlyList<RoomSeatView> Seats,
    bool ClientRunsNpcs);

/// <summary>
/// Read access to the rooms for rows that act on in-round traffic but do not own the rooms.
/// Every call takes a fresh view; a view is never updated, so a sender re-reads before acting
/// after any wait.
/// </summary>
public interface IRoomRoster
{
    /// <summary>Returns the room of a game or match connection, or <see langword="null"/> when it is in none.</summary>
    /// <param name="sessionId">A game or match session id.</param>
    RoomRosterView? FindBySession(long sessionId);

    /// <summary>Returns a room by id, or <see langword="null"/> when it no longer exists.</summary>
    /// <param name="roomId">The room id.</param>
    RoomRosterView? Find(int roomId);
}
