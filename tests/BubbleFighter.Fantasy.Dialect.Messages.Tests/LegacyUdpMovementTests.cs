using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Exercises the legacy room adapter with native four-byte cookies and packed movement.</summary>
public sealed class LegacyUdpMovementTests
{
    [Theory]
    [InlineData("cn-735", 32922)]
    [InlineData("cn-738", 32923)]
    [InlineData("cn-742", 32923)]
    public async Task TransitionalMovementUsesFourByteCookiesAndFloatBodies(string build, int optimizedOpcode)
    {
        var relay = TestBuilds.Opcodes(build).ForConnection(OpcodeConnection.Relay);
        var session = new Session(relay.Profile);
        var endpoint = new Endpoint(build + "/udp");
        var directory = new Directory(build + "/relay", session);
        var services = new ServiceRegistry();
        var events = new EventBus(NullLogger.Instance);
        var contexts = new List<PluginContext>();
        PluginContext Context(string id)
        {
            var context = new PluginContext(new PluginRow { Id = id, Plugin = id, Source = "test" }, services, events, NullLogger.Instance);
            contexts.Add(context);
            return context;
        }
        try
        {
            await new PacketRouterPlugin().ActivateAsync(Context("router"), default);
            var owner = Context("providers");
            owner.Effect(services.RegisterMany<IUdpDatagramEndpoint>(endpoint));
            owner.Effect(services.RegisterMany<IMatchUdpRelayDirectory>(directory));
            var received = new List<(ISession Session, ushort Opcode, byte[] Body)>();
            var mounted = Context("transport");
            var transport = UdpMovementTransport.Mount(mounted, endpoint.Protocol, directory.Protocol, TestBuilds.Packets,
                build, relay, 1000, (sender, opcode, body, _) =>
                { received.Add((sender, opcode, body.ToArray())); return ValueTask.CompletedTask; });
            Assert.Equal(Convert.FromHexString("B01DEFCDAB89"), PacketCodec.Write(TestBuilds.Packets, build, "UdpRelayOffer",
                new Dictionary<string, object?> { ["unknown_0"] = 7600, ["unknown_2"] = 0x89ABCDEFu }));
            await endpoint.Receive(20001, directory.Token);
            Assert.Empty(session.Sent);
            await endpoint.Receive(20002, directory.Token);
            Assert.Equal((ushort)20, Assert.Single(session.Sent).Code);
            Assert.Equal(Convert.FromHexString("130000000000"), Assert.Single(endpoint.Sent));
            endpoint.Sent.Clear();
            var full = Convert.FromHexString("40420F000000A03F000020C0000070400405060700");
            Assert.Equal(21, full.Length);
            await endpoint.Receive(20003, directory.Token.Concat(full).ToArray());
            Assert.Same(session, Assert.Single(received).Session);
            Assert.Equal((ushort)32885, received[0].Opcode);
            Assert.Equal(full, received[0].Body);
            Assert.Equal(1.25f, PacketCodec.Read(TestBuilds.Packets, build, "MovementReport", full)["x"]);
            for (var mask = 0; mask < 64; mask++)
            {
                var values = new Dictionary<string, object?> { ["actor"] = 1000000, ["tick"] = 8, ["mask"] = mask };
                string[] members = ["x", "y", "z", "yaw", "pitch", "state"];
                for (var bit = 0; bit < 6; bit++)
                    if ((mask & (1 << bit)) != 0)
                        values[members[bit]] = bit < 3 ? 1.25f : (object)(byte)9;
                var delta = PacketCodec.Write(TestBuilds.Packets, build, "MovementSample", values);
                Assert.Equal(7 + Enumerable.Range(0, 6).Where(bit => (mask & (1 << bit)) != 0).Sum(bit => bit < 3 ? 4 : 1), delta.Length);
                await endpoint.Receive(20004, directory.Token.Concat(delta).ToArray());
                Assert.Equal((ushort)optimizedOpcode, received[^1].Opcode);
                Assert.Equal(delta, received[^1].Body);
                await endpoint.Receive(20004, directory.Token.Concat(delta[..^1]).ToArray());
                Assert.Equal(mask + 2, received.Count);
            }
            await transport.SendAsync(session, 32885, full, default);
            Assert.Equal(full, Assert.Single(endpoint.Sent).AsSpan(6).ToArray());
            await services.Get<IPacketRouter>().DispatchAsync(directory.Protocol, session, new(20005, ReadOnlyMemory<byte>.Empty), default);
            await transport.SendAsync(session, 32885, full, default);
            Assert.Equal(full, session.Sent[^1].Body);
            Assert.Single(endpoint.Sent);
        }
        finally
        {
            for (var i = contexts.Count - 1; i >= 0; i--) await contexts[i].UnwindAsync();
        }
    }

    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-717")]
    [InlineData("id-1809")]
    public async Task NativeLegacyMovementPreservesPackedBodiesAndDisableRestoresTcp(string build)
    {
        var table = TestBuilds.Opcodes(build);
        var relay = table.ForConnection(OpcodeConnection.Relay);
        var session = new Session(table.Profile);
        var endpoint = new Endpoint(build + "/udp");
        var directory = new Directory(build + "/relay", session);
        var services = new ServiceRegistry();
        var events = new EventBus(NullLogger.Instance);
        var contexts = new List<PluginContext>();
        PluginContext Context(string id)
        {
            var context = new PluginContext(new PluginRow { Id = id, Plugin = id, Source = "test" }, services, events, NullLogger.Instance);
            contexts.Add(context);
            return context;
        }
        try
        {
            await new PacketRouterPlugin().ActivateAsync(Context("router"), default);
            await new MessageRouterPlugin().ActivateAsync(Context("messages"), default);
            var owner = Context("providers");
            owner.Effect(services.RegisterMany<IUdpDatagramEndpoint>(endpoint));
            owner.Effect(services.RegisterMany<IMatchUdpRelayDirectory>(directory));
            var (schema, play) = TestBuilds.Of(build);
            var codec = new PlayRoomCodec(build + "/game", build, schema, play, new Tables(table), NullLogger.Instance, TestBuilds.Packets)
            { SelectMatchRelay = true, UdpProtocol = endpoint.Protocol };
            var received = new List<(ISession Session, RoundFeedSent Feed)>();
            var subscriber = Context("subscriber");
            subscriber.Effect(services.Get<IMessageRouter>().For(subscriber).On<RoundFeedSent>(build + "/game",
                (sender, feed, _) => { received.Add((sender, feed)); return ValueTask.CompletedTask; }));
            var mounted = Context("rooms");
            codec.Mount(mounted);
            await endpoint.Receive(20001, directory.Token);
            Assert.Empty(session.Sent);
            Assert.Equal(6, Assert.Single(endpoint.Sent).Length);
            endpoint.Sent.Clear();
            await endpoint.Receive(20002, directory.Token);
            Assert.Equal(new ushort[] { relay.S2C("rspUdpBypassReady_") }, session.Sent.Select(value => value.Code));
            // kr-812 sub_502910, cn-717 sub_537AC0, id-1809 sub_4D6E10: 4+2+2+2+1+1+1+2.
            var full = Convert.FromHexString("40420F0034127856BC9A1122334433");
            Assert.Equal(15, full.Length);
            await endpoint.Receive(20003, directory.Token.Concat(full).ToArray());
            var report = Assert.Single(received);
            Assert.Same(session, report.Session);
            Assert.True(report.Feed.OnRelay);
            Assert.Equal(new[] { 1000000 }, report.Feed.ReportedActors);
            Assert.Equal((ushort)32885, report.Feed.Code);
            Assert.Equal(full, report.Feed.Payload.ToArray());
            // The mask selects two packed coordinates and one byte; the native optimized opcode is32922.
            var delta = Convert.FromHexString("40420F0008002512345678AB");
            await endpoint.Receive(20004, directory.Token.Concat(delta).ToArray());
            Assert.Equal((ushort)32922, received[^1].Feed.Code);
            Assert.Equal(delta, received[^1].Feed.Payload.ToArray());
            await endpoint.Receive(20004, directory.Token.Concat(delta).Append((byte)0).ToArray());
            Assert.Equal(2, received.Count);
            foreach (var (opcode, body) in new[] { (20003, full), (20004, delta) })
            {
                var absent = body.ToArray();
                BinaryPrimitives.WriteUInt32LittleEndian(absent, uint.MaxValue);
                await endpoint.Receive((ushort)opcode, directory.Token.Concat(absent).ToArray());
                Assert.Equal(new[] { -1 }, received[^1].Feed.ReportedActors);
                Assert.Equal(absent, received[^1].Feed.Payload.ToArray());
                await endpoint.Receive((ushort)opcode, directory.Token.Concat(body).ToArray());
                Assert.Equal(new[] { 1000000 }, received[^1].Feed.ReportedActors);
            }
            Assert.Equal(6, received.Count);
            await services.GetAll<IMessageSender<RoundFeedReceived>>().Single().SendAsync(session, new RoundFeedReceived(32885, full), default);
            var downlink = Assert.Single(endpoint.Sent);
            Assert.Equal((ushort)32885, BinaryPrimitives.ReadUInt16LittleEndian(downlink));
            Assert.Equal(full, downlink.AsSpan(6).ToArray());
            var router = services.Get<IPacketRouter>();
            await router.DispatchAsync(directory.Protocol, session, new(relay.C2S("rcpReqUdpBypassDisable"), ReadOnlyMemory<byte>.Empty), default);
            await endpoint.Receive(20003, directory.Token.Concat(full).ToArray());
            Assert.Equal(6, received.Count);
            await services.GetAll<IMessageSender<RoundFeedReceived>>().Single().SendAsync(session, new RoundFeedReceived(32885, full), default);
            Assert.Equal(full, session.Sent[^1].Body);
            Assert.Single(endpoint.Sent);
            await mounted.UnwindAsync();
            Assert.Null(endpoint.Handler);
        }
        finally
        {
            for (var i = contexts.Count - 1; i >= 0; i--) await contexts[i].UnwindAsync();
        }
    }

