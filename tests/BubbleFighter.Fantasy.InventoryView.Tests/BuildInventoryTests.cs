using System.Text;
using System.Xml.Linq;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Persistence.Memory;
using BubbleFighter.Fantasy.Fighters.Data;
using BubbleFighter.Fantasy.Tests;
using Xunit;

namespace BubbleFighter.Fantasy.InventoryView.Tests;

public sealed class BuildInventoryTests
{
    [Theory]
    [Trait("Category", "LocalClientData")]
    [InlineData("kr-1256", true)]
    [InlineData("cn-279", false)]
    [InlineData("cn-406", false)]
    public void NativeCatalogExcludesRheaFromTheTwoReportedOlderBuilds(string build, bool supportsRhea)
    {
        var data = new Data(new Dictionary<string, byte[]>
        {
            ["Config/ShopItem2.xml"] = LocalClientData.Bytes(build, "Config/ShopItem2.xml"),
            ["Config/FighterConfig.xml"] = LocalClientData.Bytes(build, "Config/FighterConfig.xml"),
        });
        var scope = InventoryLoadoutsPlugin.LoadScope(new() { BuildKey = build }, data);
        Assert.Equal(supportsRhea, scope.Supports(300029));
        Assert.All(new[] { 300001, 201001, 202001, 207001 }, id => Assert.True(scope.Supports(id), $"{build} lacks default {id}."));
    }

    [Fact]
    public void CharacterMustExistInBothTheShopCatalogAndTheModelFields()
    {
        var data = new Data(new Dictionary<string, byte[]>
        {
            ["Config/ShopItem2.xml"] = Workbook("ShopItem", [["itemId"], ["300001"], ["300029"], ["202001"]]),
            ["Config/FighterConfig.xml"] = Workbook("FighterConfig", [["[Field]", "", "1", "29"], ["name", "", "Dao", ""]]),
        });
        var scope = InventoryLoadoutsPlugin.LoadScope(new() { BuildKey = "cn-279" }, data);
        Assert.True(scope.Supports(300001));
        Assert.True(scope.Supports(202001));
        Assert.False(scope.Supports(300029));
        Assert.False(scope.Supports(999999));
    }

