using BubbleFighter.Fantasy.Tests;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Wire;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Architecture.Tests;

public sealed class MinimalProfileTests
{
    [Fact]
    public async Task DirectoryLoadedTypedLobbyRepliesPreserveContinuousWireAcrossAdapterReload()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var port = FreePort();
        var rows = Rows(port);
        rows.AddRange([
            PluginBoundaryTests.Row("lobby", "lobby"),
            // The main-menu probe and the login bindings live on the shared message row since the login slice;
            // this scenario keeps the same row id so the reload/unload checks keep their shape.
            PluginBoundaryTests.Row("messages", "dialect.messages", new()
            {
                ["protocol"] = "kr-1256/game",
                ["login"] = true,
                ["main_menu_init"] = true,
                ["inventory"] = true,
                ["packets_directory"] = SharedDialectRows.PacketsDirectory(),
            }),
            PluginBoundaryTests.Row("core.message-router", "core.message-router"),
        ]);
        var host = new PluginHost(rows, PluginBoundaryTests.Discover(), NullLoggerFactory.Instance);
        try
        {
            await host.StartAsync(timeout.Token);
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            var stream = client.GetStream();
            var greeting = new byte[BootstrapGreeting.Size];
            await stream.ReadExactlyAsync(greeting, timeout.Token);
            Assert.Equal(BootstrapGreeting.Build(4, ""), greeting);
            var inbound = SimpleCipher.ClientToServer();
            var outbound = SimpleCipher.ServerToClient();
            var patch = new byte[96];
            BinaryPrimitives.WriteInt32LittleEndian(patch, 1256);
            await SendAsync(stream, inbound, 1, patch, timeout.Token);
            await AssertPatchReplyAsync(stream, outbound, timeout.Token);
            foreach (var payload in new byte[][] { [], [1, 2, 3], [] })
            {
                await SendAsync(stream, inbound, 521, payload, timeout.Token);
                await AssertSignatureReplyAsync(stream, outbound, timeout.Token);
            }
            var blocked = await Assert.ThrowsAsync<InvalidOperationException>(() => host.UnloadAsync("messages", timeout.Token).AsTask());
            Assert.Contains("lobby", blocked.Message);
            Assert.Equal(new[] { "messages", "lobby" }, (await host.ReloadAsync("messages", timeout.Token)).ReloadedRowIds);
            await SendAsync(stream, inbound, 521, [], timeout.Token);
            await AssertSignatureReplyAsync(stream, outbound, timeout.Token);
            await host.UnloadAsync("lobby", timeout.Token);
            await host.UnloadAsync("messages", timeout.Token);
            var missing = await Assert.ThrowsAsync<InvalidOperationException>(() => host.LoadAsync("lobby", timeout.Token).AsTask());
            Assert.Contains("IInboundMessageBinding", missing.Message);
            await host.LoadAsync("messages", timeout.Token);
            await host.LoadAsync("lobby", timeout.Token);
            await SendAsync(stream, inbound, 521, [], timeout.Token);
            await AssertSignatureReplyAsync(stream, outbound, timeout.Token);
        }
        finally { await host.ShutdownAsync(CancellationToken.None); }
    }

    private static async Task AssertSignatureReplyAsync(NetworkStream stream, SimpleCipher cipher, CancellationToken ct)
    {
        var expected = PacketFrame_8Bytes.Encode(521, new byte[7]);
        var frame = new byte[expected.Length];
        await stream.ReadExactlyAsync(frame, ct);
        cipher.DecryptInPlace(frame);
        Assert.Equal(expected, frame);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectoryLoadedTransportAndProtocolWorkWithoutGameFeatures(bool reverse)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var port = FreePort();
        var rows = Rows(port);
        if (reverse) rows.Reverse();
        var host = new PluginHost(rows, PluginBoundaryTests.Discover(), NullLoggerFactory.Instance);
        try
        {
            await host.StartAsync(timeout.Token);
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            var stream = client.GetStream();
            var greeting = new byte[BootstrapGreeting.Size];
            await stream.ReadExactlyAsync(greeting, timeout.Token);
            Assert.Equal(BootstrapGreeting.Build(4, ""), greeting);
            var inbound = SimpleCipher.ClientToServer();
            var outbound = SimpleCipher.ServerToClient();
            var patch = new byte[96];
            BinaryPrimitives.WriteInt32LittleEndian(patch, 1256);
            await SendAsync(stream, inbound, 1, patch, timeout.Token);
            await AssertPatchReplyAsync(stream, outbound, timeout.Token);
            var blocked = await Assert.ThrowsAsync<InvalidOperationException>(() => host.UnloadAsync("dialect", timeout.Token).AsTask());
            Assert.Contains("core.wire-dialects", blocked.Message);
            var reload = await host.ReloadAsync("dialect", timeout.Token);
            Assert.Equal(new[] { "dialect", "core.wire-dialects", "protocol" }, reload.ReloadedRowIds);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], timeout.Token));
            using var replacement = new TcpClient();
            await replacement.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            var newStream = replacement.GetStream();
            await newStream.ReadExactlyAsync(greeting, timeout.Token);
            Assert.Equal(BootstrapGreeting.Build(4, ""), greeting);
            await SendAsync(newStream, SimpleCipher.ClientToServer(), 1, patch, timeout.Token);
            await AssertPatchReplyAsync(newStream, SimpleCipher.ServerToClient(), timeout.Token);
            // An unknown opcode must not break the stream or acquire a game dependency.
            var continuous = SimpleCipher.ClientToServer();
            var newOutbound = SimpleCipher.ServerToClient();
            using var unknownClient = new TcpClient();
            await unknownClient.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            var unknownStream = unknownClient.GetStream();
            await unknownStream.ReadExactlyAsync(greeting, timeout.Token);
            await SendAsync(unknownStream, continuous, 1, patch, timeout.Token);
            await AssertPatchReplyAsync(unknownStream, newOutbound, timeout.Token);
            await SendAsync(unknownStream, continuous, ushort.MaxValue, [], timeout.Token);
            await SendAsync(unknownStream, continuous, 1, patch, timeout.Token);
            await AssertPatchReplyAsync(unknownStream, newOutbound, timeout.Token);
        }
        finally
        {
            await host.ShutdownAsync(CancellationToken.None);
        }
        Assert.All(host.Instances, row => Assert.Equal(PluginState.Unloaded, row.State));
    }

    [Theory]
    [InlineData("core.protocol-registry", "IConnectionProtocolRegistry")]
    [InlineData("core.packet-router", "IPacketRouter")]
    [InlineData("core.client-profiles", "IClientProfileRegistry")]
    [InlineData("core.opcode-tables", "IOpcodeTableRegistry")]
    [InlineData("core.wire-dialects", "IWireDialectRegistry")]
    [InlineData("dialect", "No wire dialect registered")]
    [InlineData("core.session-registry", null)]
    [InlineData("game", null)]
    public async Task RemovingMinimalRowsEitherStartsOrNamesDeclaredDependency(string removed, string? missing)
    {
        var rows = Rows(FreePort()).Where(row => row.Id != removed).ToArray();
        var host = new PluginHost(rows, PluginBoundaryTests.Discover(), NullLoggerFactory.Instance);
        try
        {
            if (missing is null)
                await host.StartAsync(CancellationToken.None);
            else
            {
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync(CancellationToken.None).AsTask());
                Assert.Contains("protocol", error.Message);
                Assert.Contains(missing, error.Message);
            }
        }
        finally
        {
            await host.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ResourceMismatchFailsWithoutFallingBackToExecutableMajor()
    {
        var rows = Rows(FreePort()).Where(row => row.Id != "game").Select(row =>
            row.Id == "protocol" ? row with { ClientBuild = new("kr", 1256, 9999) } : row).ToArray();
        var host = new PluginHost(rows, PluginBoundaryTests.Discover(), NullLoggerFactory.Instance);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync(CancellationToken.None).AsTask());
        Assert.Contains("kr:1256:9999", error.Message);
        Assert.DoesNotContain(host.Instances, row => row.State == PluginState.Active);
    }

    internal static List<PluginRow> Rows(int port) => [
        PluginBoundaryTests.Row("core.protocol-registry", "core.protocol-registry"),
        PluginBoundaryTests.Row("core.session-registry", "core.session-registry"),
        PluginBoundaryTests.Row("core.packet-router", "core.packet-router"),
        PluginBoundaryTests.Row("core.client-profiles", "core.client-profiles"),
        PluginBoundaryTests.Row("core.opcode-tables", "core.opcode-tables"),
        PluginBoundaryTests.Row("core.wire-dialects", "core.wire-dialects"),
        PluginBoundaryTests.Row("dialect", "dialect", TestWireDialects.RowConfig("kr-1256")),
        PluginBoundaryTests.Row("protocol", "protocol", new() { ["opcode_table_path"] = Path.Combine(AppContext.BaseDirectory, "opcodes.yaml") }),
        PluginBoundaryTests.Row("game", "net.tcp", new() { ["listen"] = $"127.0.0.1:{port}", ["protocol"] = "kr-1256/game" }),
    ];

    internal static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    internal static async Task SendAsync(NetworkStream stream, SimpleCipher cipher, ushort opcode, byte[] payload, CancellationToken ct)
    {
        var frame = PacketFrame_8Bytes.Encode(opcode, payload);
        cipher.EncryptInPlace(frame);
        await stream.WriteAsync(frame, ct);
    }

    private static async Task AssertPatchReplyAsync(NetworkStream stream, SimpleCipher cipher, CancellationToken ct)
    {
        var frame = new byte[16];
        await stream.ReadExactlyAsync(frame, ct);
        cipher.DecryptInPlace(frame);
        Assert.Equal(Convert.FromHexString("080000000200000A0000000000000000"), frame);
    }
}
