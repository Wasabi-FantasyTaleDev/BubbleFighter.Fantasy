using System.Collections.Concurrent;
using System.IO.Compression;
using System.Linq;
using System.IO.Pipelines;
using System.Net;
using System.Security.Cryptography;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Tests;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>
/// Pins the modern family's bag publication against the bytes the hand-written codec produced before the
/// move (the temporary comparison ran the old writer and this binding on the same rows) and walks the
/// body in the client's read order. kr-1256 sub_FA5520 wraps bag, slot and article bodies;
/// cn-774 sub_F3CD00 wraps only articles; tw-454 sub_A8B870 reads article counts directly.
/// </summary>
public sealed class ModernInventoryTableTests
{
    private static readonly InventorySlotRecord[] CharacterBag =
    [
        new(42, InventoryBag.CharacterBag, 0x1122334455667788L, 300001, 99999, 1) { Lifetime = InventoryLifetime.Permanent },
        new(42, InventoryBag.CharacterBag, 0x0A0B0C0D0E0F1011L, 202001, 7, 1) { Lifetime = InventoryLifetime.Permanent },
        new(42, InventoryBag.CharacterBag, 0x0102030405060708L, 100123, 0, 0) { Lifetime = InventoryLifetime.Permanent },
    ];

    private static readonly InventorySlotRecord[] EquippedBag =
    [
        new(42, InventoryBag.EquippedBag, 0x1122334455667788L, 300001, 99999, 1) { Lifetime = InventoryLifetime.Permanent },
        new(42, InventoryBag.EquippedBag, 0x0102030405060708L, 100123, 0, 0) { Lifetime = InventoryLifetime.Permanent },
    ];

    /// <summary>Pins each modern build's bag, equipped-slot, Article and cash payloads in their native replacement order.</summary>
    [Theory]
    [InlineData("kr-1256", "C3BD06C4B2EF7CE011CCC7D6A3B4DCA4", "9B96FCADA055CBADC94D8EA51B39ED85",
        "816DB3A78937E9CA4069EAE6192C30F1", "DF3F619804A92FDB4057192DC43DD748")]
    [InlineData("cn-774", "FE78442D185BFE08ACC837484333A7C9", "209A0E3BF131AA797E1051C6421BE7DD",
        "816DB3A78937E9CA4069EAE6192C30F1", "DF3F619804A92FDB4057192DC43DD748")]
    [InlineData("tw-454", "FE78442D185BFE08ACC837484333A7C9", "209A0E3BF131AA797E1051C6421BE7DD",
        "DF3F619804A92FDB4057192DC43DD748", "DF3F619804A92FDB4057192DC43DD748")]
    public async Task WholeBagRestatementSendsTheAuditedBodies(string build, string bag, string slots,
        string articles, string cash)
    {
        var session = new RecordingSession();
        await Binding(build).SendAsync(session, new InventoryReplaced(CharacterBag, EquippedBag), default);
        var opcodes = TestBuilds.Opcodes(build);
        Assert.Equal(
            new ushort[]
            {
                opcodes.S2C("gspInventoryUpdateAllInventorySlot"),
                opcodes.S2C("gspInventoryUpdateInventorySlots"),
                opcodes.S2C("gspUpdateAllArticleSlot"),
                opcodes.S2C("gspCashInvenUpdateAllInventorySlot"),
            },
            session.Sent.Select(frame => frame.Opcode).ToArray());
        Assert.Equal(bag, Hash(session.Sent[0].Payload));
        Assert.Equal(slots, Hash(session.Sent[1].Payload));
        Assert.Equal(articles, Hash(session.Sent[2].Payload));
        Assert.Equal(cash, Hash(session.Sent[3].Payload));
    }

