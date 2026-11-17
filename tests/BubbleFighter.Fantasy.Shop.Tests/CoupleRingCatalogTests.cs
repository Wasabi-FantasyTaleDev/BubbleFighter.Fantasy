using System.Text;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.Contracts.Couples;
using BubbleFighter.Fantasy.Kernel;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Shop.Tests;

/// <summary>Checks native invitation classification and stock-based display-ring resolution.</summary>
public sealed class CoupleRingCatalogTests
{
    /// <summary>Sparse workbook cells and independent sheet column orders resolve through the catalog registration.</summary>
    [Fact]
    public async Task MountReadsBothSheetsAndUnwindsItsRegistration()
    {
        var services = new ServiceRegistry();
        using var source = services.Register<IClientDataStore>(new Data());
        var context = new PluginContext(new PluginRow { Id = "couple-catalog", Plugin = "inventory.couple-catalog", Source = "test" },
            services, new EventBus(NullLogger.Instance), NullLogger.Instance);
        try
        {
            await new CoupleRingCatalogPlugin().ActivateAsync(context, default);
            Assert.True(services.Get<ICoupleRingCatalog>().TryGetDisplayRing(1234, out var display));
            Assert.Equal(5678, display);
        }
        finally { await context.UnwindAsync(); }
        Assert.False(services.TryGet<ICoupleRingCatalog>(out _));
    }

    /// <summary>The build's stock parameter determines its display ring, with no identity arithmetic or cross-build constants.</summary>
    [Fact]
    public void OnlyNativeInvitationFunctionsResolveThroughTheirOwnStock()
    {
        var first = CoupleRingCatalog.Parse(Items("99"), Stocks("5678"), 8);
        var second = CoupleRingCatalog.Parse(Items("99"), Stocks("9876"), 8);
        Assert.True(first.TryGetDisplayRing(1234, out var a));
        Assert.True(second.TryGetDisplayRing(1234, out var b));
        Assert.Equal(5678, a);
        Assert.Equal(9876, b);
        Assert.False(first.TryGetDisplayRing(5555, out _));
        Assert.False(first.TryGetDisplayRing(5678, out _));
    }

