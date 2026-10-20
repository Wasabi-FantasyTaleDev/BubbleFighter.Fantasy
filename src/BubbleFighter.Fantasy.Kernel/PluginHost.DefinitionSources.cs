using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.Kernel;

public sealed partial class PluginHost
{
    private readonly Dictionary<(PluginContext Owner, string Id), DefinitionSource> _definitionSources = [];
    private readonly HashSet<DefinitionSource> _ownedDefinitionSources = [];
    private readonly HashSet<PluginContext> _hostedContexts = [];
    private readonly Dictionary<PluginContext, DefinitionSource> _sourceReaders = [];
    private long _definitionSourceRevision;
    private int _reservedDefinitions;

    /// <inheritdoc />
    public IReadOnlyList<PluginDefinitionSourceInfo> DefinitionSources
    {
        get { lock (_intentGate) return _ownedDefinitionSources.Select(source => source.Info).OrderBy(info => info.Revision).ToArray(); }
    }

    /// <inheritdoc />
    public async ValueTask<IPluginDefinitionRegistration> RegisterDefinitionSourceAsync(IPluginContext owner,
        string sourceId, int definitionCapacity, IReadOnlyList<string> coordinationKeys,
        Func<CancellationToken, ValueTask<IPluginDefinitionBatch>> acquire, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentNullException.ThrowIfNull(coordinationKeys);
        ArgumentNullException.ThrowIfNull(acquire);
        cancellationToken.ThrowIfCancellationRequested();
        if (definitionCapacity <= 0 || definitionCapacity > _budget.DefinitionCapacity)
            throw new ConfigException("Definition source reservation must be positive and within the startup definition capacity.");
        var keyCount = coordinationKeys.Count;
        if (keyCount < 0 || keyCount > definitionCapacity)
            throw new ConfigException("Definition source keys exceed its reserved definition capacity.");
        var keys = new string[keyCount];
        for (var index = 0; index < keyCount; index++) keys[index] = coordinationKeys[index];
        if (coordinationKeys.Count != keyCount) throw new ConfigException("Definition source keys changed during capture.");
        if (keys.Any(string.IsNullOrWhiteSpace) || keys.Distinct(StringComparer.Ordinal).Count() != keys.Length)
            throw new ConfigException("Definition source coordination keys must be nonempty and distinct.");
        DefinitionSource source;
        lock (_intentGate)
        {
            if (owner is not PluginContext context || !_hostedContexts.Contains(context) ||
                context.IsClosing || context.IsSuperseded || _shutdownInProgress)
                throw new InvalidOperationException("Definition sources require a current hosted activation owner.");
            if (_definitionSources.ContainsKey((context, sourceId))) throw new ConfigException($"Definition source '{sourceId}' is already owned by this activation.");
            if (_ownedDefinitionSources.Count >= _budget.DefinitionSourceCapacity ||
                (long)_reservedDefinitions + definitionCapacity + _bootstrapDefinitionCount > _budget.DefinitionCapacity)
                throw new ConfigException("Definition source reservation exceeds the fixed startup capacity.");
            var revision = checked(_definitionSourceRevision + 1);
            var keyLease = _modules.RetainSourceKeys(keys);
            source = CreateSourceRegistration(context, sourceId, revision, definitionCapacity, keys, keyLease);
            _definitionSources.Add((context, sourceId), source);
            _ownedDefinitionSources.Add(source);
            _reservedDefinitions += definitionCapacity;
            _definitionSourceRevision = revision;
        }
        try
        {
            source.Ownership = await source.Owner.AcquireEffectAsync(_ => ValueTask.FromResult<IAsyncDisposable>(new DefinitionSourceResource(source))).ConfigureAwait(false);
        }
        catch
        {
            source.AcquisitionSettled.TrySetResult();
            await source.RetireAsync().ConfigureAwait(false);
            throw;
        }
        lock (_intentGate)
        {
            if (source.State != PluginDefinitionSourceState.Preparing)
                throw new InvalidOperationException("Definition source owner closed before acquisition could start.");
            source.AcquisitionStarted = true;
        }
        var operation = AcquireDefinitionSourceAsync(source, acquire);
        _ = operation.ContinueWith(completed => _ = completed.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return await operation.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private readonly int _bootstrapDefinitionCount;

    private async Task<IPluginDefinitionRegistration> AcquireDefinitionSourceAsync(DefinitionSource source,
        Func<CancellationToken, ValueTask<IPluginDefinitionBatch>> acquire)
    {
        IDisposable? production = null;
        try
        {
            production = EnterSourceProduction(source);
            source.ProducerInvoked = true;
            source.Batch = await InvokeSourceProducerAsync(source, acquire).ConfigureAwait(false)
                ?? throw new ConfigException("Definition source returned no owned batch.");
            var definitions = source.Batch.Definitions ?? throw new ConfigException("Definition source returned no definitions.");
            var count = definitions.Count;
            if (count < 1 || count > source.Capacity)
                throw new ConfigException("Definition batch exceeds its reserved capacity or is empty.");
            var descriptors = new PluginDescriptor[count];
            for (var index = 0; index < count; index++)
                descriptors[index] = new PluginDescriptor(definitions[index] ?? throw new ConfigException("Definition batch contains a null definition."));
            if (definitions.Count != count) throw new ConfigException("Definition batch changed while being captured.");
            _ = PluginCatalog.FromDefinitions(descriptors);
            if (descriptors.Any(descriptor => descriptor.CoordinationKey is { } key && !source.Keys.Contains(key, StringComparer.Ordinal)))
                throw new ConfigException("Definition batch uses an unreserved coordination key.");
            var replaced = source.Replaces;
            using (await EnterDefinitionMetadataMutationAsync(source.AcquisitionToken).ConfigureAwait(false))
            {
                ConfigurationSnapshot baseline;
                lock (_intentGate) baseline = _configuration;
                var replacedIds = replaced is null ? new HashSet<string>(StringComparer.Ordinal) :
                    baseline.Sources.Where(pair => ReferenceEquals(pair.Value, replaced)).Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);
                var catalog = PluginCatalog.FromDefinitions(baseline.Catalog.Plugins.Where(descriptor => !replacedIds.Contains(descriptor.Id)).Concat(descriptors));
                var tree = PluginEntryTree.Build(baseline.Rows, _registry.Runtime, catalog, baseline.Tree, allowUnavailableIsolation: true);
                var changedScopes = tree.Entries.Values.Where(entry =>
                    !baseline.Tree.Entries.TryGetValue(entry.Id, out var previous) || !ReferenceEquals(previous.Scope, entry.Scope))
                    .Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
                var sources = baseline.Sources.Where(pair => !ReferenceEquals(pair.Value, replaced))
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                foreach (var descriptor in descriptors) sources.Add(descriptor.Id, source);
                RejectSourceOwnerNamespaceCycles(baseline.Rows, catalog, sources);
                var watches = BuildBindingWatches(baseline.Rows, tree, row =>
                    descriptors.FirstOrDefault(descriptor => descriptor.Id == row.Plugin) ??
                    (replacedIds.Contains(row.Plugin) ? null : DefinitionFor(row)));
                lock (_intentGate)
                {
                    if (!ReferenceEquals(baseline, _configuration) || source.State != PluginDefinitionSourceState.Preparing ||
                        source.AcquisitionToken.IsCancellationRequested || source.Owner.IsClosing || source.Owner.IsSuperseded ||
                        !_hostedContexts.Contains(source.Owner) || _shutdownInProgress)
                        throw new InvalidOperationException("Definition source admission was superseded before publication.");
                    if (replaced is not null && (!replaced.IsPublished || !ReferenceEquals(replaced.Replacement, source) ||
                        !_definitionSources.TryGetValue((source.Owner, source.Id), out var current) || !ReferenceEquals(current, replaced)))
                        throw new InvalidOperationException("The expected source registration was revoked or replaced before publication.");
                    source.CatalogPins = _moduleOwnership.RetainCatalogBatch(descriptors);
                    source.Descriptors = descriptors;
                    source.State = PluginDefinitionSourceState.Published;
                    if (replaced is not null)
                    {
                        CaptureSourceNamespaceReaders(replaced, changedScopes, tree);
                        MarkSourceReadersRetiring(replaced);
                        _definitionSources[(source.Owner, source.Id)] = source;
                        replaced.Replacement = null;
                        source.Replaces = null;
                    }
                    MarkChangedSourceNamespaces(changedScopes, tree);
                    Volatile.Write(ref _configuration, new(baseline.Revision, baseline.Rows, baseline.EntryRevisions, tree, catalog, sources));
                    _bindingReaders = watches;
                    foreach (var row in baseline.Rows.Where(row => replacedIds.Contains(row.Plugin) || descriptors.Any(descriptor => descriptor.Id == row.Plugin)))
                        _bindingDirtyRows.Add(row.Id);
                    if (_bindingDirtyRows.Count > 0)
                    {
                        _bindingChangeStamp = new();
                        _bindingChangePending = true;
                        if (_bindingSettlement is null || _bindingSettlement.Task.IsCompleted)
                            _bindingSettlement = new(TaskCreationOptions.RunContinuationsAsynchronously);
                        ScheduleIntents();
                    }
                }
            }
            production.Dispose();
            production = null;
            source.AcquisitionSettled.TrySetResult();
            if (replaced is not null) ObserveRetiredSource(replaced);
            return source;
        }
        catch (Exception acquisitionError)
        {
            // Cleanup borrows this source's reservation until its real resources and cancellation callbacks settle.
            source.AcquisitionSettled.TrySetResult();
            try { await source.Ownership!.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanupError) { throw new AggregateException("Definition source acquisition failed and cleanup retained ownership.", acquisitionError, cleanupError); }
            throw;
        }
        finally { production?.Dispose(); }
    }

    private void EnsureDefinitionSource(DefinitionSource? source, PluginContext context)
    {
        if (source is null) return;
        lock (_intentGate)
            if (!source.IsPublished || source.Owner.IsClosing || source.Owner.IsSuperseded ||
                !_hostedContexts.Contains(source.Owner) || !EntryIsReady(source.Owner.Row.Id))
            { context.Supersede(); throw new InvalidOperationException("Definition source is revoked or its owning activation is unavailable."); }
    }

    private bool IsDefinitionSourceRevoked(PluginContext context)
    {
        lock (_intentGate) return _sourceReaders.TryGetValue(context, out var source) && !source.IsPublished;
    }

    private void ReleaseDefinitionSource(PluginContext context)
    {
        lock (_intentGate)
        {
            _hostedContexts.Remove(context);
            if (_sourceReaders.Remove(context, out var source) && !source.IsPublished && !_sourceReaders.Values.Contains(source))
                source.ReadersDrained.TrySetResult();
            ReleaseSourceNamespaceReaders(context);
        }
    }

    private async Task RetireDefinitionSourceAsync(DefinitionSource source)
    {
        try
        {
            Task readers;
            lock (_intentGate)
            {
                MarkSourceReadersRetiring(source);
                readers = source.ReadersDrained.Task;
                if (_bindingDirtyRows.Count > 0)
                {
                    _bindingChangeStamp = new();
                    _bindingChangePending = true;
                    if (_bindingSettlement is null || _bindingSettlement.Task.IsCompleted)
                        _bindingSettlement = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    ScheduleIntents();
                }
            }
            if (source.AcquisitionStarted) await source.AcquisitionSettled.Task.ConfigureAwait(false);
            using (await EnterDefinitionMetadataMutationAsync(CancellationToken.None).ConfigureAwait(false))
            {
                ConfigurationSnapshot baseline;
                lock (_intentGate) baseline = _configuration;
                var ids = baseline.Sources.Where(pair => ReferenceEquals(pair.Value, source)).Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);
                if (ids.Count > 0)
                {
                    var retained = baseline.Sources.Where(pair => !ReferenceEquals(pair.Value, source)).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                    var catalog = PluginCatalog.FromDefinitions(baseline.Catalog.Plugins.Where(descriptor => !ids.Contains(descriptor.Id)));
                    var tree = PluginEntryTree.Build(baseline.Rows, _registry.Runtime, catalog, baseline.Tree, allowUnavailableIsolation: true);
                    var changedScopes = tree.Entries.Values.Where(entry =>
                        !baseline.Tree.Entries.TryGetValue(entry.Id, out var previous) || !ReferenceEquals(previous.Scope, entry.Scope))
                        .Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
                    var watches = BuildBindingWatches(baseline.Rows, tree, row => ids.Contains(row.Plugin) ? null : DefinitionFor(row));
                    lock (_intentGate)
                    {
                        if (!ReferenceEquals(baseline, _configuration))
                            throw new InvalidOperationException("Configuration changed while source retirement was being captured; source ownership is retained.");
                        CaptureSourceNamespaceReaders(source, changedScopes, tree);
                        EvictSourceDefinitions(source);
                        Volatile.Write(ref _configuration, new(baseline.Revision, baseline.Rows, baseline.EntryRevisions, tree, catalog, retained));
                        _bindingReaders = watches;
                        MarkChangedSourceNamespaces(changedScopes, tree);
                        foreach (var row in baseline.Rows.Where(row => ids.Contains(row.Plugin))) _bindingDirtyRows.Add(row.Id);
                        if (_bindingDirtyRows.Count > 0)
                        {
                            _bindingChangeStamp = new();
                            _bindingChangePending = true;
                            if (_bindingSettlement is null || _bindingSettlement.Task.IsCompleted)
                                _bindingSettlement = new(TaskCreationOptions.RunContinuationsAsynchronously);
                            ScheduleIntents();
                        }
                    }
                }
                else
                {
                    // A replaced or rejected revision must not remove matching ids published by its successor.
                    lock (_intentGate)
                    {
                        CaptureSourceNamespaceReaders(source, new HashSet<string>(StringComparer.Ordinal), baseline.Tree);
                        EvictSourceDefinitions(source);
                    }
                }
            }
            await Task.WhenAll(readers, source.NamespaceReadersDrained.Task).ConfigureAwait(false);
            using (source.ProducerInvoked ? await EnterSourceCleanupAsync(source).ConfigureAwait(false) : null)
            {
                source.AcquisitionCancellation.Request();
                Exception? cleanupFailure = null;
                try { if (source.Batch is { } batch) await batch.DisposeAsync().ConfigureAwait(false); }
                catch (Exception error) { cleanupFailure = error; }
                try { await source.AcquisitionCancellation.CompleteAsync().ConfigureAwait(false); }
                catch (Exception error)
                { cleanupFailure = cleanupFailure is null ? error : new AggregateException(cleanupFailure, error); }
                if (cleanupFailure is not null)
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
            }
            lock (_intentGate)
            {
                source.CatalogPins?.Dispose();
                source.KeyLease.Dispose();
                if (_definitionSources.TryGetValue((source.Owner, source.Id), out var current) && ReferenceEquals(current, source))
                    _definitionSources.Remove((source.Owner, source.Id));
                if (source.Replaces is { } replaced && ReferenceEquals(replaced.Replacement, source))
                    replaced.Replacement = null;
                source.Replaces = null;
                source.Replacement = null;
                _ownedDefinitionSources.Remove(source);
                _reservedDefinitions -= source.Capacity;
                source.State = PluginDefinitionSourceState.Retired;
                source.Batch = null;
                source.Descriptors = [];
                source.NamespaceReaders.Clear();
            }
        }
        catch (Exception error)
        {
            lock (_intentGate) { source.State = PluginDefinitionSourceState.CleanupFailed; source.Error = error.Message; }
            throw;
        }
    }

