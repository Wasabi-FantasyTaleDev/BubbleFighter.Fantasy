using BubbleFighter.Fantasy.Contracts.Match;

namespace BubbleFighter.Fantasy.Dialect.Messages;

/// <summary>Stores a bounded set of the latest seed tables per relay connection.</summary>
internal sealed class SceneSeedStore(int maxSessions) : INativeSceneSeedStore
{
    private readonly object _gate = new();
    private readonly Dictionary<long, SessionTables> _tables = [];

    /// <inheritdoc />
    public bool TryGet(long sessionId, int generation, NativeSceneSeedKind kind, out NativeSceneSeedTable table)
    {
        lock (_gate)
        {
            if (_tables.TryGetValue(sessionId, out var session) && session.Generation == generation
                && session.Tables.TryGetValue(kind, out table!))
                return true;
            table = null!;
            return false;
        }
    }

    /// <inheritdoc />
    public void Set(long sessionId, int generation, NativeSceneSeedTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        lock (_gate)
        {
            if (!_tables.TryGetValue(sessionId, out var session))
            {
                if (_tables.Count >= maxSessions)
                    throw new InvalidOperationException($"Native scene seed store reached its {maxSessions} session limit.");
                session = new(generation, []);
                _tables.Add(sessionId, session);
            }
            else if (session.Generation != generation)
            {
                session = new(generation, []);
                _tables[sessionId] = session;
            }
            session.Tables[table.Kind] = table;
        }
    }

    /// <inheritdoc />
    public void Remove(long sessionId)
    {
        lock (_gate) _tables.Remove(sessionId);
    }

    private sealed record SessionTables(int Generation, Dictionary<NativeSceneSeedKind, NativeSceneSeedTable> Tables);
}
