using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Tests;
using Xunit;

namespace BubbleFighter.Fantasy.ScienceLab.Tests;

/// <summary>Loads the deployed client's original catalog and assistant definitions without synthetic worksheet assumptions.</summary>
[Trait("Category", "LocalClientData")]
public sealed class NativeLaboratoryCatalogTests
{
    /// <summary>The original KR812 archives support a fully specified synthesis policy within normal parsing budgets.</summary>
    [Fact]
    public void NativeCatalogAndAssistantsAcceptSynthesisPolicy()
    {
        var options = new ScienceLabOptions
        {
            Enabled = true, BuildKey = "kr-812", SynthesisCardItemId = 100622,
            CardQuantity = 1, SynthesisSurvivor = "first", SynthesisMaximumDays = 729,
            SynthesisPermanentAtDays = 730,
        };
        var catalog = LaboratoryCatalog.Load(new NativeStore(), options);
        Assert.True(catalog.Items.Count > 1000);
        Assert.Equal("count", catalog.Items[100622].DurationType);
        Assert.True(catalog.Items[100622].Available);
        catalog.RequirePolicy(options, new InventoryLoadoutScope("kr-812", catalog.Items.Keys.ToArray()));
    }

    private sealed class NativeStore : IClientDataStore
    {
        public bool Exists(string path) => path is "Config/ShopItem2.xml" or "Config/SynthesisSystem.ini";
        public byte[] Read(string path) => LocalClientData.Bytes("kr-812", path);
    }
}
