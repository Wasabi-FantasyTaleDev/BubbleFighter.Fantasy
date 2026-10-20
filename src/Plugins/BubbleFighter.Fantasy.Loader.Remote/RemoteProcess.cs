using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Loader.Remote;

/// <summary>The transport bounds of one remote process (spec section 3).</summary>
/// <param name="MaxLineBytes">Maximum bytes in one JSON line.</param>
/// <param name="MaxInFlight">Maximum outstanding requests per direction.</param>
/// <param name="RequestTimeout">How long a host-to-plugin request may wait for its response.</param>
/// <param name="ShutdownTimeout">How long the process gets to exit after the shutdown notification.</param>
internal sealed record TransportOptions(int MaxLineBytes, int MaxInFlight, TimeSpan RequestTimeout, TimeSpan ShutdownTimeout);

/// <summary>The command line of one SDK plugin process after path resolution.</summary>
/// <param name="Command">The executable, a bare name for PATH lookup or a full path.</param>
/// <param name="Args">The arguments.</param>
/// <param name="WorkingDirectory">The existing working directory.</param>
/// <param name="Env">Extra environment variables.</param>
internal sealed record ProcessLaunch(string Command, IReadOnlyList<string> Args, string WorkingDirectory, IReadOnlyDictionary<string, string> Env);

/// <summary>Receives what the plugin sends to the host.</summary>
internal interface IInboundSink
{
    /// <summary>Serves a plugin-to-host request; throws <see cref="RpcException"/> to answer with an error.</summary>
    /// <param name="method">The method name.</param>
    /// <param name="parameters">The params object.</param>
    /// <param name="ct">Cancelled when the process stops.</param>
    /// <returns>The result value.</returns>
    ValueTask<JsonElement> HandleRequestAsync(string method, JsonElement parameters, CancellationToken ct);

    /// <summary>Serves a plugin-to-host notification.</summary>
    /// <param name="method">The method name.</param>
    /// <param name="parameters">The params object.</param>
    void HandleNotification(string method, JsonElement parameters);
}

/// <summary>
/// One child process speaking JSON-RPC 2.0 over stdio, one object per line (spec section 3).
/// Every queue is bounded: line length, outstanding requests and stderr truncation are all fixed by <see cref="TransportOptions"/>.
/// </summary>
internal sealed class RemoteProcess : IAsyncDisposable
{
    private static readonly byte[] Newline = [(byte)'\n'];

    // Long-running callbacks (tasks, events, middleware) can fill every ordinary slot, and teardown must still reach
    // the plugin so its row can unload. The reserve only admits the two lifecycle control methods, so it is a fixed
    // protocol constant rather than a deployment tunable.
    private const int ControlReserve = 4;

    private readonly IPluginChannel _process;
    private readonly TransportOptions _options;
    private readonly ILogger _logger;
    private readonly IInboundSink _sink;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly Dictionary<long, TaskCompletionSource<JsonElement>> _pending = [];
    private readonly Lock _gate = new();
    private readonly TaskCompletionSource<Exception> _faulted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _readLoop;
    private readonly Task _errorLoop;
    private string _label;
    private long _nextId;
    private int _inbound;
    private Exception? _failure;
    private volatile bool _stopping;
    private int _disposed;

    private RemoteProcess(IPluginChannel process, TransportOptions options, ILogger logger, IInboundSink sink, string label)
    {
        _process = process;
        _options = options;
        _logger = logger;
        _sink = sink;
        _label = label;
        _readLoop = Task.Run(ReadLoopAsync);
        _errorLoop = Task.Run(ErrorLoopAsync);
    }

    /// <summary>Gets a task that completes with the cause when the process dies or breaks the protocol while not being stopped. It never faults.</summary>
    public Task<Exception> Faulted => _faulted.Task;

    /// <summary>Gets the text prefixed to forwarded stderr lines; the loader sets it to the plugin ids once they are known.</summary>
    public string Label { get => Volatile.Read(ref _label); set => Volatile.Write(ref _label, value); }

