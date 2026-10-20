using BubbleFighter.Fantasy.Tests;
using System.Buffers;
using System.Buffers.Binary;
using System.Net;
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
/// Two kr-812 clients over real sockets through the mounted legacy binding and the shared lobby row: A asks for B's
/// fighter info by account id (C2S 115) and gets B's nickname and level (S2C 124, result 1); an account that is not
/// logged in gets result 0 with a whole default body, and B is never told.
/// </summary>
public sealed class FighterInfoScenarioTests_Legacy
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
    public async Task RequestForAnotherPlayerIsAnsweredFromTheOnlinePlayersAndAnUnknownOneFails()
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = limit.Token;
        var directory = Path.Combine(Path.GetTempPath(), $"bff-fighter-info-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var tablePath = Path.Combine(directory, "opcodes.yaml");
        var entries = new (int Value, string Direction, string Name)[]
        {
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
            Row("protocol", new() { ["opcode_table_path"] = tablePath, ["bootstrap_status"] = 1 }),
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

    private static byte[] Word(int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return bytes;
    }

}