    private void EvictSourceDefinitions(DefinitionSource source)
    {
        foreach (var pair in _entryDefinitions.Where(pair => ReferenceEquals(pair.Value.Source, source)).ToArray())
            if (_entryDefinitions.TryRemove(pair.Key, out var cached)) cached.Lease.Dispose();
    }

    private sealed class DefinitionSource(PluginHost host, PluginContext owner, string id, long revision, int capacity, string[] keys, IDisposable keyLease)
        : IPluginDefinitionRegistration
    {
        internal SourceAcquisitionCancellation AcquisitionCancellation { get; } = new(owner.Lifetime);
        internal CancellationToken AcquisitionToken => AcquisitionCancellation.Token;
        internal PluginContext Owner { get; } = owner;
        internal string Id { get; } = id;
        internal int Capacity { get; } = capacity;
        internal string[] Keys { get; } = keys;
        internal IDisposable KeyLease { get; } = keyLease;
        internal IDisposable? CatalogPins;
        internal IAsyncDisposable? Ownership;
        internal IPluginDefinitionBatch? Batch;
        internal PluginDescriptor[] Descriptors = [];
        internal volatile PluginDefinitionSourceState State = PluginDefinitionSourceState.Preparing;
        internal string? Error;
        internal bool AcquisitionStarted;
        internal bool ProducerInvoked;
        internal DefinitionSource? Replaces;
        internal DefinitionSource? Replacement;
        internal TaskCompletionSource AcquisitionSettled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReadersDrained { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal HashSet<PluginContext> NamespaceReaders { get; } = [];
        internal bool NamespaceReadersCaptured;
        internal TaskCompletionSource NamespaceReadersDrained { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool IsPublished => State == PluginDefinitionSourceState.Published;
        private Task? _retirement;
        public PluginDefinitionSourceInfo Info
        {
            get { lock (host._intentGate) return new(Id, revision, Owner.Row.Id, State, Descriptors.Select(descriptor => descriptor.Id).ToArray(), Error); }
        }
        public ValueTask<IPluginDefinitionRegistration> ReplaceAsync(int definitionCapacity, IReadOnlyList<string> coordinationKeys,
            Func<CancellationToken, ValueTask<IPluginDefinitionBatch>> acquire, CancellationToken cancellationToken = default) =>
            host.ReplaceDefinitionSourceAsync(this, definitionCapacity, coordinationKeys, acquire, cancellationToken);

        public ValueTask DisposeAsync()
        {
            host.RefuseSourceProducerSelfWait(this);
            host.RequestDefinitionSourceCancellation(this);
            return Ownership?.DisposeAsync() ?? RetireAsync();
        }
        internal ValueTask RetireAsync()
        {
            lock (host._intentGate)
            {
                if (_retirement is not null) return new(_retirement);
                var retiringPreparation = State == PluginDefinitionSourceState.Preparing;
                State = PluginDefinitionSourceState.Retiring;
                // Published token-bound workers remain available until their readers actually drain.
                host.RequestDefinitionSourceCancellation(this, retiringPreparation);
                _retirement = Task.Run(() => host.RetireDefinitionSourceAsync(this));
                return new(_retirement);
            }
        }
    }

    private sealed class DefinitionSourceResource(DefinitionSource source) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => source.RetireAsync();
    }
}
