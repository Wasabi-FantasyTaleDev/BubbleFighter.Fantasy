using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Contracts.Listeners;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Net.Tcp;

/// <summary>
/// TCP listener plugin. Each row binds one endpoint, accepts connections up to a bounded
/// count, announces them via <see cref="ConnectionAcceptedEvent"/>, and hands them to the
/// connection protocol named by the row's <c>protocol</c> option. Several rows can mount
/// the same plugin to listen on several ports.
/// </summary>
[Plugin("net.tcp", Description = "TCP listener.", Requires = [typeof(IConnectionProtocolRegistry), typeof(IEventBus)],
    ProvidesMany = [typeof(IListenerEndpoint)])]
public sealed class TcpListenerPlugin : IPlugin
{
    private static long _nextConnectionId;

    /// <inheritdoc />
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<TcpListenerOptions>();
        var registry = context.Services.Get<IConnectionProtocolRegistry>();
        var logger = context.Logger;

        if (!TcpListenerOptions.TryParseEndPoint(options.Listen, out var endPoint, out var error))
            throw new ConfigException(context.Row.Id, [$"listen: {error}"]);

        var listener = new Socket(endPoint!.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        // Apply before binding so an unsupported transport policy fails row activation, not the accept loop.
        try { TcpConnection.ConfigureSocket(listener, options); }
        catch { listener.Dispose(); throw; }
        if (endPoint.AddressFamily == AddressFamily.InterNetworkV6)
            listener.DualMode = true;
        listener.Bind(endPoint);
        listener.Listen(options.Backlog);

        var connections = new ConcurrentDictionary<long, TcpConnection>();
        var plugin = new Listener(context, registry, options, logger, listener, connections);
        context.Effect(plugin.Dispose);
        plugin.Start();
        context.Effect(context.Services.RegisterMany<IListenerEndpoint>(
            new BoundEndpoint(context.Row.Id, options.Protocol, (IPEndPoint)listener.LocalEndPoint!)));
        return ValueTask.CompletedTask;
    }

    private sealed record BoundEndpoint(string RowId, string Protocol, IPEndPoint LocalEndPoint) : IListenerEndpoint;

    private sealed class Listener(
        IPluginContext context,
        IConnectionProtocolRegistry registry,
        TcpListenerOptions options,
        Microsoft.Extensions.Logging.ILogger logger,
        Socket listener,
        ConcurrentDictionary<long, TcpConnection> connections) : IDisposable
    {
        private int _disposed;

        public void Start()
        {
            _ = Task.Run(AcceptLoopAsync);
            if (options.IdleTimeoutSeconds > 0)
                _ = Task.Run(IdleWatchdogAsync);
            logger.LogInformation("Row '{RowId}' listening on {Listen} (protocol {Protocol}).", context.Row.Id, options.Listen, options.Protocol);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            try
            {
                listener.Close();
            }
            catch (SocketException) { }
            foreach (var connection in connections.Values)
                _ = connection.CloseAsync("listener unloading");
        }

        private async Task AcceptLoopAsync()
        {
            var lifetime = context.Lifetime;
            while (!lifetime.IsCancellationRequested)
            {
                Socket socket;
                try
                {
                    socket = await listener.AcceptAsync(lifetime).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (SocketException ex)
                {
                    // A transient accept failure - a client RST in the backlog or
                    // a port probe - must not end the loop. Breaking here left the
                    // port bound while nothing was ever accepted again, so every
                    // later client connected and then hung waiting for a greeting
                    // that never came, until the next host restart. Only a
                    // disposed or cancelled listener ends the loop; anything else
                    // logs and continues after a bounded pause.
                    if (_disposed != 0 || lifetime.IsCancellationRequested)
                        break;
                    logger.LogWarning(ex, "Row '{RowId}' accept failed ({ErrorCode}); continuing.",
                        context.Row.Id, ex.SocketErrorCode);
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(1), lifetime).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { break; }
                    continue;
                }

                var id = Interlocked.Increment(ref _nextConnectionId);
                TcpConnection connection;
                try { connection = new TcpConnection(id, socket, options); }
                catch (Exception ex)
                {
                    socket.Dispose();
                    logger.LogError(ex, "Row '{RowId}' could not configure accepted connection {Id}.", context.Row.Id, id);
                    continue;
                }
                if (connections.Count >= options.MaxConnections)
                {
                    logger.LogWarning("Row '{RowId}' rejected connection {Id} from {Remote}: max_connections {Max} reached.",
                        context.Row.Id, id, socket.RemoteEndPoint, options.MaxConnections);
                    await connection.CloseAsync("server full").ConfigureAwait(false);
                    continue;
                }
                connections[id] = connection;
                _ = Task.Run(() => ServeAsync(connection));
            }
        }

        private async Task ServeAsync(TcpConnection connection)
        {
            var reason = "closed";
            try
            {
                await context.Events.SerialAsync(new ConnectionAcceptedEvent(connection, context.Row.Id), context.Lifetime).ConfigureAwait(false);
                var protocol = registry.Resolve(options.Protocol);
                await protocol.RunAsync(connection, context.Lifetime).ConfigureAwait(false);
                reason = connection.CloseReason ?? "protocol ended";
            }
            catch (OperationCanceledException)
            {
                reason = connection.CloseReason ?? "listener stopping";
            }
            catch (Exception ex)
            {
                reason = connection.CloseReason ?? $"error: {ex.Message}";
                logger.LogError(ex, "Row '{RowId}' connection {Id} from {Remote} failed.", context.Row.Id, connection.Id, connection.RemoteEndPoint);
            }
            finally
            {
                await connection.CloseAsync(reason).ConfigureAwait(false);
                connections.TryRemove(connection.Id, out _);
                context.Events.Emit(new ConnectionClosedEvent(connection, reason));
            }
        }

        private async Task IdleWatchdogAsync()
        {
            var timeout = TimeSpan.FromSeconds(options.IdleTimeoutSeconds);
            var period = TimeSpan.FromSeconds(Math.Max(1, Math.Min(30, options.IdleTimeoutSeconds / 4)));
            var lifetime = context.Lifetime;
            while (!lifetime.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(period, lifetime).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                var cutoff = DateTime.UtcNow - timeout;
                foreach (var connection in connections.Values)
                    if (!connection.IsClosed && connection.LastActivityUtc < cutoff)
                    {
                        logger.LogInformation("Row '{RowId}' closing idle connection {Id} from {Remote}.",
                            context.Row.Id, connection.Id, connection.RemoteEndPoint);
                        await connection.CloseAsync("idle timeout").ConfigureAwait(false);
                    }
            }
        }
    }
}
