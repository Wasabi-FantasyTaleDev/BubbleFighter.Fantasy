using BubbleFighter.Fantasy.Tests;
using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Scenario.Tests.Harness;
using BubbleFighter.Fantasy.Wire;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Scenario.Tests;

/// <summary>Proves the kr-1256 balance refresh (C2S 827, S2C 106 wallet) answers before and after a cash
/// purchase without resetting state, on the native and a renumbered opcode table.</summary>
public sealed class ShopBalanceScenarioTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Kr1256BalanceRefreshRepliesBeforeAndAfterCashPurchaseWithoutResettingState(bool remap)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var ct = limit.Token;
        var directory = TestTempDirectories.ForTest("bff-cash-refresh");
        var source = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "opcodes.yaml"), ct);
        // Renumber the two migrated directions independently. Other shop codecs have their own migration.
        var table = remap ? Regex.Replace(Regex.Replace(source,
            @"(?m)(- value: )827(\r?\n\s*direction: c2s)", "${1}3827${2}"),
            @"(?m)(- value: )106(\r?\n\s*direction: s2c)", "${1}6106${2}") : source;
        if (remap) Assert.NotEqual(source, table);
        var tablePath = Path.Combine(directory, "opcodes.yaml");
        await File.WriteAllTextAsync(tablePath, table, ct);
        // The generic catalog fixture is for display. Purchases use sellPrice in the selected currency.
        var workbook = XDocument.Load(SyntheticClientTables.ShopItem2);
        XNamespace ss = "urn:schemas-microsoft-com:office:spreadsheet";
        var cashSet = workbook.Descendants(ss + "Worksheet").Single(w => (string?)w.Attribute(ss + "Name") == "StockSet")
            .Descendants(ss + "Row").Single(r => r.Elements(ss + "Cell").First().Value == "4000001");
        cashSet.Elements(ss + "Cell").ElementAt(4).Element(ss + "Data")!.Value = "4500";
        var shopPath = Path.Combine(directory, "shop.xml");
        workbook.Save(shopPath);
        var clientData = TestClientData.RowConfig(TestClientData.Tree(
            "cash-refresh-shop-" + remap, ("Config/ShopItem2.xml", await File.ReadAllBytesAsync(shopPath, ct))));
        ushort request = remap ? (ushort)3827 : (ushort)827;
        ushort response = remap ? (ushort)6106 : (ushort)106;
        var port = FreePort();
        PluginRow Row(string plugin, Dictionary<string, object?>? config = null) =>
            new() { Id = plugin, Plugin = plugin, Source = "cash-refresh", ClientBuild = new("kr", 1256, 1304), Family = "modern", Config = config };
        var host = new PluginHost(
        [
            Row("core.protocol-registry"), Row("core.session-registry"), Row("core.packet-router"),
            Row("core.message-router"), Row("core.client-profiles"), Row("core.opcode-tables"), Row("core.wire-dialects"),
            Row("persistence.memory"), Row("dialect", TestWireDialects.RowConfig("kr-1256")),
            Row("protocol", new() { ["opcode_table_path"] = tablePath, ["bootstrap_status"] = 1 }),
            Row("clientdata", clientData),
            // No second message row: the balance route (C2S 827 -> S2C 106) mounts from the
            // shared row's shop catalog binding alone, which this scenario proves over real sockets.
            // The login burst, the main-menu install and the bag senders the lobby needs (packets/builds.yaml).
            Row("dialect.messages", TestWireDialects.MessagesConfig(new()
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
            await CheckAsync(5000);
            await CheckAsync(5000);

            cursor = client.Cursor;
            await client.SendAsync(801, new byte[12], ct, 0);
            await client.WaitAsync(cursor, 800, TimeSpan.FromSeconds(5), ct);
            var buy = new byte[8];
            BinaryPrimitives.WriteInt32LittleEndian(buy, 4000001);
            cursor = client.Cursor;
            await client.SendAsync(809, buy, ct, 0);
            await client.WaitAsync(cursor, 816, TimeSpan.FromSeconds(5), ct);
            var purchased = client.Since(cursor);
            // The wallet push after a purchase is gspUserMoneyStat from the table, so it follows a remap too.
            Assert.Equal(new ushort[] { 802, 803, response, 816 }, purchased.Select(f => f.Opcode));
            Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(purchased[^1].Payload.AsSpan(4)));
            await CheckAsync(500);
            await CheckAsync(500);

            cursor = client.Cursor;
            await client.SendAsync(809, buy, ct, 0);
            var refused = await client.WaitAsync(cursor, 816, TimeSpan.FromSeconds(5), ct);
            Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(refused.Payload.AsSpan(4)));
            Assert.Single(client.Since(cursor));
            await CheckAsync(500);

            // A nonempty balance request must close the session, without publishing a wallet.
            cursor = client.Cursor;
            await client.SendAsync(request, new byte[1], ct, 0);
            await Assert.ThrowsAsync<IOException>(() =>
                client.WaitAsync(cursor, response, TimeSpan.FromSeconds(2), ct));
            Assert.Empty(client.Since(cursor));

            async Task CheckAsync(int cash)
            {
                var from = client.Cursor;
                await client.SendAsync(request, [], ct, 0);
                var frame = await client.WaitAsync(from, response, TimeSpan.FromSeconds(5), ct);
                await client.SendAsync(521, [], ct, 0);
                await client.WaitAsync(from, 521, TimeSpan.FromSeconds(5), ct);
                Assert.Equal(new ushort[] { response, 521 }, client.Since(from).Select(f => f.Opcode));
                var expected = new byte[24];
                BinaryPrimitives.WriteInt32LittleEndian(expected.AsSpan(16), 1000);
                BinaryPrimitives.WriteInt32LittleEndian(expected.AsSpan(20), cash);
                Assert.Equal(expected, frame.Payload);
            }
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
