using System.Collections.Concurrent;
using System.Text.Json;
using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.Loader.Remote;

/// <summary>Bridges SDK resource scopes and release callbacks to native resource ownership.</summary>
internal sealed partial class RemoteActivation
{
    private readonly ConcurrentDictionary<string, RemoteResourceScope> _resourceScopes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RemoteResource> _resources = new(StringComparer.Ordinal);
    private long _resourceScopeCounter;
    private long _resourceCounter;
    private int _reservedResourceScopes;

    /// <summary>Creates a root or child scope whose capacity is charged to the activation.</summary>
    public async Task<JsonElement> CreateResourceScopeAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var name = RequiredString(parameters, "name");
        cancellationToken.ThrowIfCancellationRequested();
        string? parent = null;
        if (parameters.TryGetProperty("parent", out _))
            parent = RequiredString(parameters, "parent");
        lock (_handleGate)
        {
            if (_reservedResourceScopes >= _maxHandles)
                throw new RpcException(RpcCodes.Overloaded, "the activation resource scope capacity is exhausted");
            _reservedResourceScopes++;
        }
        IResourceScope scope;
        try
        {
            scope = parent is not null
                ? await FindScope(parent).Scope.CreateResourceScopeAsync(name).ConfigureAwait(false)
                : await Context.CreateResourceScopeAsync(name).ConfigureAwait(false);
        }
        catch
        {
            lock (_handleGate) _reservedResourceScopes--;
            throw;
        }
        var id = "s" + Interlocked.Increment(ref _resourceScopeCounter);
        var entry = new RemoteResourceScope(id, parent, scope);
        if (!_resourceScopes.TryAdd(id, entry))
        {
            lock (_handleGate) _reservedResourceScopes--;
            await scope.DisposeAsync().ConfigureAwait(false);
            throw new RpcException(RpcCodes.InternalError, "resource scope identity allocation collided");
        }
        return JsonSerializer.SerializeToElement(new { scope = id });
    }

    /// <summary>Disposes a scope, or defers disposal while its activation parent is unwinding.</summary>
    public async Task<JsonElement> DisposeResourceScopeAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var id = RequiredString(parameters, "scope");
        var scope = FindScope(id);
        if (Volatile.Read(ref _deactivating) != 0 || Context.Lifetime.IsCancellationRequested)
            return JsonSerializer.SerializeToElement(new { deferred = true });
        await scope.Scope.DisposeAsync().ConfigureAwait(false);
        ForgetScopeTree(id);
        return JsonSerializer.SerializeToElement(new { });
    }

    private void ForgetScopeTree(string id)
    {
        // Native parent disposal joins every descendant before their remote identities can be retired.
        lock (_handleGate)
        {
            foreach (var child in _resourceScopes.Values.Where(scope => scope.Parent == id))
                ForgetScopeTree(child.Id);
            if (_resourceScopes.TryRemove(id, out _)) _reservedResourceScopes--;
        }
    }

    /// <summary>Acquires a native-owned resource whose release callback remains in the SDK process.</summary>
    public async Task<JsonElement> AcquireResourceAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var scopeId = RequiredString(parameters, "scope");
        var callback = RequiredString(parameters, "callback");
        var scope = FindScope(scopeId);
        var id = "r" + Interlocked.Increment(ref _resourceCounter);
        var resource = new RemoteResource(this, id, scopeId, callback);
        if (!_resources.TryAdd(id, resource))
            throw new RpcException(RpcCodes.InternalError, "resource identity allocation collided");
        try
        {
            await scope.Scope.AcquireEffectAsync(_ => ValueTask.FromResult<IAsyncDisposable>(resource), cancellationToken).ConfigureAwait(false);
            return JsonSerializer.SerializeToElement(new { resource = id });
        }
        catch
        {
            _resources.TryRemove(id, out _);
            await resource.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Releases one resource early, preserving native ownership until its callback returns.</summary>
    public async Task<JsonElement> DisposeResourceAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var id = RequiredString(parameters, "resource");
        var resource = FindResource(id);
        var invocation = parameters.TryGetProperty("release_invocation", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
        if (invocation is not null && StringComparer.Ordinal.Equals(invocation, resource.ActiveInvocation))
            throw new RpcException(RpcCodes.InvalidParams, "a resource cannot be disposed from its own release callback");
        if (Volatile.Read(ref _deactivating) != 0 || Context.Lifetime.IsCancellationRequested)
        {
            _resources.TryRemove(id, out _);
            return JsonSerializer.SerializeToElement(new { deferred = true });
        }
        await resource.DisposeAsync().ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(new { });
    }

    private RemoteResourceScope FindScope(string id) =>
        _resourceScopes.TryGetValue(id, out var scope)
            ? scope
            : throw new RpcException(RpcCodes.InvalidParams, $"resource scope '{id}' is unknown, foreign or disposed");

    private RemoteResource FindResource(string id) =>
        _resources.TryGetValue(id, out var resource)
            ? resource
            : throw new RpcException(RpcCodes.InvalidParams, $"resource '{id}' is unknown, foreign or disposed");

    private sealed record RemoteResourceScope(string Id, string? Parent, IResourceScope Scope);

    private sealed class RemoteResource(RemoteActivation owner, string id, string scope, string callback) : IAsyncDisposable
    {
        private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _state;
        internal string? ActiveInvocation { get; private set; }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
            {
                await _completed.Task.ConfigureAwait(false);
                return;
            }
            var invocation = Guid.NewGuid().ToString("N");
            ActiveInvocation = invocation;
            try
            {
                await owner.Source.InvokeResourceReleaseAsync(owner,
                    new { activation = owner.Id, resource = id, scope, callback, invocation },
                    new { activation = owner.Id, resource = id, invocation }, CancellationToken.None).ConfigureAwait(false);
                owner._resources.TryRemove(id, out _);
                Volatile.Write(ref _state, 2);
                _completed.TrySetResult();
            }
            catch (Exception error)
            {
                Volatile.Write(ref _state, 2);
                _completed.TrySetException(error);
                throw;
            }
        }
    }
}
