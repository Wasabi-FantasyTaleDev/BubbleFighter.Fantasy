using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Checks conditional packet fields against independent bytes and invalid table dependencies.</summary>
public sealed class PacketGatedFieldTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bff-gated-" + Guid.NewGuid().ToString("N"));

    private const string Movement = """
        version: 1
        structs:
          Movement:
            - actor: u32
            - tick: u16
            - mask: u8
            - x: 'when<mask&0x01,f32>'
            - y: 'when<mask&0x02,f32>'
            - z: 'when<mask&0x04,f32>'
            - yaw: 'when<mask&0x08,u8>'
            - pitch: 'when<mask&0x10,u8>'
            - state: 'when<mask&0x20,u8>'
        """;

    private PacketTables Load(string structures)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "builds.yaml"), "version: 1\nbuilds:\n  base: {}\n  other: {base: base}\n");
        File.WriteAllText(Path.Combine(_root, "fields.yaml"), structures);
        return PacketTables.Load(_root);
    }

    /// <summary>Removes this test's uniquely owned table directory.</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>All six-bit combinations, including no delta and every delta field.</summary>
    public static TheoryData<int> Masks => new(Enumerable.Range(0, 64));

    /// <summary>
    /// The reader kr-1256 sub_10F4A10 and writer kr-1256 sub_10F4930 consume mask then 4/4/4/1/1/1 bytes.
    /// The delta builder kr-1256 sub_95F490 maps these bits to coordinates and the three packed bytes.
    /// </summary>
    [Theory]
    [MemberData(nameof(Masks))]
    public void EveryMovementMaskMatchesIndependentBytesAndPreservesAbsence(int mask)
    {
        var tables = Load(Movement);
        string[] names = ["x", "y", "z", "yaw", "pitch", "state"];
        byte[][] chunks = [
            Convert.FromHexString("0000C03F"), Convert.FromHexString("000010C0"), Convert.FromHexString("00000000"),
            [0x12], [0x34], [0x56],
        ];
        object[] samples = [1.5f, -2.25f, 0f, (byte)0x12, (byte)0x34, (byte)0x56];
        var expected = new List<byte>(Convert.FromHexString("443322116655")) { (byte)mask };
        var values = new Dictionary<string, object?> { ["actor"] = 0x11223344, ["tick"] = (ushort)0x5566, ["mask"] = mask };
        for (var bit = 0; bit < 6; bit++)
            if ((mask & (1 << bit)) != 0)
            {
                expected.AddRange(chunks[bit]);
                values[names[bit]] = samples[bit];
            }
        var body = expected.ToArray();
        var read = PacketCodec.Read(tables, "base", "Movement", body);
        var flat = PacketCodec.ReadScalars(tables, "base", "Movement", body);
        for (var bit = 0; bit < 6; bit++)
        {
            Assert.Equal((mask & (1 << bit)) != 0, read.ContainsKey(names[bit]));
            Assert.Equal(read.ContainsKey(names[bit]), flat.ContainsKey(names[bit]));
            if (read.TryGetValue(names[bit], out var value)) Assert.Equal(samples[bit], value);
        }
        Assert.Equal(body, PacketCodec.Write(tables, "base", "Movement", values));
        Assert.Equal(body, PacketCodec.Write(tables, "base", "Movement", read));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(tables, "base", "Movement", body[..^1]));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(tables, "base", "Movement", [.. body, 0]));
    }

    /// <summary>The complete delta sample has 22 bytes; an explicit zero coordinate remains present.</summary>
    [Fact]
    public void FullMovementSamplePinsTwentyTwoBytes()
    {
        var tables = Load(Movement);
        var body = Convert.FromHexString("4433221166553F0000C03F000010C000000000123456");
        Assert.Equal(22, body.Length);
        Assert.Equal(0f, PacketCodec.Read(tables, "base", "Movement", body)["z"]);
        Assert.Contains("when<mask&0x01,f32>", tables.Show("base", "Movement"));
    }

    /// <summary>Missing gates, wrong scalar types, zero masks and masks wider than the gate fail while loading.</summary>
    [Theory]
    [InlineData("    - value: 'when<missing&0x01,u8>'")]
    [InlineData("    - value: 'when<mask&0x01,u8>'\n    - mask: u8")]
    [InlineData("    - mask: bool\n    - value: 'when<mask&0x01,u8>'")]
    [InlineData("    - mask: f32\n    - value: 'when<mask&0x01,u8>'")]
    [InlineData("    - mask: wstr\n    - value: 'when<mask&0x01,u8>'")]
    [InlineData("    - mask: u8\n    - value: 'when<mask&0x00,u8>'")]
    [InlineData("    - mask: u8\n    - value: 'when<mask&0x100,u8>'")]
    [InlineData("    - mask: u16\n    - value: 'when<mask&0x10000,u8>'")]
    [InlineData("    - mask: u32\n    - value: 'when<mask&0x100000000,u8>'")]
    [InlineData("    - mask: u64\n    - value: 'when<mask&0x10000000000000000,u8>'")]
    [InlineData("    - mask: u8\n    - value: 'when<mask&0x01,Missing>'")]
    [InlineData("    - mask: u8\n    - value: 'when<mask&0x01,nothing>'")]
    [InlineData("    - mask: u8\n    - value: 'when<mask&0x01,list<u8>>'")]
    [InlineData("    - mask: u8\n    - value: 'when<mask&0xGG,u8>'")]
    [InlineData("    - mask: u8\n      except: [other]\n    - value: 'when<mask&0x01,u8>'")]
    [InlineData("    - gate: u8\n    - mask: 'when<gate&0x01,u8>'\n    - value: 'when<mask&0x01,u8>'")]
    public void InvalidDependenciesFailAtTableLoad(string fields) =>
        Assert.Throws<ConfigException>(() => Load("version: 1\nstructs:\n  Root:\n" + fields + "\n"));

    /// <summary>A writer never invents the mask or substitutes a zero for a missing selected value.</summary>
    [Fact]
    public void WriterRejectsContradictoryOrMissingValues()
    {
        var tables = Load("version: 1\nstructs:\n  Root:\n    - mask: u8\n    - value: 'when<mask&0x01,u32>'\n");
        Assert.Throws<ArgumentException>(() => PacketCodec.Write(tables, "base", "Root", new Dictionary<string, object?>()));
        Assert.Throws<ArgumentException>(() => PacketCodec.Write(tables, "base", "Root", new Dictionary<string, object?> { ["mask"] = 0, ["value"] = 0 }));
        Assert.Throws<ArgumentException>(() => PacketCodec.Write(tables, "base", "Root", new Dictionary<string, object?> { ["mask"] = 1 }));
        Assert.Throws<ArgumentException>(() => PacketCodec.Write(tables, "base", "Root", new Dictionary<string, object?> { ["mask"] = 1, ["value"] = null }));
        Assert.Equal(new byte[] { 0 }, PacketCodec.Write(tables, "base", "Root", new Dictionary<string, object?> { ["mask"] = 0, ["value"] = null }));
        Assert.Equal(new byte[] { 1, 0, 0, 0, 0 }, PacketCodec.Write(tables, "base", "Root", new Dictionary<string, object?> { ["mask"] = 1, ["value"] = 0 }));
    }

    /// <summary>Native signed word objects retain their unsigned high bit when evaluating a gate.</summary>
    [Theory]
    [InlineData("u8", "80", "80", 128L)]
    [InlineData("u16", "8000", "0080", 32768L)]
    [InlineData("u32", "80000000", "00000080", -2147483648L)]
    [InlineData("u64", "8000000000000000", "0000000000000080", long.MinValue)]
    public void HighestBitWorksAtEveryUnsignedWidth(string type, string mask, string prefix, long number)
    {
        var tables = Load($"version: 1\nstructs:\n  Root:\n    - mask: {type}\n    - value: 'when<mask&0x{mask},u8>'\n");
        var bytes = Convert.FromHexString(prefix + "AB");
        var decoded = PacketCodec.Read(tables, "base", "Root", bytes);
        Assert.Equal((byte)0xAB, decoded["value"]);
        Assert.Equal(bytes, PacketCodec.Write(tables, "base", "Root", decoded));
        Assert.Equal(bytes, PacketCodec.Write(tables, "base", "Root", new Dictionary<string, object?> { ["mask"] = number, ["value"] = 0xAB }));
    }

    /// <summary>Any selected bit admits a nested map; inner mask names resolve inside that map.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void NestedGatesUseTheirOwnScopeAndAnyBit(int mask)
    {
        var tables = Load("""
            version: 1
            structs:
              Root:
                - mask: u8
                - child: 'when<mask&0x03,Child>'
              Child:
                - mask: u16
                - value: 'when<mask&0x100,u32>'
            """);
        byte[] bytes = mask == 0 ? [0] : [(byte)mask, 0, 1, 0x44, 0x33, 0x22, 0x11];
        var decoded = PacketCodec.Read(tables, "base", "Root", bytes);
        Assert.Equal(mask != 0, decoded.ContainsKey("child"));
        if (mask != 0)
            Assert.Equal(0x11223344, Assert.IsType<Dictionary<string, object?>>(decoded["child"])["value"]);
        Assert.Equal(bytes, PacketCodec.Write(tables, "base", "Root", decoded));
        Assert.Throws<ArgumentException>(() => PacketCodec.Write(tables, "base", "Root", new Dictionary<string, object?> { ["mask"] = 1, ["child"] = 1 }));
        Assert.Throws<ConfigException>(() => PacketCodec.ReadScalars(tables, "base", "Root", bytes));
    }

    /// <summary>String rewriting preserves skipped fields and noncanonical numeric bits without adding absent text.</summary>
    [Fact]
    public void StringWalkerKeepsGatesAndOnlyReplacesPresentText()
    {
        var tables = Load("""
            version: 1
            structs:
              Root:
                - mask: u8
                - text: 'when<mask&0x01,wstr>'
                - value: 'when<mask&0x02,u32>'
                - tail: wstr
            """);
        var bytes = Convert.FromHexString("02FFFFFFFF010000004100");
        Assert.Equal(Convert.FromHexString("02FFFFFFFF0200000042004300"),
            PacketCodec.ReplaceStrings(tables, "base", "Root", bytes, new Dictionary<string, string> { ["tail"] = "BC" }));
        Assert.Throws<ArgumentException>(() => PacketCodec.ReplaceStrings(tables, "base", "Root", bytes, new Dictionary<string, string> { ["text"] = "B" }));
        var present = Convert.FromHexString("0101000000410000000000");
        Assert.Equal(Convert.FromHexString("01020000004200430000000000"),
            PacketCodec.ReplaceStrings(tables, "base", "Root", present, new Dictionary<string, string> { ["text"] = "BC" }));
    }

    /// <summary>A selected nested conditional block retains the existing reader allocation and depth bounds.</summary>
    [Fact]
    public void ConditionalStructureStillHonorsReadLimits()
    {
        var tables = Load("version: 1\nstructs:\n  Root:\n    - mask: u8\n    - child: 'when<mask&0x01,Child>'\n  Child:\n    - value: u32\n");
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(tables, "base", "Root", [1, 0, 0, 0, 0], new PacketReadLimits(MaxDepth: 1)));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(tables, "base", "Root", [1, 0, 0, 0, 0], new PacketReadLimits(MaxValues: 2)));
        Assert.False(PacketCodec.Read(tables, "base", "Root", [0], new PacketReadLimits(MaxDepth: 1)).ContainsKey("child"));
    }
}