    /// <summary>Gets the number of host-to-plugin requests currently waiting for a response.</summary>
    public int InFlight { get { lock (_gate) return _pending.Count; } }

    /// <summary>Starts the child process and its reader loops.</summary>
    /// <param name="launch">The resolved command line.</param>
    /// <param name="options">The transport bounds.</param>
    /// <param name="logger">The logger for stderr and diagnostics.</param>
    /// <param name="sink">Receives plugin-to-host traffic.</param>
    /// <param name="label">The prefix for forwarded stderr lines.</param>
    /// <returns>The running process.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the process cannot start.</exception>
    public static RemoteProcess Start(ProcessLaunch launch, TransportOptions options, ILogger logger, IInboundSink sink, string label) =>
        new(ProcessChannel.Start(launch), options, logger, sink, label);

    /// <summary>Runs the protocol over an already started channel, such as a WebAssembly module.</summary>
    /// <param name="channel">The running plugin; the returned object owns it.</param>
    /// <param name="options">The transport bounds.</param>
    /// <param name="logger">The logger for diagnostics.</param>
    /// <param name="sink">Receives plugin-to-host traffic.</param>
    /// <param name="label">The prefix for forwarded diagnostic lines.</param>
    /// <returns>The running plugin.</returns>
    public static RemoteProcess Start(IPluginChannel channel, TransportOptions options, ILogger logger, IInboundSink sink, string label) =>
        new(channel, options, logger, sink, label);

