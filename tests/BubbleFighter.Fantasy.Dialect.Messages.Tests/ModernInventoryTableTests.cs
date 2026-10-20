using System.Collections.Concurrent;
using System.IO.Compression;
using System.Linq;
using System.IO.Pipelines;
using System.Net;
using System.Security.Cryptography;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>
/// Pins the modern family's bag publication against the bytes the hand-written codec produced before the
/// move (the temporary comparison ran the old writer and this binding on the same rows) and walks the
/// body in the client's read order. kr-1256 wraps the bodies in a compressed envelope
/// (kr-1256 sub_FA5520); cn-774 and tw-454 read the counts directly.
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

    // SHA-256 prefixes of the bodies the former hand-written writer sent for the same rows: the
    // replaced sequence (bag, equipped slots, article table, cash inventory) per build.
    [Theory]
    [InlineData("kr-1256", "C3BD06C4B2EF7CE011CCC7D6A3B4DCA4", "9B96FCADA055CBADC94D8EA51B39ED85",
        "816DB3A78937E9CA4069EAE6192C30F1", "DF3F619804A92FDB4057192DC43DD748")]
    [InlineData("cn-774", "FE78442D185BFE08ACC837484333A7C9", "209A0E3BF131AA797E1051C6421BE7DD",
        "DF3F619804A92FDB4057192DC43DD748", "DF3F619804A92FDB4057192DC43DD748")]
    [InlineData("tw-454", "FE78442D185BFE08ACC837484333A7C9", "209A0E3BF131AA797E1051C6421BE7DD",
        "DF3F619804A92FDB4057192DC43DD748", "DF3F619804A92FDB4057192DC43DD748")]
    public async Task WholeBagRestatementSendsTheFormerWriterBytes(string build, string bag, string slots,
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

    private static byte[] Decompress(byte[] body)
    {
        var length = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(body);
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
