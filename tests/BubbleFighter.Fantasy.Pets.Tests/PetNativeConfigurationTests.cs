using System.Text;
using System.Xml;
using System.Xml.Linq;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Pets.Data;
using Xunit;

namespace BubbleFighter.Fantasy.Pets.Tests;

/// <summary>Validates native PetConfig minutes and rejects ambiguous, malformed or unbounded client configuration.</summary>
public sealed class PetNativeConfigurationTests
{
    private static readonly XNamespace Ss = "urn:schemas-microsoft-com:office:spreadsheet";

    /// <summary>Native minutes are converted exactly into server seconds at zero, seven hours and the policy limit.</summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(420, 25200)]
    [InlineData(525600, 31536000)]
    public void NativeIntegerMinutesHaveAnExactSecondsConversion(int minutes, int seconds)
    {
        var bytes = Bytes(Workbook($$"""{"FeedCoolTime":{{minutes}}}"""));
        var store = new Store(bytes);
        Assert.Equal(seconds, Load(store));
        Assert.Equal("Config/PetConfig.xml", store.LastPath);
        Assert.Equal(100_000, store.LastLimit);
    }

    /// <summary>Native feeding validates its configured workbook and returns no DynamicConfig payload or mileage requirement.</summary>
    [Fact]
    public void NativeFeedingNeedsOnlyTheMatchingCooldownAndConfiguredAsset()
    {
        var options = NativeOptions();
        options.NativePetConfigPath = "Config/SelectedPetConfig.xml";
        var store = new Store(Bytes(Workbook()));
        var catalog = PetTestData.Catalog();
        var scope = new InventoryLoadoutScope(options.BuildKey, catalog.Items.Keys.ToArray());
        Assert.Empty(options.Validate());
        Assert.Null(options.Mileage.DisplayMaximum);
        Assert.False(options.Mileage.Enabled);
        Assert.Null(PetsPlugin.CreateClientConfiguration(options, catalog, scope, store));
        Assert.Equal(options.NativePetConfigPath, store.LastPath);
        Assert.Equal(options.MaxWorkbookBytes, store.LastLimit);
    }

    /// <summary>Missing or mismatched server cooldown never silently replaces the duration used by the native UI.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(12600)]
    [InlineData(25260)]
    public void NativeFeedingRejectsMissingOrMismatchedServerDuration(int? seconds)
    {
        var options = NativeOptions();
        options.FeedCooldownSeconds = seconds;
        var catalog = PetTestData.Catalog();
        Assert.Throws<ConfigException>(() => PetsPlugin.CreateClientConfiguration(options, catalog,
            new(options.BuildKey, catalog.Items.Keys.ToArray()), new Store(Bytes(Workbook()))));
    }

    /// <summary>Native configuration cannot proceed without its data service or when the configured member is absent.</summary>
    [Fact]
    public void NativeFeedingRejectsMissingStoreOrMember()
    {
        var options = NativeOptions();
        var catalog = PetTestData.Catalog();
        var scope = new InventoryLoadoutScope(options.BuildKey, catalog.Items.Keys.ToArray());
        Assert.Throws<ConfigException>(() => PetsPlugin.CreateClientConfiguration(options, catalog, scope));
        Assert.Throws<FileNotFoundException>(() => PetsPlugin.CreateClientConfiguration(options, catalog, scope, new MissingStore()));
    }

    /// <summary>Malformed JSON, duplicate keys, wrong types and durations outside the validated integer range are rejected.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"FeedCoolTime\":\"420\"}")]
    [InlineData("{\"FeedCoolTime\":true}")]
    [InlineData("{\"FeedCoolTime\":-1}")]
    [InlineData("{\"FeedCoolTime\":420.5}")]
    [InlineData("{\"FeedCoolTime\":525601}")]
    [InlineData("{\"FeedCoolTime\":2147483647}")]
    [InlineData("{\"FeedCoolTime\":2147483648}")]
    [InlineData("{\"FeedCoolTime\":420,\"FeedCoolTime\":420}")]
    [InlineData("{\"FeedCoolTime\":420,}")]
    [InlineData("{\"FeedCoolTime\":420} null")]
    [InlineData("{\"FeedCoolTime\":420,\"nested\":{\"value\":1}}")]
    public void InvalidPetSystemJsonFailsWithoutFallback(string json)
    {
        var error = Assert.Throws<InvalidDataException>(() => Load(new Store(Bytes(Workbook(json)))));
        Assert.Contains("Config/PetConfig.xml", error.Message);
    }

