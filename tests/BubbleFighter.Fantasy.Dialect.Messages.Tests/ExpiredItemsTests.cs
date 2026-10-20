using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Wallet;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Tests;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>
/// Pins the native expiry notice (S2C gspCheckExpiredTimeResult): a u32 count and that many u32 ids
/// (kr-812 sub_840820), the capability, and its place after the menu in the login sequence.
/// </summary>
public sealed class ExpiredItemsTests
{
    // 300002 = 0x000493E2, 204001 = 0x00031CE1.
    private const string TwoItemsHex = "02000000" + "E2930400" + "E11C0300";

    [Theory]
    [InlineData("kr-812")]
    [InlineData("kr-1256")]
    [InlineData("cn-717")]
    [InlineData("cn-324")]
    [InlineData("cn-279")]
    [InlineData("id-400")]
    [InlineData("id-1809")]
    [InlineData("cn-706")]
    [InlineData("cn-406")]
    public void TheCountedIdListIsTwelveBytesForTwoItems(string build)
    {
        Assert.True(TestBuilds.Packets.CapabilityOf(build, "expired_item_notice"));
        var body = ExpiredItemsBinding.Encode(TestBuilds.Packets, build, [300002, 204001]);
        Assert.Equal(12, body.Length);
        Assert.Equal(TwoItemsHex, Convert.ToHexString(body));
    }

    [Fact]
    public void AnUnauditedFamilyDoesNotInheritTheCapability()
    {
        Assert.False(TestBuilds.Packets.CapabilityOf("cn-735", "expired_item_notice"));
    }

    [Fact]
    public async Task TheNoticeUsesTheBuildsOwnOpcodeAndRejectsEmptyOrInvalidLists()
    {
        var session = new RecordingSession();
        var binding = new ExpiredItemsBinding("kr-812/game", new Table(), TestBuilds.Packets, "kr-812");
        await binding.SendAsync(session, new ItemsExpired([300002, 204001]), default);
        var frame = Assert.Single(session.Sent);
        Assert.Equal((ushort)806, frame.Opcode);
        Assert.Equal(TwoItemsHex, Convert.ToHexString(frame.Payload));
        await Assert.ThrowsAsync<ArgumentException>(() => binding.SendAsync(session, new ItemsExpired([]), default).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => binding.SendAsync(session, new ItemsExpired([0]), default).AsTask());
    }

    [Fact]
    public void ABuildWhoseTableLacksTheRowFailsWhenTheBindingIsCreated()
    {
        Assert.ThrowsAny<Exception>(() => new ExpiredItemsBinding("kr-812/game", new EmptyTable(), TestBuilds.Packets, "kr-812"));
    }

    /// <summary>A table that retypes the counted id list is refused when the sender is created.</summary>
    [Fact]
    public void AFieldWithTheWrongTypeIsRefusedWhenTheBindingIsCreated()
    {
        var directory = PacketTableEdits.WithFieldType("ExpiredItems", "item_ids", "\"list<u32>\"", "u32");
        var error = Assert.Throws<ConfigException>(() => new ExpiredItemsBinding("kr-812/game",
            TestBuilds.Opcodes("kr-812"), PacketTables.Load(directory.FullName), "kr-812"));
        Assert.Contains("item_ids", error.Message, StringComparison.Ordinal);
        Assert.Contains("Expired items", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoginAnnouncesExpiredItemsAfterTheMenuAndOnlyWhenSomeExpired()
    {
        var services = new ServiceRegistry();
        var events = new EventBus(NullLogger.Instance);
        using var room = services.RegisterMany<IMessageSender<MainMenuRoomOpened>>(new MenuSender());
        using var scope = new InventoryBaselineScope(4, TimeProvider.System);
        var inventory = new InventoryBinding("kr-812/game", TestBuilds.Opcodes("kr-812"),
            TestBuilds.Packets, "kr-812", scope);
        var notice = new ExpiredItemsBinding("kr-812/game", new Table(), TestBuilds.Packets, "kr-812");
        var binding = new LoginBinding("kr-812/game", LoginValues.From("kr-812", TestBuilds.Packets.ValuesOf("kr-812")),
            3, TestBuilds.Opcodes("kr-812"), TestBuilds.Packets, "kr-812", services, events, inventory, notice,
            NullLogger.Instance);

        var withExpiry = new RecordingSession();
        await binding.SendAsync(withExpiry, Login() with { ExpiredItemIds = [204001] }, default);
        Assert.Equal(new ushort[] { 3, 105, 801, 100, 806 }, withExpiry.Sent.Select(frame => frame.Opcode));

        var without = new RecordingSession();
        await binding.SendAsync(without, Login(), default);
        Assert.Equal(new ushort[] { 3, 105, 801, 100 }, without.Sent.Select(frame => frame.Opcode));
    }

    [Fact]
    public void TheModernBuildReadsTheSameCountedList()
    {
        // kr-1256 dispatch case 807 runs kr-1256 sub_998FB0 -> kr-1256 sub_999C00: a u32 count, then that many
        // 4-byte ids appended in order; the consumer is kr-1256 sub_93C6E0.
        var body = ExpiredItemsBinding.Encode(TestBuilds.Packets, "kr-1256", [300002, 204001]);
        Assert.Equal(TwoItemsHex, Convert.ToHexString(body));
        var offset = 0;
        var count = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(offset));
        offset += 4;
        var ids = new List<int>();
        for (var index = 0; index < count; index++)
        {
            ids.Add(BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(offset)));
            offset += 4;
        }
        Assert.Equal([300002, 204001], ids.ToArray());
        Assert.Equal(body.Length, offset);
    }

