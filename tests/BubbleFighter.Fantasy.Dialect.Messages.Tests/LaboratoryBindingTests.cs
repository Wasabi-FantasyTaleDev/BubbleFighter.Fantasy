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

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
