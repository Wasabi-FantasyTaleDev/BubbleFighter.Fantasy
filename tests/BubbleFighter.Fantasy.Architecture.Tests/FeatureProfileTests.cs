using BubbleFighter.Fantasy.Tests;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Shop;
using BubbleFighter.Fantasy.Kernel;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Architecture.Tests;

public sealed class FeatureProfileTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [Trait("Category", "LocalClientData")]
    public async Task MemoryBackedFeatureTreeStartsInEitherRowOrderAndReloadsDependents(bool reverse, bool targetRequest)
    {
        var rows = Rows();
        if (reverse)
            rows.Reverse();
        var host = new PluginHost(rows, PluginBoundaryTests.Discover(), NullLoggerFactory.Instance);
        try
        {
            await host.StartAsync(CancellationToken.None);
            Assert.All(host.Instances, row => Assert.Equal(PluginState.Active, row.State));
            var reloaded = await Reload("persistence");
            foreach (var id in new[] { "persistence", "fighters", "inventoryview", "ingame", "match", "lobby", "shop", "equipment" })
                Assert.Contains(id, reloaded);
            Assert.DoesNotContain("clan", reloaded);
            Assert.All(host.Instances, row => Assert.Equal(PluginState.Active, row.State));
            // The shared message row owns kr-1256's login now, so it reloads with the lobby that consumes its
            // login services (the reload restored after the login slice moved here).
            var messageReload = await Reload("dialect.messages");
            Assert.Contains("dialect.messages", messageReload);
            Assert.Contains("lobby", messageReload);
            Assert.DoesNotContain("persistence", messageReload);
            Assert.All(host.Instances, row => Assert.Equal(PluginState.Active, row.State));
        }
        finally { await host.ShutdownAsync(CancellationToken.None); }

        async Task<IReadOnlyList<string>> Reload(string rowId)
        {
            if (!targetRequest) return (await host.ReloadAsync(rowId, CancellationToken.None)).ReloadedRowIds;
            var result = await host.RequestState(rowId, true, true).WaitAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(PluginTransitionOutcome.Converged, result.Outcome);
            return result.ChangedRowIds;
        }
    }

    [Theory]
    [InlineData("fighters", null)]
    [InlineData("inventoryview", "IEquipmentSelection")]
    [InlineData("dialect.messages", "LocalEquipmentViewUpdated")]
    [InlineData("ingame", "Match capability directory")]
    [InlineData("rooms", "room create binding")]
    [InlineData("match", null)]
    [InlineData("match-messages", "Match inbound bindings")]
    [InlineData("match-replies", "Match reply sender")]
    [InlineData("shop", null)]
    [InlineData("equipment", null)]
    [InlineData("lobby", "IInventoryPushService")]
    [InlineData("persistence", "IInventoryRepository")]
    [InlineData("core.message-router", "IMessageRouter")]
    [Trait("Category", "LocalClientData")]
    public async Task RemovingFeatureRowsStartsOrReportsTheConfiguredMissingService(string removed, string? missing)
    {
        var rows = Rows().Where(row => row.Id != removed).ToArray();
        var host = new PluginHost(rows, PluginBoundaryTests.Discover(), NullLoggerFactory.Instance);
        try
        {
            if (missing is null)
                await host.StartAsync(CancellationToken.None);
            else
            {
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync(CancellationToken.None).AsTask());
                Assert.Contains(missing, error.Message);
                Assert.DoesNotContain(host.Instances, row => row.State == PluginState.Active);
            }
        }
        finally { await host.ShutdownAsync(CancellationToken.None); }
    }

    [Fact]
    [Trait("Category", "LocalClientData")]
    public async Task MatchScopeMismatchFailsAtLoadAndUnwindsAllEffects()
    {
        var rows = Rows();
        rows.RemoveAll(row => row.Id == "match");
        rows.Add(PluginBoundaryTests.Row("match", "match-connection", new() { ["protocol"] = "missing/relay" }));
        var host = new PluginHost(rows, PluginBoundaryTests.Discover(), NullLoggerFactory.Instance);
        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync(CancellationToken.None).AsTask());
            Assert.Contains("Match inbound bindings missing", error.ToString());
            Assert.DoesNotContain(host.Instances, row => row.State == PluginState.Active);
        }
        finally { await host.ShutdownAsync(CancellationToken.None); }
    }

    private static List<PluginRow> Rows(bool clan = false) => [
        // The row reads the developer's provisioned kr-1256 tree, as a profile does; this test class is LocalClientData.
        PluginBoundaryTests.Row("clientdata", "clientdata", TestClientData.RowConfig(LocalClientData.Directories("kr-1256"))),
        PluginBoundaryTests.Row("shop", "shop", new()
        {
            ["shop_item_client_path"] = "Config/ShopItem2.xml",
            ["grant_purchased_items"] = true,
        }),
        PluginBoundaryTests.Row("equipment", "equipment"),
        PluginBoundaryTests.Row("lobby", "lobby", new() { ["inventory_source"] = "mysql", ["money_source"] = "mysql" }),
        PluginBoundaryTests.Row("rooms", "dialect.rooms", TestWireDialects.ModernRoomsConfig()),
        PluginBoundaryTests.Row("ingame", "ingame"),
        // The match transport uses capability admission. This fixture does not start rounds, so the
        // separate game-side offer binding is absent.
        PluginBoundaryTests.Row("match-messages", "dialect.match", new()
        {
            ["protocol"] = "kr-1256/relay", ["native_handshake"] = "capability",
            ["packets_directory"] = SharedDialectRows.PacketsDirectory(),
        }),
        PluginBoundaryTests.Row("match-replies", "dialect.match-replies", new()
        {
            ["protocol"] = "kr-1256/relay", ["packets_directory"] = SharedDialectRows.PacketsDirectory(),
        }),
        PluginBoundaryTests.Row("match", "match-connection", new() { ["capability_authentication"] = true }),
        PluginBoundaryTests.Row("inventoryview", "inventoryview", new() { ["enabled"] = true, ["publish_equipped_view_push"] = true }),
        SharedDialectRows.Kr1256(new() { ["inventory"] = true, ["clan"] = clan }),
        PluginBoundaryTests.Row("fighters", "fighters", new()
        {
            ["fighter_config_client_path"] = "Config/FighterConfig.xml",
        }),
        PluginBoundaryTests.Row("persistence", "persistence.memory"),
        PluginBoundaryTests.Row("core.packet-router", "core.packet-router"),
        PluginBoundaryTests.Row("core.message-router", "core.message-router"),
        PluginBoundaryTests.Row("core.opcode-tables", "core.opcode-tables"),
        // The feature rows name their packets; the protocol row supplies the kr-1256 numbers.
        PluginBoundaryTests.Row("core.protocol-registry", "core.protocol-registry"),
        PluginBoundaryTests.Row("core.client-profiles", "core.client-profiles"),
        PluginBoundaryTests.Row("core.wire-dialects", "core.wire-dialects"),
        PluginBoundaryTests.Row("dialect", "dialect", TestWireDialects.RowConfig("kr-1256")),
        PluginBoundaryTests.Row("protocol", "protocol", new() { ["opcode_table_path"] = Path.Combine(AppContext.BaseDirectory, "opcodes.yaml") }),
    ];

    /// <summary>The rows a clan-only host needs: the clan row is served by a mounted message row with <c>clan: true</c>.</summary>
    private static List<PluginRow> ClanRows() =>
    [
        PluginBoundaryTests.Row("core.protocol-registry", "core.protocol-registry"),
        PluginBoundaryTests.Row("core.session-registry", "core.session-registry"),
        PluginBoundaryTests.Row("core.packet-router", "core.packet-router"),
        PluginBoundaryTests.Row("core.message-router", "core.message-router"),
        PluginBoundaryTests.Row("core.client-profiles", "core.client-profiles"),
        PluginBoundaryTests.Row("core.opcode-tables", "core.opcode-tables"),
        PluginBoundaryTests.Row("core.wire-dialects", "core.wire-dialects"),
        PluginBoundaryTests.Row("persistence", "persistence.memory"),
        PluginBoundaryTests.Row("dialect", "dialect", TestWireDialects.RowConfig("kr-1256")),
        SharedDialectRows.Kr1256(new()
        {
            ["clan"] = true,
            ["login"] = false,
            ["main_menu_init"] = false,
            ["equipment"] = false,
            ["equipment_view"] = false,
        }),
        PluginBoundaryTests.Row("protocol", "protocol", new() { ["opcode_table_path"] = Path.Combine(AppContext.BaseDirectory, "opcodes.yaml") }),
        PluginBoundaryTests.Row("clan", "clan"),
    ];

    [Fact]
    [Trait("Category", "LocalClientData")]
    public async Task ClanStartsAlongsideTheInGameRows()
    {
        // Both rows handle a C2S 25, on different connections: the clan binding answers it on the game
        // connection, the post-swap report on the match connection.
        var rows = Rows(clan: true);
        rows.Add(PluginBoundaryTests.Row("clan", "clan"));
        var host = new PluginHost(rows, PluginBoundaryTests.Discover(), NullLoggerFactory.Instance);
        try
        {
            await host.StartAsync(CancellationToken.None);
            Assert.All(host.Instances, row => Assert.Equal(PluginState.Active, row.State));
        }
        finally { await host.ShutdownAsync(CancellationToken.None); }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClanRequiresStorageAndReloadsWithItsProvider(bool reverse)
    {
        var rows = ClanRows();
        if (reverse)
            rows.Reverse();
        var catalog = PluginBoundaryTests.Discover();
        var host = new PluginHost(rows, catalog, NullLoggerFactory.Instance);
        try
        {
            await host.StartAsync(CancellationToken.None);
            Assert.All(host.Instances, row => Assert.Equal(PluginState.Active, row.State));
            var reloaded = (await host.ReloadAsync("persistence", CancellationToken.None)).ReloadedRowIds;
            // The message row reads the settings repository optionally (customized_keys), so the persistence
            // reload cascades through it to the clan row that consumes its bindings.
            Assert.Equal(new[] { "persistence", "dialect.messages", "clan" }, reloaded);
        }
        finally { await host.ShutdownAsync(CancellationToken.None); }

        var missing = new PluginHost(rows.Where(row => row.Id != "persistence").ToArray(), catalog, NullLoggerFactory.Instance);
        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => missing.StartAsync(CancellationToken.None).AsTask());
            Assert.Contains("IClanStore", error.Message);
            Assert.DoesNotContain(missing.Instances, row => row.State == PluginState.Active);
        }
        finally { await missing.ShutdownAsync(CancellationToken.None); }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShopOpenStateUsesProviderLifetimeAndExplicitDependency(bool reverse)
    {
        var consumerId = Guid.NewGuid().ToString("N");
        var rows = new List<PluginRow>
        {
            PluginBoundaryTests.Row("clientdata", "clientdata", TestClientData.RowConfig(TestClientData.SyntheticShop())),
            PluginBoundaryTests.Row("shop", "shop", new()
            {
                ["shop_item_client_path"] = "Config/ShopItem2.xml",
                ["grant_purchased_items"] = false,
            }),
            PluginBoundaryTests.Row("persistence", "persistence.memory"),
            PluginBoundaryTests.Row("router", "core.packet-router"),
            // The shop consumes typed messages only; the shared dialect row binds them to the kr-1256 packets.
            PluginBoundaryTests.Row("core.protocol-registry", "core.protocol-registry"),
            PluginBoundaryTests.Row("core.session-registry", "core.session-registry"),
            PluginBoundaryTests.Row("core.message-router", "core.message-router"),
            PluginBoundaryTests.Row("core.client-profiles", "core.client-profiles"),
            PluginBoundaryTests.Row("core.opcode-tables", "core.opcode-tables"),
            PluginBoundaryTests.Row("core.wire-dialects", "core.wire-dialects"),
            PluginBoundaryTests.Row("dialect", "dialect", TestWireDialects.RowConfig("kr-1256")),
            PluginBoundaryTests.Row("protocol", "protocol", new() { ["opcode_table_path"] = Path.Combine(AppContext.BaseDirectory, "opcodes.yaml") }),
            PluginBoundaryTests.Row("dialect.messages", "dialect.messages", TestWireDialects.MessagesConfig(new() { ["protocol"] = "kr-1256/game", ["login"] = false })),
            PluginBoundaryTests.Row(consumerId, "test.shop-open-consumer"),
        };
        if (reverse)
            rows.Reverse();
        var catalog = PluginBoundaryTests.Discover();
        var host = new PluginHost(rows, catalog, NullLoggerFactory.Instance);
        try
        {
            await host.StartAsync(CancellationToken.None);
            var first = ShopOpenConsumer.Stores[consumerId];
            var record = new ShopOpenRecord(1, 42, 7, ShopKind.ItemShop);
            await first.RememberOpenAsync(record, default);
            var shopReload = await host.ReloadAsync("shop", CancellationToken.None);
            Assert.Equal(new[] { "shop" }, shopReload.ReloadedRowIds);
            Assert.Same(first, ShopOpenConsumer.Stores[consumerId]);
            Assert.Equal(record, await first.FindLastOpenAsync(1, default));
            var providerReload = await host.ReloadAsync("persistence", CancellationToken.None);
            Assert.Contains("shop", providerReload.ReloadedRowIds);
            Assert.Contains(consumerId, providerReload.ReloadedRowIds);
            var replacement = ShopOpenConsumer.Stores[consumerId];
            Assert.NotSame(first, replacement);
            Assert.NotSame(first.GetType().Assembly, replacement.GetType().Assembly);
            Assert.Null(await replacement.FindLastOpenAsync(1, default));
        }
        finally { await host.ShutdownAsync(CancellationToken.None); }
        Assert.False(ShopOpenConsumer.Stores.ContainsKey(consumerId));

        var missing = new PluginHost(rows.Where(row => row.Id != "persistence").ToArray(), catalog, NullLoggerFactory.Instance);
        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => missing.StartAsync(CancellationToken.None).AsTask());
            Assert.Contains("IShopSessionStore", error.Message);
            Assert.DoesNotContain(missing.Instances, row => row.State == PluginState.Active);
        }
        finally { await missing.ShutdownAsync(CancellationToken.None); }
    }

    [Plugin("test.shop-open-consumer", Requires = [typeof(IShopSessionStore)])]
    public sealed class ShopOpenConsumer : IPlugin
    {
        public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, IShopSessionStore> Stores = new();

        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
        {
            Stores[context.Row.Id] = context.Services.Get<IShopSessionStore>();
            context.Effect(() => Stores.TryRemove(context.Row.Id, out _));
            return ValueTask.CompletedTask;
        }
    }
}

