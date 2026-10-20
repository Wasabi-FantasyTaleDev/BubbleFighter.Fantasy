namespace BubbleFighter.Fantasy.Kernel;

public sealed partial class PluginHost
{
    private DefinitionSource CreateSourceRegistration(PluginContext owner, string id, long revision, int capacity, string[] keys, IDisposable keyLease)
    {
        try { return new DefinitionSource(this, owner, id, revision, capacity, keys, keyLease); }
        catch { keyLease.Dispose(); throw; }
    }

    private readonly AsyncLocal<SourceProducerFrame?> _sourceProducer = new();

    private async ValueTask<BubbleFighter.Fantasy.Abstractions.IPluginDefinitionBatch> InvokeSourceProducerAsync(DefinitionSource source,
        Func<CancellationToken, ValueTask<BubbleFighter.Fantasy.Abstractions.IPluginDefinitionBatch>> acquire)
    {
        var previous = _sourceProducer.Value;
        var frame = new SourceProducerFrame { Source = source };
        _sourceProducer.Value = frame;
        try
        {
            using var callback = source.Owner.EnterCallback();
            return await acquire(source.AcquisitionToken).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref frame.Source, null);
            _sourceProducer.Value = previous;
        }
    }

    private void RefuseSourceProducerSelfWait(DefinitionSource target)
    {
        var frame = _sourceProducer.Value;
        var producer = frame is null ? null : Volatile.Read(ref frame.Source);
        if (producer is null) return;
        lock (_intentGate)
        {
            if (ReferenceEquals(producer, target) || ReferenceEquals(producer.Replaces, target))
                throw new InvalidOperationException("A definition producer cannot await retirement of its own source or replacement predecessor.");
            var visited = new HashSet<PluginContext>();
            for (var owner = producer.Owner; visited.Add(owner) && _sourceReaders.TryGetValue(owner, out var parent); owner = parent.Owner)
                if (ReferenceEquals(parent, target))
                    throw new InvalidOperationException("A definition producer cannot await retirement of a source owning its activation.");
        }
    }

    private sealed class SourceProducerFrame
    {
        internal DefinitionSource? Source;
    }

    private void RequestDefinitionSourceCancellation(DefinitionSource source, bool retiringPreparation = false)
    {
        DefinitionSource? candidate;
        bool stopProducer;
        lock (_intentGate)
        {
            stopProducer = retiringPreparation || source.State ==
                BubbleFighter.Fantasy.Abstractions.PluginDefinitionSourceState.Preparing;
            candidate = source.Replacement is { State: BubbleFighter.Fantasy.Abstractions.PluginDefinitionSourceState.Preparing } pending
                ? pending : null;
            // CancelAsync marks the token before publication can race past a preparation stop.
            if (stopProducer) source.AcquisitionCancellation.Request();
            candidate?.AcquisitionCancellation.Request();
        }
    }

    private void CancelPreparingDefinitionSources()
    {
        DefinitionSource[] sources;
        lock (_intentGate)
            sources = _ownedDefinitionSources.Where(source => source.State ==
                BubbleFighter.Fantasy.Abstractions.PluginDefinitionSourceState.Preparing).ToArray();
        foreach (var source in sources) RequestDefinitionSourceCancellation(source);
    }

    private sealed class SourceAcquisitionCancellation
    {
        private readonly object _gate = new();
        private readonly CancellationTokenSource _source = new();
        private readonly CancellationTokenRegistration _ownerRegistration;
        private Task? _requested;
        private Task? _completion;
        private bool _closing;

        internal SourceAcquisitionCancellation(CancellationToken ownerLifetime)
        {
            Token = _source.Token;
            try
            {
                _ownerRegistration = ownerLifetime.UnsafeRegister(static state =>
                    ((SourceAcquisitionCancellation)state!).Request(), this);
            }
            catch
            {
                _source.Dispose();
                throw;
            }
        }

        internal CancellationToken Token { get; }

        internal void Request()
        {
            lock (_gate)
            {
                if (_closing || _requested is not null) return;
                try { _requested = _source.CancelAsync(); }
                catch (Exception error) { _requested = Task.FromException(error); }
            }
        }

        internal ValueTask CompleteAsync()
        {
            lock (_gate)
            {
                if (_completion is not null) return new(_completion);
                _closing = true;
                _completion = CompleteCoreAsync();
                return new(_completion);
            }
        }

        private async Task CompleteCoreAsync()
        {
            try
            {
                await _ownerRegistration.DisposeAsync().ConfigureAwait(false);
                Task callbacks;
                lock (_gate) callbacks = _requested ?? Task.CompletedTask;
                await callbacks.ConfigureAwait(false);
            }
            finally
            {
                _source.Dispose();
            }
        }
    }
}