    /// <summary>Sends a request and waits for its response, failing on timeout, process loss or a plugin error.</summary>
    /// <param name="method">The method name.</param>
    /// <param name="parameters">The params object, serialized as given.</param>
    /// <param name="ct">Cancels the wait; the plugin may still run the call.</param>
    /// <returns>The result value.</returns>
    /// <exception cref="RpcException">Thrown when the plugin answered with an error object.</exception>
    /// <exception cref="RemoteTimeoutException">Thrown when no response arrives within the request timeout.</exception>
    /// <exception cref="RemoteBackpressureException">Thrown when the in-flight bound is reached.</exception>
    /// <exception cref="RemoteProcessException">Thrown when the process is gone or broke the protocol.</exception>
    public Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken ct) =>
        RequestCoreAsync(method, parameters, ct, null);

    /// <summary>Keeps a callback request charged until its actual response or process loss, signaling cooperative cancellation after dispatch.</summary>
    /// <param name="method">The callback method.</param>
    /// <param name="parameters">The callback arguments and invocation identity.</param>
    /// <param name="cancellation">The cancellation notification's activation and invocation identity.</param>
    /// <param name="ct">Requests cancellation without abandoning the response or its resource ownership.</param>
    /// <param name="cancellationMethod">The notification for the callback family; cancellation follows the invocation on the wire.</param>
    /// <param name="responseDeadline">
    /// Whether <c>request_timeout_ms</c> bounds the wait before cancellation is requested; a task callback may legitimately
    /// run long, so its deadline starts only once cancellation was requested.
    /// </param>
    public Task<JsonElement> RequestUntilResponseAsync(string method, object parameters, object cancellation, CancellationToken ct,
        string cancellationMethod = "services.cancel", bool responseDeadline = true) =>
        RequestCoreAsync(method, parameters, ct, cancellation, cancellationMethod, responseDeadline);

    private async Task<JsonElement> RequestCoreAsync(string method, object parameters, CancellationToken ct, object? cancellation,
        string cancellationMethod = "services.cancel", bool responseDeadline = true)
    {
        var response = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        long id;
        lock (_gate)
        {
            if (_failure is not null)
                throw new RemoteProcessException($"SDK plugin process is not running: {_failure.Message}", _failure);
            var limit = ControlMethod(method) ? _options.MaxInFlight + ControlReserve : _options.MaxInFlight;
            if (_pending.Count >= limit)
            {
                _logger.LogWarning("SDK plugin {Label} already has {Count} requests in flight; refusing '{Method}'.", Label, _pending.Count, method);
                throw new RemoteBackpressureException(
                    $"SDK plugin {Label} already has {_pending.Count} requests in flight (max_in_flight {_options.MaxInFlight}).");
            }
            id = ++_nextId;
            _pending[id] = response;
        }
        try
        {
            var line = Frame(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("jsonrpc", "2.0");
                writer.WriteNumber("id", id);
                writer.WriteString("method", method);
                writer.WritePropertyName("params");
                JsonSerializer.Serialize(writer, parameters);
                writer.WriteEndObject();
            });
            var written = WriteAsync(line);
            if (cancellation is not null)
            {
                // Cancellation must follow the invocation on the wire; unknown invocation ids are never queued by the SDK.
                await written.ConfigureAwait(false);
                using var signal = CancellationTokenSource.CreateLinkedTokenSource(ct);
                using var deadline = new CancellationTokenSource(responseDeadline ? _options.RequestTimeout : Timeout.InfiniteTimeSpan);
                try
                {
                    var cancelled = Task.Delay(Timeout.Infinite, signal.Token);
                    var expired = Task.Delay(Timeout.Infinite, deadline.Token);
                    if (await Task.WhenAny(response.Task, cancelled, expired).ConfigureAwait(false) == response.Task)
                        return await response.Task.ConfigureAwait(false);
                    // Ask the callback to stop, then bound how long its actual completion may take. A callback nobody
                    // answers must fail the activation instead of pinning its native ownership forever.
                    await NotifyAsync(cancellationMethod, cancellation).ConfigureAwait(false);
                    try
                    {
                        return await response.Task.WaitAsync(_options.ShutdownTimeout).ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                        var bound = responseDeadline
                            ? $"{_options.RequestTimeout.TotalMilliseconds:0} ms (request_timeout_ms) plus {_options.ShutdownTimeout.TotalMilliseconds:0} ms (shutdown_timeout_ms) after cancellation"
                            : $"{_options.ShutdownTimeout.TotalMilliseconds:0} ms (shutdown_timeout_ms) after cancellation";
                        throw new RemoteTimeoutException($"SDK plugin {Label} did not answer '{method}' within {bound}.");
                    }
                }
                finally { signal.Cancel(); }
            }
            _ = written.ContinueWith(task =>
                {
                    var cause = task.Exception!.InnerException ?? task.Exception;
                    response.TrySetException(new RemoteProcessException($"SDK plugin {Label} could not be written to: {cause.Message}", cause));
                }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            try
            {
                return await response.Task.WaitAsync(_options.RequestTimeout, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new RemoteTimeoutException(
                    $"SDK plugin {Label} did not answer '{method}' within {_options.RequestTimeout.TotalMilliseconds:0} ms (request_timeout_ms).");
            }
        }
        finally
        {
            lock (_gate)
                _pending.Remove(id);
        }
    }

    /// <summary>Gets whether a method ends an activation or the process, so its row can drain even when callbacks fill the ordinary slots.</summary>
    /// <param name="method">The host-to-plugin method name.</param>
    /// <returns>Whether the method may use the control reserve.</returns>
    private static bool ControlMethod(string method) => method is "deactivate" or "shutdown";

    /// <summary>Sends a notification; no response is expected.</summary>
    /// <param name="method">The method name.</param>
    /// <param name="parameters">The params object.</param>
    public Task NotifyAsync(string method, object parameters)
    {
        var line = Frame(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WriteString("method", method);
            writer.WritePropertyName("params");
            JsonSerializer.Serialize(writer, parameters);
            writer.WriteEndObject();
        });
        return WriteAsync(line);
    }

    /// <summary>Asks the process to shut down, waits up to the shutdown timeout, then kills it.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _stopping = true;
        try
        {
            if (_failure is null)
                await NotifyAsync("shutdown", new { }).WaitAsync(_options.ShutdownTimeout).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or TimeoutException or InvalidOperationException)
        {
            _logger.LogDebug(ex, "SDK plugin {Label} could not be told to shut down.", Label);
        }
        try { _process.CloseInput(); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException) { /* already closed */ }
        try
        {
            using var grace = new CancellationTokenSource(_options.ShutdownTimeout);
            await _process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("SDK plugin {Label} did not exit within {Timeout} ms; killing it.", Label, _options.ShutdownTimeout.TotalMilliseconds);
            Kill();
        }
        catch (InvalidOperationException) { /* never started or already reaped */ }
        await _stop.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(_readLoop, _errorLoop).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Fail(new RemoteProcessException("SDK plugin process was stopped."));
        _process.Dispose();
        _stop.Dispose();
    }

    private async Task WriteAsync(byte[] line)
    {
        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var input = _process.Input;
            await input.WriteAsync(line).ConfigureAwait(false);
            await input.WriteAsync(Newline).ConfigureAwait(false);
            await input.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private byte[] Frame(Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
            write(writer);
        if (buffer.Length > _options.MaxLineBytes)
            throw new InvalidOperationException(
                $"A message to SDK plugin {Label} is {buffer.Length} bytes, over max_line_bytes {_options.MaxLineBytes}.");
        return buffer.ToArray();
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            await foreach (var line in BoundedLineReader.ReadAsync(_process.Output, _options.MaxLineBytes, truncate: false, _stop.Token).ConfigureAwait(false))
            {
                if (line.Bytes.IsEmpty)
                    continue;
                Dispatch(line.Bytes);
            }
            var exit = await ExitDescriptionAsync().ConfigureAwait(false);
            Fail(new RemoteProcessException($"SDK plugin process ended ({exit})."));
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            // Disposal stopped the loop.
        }
        catch (Exception ex)
        {
            Fail(new RemoteProcessException($"SDK plugin process broke the protocol: {ex.Message}", ex), kill: true);
        }
    }

    private async Task ErrorLoopAsync()
    {
        try
        {
            await foreach (var line in BoundedLineReader.ReadAsync(_process.Error, _options.MaxLineBytes, truncate: true, _stop.Token).ConfigureAwait(false))
            {
                var text = System.Text.Encoding.UTF8.GetString(line.Bytes.Span);
                _logger.LogInformation("[{Label}] {Line}{Truncated}", Label, text, line.Truncated ? " [truncated]" : "");
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidDataException or IOException or ObjectDisposedException)
        {
            // stderr is informational only; its loss never affects the protocol.
        }
    }

    private async Task<string> ExitDescriptionAsync()
    {
        try
        {
            using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
            return $"exit code {_process.ExitCode}";
        }
        catch (OperationCanceledException)
        {
            return "closed its output but is still running";
        }
    }

    private void Dispatch(ReadOnlyMemory<byte> bytes)
    {
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(bytes);
            root = document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"stdout carried a line that is not JSON ({ex.Message}); stdout is reserved for the protocol, use stderr for diagnostics.", ex);
        }
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("A protocol line must be a JSON object.");
        var hasId = root.TryGetProperty("id", out var id);
        if (root.TryGetProperty("method", out var methodElement) && methodElement.ValueKind == JsonValueKind.String)
        {
            var method = methodElement.GetString()!;
            var parameters = root.TryGetProperty("params", out var p) ? p : JsonSerializer.SerializeToElement(new { });
            if (hasId)
                StartInbound(id, method, parameters);
            else
                RunNotification(method, parameters);
            return;
        }
        if (hasId && id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out var responseId)
            && (root.TryGetProperty("result", out var result) || root.TryGetProperty("error", out _)))
        {
            TaskCompletionSource<JsonElement>? waiter;
            lock (_gate)
                _pending.TryGetValue(responseId, out waiter);
            if (waiter is null)
            {
                _logger.LogWarning("SDK plugin {Label} answered unknown or expired request {Id}; ignoring.", Label, responseId);
                return;
            }
            if (root.TryGetProperty("error", out var error))
                waiter.TrySetException(ToRpcException(error));
            else
                waiter.TrySetResult(result);
            return;
        }
        throw new InvalidDataException("A protocol line is neither a request, a notification nor a response.");
    }

    private static RpcException ToRpcException(JsonElement error)
    {
        var code = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var c) && c.TryGetInt32(out var value) ? value : RpcCodes.InternalError;
        var message = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString()! : "remote error";
        JsonElement? data = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("data", out var d) ? d : null;
        return new RpcException(code, message, data);
    }

    private void RunNotification(string method, JsonElement parameters)
    {
        try { _sink.HandleNotification(method, parameters); }
        catch (Exception ex) { _logger.LogWarning(ex, "SDK plugin {Label} notification '{Method}' failed.", Label, method); }
    }

    private void StartInbound(JsonElement id, string method, JsonElement parameters)
    {
        if (Interlocked.Increment(ref _inbound) > _options.MaxInFlight)
        {
            Interlocked.Decrement(ref _inbound);
            _logger.LogWarning("SDK plugin {Label} has too many plugin-to-host requests in flight; refusing '{Method}'.", Label, method);
            _ = Respond(id, null, new RpcException(RpcCodes.Overloaded, $"too many plugin-to-host requests in flight (max_in_flight {_options.MaxInFlight})"));
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await _sink.HandleRequestAsync(method, parameters, _stop.Token).ConfigureAwait(false);
                await Respond(id, result, null).ConfigureAwait(false);
            }
            catch (RpcException rpc)
            {
                await Respond(id, null, rpc).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                // The process is stopping; there is nobody to answer.
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SDK plugin {Label} request '{Method}' failed in the host.", Label, method);
                await Respond(id, null, new RpcException(RpcCodes.InternalError, ex.Message)).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _inbound);
            }
        });
    }

    private async Task Respond(JsonElement id, JsonElement? result, RpcException? error)
    {
        try
        {
            var line = Frame(writer => WriteResponse(writer, id, result, error));
            await WriteAsync(line).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            // The response itself was over the line bound; the plugin still needs an answer.
            var fallback = Frame(writer => WriteResponse(writer, id, null, new RpcException(RpcCodes.InternalError, ex.Message)));
            await WriteAsync(fallback).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            _logger.LogDebug(ex, "SDK plugin {Label} went away before its response could be written.", Label);
        }
    }

    private static void WriteResponse(Utf8JsonWriter writer, JsonElement id, JsonElement? result, RpcException? error)
    {
        writer.WriteStartObject();
        writer.WriteString("jsonrpc", "2.0");
        writer.WritePropertyName("id");
        id.WriteTo(writer);
        if (error is not null)
        {
            writer.WriteStartObject("error");
            writer.WriteNumber("code", error.Code);
            writer.WriteString("message", error.Message);
            if (error.ErrorData is { } data)
            {
                writer.WritePropertyName("data");
                data.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        else
        {
            writer.WritePropertyName("result");
            if (result is { } value)
                value.WriteTo(writer);
            else
                writer.WriteStartObject();
            if (result is null)
                writer.WriteEndObject();
        }
        writer.WriteEndObject();
    }

    private void Fail(Exception failure, bool kill = false)
    {
        List<TaskCompletionSource<JsonElement>> waiters;
        lock (_gate)
        {
            if (_failure is not null)
                return;
            _failure = failure;
            waiters = [.. _pending.Values];
        }
        if (!_stopping)
            _faulted.TrySetResult(failure);
        foreach (var waiter in waiters)
            waiter.TrySetException(failure);
        if (kill)
            Kill();
    }

    private void Kill()
    {
        try { _process.Kill(); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { /* already gone */ }
    }
}
