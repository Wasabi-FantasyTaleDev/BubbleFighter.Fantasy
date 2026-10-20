using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.Kernel;

/// <summary>Applies the same acquisition and ownership rules to activation and child resources.</summary>
internal sealed class ResourceOwner(EffectScope effects, Func<Exception?> workFailure, Action<string, Exception> reportFailure)
{
    internal EffectScope Effects { get; } = effects;

    internal IDisposable OwnRegistration(Func<IDisposable> acquire)
    {
        var handle = Effects.AcquireAsync(_ =>
            ValueTask.FromResult<IAsyncDisposable>(new SynchronousResource(acquire())))
            .AsTask().GetAwaiter().GetResult();
        return Wrap(handle);
    }

    internal IDisposable Wrap(IAsyncDisposable handle, OwnedCallbackRegistration? callbacks = null) =>
        new SynchronousHandle(this, handle, callbacks);

    internal IDisposable Effect(IDisposable disposer)
    {
        ArgumentNullException.ThrowIfNull(disposer);
        if (disposer is SynchronousHandle owned)
        {
            if (!ReferenceEquals(owned.Owner, this))
                throw new InvalidOperationException("An effect handle already belongs to another resource scope.");
            return owned;
        }
        if (disposer is IResourceHandle)
            throw new InvalidOperationException("An ownership handle cannot be adopted as a raw resource.");
        var admitted = false;
        try
        {
            var handle = Effects.AcquireAsync(_ =>
            {
                admitted = true;
                return ValueTask.FromResult<IAsyncDisposable>(new SynchronousResource(disposer));
            }).AsTask().GetAwaiter().GetResult();
            return Wrap(handle);
        }
        catch (Exception admissionError)
        {
            if (!admitted)
            {
                try { disposer.Dispose(); }
                catch (Exception cleanupError)
                {
                    throw new AggregateException("A rejected registration could not be released.", admissionError, cleanupError);
                }
            }
            throw;
        }
    }

    internal IDisposable Effect(Action dispose)
    {
        ArgumentNullException.ThrowIfNull(dispose);
        return Effect(new ActionDisposer(dispose));
    }

    internal ValueTask<IAsyncDisposable> AcquireEffectAsync(
        Func<CancellationToken, ValueTask<IAsyncDisposable>> acquire, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(acquire);
        return Effects.AcquireAsync(token => new ValueTask<IAsyncDisposable>(
            Task.Run(async () => await acquire(token).ConfigureAwait(false))), cancellationToken);
    }

    internal async ValueTask<IOwnedTask> RunTaskAsync(
        string name, Func<CancellationToken, ValueTask> work, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(work);
        cancellationToken.ThrowIfCancellationRequested();
        if (workFailure() is { } failure)
            throw new InvalidOperationException("Owned work has already failed in this activation.", failure);
        OwnedTask? task = null;
        var ownership = await Effects.AcquireAsync(_ =>
        {
            task = new OwnedTask(name, Effects, cancellationToken, work, reportFailure);
            return ValueTask.FromResult(task.Resource);
        }).ConfigureAwait(false);
        task!.Attach(ownership);
        return task;
    }

    internal async ValueTask<IResourceScope> CreateResourceScopeAsync(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ResourceScope? child = null;
        var ownership = await Effects.AcquireAsync(_ =>
        {
            child = new ResourceScope(name, new ResourceOwner(new EffectScope(Effects), workFailure, reportFailure));
            return ValueTask.FromResult(child.Resource);
        }).ConfigureAwait(false);
        child!.Attach(ownership);
        return child;
    }

    private sealed class SynchronousResource(IDisposable disposer) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() { disposer.Dispose(); return ValueTask.CompletedTask; }
    }

    private sealed class SynchronousHandle(ResourceOwner owner, IAsyncDisposable handle, OwnedCallbackRegistration? callbacks)
        : IDisposable, IResourceHandle
    {
        public ResourceOwner Owner { get; } = owner;
        public void Dispose()
        {
            var selfRemoval = callbacks?.IsExecuting == true;
            var release = handle.DisposeAsync();
            // Self-removal retains ownership until the current callback finishes.
            if (selfRemoval && !release.IsCompleted) return;
            release.AsTask().GetAwaiter().GetResult();
        }
    }

    private sealed class ActionDisposer(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}
