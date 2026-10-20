using BubbleFighter.Fantasy.Tests;
using BubbleFighter.Fantasy.Fighters.Data;
using Xunit;

namespace BubbleFighter.Fantasy.Fighters.Tests;

public sealed class FighterCatalogTests
{
    [Fact]
    public void EmptyContentFailsLoud()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            FighterCatalogLoader.Load(ReadOnlyMemory<byte>.Empty, "Config/FighterConfig.xml", 1_000_000));
        Assert.Contains("empty", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OversizedContentFailsLoud()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            FighterCatalogLoader.Load(new byte[64], "Config/FighterConfig.xml", 32));
        Assert.Contains("max_fighter_config_bytes", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptySourceFailsLoud()
    {
        Assert.Throws<InvalidOperationException>(() =>
            FighterCatalogLoader.Load(new byte[] { 1 }, " ", 1_000_000));
    }

    [Fact]
    [Trait("Category", "LocalClientData")]
    public void ClientHorzTableReturnsEveryFieldColumnWhenPresent()
    {
        var bytes = LocalClientData.Bytes("kr-1256", "Config/FighterConfig.xml");

        var catalog = FighterCatalogLoader.Load(bytes, "Config/FighterConfig.xml", 134_217_728);
        Assert.True(catalog.All.Count > catalog.Playable.Count, "costume / NPC Field columns sit outside 1..31");
        Assert.Equal(23, catalog.Playable.Count);
        Assert.Equal(300001, catalog.RequireField(1).ItemId);
        Assert.Equal("Dao", catalog.RequireField(1).Name);
        Assert.Equal(300002, catalog.RequireField(2).ItemId);
        Assert.Equal("Dizni", catalog.RequireField(2).Name);
        Assert.Null(catalog.Find(17));
        Assert.Null(catalog.Find(300017));
    }
}
