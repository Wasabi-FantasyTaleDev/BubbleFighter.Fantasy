using System.Buffers.Binary;
using System.Net;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Movement;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using BubbleFighter.Fantasy.Tests;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>Exercises native UDP admission, movement decoding, and the independent downlink format.</summary>
public sealed class UdpMovementBindingTests
{
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    [InlineData("cn-735")]
    [InlineData("cn-738")]
    [InlineData("cn-742")]
    public async Task ProbeAndMovementUseTheAuthenticatedSessionAndNativeDownlink(string build)
    {
        var services = new ServiceRegistry();
        var events = new EventBus(NullLogger.Instance);
        var contexts = new List<PluginContext>();
        PluginContext Context(string id)
        {
            var context = new PluginContext(new PluginRow { Id = id, Plugin = id, Source = "test" },
                services, events, NullLogger.Instance);
            contexts.Add(context);
            return context;
        }
        var root = FindRoot();
        var packets = PacketTables.Load(Path.Combine(root, "packets"));
        var profile = TestWireDialects.Get(build).Profile;
        var table = OpcodeTableLoader.Load(Path.Combine(root, "profiles", build, "opcodes.yaml"), profile);
        var relay = table.ForConnection(OpcodeConnection.Relay);
        var session = new RecordingSession(11, profile);
        var identityBytes = PacketCodec.Write(packets, build, "UdpRelayProbeReply", new Dictionary<string, object?>()).Length;
        var directory = new DirectoryStub(build + "/relay", session,
            Enumerable.Range(1, identityBytes).Select(value => (byte)value).ToArray());
        var endpoint = new EndpointStub(build + "/udp");
        try
        {
            await new PacketRouterPlugin().ActivateAsync(Context("router"), default);
            await new MessageRouterPlugin().ActivateAsync(Context("messages"), default);
            var owner = Context("endpoints");
            owner.Effect(services.RegisterMany<IMatchUdpRelayDirectory>(directory));
            owner.Effect(services.RegisterMany<IUdpDatagramEndpoint>(endpoint));
            var reports = new List<(ISession Session, RoundMovementReported Report)>();
            var subscriber = Context("subscriber");
            subscriber.Effect(services.Get<IMessageRouter>().For(subscriber).On<RoundMovementReported>(build + "/game",
                (sender, report, _) => { reports.Add((sender, report)); return ValueTask.CompletedTask; }));
            var binding = new RoundMovementBinding(build + "/game", build + "/relay", table, relay, packets,
                build, NullLogger.Instance, endpoint.Protocol);
            var mounted = Context("movement");
            binding.Mount(mounted);

            await endpoint.ReceiveAsync(20001, new byte[8]);
            Assert.Empty(session.Opcodes);
            await endpoint.ReceiveAsync(20001, directory.Token);
            Assert.Empty(session.Opcodes);

            var challenge = Assert.Single(endpoint.Sent);
            Assert.Equal(relay.ForConnection(OpcodeConnection.Udp).S2C("rspUdpBypassVerify_"), BinaryPrimitives.ReadUInt16LittleEndian(challenge.Body));
            Assert.Equal(6, challenge.Body.Length);
            endpoint.Sent.Clear();
            await endpoint.ReceiveAsync(20003, directory.Token.Concat(new byte[21]).ToArray());
            Assert.Empty(reports);
            Assert.Empty(session.Opcodes);
            await endpoint.ReceiveAsync(20002, directory.Token);
            Assert.Equal(new[] { relay.S2C("rspUdpBypassReady_") }, session.Opcodes);
            await endpoint.ReceiveAsync(20002, directory.Token);
            Assert.Single(session.Opcodes);

            var full = PacketCodec.Write(packets, build, "MovementReport", new Dictionary<string, object?>
            {
                ["object_id"] = 1000000, ["x"] = 1.25f, ["y"] = -2.5f, ["z"] = 3.75f,
                ["yaw"] = (byte)4, ["pitch"] = (byte)5, ["state"] = (byte)6, ["tick"] = (ushort)7,
            });
            var envelope = directory.Token.Concat(full).ToArray();
            await endpoint.ReceiveAsync(20003, envelope);
            Assert.Same(session, Assert.Single(reports).Session);
            Assert.Equal(new RoundMovementReported(1000000, 0x3F, 1.25f, -2.5f, 3.75f, 4, 5, 6, 7), reports[0].Report);

            await endpoint.ReceiveAsync(20003, envelope, new IPEndPoint(IPAddress.Loopback, 7602));
            await endpoint.ReceiveAsync(20003, envelope[..^1]);
            await endpoint.ReceiveAsync(20003, envelope.Concat(new byte[] { 0 }).ToArray());
            Assert.Single(reports);

            var delta = PacketCodec.Write(packets, build, "MovementSample", new Dictionary<string, object?>
            {
                ["actor"] = 1000000, ["tick"] = (ushort)8, ["mask"] = (byte)0x21,
                ["x"] = 9.5f, ["state"] = (byte)10,
            });
            await endpoint.ReceiveAsync(20004, directory.Token.Concat(delta).ToArray());
            Assert.Equal(new RoundMovementReported(1000000, 0x21, 9.5f, 0, 0, 0, 0, 10, 8), reports[^1].Report);

            var movement = new PlayerMovementState(1.25f, -2.5f, 3.75f, 4, 5, 6);
            await binding.SendAsync(session, new(1000000, movement, 7), default);
            var outbound = Assert.Single(endpoint.Sent);
            Assert.Equal(directory.Remote, outbound.Remote);
            Assert.Equal(27, outbound.Body.Length);
            Assert.Equal(relay.S2C("gppWanderLean"), BinaryPrimitives.ReadUInt16LittleEndian(outbound.Body));
            Assert.Equal(full, outbound.Body.AsSpan(6).ToArray());

            directory.Current = false;
            await endpoint.ReceiveAsync(20003, envelope);
            Assert.Equal(2, reports.Count);
            await binding.SendAsync(session, new(1000000, movement, 7), default);
            Assert.Equal(full, session.Body(relay.S2C("gppWanderLean")));
            Assert.Single(endpoint.Sent);
            mounted.Unwind();
            Assert.Null(endpoint.Handler);
        }
        finally
        {
            for (var i = contexts.Count - 1; i >= 0; i--) contexts[i].Unwind();
        }
    }

