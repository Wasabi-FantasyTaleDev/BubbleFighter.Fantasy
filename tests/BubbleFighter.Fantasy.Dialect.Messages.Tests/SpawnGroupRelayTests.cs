using System.Collections.Concurrent;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Dialect.Rooms;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Checks that a native local spawn request reaches peers without executing twice on its sender.</summary>
public sealed class SpawnGroupRelayTests
{
    [Theory]
    [InlineData("cn-324")]
    [InlineData("id-400")]
    [InlineData("cn-406")]
    [InlineData("cn-705")]
    [InlineData("cn-706")]
    public async Task NativeLocalSpawnRequestIsForwardedToPeersWithoutAnEchoToTheHost(string build)
    {
        var (schema, play) = TestBuilds.Of(build);
        Assert.True(play.Wire.SpawnGroupAppliesLocally);
        var host = new RecordingSession(1, build);
        var peer = new RecordingSession(2, build);
        var router = new PeerRouter(peer, build);
        using var handler = new TutorialHandler(TestBuilds.Packets, build, schema, play.Tutorial, play.MatchStart,
            play.Wire, new Tables(), NullLogger.Instance, messages: router);
        var body = NpcWire.WriteCreateMobsFromSpawnGroup(TestBuilds.Packets, build, 1);

        await handler.HandleCreateMobsFromSpawnGroupAsync(host, new InboundPacket(32857, body), default);

        Assert.Empty(host.Sent);
        var received = Assert.Single(peer.Sent);
        Assert.Equal((ushort)32857, received.Opcode);
        Assert.Equal(body, received.Payload);
        Assert.False(Assert.IsType<RoundFeedSent>(router.Message).IncludeSender);
    }

    /// <summary>cn-279 sub_702660 sends an ordinary-round group without a local copy, so its sender still needs the reply.</summary>
    [Theory]
    [InlineData("cn-279")]
    public async Task NativeNetworkSpawnRequestRetainsItsHostReply(string build)
    {
        var (schema, play) = TestBuilds.Of(build);
        Assert.False(play.Wire.SpawnGroupAppliesLocally);
        var host = new RecordingSession(1, build);
        using var handler = new TutorialHandler(TestBuilds.Packets, build, schema, play.Tutorial, play.MatchStart,
            play.Wire, new Tables(), NullLogger.Instance);
        var body = NpcWire.WriteCreateMobsFromSpawnGroup(TestBuilds.Packets, build, 2);
        await handler.HandleCreateMobsFromSpawnGroupAsync(host, new InboundPacket(32857, body), default);
        Assert.Equal(body, Assert.Single(host.Sent).Payload);
    }

    /// <summary>
    /// A normal stage that queues its own group (kr-812 sub_4620A0, cn-717 sub_472FE0, id-1809 sub_447910) expands it
    /// once, and peers wait for the concrete members.
    /// </summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-717")]
    [InlineData("id-1809")]
    public async Task QueuedOrdinarySpawnGroupDoesNotExpandAgainFromAServerEcho(string build)
    {
        var (schema, play) = TestBuilds.Of(build);
        var host = new RecordingSession(1, build);
        var peer = new RecordingSession(2, build);
        var router = new PeerRouter(peer, build);
        using var handler = new TutorialHandler(TestBuilds.Packets, build, schema, play.Tutorial, play.MatchStart,
            play.Wire, new Tables(), NullLogger.Instance, messages: router);
        await handler.HandleCreateMobsFromSpawnGroupAsync(host,
            new(32857, NpcWire.WriteCreateMobsFromSpawnGroup(TestBuilds.Packets, build, 2)), default);
        Assert.Empty(host.Sent);
        Assert.Empty(peer.Sent);
        Assert.Null(router.Message);
    }

