using System.Reflection;
using System.Text;
using System.Xml.Linq;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.Contracts.Pets;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Pets.Combat;
using BubbleFighter.Fantasy.Pets.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Pets.Tests;

/// <summary>Pins native pet effect identity, positional conversion and the independent resolver row lifecycle.</summary>
public sealed class PetCombatEffectsTests
{
    private const int Pet = 190100;

    /// <summary>Blank buff positions retain their payload index and native decimal bounds truncate before validation.</summary>
    [Fact]
    public void PositionalWordsAndNativeIntegerBoundsDetermineConvertedEffects()
    {
        var resolver = new PetCombatEffects("kr-1256/game", Catalog());
        Assert.True(resolver.TryResolve(Attribute(101, 1, 0, 3, 0), out var effects));
        Assert.Equal(new[]
        {
            new PetCombatEffect(PetCombatEffectKind.AssignCriticalChancePercent, 0.5f),
            new PetCombatEffect(PetCombatEffectKind.AddCriticalCancellationRatio, 0.03f),
        }, effects);
    }

    /// <summary>Signed payload words multiply as signed integers instead of unsigned bit-pattern magnitudes.</summary>
    [Fact]
    public void SignedPayloadAndFourReferenceOrderArePreserved()
    {
        var resolver = new PetCombatEffects("kr-1256/game", Catalog());
        Assert.True(resolver.TryResolve(Attribute(102, 1, -3, 3, 7), out var effects));
        Assert.Equal(new[]
        {
            new PetCombatEffect(PetCombatEffectKind.AssignCriticalChancePercent, 0.5f),
            new PetCombatEffect(PetCombatEffectKind.AddCriticalCancellationRatio, -0.03f),
            new PetCombatEffect(PetCombatEffectKind.AddDefenceMinimumRatio, 0.03f),
            new PetCombatEffect(PetCombatEffectKind.AddDefenceMaximumRatio, 0.07f),
        }, effects);
    }

    /// <summary>The legacy wider product and modern integer-to-float conversion remain distinct beyond exact float integers.</summary>
    [Fact]
    public void InputPrecisionKeepsTheNativeConversionBoundary()
    {
        var original = Catalog();
        var buffs = original.Buffs.ToDictionary(pair => pair.Key, pair => pair.Value);
        buffs[1] = buffs[1] with { Minimum = 16777217, Maximum = 16777217, Conversion = 0.03m };
        var catalog = new PetCatalog(original.Items.ToDictionary(pair => pair.Key, pair => pair.Value),
            original.Skills.ToDictionary(pair => pair.Key, pair => pair.Value), buffs, original.PetCategory);
        var attribute = Attribute(303, 16777217, 0, 0, 0);
        new PetCombatEffects("kr-1256/game", catalog, singlePrecisionInput: true).TryResolve(attribute, out var modern);
        new PetCombatEffects("cn-717/game", catalog, singlePrecisionInput: false).TryResolve(attribute, out var legacy);
        Assert.Equal(503316.46875f, Assert.Single(modern).Amount);
        Assert.Equal(503316.5f, Assert.Single(legacy).Amount);
    }

