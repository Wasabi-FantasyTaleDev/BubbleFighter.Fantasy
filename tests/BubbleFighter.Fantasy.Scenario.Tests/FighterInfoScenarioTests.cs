using BubbleFighter.Fantasy.Tests;
using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Listeners;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Scenario.Tests.Harness;
using BubbleFighter.Fantasy.Wire;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Scenario.Tests;

/// <summary>
/// Asks one connected player for another's fighter info over real sockets on both families: kr-1256 through the
/// mounted modern binding and the shared lobby row (C2S 118 answered by S2C 133), kr-812 through the legacy binding
/// and lobby row (C2S 115 answered by S2C 124). The asker receives the holder's record with result 1, an account
/// nobody holds gets result 0 with a whole default body, and the target is never told.
/// </summary>
public sealed class FighterInfoScenarioTests
{
    private static int _boundPort;

    /// <summary>Reads the listener's allocated endpoint after the socket has been bound.</summary>
    [Plugin("test.fighter-info-endpoint", RequiresMany = [typeof(IListenerEndpoint)])]
    public sealed class ListenerEndpointProbe : IPlugin
    {
        /// <summary>Records the mounted game listener's port for this scenario.</summary>
        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
        {
            _boundPort = context.Services.GetAll<IListenerEndpoint>()
                .Single(endpoint => endpoint.RowId == "net.tcp").LocalEndPoint.Port;
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task Kr1256RequestForAnotherPlayerIsAnsweredFromTheOnlinePlayersAndAnUnknownOneFails()
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
            // The login burst and main-menu install live on the shared message row since the login slice.
            Row("dialect.messages", TestWireDialects.MessagesConfig(new()
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

    /// <summary>
    /// Two kr-812 clients over real sockets through the mounted legacy binding and the shared lobby row: A asks for
    /// B's fighter info by account id (C2S 115) and gets B's nickname and level (S2C 124, result 1); an account that
    /// is not logged in gets result 0 with a whole default body, and B is never told.
    /// </summary>
    [Fact]
    public async Task Kr812RequestForAnotherPlayerIsAnsweredFromTheOnlinePlayersAndAnUnknownOneFails()
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = limit.Token;
        var directory = Path.Combine(Path.GetTempPath(), $"bff-fighter-info-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var tablePath = Path.Combine(directory, "opcodes.yaml");
        var entries = new (int Value, string Direction, string Name)[]
        {
            // The protocol row resolves the bootstrap frames by name, so the table states them.
            (1, "c2s", "gcpFirstConnect"), (2, "s2c", "gspRepGamePatch"),
            (3, "c2s", "gcpReqLogin"), (3, "s2c", "gspRepLogin"), (105, "s2c", "gspUserStat"),
            (115, "c2s", "gcpReqFighterInfo"), (124, "s2c", "gspRepFighterInfo"),
            // The shared login row publishes the bag on the login burst; the row's inventory: true states it.
            (801, "s2c", "gspInventoryUpdateAllInventorySlot"), (803, "s2c", "gspInventoryUpdateInventorySlots"),
        };
        await File.WriteAllTextAsync(tablePath, "profile: kr-812\nopcodes:\n" + string.Concat(entries.Select(e =>
            $"  - value: {e.Value}\n    direction: {e.Direction}\n    name: {e.Name}\n    name_source: label@kr-812\n    evidence: verified\n")), ct);
        var tables = PacketTables.Load(RepositoryPackets.Directory());
        PluginRow Row(string plugin, Dictionary<string, object?>? config = null) =>
            new() { Id = plugin, Plugin = plugin, Source = "fighter-info-scenario", ClientBuild = new("kr", 812, 458), Config = config };
        var host = new PluginHost(
        [
            Row("core.protocol-registry"), Row("core.session-registry"), Row("core.packet-router"),
            Row("core.message-router"), Row("core.client-profiles"), Row("core.opcode-tables"), Row("core.wire-dialects"),
            Row("persistence.memory"), Row("dialect", TestWireDialects.RowConfig("kr-812")),
            Row("protocol", new() { ["opcode_table_path"] = tablePath, ["bootstrap_status"] = 1, ["patch_request_name"] = "gcpFirstConnect" }),
            Row("dialect.messages", new()
            {
                ["protocol"] = "kr-812/game", ["inventory"] = true, ["fighter_info"] = true, ["packets_directory"] = RepositoryPackets.Directory(),
            }),
            Row("lobby", new()
            {
                ["protocol"] = "kr-812/game", ["auto_create_players"] = true, ["online_players"] = true, ["fighter_info"] = true,
            }),
            // Bind once; a released free-port probe can be claimed before activation.
            Row("net.tcp", new() { ["listen"] = "127.0.0.1:0", ["protocol"] = "kr-812/game" }),
            Row("test.fighter-info-endpoint"),
        ], PluginCatalog.Discover([Path.Combine(AppContext.BaseDirectory, "plugins")]), NullLoggerFactory.Instance);
        try
        {
            await host.StartAsync(ct);
            var port = _boundPort;
            Assert.InRange(port, 1, ushort.MaxValue);
            await using var a = await ConnectAsync(port, ct);
            await LoginAsync(a, "askera", ct);
            await using var b = await ConnectAsync(port, ct);
            await LoginAsync(b, "targetb", ct);
            var login = await b.WaitAsync(0, 3, TimeSpan.FromSeconds(10), ct);
            var targetNumber = Convert.ToInt32(PacketCodec.Read(tables, "kr-812", "LoginReply", login.Payload)["user_number"]);
            Assert.NotEqual(0, targetNumber);

            // A asks for B: the answer is result 1 with B's nickname and level in the first block.
            var ca = a.Cursor;
            var cb = b.Cursor;
            await a.SendAsync(115, [.. Word(targetNumber), .. Word(0)], ct, 0);
            var found = await a.WaitAsync(ca, 124, TimeSpan.FromSeconds(10), ct);
            var fields = PacketCodec.Read(tables, "kr-812", "FighterInfoReply", found.Payload);
            Assert.Equal(1, Convert.ToInt32(fields["result"]));
            var fighter = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(fields["fighter"]);
            Assert.Equal("targetb", fighter["nickname"]);
            Assert.Equal(1, Convert.ToInt32(fighter["level"]));
            var partner = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(fields["partner"]);
            Assert.Equal(string.Empty, partner["nickname"]);
            Assert.Equal(4 + (340 + 14) + 340 + 8, found.Payload.Length);

            // An account nobody holds: result 0, a whole constructor-default body naming nobody.
            ca = a.Cursor;
            await a.SendAsync(115, [.. Word(987654), .. Word(0)], ct, 0);
            var missing = await a.WaitAsync(ca, 124, TimeSpan.FromSeconds(10), ct);
            var failure = PacketCodec.Read(tables, "kr-812", "FighterInfoReply", missing.Payload);
            Assert.Equal(0, Convert.ToInt32(failure["result"]));
            Assert.Equal(string.Empty, ((IReadOnlyDictionary<string, object?>)failure["fighter"]!)["nickname"]);
            Assert.Equal(4 + 340 + 340 + 8, missing.Payload.Length);

            // The target is never involved.
            await Task.Delay(300, ct);
            Assert.DoesNotContain(b.Since(cb), frame => frame.Opcode == 124);
        }
        finally
        {
            await host.ShutdownAsync(CancellationToken.None);
            File.Delete(tablePath);
            Directory.Delete(directory);
        }
    }

    private static async Task<SimClient> ConnectAsync(int port, CancellationToken ct)
    {
        var client = await SimClient.ConnectAsync("fighter-info", new DnsEndPoint("127.0.0.1", port),
            TestWireDialects.Get("kr-812"), ushort.MaxValue, ct);
        var handshake = new byte[96];
        BinaryPrimitives.WriteInt32LittleEndian(handshake, 812);
        await client.SendAsync(1, handshake, ct, 0);
        await client.WaitAsync(0, 2, TimeSpan.FromSeconds(5), ct);
        return client;
    }

    private static async Task LoginAsync(SimClient client, string name, CancellationToken ct)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new PacketWriter(buffer);
        writer.WriteWideStringPrefixed(name);
        writer.WriteWideStringPrefixed(string.Empty);
        writer.WriteWideStringPrefixed(string.Empty);
        writer.WriteUInt32(0);
        writer.WriteUInt32(0);
        writer.WriteUInt32(0);
        var cursor = client.Cursor;
        await client.SendAsync(3, buffer.WrittenSpan.ToArray(), ct, 0);
        await client.WaitAsync(cursor, 105, TimeSpan.FromSeconds(10), ct);
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
