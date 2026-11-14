using BubbleFighter.Fantasy.Contracts.Signals;
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
    bool ClientRunsNpcs)
{
    /// <summary>Gets whether this is a published gameplay room rather than an automatic or training scene.</summary>
    public bool IsMatchRoom { get; init; }

    /// <summary>Gets the room owner's forced UDP choice, or null for the multiplayer-only default.</summary>
    public bool? UdpOverride { get; init; }

    /// <summary>Gets the revision of full room installations; a new installation invalidates prepared peer tables.</summary>
    public long InstallationVersion { get; init; }

    /// <summary>Gets whether this room requests UDP; the deployment switch still takes precedence.</summary>
    public bool UsesUdp => IsMatchRoom && (UdpOverride ?? Seats.Count(seat => !seat.Game.Connection.IsClosed) > 1);
}

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

/// <summary>A protocol-scoped room roster for peer setup plugins serving more than one build.</summary>
public interface IRoomPeerRoster : IRoomRoster
{
    /// <summary>Gets the owning game protocol; snapshots never cross this scope.</summary>
    string Protocol { get; }

    /// <summary>Returns immutable views of the protocol's bounded room registry, including rooms still waiting.</summary>
    IReadOnlyList<RoomRosterView> Snapshot();

    /// <summary>Runs native peer control publication on the room's bounded serial queue, after pending scene installations.</summary>
    /// <param name="roomId">The room whose current view is passed to the operation.</param>
    /// <param name="operation">A control publication that completes before later room transitions can run.</param>
    /// <param name="ct">Cancels queued work or cooperatively cancels the running publication.</param>
    /// <returns>Whether the room still existed when the operation began.</returns>
    ValueTask<bool> ExecuteAsync(int roomId, Func<RoomRosterView, CancellationToken, ValueTask> operation, CancellationToken ct);

    /// <summary>
    /// Gets a task that is complete while the protocol has at least one room and pending while it has none, so a
    /// peer setup loop can sleep on an empty build instead of asking for a snapshot on every tick. The task never
    /// faults and is never cancelled. A roster that cannot tell returns a completed task, which keeps its caller
    /// checking on every tick.
    /// </summary>
    /// <returns>The level-triggered presence of rooms; see <see cref="PresenceSignal.WhenPresent"/>.</returns>
    Task WhenAnyRoom() => Task.CompletedTask;
}
