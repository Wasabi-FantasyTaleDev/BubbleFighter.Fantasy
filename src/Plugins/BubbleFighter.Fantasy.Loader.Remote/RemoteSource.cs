using System.Collections.Concurrent;
using System.Text.Json;
using BubbleFighter.Fantasy.Abstractions;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Loader.Remote;

/// <summary>
/// One running remote process and the definitions it described. As the definition batch it owns the process:
/// the host disposes it only after every row served by these definitions has drained.
/// </summary>
internal sealed class RemoteSource : IPluginDefinitionBatch, IInboundSink
{
    private readonly ConcurrentDictionary<string, RemoteActivation> _activations = new(StringComparer.Ordinal);
    private readonly ILogger _logger;
    private readonly string _sourceId;
    private RemoteProcess _process = null!;
    private int _protocolVersion;

    private RemoteSource(string sourceId, ILogger logger)
    {
        _sourceId = sourceId;
        _logger = logger;
    }

    /// <summary>Gets the complete definition batch the process described.</summary>
    public IReadOnlyList<PluginDefinition> Definitions { get; private set; } = [];

    /// <summary>Gets whether the process died or broke the protocol.</summary>
    public bool IsFaulted => _process.Faulted.IsCompleted;

    /// <summary>Gets the process's fault signal.</summary>
    public Task<Exception> Faulted => _process.Faulted;

