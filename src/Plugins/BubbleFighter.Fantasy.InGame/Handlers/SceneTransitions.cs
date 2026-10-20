namespace BubbleFighter.Fantasy.InGame.Handlers;

/// <summary>Reserves the participants of a scene move without holding any room executor callback.</summary>
internal sealed class SceneTransitions(int capacity)
{
    private sealed class Participant
    {
        internal readonly SemaphoreSlim Gate = new(1, 1);
        internal int References;
    }

    private readonly object _gate = new();
    private readonly Dictionary<long, Participant> _participants = [];
    private readonly HashSet<TaskCompletionSource> _pending = [];
    private readonly CancellationTokenSource _stopping = new();
    private Task? _stop;

    internal async ValueTask RunAsync(IReadOnlyList<long> sessionIds,
        Func<CancellationToken, ValueTask> operation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Party capacity is at most sixteen; the solo path reserves one connection.
        if (sessionIds.Count is < 1 or > 16)
            throw new ArgumentOutOfRangeException(nameof(sessionIds));
        var ids = sessionIds.Distinct().Order().ToArray();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Participant[] participants;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stop is not null, this);
            if (_pending.Count >= capacity)
                throw new InvalidOperationException("The configured pending scene transition limit has been reached.");
            participants = ids.Select(id =>
            {
                if (!_participants.TryGetValue(id, out var participant))
                    _participants.Add(id, participant = new Participant());
                participant.References++;
                return participant;
            }).ToArray();
            _pending.Add(completion);
        }
        var acquired = 0;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token);
            // Every request reserves in identity order, so overlapping parties cannot acquire opposite locks.
            foreach (var participant in participants)
            {
                await participant.Gate.WaitAsync(linked.Token).ConfigureAwait(false);
                acquired++;
            }
            linked.Token.ThrowIfCancellationRequested();
            // Once admitted, a group move belongs to the owner; a caller disappearing cannot abandon its peers.
            await operation(_stopping.Token).ConfigureAwait(false);
        }
        finally
        {
            for (var i = acquired - 1; i >= 0; i--)
                participants[i].Gate.Release();
            lock (_gate)
            {
                for (var i = 0; i < participants.Length; i++)
                    if (--participants[i].References == 0)
                    {
                        _participants.Remove(ids[i]);
                        participants[i].Gate.Dispose();
                    }
                _pending.Remove(completion);
                completion.TrySetResult();
            }
        }
    }

    internal Task StopAsync()
    {
        lock (_gate)
        {
            if (_stop is not null) return _stop;
            var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _stop = stopped.Task;
            _ = FinishStopAsync(_pending.Select(item => item.Task).ToArray(), stopped);
            return _stop;
        }
    }

    private async Task FinishStopAsync(Task[] pending, TaskCompletionSource stopped)
    {
        try
        {
            await _stopping.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(pending).ConfigureAwait(false);
            _stopping.Dispose();
            stopped.TrySetResult();
        }
        catch (Exception error) { stopped.TrySetException(error); }
    }
}
