using BubbleFighter.Fantasy.Tests;
using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Scenario.Tests.Harness;
using BubbleFighter.Fantasy.Wire;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Scenario.Tests;

/// <summary>Exercises persistent KR812 native creation followed by shared login state and tutorial offer.</summary>
public sealed class LegacyNativeCreationScenarioTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task CreationWaitsForNativeAnimationThenReconnectKeepsIdentityAndEquipment(int characterType)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = limit.Token;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var root = Directory.GetParent(RepositoryPackets.Directory())!.FullName;
        var host = Host(port, Path.Combine(root, "profiles", "kr-812", "opcodes.yaml"));
        try
        {
            await host.StartAsync(ct);
            int identity;
            await using (var client = await Connect(port, ct))
            {
                var cursor = await Login(client, ct);
                Assert.Empty((await client.WaitAsync(cursor, 12, TimeSpan.FromSeconds(5), ct)).Payload);
                Assert.DoesNotContain(client.Since(cursor), frame => frame.Opcode == 3);
                var name = new ArrayBufferWriter<byte>();
                new PacketWriter(name).WriteWideStringPrefixed("ab");
                await client.SendAsync(15, name.WrittenSpan.ToArray(), ct, 0);
                Assert.Equal(Convert.FromHexString("000000000200000061006200"),
                    (await client.WaitAsync(cursor, 13, TimeSpan.FromSeconds(5), ct)).Payload);
                var create = new ArrayBufferWriter<byte>();
                var writer = new PacketWriter(create);
                writer.WriteWideStringPrefixed("ab");
                writer.WriteInt32(characterType);
                writer.WriteInt32(1);
                writer.WriteWideStringPrefixed("");
                writer.WriteInt32(0);
                Assert.Equal(24, create.WrittenCount);
                await client.SendAsync(16, create.WrittenSpan.ToArray(), ct, 0);
                Assert.Equal(new byte[4], (await client.WaitAsync(cursor, 14, TimeSpan.FromSeconds(5), ct)).Payload);
                Assert.DoesNotContain(client.Since(cursor), frame => frame.Opcode == 3);
                await client.SendAsync(17, [], ct, 0);
                identity = await Accepted(client, cursor, characterType, ct);
            }
            await using var reconnected = await Connect(port, ct);
            var next = await Login(reconnected, ct);
            Assert.Equal(identity, await Accepted(reconnected, next, characterType, ct));
        }
        finally { await host.ShutdownAsync(CancellationToken.None); }
    }

    private static async Task<int> Accepted(SimClient client, int cursor, int characterType, CancellationToken ct)
    {
        var login = await client.WaitAsync(cursor, 3, TimeSpan.FromSeconds(5), ct);
        var stat = await client.WaitAsync(cursor, 105, TimeSpan.FromSeconds(5), ct);
        var inventory = await client.WaitAsync(cursor, 801, TimeSpan.FromSeconds(5), ct);
        var room = await client.WaitAsync(cursor, 110, TimeSpan.FromSeconds(5), ct);
        var menu = await client.WaitAsync(cursor, 108, TimeSpan.FromSeconds(5), ct);
        var move = await client.WaitAsync(cursor, 11, TimeSpan.FromSeconds(5), ct);
        Assert.True(login.Index < stat.Index && stat.Index < inventory.Index && inventory.Index < room.Index);
        Assert.True(room.Index < menu.Index && menu.Index < move.Index);
        Assert.Equal(Convert.FromHexString("010000003200000000000000"), move.Payload);
        var reader = new PacketReader(login.Payload);
        Assert.Equal(0, reader.ReadInt32());
        Assert.Equal("creation-account", reader.ReadWideStringPrefixed());
        var identity = reader.ReadInt32();
        Assert.True(identity > 0);
        Assert.Equal("ab", reader.ReadWideStringPrefixed());
        Assert.Equal(characterType, reader.ReadInt32());
        var info = new PacketReader(stat.Payload.AsSpan(44));
        Assert.Equal("ab", info.ReadWideStringPrefixed());
        // InventorySlot carries item id at +4 in each 44-byte KR812 wire row.
        var count = BinaryPrimitives.ReadInt32LittleEndian(inventory.Payload);
        Assert.Equal(4 + 44 * count, inventory.Payload.Length);
        var rows = Enumerable.Range(0, count).Select(index => (
            Item: BinaryPrimitives.ReadInt32LittleEndian(inventory.Payload.AsSpan(4 + 44 * index)),
            Worn: BinaryPrimitives.ReadInt32LittleEndian(inventory.Payload.AsSpan(20 + 44 * index)) != 0)).ToArray();
        Assert.Contains(rows, row => row.Item == 300000 + characterType && row.Worn);
        Assert.Single(rows, row => row.Item is >= 300001 and <= 300031 && row.Worn);
        return identity;
    }

    private static async Task<SimClient> Connect(int port, CancellationToken ct)
    {
        var client = await SimClient.ConnectAsync("native-creation", new DnsEndPoint("127.0.0.1", port),
            TestWireDialects.Get("kr-812"), ushort.MaxValue, ct);
        var greeting = new byte[96];
        BinaryPrimitives.WriteInt32LittleEndian(greeting, 812);
        await client.SendAsync(1, greeting, ct, 0);
        await client.WaitAsync(0, 2, TimeSpan.FromSeconds(5), ct);
        return client;
    }

    private static async Task<int> Login(SimClient client, CancellationToken ct)
    {
        var bytes = new ArrayBufferWriter<byte>();
        var writer = new PacketWriter(bytes);
        writer.WriteWideStringPrefixed("creation-account");
        writer.WriteWideStringPrefixed("");
        writer.WriteWideStringPrefixed("");
        writer.WriteUInt32(0);
        writer.WriteUInt32(0);
        writer.WriteUInt32(0);
        var cursor = client.Cursor;
        await client.SendAsync(3, bytes.WrittenSpan.ToArray(), ct, 0);
        return cursor;
    }

    private static PluginHost Host(int port, string tablePath)
    {
        PluginRow Row(string plugin, Dictionary<string, object?>? config = null) => new()
        {
            Id = plugin, Plugin = plugin, Source = "native-creation-scenario",
            ClientBuild = new("kr", 812, 458), Config = config,
        };
        return new PluginHost([
            Row("core.protocol-registry"), Row("core.session-registry"), Row("core.packet-router"),
            Row("core.message-router"), Row("core.client-profiles"), Row("core.opcode-tables"), Row("core.wire-dialects"),
            Row("persistence.memory"), Row("dialect", TestWireDialects.RowConfig("kr-812")),
            Row("protocol", new() { ["opcode_table_path"] = tablePath, ["bootstrap_status"] = 1 }),
            Row("dialect.messages", new()
            {
                ["protocol"] = "kr-812/game", ["character_creation"] = true, ["enable_purchases"] = true,
                ["packets_directory"] = RepositoryPackets.Directory(),
            }),
            Row("dialect.rooms", new()
            {
                ["protocol"] = "kr-812/game", ["tutorial_move_in"] = true,
                ["packets_directory"] = RepositoryPackets.Directory(),
            }),
            Row("lobby", new()
            {
                ["protocol"] = "kr-812/game", ["character_creation"] = true, ["auto_create_players"] = false,
                ["inventory_source"] = "mysql", ["money_source"] = "mysql",
                ["starter_item_ids"] = new object[] { 300001, 201001, 202001, 207001 },
                ["starter_worn_ids"] = new object[] { 300001, 201001, 202001, 207001 },
            }),
            Row("ingame", new() { ["typed_only"] = true, ["game_protocol"] = "kr-812/game" }),
            Row("net.tcp", new() { ["listen"] = $"127.0.0.1:{port}", ["protocol"] = "kr-812/game" }),
        ], PluginCatalog.Discover([Path.Combine(AppContext.BaseDirectory, "plugins")]), NullLoggerFactory.Instance);
    }
}
