using BubbleFighter.Fantasy.Tests;
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

/// <summary>Runs each legacy shop request through the real protocol, router and shop plugin over TCP.</summary>
public sealed class LegacyShopScenarioTests
{
    [Theory]
    [InlineData("cn", 279, 0, false)]
    [InlineData("cn", 324, 0, false)]
    [InlineData("id", 400, 8, false)]
    [InlineData("id", 1809, 8, false)]
    [InlineData("cn", 706, 8, false)]
    [InlineData("cn", 717, 12, false)]
    [InlineData("kr", 812, 12, false)]
    [InlineData("cn", 279, 0, true)]
    [InlineData("cn", 324, 0, true)]
    [InlineData("id", 400, 8, true)]
    [InlineData("id", 1809, 8, true)]
    [InlineData("cn", 706, 8, true)]
    [InlineData("cn", 717, 12, true)]
    [InlineData("kr", 812, 12, true)]
    public async Task CatalogEntryUsesItsDialectAndDoesNotClaimInventoryOrPurchase(
        string region, int build, int requestBytes, bool remap)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = limit.Token;
        var profile = $"{region}-{build}";
        var dialect = TestWireDialects.Get(profile);
        var port = FreePort();
        var requestOpcode = (ushort)(remap ? 1801 : 801);
        var responseOpcode = (ushort)(remap ? 1800 : 800);
        var tablePath = Path.Combine(Path.GetTempPath(), $"bff-shop-{Guid.NewGuid():N}.yaml");
        await File.WriteAllTextAsync(tablePath, $"""
            profile: {profile}
            opcodes:
              - value: {requestOpcode}
                direction: c2s
                name: gcpEnterShop
                name_source: label@{profile}
                evidence: verified
              - value: {responseOpcode}
                direction: s2c
                name: gspRepEnterShop
                name_source: label@{profile}
                evidence: verified
            """, ct);
        PluginRow Row(string id, string plugin, Dictionary<string, object?>? config = null) =>
            new() { Id = id, Plugin = plugin, Source = "legacy-shop-scenario",
                ClientBuild = new(region, build, build switch { 812 => 458, 400 => 113, _ => 0 }), Config = config };
        var host = new PluginHost(
        [
            Row("core.protocol-registry", "core.protocol-registry"),
            Row("core.session-registry", "core.session-registry"),
            Row("core.packet-router", "core.packet-router"),
            Row("core.client-profiles", "core.client-profiles"),
            Row("core.opcode-tables", "core.opcode-tables"),
            Row("core.wire-dialects", "core.wire-dialects"),
            Row("persistence.memory", "persistence.memory"),
            Row("dialect", "dialect", TestWireDialects.RowConfig(profile)),
            Row("clientdata", "clientdata", TestClientData.RowConfig(TestClientData.SyntheticShop())),
            // Ordering by declared service dependency must put protocol before shop.
            Row("shop", "shop", new()
            {
                ["protocol"] = profile + "/game", ["client_profile"] = profile,
                ["version_major"] = build, ["catalog_schema"] = "legacy", ["packets_directory"] = RepositoryPackets.Directory(),
                ["catalog_display_column"] = "displayName",
                ["shop_item_client_path"] = "Config/ShopItem2.xml",
                ["send_shop_catalog"] = true, ["grant_purchased_items"] = false,
            }),
            Row("protocol", "protocol", new() { ["opcode_table_path"] = tablePath, ["bootstrap_status"] = 1 }),
            Row("game-listener", "net.tcp", new() { ["listen"] = $"127.0.0.1:{port}", ["protocol"] = profile + "/game" }),
        ], PluginCatalog.Discover([Path.Combine(AppContext.BaseDirectory, "plugins")]), NullLoggerFactory.Instance);
        try
        {
            await host.StartAsync(ct);
            await using var client = await SimClient.ConnectAsync("shop", new DnsEndPoint("127.0.0.1", port),
                dialect, ushort.MaxValue, ct);
            var handshake = new byte[96];
            BinaryPrimitives.WriteInt32LittleEndian(handshake, dialect.Profile.ClientVersionMajor);
            await client.SendAsync(1, handshake, ct, 0);
            await client.WaitAsync(0, 2, TimeSpan.FromSeconds(5), ct);
            var request = new byte[requestBytes];
            const long token = 0x4008000000000000;
            if (requestBytes >= 8) BinaryPrimitives.WriteInt64LittleEndian(request, token);
            if (requestBytes == 12) BinaryPrimitives.WriteInt32LittleEndian(request.AsSpan(8), 1000);
            byte[]? first = null;
            for (var repeat = 0; repeat < 2; repeat++)
            {
                var cursor = client.Cursor;
                // C802 is empty inventory entry, never the modern cash-shop request.
                // Unimplemented purchase C808 must also remain outside this catalog adapter.
                await client.SendAsync(802, [], ct, 0);
                await client.SendAsync(808, [], ct, 0);
                await client.SendAsync(requestOpcode, request, ct, 0);
                var reply = await client.WaitAsync(cursor, responseOpcode, TimeSpan.FromSeconds(5), ct);
                Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(reply.Payload));
                var cardOffset = 4;
                if (build >= 400)
                {
                    Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(reply.Payload.AsSpan(4)));
                    Assert.Equal(token, BinaryPrimitives.ReadInt64LittleEndian(reply.Payload.AsSpan(8)));
                    cardOffset = 16;
                }
                Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(reply.Payload.AsSpan(cardOffset)));
                Assert.InRange(reply.Payload.Length, 1, 65000);
                Assert.All(client.Since(cursor), frame => Assert.Equal(responseOpcode, frame.Opcode));
                if (first is not null) Assert.Equal(first, reply.Payload);
                first = reply.Payload;
            }
            var invalidCursor = client.Cursor;
            await client.SendAsync(requestOpcode, [1], ct, 0);
            await Assert.ThrowsAsync<IOException>(() => client.WaitAsync(invalidCursor, responseOpcode, TimeSpan.FromSeconds(5), ct));
        }
        finally
        {
            await host.ShutdownAsync(CancellationToken.None);
            File.Delete(tablePath);
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
