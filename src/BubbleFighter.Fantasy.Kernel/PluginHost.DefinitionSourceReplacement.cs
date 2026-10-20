using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.Kernel;

public sealed partial class PluginHost
{
    private async ValueTask<IPluginDefinitionRegistration> ReplaceDefinitionSourceAsync(DefinitionSource previous,
        int definitionCapacity, IReadOnlyList<string> coordinationKeys,
        Func<CancellationToken, ValueTask<IPluginDefinitionBatch>> acquire, CancellationToken cancellationToken)
    {
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
        DefinitionSource candidate;
        lock (_intentGate)
        {
            if (!previous.IsPublished ||
                !_definitionSources.TryGetValue((previous.Owner, previous.Id), out var current) || !ReferenceEquals(current, previous))
                throw new InvalidOperationException("Only the current published source registration can authorize replacement.");
            if (previous.Replacement is not null)
                throw new InvalidOperationException("A replacement of this source is already acquiring or retaining cleanup ownership.");
            if (!_hostedContexts.Contains(previous.Owner) || previous.Owner.IsClosing || previous.Owner.IsSuperseded || _shutdownInProgress)
                throw new InvalidOperationException("Definition source replacement requires its current hosted owner.");
            if (_ownedDefinitionSources.Count >= _budget.DefinitionSourceCapacity ||
                (long)_reservedDefinitions + definitionCapacity + _bootstrapDefinitionCount > _budget.DefinitionCapacity)
                throw new ConfigException("Definition source replacement exceeds the fixed old-plus-new startup capacity.");
            var revision = checked(_definitionSourceRevision + 1);
            var keyLease = _modules.RetainSourceKeys(keys);
            candidate = CreateSourceRegistration(previous.Owner, previous.Id, revision, definitionCapacity, keys, keyLease);
            candidate.Replaces = previous;
            previous.Replacement = candidate;
            _ownedDefinitionSources.Add(candidate);
            _reservedDefinitions += definitionCapacity;
            _definitionSourceRevision = revision;
        }
        try
        {
            candidate.Ownership = await candidate.Owner.AcquireEffectAsync(_ =>
                ValueTask.FromResult<IAsyncDisposable>(new DefinitionSourceResource(candidate))).ConfigureAwait(false);
        }
        catch
        {
            candidate.AcquisitionSettled.TrySetResult();
            await candidate.RetireAsync().ConfigureAwait(false);
            throw;
        }
        lock (_intentGate)
        {
            if (candidate.State != PluginDefinitionSourceState.Preparing)
                throw new InvalidOperationException("Definition source owner closed before replacement acquisition could start.");
            candidate.AcquisitionStarted = true;
        }
        var operation = AcquireDefinitionSourceAsync(candidate, acquire);
        _ = operation.ContinueWith(completed => _ = completed.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return await operation.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private void MarkSourceReadersRetiring(DefinitionSource source)
    {
        source.State = PluginDefinitionSourceState.Retiring;
        foreach (var pair in _sourceReaders.Where(pair => ReferenceEquals(pair.Value, source)))
        {
            pair.Key.Supersede();
            _bindingDirtyRows.Add(pair.Key.Row.Id);
            foreach (var dependent in DependentsClosure(pair.Key.Row.Id)) _bindingDirtyRows.Add(dependent.Row.Id);
        }
        if (!_sourceReaders.Values.Contains(source)) source.ReadersDrained.TrySetResult();
    }

    private void ObserveRetiredSource(DefinitionSource source)
    {
        try
        {
            var retirement = source.DisposeAsync().AsTask();
            _ = retirement.ContinueWith(completed => _ = completed.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        catch (Exception error)
        {
            // Successful replacement stays published even if initiating old cleanup reports a failure.
            lock (_intentGate)
            {
                source.State = PluginDefinitionSourceState.CleanupFailed;
                source.Error = error.Message;
            }
        }
    }
}
