using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.Kernel;

/// <summary>Exposes a resource child whose raw cleanup remains owned by its parent.</summary>
internal sealed class ResourceScope : IResourceScope, IResourceHandle
{
    private readonly ResourceOwner _owner;
    private IAsyncDisposable? _ownership;

    internal ResourceScope(string name, ResourceOwner owner)
    {
        Name = name;
        _owner = owner;
        Resource = new ScopeResource(this);
    }

    internal IAsyncDisposable Resource { get; }
    internal void Attach(IAsyncDisposable ownership) => _ownership = ownership;
    public string Name { get; }
    public CancellationToken Lifetime => _owner.Effects.Lifetime;
    public IDisposable Effect(IDisposable disposer) => _owner.Effect(disposer);
    public IDisposable Effect(Action dispose) => _owner.Effect(dispose);
    public ValueTask<IAsyncDisposable> AcquireEffectAsync(Func<CancellationToken, ValueTask<IAsyncDisposable>> acquire, CancellationToken cancellationToken = default) =>
        _owner.AcquireEffectAsync(acquire, cancellationToken);
    public ValueTask<IOwnedTask> RunTaskAsync(string name, Func<CancellationToken, ValueTask> work, CancellationToken cancellationToken = default) =>
        _owner.RunTaskAsync(name, work, cancellationToken);
    public ValueTask<IResourceScope> CreateResourceScopeAsync(string name) => _owner.CreateResourceScopeAsync(name);
    public ValueTask DisposeAsync()
    {
        _owner.Effects.EnsureCanClose();
        return _ownership!.DisposeAsync();
    }

    private sealed class ScopeResource(ResourceScope scope) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try { await scope._owner.Effects.DisposeAsync().ConfigureAwait(false); }
            catch (Exception failure) when (OwnedWorkException.IsDrained(failure))
            {
                // Only the child that joined every resource may rebase drained-work provenance.
                throw new OwnedWorkException(scope.Name, failure, this);
            }
        }
    }
}
