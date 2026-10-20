using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.Kernel;

public sealed partial class PluginHost
{
    private readonly HashSet<SourceOperation> _sourceOperations = [];
    private readonly Dictionary<PluginContext, BackendOperation> _sourceBackendOperations = [];

    private IDisposable EnterSourceProduction(DefinitionSource source)
    {
        lock (_intentGate)
        {
            var keys = SourceOperationKeys(source);
            if (source.State != PluginDefinitionSourceState.Preparing || source.Owner.IsClosing ||
                source.Owner.IsSuperseded || _shutdownInProgress)
                throw new InvalidOperationException("Definition source production requires a current preparing owner.");
            if (_sourceOperations.Any(operation => operation.Keys.Overlaps(keys)) ||
                _runningExecutions.Any(execution => !ReferenceEquals(execution, CurrentExecution) && execution.Modules.Overlaps(keys)) ||
                _pendingIntents.Values.Any(request => DescribeExecution(request).Modules.Overlaps(keys)) ||
                _sourceBackendOperations.Values.Any(operation => !ReferenceEquals(operation.Context, source.Owner) && keys.Contains(operation.Key)))
                throw new InvalidOperationException("Definition source production conflicts with an executing or queued backend operation; retry after it settles.");
            if (_live.Any(live => live.Descriptor.CoordinationKey is { } key && keys.Contains(key) &&
                    (live.Context.IsClosing || live.Context.WorkFailure is not null)) ||
                _ownedDefinitionSources.Any(owned => owned.State == PluginDefinitionSourceState.CleanupFailed && owned.Keys.Any(keys.Contains)))
                throw new InvalidOperationException("Definition source production is refused while matching backend cleanup or failed work remains owned.");
            return ReserveSourceOperation(source, keys);
        }
    }

    private async ValueTask<IDisposable> EnterSourceCleanupAsync(DefinitionSource source)
    {
        SourceOperationLease reservation;
        Task ready;
        lock (_intentGate)
        {
            var existing = _sourceOperations.FirstOrDefault(operation => ReferenceEquals(operation.Source, source));
            if (existing is not null)
            {
                // Failure cleanup inherits its producer's reservation; later waiters already depend on that producer.
                existing.Depth = checked(existing.Depth + 1);
                reservation = new SourceOperationLease(this, existing);
                ready = existing.Ready;
            }
            else
            {
                var keys = SourceOperationKeys(source);
                // Reserve before waiting so later factories cannot continually overtake retirement.
                var predecessors = _sourceOperations.Where(operation => operation.Keys.Overlaps(keys)).Select(operation => operation.Settled.Task)
                    .Concat(_sourceBackendOperations.Values
                        .Where(operation => !ReferenceEquals(operation.Context, source.Owner) && keys.Contains(operation.Key))
                        .Select(operation => operation.Settled.Task)).ToArray();
                ready = Task.WhenAll(predecessors);
                reservation = ReserveSourceOperation(source, keys, ready);
            }
        }
        try
        {
            await ready.ConfigureAwait(false);
            return reservation;
        }
        catch
        {
            reservation.Dispose();
            throw;
        }
    }

    private IDisposable EnterSourceBackendOperation(PluginContext context, string? key)
    {
        if (key is null) return EmptySourceOperation.Instance;
        lock (_intentGate)
        {
            if (_sourceBackendOperations.TryGetValue(context, out var existing))
            {
                if (!StringComparer.Ordinal.Equals(existing.Key, key))
                    throw new InvalidOperationException("A nested backend operation cannot change its coordination key.");
                existing.Depth = checked(existing.Depth + 1);
                return new BackendOperationLease(this, existing);
            }
            if (_sourceOperations.Any(operation => operation.Keys.Contains(key)))
                throw new InvalidOperationException("Backend operation conflicts with an owned definition source producer or cleanup.");
            if (_sourceBackendOperations.Count >= _budget.EntryCapacity)
                throw new ConfigException("Backend operation reservation exceeds startup entry capacity.");
            var operation = new BackendOperation(context, key);
            _sourceBackendOperations.Add(context, operation);
            return new BackendOperationLease(this, operation);
        }
    }

