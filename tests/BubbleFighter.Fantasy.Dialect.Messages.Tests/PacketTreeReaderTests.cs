using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Exercises hostile counts and nested build differences in table-driven inbound packets.</summary>
public sealed class PacketTreeReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bff-packet-read-" + Guid.NewGuid().ToString("N"));

    private PacketTables Load(string structures)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "builds.yaml"), "version: 1\nbuilds:\n  base: {}\n  older: {base: base}\n");
        File.WriteAllText(Path.Combine(_root, "read.yaml"), structures);
        return PacketTables.Load(_root);
    }

    /// <summary>Removes only this test's uniquely owned temporary table directory.</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private const string Nested = """
        version: 1
        structs:
          Envelope:
            - header: Row
            - rows: "list<Row>"
            - fixed: "u16[2]"
            - tail: empty_list
          Row:
            - word: u32
            - enabled: bool
            - text: wstr
            - extra: u8
              except: [older]
        """;

    [Fact]
    public void ScalarReadersPreserveIdenticalNativeBitsAndFloatingPointValues()
    {
        var tables = Load("""
            version: 1
            structs:
              Root:
                - a: u8
                - b: u16
                - c: u32
                - d: u64
                - e: bool
                - f: f32
                - g: f64
                - h: wstr
            """);
        var bytes = Convert.FromHexString("FFFEFFEFCDAB89EFCDAB8967452301010000000000C03F00000000000002C0010000004100");
        Assert.Equal(37, bytes.Length);
        var decoded = PacketCodec.Read(tables, "base", "Root", bytes);
        Assert.Equal((byte)255, decoded["a"]);
        Assert.Equal((ushort)65534, decoded["b"]);
        Assert.Equal(unchecked((int)0x89ABCDEF), decoded["c"]);
        Assert.Equal(0x0123456789ABCDEFL, decoded["d"]);
        Assert.Equal(1.5f, decoded["f"]);
        Assert.Equal(-2.25d, decoded["g"]);
        Assert.Equal("A", decoded["h"]);
        foreach (var pair in PacketCodec.ReadScalars(tables, "base", "Root", bytes))
            Assert.Equal(pair.Value, decoded[pair.Key]);
        Assert.Equal(bytes, PacketCodec.Write(tables, "base", "Root", decoded));
    }

    [Fact]
    public void FixedStructureRunsDoNotConsumeAListCount()
    {
        var tables = Load("version: 1\nstructs:\n  Root:\n    - rows: 'Row[2]'\n  Row:\n    - word: u32\n");
        var bytes = Convert.FromHexString("FFFFFFFF44332211");
        var decoded = PacketCodec.Read(tables, "base", "Root", bytes);
        var rows = Assert.IsType<object?[]>(decoded["rows"]);
        Assert.Equal(2, rows.Length);
        Assert.Equal(-1, Assert.IsType<Dictionary<string, object?>>(rows[0])["word"]);
        Assert.Equal(0x11223344, Assert.IsType<Dictionary<string, object?>>(rows[1])["word"]);
        Assert.Equal(bytes, PacketCodec.Write(tables, "base", "Root", decoded));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(tables, "base", "Root", bytes[..^1]));
    }

    [Theory]
    [InlineData("base", "FFFFFFFF02000000010000004100AA01000000070000000100000000000000BB3412785600000000", 40)]
    [InlineData("older", "FFFFFFFF02000000010000004100010000000700000001000000000000003412785600000000", 38)]
    public void NestedMapsListsAndRunsRespectBuildDifferences(string build, string hex, int length)
    {
        var tables = Load(Nested);
        var bytes = Convert.FromHexString(hex);
        Assert.Equal(length, bytes.Length);
        var decoded = PacketCodec.Read(tables, build, "Envelope", bytes);
        var header = Assert.IsType<Dictionary<string, object?>>(decoded["header"]);
        Assert.Equal(-1, header["word"]);
        Assert.Equal("A", header["text"]);
        Assert.True(Assert.IsType<bool>(header["enabled"]));
        Assert.Equal(build == "base", header.ContainsKey("extra"));
        var rows = Assert.IsType<object?[]>(decoded["rows"]);
        var row = Assert.IsType<Dictionary<string, object?>>(Assert.Single(rows));
        Assert.Equal(7, row["word"]);
        Assert.Equal(new object?[] { (ushort)0x1234, (ushort)0x5678 }, decoded["fixed"]);
        Assert.Empty(Assert.IsType<object?[]>(decoded["tail"]));
        // Noncanonical native true is normalized by the writer; all other bytes are preserved.
        bytes[4] = 1;
        Assert.Equal(bytes, PacketCodec.Write(tables, build, "Envelope", decoded));
    }

    [Fact]
    public void NativePresetVectorsRoundTripTheirOwnCountPrefixes()
    {
        var bytes = Convert.FromHexString("FFFFFFFF02000000443322118877665502000000010003000000020304");
        Assert.Equal(29, bytes.Length);
        var tables = TestBuilds.Packets;
        var decoded = PacketCodec.Read(tables, "kr-812", "WeaponPresetList", bytes);
        Assert.Equal(-1, decoded["user_number"]);
        Assert.Equal(new object?[] { 0x11223344, 0x55667788 }, decoded["item_ids"]);
        Assert.Equal(new object?[] { (byte)1, (byte)0 }, decoded["bound_flags"]);
        Assert.Equal(new object?[] { (byte)2, (byte)3, (byte)4 }, decoded["unknown_40"]);
        Assert.Equal(bytes, PacketCodec.Write(tables, "kr-812", "WeaponPresetList", decoded));
        for (var i = 0; i < bytes.Length; i++)
            Assert.Throws<PacketFormatException>(() => PacketCodec.Read(tables, "kr-812", "WeaponPresetList", bytes[..i]));
    }

    [Theory]
    [InlineData("FFFFFFFF")]
    [InlineData("01100000")]
    [InlineData("0200000001000000")]
    public void ListsRejectOversizedAndTruncatedCountsBeforeElementAllocation(string hex)
    {
        var tables = Load("version: 1\nstructs:\n  Root:\n    - values: 'list<u32>'\n");
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(tables, "base", "Root", Convert.FromHexString(hex)));
    }

    [Fact]
    public void TotalBudgetIncludesBothMapFieldsAndAllListElements()
    {
        var tables = Load("version: 1\nstructs:\n  Root:\n    - rows: 'list<Row>'\n  Row:\n    - a: u8\n    - b: u8\n");
        var bytes = Convert.FromHexString("0200000001020304");
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(tables, "base", "Root", bytes,
            new PacketReadLimits(MaxValues: 6)));
        Assert.Equal(2, Assert.IsType<object?[]>(PacketCodec.Read(tables, "base", "Root", bytes,
            new PacketReadLimits(MaxValues: 7))["rows"]).Length);
    }

    [Fact]
    public void RecursiveZeroWidthTablesAndZeroWidthCollectionsAreBounded()
    {
        var tables = Load("version: 1\nstructs:\n  Cycle:\n    - next: Cycle\n  Empty: []\n  Root:\n    - rows: 'list<Empty>'\n");
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(tables, "base", "Cycle", [],
            new PacketReadLimits(MaxDepth: 4)));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(tables, "base", "Root", Convert.FromHexString("03000000"),
            new PacketReadLimits(MaxValues: 3)));
        Assert.Equal(3, Assert.IsType<object?[]>(PacketCodec.Read(tables, "base", "Root", Convert.FromHexString("03000000"),
            new PacketReadLimits(MaxValues: 4))["rows"]).Length);
    }

    [Fact]
    public void FixedRunsAndEmptyListsObeyTheirExplicitConstraints()
    {
        var tables = Load("version: 1\nstructs:\n  Run:\n    - values: 'u8[3]'\n  Empty:\n    - values: empty_list\n");
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(tables, "base", "Run", [1, 2, 3],
            new PacketReadLimits(MaxCollectionElements: 2)));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(tables, "base", "Empty", [1, 0, 0, 0]));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(tables, "base", "Empty", [0, 0, 0, 0],
            new PacketReadLimits(MaxDepth: 1)));
    }

    [Fact]
    public void EmptyListsStillRejectUnresolvedOrUnsupportedElementTypes()
    {
        var tables = Load("version: 1\nstructs:\n  Missing:\n    - values: 'list<MissingRow>'\n  Unsupported:\n    - values: 'list<nothing>'\n");
        Assert.Throws<ConfigException>(() => PacketCodec.Read(tables, "base", "Missing", [0, 0, 0, 0]));
        Assert.Throws<ConfigException>(() => PacketCodec.Read(tables, "base", "Unsupported", [0, 0, 0, 0]));
    }

    [Fact]
    public void NestedBooleansStringsAndCompleteBodiesAreChecked()
    {
        var tables = Load(Nested);
        var bytes = Convert.FromHexString("FFFFFFFF02000000010000004100AA01000000070000000100000000000000BB3412785600000000");
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(tables, "base", "Envelope", bytes,
            requireCanonicalBooleans: true));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(tables, "base", "Envelope", bytes,
            new PacketReadLimits(MaxStringCharacters: 0)));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(tables, "base", "Envelope", [.. bytes, 0]));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(tables, "base", "Envelope", new byte[65536]));
    }

    private const string Tagged = """
        version: 1
        structs:
          Seat:
            - number: u32
            - tail: "optional<0x3A,Tail>"
              default: 186
            - last: u8
          Tail:
            - first: u32
            - inner: "optional<0x3A,Inner>"
          Inner:
            - word: u32
        """;

    [Fact]
    public void AnOptionalBlockWritesItsTagAndFieldsOnlyWhenGivenAMap()
    {
        var tables = Load(Tagged);
        var absent = PacketCodec.Write(tables, "base", "Seat", new Dictionary<string, object?> { ["number"] = 7, ["last"] = 1 });
        Assert.Equal("07000000BA01", Convert.ToHexString(absent));
        var zeroTag = PacketCodec.Write(tables, "base", "Seat", new Dictionary<string, object?> { ["tail"] = 0 });
        Assert.Equal("000000000000", Convert.ToHexString(zeroTag));
        var nested = PacketCodec.Write(tables, "base", "Seat", new Dictionary<string, object?>
        {
            ["number"] = 7,
            ["tail"] = new Dictionary<string, object?> { ["first"] = 2, ["inner"] = new Dictionary<string, object?> { ["word"] = 3 } },
        });
        Assert.Equal("070000003A020000003A0300000000", Convert.ToHexString(nested));
        Assert.Throws<ArgumentException>(() =>
            PacketCodec.Write(tables, "base", "Seat", new Dictionary<string, object?> { ["tail"] = 0x3A }));
    }

    [Fact]
    public void AnOptionalBlockReadsBackToTheSameBytes()
    {
        var tables = Load(Tagged);
        foreach (var hex in new[] { "07000000BA01", "000000000000", "070000003A020000003A0300000000", "070000003A0200000000FF" })
        {
            var bytes = Convert.FromHexString(hex);
            var decoded = PacketCodec.Read(tables, "base", "Seat", bytes);
            Assert.Equal(hex, Convert.ToHexString(PacketCodec.Write(tables, "base", "Seat", decoded)));
        }
        var present = PacketCodec.Read(tables, "base", "Seat", Convert.FromHexString("070000003A020000003A0300000000"));
        var tail = Assert.IsType<Dictionary<string, object?>>(present["tail"]);
        Assert.Equal(2, tail["first"]);
        Assert.Equal((byte)0xBA, PacketCodec.Read(tables, "base", "Seat", Convert.FromHexString("07000000BA01"))["tail"]);
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(tables, "base", "Seat", Convert.FromHexString("070000003A02")));
    }

    [Theory]
    [InlineData("optional<3A,Tail>")]
    [InlineData("optional<0x3A,tail>")]
    [InlineData("optional<0x3A>")]
    [InlineData("optional<0x13A,Tail>")]
    public void AMalformedOptionalTypeIsRejected(string type)
    {
        Assert.Throws<ConfigException>(() => Load($$"""
            version: 1
            structs:
              Seat:
                - tail: "{{type}}"
              Tail:
                - first: u32
            """));
    }

    [Fact]
    public void InvalidLimitsFailBeforeReadingPayload()
    {
        var limits = new[]
        {
            new PacketReadLimits(MaxStringCharacters: -1), new PacketReadLimits(MaxStringCharacters: 32766),
            new PacketReadLimits(MaxCollectionElements: -1), new PacketReadLimits(MaxCollectionElements: 65536),
            new PacketReadLimits(MaxValues: -1), new PacketReadLimits(MaxValues: 65536),
            new PacketReadLimits(MaxDepth: 0), new PacketReadLimits(MaxDepth: 65),
        };
        foreach (var limit in limits)
            Assert.Throws<ArgumentOutOfRangeException>(() => PacketCodec.Read(TestBuilds.Packets, "kr-812", "RelayEndpoint", [], limit));
    }
}