    /// <summary>Combat application does not use the acquisition-stage tail as an eligibility gate.</summary>
    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(uint.MaxValue)]
    public void AcquisitionStageDoesNotSuppressEarnedEffects(uint stage)
    {
        var resolver = new PetCombatEffects("kr-1256/game", Catalog());
        Assert.True(resolver.TryResolve(Attribute(303, 1, 0, 0, 0) with { Unknown28 = stage }, out var effects));
        Assert.Equal(0.5f, Assert.Single(effects).Amount);
    }

    /// <summary>Known base metadata produces no combat operations, while unrelated identities remain unhandled.</summary>
    [Fact]
    public void NativeFollowerFunctionAndCategoryOwnTheResolverBoundary()
    {
        var resolver = new PetCombatEffects("kr-1256/game", Catalog());
        Assert.True(resolver.TryResolve(Attribute(7, 0, 100, 10, 0), out var effects));
        Assert.Empty(effects);
        Assert.False(resolver.TryResolve(Attribute(101, 1, 0, 3, 0) with { ItemId = 100321 }, out effects));
        Assert.Empty(effects);
        Assert.False(resolver.TryResolve(Attribute(101, 1, 0, 3, 0) with { ItemId = 1234 }, out effects));
        Assert.Empty(effects);
    }

    /// <summary>Unknown selectors, wrong categories, missing words and nonzero blank positions fail before seed drawing.</summary>
    [Fact]
    public void MalformedRecognizedAttributesFailLoudly()
    {
        var resolver = new PetCombatEffects("kr-1256/game", Catalog());
        RoomItemAttribute[] invalid =
        [
            Attribute(999, 1, 0, 3, 0), Attribute(404, 1, 0, 0, 0), Attribute(101, 2, 0, 3, 0),
            Attribute(101, 1, 1, 3, 0), Attribute(101, 1, 0, 4, 0),
            Attribute(101, 1, 0, 3, 0) with { Values = [1, 0, 3] },
            Attribute(101, 1, 0, 3, 0) with { Kind = uint.MaxValue },
        ];
        foreach (var attribute in invalid)
            Assert.Throws<InvalidDataException>(() => resolver.TryResolve(attribute, out _));
    }

    /// <summary>The disabled independent row publishes no resolver and requires neither a build nor client data.</summary>
    [Fact]
    public async Task DisabledRowHasNoCatalogOrServiceDependency()
    {
        var services = new ServiceRegistry();
        var context = Context(services, enabled: false, withBuild: false);
        var metadata = typeof(PetCombatEffectsPlugin).GetCustomAttribute<PluginAttribute>()!;
        Assert.Empty(metadata.ProvidesMany);
        Assert.Contains(typeof(IPetCombatEffects), metadata.ConditionalProvidesMany);
        context.BindServices(metadata);
        try
        {
            await new PetCombatEffectsPlugin().ActivateAsync(context, default);
            Assert.Empty(services.GetAll<IPetCombatEffects>());
            Assert.False(new PetCombatEffectsOptions().Enabled);
        }
        finally { await context.UnwindAsync(); }
    }

    /// <summary>The enabled row reads through the bounded client-data interface and owns its resolver until unload.</summary>
    [Fact]
    public async Task EnabledRowPublishesOnlyItsBuildAndUnloadsCleanly()
    {
        var services = new ServiceRegistry();
        var store = new Store(Encoding.UTF8.GetBytes(Workbook().ToString()));
        using var data = services.Register<IClientDataStore>(store);
        var context = Context(services, enabled: true);
        context.BindServices(typeof(PetCombatEffectsPlugin).GetCustomAttribute<PluginAttribute>()!);
        try
        {
            await new PetCombatEffectsPlugin().ActivateAsync(context, default);
            Assert.Equal("kr-1256/game", Assert.Single(services.GetAll<IPetCombatEffects>()).Protocol);
            Assert.Equal(134217728, store.LastLimit);
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(services.GetAll<IPetCombatEffects>());
    }

    /// <summary>A foreign game protocol cannot publish a resolver using this build's data.</summary>
    [Fact]
    public async Task MismatchedProtocolFailsBeforeReadingData()
    {
        var services = new ServiceRegistry();
        var context = Context(services, enabled: true, protocol: "cn-717/game");
        try
        {
            await Assert.ThrowsAsync<ConfigException>(() => new PetCombatEffectsPlugin().ActivateAsync(context, default).AsTask());
            Assert.Empty(services.GetAll<IPetCombatEffects>());
        }
        finally { await context.UnwindAsync(); }
    }

    private static RoomItemAttribute Attribute(uint selector, int a, int b, int c, int d) =>
        new(Pet, selector, [unchecked((uint)a), unchecked((uint)b), unchecked((uint)c), unchecked((uint)d)], 2, "");

    private static PetCatalog Catalog() => PetCatalogLoader.Load(new Store(Encoding.UTF8.GetBytes(Workbook().ToString())),
        "Config/ShopItem2.xml", 100_000, 100, 32, 256);

    private static PluginContext Context(ServiceRegistry services, bool enabled, bool withBuild = true,
        string protocol = "kr-1256/game") => new(new PluginRow
        {
            Id = "pet-combat", Plugin = "pets.combat-effects", Source = "test",
            ClientBuild = withBuild ? new ClientBuild("kr", 1256, 0) : null,
            Config = new Dictionary<string, object?> { ["enabled"] = enabled, ["protocol"] = protocol },
        }, services, new EventBus(NullLogger.Instance), NullLogger.Instance);

    private static readonly XNamespace Ss = "urn:schemas-microsoft-com:office:spreadsheet";

    private static XElement Workbook() => new(Ss + "Workbook", new XAttribute(XNamespace.Xmlns + "ss", Ss),
        Table("ShopItem", ["itemId", "subCategory", "parameter", "function"],
            ["190100", "Followers", "2", "10001"], ["100321", "Followers", "5", "19"], ["1234", "Other", "0", "0"]),
        Table("SubCategory", ["id", "name"], ["777", "Followers"], ["900", "Other"]),
        Table("ItemAttr", ["index", "category", "grade", "value", "weight", "buffIndex_1", "buffIndex_2", "buffIndex_3", "buffIndex_4"],
            ["7", "777", "0", "0", "100", "", "18", "16", ""],
            ["101", "777", "1", "9", "3", "1", "", "2", ""],
            ["102", "777", "1", "0", "1", "1", "2", "3", "4"],
            ["303", "777", "1", "1", "0", "1", "", "", ""],
            ["404", "900", "1", "1", "1", "1", "", "", ""]),
        Table("RandomTable", ["buffIndex", "buffName", "randomGroup", "min", "max", "conv"],
            ["1", "criFactorIncAmount", "1", "1.5", "1.5", "0.5"],
            ["2", "criticalCancelRatio", "2", "-3", "3", "0.01"],
            ["3", "damagedDecRatioMin", "3", "3", "3", "0.01"],
            ["4", "damagedDecRatioMax", "4", "7", "7", "0.01"],
            ["16", "gradeExp1", "6", "0", "10", "1"], ["17", "gradeExp2", "7", "0", "100", "1"],
            ["18", "hunger", "8", "86", "100", "1"]));

    private static XElement Table(string name, string[] header, params string[][] rows) =>
        new(Ss + "Worksheet", new XAttribute(Ss + "Name", name), new XElement(Ss + "Table",
            new[] { header }.Concat(rows).Select(row => new XElement(Ss + "Row",
                row.Select(cell => new XElement(Ss + "Cell", new XElement(Ss + "Data", cell)))))));

    private sealed class Store(byte[] bytes) : IClientDataStore
    {
        internal int LastLimit { get; private set; }
        /// <summary>Exposes the fixture's one native workbook to the catalog loader.</summary>
        public bool Exists(string path) => true;

        /// <summary>Rejects unbounded reads so activation must use the resource limit contract.</summary>
        public byte[] Read(string path) => throw new InvalidOperationException("A bounded read is required.");

        /// <summary>Captures the loader's byte bound before returning the fixture's workbook.</summary>
        public byte[] Read(string path, int maximumBytes) { LastLimit = maximumBytes; return bytes; }
    }
}
