using BubbleFighter.Fantasy.Tests;
using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Xml.Linq;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Scenario.Tests.Harness;
using BubbleFighter.Fantasy.Wire;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Scenario.Tests;

/// <summary>Proves over real loopback TCP that a shop-row reload does not reset persisted wallet state.</summary>
public sealed class ShopReloadStateScenarioTests_Kr1256
{
    [Fact]
    public async Task WalletBalanceSurvivesAShopRowReload()
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var ct = limit.Token;
        var directory = Path.Combine(Path.GetTempPath(), $"bff-reload-state-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var workbook = XDocument.Load(SyntheticClientTables.ShopItem2);
        XNamespace ss = "urn:schemas-microsoft-com:office:spreadsheet";
        var cashSet = workbook.Descendants(ss + "Worksheet").Single(w => (string?)w.Attribute(ss + "Name") == "StockSet")
            .Descendants(ss + "Row").Single(r => r.Elements(ss + "Cell").First().Value == "4000001");
        cashSet.Elements(ss + "Cell").ElementAt(4).Element(ss + "Data")!.Value = "4500";
        var shopPath = Path.Combine(directory, "shop.xml");
        workbook.Save(shopPath);
        var clientData = TestClientData.RowConfig(TestClientData.Tree(
            "reload-state-shop", ("Config/ShopItem2.xml", await File.ReadAllBytesAsync(shopPath, ct))));
        const ushort request = 827, response = 106;
        var port = FreePort();
        PluginRow Row(string plugin, Dictionary<string, object?>? config = null) =>
            new() { Id = plugin, Plugin = plugin, Source = "reload-state", ClientBuild = new("kr", 1256, 1304), Family = "modern", Config = config };
        var host = new PluginHost(
        [
            Row("core.protocol-registry"), Row("core.session-registry"), Row("core.packet-router"),
            Row("core.message-router"), Row("core.client-profiles"), Row("core.opcode-tables"), Row("core.wire-dialects"),
            Row("persistence.memory"), Row("dialect", TestWireDialects.RowConfig("kr-1256")),
            Row("protocol", new() { ["opcode_table_path"] = Path.Combine(AppContext.BaseDirectory, "opcodes.yaml"), ["bootstrap_status"] = 1 }),
            Row("clientdata", clientData),
            Row("dialect.modern.messages", TestWireDialects.ModernMessagesConfig(new())),
            // The login burst, the main-menu install and the bag senders the lobby needs (packets/builds.yaml).
            Row("dialect.messages", TestWireDialects.ModernMessagesConfig(new()
            {
                ["protocol"] = "kr-1256/game", ["login"] = true, ["main_menu_init"] = true, ["inventory"] = true,
            })),
            Row("lobby", new()
            {
                ["inventory_source"] = "mysql", ["money_source"] = "mysql",
                ["starting_lucci"] = 1000, ["starting_cash"] = 5000,
            }),
            Row("shop", new()
            {
                ["shop_item_client_path"] = "Config/ShopItem2.xml",
                ["starting_lucci"] = 1000, ["starting_cash"] = 5000, ["grant_purchased_items"] = true,
                ["send_shop_catalog"] = true,
            }),
            Row("net.tcp", new() { ["listen"] = $"127.0.0.1:{port}", ["protocol"] = "kr-1256/game" }),
        ], PluginCatalog.Discover([Path.Combine(AppContext.BaseDirectory, "plugins")]), NullLoggerFactory.Instance);
        try
        {
            await host.StartAsync(ct);
            await using var client = await SimClient.ConnectAsync("cash", new DnsEndPoint("127.0.0.1", port),
                TestWireDialects.Get("kr-1256"), ushort.MaxValue, ct);
            var handshake = new byte[96];
            BinaryPrimitives.WriteInt32LittleEndian(handshake, 1256);
            await client.SendAsync(1, handshake, ct, 0);
            await client.WaitAsync(0, 2, TimeSpan.FromSeconds(5), ct);
            var cursor = client.Cursor;
            await client.SendAsync(3, Login(), ct, 0);
            await client.WaitAsync(cursor, 163, TimeSpan.FromSeconds(5), ct);

            // The purchase debits the persisted wallet to 500; the reload must not re-grant the start value.
            cursor = client.Cursor;
            await client.SendAsync(801, new byte[12], ct, 0);
            await client.WaitAsync(cursor, 800, TimeSpan.FromSeconds(5), ct);
            var buy = new byte[8];
            BinaryPrimitives.WriteInt32LittleEndian(buy, 4000001);
            cursor = client.Cursor;
            await client.SendAsync(809, buy, ct, 0);
            await client.WaitAsync(cursor, 816, TimeSpan.FromSeconds(5), ct);

            var reloaded = await host.ReloadAsync("shop", ct).AsTask().WaitAsync(TimeSpan.FromSeconds(10), ct);
            Assert.Contains("shop", reloaded.ReloadedRowIds);

            // The same live connection keeps working and the balance is the post-purchase 500, not 5000.
            var from = client.Cursor;
            await client.SendAsync(request, [], ct, 0);
            var frame = await client.WaitAsync(from, response, TimeSpan.FromSeconds(5), ct);
            var expected = new byte[24];
            BinaryPrimitives.WriteInt32LittleEndian(expected.AsSpan(16), 1000);
            BinaryPrimitives.WriteInt32LittleEndian(expected.AsSpan(20), 500);
            Assert.Equal(expected, frame.Payload);

            // A second purchase now fails: the reloaded row still sees the persisted debit, not a fresh grant.
            cursor = client.Cursor;
            await client.SendAsync(809, buy, ct, 0);
            var refused = await client.WaitAsync(cursor, 816, TimeSpan.FromSeconds(5), ct);
            Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(refused.Payload.AsSpan(4)));
        }
        finally
        {
            await host.ShutdownAsync(CancellationToken.None);
            Directory.Delete(directory, true);
        }
    }

    private static byte[] Login()
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new PacketWriter(buffer);
        writer.WriteWideStringPrefixed("cash");
        writer.WriteWideStringPrefixed(string.Empty); writer.WriteWideStringPrefixed(string.Empty);
        writer.WriteUInt32(0); writer.WriteInt32(0); writer.WriteInt32(0);
        writer.WriteWideStringPrefixed(string.Empty);
        return buffer.WrittenSpan.ToArray();
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