    /// <summary>The article packet follows its own native envelope even when ordinary inventory is plain.</summary>
    [Theory]
    [InlineData("kr-1256", true)]
    [InlineData("cn-774", true)]
    [InlineData("tw-454", false)]
    public async Task ArticleEnvelopeMatchesTheNativeReader(string build, bool compressed)
    {
        var session = new RecordingSession();
        await Binding(build).SendAsync(session, new InventoryReplaced([], []), default);
        var articleOpcode = TestBuilds.Opcodes(build).S2C("gspUpdateAllArticleSlot");
        var payload = Assert.Single(session.Sent, frame => frame.Opcode == articleOpcode).Payload;
        var body = compressed ? Decompress(payload) : payload;
        Assert.Equal("00000000", Convert.ToHexString(body));
        if (compressed)
            Assert.True(payload.Length > body.Length);
    }

    /// <summary>An omitted article wrapper follows the common wrapper; descendants can override just articles.</summary>
    [Theory]
    [InlineData("kr-812", "plain", "plain")]
    [InlineData("kr-1256", "zlib", "zlib")]
    [InlineData("cn-774", "plain", "zlib")]
    [InlineData("tw-454", "plain", "plain")]
    public void ArticleEnvelopeUsesTheValidatedBuildValue(string build, string common, string article)
    {
        var values = InventoryValues.Of(TestBuilds.Packets, build);
        Assert.Equal(common, values.Envelope);
        Assert.Equal(article, values.ArticleEnvelope);
    }

    /// <summary>Inherited inventory values admit only the explicitly named ordinary Article build.</summary>
    [Theory]
    [InlineData("kr-812", false)]
    [InlineData("kr-1256", true)]
    [InlineData("cn-774", false)]
    [InlineData("tw-454", false)]
    public void OrdinaryArticleAdmissionUsesExactBuildMembership(string build, bool admitted) =>
        Assert.Equal(admitted, InventoryValues.Of(TestBuilds.Packets, build).OrdinaryArticlesAdmitted);

    /// <summary>Removing or emptying the admission list keeps ordinary ownership out of the Article table.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("[]")]
    public void MissingOrEmptyOrdinaryArticleAdmissionIsConservative(string? admission)
    {
        var tables = WithBuildValues("      ordinary_article_builds: [kr-1256]\n",
            admission is null ? "" : $"      ordinary_article_builds: {admission}\n");
        foreach (var build in new[] { "kr-1256", "cn-774", "tw-454" })
            Assert.False(InventoryValues.Of(tables, build).OrdinaryArticlesAdmitted);
    }

    /// <summary>An explicitly added build does not admit its own descendants through packet inheritance.</summary>
    [Fact]
    public void OrdinaryArticleAdmissionDoesNotExpandToDescendants()
    {
        var tables = WithBuildValues("ordinary_article_builds: [kr-1256]", "ordinary_article_builds: [kr-1256, cn-774]");
        Assert.True(InventoryValues.Of(tables, "kr-1256").OrdinaryArticlesAdmitted);
        Assert.True(InventoryValues.Of(tables, "cn-774").OrdinaryArticlesAdmitted);
        Assert.False(InventoryValues.Of(tables, "tw-454").OrdinaryArticlesAdmitted);
    }

