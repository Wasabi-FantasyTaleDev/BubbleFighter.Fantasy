using BubbleFighter.Fantasy.Contracts.Movement;

namespace BubbleFighter.Fantasy.InGame.Sessions;

/// <summary>
/// The last movement of each scene object a client created and moves itself during one round: the NPCs a horde or
/// pirate host runs and the objects any player adds from its own seat's scene range. The record count is bounded; a
/// round that creates more objects forgets the oldest record first, whose next partial report is then dropped until
/// a full one arrives.
/// </summary>
internal sealed class SceneObjectMovements(int capacity)
{
    private readonly Dictionary<int, PlayerMovementState> _records = [];
    private readonly Queue<int> _order = new();

    internal int Count => _records.Count;

    internal PlayerMovementState? Find(int objectId) => _records.GetValueOrDefault(objectId);

    internal void Store(int objectId, PlayerMovementState state)
    {
        if (!_records.ContainsKey(objectId))
        {
            while (_records.Count >= capacity && _order.TryDequeue(out var oldest))
                _records.Remove(oldest);
            _order.Enqueue(objectId);
        }
        _records[objectId] = state;
    }
}