    [Fact]
    public async Task TheModernBurstSendsTheEmptyFirstMoveChannel()
    {
        // kr-1256 sub_9D2FF0 case 163 stores the label and sets a session flag (byte_18E55CF); it reads no
        // body, so the frame the shared login sends for it is empty.
        var session = new RecordingSession();
        var services = new ServiceRegistry();
        var events = new EventBus(NullLogger.Instance);
        using var scope = new InventoryBaselineScope(4, TimeProvider.System);
        var inventory = new InventoryBinding("kr-1256/game", TestBuilds.Opcodes("kr-1256"),
            TestBuilds.Packets, "kr-1256", scope);
        var binding = new LoginBinding("kr-1256/game",
            LoginValues.From("kr-1256", TestBuilds.Packets.ValuesOf("kr-1256")),
            3, TestBuilds.Opcodes("kr-1256"), TestBuilds.Packets, "kr-1256", services, events, inventory, null,
            NullLogger.Instance);
        await binding.SendAsync(session, Login(), default);
        var channel = TestBuilds.Opcodes("kr-1256").S2C("gspNotifyFirstMoveChannel");
        var frame = session.Sent.Single(sent => sent.Opcode == channel);
        Assert.Empty(frame.Payload);
    }

    [Theory]
    [InlineData("kr-1256")]
    public async Task TheModernBurstAnnouncesExpiredItemsAfterTheMenu(string build)
    {
        var session = new RecordingSession();
        var services = new ServiceRegistry();
        var events = new EventBus(NullLogger.Instance);
        using var scope = new InventoryBaselineScope(4, TimeProvider.System);
        var inventory = new InventoryBinding(build + "/game", TestBuilds.Opcodes(build),
            TestBuilds.Packets, build, scope);
        var notice = new ExpiredItemsBinding(build + "/game", TestBuilds.Opcodes(build),
            TestBuilds.Packets, build);
        var binding = new LoginBinding(build + "/game",
            LoginValues.From(build, TestBuilds.Packets.ValuesOf(build)),
            3, TestBuilds.Opcodes(build), TestBuilds.Packets, build, services, events, inventory, notice,
            NullLogger.Instance);

        var withExpiry = new RecordingSession();
        await binding.SendAsync(withExpiry, Login() with { ExpiredItemIds = [204001] }, default);
        var expired = TestBuilds.Opcodes(build).S2C("gspCheckExpiredTimeResult");
        var noticeFrame = Assert.Single(withExpiry.Sent, sent => sent.Opcode == expired);
        Assert.Equal("01000000" + "E11C0300", Convert.ToHexString(noticeFrame.Payload));
        Assert.Equal(expired, withExpiry.Sent[^1].Opcode);

        var without = new RecordingSession();
        await binding.SendAsync(without, Login(), default);
        Assert.DoesNotContain(without.Sent, sent => sent.Opcode == expired);
    }

    private static LoginSucceeded Login() => new(42, "account", "ab", 17, new DateTime(2026, 10, 7),
        300001, [300001], new PlayerWallet(42, 12345, 678), [], []);

    private sealed class MenuSender : IMessageSender<MainMenuRoomOpened>
    {
        public string Protocol => "kr-812/game";
        public ValueTask SendAsync(ISession session, MainMenuRoomOpened message, CancellationToken ct) =>
            session.SendAsync(100, ReadOnlyMemory<byte>.Empty, ct);
    }

    private class EmptyTable : IOpcodeTable
    {
        public ClientProfile Profile => throw new NotSupportedException();
        public OpcodeConnection Connection => OpcodeConnection.Game;
        public virtual IEnumerable<OpcodeEntry> All => [];
        public IOpcodeTable ForConnection(OpcodeConnection connection) => this;
        public bool TryGet(PacketDirection direction, ushort value, out OpcodeEntry? entry) => throw new NotSupportedException();
        public string Describe(PacketDirection direction, ushort value) => throw new NotSupportedException();
    }

    private sealed class Table : EmptyTable
    {
        public override IEnumerable<OpcodeEntry> All =>
            [new(806, PacketDirection.ServerToClient, "gspCheckExpiredTimeResult", Evidence.Verified, null, null)];
    }

    private sealed class RecordingSession : ISession
    {
        public long Id => 1;
        public IConnection Connection { get; } = new StubConnection();
        public ClientProfile Profile { get; } = new("kr-812", "kr", 812, 458, 0, "kr-812");
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
        public EndPoint RemoteEndPoint => new IPEndPoint(IPAddress.Loopback, 1);
        public PipeReader Input => throw new NotSupportedException();
        public PipeWriter Output => throw new NotSupportedException();
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public bool IsClosed => false;
        public ValueTask CloseAsync(string reason) => ValueTask.CompletedTask;
    }
}
