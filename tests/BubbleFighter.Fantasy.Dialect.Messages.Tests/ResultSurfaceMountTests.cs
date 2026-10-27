using System.Collections.Concurrent;
using System.Buffers.Binary;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Checks native result output independently of admission of inbound kill-credit packets.</summary>
public sealed class ResultSurfaceMountTests
{
    /// <summary>Checks wide reference-based ordinary reports independently of side fields and unsupported event kinds.</summary>
    [Theory]
    [InlineData("id-400", 32, true)]
    [InlineData("cn-406", 32, true)]
    [InlineData("cn-279", 20, false)]
    public async Task WideOrdinaryReportsUseExplicitScorerAndVictimReferences(string build, int length, bool directKills)
    {
        var profile = new ClientProfile(build, build[..2], int.Parse(build[3..]), 0, 0, build);
        var path = Path.Combine(Directory.GetParent(TestBuilds.PacketsDirectory())!.FullName, "profiles", build, "opcodes.yaml");
        var table = OpcodeTableLoader.Load(path, profile);
        var services = new ServiceRegistry();
        var routes = new Routes();
        var messages = new Messages();
        using var packets = services.Register<IPacketRouter>(routes);
        using var typed = services.Register<IMessageRouter>(messages);
        var context = new PluginContext(new PluginRow { Id = "wide-ordinary", Plugin = "test", Source = "test" },
            services, new EventBus(NullLogger.Instance), NullLogger.Instance);
        var (schema, play) = TestBuilds.Of(build);
        var codec = new PlayRoomCodec(build + "/game", build, schema, play,
            new Tables(table), NullLogger.Instance, TestBuilds.Packets);
        var session = new Session(profile);
        try
        {
            codec.Mount(context);
            var opcode = table.C2S("gcpGameUpdateScore");
            var handler = routes.Handlers[(build + "/game", opcode)];
            var body = new byte[length];
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(4), 1_500_000);
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(8), 1_200_000);
            body.AsSpan(12).Fill(0xFD);
            for (var kind = 0; kind <= byte.MaxValue; kind++)
            {
                BinaryPrimitives.WriteInt32LittleEndian(body, kind);
                await handler(session, new(opcode, body), default);
            }
            var expectedCount = directKills ? 3 : 2;
            Assert.Equal(expectedCount, messages.Received.Count);
            var credit = Assert.IsType<RoundKillCreditReported>(messages.Received[0]);
            Assert.Equal(2, credit.ReporterSlot);
            Assert.Equal(5, credit.Victim);
            Assert.Equal(0, credit.ComboCount);
            Assert.True(credit.RequiresReporterOwnership);
            var finishing = Assert.IsType<RoundFinishingBlowReported>(messages.Received[1]);
            Assert.Equal(2, finishing.ReporterSlot);
            Assert.Equal(5, finishing.Victim);
            Assert.True(finishing.HasVictimReference);
            Assert.True(finishing.RequiresReporterOwnership);
            Assert.False(finishing.RequiresDirectKillMode);
            if (directKills)
                Assert.True(Assert.IsType<RoundFinishingBlowReported>(messages.Received[2]).RequiresDirectKillMode);
            for (var kind = 1; kind <= 2; kind++)
            {
                BinaryPrimitives.WriteInt32LittleEndian(body, kind);
                BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(4), 1_500_031);
                await handler(session, new(opcode, body), default);
                var npc = messages.Received[^1];
                Assert.Null(npc is RoundKillCreditReported knockdown ? knockdown.Victim : ((RoundFinishingBlowReported)npc).Victim);
                BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(8), 1_200_031);
                await handler(session, new(opcode, body), default);
                BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(8), 1_500_031);
                await handler(session, new(opcode, body), default);
                BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(8), 1_200_000);
                BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(4), 1_200_000);
                await handler(session, new(opcode, body), default);
                await handler(session, new(opcode, body[..^1]), default);
                await handler(session, new(opcode, new byte[length + 1]), default);
                Assert.Equal(expectedCount + kind, messages.Received.Count);
            }
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(routes.Handlers);
        Assert.Empty(services.GetAll<IInboundMessageBinding<RoundKillCreditReported>>());
        Assert.Empty(services.GetAll<IInboundMessageBinding<RoundFinishingBlowReported>>());
    }

    /// <summary>Checks mounted direct-kill codecs across both envelopes, including NPC references and rejected combo events.</summary>
    [Theory]
    [InlineData("kr-812", 16)]
    [InlineData("cn-717", 16)]
    [InlineData("id-400", 32)]
    [InlineData("cn-406", 32)]
    [InlineData("id-1809", 16)]
    public async Task DirectKillsUseCharacterReferencesInsteadOfSideBytes(string build, int length)
    {
        var profile = new ClientProfile(build, build[..2], int.Parse(build[3..]), 0, 0, build);
        var path = Path.Combine(Directory.GetParent(TestBuilds.PacketsDirectory())!.FullName, "profiles", build, "opcodes.yaml");
        var table = OpcodeTableLoader.Load(path, profile);
        var services = new ServiceRegistry();
        var routes = new Routes();
        var messages = new Messages();
        using var packets = services.Register<IPacketRouter>(routes);
        using var typed = services.Register<IMessageRouter>(messages);
        var context = new PluginContext(new PluginRow { Id = "direct-kills", Plugin = "test", Source = "test" },
            services, new EventBus(NullLogger.Instance), NullLogger.Instance);
        var (schema, play) = TestBuilds.Of(build);
        var codec = new PlayRoomCodec(build + "/game", build, schema, play,
            new Tables(table), NullLogger.Instance, TestBuilds.Packets);
        var session = new Session(profile);
        try
        {
            codec.Mount(context);
            var opcode = table.C2S("gcpGameUpdateScore");
            var handler = routes.Handlers[(build + "/game", opcode)];
            var body = new byte[length];
            body[0] = 3;
            var references = length == 16 ? 1 : 4;
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(references), 1_500_000);
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(references + 4), 1_200_000);
            // Native sub_528550 side codes differ from the scorer's seat 2.
            body.AsSpan(references + 8).Fill(0xFD);
            await handler(session, new(opcode, body), default);
            var report = Assert.IsType<RoundFinishingBlowReported>(Assert.Single(messages.Received));
            Assert.Equal(2, report.ReporterSlot);
            Assert.Equal(5, report.Victim);
            Assert.True(report.HasVictimReference);
            Assert.True(report.RequiresDirectKillMode);
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(references), 1_500_031);
            await handler(session, new(opcode, body), default);
            Assert.Null(Assert.IsType<RoundFinishingBlowReported>(messages.Received[^1]).Victim);
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(references + 4), 1_200_031);
            await handler(session, new(opcode, body), default);
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(references + 4), 1_500_031);
            await handler(session, new(opcode, body), default);
            await handler(session, new(opcode, body[..^1]), default);
            await handler(session, new(opcode, new byte[length + 1]), default);
            Assert.Equal(2, messages.Received.Count);
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(references + 4), 1_200_000);
            body[0] = 26;
            await handler(session, new(opcode, body), default);
            Assert.Equal(2, messages.Received.Count);
            if (!play.ScoreReports && !TestBuilds.Packets.CapabilityOf(build, "character_reference_score_reports"))
            {
                body[0] = 1;
                await handler(session, new(opcode, body), default);
                Assert.Equal(2, messages.Received.Count);
                Assert.Empty(services.GetAll<IInboundMessageBinding<RoundKillCreditReported>>());
            }
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(routes.Handlers);
        Assert.Empty(services.GetAll<IInboundMessageBinding<RoundFinishingBlowReported>>());
    }

    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-717")]
    [InlineData("id-1809")]
    public async Task NarrowReportsDispatchOnlyAuditedKillKinds(string build)
    {
        var profile = new ClientProfile(build, build[..2], int.Parse(build[3..]), 0, 0, build);
        var path = Path.Combine(Directory.GetParent(TestBuilds.PacketsDirectory())!.FullName, "profiles", build, "opcodes.yaml");
        var table = OpcodeTableLoader.Load(path, profile);
        var services = new ServiceRegistry();
        var routes = new Routes();
        var messages = new Messages();
        using var packets = services.Register<IPacketRouter>(routes);
        using var typed = services.Register<IMessageRouter>(messages);
        var context = new PluginContext(new PluginRow { Id = "score-kinds", Plugin = "test", Source = "test" },
            services, new EventBus(NullLogger.Instance), NullLogger.Instance);
        var (schema, play) = TestBuilds.Of(build);
        var codec = new PlayRoomCodec(build + "/game", build, schema, play,
            new Tables(table), NullLogger.Instance, TestBuilds.Packets);
        var session = new Session(profile);
        try
        {
            codec.Mount(context);
            foreach (var (name, length, supported) in new[]
            {
                ("gcpGameUpdateScore", 16, 1), ("gcpGameUpdateScoreOptimze", 6, 2),
            })
            {
                var opcode = table.C2S(name);
                var handler = routes.Handlers[(build + "/game", opcode)];
                var before = messages.Received.Count;
                for (var kind = 0; kind <= byte.MaxValue; kind++)
                {
                    var body = new byte[length];
                    body[0] = (byte)kind;
                    body[length == 16 ? 10 : 5] = 2;
                    await handler(session, new(opcode, body), default);
                }
                Assert.Equal(before + 1, messages.Received.Count);
                if (supported == 1)
                    Assert.Equal(2, Assert.IsType<RoundKillCreditReported>(messages.Received[^1]).ReporterSlot);
                else
                    Assert.Equal(2, Assert.IsType<RoundFinishingBlowReported>(messages.Received[^1]).ReporterSlot);
                await handler(session, new(opcode, new byte[length - 1]), default);
                await handler(session, new(opcode, new byte[length + 1]), default);
                Assert.Equal(before + 1, messages.Received.Count);
            }
            var fullOpcode = table.C2S("gcpGameUpdateScore");
            var full = new byte[16];
            full[0] = 1;
            BinaryPrimitives.WriteInt32LittleEndian(full.AsSpan(1), 1_500_000);
            BinaryPrimitives.WriteInt32LittleEndian(full.AsSpan(5), 1_200_000);
            full[9] = 5;
            full[10] = 2;
            BinaryPrimitives.WriteUInt16LittleEndian(full.AsSpan(12), 9);
            await routes.Handlers[(build + "/game", fullOpcode)](session, new(fullOpcode, full), default);
            var ordinary = Assert.IsType<RoundKillCreditReported>(messages.Received[^1]);
            Assert.Equal(2, ordinary.ReporterSlot);
            Assert.Equal(5, ordinary.Victim);
            Assert.Equal(9, ordinary.ComboCount);
            full[9] = 0xFE;
            await routes.Handlers[(build + "/game", fullOpcode)](session, new(fullOpcode, full), default);
            Assert.Null(Assert.IsType<RoundKillCreditReported>(messages.Received[^1]).Victim);
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(routes.Handlers);
    }

    [Theory]
    [InlineData("kr-812", true, true)]
    [InlineData("cn-717", true, true)]
    [InlineData("cn-324", true, true)]
    [InlineData("id-400", true, false)]
    [InlineData("id-1809", true, true)]
    [InlineData("cn-279", true, false)]
    [InlineData("cn-406", true, false)]
    [InlineData("cn-706", false, false)]
    public async Task ResultsMountAndSendWithoutEnablingUnauditedReports(string build, bool resultOutput, bool reports)
    {
        var profile = new ClientProfile(build, build[..2], int.Parse(build[3..]), 0, 0, build);
        var path = Path.Combine(Directory.GetParent(TestBuilds.PacketsDirectory())!.FullName, "profiles", build, "opcodes.yaml");
        var table = OpcodeTableLoader.Load(path, profile);
        var services = new ServiceRegistry();
        var routes = new Routes();
        using var packetRegistration = services.Register<IPacketRouter>(routes);
        using var messageRegistration = services.Register<IMessageRouter>(new Messages());
        var context = new PluginContext(new PluginRow { Id = "result-surface", Plugin = "test", Source = "test" },
            services, new EventBus(NullLogger.Instance), NullLogger.Instance);
        var (schema, play) = TestBuilds.Of(build);
        Assert.Equal(resultOutput, TestBuilds.Packets.CapabilityOf(build, "round_results"));
        Assert.Equal(reports, play.ScoreReports);
        var codec = new PlayRoomCodec(build + "/game", build, schema, play,
            new Tables(table), NullLogger.Instance, TestBuilds.Packets);
        try
        {
            codec.Mount(context);
            Assert.Equal(resultOutput ? 1 : 0, services.GetAll<IMessageSender<RoundScoreboard>>().Count);
            Assert.Equal(resultOutput ? 1 : 0, services.GetAll<IMessageSender<ResultStageAdvanced>>().Count);
            Assert.Equal(resultOutput ? 1 : 0, services.GetAll<IMessageSender<RoundScoreUpdated>>().Count);
            Assert.Equal(reports || TestBuilds.Packets.CapabilityOf(build, "character_reference_score_reports") ? 1 : 0,
                services.GetAll<IInboundMessageBinding<RoundKillCreditReported>>().Count);
            Assert.Equal(reports || TestBuilds.Packets.CapabilityOf(build, "direct_kill_reports")
                || TestBuilds.Packets.CapabilityOf(build, "character_reference_score_reports") ? 1 : 0,
                services.GetAll<IInboundMessageBinding<RoundFinishingBlowReported>>().Count);
            if (table.Find(PacketDirection.ClientToServer, "gcpGameUpdateScore") is { } report)
                Assert.Equal(reports || TestBuilds.Packets.CapabilityOf(build, "direct_kill_reports")
                    || TestBuilds.Packets.CapabilityOf(build, "character_reference_score_reports"),
                    routes.Handlers.ContainsKey((build + "/game", report)));
            if (resultOutput)
            {
                var session = new Session(profile);
                var board = new RoundScoreboard(1, 2, [11, 1], []);
                var sender = Assert.Single(services.GetAll<IMessageSender<RoundScoreboard>>());
                await sender.SendAsync(session, board, default);
                var nested = play.Score.TimeOverEnd == PlayValues.TimeOverEndBody.Scoreboard;
                Assert.Equal(table.S2C(nested ? "gspGameTimeOverEnd" : "gspGameBroadcastPlayerScoreData"), session.Sent[0].Opcode);
                Assert.Equal(PlayRoomCodec.WriteScoreboard(TestBuilds.Packets, build, board), session.Sent[0].Body);
                await sender.SendAsync(session, board with { WithRewards = true }, default);
                Assert.Equal(table.S2C("gspGameBroadcastPlayerScoreData"), session.Sent[1].Opcode);
                Assert.Equal(session.Sent[0].Body, session.Sent[1].Body);
                await sender.SendAsync(session, board with { TimeOver = true }, default);
                Assert.Equal(table.S2C("gspGameTimeOverEnd"), session.Sent[2].Opcode);
                Assert.Equal(nested ? session.Sent[0].Body : new byte[] { 11, 1 }, session.Sent[2].Body);
                var stage = Assert.Single(services.GetAll<IMessageSender<ResultStageAdvanced>>());
                await stage.SendAsync(session, new(1, ResultStage.Scoreboard), default);
                Assert.Equal(3, session.Sent.Count);
                await stage.SendAsync(session, new(1, ResultStage.Reward), default);
                Assert.Equal(table.S2C("gspGameShowResult_"), session.Sent[3].Opcode);
                Assert.Empty(session.Sent[3].Body);
            }
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(routes.Handlers);
        Assert.Empty(services.GetAll<IMessageSender<RoundScoreboard>>());
        Assert.Empty(services.GetAll<IMessageSender<ResultStageAdvanced>>());
    }

    private sealed class Session(ClientProfile profile) : ISession
    {
        internal List<(ushort Opcode, byte[] Body)> Sent { get; } = [];
        public long Id => 1;
        public ClientProfile Profile => profile;
        public IConnection Connection => throw new NotSupportedException();
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct)
        { Sent.Add((opcode, payload.ToArray())); return ValueTask.CompletedTask; }
    }

    private sealed class Routes : IPacketRouter
    {
        internal Dictionary<(string, ushort), PacketHandler> Handlers { get; } = [];
        public IDisposable Register(string protocolName, ushort opcode, PacketHandler handler)
        {
            Handlers.Add((protocolName, opcode), handler);
            return new Removal(() => Handlers.Remove((protocolName, opcode)));
        }
        public ValueTask DispatchAsync(string protocolName, ISession session, InboundPacket packet, CancellationToken ct) => throw new NotSupportedException();
        private sealed class Removal(Action remove) : IDisposable { public void Dispose() => remove(); }
    }

    private sealed class Tables(IOpcodeTable table) : IOpcodeTableRegistry
    {
        public IReadOnlyList<IOpcodeTable> All => [table];
        public IDisposable Register(IOpcodeTable value) => throw new NotSupportedException();
        public IOpcodeTable Get(string profileId) => table;
        public bool TryGet(string profileId, out IOpcodeTable? value) { value = table; return true; }
    }

    private sealed class Messages : IMessageRouter
    {
        internal List<IMessage> Received { get; } = [];
        public IReadOnlyList<MessageHandlerRegistration> Handlers => [];
        public IMessageRoutes For(IPluginContext owner) => throw new NotSupportedException();
        public ValueTask<bool> DispatchAsync<T>(string protocol, ISession session, T message, CancellationToken ct) where T : IMessage
        { Received.Add(message); return ValueTask.FromResult(true); }
    }
}
