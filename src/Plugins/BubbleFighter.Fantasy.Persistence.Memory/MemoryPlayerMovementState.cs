using BubbleFighter.Fantasy.Contracts.Movement;
using System.Collections.Concurrent;

namespace BubbleFighter.Fantasy.Persistence.Memory;

/// <summary>
/// In-process store for the per-actor merged movement state the others-only S2C 32885 rebroadcast
/// reads. Actors are keyed by the id each movement sample carries in its own body; the map is bounded by <see
/// cref="MemoryPersistenceOptions.MaxMovementActors"/> and a merge that would track a new actor past the cap
/// throws instead of evicting or growing, so a full store is a loud error at the call site rather than silent
/// data loss. Each actor's merge is atomic under the actor's own gate, so one actor's samples never interleave
/// half-applied. State lives only for the life of the process, like every other store of this row.
/// </summary>
public sealed class MemoryPlayerMovementState : IPlayerMovementState
{
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<int, MovementActorEntry> _actors = [];

    /// <summary>Initializes the store with its actor bound.</summary>
    /// <param name="maxActors">Maximum distinct actors tracked before a merge
    /// for a new one refuses.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when
    /// <paramref name="maxActors"/> is below 1.</exception>
    public MemoryPlayerMovementState(int maxActors)
    {
        if (maxActors < 1)
            throw new ArgumentOutOfRangeException(nameof(maxActors), maxActors, "max_movement_actors must be at least 1.");
        MaxActors = maxActors;
    }

    /// <summary>Gets the configured bound on distinct tracked actors.</summary>
    public int MaxActors { get; }

    /// <inheritdoc />
    public ValueTask<PlayerMovementState?> FindAsync(int actorId, CancellationToken ct)
    {
        RequireActorId(actorId);
        if (!_actors.TryGetValue(actorId, out var entry))
            return ValueTask.FromResult<PlayerMovementState?>(null);
        lock (entry.Gate)
            return ValueTask.FromResult<PlayerMovementState?>(entry.State);
    }

    /// <inheritdoc />
    public ValueTask<PlayerMovementState?> MergeAsync(
        int actorId,
        byte mask,
        float x,
        float y,
        float z,
        byte yaw,
        byte pitch,
        byte state,
        CancellationToken ct)
    {
        RequireActorId(actorId);
        if (!_actors.TryGetValue(actorId, out var entry))
        {
            // A partial for an actor with no stored state cannot be merged
            // without fabricating the untouched fields, so it is refused. The
            // client always sends a FULL before any partial (its mode-1 first
            // sample), so reaching here means the server missed the FULL.
            if (mask != PlayerMovementState.FullMask)
                return ValueTask.FromResult<PlayerMovementState?>(null);
            entry = Track(actorId);
        }

        lock (entry.Gate)
        {
            var current = entry.State;
            var merged = new PlayerMovementState(
                (mask & 0x01) != 0 ? x : current?.X ?? 0f,
                (mask & 0x02) != 0 ? y : current?.Y ?? 0f,
                (mask & 0x04) != 0 ? z : current?.Z ?? 0f,
                (mask & 0x08) != 0 ? yaw : current?.Yaw ?? 0,
                (mask & 0x10) != 0 ? pitch : current?.Pitch ?? 0,
                (mask & 0x20) != 0 ? state : current?.State ?? 0);
            entry.State = merged;
            return ValueTask.FromResult<PlayerMovementState?>(merged);
        }
    }

    /// <summary>
    /// Tracks a new actor, refusing when the store already holds
    /// <see cref="MaxActors"/> of them.
    ///
    /// </summary>
    /// <param name="actorId">The actor id to track.</param>
    /// <returns>The new entry, untracked by any merge yet.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the store is at
    /// its actor cap.</exception>
    private MovementActorEntry Track(int actorId)
    {
        lock (_gate)
        {
            if (_actors.TryGetValue(actorId, out var existing))
                return existing;
            if (_actors.Count >= MaxActors)
                throw new InvalidOperationException(
                    $"persistence.memory reached its max_movement_actors cap ({MaxActors}); refusing to track actor {actorId}.");
            var entry = new MovementActorEntry();
            _actors[actorId] = entry;
            return entry;
        }
    }

    private static void RequireActorId(int actorId)
    {
        if (actorId <= 0)
            throw new ArgumentOutOfRangeException(nameof(actorId), actorId, "actor id must be a positive client-computed id.");
    }
}

/// <summary>One tracked actor's merged movement state. Guarded by
/// <see cref="Gate"/> so a merge is atomic against concurrent samples of the
/// same actor.</summary>
internal sealed class MovementActorEntry
{
    /// <summary>Gets the lock serializing this actor's merges and reads.</summary>
    public object Gate { get; } = new();

    /// <summary>Gets or sets the merged state, or <see langword="null"/> before
    /// the first merge. The record is immutable, so readers may keep the
    /// reference they read under the gate.</summary>
    public PlayerMovementState? State { get; set; }
}
