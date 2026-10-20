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
    public async Task NativeLocalSpawnRequestIsForwardedToPeersWithoutAnEchoToTheHost(string build)
    {
        var (schema, play) = TestBuilds.Of(build);
        Assert.True(play.Wire.SpawnGroupAppliesLocally);
        var host = new RecordingSession(1, build);
        var peer = new RecordingSession(2, build);
        var router = new PeerRouter(peer, build);
        using var handler = new TutorialHandler(TestBuilds.Packets, build, schema, play.Tutorial, play.MatchStart,
            play.Wire, new Tables(), NullLogger.Instance, messages: router);
        var body = TutorialWire.WriteCreateMobsFromSpawnGroup(TestBuilds.Packets, build, 1);

        await handler.HandleCreateMobsFromSpawnGroupAsync(host, new InboundPacket(32857, body), default);

        Assert.Empty(host.Sent);
        var received = Assert.Single(peer.Sent);
        Assert.Equal((ushort)32857, received.Opcode);
        Assert.Equal(body, received.Payload);
        Assert.False(Assert.IsType<RoundFeedSent>(router.Message).IncludeSender);
    }

    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-717")]
    [InlineData("id-1809")]
    [InlineData("cn-279")]
    [InlineData("cn-706")]
    public async Task NativeNetworkSpawnRequestRetainsItsHostReply(string build)
    {
        var (schema, play) = TestBuilds.Of(build);
        Assert.False(play.Wire.SpawnGroupAppliesLocally);
        var host = new RecordingSession(1, build);
        using var handler = new TutorialHandler(TestBuilds.Packets, build, schema, play.Tutorial, play.MatchStart,
            play.Wire, new Tables(), NullLogger.Instance);
        var body = TutorialWire.WriteCreateMobsFromSpawnGroup(TestBuilds.Packets, build, 2);
        await handler.HandleCreateMobsFromSpawnGroupAsync(host, new InboundPacket(32857, body), default);
        Assert.Equal(body, Assert.Single(host.Sent).Payload);
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
        public IEnumerable<OpcodeEntry> All => [new(32857, PacketDirection.ServerToClient,
            "gppCreateMobsFromSpawnGroup", Evidence.Verified, null, null)];
        public IOpcodeTable ForConnection(OpcodeConnection connection) => this;
        public bool TryGet(PacketDirection d, ushort value, out OpcodeEntry? e) => throw new NotSupportedException();
        public string Describe(PacketDirection d, ushort value) => throw new NotSupportedException();
    }
}
