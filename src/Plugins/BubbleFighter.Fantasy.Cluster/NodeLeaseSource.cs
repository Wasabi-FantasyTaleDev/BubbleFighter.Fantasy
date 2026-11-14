using BubbleFighter.Fantasy.Contracts.Cluster;

namespace BubbleFighter.Fantasy.Cluster;

internal sealed class NodeLeaseSource(string nodeId, string directoryUrl) : INodeLeaseSource
{
    private readonly Lock _gate = new();
    private NodeLeaseCredentials? _current;
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public string NodeId => nodeId;
    public string DirectoryUrl => directoryUrl;
    public NodeLeaseCredentials? Current => Volatile.Read(ref _current);

    // Compared and handed out under the lock Publish takes: a caller whose lease is already stale gets a completed
    // task, and one whose lease is current gets the task the next different lease completes.
    public Task WhenChangedFrom(NodeLeaseCredentials? seen)
    {
        lock (_gate) return Equals(_current, seen) ? _changed.Task : Task.CompletedTask;
    }

    internal void Publish(LeaseGrant? lease)
    {
        var next = lease is null ? null : new NodeLeaseCredentials(lease.Token, lease.Epoch);
        TaskCompletionSource? stale = null;
        lock (_gate)
        {
            // Every heartbeat publishes; only a different lease wakes the waiters.
            if (!Equals(_current, next))
            {
                stale = _changed;
                _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            Volatile.Write(ref _current, next);
        }
        stale?.TrySetResult();
    }
}
