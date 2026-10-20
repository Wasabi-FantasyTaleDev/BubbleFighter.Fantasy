using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Movement;
using BubbleFighter.Fantasy.Contracts.Rooms;

namespace BubbleFighter.Fantasy.MatchRelay;

/// <summary>Where a seat's character is in the death and reset cycle of the timed-score mode.</summary>
internal enum CharacterLife
{
    /// <summary>Playing normally.</summary>
    Alive,

    /// <summary>Trapped in a bubble; a teammate's release or an enemy's pop decides what follows.</summary>
    Bubble,

    /// <summary>Popped; waits for its death cleanup and then asks for a reset (C2S 602).</summary>
    Dead,

    /// <summary>The reset reply (S2C 632) went out; the client's C2S 32926 completes it.</summary>
    ResetPending,
}

/// <summary>One seat's state in the running round. Read and written only under the owning <see cref="RoundState.Gate"/>.</summary>
internal sealed class SeatRoundState
{
    /// <summary>Gets or sets the character's life.</summary>
    internal CharacterLife Life { get; set; } = CharacterLife.Alive;

    /// <summary>Gets or sets whether the death cleanup (C2S 32836) arrived after the last death.</summary>
    internal bool DeathCleanupReceived { get; set; }

    /// <summary>Gets or sets the match connection the pending reset was granted on; a new connection voids it.</summary>
    internal ISession? ResetRelay { get; set; }

    /// <summary>Gets or sets the merged movement the next partial sample builds on.</summary>
    internal PlayerMovementState? Movement { get; set; }
}

/// <summary>The per-seat state of one room's current round.</summary>
/// <param name="generation">The round generation the state belongs to.</param>
internal sealed class RoundState(int generation)
{
    private readonly Dictionary<int, SeatRoundState> _seats = [];

    /// <summary>Gets the lock guarding every seat state of this round.</summary>
    internal object Gate { get; } = new();

    /// <summary>Gets the round generation this state belongs to.</summary>
    internal int Generation { get; } = generation;

    /// <summary>Returns a seat's state, creating it on first use. Call under <see cref="Gate"/>.</summary>
    /// <param name="slot">The seat index.</param>
    internal SeatRoundState Seat(int slot)
    {
        if (!_seats.TryGetValue(slot, out var seat))
            _seats[slot] = seat = new SeatRoundState();
        return seat;
    }
}

/// <summary>
/// The round state of every room with in-round traffic, keyed by room and replaced when the
/// room starts a new round. The rooms row owns the rooms; entries of rooms it no longer lists
/// are dropped when space is needed, and the table never holds more than its capacity.
/// </summary>
/// <param name="roster">The rooms row's roster.</param>
/// <param name="capacity">The most rooms tracked at once.</param>
internal sealed class RoundStates(IRoomRoster roster, int capacity)
{
    private readonly object _gate = new();
    private readonly Dictionary<int, RoundState> _rooms = [];

    /// <summary>Returns the state of the view's round, starting over when the room began a newer round.</summary>
    /// <param name="view">The room view the caller acts on.</param>
    /// <exception cref="InvalidOperationException">Thrown when the table is full of live rooms.</exception>
    internal RoundState For(RoomRosterView view)
    {
        lock (_gate)
        {
            if (_rooms.TryGetValue(view.RoomId, out var state))
            {
                if (state.Generation == view.Generation)
                    return state;
                // A view taken before a restart must not revive the older round.
                if (state.Generation > view.Generation)
                    return new RoundState(view.Generation);
            }
            else if (_rooms.Count >= capacity)
            {
                foreach (var stale in _rooms.Where(pair => roster.Find(pair.Key) is not { Stage: not RoomStage.Waiting }).Select(pair => pair.Key).ToArray())
                    _rooms.Remove(stale);
                if (_rooms.Count >= capacity)
                    throw new InvalidOperationException($"Match relay tracks {capacity} running rounds already; raise max_rounds.");
            }
            return _rooms[view.RoomId] = new RoundState(view.Generation);
        }
    }

    /// <summary>Gets how many rooms are tracked.</summary>
    internal int Count
    {
        get
        {
            lock (_gate)
                return _rooms.Count;
        }
    }
}