    [Fact]
    public async Task ProjectedBagAndPresetInterfacesKeepSharedEntitlementsAndBuildSelectionsSeparate()
    {
        var store = new MemoryInventoryRepository(new MemoryPlayerTable(4));
        InventorySlotRecord[] rows =
        [
            new(1, InventoryBag.CharacterBag, 1, 300001, 1, 128),
            new(1, InventoryBag.CharacterBag, 2, 300029, 1, 129),
            new(1, InventoryBag.CharacterBag, 3, 202001, 5, 129),
        ];
        await store.ReplaceSlotsAsync(1, InventoryBag.CharacterBag, rows, default);
        var modern = new BuildInventory(store, new("kr-1256", [300001, 300029, 202001]));
        var older = new BuildInventory(store, new("cn-406", [300001, 202001]));
        await modern.UpdateWornSelectionAsync(1, [new(300001, 300031, 300001, true)], null, false, default);
        await older.UpdateWornSelectionAsync(1, [new(300001, 300031, 300001, true)], null, false, default);
        var old = await older.ListSlotsAsync(1, InventoryBag.CharacterBag, InventoryLimits.MaxInventorySlotsPerBag, default);
        Assert.DoesNotContain(old, row => row.ItemId == 300029);
        Assert.Equal(129, old.Single(row => row.ItemId == 300001).Flags);
        Assert.Equal(5, old.Single(row => row.ItemId == 202001).Quantity);
        Assert.Equal(rows, await store.ListSlotsAsync(1, InventoryBag.CharacterBag, InventoryLimits.MaxInventorySlotsPerBag, default));
        await modern.SetActiveWeaponPresetSlotAsync(1, 4, default);
        await older.SetActiveWeaponPresetSlotAsync(1, 2, default);
        Assert.Equal(4, await modern.GetActiveWeaponPresetSlotAsync(1, default));
        Assert.Equal(2, await older.GetActiveWeaponPresetSlotAsync(1, default));
        Assert.Contains(await modern.ListSlotsAsync(1, InventoryBag.CharacterBag, InventoryLimits.MaxInventorySlotsPerBag, default),
            row => row.ItemId == 300029 && (row.Flags & 1) != 0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("CN-279")]
    [InlineData("../cn-279")]
    [InlineData("cn 279")]
    public void InvalidBuildKeysCannotAddressStorage(string key) =>
        Assert.Throws<ArgumentException>(() => new InventoryLoadoutScope(key, [300001]));

    [Fact]
    public async Task OptionalFighterRosterUsesTheBuildSelectionForBothReadsAndWrites()
    {
        var table = new MemoryPlayerTable(4);
        var store = new MemoryInventoryRepository(table);
        await store.ReplaceSlotsAsync(1, InventoryBag.CharacterBag,
            [new(1, InventoryBag.CharacterBag, 1, 300001, 1, 1), new(1, InventoryBag.CharacterBag, 2, 300029, 1, 0)], default);
        var fighters = new MemoryFighterRepository(table);
        var krInventory = new BuildInventory(store, new("kr-1256", [300001, 300029]));
        var cnInventory = new BuildInventory(store, new("cn-279", [300001]));
        var krCatalog = new FighterCatalog("test", [new(1, "Dao", 1, true, 300001), new(29, "Rhea", 29, true, 300029)]);
        var cnCatalog = new FighterCatalog("test", [new(1, "Dao", 1, true, 300001)]);
        var kr = new StoreBackedFighterRoster(krInventory, fighters, krCatalog, new CharacterSelection(krInventory));
        var cn = new StoreBackedFighterRoster(cnInventory, fighters, cnCatalog, new CharacterSelection(cnInventory));
        await kr.EquipAsync(1, 300029, default);
        await cn.EquipAsync(1, 300001, default);
        Assert.Equal(300029, await kr.GetEquippedAsync(1, default));
        Assert.Equal(300001, await cn.GetEquippedAsync(1, default));
        Assert.Empty(await fighters.ListByUserNumAsync(1, default));
        Assert.Equal(1, (await store.ListSlotsAsync(1, InventoryBag.CharacterBag, 10, default))[0].Flags);
    }

    private sealed class CharacterSelection(BuildInventory inventory) : IEquipmentSelection
    {
        public async ValueTask<IReadOnlyList<InventorySlotRecord>> ReadAsync(long userNum, CancellationToken ct) =>
            await inventory.UpdateWornSelectionAsync(userNum, [new(300001, 300031, 300001, true)], null, false, ct)
                ?? throw new InvalidOperationException("Missing character selection.");

        public ValueTask<IReadOnlyList<InventorySlotRecord>?> ApplyAsync(long userNum, int itemId, bool worn, CancellationToken ct) =>
            inventory.UpdateWornSelectionAsync(userNum, [new(300001, 300031, 300001, true)], itemId, worn, ct);
    }

    private static byte[] Workbook(string name, string[][] rows)
    {
        XNamespace ns = "urn:schemas-microsoft-com:office:spreadsheet";
        return Encoding.UTF8.GetBytes(new XDocument(new XElement(ns + "Workbook",
            new XElement(ns + "Worksheet", new XAttribute(ns + "Name", name), new XElement(ns + "Table",
                rows.Select(row => new XElement(ns + "Row", row.Select(cell => new XElement(ns + "Cell",
                    new XElement(ns + "Data", cell))))))))).ToString());
    }

    private sealed class Data(Dictionary<string, byte[]> files) : IClientDataStore
    {
        public bool Exists(string path) => files.ContainsKey(path);
        public byte[] Read(string path) => files[path];
    }
}
