using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Sessions;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Loader.Remote;

/// <summary>
/// The host side of one <c>activate</c>: the plugin's live registrations, the sessions it may answer and the
/// fault signal that tells the row its process or a request failed. Every handle is an effect owned by the row's context.
/// </summary>
internal sealed partial class RemoteActivation
{
    private static readonly MethodInfo RegisterOn = typeof(RemoteActivation).GetMethod(nameof(RegisterHandler), BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo RegisterOverride = typeof(RemoteActivation).GetMethod(nameof(RegisterOverrideHandler), BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo RegisterUse = typeof(RemoteActivation).GetMethod(nameof(RegisterMiddleware), BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo SendTyped = typeof(RemoteActivation).GetMethod(nameof(SendTypedAsync), BindingFlags.Instance | BindingFlags.NonPublic)!;

    private readonly ConcurrentDictionary<string, IDisposable> _handles = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource<Exception> _faulted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<string, IRemoteMiddlewareInvocation> _middlewareInvocations = new(StringComparer.Ordinal);
    private readonly int _maxHandles;
    private readonly TimeSpan _requestTimeout;
    private long _handleCounter;
    private int _deactivating;

    /// <summary>Creates an activation record in the not-yet-live state.</summary>
    /// <param name="source">The process-owning source.</param>
    /// <param name="definition">The definition being activated.</param>
    /// <param name="context">The row's activation context.</param>
    /// <param name="sessionCapacity">The bound on remembered sessions.</param>
    /// <param name="maxHandles">The bound on live handles, taken from the row's effect capacity.</param>
    /// <param name="requestTimeout">Maximum wait for a service call; unfinished work remains owned by the row.</param>
    public RemoteActivation(RemoteSource source, RemoteDefinition definition, IPluginContext context, int sessionCapacity, int maxHandles, TimeSpan requestTimeout)
    {
        Id = Guid.NewGuid().ToString("N");
        Source = source;
        Definition = definition;
        Context = context;
        Sessions = new SessionMap(sessionCapacity, opaqueHandles: definition.ProtocolVersion >= 2);
        _maxHandles = maxHandles;
        _requestTimeout = requestTimeout;
    }

    /// <summary>Gets the opaque activation id the plugin echoes on every call.</summary>
    public string Id { get; }

    /// <summary>Gets the source that owns the process.</summary>
    public RemoteSource Source { get; }

    /// <summary>Gets the definition being served.</summary>
    public RemoteDefinition Definition { get; }

    /// <summary>Gets the row's activation context.</summary>
    public IPluginContext Context { get; }

    /// <summary>Gets the sessions the plugin may answer.</summary>
    public SessionMap Sessions { get; }

    /// <summary>Gets whether plugin-to-host calls for this activation are accepted.</summary>
    public bool Live { get; private set; }

    /// <summary>Gets a task completing with the cause when the activation's process dies or one of its requests times out. It never faults.</summary>
    public Task<Exception> Faulted => _faulted.Task;

    /// <summary>Starts accepting plugin-to-host calls; the plugin may call back before <c>activate</c> returns.</summary>
    public void GoLive() => Live = true;

    /// <summary>Marks the activation as failed so its watcher faults the row.</summary>
    /// <param name="cause">What went wrong.</param>
    public void Fault(Exception cause) => _faulted.TrySetResult(cause);

    /// <summary>Observes the activation failure, preserving a recorded cause when watcher cancellation races its delivery.</summary>
    internal async Task<Exception> WaitForFaultAsync(CancellationToken cancellationToken)
    {
        try { return await Faulted.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && Faulted.IsCompletedSuccessfully)
        {
            // Shutdown can cancel WaitAsync before its asynchronously forwarded result arrives.
            // A failure recorded before observation stopped must still reach the owned task.
            return await Faulted.ConfigureAwait(false);
        }
    }

    /// <summary>Runs a declared service call as owned work so timeout or unload cannot abandon its resource accounting.</summary>
    public async Task<JsonElement> CallServiceAsync(JsonElement parameters, CancellationToken ct)
    {
        if (_faulted.Task.IsCompleted || Volatile.Read(ref _deactivating) != 0 || Context.Lifetime.IsCancellationRequested)
            throw new RpcException(RpcCodes.ActivationNotLive, "the activation is stopping");
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = completion.Task.ContinueWith(task => task.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        await Context.RunTaskAsync("sdk-service-call", async token =>
        {
            try { completion.TrySetResult(await RemoteServiceInvoker.InvokeAsync(Definition, Context, Sessions, parameters, _requestTimeout, token, SelectCollectionMember(parameters)).ConfigureAwait(false)); }
            catch (Exception error) { completion.TrySetException(error); }
        }, ct).ConfigureAwait(false);
        try { return await completion.Task.WaitAsync(_requestTimeout, ct).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            var error = new RemoteTimeoutException("SDK service call exceeded request_timeout_ms; its row is being stopped.");
            Fault(error);
            throw new RpcException(RpcCodes.InternalError, error.Message);
        }
    }

    /// <summary>Sends <c>deactivate</c> once and stops accepting plugin calls. Safe to call repeatedly.</summary>
    /// <param name="bestEffort">Swallow plugin errors (used when activation itself failed).</param>
    public async Task DeactivateAsync(bool bestEffort)
    {
        if (Interlocked.Exchange(ref _deactivating, 1) != 0)
            return;
        if (Source.IsFaulted)
        {
            // A lost transport cannot run cleanup. A row timeout alone must still release SDK-side
            // state after its admitted work drains, because the process may serve other live rows.
            Live = false;
            return;
        }
        try
        {
            await Source.RequestAsync(this, "deactivate", new { activation = Id }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (RemoteProcessException ex)
        {
            Context.Logger.LogWarning("SDK plugin '{Plugin}' was already gone at deactivation: {Message}", Definition.Id, ex.Message);
        }
        catch (Exception ex) when (bestEffort)
        {
            Context.Logger.LogWarning(ex, "SDK plugin '{Plugin}' failed to deactivate after a failed activation.", Definition.Id);
        }
        finally
        {
            Live = false;
        }
    }

    /// <summary>Serves <c>messages.on</c>.</summary>
    /// <param name="parameters">The call params.</param>
    /// <returns>The <c>{handle}</c> result.</returns>
    public JsonElement On(JsonElement parameters)
    {
        var protocol = RequiredString(parameters, "protocol");
        var typeName = RequiredString(parameters, "type");
        if (!Definition.Handles.TryGetValue(typeName, out var type))
            throw new RpcException(RpcCodes.TypeNotAllowed, $"type '{typeName}' is not in the definition's handles");
        var bindingType = typeof(IInboundMessageBinding<>).MakeGenericType(type);
        if (!HasProtocol(Context.Services, bindingType, protocol))
            throw new RpcException(RpcCodes.NoBinding, $"protocol '{protocol}' has no inbound binding for '{typeName}'");
        return AddHandle(handle => (IDisposable)Invoke(RegisterOn.MakeGenericMethod(type), protocol, typeName, handle));
    }

    /// <summary>Serves <c>messages.override</c> for an explicitly declared native handler target.</summary>
    /// <param name="parameters">The call params.</param>
    /// <returns>The <c>{handle}</c> result.</returns>
    public JsonElement Override(JsonElement parameters)
    {
        var protocol = RequiredString(parameters, "protocol");
        var typeName = RequiredString(parameters, "type");
        var target = RequiredString(parameters, "target_row_id");
        if (!Definition.Handles.TryGetValue(typeName, out var type))
            throw new RpcException(RpcCodes.TypeNotAllowed, $"type '{typeName}' is not in the definition's handles");
        var declaration = $"{target}:{type.FullName}";
        if (!Definition.Declaration.Overrides.Contains(declaration, StringComparer.Ordinal))
            throw new RpcException(RpcCodes.TypeNotAllowed, $"override target '{declaration}' is not declared by this plugin");
        var bindingType = typeof(IInboundMessageBinding<>).MakeGenericType(type);
        if (!HasProtocol(Context.Services, bindingType, protocol))
            throw new RpcException(RpcCodes.NoBinding, $"protocol '{protocol}' has no inbound binding for '{typeName}'");
        return AddHandle(handle => (IDisposable)Invoke(RegisterOverride.MakeGenericMethod(type), protocol, typeName, target, handle));
    }

    /// <summary>Serves <c>messages.use</c> for a declared SDK middleware contract.</summary>
    public JsonElement Use(JsonElement parameters)
    {
        var protocol = RequiredString(parameters, "protocol");
        var typeName = RequiredString(parameters, "type");
        var priority = 0;
        if (parameters.TryGetProperty("priority", out var value) && (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out priority)))
            throw new RpcException(RpcCodes.InvalidParams, "'priority' must be an int32");
        if (!Definition.Middleware.TryGetValue(typeName, out var type))
            throw new RpcException(RpcCodes.TypeNotAllowed, $"type '{typeName}' is not in the definition's middleware");
        var bindingType = typeof(IInboundMessageBinding<>).MakeGenericType(type);
        if (!HasProtocol(Context.Services, bindingType, protocol))
            throw new RpcException(RpcCodes.NoBinding, $"protocol '{protocol}' has no inbound binding for '{typeName}'");
        return AddHandle(handle => (IDisposable)Invoke(RegisterUse.MakeGenericMethod(type), protocol, typeName, priority, handle));
    }

    /// <summary>Continues one SDK middleware invocation exactly once.</summary>
    public async Task<JsonElement> NextMessageAsync(JsonElement parameters, CancellationToken ct)
    {
        var invocation = RequiredString(parameters, "invocation");
        if (!_middlewareInvocations.TryGetValue(invocation, out var continuation))
            throw new RpcException(RpcCodes.InvalidParams, $"message middleware invocation '{invocation}' is unknown or closed");
        await continuation.NextAsync(ct).ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(new { });
    }

    /// <summary>Serves <c>messages.send</c>.</summary>
    /// <param name="parameters">The call params.</param>
    /// <param name="ct">Cancelled when the request or the row ends.</param>
    /// <returns>The empty result.</returns>
    public async Task<JsonElement> SendAsync(JsonElement parameters, CancellationToken ct)
    {
        var protocol = RequiredString(parameters, "protocol");
        var sessionId = RequiredString(parameters, "session_id");
        var typeName = RequiredString(parameters, "type");
        if (!parameters.TryGetProperty("body", out var body))
            throw new RpcException(RpcCodes.InvalidParams, "missing 'body'");
        if (!Definition.Sends.TryGetValue(typeName, out var type))
            throw new RpcException(RpcCodes.TypeNotAllowed, $"type '{typeName}' is not in the definition's sends");
        if (!Sessions.TryGet(sessionId, out var session))
            throw new RpcException(RpcCodes.SessionUnknown, $"session '{sessionId}' is unknown or closed");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, Context.Lifetime);
        var task = (Task)Invoke(SendTyped.MakeGenericMethod(type), protocol, typeName, session, body, linked.Token);
        await task.ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(new { });
    }

    /// <summary>Serves <c>effect.dispose</c>.</summary>
    /// <param name="parameters">The call params.</param>
    /// <returns>The empty result.</returns>
    public JsonElement DisposeHandle(JsonElement parameters)
    {
        var handle = RequiredString(parameters, "handle");
        if (!_handles.TryRemove(handle, out var registration))
            throw new RpcException(RpcCodes.InvalidParams, $"handle '{handle}' is unknown or already disposed");
        // While the row is unwinding, its effect scope already owns the release and is waiting for this very call chain
        // (the deactivate effect); disposing here would wait on that same unwind. The scope releases the handle right after.
        if (Volatile.Read(ref _deactivating) == 0)
            registration.Dispose();
        // Failed disposal keeps its capacity charged; the activation's effect scope still owns the actual release.
        lock (_handleGate) _reservedHandles--;
        return JsonSerializer.SerializeToElement(new { });
    }

    /// <summary>Forgets a closed connection's sessions.</summary>
    /// <param name="closed">The close event.</param>
    public void OnConnectionClosed(ConnectionClosedEvent closed) => Sessions.RemoveConnection(closed.Connection.Id);

    private IDisposable RegisterHandler<T>(string protocol, string typeName, string handle) where T : IMessage
    {
        var router = Context.Services.Get<IMessageRouter>();
        // The row owns the registration; the plugin's handle is the same effect, so disposing either releases it once.
        return Context.Effect(router.For(Context).On<T>(protocol,
            (session, message, ct) => new ValueTask(DeliverAsync(protocol, typeName, handle, session, message, ct))));
    }

    private IDisposable RegisterOverrideHandler<T>(string protocol, string typeName, string target, string handle) where T : IMessage
    {
        var router = Context.Services.Get<IMessageRouter>();
        return Context.Effect(router.For(Context).Override<T>(protocol, target,
            (session, message, ct) => new ValueTask(DeliverAsync(protocol, typeName, handle, session, message, ct))));
    }

    private IDisposable RegisterMiddleware<T>(string protocol, string typeName, int priority, string handle) where T : IMessage
    {
        var router = Context.Services.Get<IMessageRouter>();
        return Context.Effect(router.For(Context).Use<T>(protocol, (session, message, next, ct) =>
            new ValueTask(DeliverMiddlewareAsync(protocol, typeName, handle, session, message, next, ct)), priority));
    }

    private async Task DeliverMiddlewareAsync<T>(string protocol, string typeName, string handle, ISession session, T message,
        MessageHandler<T> next, CancellationToken ct) where T : IMessage
    {
        if (!Live)
            throw new InvalidOperationException($"SDK plugin '{Definition.Id}' is not active; '{typeName}' was not delivered.");
        var sessionId = Sessions.Add(session);
        var invocationId = Guid.NewGuid().ToString("N");
        var invocation = new MiddlewareInvocation<T>(session, message, next, ExecutionContext.Capture(), ct);
        if (!_middlewareInvocations.TryAdd(invocationId, invocation))
            throw new InvalidOperationException("message middleware invocation identity collided");
        try
        {
            var body = JsonSerializer.SerializeToElement(message, WireJson.Options);
            await Source.InvokeMessageMiddlewareAsync(this, new
            {
                activation = Id,
                invocation = invocationId,
                handle,
                protocol,
                session = DescribeSession(sessionId, protocol, session),
                message = new { type = typeName, body },
            }, new { activation = Id, invocation = invocationId }, ct).ConfigureAwait(false);
        }
        finally
        {
            _middlewareInvocations.TryRemove(invocationId, out _);
            await invocation.CloseAsync().ConfigureAwait(false);
        }
    }

    private interface IRemoteMiddlewareInvocation
    {
        Task NextAsync(CancellationToken cancellationToken);
        Task CloseAsync();
    }

    private sealed class MiddlewareInvocation<T>(ISession session, T message, MessageHandler<T> next,
        ExecutionContext? context, CancellationToken cancellation)
        : RemoteCallbackInvocation(context, cancellation), IRemoteMiddlewareInvocation where T : IMessage
    {
        private int _used;

        public async Task NextAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _used, 1) != 0 || !TryEnter())
                throw new RpcException(RpcCodes.InvalidParams, "message middleware next is single-use and cannot escape its invocation");
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(Cancellation, cancellationToken);
                ValueTask work = default;
                Run(() => work = next(session, message, linked.Token));
                await work.ConfigureAwait(false);
            }
            finally { Exit(); }
        }

        Task IRemoteMiddlewareInvocation.CloseAsync() => CloseAsync();
    }

