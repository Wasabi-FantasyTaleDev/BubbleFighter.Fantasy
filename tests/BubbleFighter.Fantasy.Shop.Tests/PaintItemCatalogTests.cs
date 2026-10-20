using BubbleFighter.Fantasy.Shop;
using System.Text;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Kernel;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Shop.Tests;

/// <summary>Checks native item-function selection without remapping colors by item identity.</summary>
public sealed class PaintItemCatalogTests
{
    /// <summary>The catalog row reads sparse native workbook cells and releases its service when unloaded.</summary>
    [Fact]
    public async Task MountReadsNativeDataAndUnwindsItsRegistration()
    {
        var services = new ServiceRegistry();
        using var source = services.Register<IClientDataStore>(new Data());
        var context = new PluginContext(new PluginRow { Id = "paint-catalog", Plugin = "inventory.paint-catalog", Source = "test" },
            services, new EventBus(NullLogger.Instance), NullLogger.Instance);
        try
        {
            await new PaintItemCatalogPlugin().ActivateAsync(context, default);
            Assert.True(services.Get<IPaintItemCatalog>().TryGetColorIndex(100180, out var color));
            Assert.Equal((ushort)14, color);
        }
        finally { await context.UnwindAsync(); }
        Assert.False(services.TryGet<IPaintItemCatalog>(out _));
    }

    /// <summary>Only function 1 in the native Spray sub-category maps to color.</summary>
    [Fact]
    public void ColorComesFromNativeParameterAndNotItemNumber()
    {
        var catalog = PaintItemCatalog.Parse([
            new[] { "parameter", "itemId", "function", "subCategory" },
            new[] { "14", "100180", "1", "Spray" },
            new[] { "1", "100064", "1", "Spray" },
            new[] { "rename", "100104", "3", "All_Etc" },
            new[] { "1", "100578", "0", "Spray" },
        ], 8);
        Assert.True(catalog.TryGetColorIndex(100180, out var gray));
        Assert.Equal((ushort)14, gray);
        Assert.True(catalog.TryGetColorIndex(100064, out var blue));
        Assert.Equal((ushort)1, blue);
        Assert.False(catalog.TryGetColorIndex(100104, out _));
        Assert.False(catalog.TryGetColorIndex(100578, out _));
    }

    /// <summary>Malformed colors and duplicate identities fail rather than silently replacing a definition.</summary>
    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("-1")]
    [InlineData("blue")]
    public void InvalidColorFails(string color) => Assert.Throws<InvalidOperationException>(() =>
        PaintItemCatalog.Parse([new[] { "itemId", "function", "parameter" }, new[] { "100180", "1", color }], 8));

    /// <summary>Headers and capacity are enforced, and duplicate definitions are not accepted.</summary>
    [Fact]
    public void MissingColumnsDuplicatesAndCapacityFail()
    {
        Assert.Throws<InvalidOperationException>(() => PaintItemCatalog.Parse([new[] { "itemId", "function", "parameter" }], 8));
        IReadOnlyList<IReadOnlyList<string>> duplicate = [new[] { "itemId", "function", "parameter", "subCategory" },
            new[] { "100180", "1", "14", "Spray" }, new[] { "100180", "1", "8", "Spray" }];
        Assert.Throws<InvalidOperationException>(() => PaintItemCatalog.Parse(duplicate, 8));
        Assert.Throws<InvalidOperationException>(() => PaintItemCatalog.Parse(duplicate, 1));
    }

    private sealed class Data : IClientDataStore
    {
        public bool Exists(string path) => path == "Config/ShopItem2.xml";
        public byte[] Read(string path)
        {
            Assert.True(Exists(path));
            return Encoding.UTF8.GetBytes("""
                <Workbook xmlns="urn:schemas-microsoft-com:office:spreadsheet" xmlns:ss="urn:schemas-microsoft-com:office:spreadsheet">
                  <Worksheet ss:Name="ShopItem"><Table>
                    <Row><Cell><Data ss:Type="String">itemId</Data></Cell><Cell ss:Index="3"><Data ss:Type="String">function</Data></Cell><Cell><Data ss:Type="String">parameter</Data></Cell><Cell><Data ss:Type="String">subCategory</Data></Cell></Row>
                    <Row><Cell><Data ss:Type="Number">100180</Data></Cell><Cell ss:Index="3"><Data ss:Type="Number">1</Data></Cell><Cell><Data ss:Type="Number">14</Data></Cell><Cell><Data ss:Type="String">Spray</Data></Cell></Row>
                  </Table></Worksheet>
                </Workbook>
                """);
        }
    }
}
