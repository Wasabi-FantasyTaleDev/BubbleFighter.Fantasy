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
