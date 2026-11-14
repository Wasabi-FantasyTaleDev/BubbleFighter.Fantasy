using BubbleFighter.Fantasy.Contracts.Progress;

namespace BubbleFighter.Fantasy.Persistence.Memory;

/// <summary>Bounds process-local successful practice identities for development profiles.</summary>
public sealed class MemoryTutorialPractice(int maximumEntries) : ITutorialPracticeStore
{
    private readonly HashSet<TutorialPracticeKey> _clears = [];
    private readonly Lock _gate = new();

    /// <summary>Reads a committed practice identity under the store gate.</summary>
    public ValueTask<bool> HasClearedAsync(TutorialPracticeKey key, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);
        ct.ThrowIfCancellationRequested();
        lock (_gate) return ValueTask.FromResult(_clears.Contains(key));
    }

    /// <summary>Inserts a practice identity once, refusing new identities when the configured capacity is full.</summary>
    public ValueTask<bool> RecordClearAsync(TutorialPracticeKey key, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_clears.Contains(key)) return ValueTask.FromResult(false);
            if (_clears.Count >= maximumEntries) throw new InvalidOperationException("Tutorial practice storage is full.");
            _clears.Add(key);
            return ValueTask.FromResult(true);
        }
    }
}
