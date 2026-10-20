using BubbleFighter.Fantasy.Tests;
using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Scenario.Tests.Harness;
using BubbleFighter.Fantasy.Wire;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Scenario.Tests;

/// <summary>Exercises the kr-1256 family's shared login and main-menu path over TCP after renumbering its opcode table.</summary>
public sealed class LoginRemapScenarioTests_Kr1256
{
    private const int Shift = 3000;
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Preserves the login burst's order and payloads under a shifted table: reply, user state, wallet and room
    /// counters, the empty gate table, the solo party, the empty option sync, the bag family, the kind-1
    /// main-menu install and the first-move-channel notice.
    /// </summary>
    [Fact]
    public async Task RenumberedTablePreservesLoginAndMainMenu()
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var directory = Path.Combine(Path.GetTempPath(), $"bff-login-kr1256-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var original = await LoginBurstAsync(directory, 0, limit.Token);
            var shifted = await LoginBurstAsync(directory, Shift, limit.Token);
            int[] expected =
                [3, 105, 106, 108, 17, 201, 1557, 802, 803, 808, 810, 117, 163];
            Assert.Equal(expected, original.Select(f => (int)f.Opcode));
            Assert.Equal(expected, shifted.Select(f => f.Opcode - Shift));
            for (var i = 0; i < expected.Length; i++)
            {
                Assert.Equal(expected[i] == 3 ? ValidateLoginAndReadTail(original[i].Payload) : original[i].Payload,
                    expected[i] == 3 ? ValidateLoginAndReadTail(shifted[i].Payload) : shifted[i].Payload);
            }
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static async Task<IReadOnlyList<SimFrame>> LoginBurstAsync(
        string directory, int shift, CancellationToken ct)
    {
        var source = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "opcodes.yaml"), ct);
        var tablePath = Path.Combine(directory, $"opcodes-{shift}.yaml");
        // Retain names, directions and connection scopes, including UnknownNNN names, across renumbering.
        var table = Regex.Replace(source, @"(?m)^(\s*- value: )([0-9]+)", match =>
            match.Groups[1].Value + checked((ushort)(int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) + shift))
                .ToString(CultureInfo.InvariantCulture));
        if (shift != 0)
            Assert.NotEqual(source, table);
        await File.WriteAllTextAsync(tablePath, table, ct);
        var port = FreePort();
        ushort Op(int value) => checked((ushort)(value + shift));
        PluginRow Row(string plugin, Dictionary<string, object?>? config = null) =>
            new() { Id = plugin, Plugin = plugin, Source = "login-remap-scenario", ClientBuild = new("kr", 1256, 1304), Family = "modern", Config = config };
        var host = new PluginHost(
        [
            Row("core.protocol-registry"), Row("core.session-registry"), Row("core.packet-router"),
            Row("core.message-router"), Row("core.client-profiles"), Row("core.opcode-tables"), Row("core.wire-dialects"),
            Row("persistence.memory"), Row("dialect", TestWireDialects.RowConfig("kr-1256")),
            Row("protocol", new() { ["opcode_table_path"] = tablePath, ["bootstrap_status"] = 1 }),
            SharedDialectRows.Kr1256(new() { ["inventory"] = true }),
            // A stored wallet proves the binding passes the login's wallet into the S2C 106 words.
            Row("lobby", new()
            {
                ["inventory_source"] = "mysql", ["money_source"] = "mysql",
                ["starting_lucci"] = 12345, ["starting_cash"] = 6789,
            }),
            Row("net.tcp", new() { ["listen"] = $"127.0.0.1:{port}", ["protocol"] = "kr-1256/game" }),
        ], PluginCatalog.Discover([Path.Combine(AppContext.BaseDirectory, "plugins")]), NullLoggerFactory.Instance);
        try
        {
            await host.StartAsync(ct);
            await using var client = await SimClient.ConnectAsync("remap", new DnsEndPoint("127.0.0.1", port),
                TestWireDialects.Get("kr-1256"), ushort.MaxValue, ct);
            var handshake = new byte[96];
            BinaryPrimitives.WriteInt32LittleEndian(handshake, 1256);
            // Protocol bootstrap C2S 1 / S2C 2 precedes table-driven messages and remains wire-fixed.
            await client.SendAsync(1, handshake, ct, 0);
            var patch = await client.WaitAsync(0, 2, ReplyTimeout, ct);
            Assert.Equal(new byte[8], patch.Payload);
            Assert.Single(client.Since(0));

            var cursor = client.Cursor;
            await client.SendAsync(Op(3), LauncherLogin(client.Name), ct, 0);
            await client.WaitAsync(cursor, Op(163), ReplyTimeout, ct);
            // The empty main-menu probe is also table-driven on the game scope; the shared row answers the
            // empty session signature.
            await client.SendAsync(Op(521), [], ct, 0);
            await client.WaitAsync(cursor, Op(521), ReplyTimeout, ct);
            var frames = client.Since(cursor);
            Assert.Equal(7, Assert.Single(frames, f => f.Opcode == Op(521)).Payload.Length);
            var wallet = Assert.Single(frames, f => f.Opcode == Op(106)).Payload;
            Assert.Equal(24, wallet.Length);
            // The binding copies the stored wallet into the two balance words the client keeps.
            Assert.Equal(12345, BinaryPrimitives.ReadInt32LittleEndian(wallet.AsSpan(16)));
            Assert.Equal(6789, BinaryPrimitives.ReadInt32LittleEndian(wallet.AsSpan(20)));
            Assert.True(Assert.Single(frames, f => f.Opcode == Op(802)).Payload.Length > sizeof(uint));
            Assert.True(Assert.Single(frames, f => f.Opcode == Op(803)).Payload.Length > sizeof(uint));
            return frames.Where(frame => frame.Opcode != Op(521)).ToArray();
        }
        finally
        {
            await host.ShutdownAsync(CancellationToken.None);
        }
    }

    private static byte[] LauncherLogin(string account)
    {
        // Launcher serializer sub_10C6A00: three strings, counted i32 vector, two i32 and one string.
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new PacketWriter(buffer);
        writer.WriteWideStringPrefixed(account);
        writer.WriteWideStringPrefixed(string.Empty);
        writer.WriteWideStringPrefixed(string.Empty);
        writer.WriteUInt32(0);
        writer.WriteInt32(0);
        writer.WriteInt32(0);
        writer.WriteWideStringPrefixed(string.Empty);
        return buffer.WrittenSpan.ToArray();
    }

    private static byte[] ValidateLoginAndReadTail(byte[] payload)
    {
        // sub_10C6E80 reads result then a prefixed session id. Only that generated id may differ.
        var reader = new PacketReader(payload);
        Assert.Equal(0, reader.ReadInt32());
        var sessionId = reader.ReadWideStringPrefixed();
        Assert.Matches(@"\Abff-[1-9][0-9]*\z", sessionId);
        Assert.True(long.TryParse(sessionId.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out var id));
        Assert.True(id > 0);
        return payload[reader.Offset..];
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