    /// <summary>The native constant worksheet, header and PetSystem row must each be present and unambiguous.</summary>
    [Theory]
    [InlineData("missing_sheet")]
    [InlineData("duplicate_sheet")]
    [InlineData("missing_header")]
    [InlineData("missing_name_column")]
    [InlineData("missing_value_column")]
    [InlineData("duplicate_column")]
    [InlineData("missing_row")]
    [InlineData("duplicate_row")]
    [InlineData("missing_value")]
    public void MissingOrDuplicateWorkbookFieldsFail(string change)
    {
        var workbook = Workbook();
        var sheet = workbook.Element(Ss + "Worksheet")!;
        var table = sheet.Element(Ss + "Table")!;
        var header = table.Elements(Ss + "Row").First();
        var row = table.Elements(Ss + "Row").ElementAt(1);
        switch (change)
        {
            case "missing_sheet": sheet.Remove(); break;
            case "duplicate_sheet": workbook.Add(new XElement(sheet)); break;
            case "missing_header": table.RemoveNodes(); break;
            case "missing_name_column": header.Elements(Ss + "Cell").First().Value = "wrong"; break;
            case "missing_value_column": header.Elements(Ss + "Cell").Last().Remove(); break;
            case "duplicate_column": header.Add(new XElement(header.Elements(Ss + "Cell").First())); break;
            case "missing_row": row.Remove(); break;
            case "duplicate_row": table.Add(new XElement(row)); break;
            case "missing_value": row.Elements(Ss + "Cell").Last().Remove(); break;
            default: throw new ArgumentOutOfRangeException(nameof(change));
        }
        Assert.Throws<InvalidDataException>(() => Load(new Store(Bytes(workbook))));
    }

    /// <summary>Column positions come from names, including sparse cells, and styled blank rows have no policy meaning.</summary>
    [Fact]
    public void ReorderedColumnsSparseCellsAndBlankRowsRetainTheNativeValue()
    {
        var workbook = new XElement(Ss + "Workbook", new XElement(Ss + "Worksheet", new XAttribute(Ss + "Name", "ConstValue"),
            new XElement(Ss + "Table",
                new XElement(Ss + "Row", Cell("ConstValue"), new XElement(Ss + "Cell"), Cell("ConstName")),
                new XElement(Ss + "Row", Cell("{\"FeedCoolTime\":420}"),
                    new XElement(Ss + "Cell", new XAttribute(Ss + "Index", 3), new XElement(Ss + "Data", "PetSystem"))),
                new XElement(Ss + "Row", new XElement(Ss + "Cell", new XAttribute(Ss + "Index", 2000))))));
        Assert.Equal(25200, Load(new Store(Bytes(workbook))));
    }

    /// <summary>The existing workbook limits apply to native settings before sparse cells or JSON can grow without bounds.</summary>
    [Fact]
    public void WorkbookByteRowColumnAndCellLimitsRemainEnforced()
    {
        var bytes = Bytes(Workbook());
        Assert.Throws<InvalidDataException>(() => PetNativeConfiguration.ReadFeedCooldownSeconds(new Store(bytes), "Config/PetConfig.xml", 10, 100, 32, 256));
        Assert.Throws<InvalidDataException>(() => PetNativeConfiguration.ReadFeedCooldownSeconds(new Store([]), "Config/PetConfig.xml", 100_000, 100, 32, 256));
        Assert.Throws<InvalidDataException>(() => PetNativeConfiguration.ReadFeedCooldownSeconds(new Store(bytes), "Config/PetConfig.xml", 100_000, 1, 32, 256));
        Assert.Throws<InvalidDataException>(() => PetNativeConfiguration.ReadFeedCooldownSeconds(new Store(bytes), "Config/PetConfig.xml", 100_000, 100, 1, 256));
        var workbook = Workbook();
        workbook.Descendants(Ss + "Cell").Last().SetAttributeValue(Ss + "Index", int.MaxValue);
        Assert.Throws<InvalidDataException>(() => Load(new Store(Bytes(workbook))));
        Assert.Throws<InvalidDataException>(() => Load(new Store(Bytes(Workbook(new string('x', 257))))));
    }

    /// <summary>Invalid XML and DTD declarations are rejected before any external resource could supply a duration.</summary>
    [Theory]
    [InlineData("<Workbook")]
    [InlineData("<!DOCTYPE Workbook [<!ENTITY value '420'>]><Workbook/>")]
    public void MalformedXmlAndDtdAreRejected(string xml)
    {
        Assert.Throws<XmlException>(() => Load(new Store(Encoding.UTF8.GetBytes(xml))));
    }

