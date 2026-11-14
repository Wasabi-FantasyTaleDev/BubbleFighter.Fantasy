using System.Security;
using System.Text;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.InGame.Data;
using BubbleFighter.Fantasy.Tests;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>Checks native mode allowlists, values, fixed options, conditional exclusions and bounded XML parsing.</summary>
public sealed class CustomGameRulesCatalogTests
{
    /// <summary>Mode membership is explicit, and mode-specific UI defaults are distinct from FirstChanges wire rows.</summary>
    [Fact]
    public void ModeDefaultsAndForcedFirstChangesRemainSeparate()
    {
        var catalog = Load(Sample());
        Assert.Equal(new[] { 1, 8, 30, 37 }, catalog.Modes.Keys.Order());
        Assert.Empty(catalog.InitialValues(1));
        Assert.Empty(catalog.InitialValues(30));
        Assert.Equal(new uint[] { 1 }, catalog.Modes[30].Rules[20].InitialValues);
        Assert.Equal(new ushort[] { 31, 32 }, catalog.InitialValues(37).Select(row => row.ValueId));
        catalog.Validate(1, []);
        catalog.Validate(37, catalog.InitialValues(37));
        Assert.Throws<ArgumentException>(() => catalog.Validate(37, []));
        Assert.Throws<ArgumentException>(() => catalog.Validate(43, []));
        Assert.Throws<ArgumentException>(() => catalog.Validate(8, [Value(12, 2)]));
    }

    /// <summary>Native selector domains, min/max/step, uniqueness and dependencies reject invalid requests.</summary>
    [Fact]
    public void EnforcesDomainsAndDependencies()
    {
        var catalog = Load(Sample());
        catalog.Validate(1, [Value(1, 150), Value(12, 2, 4), Value(31, 1), Value(32, 0)]);
        foreach (var values in new IReadOnlyList<RoomCustomRuleValue>[]
                 {
                     [Value(1, 49)], [Value(1, 151)], [Value(1, uint.MaxValue)], [Value(1)], [Value(1, 100, 150)],
                     [Value(1, 100), Value(1, 110)], [Value(12, 1)], [Value(12, 2, 3, 4)], [Value(12, 2, 2)],
                     [Value(13, 0, 1, 2, 3)], [Value(17, 0, 1)], [Value(31, 2)], [Value(32, 1)], [Value(65535, 1)],
                 })
            Assert.Throws<ArgumentException>(() => catalog.Validate(1, values));
    }

    /// <summary>Slot restrictions conflict only with their corresponding weapon family, and zero grenade indices remain active.</summary>
    [Fact]
    public void ValidatesConditionalNativeExclusions()
    {
        var catalog = Load(Sample());
        catalog.Validate(1, [Value(12, 2), Value(13, 1)]);
        catalog.Validate(1, [Value(13, 0), Value(16, 1)]);
        catalog.Validate(1, [Value(13, 2), Value(17, 0)]);
        catalog.Validate(30, [Value(20, 0), Value(23, 1)]);
        foreach (var values in new IReadOnlyList<RoomCustomRuleValue>[]
                 {
                     [Value(1, 150), Value(3, 150)], [Value(12, 2), Value(13, 0)],
                     [Value(13, 3), Value(16, 1)], [Value(13, 3), Value(17, 0)], [Value(16, 1), Value(17, 0)],
                 })
            Assert.Throws<ArgumentException>(() => catalog.Validate(1, values));
        Assert.Throws<ArgumentException>(() => catalog.Validate(30, [Value(23, 1)]));
    }

    /// <summary>Unchanged controls cannot create presence-based gameplay effects, while the native preset slider still returns zero.</summary>
    [Fact]
    public void UnchangedControlsAreOmittedExceptForAlwaysCurrentSliders()
    {
        var catalog = Load(Sample());
        foreach (var row in new[] { Value(1, 100), Value(16, 0), Value(20, 0), Value(31, 0) })
            Assert.Throws<ArgumentException>(() => catalog.Validate(1, [row]));
        Assert.Throws<ArgumentException>(() => catalog.Validate(30, [Value(20, 1)]));
        catalog.Validate(30, [Value(20, 0)]);
        catalog.Validate(1, [Value(31, 1), Value(32, 0)]);
    }

