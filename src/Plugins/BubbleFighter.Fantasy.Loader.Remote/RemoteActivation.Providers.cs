using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.Loader.Remote;

internal sealed partial class RemoteActivation
{
    private readonly object _handleGate = new();
    private readonly HashSet<string> _providerCallbacks = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ProviderInvocation> _providerInvocations = new(StringComparer.Ordinal);
    private int _reservedHandles;

    /// <summary>Registers a declared SDK provider or decorator in this activation's ordinary service registry.</summary>
    public JsonElement Provide(JsonElement parameters)
    {
        var service = RequiredString(parameters, "service");
        var mode = RequiredString(parameters, "mode");
        var callback = RequiredString(parameters, "callback");
        var declarations = mode switch
        {
            "singleton" or "decoratable" => Definition.ProvidedServices,
            "many" => Definition.ProvidedManyServices,
            "decorate" => Definition.DecoratedServices,
            _ => throw new RpcException(RpcCodes.InvalidParams, $"unknown service registration mode '{mode}'"),
        };
        if (!declarations.TryGetValue(service, out var type))
            throw new RpcException(RpcCodes.TypeNotAllowed, $"service '{service}' is not declared for mode '{mode}'");
        var priority = 0;
        if (parameters.TryGetProperty("priority", out var supplied) && (supplied.ValueKind != JsonValueKind.Number || !supplied.TryGetInt32(out priority)))
            throw new RpcException(RpcCodes.InvalidParams, "'priority' must be an int32");
        if (priority != 0 && mode is "singleton" or "decoratable")
            throw new RpcException(RpcCodes.InvalidParams, "singleton provider registrations do not have a priority");
        if (!parameters.TryGetProperty("methods", out var methods))
            throw new RpcException(RpcCodes.InvalidParams, "'methods' is required");
        var provider = new RemoteServiceProvider(this, type, callback, methods);
        lock (_handleGate)
        {
            // Hosted consumers retain selected decorators after unregister; their SDK targets survive until row teardown.
            if (_providerCallbacks.Count + _eventCallbacks.Count >= _maxHandles)
                throw new RpcException(RpcCodes.Overloaded, $"the activation retains {_maxHandles} provider callbacks (effect_capacity)");
            if (!_providerCallbacks.Add(callback))
                throw new RpcException(RpcCodes.InvalidParams, $"provider callback '{callback}' already exists");
        }
        try { return AddHandle(_ => Context.Effect(provider.Register(mode, priority))); }
        catch (Exception error)
        {
            lock (_handleGate) _providerCallbacks.Remove(callback);
            if (error is TargetInvocationException { InnerException: { } inner })
                ExceptionDispatchInfo.Capture(inner).Throw();
            throw;
        }
    }

    private JsonElement AddHandle(Func<string, IDisposable> acquire)
    {
        lock (_handleGate)
        {
            if (Volatile.Read(ref _deactivating) != 0 || _faulted.Task.IsCompleted || Context.Lifetime.IsCancellationRequested)
                throw new RpcException(RpcCodes.ActivationNotLive, "the activation is stopping");
            if (_reservedHandles >= _maxHandles)
                throw new RpcException(RpcCodes.Overloaded, $"the activation already holds {_maxHandles} handles (effect_capacity)");
            _reservedHandles++;
        }
        try
        {
            var handle = "h" + Interlocked.Increment(ref _handleCounter);
            var registration = acquire(handle);
            _handles[handle] = registration;
            return JsonSerializer.SerializeToElement(new { handle });
        }
        catch
        {
            lock (_handleGate) _reservedHandles--;
            throw;
        }
    }

