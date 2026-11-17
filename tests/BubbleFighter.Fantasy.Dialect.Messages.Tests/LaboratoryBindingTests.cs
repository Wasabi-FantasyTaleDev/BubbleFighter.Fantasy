using System.Buffers.Binary;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Enhancement;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

public sealed class LaboratoryBindingTests
{
    private const string RequestHex = "000000000B000000010000000200000000000000000000000300000065000000660000006700000002000000FFFFFFFFFFFFFFFF";

    [Fact]
    public void NativeWordVectorsAndBool32ArePinnedAndTruncationCannotDispatch()
    {
        using var baselines = new InventoryBaselineScope(2, TimeProvider.System);
        var binding = new LaboratoryBinding("kr-812/game", TestBuilds.Opcodes("kr-812"), TestBuilds.Packets, "kr-812", baselines);
        var body = Convert.FromHexString(RequestHex);
        Assert.Equal(52, body.Length);
        var message = Assert.IsType<LaboratoryRequested>(binding.Read(body, out var operation));
        Assert.Equal(LaboratoryOperation.Synthesis, operation);
        Assert.Equal(11, message.CardItemId);
        Assert.Equal(2, message.Category);
        Assert.False(message.Grouped);
        Assert.Equal([101, 102, 103], message.Materials);
        Assert.Equal([-1, -1], message.Additives);
        for (var length = 0; length < body.Length; length++)
            Assert.Throws<PacketFormatException>(() => binding.Read(body[..length], out _));
        Assert.Throws<PacketFormatException>(() => binding.Read([.. body, 0], out _));
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(16), 2);
        Assert.Throws<PacketFormatException>(() => binding.Read(body, out _));
    }

    [Fact]
    public void SecondaryPasswordAndDiagnosticPathNeverReachBusinessLogic()
    {
        using var baselines = new InventoryBaselineScope(2, TimeProvider.System);
        var binding = new LaboratoryBinding("kr-812/game", TestBuilds.Opcodes("kr-812"), TestBuilds.Packets, "kr-812", baselines);
        var body = Convert.FromHexString(RequestHex);
        byte[] password = [1, 0, 0, 0, (byte)'x', 0, .. body[4..]];
        Assert.Null(binding.Read(password, out _));
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(20), 9999);
        Assert.Null(binding.Read(body, out _));
        var fields = PacketCodec.Read(TestBuilds.Packets, "kr-812", "SynthesisProcessRequest", password);
        Assert.Equal("x", fields["secondary_password"]);
    }

    /// <summary>Pins the shared inventory origin, symbolic result opcode and forty-byte native laboratory completion body.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletionUsesSharedBaselineAndSymbolicOpcodeWithFortyByteReply(bool remap)
    {
        var source = TestBuilds.Opcodes("kr-812");
        var table = remap ? new OpcodeTable(source.Profile, source.All.Select(row => row with { Value = checked((ushort)(row.Value + 1000)) })) : source;
        var now = new DateTimeOffset(2026, 11, 11, 0, 0, 0, TimeSpan.Zero);
        using var baselines = new InventoryBaselineScope(2, new Clock(now));
        var binding = new LaboratoryBinding("kr-812/game", table, TestBuilds.Packets, "kr-812", baselines);
        var session = new MatchMessagesMountTests.Session(table.Profile);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await binding.SendAsync(session,
            new LaboratoryCompleted(LaboratoryOperation.Synthesis, 101, InventoryLifetime.Until(now.AddDays(12.5))), default));
        using (var publication = await baselines.EnterAsync(session.Connection, default))
            publication.Reset(publication.PrepareReset());
        await binding.SendAsync(session, new LaboratoryCompleted(LaboratoryOperation.Synthesis, 101, InventoryLifetime.Until(now.AddDays(12.5))), default);
        Assert.Equal(table.S2C("gspSynthesisProcess"), Assert.Single(session.Sent));
        Assert.Equal("01000000000000000000000000000000650000000000000000002940000000000000000000000000", Convert.ToHexString(Assert.Single(session.Bodies)));
        Assert.Equal(40, session.Bodies[0].Length);
        await binding.SendAsync(session, new LaboratoryRefused(LaboratoryOperation.Decomposition), default);
        Assert.Equal("02000000FFFFFFFF0000000000000000000000000000000000000000000000000000000000000000", Convert.ToHexString(session.Bodies[1]));
    }

    [Fact]
    public async Task DecompositionAndAdditionalReferencesUseStockIdsWithNoInventoryQuantitySubstitution()
    {
        var table = TestBuilds.Opcodes("kr-812");
        using var baselines = new InventoryBaselineScope(2, TimeProvider.System);
        var binding = new LaboratoryBinding("kr-812/game", table, TestBuilds.Packets, "kr-812", baselines);
        var session = new MatchMessagesMountTests.Session(table.Profile);
        var expiry = InventoryLifetime.Until(DateTimeOffset.UtcNow.AddDays(13));
        await binding.SendAsync(session, new LaboratoryCompleted(LaboratoryOperation.Decomposition, 1000001, expiry,
            SecondReference: 1000002), default);
        Assert.Equal("0200000000000000000000000000000041420F00000000000000000042420F000000000000000000",
            Convert.ToHexString(Assert.Single(session.Bodies)));
        var fields = PacketCodec.Read(TestBuilds.Packets, "kr-812", "SynthesisProcessReply", session.Bodies[0]);
        Assert.Equal(1000001, fields["primary_reference"]);
        Assert.Equal(1000002, fields["second_reference"]);
        Assert.Equal(0.0, fields["remaining_days"]);
        await Assert.ThrowsAsync<ArgumentException>(() => binding.SendAsync(session,
            new LaboratoryCompleted(LaboratoryOperation.Decomposition, 100001, expiry), default).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => binding.SendAsync(session,
            new LaboratoryCompleted(LaboratoryOperation.Decomposition, 1000001, expiry, SecondReference: 1000002, ThirdReference: 1000003), default).AsTask());
        await binding.SendAsync(session, new LaboratoryCompleted(LaboratoryOperation.Synthesis, 100001, InventoryLifetime.Permanent,
            SecondReference: 1000002, ThirdReference: 1000003), default);
        fields = PacketCodec.Read(TestBuilds.Packets, "kr-812", "SynthesisProcessReply", session.Bodies[1]);
        Assert.Equal(100001, fields["primary_reference"]);
        Assert.Equal(1000002, fields["second_reference"]);
        Assert.Equal(1000003, fields["third_reference"]);
        await Assert.ThrowsAsync<ArgumentException>(() => binding.SendAsync(session,
            new LaboratoryCompleted(LaboratoryOperation.Synthesis, 100001, InventoryLifetime.Permanent, SecondReference: 100002), default).AsTask());
    }

    [Fact]
    public async Task MountAndUnloadOwnEveryRouteAndFallbackUsesNativeFailure()
    {
        var table = TestBuilds.Opcodes("kr-812");
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var context = fixture.Context("kr-812", "kr-812/game", "dialect.messages");
        using var baselines = new InventoryBaselineScope(2, TimeProvider.System);
        var binding = new LaboratoryBinding("kr-812/game", table, TestBuilds.Packets, "kr-812", baselines);
        var session = new MatchMessagesMountTests.Session(table.Profile);
        try
        {
            binding.Bind(context, fixture.Messages);
            await fixture.Packets.DispatchAsync("kr-812/game", session, new(table.C2S("gcpSynthesisProcess"), Convert.FromHexString(RequestHex)), default);
            Assert.IsType<LaboratoryRequested>(Assert.Single(fixture.Messages.Received));
            fixture.Messages.HasHandler = false;
            await fixture.Packets.DispatchAsync("kr-812/game", session, new(table.C2S("gcpSynthesisProcess"), Convert.FromHexString(RequestHex)), default);
            Assert.Equal(-1, BinaryPrimitives.ReadInt32LittleEndian(Assert.Single(session.Bodies).AsSpan(4)));
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(fixture.Packets.Handlers);
        Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<LaboratoryRequested>>());
        Assert.Empty(fixture.Services.GetAll<IMessageSender<LaboratoryCompleted>>());
        Assert.Empty(fixture.Services.GetAll<IMessageSender<LaboratoryRefused>>());
    }

    // cn-717 sub_A9C5C0: card, operation, category, the two counted lists, then the trailing word (no password, no set flag).
    private const string Cn717RequestHex = "0B00000001000000020000000300000065000000660000006700000002000000FFFFFFFFFFFFFFFF00000000";

    /// <summary>Pins cn-717's older 44-byte request and refuses its repeat probe before business logic.</summary>
    [Fact]
    public void Cn717RequestIsPinnedWithoutPasswordOrSetFlag()
    {
        using var baselines = new InventoryBaselineScope(2, TimeProvider.System);
        var binding = new LaboratoryBinding("cn-717/game", TestBuilds.Opcodes("cn-717"), TestBuilds.Packets, "cn-717", baselines);
        var body = Convert.FromHexString(Cn717RequestHex);
        Assert.Equal(44, body.Length);
        var message = Assert.IsType<LaboratoryRequested>(binding.Read(body, out var operation));
        Assert.Equal(LaboratoryOperation.Synthesis, operation);
        Assert.Equal(11, message.CardItemId);
        Assert.Equal(2, message.Category);
        Assert.False(message.Grouped);
        Assert.Equal([101, 102, 103], message.Materials);
        Assert.Equal([-1, -1], message.Additives);
        for (var length = 0; length < body.Length; length++)
            Assert.Throws<PacketFormatException>(() => binding.Read(body[..length], out _));
        // cn-717 sub_A9ADD0 sends 9999 on its repeat path.
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(40), 9999);
        Assert.Null(binding.Read(body, out _));
    }

    /// <summary>cn-717 sub_8B9120 reads the outcome from the result word of its 28-byte reply, which has no third slot.</summary>
    [Fact]
    public async Task Cn717ReplyCarriesTheOutcomeInItsResultWord()
    {
        var table = TestBuilds.Opcodes("cn-717");
        var now = new DateTimeOffset(2026, 11, 11, 0, 0, 0, TimeSpan.Zero);
        using var baselines = new InventoryBaselineScope(2, new Clock(now));
        var binding = new LaboratoryBinding("cn-717/game", table, TestBuilds.Packets, "cn-717", baselines);
        var session = new MatchMessagesMountTests.Session(table.Profile);
        using (var publication = await baselines.EnterAsync(session.Connection, default))
            publication.Reset(publication.PrepareReset());
        await binding.SendAsync(session, new LaboratoryCompleted(LaboratoryOperation.Synthesis, 101, InventoryLifetime.Until(now.AddDays(12.5))), default);
        Assert.Equal(table.S2C("gspSynthesisProcess"), Assert.Single(session.Sent));
        Assert.Equal("010000000000000065000000000000000000294000000000" + "00000000", Convert.ToHexString(session.Bodies[0]));
        Assert.Equal(28, session.Bodies[0].Length);
        await binding.SendAsync(session, new LaboratoryCompleted(LaboratoryOperation.Synthesis, 101, InventoryLifetime.Permanent,
            LaboratoryOutcome.BecamePermanent, SecondReference: 1000002), default);
        var fields = PacketCodec.Read(TestBuilds.Packets, "cn-717", "SynthesisProcessReply", session.Bodies[1]);
        Assert.Equal(3, fields["result"]);
        Assert.Equal(1000002, fields["second_reference"]);
        await binding.SendAsync(session, new LaboratoryRefused(LaboratoryOperation.Decomposition), default);
        Assert.Equal("02000000FFFFFFFF" + new string('0', 40), Convert.ToHexString(session.Bodies[2]));
        await Assert.ThrowsAsync<ArgumentException>(() => binding.SendAsync(session, new LaboratoryCompleted(LaboratoryOperation.Synthesis,
            101, InventoryLifetime.Permanent, SecondReference: 1000002, ThirdReference: 1000003), default).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => binding.SendAsync(session, new LaboratoryCompleted(LaboratoryOperation.Synthesis,
            101, InventoryLifetime.Permanent, LaboratoryOutcome.AdditivePermanent), default).AsTask());
    }

    /// <summary>A compact cn-406 reply announces the synthesis bonus as the Stock id in its one second slot.</summary>
    [Fact]
    public async Task Cn406ReplyCarriesTheBonusStockInItsSecondSlot()
    {
        var table = TestBuilds.Opcodes("cn-406");
        var now = new DateTimeOffset(2026, 11, 11, 0, 0, 0, TimeSpan.Zero);
        using var baselines = new InventoryBaselineScope(2, new Clock(now));
        var binding = new LaboratoryBinding("cn-406/game", table, TestBuilds.Packets, "cn-406", baselines);
        var session = new MatchMessagesMountTests.Session(table.Profile);
        using (var publication = await baselines.EnterAsync(session.Connection, default))
            publication.Reset(publication.PrepareReset());
        await binding.SendAsync(session, new LaboratoryCompleted(LaboratoryOperation.Synthesis, 101,
            InventoryLifetime.Until(now.AddDays(12.5)), SecondReference: 1011320), default);
        Assert.Equal(table.S2C("gspSynthesisProcess"), Assert.Single(session.Sent));
        Assert.Equal(28, session.Bodies[0].Length);
        var fields = PacketCodec.Read(TestBuilds.Packets, "cn-406", "SynthesisProcessReply", session.Bodies[0]);
        Assert.Equal(1011320, fields["second_reference"]);
        Assert.Equal("0100000000000000650000000000000000002940" + "786E0F00" + "00000000", Convert.ToHexString(session.Bodies[0]));
    }

    /// <summary>cn-717 binds and answers through its own symbolic opcodes, and unloads every route.</summary>
    [Fact]
    public async Task Cn717MountsAndFallsBackToTheNativeFailure()
    {
        var table = TestBuilds.Opcodes("cn-717");
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var context = fixture.Context("cn-717", "cn-717/game", "dialect.messages");
        using var baselines = new InventoryBaselineScope(2, TimeProvider.System);
        var binding = new LaboratoryBinding("cn-717/game", table, TestBuilds.Packets, "cn-717", baselines);
        var session = new MatchMessagesMountTests.Session(table.Profile);
        try
        {
            binding.Bind(context, fixture.Messages);
            await fixture.Packets.DispatchAsync("cn-717/game", session, new(table.C2S("gcpSynthesisProcess"), Convert.FromHexString(Cn717RequestHex)), default);
            Assert.IsType<LaboratoryRequested>(Assert.Single(fixture.Messages.Received));
            fixture.Messages.HasHandler = false;
            await fixture.Packets.DispatchAsync("cn-717/game", session, new(table.C2S("gcpSynthesisProcess"), Convert.FromHexString(Cn717RequestHex)), default);
            Assert.Equal(-1, BinaryPrimitives.ReadInt32LittleEndian(Assert.Single(session.Bodies).AsSpan(4)));
            Assert.Equal(28, session.Bodies[0].Length);
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(fixture.Packets.Handlers);
    }

    /// <summary>Every audited legacy build binds through its own verified opcodes and answers in its own reply length.</summary>
    [Theory]
    [InlineData("kr-812", 40)]
    [InlineData("id-1809", 40)]
    [InlineData("cn-324", 28)]
    [InlineData("cn-406", 28)]
    [InlineData("cn-404", 28)]
    [InlineData("cn-408", 28)]
    [InlineData("cn-706", 28)]
    [InlineData("cn-705", 28)]
    [InlineData("cn-717", 28)]
    public async Task EveryAuditedLegacyBuildBindsAndRefusesNatively(string build, int replyLength)
    {
        var table = TestBuilds.Opcodes(build);
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var context = fixture.Context(build, build + "/game", "dialect.messages");
        using var baselines = new InventoryBaselineScope(2, TimeProvider.System);
        var binding = new LaboratoryBinding(build + "/game", table, TestBuilds.Packets, build, baselines);
        var session = new MatchMessagesMountTests.Session(table.Profile);
        try
        {
            binding.Bind(context, fixture.Messages);
            await binding.SendAsync(session, new LaboratoryRefused(LaboratoryOperation.Synthesis), default);
            Assert.Equal(table.S2C("gspSynthesisProcess"), Assert.Single(session.Sent));
            Assert.Equal(replyLength, session.Bodies[0].Length);
            Assert.Equal(-1, BinaryPrimitives.ReadInt32LittleEndian(session.Bodies[0].AsSpan(4)));
        }
        finally { await context.UnwindAsync(); }
    }

    /// <summary>A legacy build that is not admitted (unread consumer, or no native configuration) stays unbound.</summary>
    [Theory]
    [InlineData("id-400")]
    [InlineData("tw-410")]
    public void UnauditedLegacyBuildCannotBind(string build)
    {
        var table = TestBuilds.Opcodes(build);
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var context = fixture.Context(build, build + "/game", "dialect.messages");
        using var baselines = new InventoryBaselineScope(2, TimeProvider.System);
        var binding = new LaboratoryBinding(build + "/game", table, TestBuilds.Packets, build, baselines);
        Assert.Throws<BubbleFighter.Fantasy.Abstractions.ConfigException>(() => binding.Bind(context, fixture.Messages));
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