    private async ValueTask<IDisposable> EnterSourceBackendCleanupAsync(PluginContext context, string? key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (key is null) return EmptySourceOperation.Instance;
        BackendOperationLease lease;
        Task[] predecessors;
        lock (_intentGate)
        {
            if (_sourceBackendOperations.TryGetValue(context, out var existing))
            {
                if (!StringComparer.Ordinal.Equals(existing.Key, key))
                    throw new InvalidOperationException("A nested backend cleanup cannot change its coordination key.");
                existing.Depth = checked(existing.Depth + 1);
                return new BackendOperationLease(this, existing);
            }
            if (_sourceBackendOperations.Count >= _budget.EntryCapacity)
                throw new ConfigException("Backend cleanup reservation exceeds startup entry capacity.");
            predecessors = _sourceOperations.Where(operation => operation.Keys.Contains(key))
                .Select(operation => operation.Settled.Task).ToArray();
            // Publish the waiting cleanup before releasing the gate; later producers cannot overtake it.
            var operation = new BackendOperation(context, key);
            _sourceBackendOperations.Add(context, operation);
            lease = new BackendOperationLease(this, operation);
        }
        try
        {
            await Task.WhenAll(predecessors).WaitAsync(cancellationToken).ConfigureAwait(false);
            return lease;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    private static HashSet<string> SourceOperationKeys(DefinitionSource source)
    {
        // A producer stages separate resources; an unrelated predecessor key must not block an independent backend.
        return new HashSet<string>(source.Keys, StringComparer.Ordinal);
    }

    private bool HasSourceOperationConflict(IntentExecution execution) =>
        _sourceOperations.Any(operation => operation.Keys.Overlaps(execution.Modules));

    private SourceOperationLease ReserveSourceOperation(DefinitionSource source, HashSet<string> keys, Task? ready = null)
    {
        if (_sourceOperations.Count >= _budget.DefinitionSourceCapacity)
            throw new ConfigException("Definition source operation reservation exceeds startup source capacity.");
        var operation = new SourceOperation(source, keys, ready ?? Task.CompletedTask);
        _sourceOperations.Add(operation);
        return new SourceOperationLease(this, operation);
    }

    private sealed class SourceOperation(DefinitionSource source, HashSet<string> keys, Task ready)
    {
        internal DefinitionSource Source { get; } = source;
        internal HashSet<string> Keys { get; } = keys;
        internal Task Ready { get; } = ready;
        internal int Depth = 1;
        internal TaskCompletionSource Settled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class SourceOperationLease(PluginHost host, SourceOperation operation) : IDisposable
    {
        private bool _released;
        public void Dispose()
        {
            lock (host._intentGate)
            {
                if (_released) return;
                _released = true;
                if (--operation.Depth != 0) return;
                host._sourceOperations.Remove(operation);
                operation.Settled.TrySetResult();
                host.ScheduleIntents();
            }
        }
    }

    private sealed class BackendOperation(PluginContext context, string key)
    {
        internal PluginContext Context { get; } = context;
        internal string Key { get; } = key;
        internal int Depth = 1;
        internal TaskCompletionSource Settled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class BackendOperationLease(PluginHost host, BackendOperation operation) : IDisposable
    {
        private bool _released;
        public void Dispose()
        {
            lock (host._intentGate)
            {
                if (_released) return;
                _released = true;
                if (--operation.Depth != 0) return;
                host._sourceBackendOperations.Remove(operation.Context);
                operation.Settled.TrySetResult();
                host.ScheduleIntents();
            }
        }
    }

    private sealed class EmptySourceOperation : IDisposable
    {
        internal static EmptySourceOperation Instance { get; } = new();
        public void Dispose() { }
    }
}