    private sealed class Tables(IOpcodeTable table) : IOpcodeTableRegistry
    {
        public IReadOnlyList<IOpcodeTable> All => [table];
        public IDisposable Register(IOpcodeTable value) => throw new NotSupportedException();
        public IOpcodeTable Get(string profileId) => table;
        public bool TryGet(string profileId, out IOpcodeTable? value) { value = table; return true; }
    }

    private sealed class Session(ClientProfile profile) : ISession
    {
        public long Id => 1;
        public ClientProfile Profile => profile;
        public IConnection Connection => throw new NotSupportedException();
        public ConcurrentDictionary<string, object> Items { get; } = new();
        internal List<(ushort Code, byte[] Body)> Sent { get; } = [];
        public ValueTask SendAsync(ushort code, ReadOnlyMemory<byte> body, CancellationToken ct)
        { Sent.Add((code, body.ToArray())); return ValueTask.CompletedTask; }
    }

    private sealed class Endpoint(string protocol) : IUdpDatagramEndpoint
    {
        public string Protocol => protocol;
        public IPEndPoint LocalEndPoint => new(IPAddress.Loopback, 7600);
        internal UdpDatagramHandler? Handler;
        internal List<byte[]> Sent { get; } = [];
        public IUdpDatagramSubscription Register(UdpDatagramHandler handler) { Handler = handler; return new Removal(() => Handler = null); }
        public ValueTask SendAsync(ReadOnlyMemory<byte> payload, IPEndPoint remoteEndPoint, CancellationToken ct)
        { Sent.Add(payload.ToArray()); return ValueTask.CompletedTask; }
        internal ValueTask Receive(ushort opcode, byte[] body)
        {
            var bytes = new byte[6 + body.Length];
            BinaryPrimitives.WriteUInt16LittleEndian(bytes, opcode);
            body.CopyTo(bytes, 6);
            return Handler!(new(IPAddress.Loopback, 7601), bytes, default);
        }
    }

