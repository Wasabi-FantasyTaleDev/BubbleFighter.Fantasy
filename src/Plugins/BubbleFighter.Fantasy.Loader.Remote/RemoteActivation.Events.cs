using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.Loader.Remote;

internal sealed partial class RemoteActivation
{
    private static readonly MethodInfo SubscribeEventMethod = typeof(RemoteActivation).GetMethod(nameof(SubscribeEvent), BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo SubscribeResultMethod = typeof(RemoteActivation).GetMethod(nameof(SubscribeResultEvent), BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo SubscribeBailMethod = typeof(RemoteActivation).GetMethod(nameof(SubscribeBailEvent), BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo PublishEventMethod = typeof(RemoteActivation).GetMethod(nameof(PublishTypedEventAsync), BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo PublishResultMethod = typeof(RemoteActivation).GetMethod(nameof(PublishResultEventAsync), BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo PublishBailMethod = typeof(RemoteActivation).GetMethod(nameof(PublishBailEventAsync), BindingFlags.Instance | BindingFlags.NonPublic)!;
    private readonly ConcurrentDictionary<string, EventInvocation> _eventInvocations = new(StringComparer.Ordinal);
    private readonly HashSet<string> _eventCallbacks = new(StringComparer.Ordinal);

    /// <summary>Registers one declared event operation on the activation's owned native event bus.</summary>
    public JsonElement OnEvent(JsonElement parameters)
    {
        var contract = DeclaredEvent(parameters, publishing: false);
        var callback = RequiredString(parameters, "callback");
        var priority = 0;
        if (parameters.TryGetProperty("priority", out var supplied) && (supplied.ValueKind != JsonValueKind.Number || !supplied.TryGetInt32(out priority)))
            throw new RpcException(RpcCodes.InvalidParams, "'priority' must be an int32");
        lock (_handleGate)
        {
            if (_providerCallbacks.Count + _eventCallbacks.Count >= _maxHandles)
                throw new RpcException(RpcCodes.Overloaded, "retained callbacks exceed effect_capacity");
            if (!_eventCallbacks.Add(callback))
                throw new RpcException(RpcCodes.InvalidParams, "event callback identity is already registered");
        }
        try
        {
            var method = contract.Mode == "waterfall" ? SubscribeResultMethod : contract.Mode == "bail" ? SubscribeBailMethod : SubscribeEventMethod;
            var arguments = contract.Result == typeof(void) ? new[] { contract.Event } : new[] { contract.Event, contract.Result };
            return AddHandle(_ => Context.Effect((IDisposable)Invoke(method.MakeGenericMethod(arguments), contract, callback, priority)));
        }
        catch
        {
            lock (_handleGate) _eventCallbacks.Remove(callback);
            throw;
        }
    }

    private RemoteEventContract DeclaredEvent(JsonElement parameters, bool publishing)
    {
        RemoteEventContract requested;
        try { requested = RemoteEventContract.Resolve(parameters, publishing); }
        catch (ConfigException error) { throw new RpcException(RpcCodes.InvalidParams, error.Message); }
        return (publishing ? Definition.Publishes : Definition.Subscribes).FirstOrDefault(item => item.Key == requested.Key)
            ?? throw new RpcException(RpcCodes.TypeNotAllowed, $"event operation '{requested.Key}' was not declared");
    }

    private IDisposable SubscribeEvent<T>(RemoteEventContract contract, string callback, int priority) where T : IEvent =>
        contract.Mode == "sync"
            ? Context.Events.On<T>(value => InvokeEventCallbackAsync(contract, callback, value, null, default).GetAwaiter().GetResult(), priority)
            : Context.Events.OnAsync<T>(async (value, ct) => { await InvokeEventCallbackAsync(contract, callback, value, null, ct).ConfigureAwait(false); }, priority);

    private IDisposable SubscribeResultEvent<T, TResult>(RemoteEventContract contract, string callback, int priority) where T : IEvent =>
        Context.Events.Intercept<T, TResult>(async (value, next, ct) =>
            (TResult)(await InvokeEventCallbackAsync(contract, callback, value,
                async token => contract.Write(await next(token).ConfigureAwait(false), typeof(TResult), Sessions), ct).ConfigureAwait(false))!, priority);

    private IDisposable SubscribeBailEvent<T, TResult>(RemoteEventContract contract, string callback, int priority) where T : IEvent where TResult : class =>
        Context.Events.OnBail<T, TResult>(value => (TResult?)InvokeEventCallbackAsync(contract, callback, value, null, default).GetAwaiter().GetResult(), priority);

    private async Task<object?> InvokeEventCallbackAsync(RemoteEventContract contract, string callback, object value,
        Func<CancellationToken, Task<JsonElement>>? next, CancellationToken ct, bool terminal = false)
    {
        var invocationContext = ExecutionContext.Capture();
        if (!Live || _faulted.Task.IsCompleted || Volatile.Read(ref _deactivating) != 0 || Context.Lifetime.IsCancellationRequested)
            throw new RpcException(RpcCodes.ActivationNotLive, "the event activation is stopping");
        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = completion.Task.ContinueWith(task => task.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        await Context.RunTaskAsync("sdk-event-callback", async lifetime =>
        {
            using var deadline = new CancellationTokenSource(_requestTimeout);
            using var signal = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime, deadline.Token);
            var invocation = Guid.NewGuid().ToString("N");
            var frame = new EventInvocation(invocationContext, next, signal.Token);
            _eventInvocations[invocation] = frame;
            object? result = null;
            Exception? failure = null;
            try
            {
                signal.Token.ThrowIfCancellationRequested();
                var response = await Source.InvokeEventAsync(this, new
                {
                    activation = Id, callback, invocation, type = ContractSchema.TypeName(contract.Event),
                    mode = terminal ? "terminal" : contract.Mode,
                    result_type = contract.Result == typeof(void) ? null : ContractSchema.TypeName(contract.Result),
                    body = contract.Write(value, contract.Event, Sessions), has_next = next is not null,
                }, new { activation = Id, invocation }, signal.Token).ConfigureAwait(false);
                // Cancellation is cooperative, as for a native listener. In particular, self-removal
                // cancels the subscription token but must not discard the callback's eventual result.
                if (deadline.IsCancellationRequested)
                    throw new RemoteTimeoutException("SDK event callback exceeded request_timeout_ms; actual completion remains owned.");
                result = contract.Read(response, contract.Result, Sessions, contract.Mode == "bail");
            }
            catch (Exception error)
            {
                if (error is RemoteTimeoutException || (deadline.IsCancellationRequested && !ct.IsCancellationRequested && !lifetime.IsCancellationRequested))
                {
                    failure = new RemoteTimeoutException("SDK event callback exceeded request_timeout_ms; actual completion remains owned.");
                    Fault(failure);
                }
                else failure = error;
            }
            finally
            {
                await frame.CloseAsync().ConfigureAwait(false);
                _eventInvocations.TryRemove(invocation, out _);
            }
            if (failure is null) completion.TrySetResult(result);
            else completion.TrySetException(failure);
        }, ct).ConfigureAwait(false);
        try { return await completion.Task.WaitAsync(_requestTimeout).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            var error = new RemoteTimeoutException("SDK event callback exceeded request_timeout_ms; actual completion remains owned.");
            Fault(error);
            throw error;
        }
    }

    /// <summary>Publishes a declared event while retaining ownership until the actual native dispatch finishes.</summary>
    public async Task<JsonElement> PublishEventAsync(JsonElement parameters, CancellationToken ct)
    {
        var contract = DeclaredEvent(parameters, publishing: true);
        if (!parameters.TryGetProperty("body", out var body)) throw new RpcException(RpcCodes.InvalidParams, "'body' is required");
        object value;
        try { value = contract.Read(body, contract.Event, Sessions)!; }
        catch (JsonException error) { throw new RpcException(RpcCodes.InvalidParams, error.Message); }
        var terminal = contract.Mode == "waterfall" ? RequiredString(parameters, "terminal") : "";
        var method = contract.Mode == "waterfall" ? PublishResultMethod : contract.Mode == "bail" ? PublishBailMethod : PublishEventMethod;
        var types = contract.Result == typeof(void) ? new[] { contract.Event } : new[] { contract.Event, contract.Result };
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = completion.Task.ContinueWith(task => task.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        await Context.RunTaskAsync("sdk-event-publish", async lifetime =>
        {
            using var deadline = new CancellationTokenSource(_requestTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime, deadline.Token);
            using var fault = deadline.Token.Register(() => Fault(new RemoteTimeoutException("SDK event publication exceeded request_timeout_ms; dispatch remains owned.")));
            try
            {
                linked.Token.ThrowIfCancellationRequested();
                var task = (Task<JsonElement>)Invoke(method.MakeGenericMethod(types), contract, value, terminal, linked.Token);
                var result = await task.ConfigureAwait(false);
                if (deadline.IsCancellationRequested) throw new RemoteTimeoutException("SDK event publication exceeded request_timeout_ms.");
                completion.TrySetResult(result);
            }
            catch (Exception error) { completion.TrySetException(error); }
        }, ct).ConfigureAwait(false);
        // The SDK keeps a publication's terminal callback until this response. A deadline faults the row
        // but cannot send an early response that would discard a terminal still reachable by native work.
        return await completion.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task<JsonElement> PublishTypedEventAsync<T>(RemoteEventContract contract, object value, string terminal, CancellationToken ct) where T : IEvent
    {
        switch (contract.Mode)
        {
            case "emit": Context.Events.Emit((T)value); break;
            case "serial": await Context.Events.SerialAsync((T)value, ct).ConfigureAwait(false); break;
            case "parallel": await Context.Events.ParallelAsync((T)value, ct).ConfigureAwait(false); break;
            default: throw new InvalidOperationException("The event mode was not bound.");
        }
        return JsonSerializer.SerializeToElement((object?)null);
    }

    private async Task<JsonElement> PublishResultEventAsync<T, TResult>(RemoteEventContract contract, object value, string terminal, CancellationToken ct) where T : IEvent
    {
        var result = await Context.Events.WaterfallAsync<T, TResult>((T)value,
            async token => (TResult)(await InvokeEventCallbackAsync(contract, terminal, value, null, token, terminal: true).ConfigureAwait(false))!, ct).ConfigureAwait(false);
        return contract.Write(result, typeof(TResult), Sessions);
    }

    private Task<JsonElement> PublishBailEventAsync<T, TResult>(RemoteEventContract contract, object value, string terminal, CancellationToken ct) where T : IEvent where TResult : class =>
        Task.FromResult(contract.Write(Context.Events.Bail<T, TResult>((T)value), typeof(TResult), Sessions, nullable: true));

    /// <summary>Runs the selected native continuation only inside its live event invocation.</summary>
    public async Task<JsonElement> NextEventAsync(JsonElement parameters, CancellationToken ct)
    {
        var frame = EnterEventInvocation(parameters);
        try
        {
            if (frame.Next is null) throw new RpcException(RpcCodes.InvalidParams, "this event invocation has no next operation");
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, frame.Cancellation);
            linked.Token.ThrowIfCancellationRequested();
            Task<JsonElement>? task = null;
            frame.Run(() => task = frame.Next(linked.Token));
            return await task!.ConfigureAwait(false);
        }
        finally { frame.Exit(); }
    }

    /// <summary>Restores the native callback context for explicit self-disposal without releasing its unfinished work.</summary>
    public JsonElement DisposeInEventContext(JsonElement parameters)
    {
        if (!parameters.TryGetProperty("event_invocation", out _)) return DisposeHandle(parameters);
        var frame = EnterEventInvocation(parameters, "event_invocation");
        try
        {
            JsonElement result = default;
            frame.Run(() => result = DisposeHandle(parameters));
            return result;
        }
        finally { frame.Exit(); }
    }

    private EventInvocation EnterEventInvocation(JsonElement parameters, string field = "invocation") =>
        _eventInvocations.TryGetValue(RequiredString(parameters, field), out var frame) && frame.TryEnter() ? frame
            : throw new RpcException(RpcCodes.InvalidParams, "the event invocation is unknown, foreign or finished");

    private sealed class EventInvocation(ExecutionContext? context, Func<CancellationToken, Task<JsonElement>>? next, CancellationToken cancellation)
        : RemoteCallbackInvocation(context, cancellation)
    {
        internal Func<CancellationToken, Task<JsonElement>>? Next { get; } = next;
    }
}
