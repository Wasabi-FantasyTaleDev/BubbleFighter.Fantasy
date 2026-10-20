using BubbleFighter.Fantasy.Tests;
using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Scenario.Tests.Harness;
using BubbleFighter.Fantasy.Wire;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Scenario.Tests;

/// <summary>
/// Two kr-1256 clients over real sockets through the mounted modern binding and the shared lobby row: A asks for B's
/// fighter info by account id (C2S 118) and gets result 1 with B's record (S2C 133); an account nobody holds gets
/// result 0 and a whole default body, and B is never told.
/// </summary>
public sealed class FighterInfoScenarioTests_Kr1256
{
    [Fact]
    public async Task RequestForAnotherPlayerIsAnsweredFromTheOnlinePlayersAndAnUnknownOneFails()
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = limit.Token;
        var port = FreePort();
        PluginRow Row(string plugin, Dictionary<string, object?>? config = null) =>
            new() { Id = plugin, Plugin = plugin, Source = "fighter-info-scenario", ClientBuild = new("kr", 1256, 1304), Family = "modern", Config = config };
        var host = new PluginHost(
        [
            Row("core.protocol-registry"), Row("core.session-registry"), Row("core.packet-router"),
            Row("core.message-router"), Row("core.client-profiles"), Row("core.opcode-tables"), Row("core.wire-dialects"),
            Row("persistence.memory"), Row("dialect", TestWireDialects.RowConfig("kr-1256")),
            Row("protocol", new() { ["opcode_table_path"] = Path.Combine(AppContext.BaseDirectory, "opcodes.yaml"), ["bootstrap_status"] = 1 }),
            Row("dialect.modern.messages", TestWireDialects.ModernMessagesConfig(new())),
            // The login burst and main-menu install live on the shared message row since the login slice.
            Row("dialect.messages", TestWireDialects.ModernMessagesConfig(new()
            {
                ["protocol"] = "kr-1256/game", ["login"] = true, ["main_menu_init"] = true, ["inventory"] = true,
                ["fighter_info"] = true,
            })),
            Row("lobby", new()
            {
                ["inventory_source"] = "mysql", ["money_source"] = "mysql", ["starting_lucci"] = 1000, ["starting_cash"] = 5000,
                ["online_players"] = true, ["fighter_info"] = true,
            }),
            Row("net.tcp", new() { ["listen"] = $"127.0.0.1:{port}", ["protocol"] = "kr-1256/game" }),
        ], PluginCatalog.Discover([Path.Combine(AppContext.BaseDirectory, "plugins")]), NullLoggerFactory.Instance);
        try
        {
            await host.StartAsync(ct);
            await using var a = await ConnectAsync(port, "askera", ct);
            await using var b = await ConnectAsync(port, "targetb", ct);
            var targetNumber = LoginNumber(await b.WaitAsync(0, 3, TimeSpan.FromSeconds(10), ct));

            // A asks for B: result 1, B's account id and nickname in the first FighterInfo (kr-1256 sub_99A430).
            var ca = a.Cursor;
            var cb = b.Cursor;
            await a.SendAsync(118, [.. Word(targetNumber), .. Word(0)], ct, 0);
            var found = (await a.WaitAsync(ca, 133, TimeSpan.FromSeconds(10), ct)).Payload;
            Assert.Equal(1, Int(found, 0));
            Assert.Equal(targetNumber, Int(found, 4));
            Assert.Equal(7, Int(found, 8));
            Assert.Equal("targetb", Encoding.Unicode.GetString(found, 12, 14));
            // 440 for an empty nickname, plus the nickname at +8 and +504; the partner block is constructor defaults.
            Assert.Equal(4 + (440 + 28) + 440 + 4 + 4 + 16, found.Length);

            // An account nobody holds: result 0 and a whole default body that names nobody.
            ca = a.Cursor;
            await a.SendAsync(118, [.. Word(987654), .. Word(0)], ct, 0);
            var missing = (await a.WaitAsync(ca, 133, TimeSpan.FromSeconds(10), ct)).Payload;
            Assert.Equal(0, Int(missing, 0));
            Assert.Equal(0, Int(missing, 4));
            Assert.Equal(0, Int(missing, 8));
            Assert.Equal(4 + 440 + 440 + 4 + 4 + 16, missing.Length);

            await Task.Delay(300, ct);
            Assert.DoesNotContain(b.Since(cb), frame => frame.Opcode == 133);
        }
        finally
        {
            await host.ShutdownAsync(CancellationToken.None);
        }
    }

    // The login reply: result, sid, account id, nickname (LoginWire.RepLoginSuccess).
    private static int LoginNumber(SimFrame frame)
    {
        var payload = frame.Payload;
        var sid = Int(payload, 4);
        return Int(payload, 8 + sid * 2);
    }

    private static int Int(byte[] payload, int at) => BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(at));

    private static async Task<SimClient> ConnectAsync(int port, string name, CancellationToken ct)
    {
        var client = await SimClient.ConnectAsync(name, new DnsEndPoint("127.0.0.1", port),
            TestWireDialects.Get("kr-1256"), ushort.MaxValue, ct);
        var handshake = new byte[96];
        BinaryPrimitives.WriteInt32LittleEndian(handshake, 1256);
        await client.SendAsync(1, handshake, ct, 0);
        await client.WaitAsync(0, 2, TimeSpan.FromSeconds(5), ct);
        var cursor = client.Cursor;
        await client.SendAsync(3, Login(name), ct, 0);
        await client.WaitAsync(cursor, 163, TimeSpan.FromSeconds(10), ct);
        return client;
    }

    private static byte[] Login(string name)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new PacketWriter(buffer);
        writer.WriteWideStringPrefixed(name);
        writer.WriteWideStringPrefixed(string.Empty); writer.WriteWideStringPrefixed(string.Empty);
        writer.WriteUInt32(0); writer.WriteInt32(0); writer.WriteInt32(0);
        writer.WriteWideStringPrefixed(string.Empty);
        return buffer.WrittenSpan.ToArray();
    }

    private static byte[] Word(int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return bytes;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
