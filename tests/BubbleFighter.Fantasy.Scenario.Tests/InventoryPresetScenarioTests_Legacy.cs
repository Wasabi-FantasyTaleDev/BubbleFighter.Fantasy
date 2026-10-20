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

/// <summary>
/// Boots a kr-812 row set and drives the bag scenarios the card covers end to end:
/// a fresh login seeds the build's starter subset and answers with the S801 bag plus
/// the S805 view and S833 preset bar; opening the bag restates it; equipping a stored
/// item republishes bag, view and bar; a C2S 830 preset request persists the slot and
/// unlatches the client through S2C 832.
/// </summary>
public sealed class InventoryPresetScenarioTests_Legacy
{
    // kr-812's ShopItem table carries fifteen of the modern starter ids.
    private static readonly int[] StarterIds =
        [300001, 201001, 201003, 201004, 202001, 202003, 202004, 203001, 203003, 203004,
         204001, 204003, 204004, 205001, 207001];
    private static readonly int[] StarterWorn = [300001, 202001, 201001, 205001, 207001];

    [Fact]
    public async Task LoginSeedsTheBagAndPresetSwitchingUnlatches()
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = limit.Token;
        var directory = Path.Combine(Path.GetTempPath(), $"bff-bag-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var tablePath = Path.Combine(directory, "opcodes.yaml");
        var entries = new (int Value, string Direction, string Name)[]
        {
            (3, "c2s", "gcpReqLogin"), (3, "s2c", "gspRepLogin"),
            (802, "c2s", "gcpEnterInventory"),
            (803, "c2s", "gcpReqInventoryEquip"), (804, "c2s", "gcpReqInventoryUnequip"),
            (808, "c2s", "gcpReqShopBuy"), (825, "c2s", "gcpReqCheckCash"),
            (830, "c2s", "gcpReqInventoryChangeWeaponPresetIdx"),
            (105, "s2c", "gspUserStat"), (813, "s2c", "gspRepShopBuy"),
            (801, "s2c", "gspInventoryUpdateAllInventorySlot"),
            (802, "s2c", "gspInventoryUpdateInventorySlots"),
            (805, "s2c", "gspInventoryViewUpdateView"),
            (832, "s2c", "gspReqInventoryChangeWeaponPresetIdx"),
            (833, "s2c", "gspReqWeaponPreSetUpdate"),
        };
        await File.WriteAllTextAsync(tablePath, "profile: kr-812\nopcodes:\n" + string.Concat(entries.Select(e =>
            $"  - value: {e.Value}\n    direction: {e.Direction}\n    name: {e.Name}\n    name_source: label@kr-812\n    evidence: verified\n")), ct);
        var port = FreePort();
        PluginRow Row(string plugin, Dictionary<string, object?>? config = null) =>
            new() { Id = plugin, Plugin = plugin, Source = "bag-scenario", ClientBuild = new("kr", 812, 458), Config = config };
        var host = new PluginHost(
        [
            Row("core.protocol-registry"), Row("core.session-registry"), Row("core.packet-router"),
            Row("core.message-router"), Row("core.client-profiles"), Row("core.opcode-tables"), Row("core.wire-dialects"),
            Row("persistence.memory"), Row("dialect", TestWireDialects.RowConfig("kr-812")),
            Row("protocol", new() { ["opcode_table_path"] = tablePath, ["bootstrap_status"] = 1 }),
            Row("clientdata", TestClientData.RowConfig(TestClientData.SyntheticShop())),
            Row("dialect.messages", new() { ["protocol"] = "kr-812/game", ["enable_purchases"] = true, ["packets_directory"] = RepositoryPackets.Directory() }),
            Row("lobby", new()
            {
                ["protocol"] = "kr-812/game", ["auto_create_players"] = true,
                ["inventory_source"] = "mysql", ["money_source"] = "mysql",
                ["starter_item_ids"] = StarterIds.Select(id => (object)id).ToArray(),
                ["starter_worn_ids"] = StarterWorn.Select(id => (object)id).ToArray(),
            }),
            Row("inventoryview", new()
            {
                ["protocol"] = "kr-812/game", ["client_profile"] = "kr-812", ["version_major"] = 812,
                ["enabled"] = true, ["publish_equipped_view_push"] = true, ["view_request"] = "",
            }),
            Row("equipment", new() { ["protocol"] = "kr-812/game", ["client_profile"] = "kr-812", ["version_major"] = 812 }),
            Row("shop", new()
            {
                ["protocol"] = "kr-812/game", ["client_profile"] = "kr-812", ["version_major"] = 812,
                ["catalog_schema"] = "legacy", ["packets_directory"] = RepositoryPackets.Directory(), ["catalog_display_column"] = "displayName",
                ["shop_item_client_path"] = "Config/ShopItem2.xml",
                ["send_shop_catalog"] = false, ["grant_purchased_items"] = false,
            }),
            Row("net.tcp", new() { ["listen"] = $"127.0.0.1:{port}", ["protocol"] = "kr-812/game" }),
        ], PluginCatalog.Discover([Path.Combine(AppContext.BaseDirectory, "plugins")]), NullLoggerFactory.Instance);
        try
        {
            await host.StartAsync(ct);
            var client = await ConnectAsync(port, ct);

            // Login: repLogin, user state, the seeded fifteen-row bag, then view + preset bar.
            var cursor = await SendLoginAsync(client, ct);
            await client.WaitAsync(cursor, 833, TimeSpan.FromSeconds(10), ct);
            var login = client.Since(cursor).ToArray();
            Assert.Equal([3, 105, 801, 805, 833], login.Select(f => f.Opcode).ToArray());
            var bag = Slots(login[2].Payload);
            Assert.Equal(StarterIds.Order().ToArray(), bag.Select(s => s.ItemId).ToArray());
            Assert.Equal(StarterWorn.Order().ToArray(), bag.Where(s => s.Worn).Select(s => s.ItemId).Order().ToArray());
            var bar = PresetBar(login[4].Payload);
            Assert.Equal(0, bar.Active);
            Assert.Equal([202001, 201001, 205001, 207001, 0], bar.Items);
            Assert.Equal([1, 1, 1, 1, 0], bar.Selectable);

            // Opening the bag restates it on 801; the bar + view order is a dialect choice and
            // both packets must arrive.
            cursor = client.Cursor;
            await client.SendAsync(802, [], ct, 0);
            await client.WaitAsync(cursor, 833, TimeSpan.FromSeconds(10), ct);
            var opened = client.Since(cursor).ToArray();
            Assert.Equal([801, 805, 833], opened.Select(f => f.Opcode).ToArray());
            Assert.Equal(StarterIds.Order().ToArray(), Slots(opened[0].Payload).Select(s => s.ItemId).ToArray());

            // Equipping a stored sniper republishes the bag, the view and the bar with the
            // new primary in slot 0.
            cursor = client.Cursor;
            await client.SendAsync(803, Int(203001), ct, 0);
            await client.WaitAsync(cursor, 833, TimeSpan.FromSeconds(10), ct);
            var equip = client.Since(cursor).ToArray();
            Assert.Equal([802, 805, 833], equip.Select(f => f.Opcode).ToArray());
            Assert.Contains(203001, Slots(equip[0].Payload).Where(s => s.Worn).Select(s => s.ItemId));
            Assert.Equal(203001, PresetBar(equip[2].Payload).Items[0]);

            // A preset request for a bound slot persists it and unlatches through 832.
            cursor = client.Cursor;
            await client.SendAsync(830, PresetRequest(1), ct, 0);
            await client.WaitAsync(cursor, 832, TimeSpan.FromSeconds(10), ct);
            var preset = client.Since(cursor).ToArray();
            Assert.Equal([805, 833, 832], preset.Select(f => f.Opcode).ToArray());
            Assert.Equal(1, preset[2].Payload[0]);
            Assert.Equal(0, Int(preset[2].Payload, 1));
            Assert.Equal(1, PresetBar(preset[1].Payload).Active);

            // An unbound slot is refused: the reply carries the still-active slot.
            cursor = client.Cursor;
            await client.SendAsync(830, PresetRequest(4), ct, 0);
            await client.WaitAsync(cursor, 832, TimeSpan.FromSeconds(10), ct);
            var refused = client.Since(cursor).ToArray();
            Assert.Equal([832], refused.Select(f => f.Opcode).ToArray());
            Assert.Equal(1, refused[0].Payload[0]);

            // The persisted slot survives a reconnect.
            var again = await ConnectAsync(port, ct);
            var cursor2 = await SendLoginAsync(again, ct);
            await again.WaitAsync(cursor2, 833, TimeSpan.FromSeconds(10), ct);
            var relogin = again.Since(cursor2).ToArray();
            Assert.Equal(1, PresetBar(relogin.Single(f => f.Opcode == 833).Payload).Active);
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
        var client = await SimClient.ConnectAsync("bag", new DnsEndPoint("127.0.0.1", port),
            TestWireDialects.Get("kr-812"), ushort.MaxValue, ct);
        var handshake = new byte[96];
        BinaryPrimitives.WriteInt32LittleEndian(handshake, 812);
        await client.SendAsync(1, handshake, ct, 0);
        await client.WaitAsync(0, 2, TimeSpan.FromSeconds(5), ct);
        return client;
    }

    private static async Task<int> SendLoginAsync(SimClient client, CancellationToken ct)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new PacketWriter(buffer);
        writer.WriteWideStringPrefixed("bag-test");
        writer.WriteWideStringPrefixed(string.Empty);
        writer.WriteWideStringPrefixed(string.Empty);
        writer.WriteUInt32(0);
        writer.WriteUInt32(0);
        writer.WriteUInt32(0);
        var cursor = client.Cursor;
        await client.SendAsync(3, buffer.WrittenSpan.ToArray(), ct, 0);
        return cursor;
    }

    private static (int ItemId, bool Worn)[] Slots(byte[] payload)
    {
        var count = BinaryPrimitives.ReadInt32LittleEndian(payload);
        var slots = new (int ItemId, bool Worn)[count];
        for (var i = 0; i < count; i++)
        {
            var at = 4 + i * 44;
            slots[i] = (BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(at)),
                BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(at + 16)) != 0);
        }
        return slots;
    }

    private static (byte Active, int[] Items, int[] Selectable) PresetBar(byte[] payload)
    {
        var count = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(5));
        var items = new int[count];
        for (var i = 0; i < count; i++)
            items[i] = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(9 + i * 4));
        var at = 9 + count * 4;
        var selectable = new int[BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(at))];
        at += 4;
        for (var i = 0; i < selectable.Length; i++)
            selectable[i] = payload[at + i];
        return (payload[0], items, selectable);
    }

    private static byte[] PresetRequest(int slot)
    {
        var body = new byte[5];
        BinaryPrimitives.WriteInt32LittleEndian(body, -1);
        body[4] = (byte)slot;
        return body;
    }

    private static byte[] Int(int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
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
