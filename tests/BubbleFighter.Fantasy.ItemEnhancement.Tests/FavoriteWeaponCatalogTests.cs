using System.Text;
using System.Xml.Linq;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.Contracts.Enhancement;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Party;
using BubbleFighter.Fantasy.ItemEnhancement.Data;
using BubbleFighter.Fantasy.Tests;
using Xunit;

namespace BubbleFighter.Fantasy.ItemEnhancement.Tests;

/// <summary>Checks the Favorite weapon workbook parse, grade derivation, awards and the native attribute 164 presentation.</summary>
public sealed class FavoriteWeaponCatalogTests
{
    /// <summary>Builds a SpreadsheetML workbook shaped like the client's FavoriteWeaponSystem.xml, with test-chosen values.</summary>
    internal static byte[] Workbook(string[][]? baseRows = null, string[][]? upgradeRows = null)
    {
        XNamespace ns = "urn:schemas-microsoft-com:office:spreadsheet";
        XElement Sheet(string name, params string[][] rows) => new(ns + "Worksheet", new XAttribute(ns + "Name", name),
            new XElement(ns + "Table", rows.Select(row => new XElement(ns + "Row", row.Select(cell =>
                new XElement(ns + "Cell", new XElement(ns + "Data", new XAttribute(ns + "Type", "String"), cell)))))));
        return Encoding.UTF8.GetBytes(new XElement(ns + "Workbook",
            Sheet("Base", baseRows ?? [["Key", "Value"], ["IncreasePointEnchant", "30"], ["IncreasePointModification", "B,6"],
                ["IncreasePointModification", "A,12"], ["IncreasePointModification", "S,24"], ["DecreasePoint", "50"]]),
            Sheet("Upgrade", upgradeRows ?? [["FavoriteGrade", "Point"], ["Normal", "120"], ["Expert", "420"], ["Special", "1260"], ["Master", "0"]])).ToString());
    }

    private static FavoriteWeaponCatalog Load(byte[] bytes) => FavoriteWeaponCatalog.Load(new Store(bytes), "Config/FavoriteWeaponSystem.xml", 1 << 20);

    /// <summary>The Modification points are keyed by the client's grade index S=0, A=1, B=2, not by file order.</summary>
    [Fact]
    public void ModificationPointsFollowTheClientGradeIndex()
    {
        string[][] baseRows = [["Key", "Value"], ["IncreasePointEnchant", "30"], ["IncreasePointModification", "B,6"],
            ["IncreasePointModification", "S,24"], ["IncreasePointModification", "A,12"]];
        Assert.Equal(new[] { 24, 12, 6 }, Load(Workbook(baseRows)).ModificationPoints);
    }

    /// <summary>The grade is derived from points at the exact Upgrade thresholds and never clamped above Master's entry.</summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(119, 0)]
    [InlineData(120, 1)]
    [InlineData(419, 1)]
    [InlineData(420, 2)]
    [InlineData(1259, 2)]
    [InlineData(1260, 3)]
    [InlineData(100000, 3)]
    public void GradeFollowsThePointThresholds(long points, int grade)
    {
        var catalog = Load(Workbook());
        Assert.Equal((FavoriteGrade)grade, catalog.GradeOf(points));
        Assert.Equal(30, catalog.EnchantPoints);
        Assert.Equal(new[] { 24, 12, 6 }, catalog.ModificationPoints);
    }