    private sealed class EndpointStub(string protocol) : IUdpDatagramEndpoint
    {
        public string Protocol => protocol;
        public IPEndPoint LocalEndPoint => new(IPAddress.Loopback, 7600);
        internal UdpDatagramHandler? Handler;
        internal List<(IPEndPoint Remote, byte[] Body)> Sent { get; } = [];
        public IUdpDatagramSubscription Register(UdpDatagramHandler handler)
        {
            Assert.Null(Handler);
            Handler = handler;
            return new Removal(() => Handler = null);
        }
        public ValueTask SendAsync(ReadOnlyMemory<byte> payload, IPEndPoint remoteEndPoint, CancellationToken ct)
        {
            Sent.Add((remoteEndPoint, payload.ToArray()));
            return ValueTask.CompletedTask;
        }
        internal ValueTask ReceiveAsync(ushort opcode, byte[] body, IPEndPoint? remote = null)
        {
            var frame = new byte[6 + body.Length];
            BinaryPrimitives.WriteUInt16LittleEndian(frame, opcode);
            body.CopyTo(frame, 6);
            return Handler!(remote ?? new(IPAddress.Loopback, 7601), frame, default);
        }
    }

    private sealed class DirectoryStub(string protocol, ISession session, byte[] token) : IMatchUdpRelayDirectory
    {
        internal byte[] Token { get; } = token;
        internal IPEndPoint Remote { get; } = new(IPAddress.Loopback, 7601);
        internal bool Current = true;
        private bool _bound;
        public string Protocol => protocol;
        public IDisposable Register(ISession value, Func<bool> isCurrent) => throw new NotSupportedException();
        public ValueTask<ReadOnlyMemory<byte>> IssueAsync(ISession value, int identityBytes, CancellationToken ct) => throw new NotSupportedException();
        public bool TryBind(ReadOnlySpan<byte> token, IPEndPoint remoteEndPoint, out ISession? value)
        {
            _bound = Current && token.SequenceEqual(Token) && Remote.Equals(remoteEndPoint);
            value = _bound ? session : null;
            return _bound;
        }
        public bool TryResolve(ReadOnlySpan<byte> token, IPEndPoint remoteEndPoint, out ISession? value)
        {
            var found = Current && _bound && token.SequenceEqual(Token) && Remote.Equals(remoteEndPoint);
            value = found ? session : null;
            return found;
        }
        public void Disable(ISession value) => Current = false;
        public IReadOnlyList<ISession> RegisteredSessions() => [];
        private bool _verified;
        public MatchUdpRelayState StateOf(ISession value) => !Current || !ReferenceEquals(value, session) ? MatchUdpRelayState.Absent
            : _verified ? MatchUdpRelayState.Verified : _bound ? MatchUdpRelayState.Bound : MatchUdpRelayState.Offered;
        public bool Verify(ISession value)
        {
            if (!Current || !_bound || !ReferenceEquals(value, session) || _verified) return false;
            _verified = true;
            return true;
        }
        public void Reset(ISession value) { _bound = false; _verified = false; }
        public IReadOnlyList<ISession> BoundSessions() => [];
        public bool TryGetEndpoint(ISession value, out IPEndPoint? remoteEndPoint)
        {
            remoteEndPoint = null;
            return _verified && TryGetTarget(value, out remoteEndPoint, out _);
        }
        public bool TryGetTarget(ISession value, out IPEndPoint? remoteEndPoint, out ReadOnlyMemory<byte> identity)
        {
            var found = Current && _bound && ReferenceEquals(value, session);
            remoteEndPoint = found ? Remote : null;
            identity = found ? Token : ReadOnlyMemory<byte>.Empty;
            return found;
        }
    }

    private sealed class Removal(Action remove) : IUdpDatagramSubscription
    {
        public void Dispose() => remove();
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "BubbleFighter.Fantasy.sln"))) return directory.FullName;
        throw new InvalidOperationException("Repository root not found.");
    }
}