    /// <summary>
    /// Multiplayer NPC creation and life points return to their sender only where it keeps no local copy
    /// (cn-279 sub_702660); elsewhere the sender already applied them and a second copy would duplicate the NPC.
    /// </summary>
    [Theory]
    [InlineData("cn-279", true)]
    [InlineData("cn-406", false)]
    [InlineData("cn-717", false)]
    [InlineData("kr-812", false)]
    public async Task MultiplayerNpcUpdatesFollowTheBuildsSenderEcho(string build, bool echo)
    {
        var (schema, play) = TestBuilds.Of(build);
        var host = new RecordingSession(1, build);
        var peer = new RecordingSession(2, build);
        var router = new PeerRouter(peer, build);
        using var handler = new TutorialHandler(TestBuilds.Packets, build, schema, play.Tutorial, play.MatchStart,
            play.Wire, new Tables(), NullLogger.Instance, messages: router);
        var body = new byte[] { 1, 0, 0, 0 };

        await handler.HandleCreateSpawnGroupMemberMobAsync(host, new InboundPacket(32862, body), default);
        Assert.Equal(echo, Assert.IsType<RoundFeedSent>(router.Message).IncludeSender);
        await handler.HandleCreateAIObjectAsync(host, new InboundPacket(32892, body), default);
        Assert.Equal(echo, Assert.IsType<RoundFeedSent>(router.Message).IncludeSender);
        await handler.HandleLifePointAsync(host, new InboundPacket(32920, body), default);
        Assert.Equal(echo, Assert.IsType<RoundFeedSent>(router.Message).IncludeSender);

        Assert.Empty(host.Sent);
        Assert.Equal(new ushort[] { 32862, 32892, 32920 }, peer.Sent.Select(sent => sent.Opcode));
    }

    private sealed class RecordingSession(long id, string build) : ISession
    {
        public long Id => id;
        public IConnection Connection => throw new NotSupportedException();
        public ClientProfile Profile { get; } = new(build, build.Split('-')[0], int.Parse(build.Split('-')[1]), 0, 0, build);
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public List<(ushort Opcode, byte[] Payload)> Sent { get; } = [];
        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            Sent.Add((opcode, payload.ToArray()));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class PeerRouter(RecordingSession peer, string build) : IMessageRouter
    {
        public object? Message { get; private set; }
        public IReadOnlyList<MessageHandlerRegistration> Handlers => [];
        public IMessageRoutes For(IPluginContext owner) => throw new NotSupportedException();
        public async ValueTask<bool> DispatchAsync<T>(string protocol, ISession session, T message, CancellationToken ct) where T : IMessage
        {
            Assert.Equal(build + "/game", protocol);
            Message = message;
            var feed = Assert.IsType<RoundFeedSent>(message);
            await peer.SendAsync(feed.Code, feed.Payload, ct);
            return true;
        }
    }

    private sealed class Tables : IOpcodeTableRegistry
    {
        private readonly IOpcodeTable _table = new SpawnTable();
        public IReadOnlyList<IOpcodeTable> All => [_table];
        public IDisposable Register(IOpcodeTable table) => throw new NotSupportedException();
        public IOpcodeTable Get(string profileId) => _table;
        public bool TryGet(string profileId, out IOpcodeTable? table) { table = _table; return true; }
    }

    private sealed class SpawnTable : IOpcodeTable
    {
        public ClientProfile Profile => throw new NotSupportedException();
        public OpcodeConnection Connection => OpcodeConnection.Game;
        public IEnumerable<OpcodeEntry> All => new (ushort Code, string Name)[]
        {
            (32857, "gppCreateMobsFromSpawnGroup"), (32862, "gppCreateSpawnGroupMemberMob"),
            (32892, "gppCreateAIObject"), (32920, "gppLifePoint"),
        }.Select(item => new OpcodeEntry(item.Code, PacketDirection.ServerToClient, item.Name, Evidence.Verified, null, null));
        public IOpcodeTable ForConnection(OpcodeConnection connection) => this;
        public bool TryGet(PacketDirection d, ushort value, out OpcodeEntry? e) => throw new NotSupportedException();
        public string Describe(PacketDirection d, ushort value) => throw new NotSupportedException();
    }
}