    /// <summary>Duplicate, missing, unknown, unordered and malformed rows are refused loudly.</summary>
    [Fact]
    public void MalformedSheetsAreRefused()
    {
        string[] header = ["Key", "Value"];
        string[][] good = [header, ["IncreasePointEnchant", "30"], ["IncreasePointModification", "B,6"], ["IncreasePointModification", "A,12"], ["IncreasePointModification", "S,24"]];
        string[][] up = [["FavoriteGrade", "Point"], ["Normal", "120"], ["Expert", "420"], ["Special", "1260"], ["Master", "0"]];
        Assert.Throws<InvalidDataException>(() => Load(Workbook([.. good, ["IncreasePointEnchant", "31"]])));
        Assert.Throws<InvalidDataException>(() => Load(Workbook(good[..^1])));
        Assert.Throws<InvalidDataException>(() => Load(Workbook(good.Where(row => row[0] != "IncreasePointEnchant").ToArray())));
        Assert.Throws<InvalidDataException>(() => Load(Workbook([.. good[..^1], ["IncreasePointModification", "C,24"]])));
        Assert.Throws<InvalidDataException>(() => Load(Workbook([.. good, ["IncreasePointModification", "S,25"]])));
        Assert.Throws<InvalidDataException>(() => Load(Workbook([.. good[..^1], ["IncreasePointModification", "S"]])));
        Assert.Throws<InvalidDataException>(() => Load(Workbook([.. good[..^1], ["IncreasePointModification", "S,0"]])));
        Assert.Throws<InvalidDataException>(() => Load(Workbook(good, [.. up, ["Expert", "421"]])));
        Assert.Throws<InvalidDataException>(() => Load(Workbook(good, up[..^1])));
        Assert.Throws<InvalidDataException>(() => Load(Workbook(good, [up[0], up[1], up[2], ["Special", "400"], up[4]])));
        Assert.Throws<InvalidDataException>(() => Load(Workbook(good, [up[0], up[1], up[2], up[3], ["Master", "9"]])));
        Assert.Throws<InvalidDataException>(() => Load(Workbook(good, [up[0], up[1], up[2], up[3], ["Legend", "0"]])));
    }

    /// <summary>A successful weapon enhancement awards the workbook points and presents attribute 164 as grade then points.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EnhancementAwardsAndPresentationShowsGradeAndPoints(bool success)
    {
        await using var fixture = await EnhancementPluginFixture.CreateAsync();
        Dictionary<string, object?> favorite = new() { ["favorite"] = new Dictionary<string, object?> { ["enabled"] = true } };
        Dictionary<string, object?> chance = new(favorite) { ["success_basis_points"] = success ? 10000 : 0, ["additive_success_basis_points"] = success ? 10000 : 0 };
        await new EnhancementArticlesPlugin().ActivateAsync(fixture.Articles(favorite), default);
        var initial = await fixture.PrepareAsync();
        var key = initial!.CharacterBag.Single(row => row.ItemId == 202020).ArticleIdentity!.ArticleKey;
        await new ItemEnhancementPlugin().ActivateAsync(fixture.Economy(chance), default);
        Assert.True(await fixture.Router.DispatchAsync(EnhancementPluginFixture.Protocol, fixture.Session,
            new LabOperationRequested(LabOperation.Enchant, [checked((long)key)], 0), default));
        var target = Assert.Single(fixture.Inventories).CharacterBag.Single(row => row.ItemId == 202020);
        Assert.Equal(success ? 30L : null, target.Favorites?.Find(fixture.Scope.Key));
        var presentation = await fixture.PresentationAsync(target);
        Assert.Equal(success, presentation.Attributes.Any(attribute => attribute.Kind == 164));
        if (!success) return;
        var entry = presentation.Attributes.Single(attribute => attribute.Kind == 164);
        Assert.Equal(202020, entry.ItemId);
        Assert.Equal(new uint[] { 0, 30 }, entry.Values);
        Assert.Equal(0u, entry.Unknown28);
        Assert.Equal("", entry.Unknown32);
        var expert = await fixture.PresentationAsync(target with { Favorites = ItemFavorites.Empty.With(fixture.Scope.Key, 120) });
        Assert.Equal(new uint[] { 1, 120 }, expert.Attributes.Single(attribute => attribute.Kind == 164).Values);
    }

