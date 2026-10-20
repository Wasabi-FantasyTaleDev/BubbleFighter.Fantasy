using BubbleFighter.Fantasy.Contracts.Progress;

namespace BubbleFighter.Fantasy.Persistence.Memory;

/// <summary>Bounded, process-local tutorial stage clears for development profiles; lost on restart.</summary>
public sealed class MemoryTutorialStages(int maximumPlayers) : ITutorialStageStore
{
    // A legacy tutorial room offers at most three tabs of ten rows.
    private const int MaximumStagesPerPlayer = 64;

    private readonly Dictionary<long, SortedDictionary<int, TutorialStageClear>> _clears = [];
    private readonly Lock _gate = new();

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<TutorialStageClear>> ListAsync(long userNum, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
            return ValueTask.FromResult<IReadOnlyList<TutorialStageClear>>(
                _clears.TryGetValue(userNum, out var stages) ? [.. stages.Values] : []);
    }

    /// <inheritdoc />
    public ValueTask<bool> RecordClearAsync(long userNum, TutorialStageClear clear, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(clear);
        lock (_gate)
        {
            if (!_clears.TryGetValue(userNum, out var stages))
            {
                if (_clears.Count >= maximumPlayers) throw new InvalidOperationException("Tutorial stage storage is full.");
                _clears[userNum] = stages = [];
            }
            if (stages.TryGetValue(clear.Stage, out var earlier))
            {
                stages[clear.Stage] = Best(earlier, clear);
                return ValueTask.FromResult(false);
            }
            if (stages.Count >= MaximumStagesPerPlayer) throw new InvalidOperationException("A player's tutorial stage clears are full.");
            stages[clear.Stage] = clear;
            return ValueTask.FromResult(true);
        }
    }

    // Higher counts and shorter times are better; 0 means the clear carried no such record.
    private static TutorialStageClear Best(TutorialStageClear earlier, TutorialStageClear clear) => earlier with
    {
        BestCount = Math.Max(earlier.BestCount, clear.BestCount),
        BestSeconds = earlier.BestSeconds <= 0 ? clear.BestSeconds
            : clear.BestSeconds <= 0 ? earlier.BestSeconds : Math.Min(earlier.BestSeconds, clear.BestSeconds),
    };
}