    /// <summary>Malformed, duplicate and unknown ordinary Article admissions fail before publication.</summary>
    [Theory]
    [InlineData("true")]
    [InlineData("kr-1256")]
    [InlineData("[kr-1256, kr-1256]")]
    [InlineData("[unknown-build]")]
    [InlineData("[KR-1256]")]
    [InlineData("null")]
    [InlineData("{}")]
    public void InvalidOrdinaryArticleAdmissionFailsAtActivation(string admission)
    {
        var tables = WithBuildValues("ordinary_article_builds: [kr-1256]", "ordinary_article_builds: " + admission);
        var error = Assert.Throws<ConfigException>(() => InventoryValues.Of(tables, "kr-1256"));
        Assert.Contains("ordinary_article_builds", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Admitted ordinary ownership requires the complete Article table to be part of replacement publication.</summary>
    [Fact]
    public void OrdinaryArticleAdmissionRequiresTheFullTablePublicationStep()
    {
        var tables = WithBuildValues("replaced: [bag, equipped, articles, cash]", "replaced: [bag, equipped, cash]");
        var error = Assert.Throws<ConfigException>(() =>
            new InventoryBinding("kr-1256/game", TestBuilds.Opcodes("kr-1256"), tables, "kr-1256"));
        Assert.Contains("complete native Article publication", error.Message, StringComparison.Ordinal);
    }

    /// <summary>An empty-only Article layout cannot accept ordinary Article admission even when its packet is published.</summary>
    [Fact]
    public void OrdinaryArticleAdmissionRequiresOwnedArticleRows()
    {
        var directory = PacketTableEdits.WithFieldType("ArticleSlots", "articles", "\"list<ArticleSlot>\"", "empty_list");
        var tables = PacketTables.Load(directory.FullName);
        var error = Assert.Throws<ConfigException>(() =>
            new InventoryBinding("kr-1256/game", TestBuilds.Opcodes("kr-1256"), tables, "kr-1256"));
        Assert.Contains("complete native Article publication", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Unsupported wrapper names and nonscalar settings fail before inventory publication.</summary>
    [Theory]
    [InlineData("gzip")]
    [InlineData("[zlib]")]
    public void InvalidArticleEnvelopeFailsAtActivation(string replacement)
    {
        var directory = TestTempDirectories.ForTest("bff-inventory-envelope");
        foreach (var file in Directory.GetFiles(TestBuilds.PacketsDirectory(), "*.yaml"))
            File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
        var path = Path.Combine(directory, "builds.yaml");
        File.WriteAllText(path, File.ReadAllText(path).Replace("article_envelope: zlib",
            "article_envelope: " + replacement, StringComparison.Ordinal));
        var tables = PacketTables.Load(directory);
        var error = Assert.Throws<ConfigException>(() => InventoryValues.Of(tables, "cn-774"));
        Assert.Contains("article_envelope", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Changing an article container or nested field type is rejected when the wire is constructed.</summary>
    [Theory]
    [InlineData("ArticleSlots", "articles", "\"list<ArticleSlot>\"", "\"list<u32>\"")]
    [InlineData("ArticleSlot", "article_key", "u64", "u32")]
    [InlineData("ArticleSlot", "equipped", "bool", "u8")]
    [InlineData("ArticleSlot", "attributes", "\"list<ItemAttr>\"", "\"list<u32>\"")]
    [InlineData("ItemAttr", "unknown_12", "\"list<u32>\"", "u32")]
    public void ArticleLayoutsAreValidatedBeforePublication(string structure, string field, string from, string to)
    {
        var directory = PacketTableEdits.WithFieldType(structure, field, from, to);
        var tables = PacketTables.Load(directory.FullName);
        var error = Assert.Throws<ConfigException>(() => new InventoryWire(tables, "kr-1256"));
        Assert.Contains(structure, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("kr-1256", "C3BD06C4B2EF7CE011CCC7D6A3B4DCA4", "369A6EA85EED3AEBD1E688B74C9A8481")]
    [InlineData("cn-774", "FE78442D185BFE08ACC837484333A7C9", "FE78442D185BFE08ACC837484333A7C9")]
    public async Task PurchaseSendsTheBagThenTheSlotsPacket(string build, string bag, string slots)
    {
        var session = new RecordingSession();
        await Binding(build).SendAsync(session, new InventoryPurchaseApplied(CharacterBag), default);
        var opcodes = TestBuilds.Opcodes(build);
        Assert.Equal(
            new ushort[]
            {
                opcodes.S2C("gspInventoryUpdateAllInventorySlot"),
                opcodes.S2C("gspInventoryUpdateInventorySlots"),
            },
            session.Sent.Select(frame => frame.Opcode).ToArray());
        Assert.Equal(bag, Hash(session.Sent[0].Payload));
        Assert.Equal(slots, Hash(session.Sent[1].Payload));
    }

    [Fact]
    public async Task ChangedSlotsSendTheSlotsPacket()
    {
        var session = new RecordingSession();
        var build = "kr-1256";
        await Binding(build).SendAsync(session, new InventorySlotsUpdated([CharacterBag[0]]), default);
        var frame = Assert.Single(session.Sent);
        Assert.Equal(TestBuilds.Opcodes(build).S2C("gspInventoryUpdateInventorySlots"), frame.Opcode);
        Assert.Equal("1B3A35018CFB4D7CE4B9434D9807164F", Hash(frame.Payload));
    }

    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    public async Task TheBagBodyIsConsumedExactlyInTheClientReadOrder(string build)
    {
        var session = new RecordingSession();
        await Binding(build).SendAsync(session, new InventoryReplaced(CharacterBag, EquippedBag), default);
        var body = build == "kr-1256" ? Decompress(session.Sent[0].Payload) : session.Sent[0].Payload;
        var reader = new PacketReader(body);
        Assert.Equal((uint)CharacterBag.Length, reader.ReadUInt32());
        // The modern rows are mirrored in their stored order, not ranked by item id.
        foreach (var slot in CharacterBag)
        {
            Assert.Equal(slot.ItemId, reader.ReadInt32());
            Assert.Equal(slot.Quantity, reader.ReadInt32());
            Assert.Equal(slot.SlotKey, reader.ReadInt64());
            Assert.Equal(string.Empty, reader.ReadWideStringPrefixed());
            Assert.Equal(0u, reader.ReadUInt32());
            Assert.Equal(slot.Flags, reader.ReadByte());
        }
        Assert.Equal(0, reader.Remaining);
    }

    [Fact]
    public async Task TheSlotsBodyCarriesTheThreeModernContainers()
    {
        var session = new RecordingSession();
        var build = "kr-1256";
        await Binding(build).SendAsync(session, new InventoryReplaced(CharacterBag, EquippedBag), default);
        var reader = new PacketReader(Decompress(session.Sent[1].Payload));
        Assert.Equal((uint)EquippedBag.Length, reader.ReadUInt32());
        reader.ReadInt32(); reader.ReadInt32(); reader.ReadInt64(); reader.ReadWideStringPrefixed();
        Assert.Equal(0u, reader.ReadUInt32());
        reader.ReadByte();
        reader.ReadInt32(); reader.ReadInt32(); reader.ReadInt64(); reader.ReadWideStringPrefixed();
        Assert.Equal(0u, reader.ReadUInt32());
        reader.ReadByte();
        Assert.Equal(0u, reader.ReadUInt32()); // the empty article container
        Assert.Equal(0u, reader.ReadUInt32()); // the empty removal list
        Assert.Equal(0, reader.Remaining);
    }

    private static InventoryBinding Binding(string build) => new(build + "/game", TestBuilds.Opcodes(build), TestBuilds.Packets, build);

    private static PacketTables WithBuildValues(string before, string after)
    {
        var directory = TestTempDirectories.ForTest("bff-inventory-admission");
        foreach (var file in Directory.GetFiles(TestBuilds.PacketsDirectory(), "*.yaml"))
            File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
        var path = Path.Combine(directory, "builds.yaml");
        var source = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Contains(before, source, StringComparison.Ordinal);
        File.WriteAllText(path, source.Replace(before, after, StringComparison.Ordinal));
        return PacketTables.Load(directory);
    }

    private static byte[] Decompress(byte[] body)
    {
        var length = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(body);
        Assert.Equal(body.Length - 4, length);
        using var source = new MemoryStream(body, 4, length);
        using var zip = new ZLibStream(source, CompressionMode.Decompress);
        using var plain = new MemoryStream();
        zip.CopyTo(plain);
        return plain.ToArray();
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes))[..32];

    private sealed class RecordingSession : ISession
    {
        public long Id => 1;

        public IConnection Connection { get; } = new StubConnection();

        public ClientProfile Profile { get; } = new("kr-1256", "kr", 1256, 0, 0, "kr-1256");

        public ConcurrentDictionary<string, object> Items { get; } = new();

        public List<(ushort Opcode, byte[] Payload)> Sent { get; } = [];

        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            Sent.Add((opcode, payload.ToArray()));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StubConnection : IConnection
    {
        public long Id => 1;
        public EndPoint RemoteEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 1);
        public PipeReader Input => throw new NotSupportedException();
        public PipeWriter Output => throw new NotSupportedException();
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public bool IsClosed => false;
        public ValueTask CloseAsync(string reason) => ValueTask.CompletedTask;
    }
}