    private async Task DeliverAsync<T>(string protocol, string typeName, string handle, ISession session, T message, CancellationToken ct) where T : IMessage
    {
        if (!Live)
            throw new InvalidOperationException($"SDK plugin '{Definition.Id}' is not active; '{typeName}' was not delivered.");
        var id = Sessions.Add(session);
        var body = JsonSerializer.SerializeToElement(message, WireJson.Options);
        var result = await Source.RequestAsync(this, "deliver", new
        {
            activation = Id,
            handle,
            session = DescribeSession(id, protocol, session),
            message = new { type = typeName, body },
        }, ct).ConfigureAwait(false);
        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("handled", out var handled) || handled.ValueKind != JsonValueKind.True)
            throw new InvalidOperationException($"SDK plugin '{Definition.Id}' did not report '{typeName}' as handled.");
    }

    private async Task SendTypedAsync<T>(string protocol, string typeName, ISession session, JsonElement body, CancellationToken ct) where T : IMessage
    {
        var senders = Context.Services.GetAll<IMessageSender<T>>().Where(sender => sender.Protocol == protocol).ToArray();
        if (senders.Length != 1)
            throw new RpcException(RpcCodes.NoBinding, $"protocol '{protocol}' has {senders.Length} senders for '{typeName}', expected exactly one");
        T message;
        try
        {
            message = body.Deserialize<T>(WireJson.Options) ?? throw new JsonException("body must not be null");
        }
        catch (JsonException ex)
        {
            throw new RpcException(RpcCodes.InvalidParams, $"body does not bind to '{typeName}': {ex.Message}");
        }
        try
        {
            await senders[0].SendAsync(session, message, ct).ConfigureAwait(false);
        }
        catch (ObjectDisposedException ex)
        {
            throw new RpcException(RpcCodes.SessionUnknown, $"session is closed: {ex.Message}");
        }
    }

    private static object DescribeSession(string id, string protocol, ISession session)
    {
        var profile = session.Profile;
        int? player = session.TryFeature<IPlayerSession>(out var state) ? state.Identity.UserNum : null;
        return new { id, protocol, player, build = $"{profile.Region}:{profile.ClientVersionMajor}:{profile.ResourceVersion}" };
    }

    private static bool HasProtocol(IServiceRegistry services, Type bindingType, string protocol)
    {
        var getAll = typeof(IServiceRegistry).GetMethod(nameof(IServiceRegistry.GetAll))!.MakeGenericMethod(bindingType);
        var protocolProperty = bindingType.GetProperty(nameof(IInboundMessageBinding<IMessage>.Protocol))!;
        return ((System.Collections.IEnumerable)getAll.Invoke(services, null)!).Cast<object>()
            .Any(binding => (string?)protocolProperty.GetValue(binding) == protocol);
    }

    private object Invoke(MethodInfo method, params object[] arguments)
    {
        try { return method.Invoke(this, arguments)!; }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    private static string RequiredString(JsonElement parameters, string name) =>
        parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(value.GetString())
            ? value.GetString()!
            : throw new RpcException(RpcCodes.InvalidParams, $"'{name}' must be a nonempty string");
}