    internal async Task<object?> InvokeProviderAsync(RemoteServiceProvider provider, MethodInfo method, object?[] arguments, object? inner)
    {
        // The transport dispatches nested RPCs in another execution context. Capture the admitted native
        // invocation so a scoped inner call selects that same decorator chain, never a later live chain.
        var invocationContext = ExecutionContext.Capture();
        if (!Live || Volatile.Read(ref _deactivating) != 0 || _faulted.Task.IsCompleted || Context.Lifetime.IsCancellationRequested)
            throw new RpcException(RpcCodes.ActivationNotLive, "the provider activation is stopping");
        var cancellations = arguments.OfType<CancellationToken>().Append(Context.Lifetime).ToArray();
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(cancellations);
        var callerToken = caller.Token;
        callerToken.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = completion.Task.ContinueWith(task => task.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        await Context.RunTaskAsync("sdk-service-provider", async lifetime =>
        {
            using var deadline = new CancellationTokenSource(_requestTimeout);
            using var signal = CancellationTokenSource.CreateLinkedTokenSource(callerToken, lifetime, deadline.Token);
            var invocation = Guid.NewGuid().ToString("N");
            var frame = new ProviderInvocation(provider, inner, signal.Token, invocationContext);
            _providerInvocations[invocation] = frame;
            object? result = null;
            Exception? failure = null;
            try
            {
                signal.Token.ThrowIfCancellationRequested();
                var args = RemoteServiceProvider.Arguments(method, arguments, Sessions);
                var response = await Source.InvokeProviderAsync(this, new
                {
                    activation = Id, callback = provider.Callback, invocation,
                    service = provider.Service.Name, method = ContractSchema.MethodId(method), method_name = method.Name,
                    args, has_inner = inner is not null,
                }, new { activation = Id, invocation }, signal.Token).ConfigureAwait(false);
                signal.Token.ThrowIfCancellationRequested();
                result = RemoteServiceProvider.Result(method, response, Sessions);
            }
            catch (Exception error)
            {
                if (deadline.IsCancellationRequested && !callerToken.IsCancellationRequested && !lifetime.IsCancellationRequested)
                {
                    failure = new RemoteTimeoutException($"SDK provider '{provider.Service.Name}.{method.Name}' exceeded request_timeout_ms; actual completion remains owned.");
                    Fault(failure);
                }
                else failure = error;
            }
            finally
            {
                await frame.CloseAsync().ConfigureAwait(false);
                _providerInvocations.TryRemove(invocation, out _);
            }
            if (failure is null) completion.TrySetResult(result);
            else completion.TrySetException(failure);
        }, callerToken).ConfigureAwait(false);
        try { return await completion.Task.WaitAsync(_requestTimeout, callerToken).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            var error = new RemoteTimeoutException($"SDK provider '{provider.Service.Name}.{method.Name}' exceeded request_timeout_ms; actual completion remains owned.");
            Fault(error);
            throw error;
        }
    }

    /// <summary>Invokes a decorator's native inner service only while the matching callback is still admitted.</summary>
    public async Task<JsonElement> CallInnerAsync(JsonElement parameters, CancellationToken ct)
    {
        var invocation = RequiredString(parameters, "invocation");
        if (!_providerInvocations.TryGetValue(invocation, out var frame) || !frame.TryEnter())
            throw new RpcException(RpcCodes.InvalidParams, "the inner-service invocation is unknown, foreign or finished");
        try
        {
            if (frame.Inner is null)
                throw new RpcException(RpcCodes.TypeNotAllowed, "this provider invocation has no inner service");
            var method = frame.Provider.Method(RequiredString(parameters, "method"));
            if (!parameters.TryGetProperty("args", out var args) || args.ValueKind != JsonValueKind.Object || !RemoteServiceInvoker.ParameterNamesMatch(method, args))
                throw new RpcException(RpcCodes.InvalidParams, "inner-service arguments do not match the declared method");
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, frame.Cancellation);
            var bound = RemoteServiceInvoker.BindArguments(method, args, Sessions, Context, linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            object? value = null;
            frame.Run(() => value = method.Invoke(frame.Inner, bound));
            value = await RemoteServiceInvoker.AwaitResultAsync(value, method.ReturnType).ConfigureAwait(false);
            var type = ContractSchema.Unwrap(method.ReturnType);
            var response = type == typeof(void) ? JsonSerializer.SerializeToElement((object?)null)
                : JsonSerializer.SerializeToElement(value, type, RemoteServiceInvoker.SessionOptions(Sessions));
            _ = RemoteServiceProvider.Result(method, response, Sessions);
            return response;
        }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw;
        }
        finally { frame.Exit(); }
    }

    private sealed class ProviderInvocation(RemoteServiceProvider provider, object? inner, CancellationToken cancellation,
        ExecutionContext? invocationContext) : RemoteCallbackInvocation(invocationContext, cancellation)
    {
        internal RemoteServiceProvider Provider { get; } = provider;
        internal object? Inner { get; } = inner;
    }
}