    /// <summary>Malformed native configuration fails during loading, before any room receives a rule catalogue.</summary>
    [Theory]
    [InlineData("SpeedRed", "UnknownRule")]
    [InlineData("50,200,10", "50,200,0")]
    [InlineData("FALSE,FALSE,FALSE", "FALSE,FALSE")]
    [InlineData("wgtPvp", "wgtMissing")]
    [InlineData("TRUE", "perhaps")]
    public void RejectsMalformedDefinitions(string before, string after) =>
        Assert.Throws<InvalidDataException>(() => Load(Sample().Replace(before, after, StringComparison.Ordinal)));

    /// <summary>Byte, row, sparse-column and cell limits are checked before allocating attacker-selected containers.</summary>
    [Fact]
    public void BoundsWorkbookResources()
    {
        var source = Sample();
        Assert.Throws<InvalidDataException>(() => Load(source, maxBytes: 100));
        Assert.Throws<InvalidDataException>(() => Load(source, maxRows: 10));
        Assert.Throws<InvalidDataException>(() => Load(source, maxColumns: 4));
        Assert.Throws<InvalidDataException>(() => Load(source, maxCellCharacters: 3));
        Assert.Throws<InvalidDataException>(() => Load(source.Replace("<Row>", "<Row ss:Index='2147483647'>", StringComparison.Ordinal)));
        Assert.Throws<InvalidDataException>(() => Load(source.Replace("<Cell>", "<Cell ss:Index='2147483647'>", StringComparison.Ordinal)));
    }

    /// <summary>Sparse padding counts against the shared row limit across all selected worksheets.</summary>
    [Fact]
    public void SparsePaddingSharesTheSelectedWorkbookRowBudget()
    {
        var row = "<Row ss:Index='3'><Cell><Data ss:Type='String'>value</Data></Cell></Row>";
        var xml = "<Workbook xmlns='urn:schemas-microsoft-com:office:spreadsheet' xmlns:ss='urn:schemas-microsoft-com:office:spreadsheet'>"
            + Sheet("first", [row]) + Sheet("second", [row]) + "</Workbook>";
        var bytes = Encoding.UTF8.GetBytes(xml);
        Assert.Throws<InvalidDataException>(() => CustomGameRulesWorkbook.Read(bytes, "rules.xml", 5, 4, 32, ["first", "second"]));
        var sheets = CustomGameRulesWorkbook.Read(bytes, "rules.xml", 6, 4, 32, ["first", "second"]);
        Assert.Equal(3, sheets["first"].Count);
        Assert.Equal(3, sheets["second"].Count);
    }

    /// <summary>The actual resource enables only its eleven mode columns and retains native ranges and fixed top-view defaults.</summary>
    [Fact]
    [Trait("Category", "LocalClientData")]
    public void LoadsNativeKoreanWorkbook()
    {
        var catalog = CustomGameRulesCatalog.Load(new BytesData(LocalClientData.Bytes("kr-1256", "Config/CustomGameMode.xml")),
            "Config/CustomGameMode.xml", 8 * 1024 * 1024, 4096, 64, 8192);
        Assert.Equal(new[] { 1, 3, 8, 11, 13, 28, 30, 32, 37, 38, 39 }, catalog.Modes.Keys.Order());
        Assert.Equal(50u, catalog.Modes[8].Rules[4].Minimum);
        Assert.Equal(200u, catalog.Modes[8].Rules[4].Maximum);
        Assert.Equal(new uint[] { 100 }, catalog.Modes[8].Rules[22].InitialValues);
        Assert.Equal(new ushort[] { 31, 32 }, catalog.InitialValues(37).Select(row => row.ValueId));
        Assert.Equal(new ushort[] { 31, 32 }, catalog.InitialValues(11).Select(row => row.ValueId));
        Assert.DoesNotContain(43, catalog.Modes.Keys);
        foreach (var mode in catalog.Modes.Keys) catalog.Validate(mode, catalog.InitialValues(mode));
    }

    private static RoomCustomRuleValue Value(ushort id, params uint[] values) => new(id, values);
    private static CustomGameRulesCatalog Load(string xml, int maxBytes = 1024 * 1024, int maxRows = 1024,
        int maxColumns = 64, int maxCellCharacters = 4096) => CustomGameRulesCatalog.Load(new BytesData(Encoding.UTF8.GetBytes(xml)),
        "Config/CustomGameMode.xml", maxBytes, maxRows, maxColumns, maxCellCharacters);

