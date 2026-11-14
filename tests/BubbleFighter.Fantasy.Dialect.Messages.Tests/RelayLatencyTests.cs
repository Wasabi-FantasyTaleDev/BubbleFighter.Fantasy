using System.Buffers.Binary;
using System.Reflection;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Dialect.Messages;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using BubbleFighter.Fantasy.Tests;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins native delay words, relay-only routing and reversible dialect registration.</summary>
public sealed class RelayLatencyTests
{
    /// <summary>Preserves every tick bit and rejects bodies that do not match the native four-byte request.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("kr-1256")]
    [InlineData("cn-738")]
    [InlineData("cn-742")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void ExactWordLayoutsPreserveUnsignedTicksAndOpaqueReplies(string build)
    {
        var tables = TestBuilds.Packets;
        RelayLatencyWire.RequireLayouts(tables, build);
        foreach (var word in new[] { 0u, 0x80000000u, uint.MaxValue })
        {
            var body = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(body, word);
            Assert.Equal(word, RelayLatencyWire.ReadRequest(tables, build, body).ClientTick);
            var reply = RelayLatencyWire.WriteReply(tables, build, new(word));
            Assert.Equal(4, reply.Length);
            Assert.Equal(body, reply);
        }
        foreach (var length in new[] { 0, 3, 5, 8 })
            Assert.Throws<PacketFormatException>(() => RelayLatencyWire.ReadRequest(tables, build, new byte[length]));
    }

    /// <summary>Installs each audited relay route and removes its decoder and typed sender when the row unloads.</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-738")]
    [InlineData("cn-742")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public async Task InstalledDialectUsesOnlyItsRelayNamesAndUnwindsBothPublications(string build)
    {
        using var fixture = new MatchMessagesMountTests.Fixture(TestBuilds.Opcodes(build));
        var protocol = build + "/relay";
        var context = fixture.Context(build, protocol, "dialect.relay-latency");
        context.BindServices(typeof(RelayLatencyPlugin).GetCustomAttribute<PluginAttribute>()!);
        try
        {
            await new RelayLatencyPlugin().ActivateAsync(context, default);
            Assert.Equal((protocol, (ushort)20006), Assert.Single(fixture.Packets.Handlers).Key);
            Assert.Equal(protocol, Assert.Single(fixture.Services.GetAll<IInboundMessageBinding<RelayLatencyRequested>>()).Protocol);
            var sender = Assert.Single(fixture.Services.GetAll<IMessageSender<RelayLatencyAcknowledged>>());
            var session = new MatchMessagesMountTests.Session(TestBuilds.Opcodes(build).Profile);
            await fixture.Packets.DispatchAsync(protocol, session, new(20006, new byte[] { 0, 0, 0, 128 }), default);
            Assert.Equal(0x80000000u, Assert.IsType<RelayLatencyRequested>(Assert.Single(fixture.Messages.Received)).ClientTick);
            Assert.Empty(session.Sent);
            await sender.SendAsync(session, new(0x12345678u), default);
            Assert.Equal((ushort)21, Assert.Single(session.Sent));
            Assert.Equal(new byte[] { 0x78, 0x56, 0x34, 0x12 }, Assert.Single(session.Bodies));
            Assert.DoesNotContain(fixture.Packets.Handlers, pair => pair.Key.Item1 != protocol);
            Assert.NotEqual("rspRepLagDisPing", TestBuilds.Opcodes(build).All
                .FirstOrDefault(entry => entry.Direction == PacketDirection.ServerToClient && entry.Value == 21)?.Name);
            var settings = PacketCodec.Write(TestBuilds.Packets, build, "RelayHandshakeSettings", new Dictionary<string, object?>());
            Assert.Equal(new byte[10], settings);
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(fixture.Packets.Handlers);
        Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<RelayLatencyRequested>>());
        Assert.Empty(fixture.Services.GetAll<IMessageSender<RelayLatencyAcknowledged>>());
    }

    /// <summary>Rejects mismatched profiles, connection scopes and unaudited builds without leaving registered effects.</summary>
    [Theory]
    [InlineData(null, "kr-1256/relay", "kr-1256")]
    [InlineData("kr-1256", "kr-1256/game", "kr-1256")]
    [InlineData("kr-1256", "cn-774/relay", "kr-1256")]
    [InlineData("kr-1256", "kr-1256/relay", "cn-774")]
    [InlineData("kr-812", "kr-812/relay", "kr-812")]
    public async Task WrongBuildScopeOrUnauditedMembershipFailsBeforeRegistration(string? build, string protocol, string tableBuild)
    {
        using var fixture = new MatchMessagesMountTests.Fixture(TestBuilds.Opcodes(tableBuild));
        var context = fixture.Context(build, protocol, "dialect.relay-latency");
        try { await Assert.ThrowsAsync<ConfigException>(() => new RelayLatencyPlugin().ActivateAsync(context, default).AsTask()); }
        finally { await context.UnwindAsync(); }
        Assert.Empty(fixture.Packets.Handlers);
        Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<RelayLatencyRequested>>());
        Assert.Empty(fixture.Services.GetAll<IMessageSender<RelayLatencyAcknowledged>>());
    }

    /// <summary>Requires both native relay names before publishing a request route or reply sender.</summary>
    [Theory]
    [InlineData("rcpReqLagDisPing")]
    [InlineData("rspRepLagDisPing")]
    public async Task MissingNativeNameFailsBeforeRegistration(string missing)
    {
        var original = TestBuilds.Opcodes("kr-1256");
        var table = new OpcodeTable(original.Profile,
            original.ForConnection(OpcodeConnection.Relay).All.Where(entry => entry.Name != missing));
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var context = fixture.Context("kr-1256", "kr-1256/relay", "dialect.relay-latency");
        try { await Assert.ThrowsAsync<ConfigException>(() => new RelayLatencyPlugin().ActivateAsync(context, default).AsTask()); }
        finally { await context.UnwindAsync(); }
        Assert.Empty(fixture.Packets.Handlers);
        Assert.Empty(fixture.Services.GetAll<IMessageSender<RelayLatencyAcknowledged>>());
    }

    /// <summary>Rejects a narrowed request or reply field before the dialect registers its route.</summary>
    [Theory]
    [InlineData("RelayLatencyRequest", "client_tick")]
    [InlineData("RelayLatencyReply", "unknown_4")]
    public async Task RetypedWordFailsBeforeRegistration(string structure, string field)
    {
        var edited = PacketTableEdits.WithFieldType(structure, field, "u32", "u16");
        using var fixture = new MatchMessagesMountTests.Fixture(TestBuilds.Opcodes("kr-1256"));
        var context = fixture.Context("kr-1256", "kr-1256/relay", "dialect.relay-latency", edited.FullName);
        try { await Assert.ThrowsAsync<ConfigException>(() => new RelayLatencyPlugin().ActivateAsync(context, default).AsTask()); }
        finally { await context.UnwindAsync(); }
        Assert.Empty(fixture.Packets.Handlers);
    }

    /// <summary>Propagates malformed bodies, absent logic, wrong recipients, cancellation and publication failures.</summary>
    [Fact]
    public async Task MalformedRequestsMissingHandlersWrongRecipientsAndFailedSendsRemainErrors()
    {
        using var fixture = new MatchMessagesMountTests.Fixture(TestBuilds.Opcodes("kr-1256"));
        var context = fixture.Context("kr-1256", "kr-1256/relay", "dialect.relay-latency");
        try
        {
            await new RelayLatencyPlugin().ActivateAsync(context, default);
            var session = new MatchMessagesMountTests.Session(TestBuilds.Opcodes("kr-1256").Profile);
            await Assert.ThrowsAsync<PacketFormatException>(() => fixture.Packets.DispatchAsync("kr-1256/relay", session, new(20006, new byte[5]), default).AsTask());
            Assert.Empty(fixture.Messages.Received);
            fixture.Messages.HasHandler = false;
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Packets.DispatchAsync("kr-1256/relay", session, new(20006, new byte[4]), default).AsTask());
            var sender = Assert.Single(fixture.Services.GetAll<IMessageSender<RelayLatencyAcknowledged>>());
            var other = new MatchMessagesMountTests.Session(TestBuilds.Opcodes("cn-774").Profile);
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Packets.DispatchAsync("kr-1256/relay", other, new(20006, new byte[4]), default).AsTask());
            await Assert.ThrowsAsync<InvalidOperationException>(() => sender.SendAsync(other, new(0), default).AsTask());
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sender.SendAsync(session, new(0), canceled.Token).AsTask());
            session.FailSend = true;
            await Assert.ThrowsAsync<IOException>(() => sender.SendAsync(session, new(0), default).AsTask());
            Assert.Empty(session.Sent);
            Assert.Empty(other.Sent);
        }
        finally { await context.UnwindAsync(); }
    }
}
