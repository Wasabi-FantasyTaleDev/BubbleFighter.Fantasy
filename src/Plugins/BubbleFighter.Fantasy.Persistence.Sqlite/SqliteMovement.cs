using BubbleFighter.Fantasy.Contracts.Movement;

namespace BubbleFighter.Fantasy.Persistence.Sqlite;

/// <summary>
/// In-process movement state, deliberately outside the durable player snapshot and its disk
/// transaction path: per-actor runtime positions are lost on restart by design, gate-serialized and
/// capacity-bounded.
/// </summary>
// Movement never enters the durable player snapshot or its disk transaction path.
internal sealed class SqliteMovement(int maximum) : IPlayerMovementState
{
    private readonly object _gate = new();
    private readonly Dictionary<int, PlayerMovementState> _actors = [];

    public ValueTask<PlayerMovementState?> FindAsync(int actorId, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(actorId);
        ct.ThrowIfCancellationRequested();
        lock (_gate) return new(_actors.GetValueOrDefault(actorId));
    }

    public ValueTask<PlayerMovementState?> MergeAsync(int actorId, byte mask, float x, float y, float z,
        byte yaw, byte pitch, byte state, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(actorId);
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var current = _actors.GetValueOrDefault(actorId);
            if (current is null)
            {
                if (mask != PlayerMovementState.FullMask) return new((PlayerMovementState?)null);
                if (_actors.Count >= maximum) throw new InvalidOperationException("SQLite provider movement actor capacity reached.");
            }
            var merged = new PlayerMovementState((mask & 1) != 0 ? x : current?.X ?? 0,
                (mask & 2) != 0 ? y : current?.Y ?? 0, (mask & 4) != 0 ? z : current?.Z ?? 0,
                (mask & 8) != 0 ? yaw : current?.Yaw ?? 0, (mask & 16) != 0 ? pitch : current?.Pitch ?? 0,
                (mask & 32) != 0 ? state : current?.State ?? 0);
            _actors[actorId] = merged;
            return new(merged);
        }
    }
}
