using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using BubbleFighter.Fantasy.Contracts.Companion;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Companion;

/// <summary>
/// One negotiated companion connection. It answers the launcher's heartbeats, dispatches the client events and requests
/// it negotiated to their server handlers, and sends server events once the launch is bound to its game session.
/// Every queue is bounded; a peer that breaks the protocol or cannot keep up is disconnected with a reason code, never
/// resynchronised, and reconnects with a fresh ticket and epoch.
/// </summary>
internal sealed class CompanionSocket
{
    private static readonly IReadOnlyDictionary<string, int> None = new Dictionary<string, int>();
    private readonly WebSocket _socket;
    private readonly CompanionOptions _options;
    private readonly JsonSerializerOptions _json;
    private readonly CompanionHub _hub;
    private readonly CompanionLaunches _launches;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<byte[]> _outbound;
    private readonly SemaphoreSlim _handlers;
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly Queue<string> _seenOrder = new();
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IReadOnlyDictionary<string, int> _capabilities = None;
    private string _locale = "en";
    private int _queuedBytes, _connected, _active;
    private string? _closeReason;

    internal CompanionSocket(WebSocket socket, CompanionOptions options, JsonSerializerOptions json, CompanionHub hub,
        CompanionLaunches launches, ILogger logger)
    {
        _socket = socket; _options = options; _json = json; _hub = hub; _launches = launches; _logger = logger;
        _outbound = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(options.QueueMessages) { SingleReader = true });
        _handlers = new SemaphoreSlim(options.MaxConcurrentRequests);
    }

    /// <summary>Gets this connection's epoch, carried by every message after the welcome.</summary>
    internal string Epoch { get; } = CompanionWire.NewId();

    /// <summary>Gets the launch this connection belongs to once attached.</summary>
    internal CompanionLaunches.Launch? Launch { get; private set; }

    /// <summary>Gets why the connection closed, or null while it is open.</summary>
    internal string? CloseReason => Volatile.Read(ref _closeReason);

    internal bool Connected => Volatile.Read(ref _connected) == 1;

    internal void Negotiate(IReadOnlyDictionary<string, int> capabilities, string locale)
    {
        _capabilities = capabilities;
        _locale = locale;
    }

    /// <summary>Called by the launch table under its lock when the connection becomes the launch's.</summary>
    internal void Initialize(CompanionLaunches.Launch launch, bool bound)
    {
        Launch = launch;
        Volatile.Write(ref _connected, bound ? 1 : 0);
    }

    internal bool Negotiated(string type, int version) =>
        Connected && !_stop.IsCancellationRequested && _capabilities.TryGetValue(type, out var accepted) && accepted == version;

    /// <summary>Queues a server event; false when it was not negotiated, the launch is not bound or the queue is full.</summary>
    internal bool TrySend(string type, int version, object? payload)
    {
        if (!Negotiated(type, version)) return false;
        byte[] bytes;
        try { bytes = CompanionWire.Encode(new("event", CompanionWire.NewId(), type, version, CompanionWire.Payload(payload, _json), Epoch: Epoch), _json, _options.MessageBytes); }
        catch (InvalidDataException)
        {
            _logger.LogWarning("Companion event {Type} v{Version} exceeds message_bytes and was not sent.", type, version);
            return false;
        }
        return Enqueue(bytes);
    }

    /// <summary>Tells the launcher its launch is now bound to the game session.</summary>
    internal void MarkConnected()
    {
        if (Interlocked.CompareExchange(ref _connected, 1, 0) == 0)
            Control(new("state", CompanionWire.NewId(), "companion.state", 1, CompanionWire.Payload(new { State = "connected" }, _json), Epoch: Epoch));
    }

    /// <summary>
    /// Ends the connection; the first reason is kept. The send loop flushes what is queued and sends a close frame carrying
    /// the reason, because cancelling a pending WebSocket receive would abort the transport before the peer learns why;
    /// a peer that does not answer the close is cut off after <see cref="CloseGrace"/>.
    /// </summary>
    internal void Close(string reason)
    {
        if (Interlocked.CompareExchange(ref _closeReason, reason, null) is not null) return;
        _outbound.Writer.TryComplete();
        try { _stop.CancelAfter(CloseGrace); }
        catch (ObjectDisposedException) { }
    }

    private static readonly TimeSpan CloseGrace = TimeSpan.FromSeconds(2);

    /// <summary>Sends the welcome, then serves the connection until either side ends it.</summary>
    internal async Task RunAsync(byte[] welcome, CancellationToken shutdown)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, shutdown);
        var token = lifetime.Token;
        Task sender = Task.CompletedTask;
        try
        {
            await _socket.SendAsync(welcome, WebSocketMessageType.Text, true, token).ConfigureAwait(false);
            sender = SendLoopAsync(token);
            await ReceiveLoopAsync(token).ConfigureAwait(false);
        }
        catch (InvalidDataException error) { Close(error.Message); }
        catch (OperationCanceledException) { Close(shutdown.IsCancellationRequested ? "companion.server-stopping" : "companion.closed"); }
        catch (Exception error) when (error is WebSocketException or IOException) { Close("companion.transport-ended"); }
        finally
        {
            Close("companion.closed");
            await sender.ConfigureAwait(false);
            _stop.Cancel();
            // Handlers see the cancellation; the connection is released once they return or the shutdown budget ends.
            if (Volatile.Read(ref _active) != 0)
                await _drained.Task.WaitAsync(TimeSpan.FromSeconds(_options.ShutdownSeconds)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            _launches.Detach(this);
            await AwaitPeerCloseAsync().ConfigureAwait(false);
            _logger.LogDebug("Companion connection for launch {LaunchId} closed: {Reason}.", Launch?.Id, CloseReason);
        }
    }

    /// <summary>
    /// After a close the receive loop did not see answered, waits briefly for the peer's close frame: resetting the
    /// transport at once can discard the close frame, and its reason, before the launcher reads it.
    /// </summary>
    private async Task AwaitPeerCloseAsync()
    {
        if (_socket.State == WebSocketState.CloseSent)
        {
            using var grace = new CancellationTokenSource(CloseGrace);
            var buffer = new byte[256];
            try
            {
                while ((await _socket.ReceiveAsync(buffer.AsMemory(), grace.Token).ConfigureAwait(false)).MessageType != WebSocketMessageType.Close) { }
            }
            catch (Exception error) when (error is OperationCanceledException or WebSocketException or IOException) { }
        }
        if (_socket.State != WebSocketState.Closed) _socket.Abort();
    }

    private async Task ReceiveLoopAsync(CancellationToken token)
    {
        var window = Stopwatch.StartNew();
        var count = 0;
        while (true)
        {
            CompanionEnvelope? message;
            using (var idle = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                idle.CancelAfter(TimeSpan.FromSeconds(_options.IdleSeconds));
                try { message = await CompanionWire.ReceiveAsync(_socket, _options, _json, idle.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new InvalidDataException("companion.idle"); }
            }
            if (message is null) { Close("companion.peer-closed"); return; }
            if (window.Elapsed >= TimeSpan.FromSeconds(1)) { window.Restart(); count = 0; }
            if (++count > _options.MessagesPerSecond) throw new InvalidDataException("companion.rate-limit");
            if (message.Epoch != Epoch) throw new InvalidDataException("companion.stale-epoch");
            if (!_seen.Add(message.Id)) throw new InvalidDataException("companion.duplicate-id");
            _seenOrder.Enqueue(message.Id);
            if (_seenOrder.Count > 1024) _seen.Remove(_seenOrder.Dequeue());
            switch (message.Kind)
            {
                case "heartbeat":
                    if (message.Type != "companion.heartbeat" || message.Version != 1) throw new InvalidDataException("companion.invalid-heartbeat");
                    Control(new("heartbeat_ack", CompanionWire.NewId(), "companion.heartbeat", 1, CompanionWire.Payload(null, _json), message.Id, Epoch));
                    break;
                case "event" or "request":
                    Dispatch(message, token);
                    break;
                default:
                    // The server never probes or sends requests, so acknowledgements and replies from the launcher are errors.
                    throw new InvalidDataException("companion.unexpected-kind");
            }
        }
    }

    private void Dispatch(CompanionEnvelope message, CancellationToken token)
    {
        // The launcher sends application messages only after it has seen the binding.
        if (!Connected || Launch?.Session is not { } session) throw new InvalidDataException("companion.not-bound");
        if (!_capabilities.TryGetValue(message.Type, out var version) || version != message.Version)
            throw new InvalidDataException("companion.unnegotiated-type");
        var request = message.Kind == "request";
        if (_hub.Handler(message.Type, message.Version) is not { } handler)
        {
            // The route was withdrawn after this connection negotiated it.
            if (request) Reply(message, "error", new { Code = "unavailable" });
            return;
        }
        if (!_handlers.Wait(0))
        {
            if (!request) throw new InvalidDataException("companion.slow-server");
            Reply(message, "error", new { Code = "busy" });
            return;
        }
        var caller = new CompanionCaller(Launch.Account, Launch.Build, Launch.Id, session, _locale);
        Interlocked.Increment(ref _active);
        _ = HandleAsync(handler, caller, message, request, token);
    }

    private async Task HandleAsync(CompanionHandler handler, CompanionCaller caller, CompanionEnvelope message, bool request, CancellationToken token)
    {
        await Task.Yield();
        try
        {
            var result = await handler(caller, message.Payload, token).ConfigureAwait(false);
            if (request) Reply(message, "response", result);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is ArgumentException or JsonException or KeyNotFoundException or InvalidDataException or FormatException)
        {
            if (request) Reply(message, "error", new { Code = "invalid_request" });
            else _logger.LogInformation("Companion event {Type} v{Version} from launch {LaunchId} was refused: {Reason}", message.Type, message.Version, caller.LaunchId, error.GetType().Name);
        }
        catch (Exception error)
        {
            _logger.LogError(error, "Companion handler for {Type} v{Version} failed.", message.Type, message.Version);
            if (request) Reply(message, "error", new { Code = "failed" });
        }
        finally
        {
            _handlers.Release();
            if (Interlocked.Decrement(ref _active) == 0 && _stop.IsCancellationRequested) _drained.TrySetResult();
        }
    }

    private void Reply(CompanionEnvelope message, string kind, object? payload)
    {
        byte[] bytes;
        try { bytes = CompanionWire.Encode(new(kind, CompanionWire.NewId(), message.Type, message.Version, CompanionWire.Payload(payload, _json), message.Id, Epoch), _json, _options.MessageBytes); }
        catch (InvalidDataException)
        {
            _logger.LogWarning("Companion reply to {Type} v{Version} exceeds message_bytes; an error was sent instead.", message.Type, message.Version);
            bytes = CompanionWire.Encode(new("error", CompanionWire.NewId(), message.Type, message.Version, CompanionWire.Payload(new { Code = "reply_too_large" }, _json), message.Id, Epoch), _json, _options.MessageBytes);
        }
        if (!Enqueue(bytes)) Close("companion.slow-consumer");
    }

    private void Control(CompanionEnvelope envelope)
    {
        if (!Enqueue(CompanionWire.Encode(envelope, _json, _options.MessageBytes))) Close("companion.slow-consumer");
    }

    private bool Enqueue(byte[] bytes)
    {
        if (Interlocked.Add(ref _queuedBytes, bytes.Length) > _options.QueueBytes || !_outbound.Writer.TryWrite(bytes))
        {
            Interlocked.Add(ref _queuedBytes, -bytes.Length);
            return false;
        }
        return true;
    }

    private async Task SendLoopAsync(CancellationToken token)
    {
        try
        {
            await foreach (var bytes in _outbound.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                Interlocked.Add(ref _queuedBytes, -bytes.Length);
                await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, token).ConfigureAwait(false);
            }
            if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                var status = CloseReason is "companion.closed" or "companion.peer-closed" or "companion.game-ended" or "companion.server-stopping"
                    ? WebSocketCloseStatus.NormalClosure : WebSocketCloseStatus.PolicyViolation;
                await _socket.CloseOutputAsync(status, CloseReason, token).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is OperationCanceledException or WebSocketException or IOException or ObjectDisposedException)
        { Close("companion.send-ended"); }
    }
}