    /// <summary>Only audited source combinations can activate, and every enabled manual policy is explicit.</summary>
    [Theory]
    [InlineData(PetClientConfigurationSource.None, true, 0, null, null)]
    [InlineData(PetClientConfigurationSource.None, false, 1, null, null)]
    [InlineData(PetClientConfigurationSource.DynamicConfig, false, 0, 317, null)]
    [InlineData(PetClientConfigurationSource.DynamicConfig, true, 0, null, null)]
    [InlineData(PetClientConfigurationSource.DynamicConfig, true, 0, 0, null)]
    [InlineData(PetClientConfigurationSource.DynamicConfig, true, null, 317, null)]
    [InlineData(PetClientConfigurationSource.DynamicConfig, true, 0, 317, "Config/PetConfig.xml")]
    [InlineData(PetClientConfigurationSource.NativePetConfig, true, 25200, null, null)]
    [InlineData(PetClientConfigurationSource.NativePetConfig, true, 25200, null, " ")]
    [InlineData(PetClientConfigurationSource.NativePetConfig, true, null, null, "Config/PetConfig.xml")]
    [InlineData(PetClientConfigurationSource.NativePetConfig, false, 25200, null, "Config/PetConfig.xml")]
    [InlineData((PetClientConfigurationSource)99, true, 0, null, null)]
    public void InvalidSourceCombinationsCannotActivate(PetClientConfigurationSource source, bool manual,
        int? seconds, int? maximum, string? path)
    {
        var options = NativeOptions();
        options.ClientConfigurationSource = source;
        options.ManualLevelUp = manual;
        options.FeedCooldownSeconds = seconds;
        options.NativePetConfigPath = path;
        options.Mileage.DisplayMaximum = maximum;
        Assert.NotEmpty(options.ValidateClientConfiguration(requireSettings: true));
        var catalog = PetTestData.Catalog();
        Assert.Throws<ConfigException>(() => PetsPlugin.CreateClientConfiguration(options, catalog,
            new(options.BuildKey, catalog.Items.Keys.ToArray()), new Store(Bytes(Workbook()))));
    }

    /// <summary>Static native settings cannot claim a mileage display publication that the client has no route to receive.</summary>
    [Fact]
    public void NativeSourceDoesNotAdmitMileageConfiguration()
    {
        var options = NativeOptions();
        options.Mileage.Enabled = true;
        options.Mileage.DisplayMaximum = 317;
        Assert.Contains(options.ValidateClientConfiguration(requireSettings: true),
            error => error.MemberNames.Contains(nameof(PetsOptions.ClientConfigurationSource)));
    }

    private static PetsOptions NativeOptions() => new()
    {
        Enabled = true, BuildKey = "native-fixture", Protocol = "native-fixture/game", ManualLevelUp = true,
        ClientConfigurationSource = PetClientConfigurationSource.NativePetConfig, NativePetConfigPath = "Config/PetConfig.xml",
        FeedCooldownSeconds = 25200, ClientUtcOffsetMinutes = 0,
    };

    private static int Load(IClientDataStore store) =>
        PetNativeConfiguration.ReadFeedCooldownSeconds(store, "Config/PetConfig.xml", 100_000, 100, 32, 256);

    private static XElement Workbook(string json = "{\"FeedCoolTime\":420}") => new(Ss + "Workbook",
        new XElement(Ss + "Worksheet", new XAttribute(Ss + "Name", "ConstValue"), new XElement(Ss + "Table",
            new XElement(Ss + "Row", Cell("ConstName"), Cell("ConstValue")),
            new XElement(Ss + "Row", Cell("PetSystem"), Cell(json)))));

    private static XElement Cell(string text) => new(Ss + "Cell", new XElement(Ss + "Data", text));
    private static byte[] Bytes(XElement workbook) => Encoding.UTF8.GetBytes(workbook.ToString());

    private sealed class Store(byte[] bytes) : IClientDataStore
    {
        internal string? LastPath { get; private set; }
        internal int LastLimit { get; private set; }
        /// <summary>Makes the supplied native configuration bytes available under any requested path.</summary>
        public bool Exists(string path) => true;
        /// <summary>Rejects unbounded native configuration reads so callers must supply a byte limit.</summary>
        public byte[] Read(string path) => throw new NotSupportedException("A native configuration read must carry a byte limit.");
        /// <summary>Records the requested path and byte limit before returning the supplied native configuration bytes.</summary>
        public byte[] Read(string path, int maximumBytes)
        {
            LastPath = path;
            LastLimit = maximumBytes;
            return bytes;
        }
    }

    private sealed class MissingStore : IClientDataStore
    {
        /// <summary>Reports every native configuration path as absent.</summary>
        public bool Exists(string path) => false;
        /// <summary>Throws a missing-file error containing the requested path instead of inventing native pet defaults.</summary>
        public byte[] Read(string path) => throw new FileNotFoundException("Native PetConfig is absent.", path);
    }
}