    internal static string Sample()
    {
        string[] names = ["SpeedRed", "SpeedAll", "PrimaryWeaponRestrict", "WeaponSlotRestrict", "GrenadeUnlimit",
            "GrenadeRestrict", "BurstPang", "SuperPang", "TopView", "TopViewSpecPreset"];
        string[][] controls =
        [
            ["SpeedRed", "1", "Slide", "50,200,10", "100", "SpeedAll", "", "0"],
            ["SpeedAll", "1", "Slide", "50,200,10", "100", "SpeedRed", "", "0"],
            ["PrimaryWeaponRestrict", "1", "CheckBox", "rifle,shotgun,sniper", "FALSE,FALSE,FALSE", "WeaponSlotRestrict", "", "0"],
            ["WeaponSlotRestrict", "1", "CheckBox", "primary,pistol,hammer,grenade", "FALSE,FALSE,FALSE,FALSE", "PrimaryWeaponRestrict,GrenadeUnlimit,GrenadeRestrict", "", "0"],
            ["GrenadeUnlimit", "1", "CheckBox", "unlimited", "FALSE", "GrenadeRestrict,WeaponSlotRestrict", "", "0"],
            ["GrenadeRestrict", "1", "CheckBox", "one,two", "FALSE,FALSE", "GrenadeUnlimit,WeaponSlotRestrict", "", "0"],
            ["BurstPang", "1", "CheckBox", "burst", "FALSE", "SuperPang", "", "0"],
            ["SuperPang", "1", "CheckBox", "super", "FALSE", "BurstPang", "", "0"],
            ["TopView", "1", "CheckBox", "top", "FALSE", "", "", "0"],
            ["TopViewSpecPreset", "1", "Slide", "0,1,1", "0", "", "TopView", "1"],
        ];
        var common = Sheet("CustomGameCommon", [Row("CustomGameCommon:HorzTable"), Row("description"),
            Row("ValueId", "OnOff", "SetKind", "SetValues", "InitValues", "ExclusiveValues", "DependenceValues", "AlwaysReturnCurrent"),
            .. controls.Select(row => Row(row))]);
        var allowed = Sheet("CustomGameWgt", [Row("CustomGameWgt:HorzTable"), Row("EntryId", "Display", "wgtPvp", "wgtZombie", "wgtFinalZone", "wgtPsionic"),
            .. names.Select(name => Row(name, name, "1", name is "SpeedRed" or "SpeedAll" ? "1" : "0", "1", "1"))]);
        var initial = Sheet("WgtInitValues", [Row("CustomGameWgt:HorzTable"), Row("EntryId", "Display", "wgtFinalZone"),
            .. names.Select(name => Row(name, name, name == "BurstPang" ? "TRUE" : ""))]);
        var fixedValues = Sheet("Nochangables", [Row("CustomGameWgt:HorzTable"), Row("EntryId", "Display", "wgtPsionic"),
            .. names.Select(name => Row(name, name, name is "TopView" or "TopViewSpecPreset" ? "1" : "0"))]);
        var first = Sheet("FirstChanges", [Row("CustomGameWgt:HorzTable"), Row("EntryId", "Display", "wgtPsionic"),
            .. names.Select(name => Row(name, name, name is "TopView" or "TopViewSpecPreset" ? "1" : ""))]);
        return "<Workbook xmlns='urn:schemas-microsoft-com:office:spreadsheet' xmlns:ss='urn:schemas-microsoft-com:office:spreadsheet'>"
            + common + allowed + initial + fixedValues + first + "</Workbook>";
    }

    private static string Row(params string[] values) => "<Row>" + string.Concat(values.Select(value =>
        "<Cell><Data ss:Type='String'>" + SecurityElement.Escape(value) + "</Data></Cell>")) + "</Row>";
    private static string Sheet(string name, string[] rows) => "<Worksheet ss:Name='" + name + "'><Table>"
        + string.Concat(rows) + "</Table></Worksheet>";
    private sealed class BytesData(byte[] bytes) : IClientDataStore
    {
        public bool Exists(string path) => true;
        public byte[] Read(string path) => bytes;
    }
}
