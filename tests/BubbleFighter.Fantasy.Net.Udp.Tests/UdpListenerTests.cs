using System.Net;
using System.Net.Sockets;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Net.Udp;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Net.Udp.Tests;

public sealed class UdpListenerTests
{
    [Fact]
    public async Task DisabledListenerPublishesNoEndpointAndDoesNotBindItsPort()
    {
        var port = FreePort();
        var host = new PluginHost([
            Row("udp", "net.udp", new() { ["enabled"] = false, ["listen"] = $"127.0.0.1:{port}", ["protocol"] = "test/udp" }),
            Row("observer", "test.udp-disabled-observer"),
        ], PluginCatalog.ScanAssemblies([typeof(UdpListenerPlugin).Assembly, typeof(UdpListenerTests).Assembly]), NullLoggerFactory.Instance);
        try
        {
            await host.StartAsync(default);
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(IPAddress.Loopback, port));
        }
        finally { await host.ShutdownAsync(default); }
    }

    [Plugin("test.udp-disabled-observer", OptionalMany = [typeof(IUdpDatagramEndpoint)])]
    public sealed class DisabledObserver : IPlugin
    {
        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
        {
            Assert.Empty(context.Services.GetAll<IUdpDatagramEndpoint>());
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task ListenerEchoesBoundedDatagramsAndContinuesAfterOversizedInput()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = timeout.Token;
        var port = FreePort();
        var host = new PluginHost(
        [
            Row("udp", "net.udp", new() { ["listen"] = $"127.0.0.1:{port}", ["protocol"] = "test/udp", ["max_datagram_bytes"] = 64 }),
            Row("echo", "test.udp-echo"),
        ],
        PluginCatalog.ScanAssemblies([typeof(UdpListenerPlugin).Assembly, typeof(UdpListenerTests).Assembly]),
        NullLoggerFactory.Instance);

        try
        {
            await host.StartAsync(ct);
            using var client = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            var endpoint = new IPEndPoint(IPAddress.Loopback, port);
            var payload = Enumerable.Range(0, 64).Select(value => (byte)value).ToArray();
            await client.SendToAsync(payload, SocketFlags.None, endpoint, ct);
            Assert.Equal(payload, await ReceiveAsync(client, ct));

            await client.SendToAsync(new byte[65], SocketFlags.None, endpoint, ct);
            using (var noReply = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                noReply.CancelAfter(TimeSpan.FromMilliseconds(200));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReceiveAsync(client, noReply.Token));
            }

            await client.SendToAsync(new byte[] { 9, 8, 7 }, SocketFlags.None, endpoint, ct);
            Assert.Equal(new byte[] { 9, 8, 7 }, await ReceiveAsync(client, ct));
        }
        finally
        {
            await host.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ConsumerRemovalCancelsAndDrainsAnAdmittedCallbackBeforeReleasingItsScope()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var port = FreePort();
        var services = new ServiceRegistry();
        var events = new EventBus(NullLogger.Instance);
        var listener = new PluginContext(Row("udp", "net.udp", new()
        {
            ["listen"] = $"127.0.0.1:{port}", ["protocol"] = "test/udp", ["max_datagram_bytes"] = 64,
        }), services, events, NullLogger.Instance);
        var consumer = new PluginContext(Row("consumer", "test"), services, events, NullLogger.Instance);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await new UdpListenerPlugin().ActivateAsync(listener, timeout.Token);
            var endpoint = Assert.Single(services.GetAll<IUdpDatagramEndpoint>());
            await consumer.AcquireEffectAsync(_ => ValueTask.FromResult<IAsyncDisposable>(endpoint.Register(async (_, _, ct) =>
            {
                using var cancellation = ct.Register(() => cancelled.TrySetResult());
                entered.TrySetResult();
                try { await release.Task.WaitAsync(timeout.Token); }
                finally { exited.TrySetResult(); }
            })), timeout.Token);
            using var client = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            var remote = new IPEndPoint(IPAddress.Loopback, port);
            await client.SendToAsync(new byte[] { 1 }, SocketFlags.None, remote, timeout.Token);
            await entered.Task.WaitAsync(timeout.Token);
            var unload = consumer.UnwindAsync().AsTask();
            await cancelled.Task.WaitAsync(timeout.Token);
            Assert.False(unload.IsCompleted);
            Assert.False(exited.Task.IsCompleted);
            release.TrySetResult();
            await unload.WaitAsync(timeout.Token);
            Assert.True(exited.Task.IsCompletedSuccessfully);
            await using var replacement = endpoint.Register((sender, body, ct) => endpoint.SendAsync(body, sender, ct));
            await client.SendToAsync(new byte[] { 2 }, SocketFlags.None, remote, timeout.Token);
            Assert.Equal(new byte[] { 2 }, await ReceiveAsync(client, timeout.Token));
        }
        finally
        {
            release.TrySetResult();
            await consumer.UnwindAsync();
            await listener.UnwindAsync();
        }
    }

    [Fact]
    public async Task ReplacingAnIdleConsumerRevokesItsPendingReceiveAndShutdownReleasesThePort()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var port = FreePort();
        var context = new PluginContext(Row("udp", "net.udp", new()
        {
            ["listen"] = $"127.0.0.1:{port}", ["protocol"] = "test/udp", ["max_datagram_bytes"] = 64,
        }), new ServiceRegistry(), new EventBus(NullLogger.Instance), NullLogger.Instance);
        try
        {
            await new UdpListenerPlugin().ActivateAsync(context, timeout.Token);
            var endpoint = Assert.Single(context.Services.GetAll<IUdpDatagramEndpoint>());
            using var client = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            var remote = new IPEndPoint(IPAddress.Loopback, port);
            var firstCalls = 0;
            var first = endpoint.Register((sender, payload, ct) =>
            {
                Interlocked.Increment(ref firstCalls);
                return endpoint.SendAsync(payload, sender, ct);
            });
            await client.SendToAsync(new byte[] { 1 }, SocketFlags.None, remote, timeout.Token);
            Assert.Equal(new byte[] { 1 }, await ReceiveAsync(client, timeout.Token));
            first.Dispose();
            using var second = endpoint.Register((sender, payload, ct) => endpoint.SendAsync(payload, sender, ct));
            await client.SendToAsync(new byte[] { 2 }, SocketFlags.None, remote, timeout.Token);
            Assert.Equal(new byte[] { 2 }, await ReceiveAsync(client, timeout.Token));
            Assert.Equal(1, Volatile.Read(ref firstCalls));
            await context.UnwindAsync();
            Assert.Throws<ObjectDisposedException>(() => endpoint.Register((_, _, _) => ValueTask.CompletedTask));
            using var rebound = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            rebound.Bind(remote);
        }
        finally { await context.UnwindAsync(); }
    }

    private static async Task<byte[]> ReceiveAsync(Socket socket, CancellationToken ct)
    {
        var buffer = new byte[128];
        var result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), ct);
        return buffer.AsSpan(0, result.ReceivedBytes).ToArray();
    }

    private static int FreePort()
    {
        for (var port = 7500; port < 7700; port++)
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try { socket.Bind(new IPEndPoint(IPAddress.Loopback, port)); return port; }
            catch (SocketException) { }
        }
        throw new InvalidOperationException("No isolated UDP test port available.");
    }

    private static PluginRow Row(string id, string plugin, Dictionary<string, object?>? config = null) =>
        new() { Id = id, Plugin = plugin, Source = "udp-test", Config = config };

    [Plugin("test.udp-echo", RequiresMany = [typeof(IUdpDatagramEndpoint)])]
    public sealed class EchoPlugin : IPlugin
    {
        /// <inheritdoc />
        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
        {
            var endpoint = Assert.Single(context.Services.GetAll<IUdpDatagramEndpoint>());
            context.Effect(endpoint.Register((remote, payload, ct) => endpoint.SendAsync(payload, remote, ct)));
            return ValueTask.CompletedTask;
        }
    }
}
