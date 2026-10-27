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

/// <summary>Drives one legacy-catalog purchase per legacy profile (kr-812, cn-717, id-400, cn-324, cn-279 and
/// their remapped-opcode variants) through the real rows over TCP: the item renews exactly once, the wallet is
/// charged once, and the grant survives a reconnect.</summary>
public sealed class ShopPurchaseScenarioTests
{
    [Theory]
    [InlineData("kr", 812, 12, 380, 44, 825, false)]
    [InlineData("kr", 812, 12, 380, 44, 825, true)]
    [InlineData("cn", 717, 12, 380, 44, 825, false)]
    [InlineData("cn", 717, 12, 380, 44, 825, true)]
    [InlineData("id", 400, 8, 340, 44, 825, false)]
    [InlineData("id", 400, 8, 340, 44, 825, true)]
    [InlineData("cn", 324, 0, 256, 36, 826, false)]
    [InlineData("cn", 324, 0, 256, 36, 826, true)]
    [InlineData("cn", 279, 0, 168, 36, 826, false)]
    [InlineData("cn", 279, 0, 168, 36, 826, true)]
    public async Task PurchaseRenewsOneItemChargesOnceAndSurvivesReconnect(
        string region, int build, int shopRequestLength, int statLength, int slotLength, int cashCheck, bool remap)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var ct = limit.Token;
        var profile = $"{region}-{build}";
        var directory = TestTempDirectories.ForTest("bff-purchase");
        var tablePath = Path.Combine(directory, "opcodes.yaml");
        var shopPath = Path.Combine(directory, "shop.xml");
        WriteWorkbook(shopPath, build);
        var clientData = TestClientData.RowConfig(TestClientData.Tree(
            $"purchase-shop-{profile}-{remap}", ("Config/ShopItem2.xml", await File.ReadAllBytesAsync(shopPath, ct))));
        ushort Op(int value) => checked((ushort)(value + (remap ? 1000 : 0)));
        var entries = new (int Value, string Direction, string Name)[]
        {
            (3, "c2s", "gcpReqLogin"), (3, "s2c", "gspRepLogin"),
            (106, "c2s", "gcpReqGotoLobby"), (118, "s2c", "gspGotoLobby"),
            (801, "c2s", "gcpEnterShop"), (800, "s2c", "gspRepEnterShop"),
            (802, "c2s", "gcpEnterInventory"), (808, "c2s", "gcpReqShopBuy"),
            (cashCheck, "c2s", "gcpReqCheckCash"),
            (801, "s2c", "gspInventoryUpdateAllInventorySlot"),
            (802, "s2c", "gspInventoryUpdateInventorySlots"),
            (808, "s2c", "gspRepInventoryEquip"), (809, "s2c", "gspRepInventoryUnequip"),
            (105, "s2c", "gspUserStat"), (813, "s2c", "gspRepShopBuy"),
        };
        // The protocol row resolves the bootstrap frames by name; they stay at the wire-fixed 1 and 2
        // even when the remap case shifts every numbered message.
        var handshake = $"  - value: 1\n    direction: c2s\n    name: gcpFirstConnect\n    name_source: label@{profile}\n    evidence: verified\n"
            + $"  - value: 2\n    direction: s2c\n    name: gspRepGamePatch\n    name_source: label@{profile}\n    evidence: verified\n";
        await File.WriteAllTextAsync(tablePath, $"profile: {profile}\nopcodes:\n" + handshake + string.Concat(entries.Where(e => e.Value != 0).Select(e =>
            $"  - value: {Op(e.Value)}\n    direction: {e.Direction}\n    name: {e.Name}\n    name_source: label@{profile}\n    evidence: verified\n")), ct);
        var port = FreePort();
        PluginRow Row(string plugin, Dictionary<string, object?>? config = null) =>
            new() { Id = plugin, Plugin = plugin, Source = "purchase-scenario", ClientBuild = new(region, build, build switch { 812 => 458, 400 => 113, _ => 0 }), Config = config,
                Lineage = profile == "cn-717" ? [profile, "kr-812"] : [profile] };
        var host = new PluginHost(
        [
            Row("core.protocol-registry"), Row("core.session-registry"), Row("core.packet-router"),
            Row("core.message-router"), Row("core.client-profiles"), Row("core.opcode-tables"), Row("core.wire-dialects"),
            Row("persistence.memory"), Row("dialect", TestWireDialects.RowConfig(profile)),
            Row("protocol", new() { ["opcode_table_path"] = tablePath, ["bootstrap_status"] = 1, ["patch_request_name"] = "gcpFirstConnect" }),
            Row("clientdata", clientData),
            Row("dialect.messages", new() { ["protocol"] = profile + "/game", ["enable_purchases"] = true, ["packets_directory"] = RepositoryPackets.Directory() }),
            Row("lobby", new() { ["protocol"] = profile + "/game", ["auto_create_players"] = true }),
            Row("shop", new()
            {
                ["protocol"] = profile + "/game", ["client_profile"] = profile, ["version_major"] = build,
                ["catalog_schema"] = "legacy", ["packets_directory"] = RepositoryPackets.Directory(), ["catalog_display_column"] = "displayName",
                ["shop_item_client_path"] = "Config/ShopItem2.xml", ["send_shop_catalog"] = true, ["grant_purchased_items"] = true,
                ["starting_lucci"] = 2500, ["starting_cash"] = 5000,
                // The purchase rules profiles/cn-324 and profiles/cn-279 state on their shop rows.
                ["reject_repeat_function_10001"] = build is 324 or 279 ? "false" : "true",
                ["permanent_day_stock_types"] = build == 279 ? new List<object?> { "9", "1" } : new List<object?> { "9" },
            }),
            Row("net.tcp", new() { ["listen"] = $"127.0.0.1:{port}", ["protocol"] = profile + "/game" }),
        ], PluginCatalog.Discover([Path.Combine(AppContext.BaseDirectory, "plugins")]), NullLoggerFactory.Instance);
        try
        {
            await host.StartAsync(ct);
            await using var client = await LoginAsync(port, Op(3), Op(105), profile, build, ct);
            await CheckBalanceAsync(client, cashCheck == 0 ? (ushort)0 : Op(cashCheck), Op(105), statLength, 2500, 5000, ct);
            var cursor = client.Cursor;
            await client.SendAsync(Op(808), Buy(1000942, 0), ct, 0);
            await client.WaitAsync(cursor, Op(813), TimeSpan.FromSeconds(5), ct);
            Assert.Equal(0, Int(client.Since(cursor).Single().Payload, 4));

            cursor = client.Cursor;
            await client.SendAsync(Op(801), new byte[shopRequestLength], ct, 0);
            await client.WaitAsync(cursor, Op(800), TimeSpan.FromSeconds(5), ct);
            Assert.Equal(new[] { Op(801), Op(105), Op(800) }, client.Since(cursor).Select(f => f.Opcode));
            Assert.Equal(2500, Int(client.Since(cursor).Single(f => f.Opcode == Op(105)).Payload, 16));

            double previousDays = 0;
            for (var purchase = 1; purchase <= 2; purchase++)
            {
                cursor = client.Cursor;
                await client.SendAsync(Op(808), Buy(1000942, 0), ct, 0);
                await client.WaitAsync(cursor, Op(813), TimeSpan.FromSeconds(5), ct);
                var frames = client.Since(cursor).ToArray();
                Assert.Equal(new[] { Op(802), Op(105), Op(813) }, frames.Select(f => f.Opcode));
                Assert.Equal(4 + slotLength, frames[0].Payload.Length);
                Assert.Equal(1, Int(frames[0].Payload, 0));
                Assert.Equal(204033, Int(frames[0].Payload, 4));
                var days = BinaryPrimitives.ReadDoubleLittleEndian(frames[0].Payload.AsSpan(12));
                if (purchase == 1) Assert.InRange(days, 1, 1.01);
                else Assert.Equal(1, days - previousDays, 8);
                previousDays = days;
                Assert.Equal(2500 - purchase * 1000, Int(frames[1].Payload, 16));
                Assert.Equal(1000942, Int(frames[2].Payload, 0));
                Assert.Equal(1, Int(frames[2].Payload, 4));
                await CheckBalanceAsync(client, cashCheck == 0 ? (ushort)0 : Op(cashCheck), Op(105), statLength, 2500 - purchase * 1000, 5000, ct);
            }
            foreach (var (key, option) in new[] { (1000942, 0), (1000942, 1), (204033, 0), (int.MaxValue, 0) })
            {
                cursor = client.Cursor;
                await client.SendAsync(Op(808), Buy(key, option), ct, 0);
                await client.WaitAsync(cursor, Op(813), TimeSpan.FromSeconds(5), ct);
                var frame = Assert.Single(client.Since(cursor));
                Assert.Equal(0, Int(frame.Payload, 4));
            }

            await using var reconnected = await LoginAsync(port, Op(3), Op(105), profile, build, ct);
            cursor = reconnected.Cursor;
            // Inventory entry restates the bag on S801, the one packet whose handler builds the
            // bag screen's cells. No balance frame rides along; the dialog asks for one itself.
            await reconnected.SendAsync(Op(802), [], ct, 0);
            await reconnected.WaitAsync(cursor, Op(801), TimeSpan.FromSeconds(5), ct);
            var restored = reconnected.Since(cursor).ToArray();
            Assert.Equal(new[] { Op(801) }, restored.Select(f => f.Opcode));
            Assert.Equal(1, Int(restored[0].Payload, 0));
            Assert.Equal(204033, Int(restored[0].Payload, 4));
            Assert.InRange(BinaryPrimitives.ReadDoubleLittleEndian(restored[0].Payload.AsSpan(12)), 1.99, 2.001);

            // The cash dialog waits for a fresh S105 even when its displayed balance is sufficient.
            await CheckBalanceAsync(reconnected, cashCheck == 0 ? (ushort)0 : Op(cashCheck), Op(105), statLength, 500, 5000, ct);
            cursor = reconnected.Cursor;
            await reconnected.SendAsync(Op(808), Buy(4000001, 0), ct, 0);
            await reconnected.WaitAsync(cursor, Op(813), TimeSpan.FromSeconds(5), ct);
            var cashPurchase = reconnected.Since(cursor).ToArray();
            Assert.Equal(new[] { Op(802), Op(105), Op(813) }, cashPurchase.Select(f => f.Opcode));
            Assert.Equal(200001, Int(cashPurchase[0].Payload, 4));
            Assert.Equal(1, Int(cashPurchase[2].Payload, 4));
            Assert.Equal(statLength + 26, cashPurchase[1].Payload.Length);
            Assert.Equal(500, Int(cashPurchase[1].Payload, 16));
            Assert.Equal(500, Int(cashPurchase[1].Payload, 20));
            await CheckBalanceAsync(reconnected, cashCheck == 0 ? (ushort)0 : Op(cashCheck), Op(105), statLength, 500, 500, ct);
            cursor = reconnected.Cursor;
            await reconnected.SendAsync(Op(808), Buy(4000001, 0), ct, 0);
            await reconnected.WaitAsync(cursor, Op(813), TimeSpan.FromSeconds(5), ct);
            Assert.Equal(0, Int(Assert.Single(reconnected.Since(cursor)).Payload, 4));
            await CheckBalanceAsync(reconnected, cashCheck == 0 ? (ushort)0 : Op(cashCheck), Op(105), statLength, 500, 500, ct);

            cursor = reconnected.Cursor;
            await reconnected.SendAsync(Op(808), Buy(1000943, 0), ct, 0);
            await reconnected.WaitAsync(cursor, Op(813), TimeSpan.FromSeconds(5), ct);
            var permanent = reconnected.Since(cursor).ToArray();
            Assert.Equal(new[] { Op(802), Op(105), Op(813) }, permanent.Select(f => f.Opcode));
            Assert.Equal(204034, Int(permanent[0].Payload, 4));
            Assert.Equal(2958101.0, BinaryPrimitives.ReadDoubleLittleEndian(permanent[0].Payload.AsSpan(12)));
            Assert.Equal(400, Int(permanent[1].Payload, 16));
            Assert.Equal(1, Int(permanent[2].Payload, 4));
            cursor = reconnected.Cursor;
            await reconnected.SendAsync(Op(808), Buy(1000943, 0), ct, 0);
            await reconnected.WaitAsync(cursor, Op(813), TimeSpan.FromSeconds(5), ct);
            Assert.Equal(0, Int(Assert.Single(reconnected.Since(cursor)).Payload, 4));
            cursor = reconnected.Cursor;
            await reconnected.SendAsync(Op(802), [], ct, 0);
            await reconnected.WaitAsync(cursor, Op(801), TimeSpan.FromSeconds(5), ct);
            var finalState = reconnected.Since(cursor).ToArray();
            Assert.Equal(new[] { Op(801) }, finalState.Select(f => f.Opcode));
            Assert.Equal(3, Int(finalState[0].Payload, 0));

            if (cashCheck != 0)
            {
                await using var invalidBalance = await LoginAsync(port, Op(3), Op(105), profile, build, ct);
                var invalidCursor = invalidBalance.Cursor;
                await invalidBalance.SendAsync(Op(cashCheck), new byte[] { 0 }, ct, 0);
                await Assert.ThrowsAsync<IOException>(() => invalidBalance.WaitAsync(invalidCursor, Op(105), TimeSpan.FromSeconds(5), ct));
            }

            cursor = reconnected.Cursor;
            await reconnected.SendAsync(Op(808), Buy(1000942, 2), ct, 0);
            await Assert.ThrowsAsync<IOException>(() => reconnected.WaitAsync(cursor, Op(813), TimeSpan.FromSeconds(5), ct));
        }
        finally
        {
            await host.ShutdownAsync(CancellationToken.None);
            File.Delete(tablePath);
            File.Delete(shopPath);
            Directory.Delete(directory);
        }
    }

    private static async Task CheckBalanceAsync(SimClient client, ushort request, ushort reply, int statLength, int lucci, int cash, CancellationToken ct)
    {
        if (request == 0) return;
        var cursor = client.Cursor;
        await client.SendAsync(request, [], ct, 0);
        await client.WaitAsync(cursor, reply, TimeSpan.FromSeconds(5), ct);
        var frame = Assert.Single(client.Since(cursor));
        Assert.Equal(reply, frame.Opcode);
        Assert.Equal(statLength + 26, frame.Payload.Length); // The 13-character login name is UTF-16LE.
        Assert.Equal(lucci, Int(frame.Payload, 16));
        Assert.Equal(cash, Int(frame.Payload, 20));
    }

    private static async Task<SimClient> LoginAsync(int port, ushort loginOpcode, ushort statOpcode, string profile, int build, CancellationToken ct)
    {
        var client = await SimClient.ConnectAsync("purchase", new DnsEndPoint("127.0.0.1", port),
            TestWireDialects.Get(profile), ushort.MaxValue, ct);
        var handshake = new byte[96];
        BinaryPrimitives.WriteInt32LittleEndian(handshake, build);
        await client.SendAsync(1, handshake, ct, 0);
        await client.WaitAsync(0, 2, TimeSpan.FromSeconds(5), ct);
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new PacketWriter(buffer);
        writer.WriteWideStringPrefixed("purchase-test");
        writer.WriteWideStringPrefixed(string.Empty);
        writer.WriteWideStringPrefixed(string.Empty);
        writer.WriteUInt32(0);
        if (build is 812 or 717)
        {
            writer.WriteUInt32(0);
            writer.WriteUInt32(0);
        }
        var cursor = client.Cursor;
        await client.SendAsync(loginOpcode, buffer.WrittenSpan.ToArray(), ct, 0);
        await client.WaitAsync(cursor, loginOpcode, TimeSpan.FromSeconds(5), ct);
        await client.WaitAsync(cursor, statOpcode, TimeSpan.FromSeconds(5), ct);
        return client;
    }

    private static void WriteWorkbook(string path, int build)
    {
        var xml = XDocument.Load(SyntheticClientTables.ShopItem2WithoutTerms);
        XNamespace ns = "urn:schemas-microsoft-com:office:spreadsheet";
        foreach (var data in xml.Descendants(ns + "Data"))
            data.Value = data.Value switch { "1000001" => "1000942", "100001" => "204033", "120" => "1000", _ => data.Value };
        // Legacy cash prices use sellPrice; the modern fixture stores them in sellPriceCash.
        foreach (var table in xml.Descendants(ns + "Worksheet").Where(e =>
            (string?)e.Attribute(ns + "Name") is "Stock" or "StockSet"))
        {
            var tableRows = table.Descendants(ns + "Row").ToArray();
            var headers = tableRows[0].Descendants(ns + "Data").Select(e => e.Value).ToArray();
            foreach (var row in tableRows.Skip(1))
            {
                var cells = row.Descendants(ns + "Data").ToArray();
                if (cells[0].Value is "3000001" or "4000001")
                    cells[Array.IndexOf(headers, "sellPrice")].Value = "4500";
            }
        }
        var sheet = xml.Descendants(ns + "Worksheet").Single(e => (string?)e.Attribute(ns + "Name") == "ShopItem");
        var rows = sheet.Descendants(ns + "Row").ToArray();
        string[][] display = [["1", "lucci", "1", "Clothes", "Shoes", "0"], ["1", "cash", "0", "Accessories", "Spray", "0"]];
        for (var row = 1; row < rows.Length; row++)
            foreach (var value in display[row - 1])
                rows[row].Add(new XElement(ns + "Cell", new XElement(ns + "Data", new XAttribute(ns + "Type", "String"), value)));
        string[][] extra = [["durationType", "maxDuration", "buyAgain", "function"], ["day", "360", "0", "0"], ["count", "999", "0", "0"]];
        for (var row = 0; row < rows.Length; row++)
            for (var col = 0; col < 4; col++)
                rows[row].Add(new XElement(ns + "Cell", new XAttribute(ns + "Index", 9 + col),
                    new XElement(ns + "Data", new XAttribute(ns + "Type", "String"), extra[row][col])));
        var permanentItem = new XElement(rows[1]);
        permanentItem.Descendants(ns + "Data").First().Value = "204034";
        rows[1].Parent!.Add(permanentItem);
        var stockTable = xml.Descendants(ns + "Worksheet").Single(e => (string?)e.Attribute(ns + "Name") == "Stock")
            .Element(ns + "Table")!;
        var permanentStock = new XElement(stockTable.Elements(ns + "Row").ElementAt(1));
        var permanentCells = permanentStock.Descendants(ns + "Data").ToArray();
        permanentCells[0].Value = "1000943";
        permanentCells[1].Value = "204034";
        permanentCells[3].Value = build == 279 ? "1" : "9";
        permanentCells[4].Value = "-1";
        permanentCells[5].Value = permanentCells[6].Value = "100";
        stockTable.Add(permanentStock);
        xml.Save(path);
    }

    private static byte[] Buy(int key, int option)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, key);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), option);
        return bytes;
    }

    private static int Int(byte[] bytes, int offset) => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset));

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