    /// <summary>The grade notice names the weapon and both grades only when the award crosses a threshold, using the row as it stood before the award.</summary>
    [Theory]
    [InlineData(30, 30, true, 0, 1)]
    [InlineData(100, 30, false, 0, 0)]
    [InlineData(29, 30, false, 0, 0)]
    [InlineData(0, 30, false, 0, 0)]
    [InlineData(400, 30, true, 1, 2)]
    [InlineData(1240, 30, true, 2, 3)]
    [InlineData(5000, 30, false, 3, 3)]
    [InlineData(0, 0, false, 0, 0)]
    public async Task GradeNoticeFollowsTheThresholdCrossing(long before, int points, bool sent, int oldGrade, int newGrade)
    {
        var catalog = Load(Workbook(upgradeRows: [["FavoriteGrade", "Point"], ["Normal", "60"], ["Expert", "420"], ["Special", "1260"], ["Master", "0"]]));
        var notices = new List<FavoriteWeaponGradeChanged>();
        var awards = new FavoriteAwards(catalog, new Notices(notices), new BubbleFighter.Fantasy.Kernel.EventBus(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance), "test", "kr-1256");
        var row = new InventorySlotRecord(1, InventoryBag.CharacterBag, 1, 202020, 1, 0) { Favorites = ItemFavorites.Empty.With("kr-1256", before) };
        await awards.NotifyAsync(null!, row, points, default);
        if (!sent) { Assert.Empty(notices); return; }
        Assert.Equal(new FavoriteWeaponGradeChanged(202020, oldGrade, newGrade), Assert.Single(notices));
    }

    private sealed class Notices(List<FavoriteWeaponGradeChanged> list) : BubbleFighter.Fantasy.Abstractions.Protocol.IMessageSender<FavoriteWeaponGradeChanged>
    {
        public string Protocol => "test";
        public ValueTask SendAsync(BubbleFighter.Fantasy.Abstractions.Protocol.ISession session, FavoriteWeaponGradeChanged message, CancellationToken ct)
        { list.Add(message); return ValueTask.CompletedTask; }
    }
    /// <summary>The Master count derives the grade from the stored points at the Upgrade threshold and counts only weapon Articles.</summary>
    [Theory]
    [InlineData(1259, 0)]
    [InlineData(1260, 1)]
    public async Task MasterCountDerivesTheGradeFromStoredPoints(int points, int masters)
    {
        await using var fixture = await EnhancementPluginFixture.CreateAsync();
        Dictionary<string, object?> favorite = new() { ["favorite"] = new Dictionary<string, object?> { ["enabled"] = true } };
        await new EnhancementArticlesPlugin().ActivateAsync(fixture.Articles(favorite), default);
        var progress = fixture.Services.GetAll<IFavoriteWeaponProgress>().Single(reader => reader.Protocol == EnhancementPluginFixture.Protocol);
        var initial = await fixture.PrepareAsync();
        Assert.Equal(0, await progress.CountMasterWeaponsAsync(1, default));
        var weapon = initial!.CharacterBag.Single(row => row.ItemId == 202020);
        Assert.NotNull(await fixture.Store.TryEnhanceAsync(new([weapon], fixture.Scope, [], ItemEnhancementKind.Weapon, true,
            DateTimeOffset.UtcNow) { FavoritePoints = points }, default));
        Assert.Equal(masters, await progress.CountMasterWeaponsAsync(1, default));
    }
    /// <summary>With the feature off, stored points are neither awarded nor shown.</summary>
    [Fact]
    public async Task DisabledFeatureShowsNothing()
    {
        await using var fixture = await EnhancementPluginFixture.CreateAsync();
        await new EnhancementArticlesPlugin().ActivateAsync(fixture.Articles(), default);
        var initial = await fixture.PrepareAsync();
        var row = initial!.CharacterBag.Single(row => row.ItemId == 202020) with { Favorites = ItemFavorites.Empty.With(fixture.Scope.Key, 500) };
        Assert.DoesNotContain(await fixture.PresentationAsync(row) is var p ? p.Attributes : [], attribute => attribute.Kind == 164);
    }

    /// <summary>The client's own sheet states the thresholds 120, 420, 1260 and an enhancement award of 30.</summary>
    [Fact]
    [Trait("Category", "LocalClientData")]
    public void NativeWorkbookStatesTheKnownThresholds()
    {
        var catalog = Load(LocalClientData.Bytes("kr-1256", "Config/FavoriteWeaponSystem.xml"));
        Assert.Equal(new long[] { 120, 420, 1260 }, catalog.PromotionPoints);
        Assert.Equal(30, catalog.EnchantPoints);
        Assert.Equal(new[] { 24, 12, 6 }, catalog.ModificationPoints);
    }

    private sealed class Store(byte[] bytes) : IClientDataStore
    {
        public bool Exists(string path) => true;
        public byte[] Read(string path) => bytes;
    }
}