    /// <summary>Malformed or missing referenced stock data cannot silently turn an arbitrary item into a ring.</summary>
    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("not-a-stock")]
    [InlineData("100")]
    public void InvalidInvitationStockFails(string parameter) => Assert.Throws<InvalidOperationException>(() =>
        CoupleRingCatalog.Parse(Items(parameter), Stocks("5678"), 8));

    /// <summary>A referenced stock must identify a positive display item.</summary>
    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("ring")]
    [InlineData("9999")]
    public void InvalidDisplayRingFails(string display) => Assert.Throws<InvalidOperationException>(() =>
        CoupleRingCatalog.Parse(Items("99"), Stocks(display), 8));

    /// <summary>Ambiguous columns, repeated definitions and allocation limits are rejected.</summary>
    [Fact]
    public void InvalidHeadersDuplicatesAndCapacityFail()
    {
        var items = Items("99");
        var stocks = Stocks("5678");
        Assert.Throws<InvalidOperationException>(() => CoupleRingCatalog.Parse([], stocks, 8));
        Assert.Throws<InvalidOperationException>(() => CoupleRingCatalog.Parse([new[] { "itemId", "function" }], stocks, 8));
        Assert.Throws<InvalidOperationException>(() => CoupleRingCatalog.Parse(items, [new[] { "stockId", "itemId", "itemId" }], 8));
        Assert.Throws<InvalidOperationException>(() => CoupleRingCatalog.Parse([.. items, items[1]], stocks, 8));
        Assert.Throws<InvalidOperationException>(() => CoupleRingCatalog.Parse(items, [.. stocks, stocks[1]], 8));
        Assert.Throws<InvalidOperationException>(() => CoupleRingCatalog.Parse([.. items, new[] { "17", "1235", "99" }], stocks, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => CoupleRingCatalog.Parse(items, stocks, 0));
    }

    private static IReadOnlyList<IReadOnlyList<string>> Items(string parameter) =>
        [new[] { "function", "itemId", "parameter" }, new[] { "17", "1234", parameter }, new[] { "0", "5555", "99" },
            new[] { "0", "5678", "" }, new[] { "0", "9876", "" }];

    /// <summary>An explicit native data omission preserves invitation identity without inventing a display item.</summary>
    [Fact]
    public void OnlyTheDocumentedMissingStockCanHaveNoDisplayRing()
    {
        Assert.Throws<InvalidOperationException>(() => CoupleRingCatalog.Parse(Items("100"), Stocks("5678"), 8));
        var omitted = CoupleRingCatalog.Parse(Items("100"), Stocks("5678"), 8, [100]);
        Assert.True(omitted.TryGetDisplayRing(1234, out var ring));
        Assert.Equal(0, ring);
        Assert.False(omitted.TryGetDisplayRing(5555, out _));
        Assert.Throws<InvalidOperationException>(() => CoupleRingCatalog.Parse(Items("101"), Stocks("5678"), 8, [100]));
        Assert.Throws<InvalidOperationException>(() => CoupleRingCatalog.Parse(Items("99"), Stocks("bad"), 8, [99]));
        var present = CoupleRingCatalog.Parse(Items("99"), Stocks("5678"), 8, [99]);
        Assert.True(present.TryGetDisplayRing(1234, out var restored));
        Assert.Equal(5678, restored);
    }

    /// <summary>The omission list is bounded and never accepts invalid or ambiguous identities.</summary>
    [Fact]
    public void MissingStockConfigurationIsValidated()
    {
        foreach (int[] invalid in new[] { new[] { 0 }, new[] { -1 }, new[] { 99, 99 }, Enumerable.Range(1, 257).ToArray() })
            Assert.Throws<InvalidOperationException>(() => CoupleRingCatalog.Parse(Items("99"), Stocks("5678"), 8, invalid));
    }

    private static IReadOnlyList<IReadOnlyList<string>> Stocks(string display) =>
        [new[] { "itemId", "stockId" }, new[] { display, "99" }, new[] { "1111", "22" }];

    private sealed class Data : IClientDataStore
    {
        /// <summary>Exposes only the native fixture workbook path.</summary>
        public bool Exists(string path) => path == "Config/ShopItem2.xml";

        /// <summary>Returns a fixture with sparse cells and a display identity distinct from the invitation identity.</summary>
        public byte[] Read(string path)
        {
            Assert.True(Exists(path));
            return Encoding.UTF8.GetBytes("""
                <Workbook xmlns="urn:schemas-microsoft-com:office:spreadsheet" xmlns:ss="urn:schemas-microsoft-com:office:spreadsheet">
                  <Worksheet ss:Name="ShopItem"><Table>
                    <Row><Cell><Data ss:Type="String">itemId</Data></Cell><Cell ss:Index="3"><Data ss:Type="String">function</Data></Cell><Cell><Data ss:Type="String">parameter</Data></Cell></Row>
                    <Row><Cell><Data ss:Type="Number">1234</Data></Cell><Cell ss:Index="3"><Data ss:Type="Number">17</Data></Cell><Cell><Data ss:Type="Number">99</Data></Cell></Row>
                    <Row><Cell><Data ss:Type="Number">5678</Data></Cell><Cell ss:Index="3"><Data ss:Type="Number">0</Data></Cell></Row>
                  </Table></Worksheet>
                  <Worksheet ss:Name="Stock"><Table>
                    <Row><Cell><Data ss:Type="String">itemId</Data></Cell><Cell ss:Index="3"><Data ss:Type="String">stockId</Data></Cell></Row>
                    <Row><Cell><Data ss:Type="Number">5678</Data></Cell><Cell ss:Index="3"><Data ss:Type="Number">99</Data></Cell></Row>
                  </Table></Worksheet>
                </Workbook>
                """);
        }
    }
}
