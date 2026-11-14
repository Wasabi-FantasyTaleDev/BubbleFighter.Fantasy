using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.ItemEnhancement.Data;
using BubbleFighter.Fantasy.Tests;
using Xunit;

namespace BubbleFighter.Fantasy.ItemEnhancement.Tests;

/// <summary>Checks modification material eligibility against the original KR1256 workbook.</summary>
[Trait("Category", "LocalClientData")]
public sealed class NativeModificationCatalogTests
{
    /// <summary>All 24 native parts lack the target flag, and a real part accepts a complete synthetic spending and outcome policy.</summary>
    [Fact]
    public void NativePartsWithoutTargetFlagAcceptExplicitSyntheticPolicy()
    {
        var limits = new EnhancementArticleOptions();
        var catalog = EnhancementCatalog.Load(new NativeStore(), limits.CatalogPath, limits.MaxWorkbookBytes,
            limits.MaxCatalogRows, limits.MaxCatalogColumns, limits.MaxCellCharacters);
        var parts = Enumerable.Range(102461, 24).Select(id => catalog.Items[id]).ToArray();
        Assert.All(parts, part =>
        {
            Assert.Equal("SienceLab", part.SubCategory);
            Assert.Equal("count", part.DurationType);
            Assert.False(part.IsModificatable);
            Assert.False(part.RequiresArticle);
        });

        var selectors = Enumerable.Range(101, 7).ToArray();
        // Prices, consumption, effect values and weights below are synthetic test choices, not native server policy.
        var options = new ItemModificationOptions
        {
            Enabled = true, PartQuantity = 1, ApplyLucciCost = 0, DeleteLucciCost = 0,
            Parts =
            [
                new()
                {
                    ItemId = 102461, NumericTag = 0, EffectCategory = 1, AttributeIds = selectors, Value0 = 1,
                    NegativeValues = selectors.Select(id => new ModificationNegativeOptions { AttributeId = id, Value = -1 }).ToArray(),
                },
            ],
            TagWeights = [new() { Key = 0, Weights = [1, 1, 1] }],
            EffectWeights = [new() { Key = 1, Weights = [1, 1, 1] }],
        };
        var policy = new ModificationPolicy(options, catalog, new InventoryLoadoutScope("kr-1256", catalog.Items.Keys.ToArray()));

        Assert.True(policy.HasPart(102461));
        Assert.Equal(selectors, Assert.Single(policy.Configuration.Parts).AttributeIds);
    }

    private sealed class NativeStore : IClientDataStore
    {
        public bool Exists(string path) => path == "Config/ShopItem2.xml";
        public byte[] Read(string path) => LocalClientData.Bytes("kr-1256", path);
    }
}
