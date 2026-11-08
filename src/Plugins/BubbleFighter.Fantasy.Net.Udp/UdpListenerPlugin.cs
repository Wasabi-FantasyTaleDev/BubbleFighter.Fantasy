using System.Net;
using System.Net.Sockets;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Net.Udp;

/// <summary>Listens for bounded connectionless UDP datagrams and hands them to an owning plugin handler.</summary>
[Plugin("net.udp", Description = "UDP datagram listener.", ConditionalProvidesMany = [typeof(IUdpDatagramEndpoint)])]
public sealed class UdpListenerPlugin : IPlugin
{
    /// <inheritdoc />
    public async ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<UdpListenerOptions>();
        if (!options.Enabled)
        {
            context.Logger.LogInformation("UDP listener {Row} is disabled by configuration.", context.Row.Id);
            return;
        }
        if (!UdpListenerOptions.TryParseEndPoint(options.Listen, out var endpoint, out var error))
            throw new ConfigException(context.Row.Id, [$"listen: {error}"]);

        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            socket.Bind(endpoint!);
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        var listener = new Listener(context, options, socket);
        context.Effect(listener.Dispose);
        await listener.StartAsync().ConfigureAwait(false);
        context.Effect(context.Services.RegisterMany<IUdpDatagramEndpoint>(listener));
    }

    private sealed class Listener(IPluginContext context, UdpListenerOptions options, Socket socket) : IUdpDatagramEndpoint, IDisposable
    {
        private readonly object _gate = new();
        private Registration? _registration;
        private TaskCompletionSource<Registration> _handlerReady = NewHandlerReady();
        private int _disposed;

        private CancellationToken Lifetime => context.Lifetime;
        public string Protocol => options.Protocol;
        public IPEndPoint LocalEndPoint => (IPEndPoint)socket.LocalEndPoint!;

        public async ValueTask StartAsync()
        {
            await context.RunTaskAsync("udp-receive", ReceiveLoopAsync).ConfigureAwait(false);
            Log(LogLevel.Information, null, $"Row '{context.Row.Id}' listening on UDP {LocalEndPoint} (protocol {Protocol}).");
        }

        public IUdpDatagramSubscription Register(UdpDatagramHandler handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed != 0, this);
                if (_registration is not null)
                    throw new InvalidOperationException($"UDP listener '{context.Row.Id}' already has a handler.");
                var registration = new Registration(this, handler);
                _registration = registration;
                _handlerReady.TrySetResult(registration);
                return registration;
            }
        }

        public async ValueTask SendAsync(ReadOnlyMemory<byte> payload, IPEndPoint remoteEndPoint, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(remoteEndPoint);
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (payload.Length > options.MaxDatagramBytes)
                throw new ArgumentOutOfRangeException(nameof(payload), "Datagram exceeds max_datagram_bytes.");
            var sent = await socket.SendToAsync(payload, SocketFlags.None, remoteEndPoint, ct).ConfigureAwait(false);
            if (sent != payload.Length)
                throw new IOException($"UDP socket sent {sent} of {payload.Length} bytes.");
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Registration? registration;
            lock (_gate)
            {
                registration = _registration;
                _registration = null;
                _handlerReady.TrySetCanceled();
            }
            registration?.Cancel();
            socket.Dispose();
        }

        private async ValueTask ReceiveLoopAsync(CancellationToken lifetime)
        {
            // One extra byte distinguishes an oversized datagram from a valid one that fills the limit.
            var buffer = new byte[options.MaxDatagramBytes + 1];
            while (!lifetime.IsCancellationRequested && _disposed == 0)
            {
                try
                {
                    var handler = await WaitForHandlerAsync(lifetime).ConfigureAwait(false);
                    var received = await socket.ReceiveFromAsync(buffer, SocketFlags.None,
                        new IPEndPoint(IPAddress.Any, 0), handler.Token).ConfigureAwait(false);
                    if (received.ReceivedBytes > options.MaxDatagramBytes)
                    {
                        Log(LogLevel.Warning, null,
                            $"Row '{context.Row.Id}' discarded UDP datagram from {received.RemoteEndPoint}: {received.ReceivedBytes} bytes exceeds max_datagram_bytes {options.MaxDatagramBytes}.");
                        continue;
                    }
                    lock (_gate)
                    {
                        // An unregistered consumer must not receive a datagram from its old blocked socket read.
                        if (_disposed != 0 || !ReferenceEquals(handler, _registration) || !handler.TryEnter())
                            continue;
                    }
                    try
                    {
                        await handler.Handler((IPEndPoint)received.RemoteEndPoint,
                            buffer.AsMemory(0, received.ReceivedBytes).ToArray(), handler.Token).ConfigureAwait(false);
                    }
                    finally { handler.Exit(); }
                }
                catch (OperationCanceledException) when (_disposed != 0 || lifetime.IsCancellationRequested) { break; }
                catch (OperationCanceledException) { continue; }
                catch (ObjectDisposedException) when (_disposed != 0) { break; }
                catch (SocketException) when (_disposed != 0 || lifetime.IsCancellationRequested) { break; }
                catch (SocketException ex)
                {
                    Log(LogLevel.Warning, ex, $"Row '{context.Row.Id}' UDP receive failed ({ex.SocketErrorCode}); continuing.");
                    try { await Task.Delay(TimeSpan.FromSeconds(1), lifetime).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
                catch (Exception ex)
                {
                    Log(LogLevel.Error, ex, $"Row '{context.Row.Id}' UDP datagram handler failed.");
                }
            }
        }

        private void Unregister(Registration registration)
        {
            lock (_gate)
                if (ReferenceEquals(_registration, registration))
                {
                    _registration = null;
                    _handlerReady = NewHandlerReady();
                }
        }

        private Task<Registration> WaitForHandlerAsync(CancellationToken ct)
        {
            lock (_gate)
                return _registration is { } handler ? Task.FromResult(handler) : _handlerReady.Task.WaitAsync(ct);
        }

        private static TaskCompletionSource<Registration> NewHandlerReady() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private void Log(LogLevel level, Exception? exception, string message) =>
            context.Logger.Log(level, new EventId(0), message, exception, static (state, _) => state);

        private sealed class Registration : IUdpDatagramSubscription
        {
            private readonly object _gate = new();
            private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private int _active;
            private Listener? _listener;
            private readonly CancellationTokenSource _cancellation;
            internal UdpDatagramHandler Handler { get; }
            internal CancellationToken Token { get; }
            internal Registration(Listener listener, UdpDatagramHandler handler)
            {
                _listener = listener;
                Handler = handler;
                _cancellation = CancellationTokenSource.CreateLinkedTokenSource(listener.Lifetime);
                Token = _cancellation.Token;
            }
            private int _cancelled;
            internal bool TryEnter()
            {
                lock (_gate)
                {
                    if (_cancelled != 0) return false;
                    _active++;
                    return true;
                }
            }
            internal void Exit()
            {
                lock (_gate)
                    if (--_active == 0 && _cancelled == 2) _drained.TrySetResult();
            }
            internal void Cancel()
            {
                lock (_gate)
                {
                    if (_cancelled != 0) return;
                    _cancelled = 1;
                }
                try { _cancellation.Cancel(); }
                finally
                {
                    _cancellation.Dispose();
                    lock (_gate)
                    {
                        _cancelled = 2;
                        if (_active == 0) _drained.TrySetResult();
                    }
                }
            }
            public void Dispose()
            {
                Interlocked.Exchange(ref _listener, null)?.Unregister(this);
                Cancel();
            }
            public async ValueTask DisposeAsync()
            {
                Dispose();
                await _drained.Task.ConfigureAwait(false);
            }
        }
    }
}
