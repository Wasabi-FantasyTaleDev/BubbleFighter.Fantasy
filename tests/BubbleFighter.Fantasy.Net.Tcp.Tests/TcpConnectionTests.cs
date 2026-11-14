using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.ComponentModel.DataAnnotations;
using BubbleFighter.Fantasy.Net.Tcp;
using Xunit;

namespace BubbleFighter.Fantasy.Net.Tcp.Tests;

/// <summary>Tests for <see cref="TcpConnection"/> inbound activity tracking.</summary>
public sealed class TcpConnectionTests
{
    /// <summary>The listener policy reaches actual accepted socket options, including explicit opt-out.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AcceptedSocketUsesConfiguredKeepAlive(bool enabled)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var accept = listener.AcceptSocketAsync();
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        var socket = await accept;
        var options = new TcpListenerOptions
        {
            KeepAlive = enabled, KeepAliveTimeSeconds = 23,
            KeepAliveIntervalSeconds = 7, KeepAliveRetryCount = 3,
        };
        var connection = new TcpConnection(2, socket, options);
        try
        {
            Assert.Equal(enabled ? 1 : 0, (int)socket.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive)!);
            if (enabled)
            {
                Assert.Equal(23, (int)socket.GetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime)!);
                Assert.Equal(7, (int)socket.GetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval)!);
                Assert.Equal(3, (int)socket.GetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount)!);
            }
            await client.SendAsync(new byte[] { 7 });
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var received = await connection.Input.ReadAsync(deadline.Token);
            Assert.Equal(1, received.Buffer.Length);
            connection.Input.AdvanceTo(received.Buffer.End);
        }
        finally { await connection.CloseAsync("test complete"); }
    }

    /// <summary>Invalid probe timing and retry limits are rejected by the row's validation mechanism.</summary>
    [Theory]
    [InlineData(0, 15, 4)]
    [InlineData(86401, 15, 4)]
    [InlineData(60, 0, 4)]
    [InlineData(60, 86401, 4)]
    [InlineData(60, 15, 0)]
    [InlineData(60, 15, 256)]
    public void InvalidKeepAlivePolicyFailsValidation(int seconds, int interval, int retries)
    {
        var options = new TcpListenerOptions
        {
            Protocol = "test/game", KeepAliveTimeSeconds = seconds,
            KeepAliveIntervalSeconds = interval, KeepAliveRetryCount = retries,
        };
        Assert.False(Validator.TryValidateObject(options, new ValidationContext(options), [], validateAllProperties: true));
    }

    private static async Task<(Socket Client, Socket Server)> ConnectAsync()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var accept = listener.AcceptSocketAsync();
        var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        return (client, await accept);
    }

    /// <summary>
    /// Bytes nobody asked for stay in the operating system's socket buffer, where the peer's own send window
    /// bounds them. The connection must not pull them into a buffer of its own ahead of the protocol.
    /// </summary>
    [Fact]
    public async Task BytesStayInTheSocketUntilTheProtocolReads()
    {
        var (client, server) = await ConnectAsync();
        using var _ = client;
        var connection = new TcpConnection(3, server, noDelay: true);
        try
        {
            await client.SendAsync(new byte[] { 1, 2, 3 });
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (server.Available != 3 && DateTime.UtcNow < deadline)
                await Task.Delay(20);
            await Task.Delay(100);
            Assert.Equal(3, server.Available);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var result = await connection.Input.ReadAsync(timeout.Token);
            Assert.Equal(new byte[] { 1, 2, 3 }, result.Buffer.ToArray());
            connection.Input.AdvanceTo(result.Buffer.End);
        }
        finally { await connection.CloseAsync("test complete"); }
    }

    /// <summary>A megabyte sent in uneven pieces arrives complete and in order.</summary>
    [Fact]
    public async Task ALargeTransferArrivesCompleteAndInOrder()
    {
        var (client, server) = await ConnectAsync();
        using var _ = client;
        var connection = new TcpConnection(4, server, noDelay: true);
        try
        {
            var sent = new byte[1024 * 1024];
            new Random(20261111).NextBytes(sent);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var sending = Task.Run(async () =>
            {
                for (var offset = 0; offset < sent.Length;)
                {
                    var count = Math.Min(sent.Length - offset, 1 + offset % 7919);
                    offset += await client.SendAsync(sent.AsMemory(offset, count), timeout.Token);
                }
            });
            var received = new byte[sent.Length];
            for (var filled = 0; filled < received.Length;)
            {
                var result = await connection.Input.ReadAsync(timeout.Token);
                Assert.False(result.IsCompleted && result.Buffer.IsEmpty, $"the stream ended after {filled} bytes");
                result.Buffer.CopyTo(received.AsSpan(filled));
                filled += (int)result.Buffer.Length;
                connection.Input.AdvanceTo(result.Buffer.End);
            }
            await sending;
            Assert.True(sent.AsSpan().SequenceEqual(received));
        }
        finally { await connection.CloseAsync("test complete"); }
    }

    /// <summary>The peer closing its side completes the input after the bytes it sent.</summary>
    [Fact]
    public async Task PeerCloseCompletesTheInputAfterItsLastBytes()
    {
        var (client, server) = await ConnectAsync();
        var connection = new TcpConnection(5, server, noDelay: true);
        try
        {
            await client.SendAsync(new byte[] { 9, 8 });
            client.Shutdown(SocketShutdown.Send);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var seen = 0;
            while (true)
            {
                var result = await connection.Input.ReadAsync(timeout.Token);
                seen += (int)result.Buffer.Length;
                connection.Input.AdvanceTo(result.Buffer.End);
                if (result.IsCompleted)
                    break;
            }
            Assert.Equal(2, seen);
        }
        finally
        {
            await connection.CloseAsync("test complete");
            client.Dispose();
        }
    }

    /// <summary>Closing the connection releases a read that is waiting for the peer.</summary>
    [Fact]
    public async Task ClosingReleasesAPendingRead()
    {
        var (client, server) = await ConnectAsync();
        using var _ = client;
        var connection = new TcpConnection(6, server, noDelay: true);
        var pending = connection.Input.ReadAsync().AsTask();
        await Task.Delay(100);
        Assert.False(pending.IsCompleted);

        await connection.CloseAsync("test close");
        var finished = await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(pending, finished);
        // Cancelled, completed or failed are all acceptable ends; waiting forever is not.
        try
        {
            var result = await pending;
            Assert.True(result.IsCanceled || result.IsCompleted);
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException)
        {
        }
        Assert.Equal("test close", connection.CloseReason);
    }

    [Fact]
    public async Task InboundBytesAdvanceLastActivityUtc()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var accept = listener.AcceptSocketAsync();
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(IPAddress.Loopback, port);
        var serverSocket = await accept;

        var connection = new TcpConnection(1, serverSocket, noDelay: true);
        try
        {
            var before = connection.LastActivityUtc;
            await Task.Delay(120);
            await client.SendAsync(new byte[] { 1, 2, 3 });

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var result = await connection.Input.ReadAsync(timeout.Token);
            Assert.True(result.Buffer.Length >= 3);
            connection.Input.AdvanceTo(result.Buffer.End);

            Assert.True(connection.LastActivityUtc > before,
                $"expected activity after {before:o}, observed {connection.LastActivityUtc:o}");
        }
        finally
        {
            await connection.CloseAsync("test complete");
        }
    }
}