    /// <summary>
    /// Starts the process, runs <c>initialize</c> and <c>describe</c>, and returns the owned batch. A failure at any step
    /// stops the process before the exception leaves, because the host never sees a batch it must dispose.
    /// </summary>
    /// <param name="entry">The configured process.</param>
    /// <param name="config">The loader configuration.</param>
    /// <param name="logger">The loader's logger.</param>
    /// <param name="ct">Cancels acquisition.</param>
    /// <returns>The batch.</returns>
    public static async ValueTask<IPluginDefinitionBatch> AcquireAsync(RemoteEntry entry, RemoteLoaderConfig config, ILogger logger, CancellationToken ct)
    {
        var source = new RemoteSource(entry.SourceId, logger);
        try
        {
            source._process = entry.Wasm is { } wasm
                ? RemoteProcess.Start(WasmChannel.Start(wasm, config.Transport.MaxLineBytes), config.Transport, logger, source, entry.SourceId)
                : RemoteProcess.Start(entry.Launch!, config.Transport, logger, source, entry.SourceId);
            // One observer per process keeps removed rows collectible while the shared process stays alive.
            _ = source._process.Faulted.ContinueWith(static (task, state) =>
            {
                foreach (var activation in ((RemoteSource)state!)._activations.Values)
                    activation.Fault(task.Result);
            }, source, CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
            await source.DescribeAsync(entry, config, ct).ConfigureAwait(false);
            return source;
        }
        catch
        {
            await source.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Sends a request tied to an activation; a timeout faults that activation's row.</summary>
    /// <param name="activation">The activation the request belongs to.</param>
    /// <param name="method">The method name.</param>
    /// <param name="parameters">The params object.</param>
    /// <param name="ct">Cancels the wait.</param>
    /// <returns>The result value.</returns>
    public async Task<JsonElement> RequestAsync(RemoteActivation activation, string method, object parameters, CancellationToken ct)
    {
        try
        {
            return await _process.RequestAsync(method, parameters, ct).ConfigureAwait(false);
        }
        catch (RemoteTimeoutException ex)
        {
            activation.Fault(ex);
            throw;
        }
    }

    /// <summary>Runs an owned provider callback until its response or process loss; cancellation is forwarded without releasing its slot.</summary>
    /// <param name="activation">The activation whose callback is running.</param>
    /// <param name="parameters">The callback arguments and invocation identity.</param>
    /// <param name="cancellation">The cancellation notification's activation and invocation identity.</param>
    /// <param name="ct">Requests cancellation without abandoning the response.</param>
    public Task<JsonElement> InvokeProviderAsync(RemoteActivation activation, object parameters, object cancellation, CancellationToken ct) =>
        CallbackAsync(activation, "services.invoke", parameters, cancellation, ct, "services.cancel");

    /// <summary>Retains an event callback's transport ownership until its response, forwarding cooperative cancellation.</summary>
    /// <param name="activation">The activation whose callback is running.</param>
    /// <param name="parameters">The callback arguments and invocation identity.</param>
    /// <param name="cancellation">The cancellation notification's activation and invocation identity.</param>
    /// <param name="ct">Requests cancellation without abandoning the response.</param>
    public Task<JsonElement> InvokeEventAsync(RemoteActivation activation, object parameters, object cancellation, CancellationToken ct) =>
        CallbackAsync(activation, "events.invoke", parameters, cancellation, ct, "events.cancel");

    /// <summary>Runs one SDK task callback while retaining the native task owner until its response.</summary>
    /// <param name="activation">The activation whose callback is running.</param>
    /// <param name="parameters">The callback arguments and invocation identity.</param>
    /// <param name="cancellation">The cancellation notification's activation and invocation identity.</param>
    /// <param name="ct">Requests cancellation without abandoning the response.</param>
    public Task<JsonElement> InvokeTaskAsync(RemoteActivation activation, object parameters, object cancellation, CancellationToken ct) =>
        // A task callback may legitimately run long: its deadline starts only after cancellation was requested.
        CallbackAsync(activation, "tasks.invoke", parameters, cancellation, ct, "tasks.cancel", responseDeadline: false);

    /// <summary>Runs an SDK resource release callback while the native resource owner remains admitted.</summary>
    /// <param name="activation">The activation whose callback is running.</param>
    /// <param name="parameters">The callback arguments and invocation identity.</param>
    /// <param name="cancellation">The cancellation notification's activation and invocation identity.</param>
    /// <param name="ct">Requests cancellation without abandoning the response.</param>
    public Task<JsonElement> InvokeResourceReleaseAsync(RemoteActivation activation, object parameters, object cancellation, CancellationToken ct) =>
        CallbackAsync(activation, "resources.invoke", parameters, cancellation, ct, "resources.cancel");

    /// <summary>Runs one SDK message middleware callback while retaining its native continuation.</summary>
    /// <param name="activation">The activation whose callback is running.</param>
    /// <param name="parameters">The callback arguments and invocation identity.</param>
    /// <param name="cancellation">The cancellation notification's activation and invocation identity.</param>
    /// <param name="ct">Requests cancellation without abandoning the response.</param>
    public Task<JsonElement> InvokeMessageMiddlewareAsync(RemoteActivation activation, object parameters, object cancellation, CancellationToken ct) =>
        CallbackAsync(activation, "messages.invoke", parameters, cancellation, ct, "messages.cancel");

    private async Task<JsonElement> CallbackAsync(RemoteActivation activation, string method, object parameters, object cancellation,
        CancellationToken ct, string cancellationMethod, bool responseDeadline = true)
    {
        try
        {
            return await _process.RequestUntilResponseAsync(method, parameters, cancellation, ct, cancellationMethod, responseDeadline).ConfigureAwait(false);
        }
        catch (RemoteTimeoutException ex)
        {
            // A callback that outlived its deadline leaves its work unowned, so the row must stop instead of continuing.
            activation.Fault(ex);
            throw;
        }
    }

    /// <summary>Signals one prepared SDK activation without abandoning the transport write.</summary>
    public Task CancelLifetimeAsync(string activation) =>
        _process.NotifyAsync("lifecycle.cancel", new { activation });

    /// <summary>Sends <c>activate</c> raced against the activation's fault signal.</summary>
    /// <param name="activation">The activation.</param>
    /// <param name="parameters">The params object.</param>
    /// <param name="ct">Cancels the wait.</param>
    public async Task ActivateAsync(RemoteActivation activation, object parameters, CancellationToken ct)
    {
        var request = RequestAsync(activation, "activate", parameters, ct);
        var first = await Task.WhenAny(request, activation.Faulted).ConfigureAwait(false);
        if (first != request)
        {
            _ = request.ContinueWith(task => task.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            throw new RemoteProcessException($"SDK plugin '{activation.Definition.Id}' failed during activation: {activation.Faulted.Result.Message}", activation.Faulted.Result);
        }
        await request.ConfigureAwait(false);
    }

    /// <summary>Registers an activation so plugin calls can reach it, faulting it at once if the process is already gone.</summary>
    /// <param name="activation">The activation.</param>
    public void Add(RemoteActivation activation)
    {
        if (_process.Faulted.IsCompleted)
            throw new RemoteProcessException($"SDK plugin process for '{activation.Definition.Id}' is not running: {_process.Faulted.Result.Message}", _process.Faulted.Result);
        _activations[activation.Id] = activation;
        // The process can fail between the first check and insertion, after its observer took a snapshot.
        if (_process.Faulted.IsCompleted)
            activation.Fault(_process.Faulted.Result);
    }

    /// <summary>Forgets an activation.</summary>
    /// <param name="activation">The activation.</param>
    public void Remove(RemoteActivation activation)
    {
        _activations.TryRemove(activation.Id, out _);
        activation.Sessions.Clear();
    }

    /// <inheritdoc />
    public ValueTask<JsonElement> HandleRequestAsync(string method, JsonElement parameters, CancellationToken ct)
    {
        switch (method)
        {
            case "lifecycle.ready" when _protocolVersion >= 2:
                return new ValueTask<JsonElement>(LiveActivation(parameters).ObserveLifetimeAsync());
            case "services.call" when _protocolVersion >= 2:
                return new ValueTask<JsonElement>(LiveActivation(parameters).CallServiceAsync(parameters, ct));
            case "services.get_all" when _protocolVersion >= 2:
                return ValueTask.FromResult(LiveActivation(parameters).GetAllServices(parameters));
            case "services.provide" when _protocolVersion >= 2:
                return ValueTask.FromResult(LiveActivation(parameters).Provide(parameters));
            case "services.inner" when _protocolVersion >= 2:
                return new ValueTask<JsonElement>(LiveActivation(parameters).CallInnerAsync(parameters, ct));
            case "events.on" when _protocolVersion >= 2:
                return ValueTask.FromResult(LiveActivation(parameters).OnEvent(parameters));
            case "events.publish" when _protocolVersion >= 2:
                return new ValueTask<JsonElement>(LiveActivation(parameters).PublishEventAsync(parameters, ct));
            case "events.next" when _protocolVersion >= 2:
                return new ValueTask<JsonElement>(LiveActivation(parameters).NextEventAsync(parameters, ct));
            case "tasks.start" when _protocolVersion >= 2:
                return new ValueTask<JsonElement>(LiveActivation(parameters).StartTaskAsync(parameters, ct));
            case "tasks.wait" when _protocolVersion >= 2:
                return new ValueTask<JsonElement>(LiveActivation(parameters).WaitTaskAsync(parameters, ct));
            case "tasks.dispose" when _protocolVersion >= 2:
                return new ValueTask<JsonElement>(LiveActivation(parameters).DisposeTaskAsync(parameters, ct));
            case "resources.scope" when _protocolVersion >= 2:
                return new ValueTask<JsonElement>(LiveActivation(parameters).CreateResourceScopeAsync(parameters, ct));
            case "resources.scope.dispose" when _protocolVersion >= 2:
                return new ValueTask<JsonElement>(LiveActivation(parameters).DisposeResourceScopeAsync(parameters, ct));
            case "resources.acquire" when _protocolVersion >= 2:
                return new ValueTask<JsonElement>(LiveActivation(parameters).AcquireResourceAsync(parameters, ct));
            case "resources.dispose" when _protocolVersion >= 2:
                return new ValueTask<JsonElement>(LiveActivation(parameters).DisposeResourceAsync(parameters, ct));
            case "messages.on":
                return ValueTask.FromResult(LiveActivation(parameters).On(parameters));
            case "messages.override":
                return ValueTask.FromResult(LiveActivation(parameters).Override(parameters));
            case "messages.use" when _protocolVersion >= 2:
                return ValueTask.FromResult(LiveActivation(parameters).Use(parameters));
            case "messages.next" when _protocolVersion >= 2:
                return new ValueTask<JsonElement>(LiveActivation(parameters).NextMessageAsync(parameters, ct));
            case "messages.send":
                return new ValueTask<JsonElement>(LiveActivation(parameters).SendAsync(parameters, ct));
            case "effect.dispose":
                return ValueTask.FromResult(LiveActivation(parameters).DisposeInEventContext(parameters));
            default:
                throw new RpcException(RpcCodes.MethodNotFound, $"unknown method '{method}'");
        }
    }

    /// <inheritdoc />
    public void HandleNotification(string method, JsonElement parameters)
    {
        if (method != "log")
        {
            _logger.LogWarning("SDK plugin source '{Source}' sent unknown notification '{Method}'.", _sourceId, method);
            return;
        }
        var logger = _logger;
        if (parameters.TryGetProperty("activation", out var id) && id.ValueKind == JsonValueKind.String
            && _activations.TryGetValue(id.GetString()!, out var activation))
            logger = activation.Context.Logger;
        var level = parameters.TryGetProperty("level", out var levelValue) && levelValue.ValueKind == JsonValueKind.String ? levelValue.GetString() : null;
        var text = parameters.TryGetProperty("message", out var messageValue) && messageValue.ValueKind == JsonValueKind.String ? messageValue.GetString() : null;
        if (text is null || level is null)
        {
            _logger.LogWarning("SDK plugin source '{Source}' sent a log notification without 'level' and 'message'.", _sourceId);
            return;
        }
        var mapped = level switch
        {
            "trace" => LogLevel.Trace,
            "debug" => LogLevel.Debug,
            "info" => LogLevel.Information,
            "warn" => LogLevel.Warning,
            "error" => LogLevel.Error,
            _ => (LogLevel?)null,
        };
        if (mapped is not { } logLevel)
        {
            _logger.LogWarning("SDK plugin source '{Source}' sent unknown log level '{Level}'.", _sourceId, level);
            return;
        }
        if (parameters.TryGetProperty("fields", out var fields) && fields.ValueKind == JsonValueKind.Object)
            logger.Log(logLevel, "{Message} {Fields}", text, fields.GetRawText());
        else
            logger.Log(logLevel, "{Message}", text);
    }

    /// <summary>Stops the process after the host has drained every row served by it.</summary>
    public ValueTask DisposeAsync() => _process is null ? ValueTask.CompletedTask : _process.DisposeAsync();

    private async Task DescribeAsync(RemoteEntry entry, RemoteLoaderConfig config, CancellationToken ct)
    {
        var contracts = ContractVersions();
        var init = await _process.RequestAsync("initialize", new
        {
            protocol_version = 1,
            protocol_versions = new[] { 1, 2 },
            features = RemoteProtocolFeatures.Names,
            max_in_flight = config.Transport.MaxInFlight,
            max_reentry_depth = config.MaxReentryDepth,
            host_version = typeof(IPlugin).Assembly.GetName().Version!.ToString(),
            contracts,
        }, ct).ConfigureAwait(false);
        if (init.ValueKind != JsonValueKind.Object || !init.TryGetProperty("protocol_version", out var version)
            || !version.TryGetInt32(out var number) || number is not (1 or 2))
            throw new ConfigException($"SDK plugin source '{entry.SourceId}' answered initialize without a supported protocol_version (1 or 2).");
        _protocolVersion = number;
        if (init.TryGetProperty("sdk", out var sdk) && sdk.ValueKind == JsonValueKind.Object)
            _logger.LogInformation("SDK plugin source '{Source}' runs SDK {Sdk}.", entry.SourceId, sdk.GetRawText());

        var described = await _process.RequestAsync("describe", new { }, ct).ConfigureAwait(false);
        if (described.ValueKind != JsonValueKind.Object || !described.TryGetProperty("definitions", out var list) || list.ValueKind != JsonValueKind.Array)
            throw new ConfigException($"SDK plugin source '{entry.SourceId}': describe must return an object with a 'definitions' list.");
        if (list.GetArrayLength() > entry.MaxDefinitions)
            throw new ConfigException($"SDK plugin source '{entry.SourceId}' described {list.GetArrayLength()} definitions, over its max_definitions {entry.MaxDefinitions}.");

        var definitions = new List<PluginDefinition>();
        var ids = new List<string>();
        foreach (var element in list.EnumerateArray())
        {
            var definition = RemoteDefinition.Parse(element, entry.SourceId, _protocolVersion, entry.AllowedServices, entry.AllowedEvents);
            if (ids.Contains(definition.Id))
                throw new ConfigException($"SDK plugin source '{entry.SourceId}' described plugin '{definition.Id}' twice.");
            ids.Add(definition.Id);
            var factory = new RemoteExecutionFactory(this, definition, contracts, config.MaxSessions, config.Transport.RequestTimeout);
            definitions.Add(new PluginDefinition(definition.Declaration, factory, $"remote:{entry.SourceId}", definition.Version, contracts));
        }
        Definitions = definitions;
        _process.Label = $"{entry.SourceId}:{string.Join(",", ids)}";
    }

    internal static Dictionary<string, string> ContractVersions() => new(StringComparer.Ordinal)
    {
        ["BubbleFighter.Fantasy.Abstractions"] = typeof(IPlugin).Assembly.GetName().Version!.ToString(),
        ["BubbleFighter.Fantasy.Contracts"] = typeof(BubbleFighter.Fantasy.Contracts.Lobby.LoginRequested).Assembly.GetName().Version!.ToString(),
    };

    private RemoteActivation LiveActivation(JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object || !parameters.TryGetProperty("activation", out var id) || id.ValueKind != JsonValueKind.String)
            throw new RpcException(RpcCodes.InvalidParams, "'activation' must be a string");
        if (!_activations.TryGetValue(id.GetString()!, out var activation) || !activation.Live)
            throw new RpcException(RpcCodes.ActivationNotLive, $"activation '{id.GetString()}' is not live");
        return activation;
    }
}