    private sealed class Directory(string protocol, ISession session) : IMatchUdpRelayDirectory
    {
        public string Protocol => protocol;
        internal byte[] Token { get; } = [1, 2, 3, 4];
        private bool _bound;
        private bool _disabled;
        public IDisposable Register(ISession value, Func<bool> isCurrent) => throw new NotSupportedException();
        public ValueTask<ReadOnlyMemory<byte>> IssueAsync(ISession value, int identityBytes, CancellationToken ct) => throw new NotSupportedException();
        public bool TryBind(ReadOnlySpan<byte> token, IPEndPoint remote, out ISession? found)
        { _bound = !_disabled && token.SequenceEqual(Token); found = _bound ? session : null; return _bound; }
        public bool TryResolve(ReadOnlySpan<byte> token, IPEndPoint remote, out ISession? found)
        { var valid = _bound && !_disabled && token.SequenceEqual(Token); found = valid ? session : null; return valid; }
        public void Disable(ISession value) => _disabled = true;
        public IReadOnlyList<ISession> RegisteredSessions() => [];
        private bool _verified;
        public MatchUdpRelayState StateOf(ISession value) => _verified ? MatchUdpRelayState.Verified : MatchUdpRelayState.Bound;
        public bool Verify(ISession value) { var first = !_verified; _verified = true; return first; }
        public void Reset(ISession value) { _bound = false; _verified = false; }
        public IReadOnlyList<ISession> BoundSessions() => [];
        public bool TryGetEndpoint(ISession value, out IPEndPoint? remote) => TryGetTarget(value, out remote, out _);
        public bool TryGetTarget(ISession value, out IPEndPoint? remote, out ReadOnlyMemory<byte> token)
        {
            var valid = _bound && !_disabled && ReferenceEquals(value, session);
            remote = valid ? new(IPAddress.Loopback, 7601) : null;
            token = valid ? Token : ReadOnlyMemory<byte>.Empty;
            return valid;
        }
    }

    private sealed class Removal(Action remove) : IUdpDatagramSubscription
    {
        public void Dispose() => remove();
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
