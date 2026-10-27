using System.Buffers;
using BubbleFighter.Fantasy.RoomCore;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Dialect.Messages;
using BubbleFighter.Fantasy.InGame;
using BubbleFighter.Fantasy.InGame.Sessions;
using BubbleFighter.Fantasy.Packets;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.MatchConnection.Tests;

public sealed class MatchConnectionTests
{
    private const string Build = "kr-1256";

    private static readonly PacketTables Tables =
        PacketTables.Load(Path.Combine(RelayHandshakeTableParityTests.RepositoryDirectory(), "packets"));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(65535)]
    public void SessionSignaturePinsModeAndCompleteSevenByteBody(ushort mode)
    {
        var payload = LoginBurstWire.WriteSessionSignature(Tables, Build, mode);
        Assert.Equal(7, payload.Length);
        var reader = new PacketReader(payload);
        Assert.Equal(mode, reader.ReadUInt16());
        Assert.Equal(0, reader.ReadByte());
        Assert.Equal(0u, reader.ReadUInt32());
    }

    [Fact]
    public async Task AuthenticationThenSceneRegistrationUsesDerivedFramingAndSeatIdentity()
    {
        var (registry, room, seat) = Room();
        using var lifecycle = Lifecycle(registry);
        var relay = new Session(10);
        await lifecycle.AuthenticateAsync(relay, Authentication(seat), CancellationToken.None);
        Assert.Equal(new ushort[] { 22, 1 }, relay.Sent.Select(f => f.Opcode));
        Assert.Equal(new[] { 10, 4 }, relay.Sent.Select(f => f.Body.Length));
        Assert.False(seat.RelayInitialized);
        await lifecycle.RegisterSceneAsync(relay, new(7, 1000000, "host"), CancellationToken.None);
        Assert.Equal(new ushort[] { 22, 1, 4, 5, 6, 13 }, relay.Sent.Select(f => f.Opcode));
        Assert.Equal(new[] { 10, 4, 268, 1008, 136, 0 }, relay.Sent.Select(f => f.Body.Length));
        foreach (var frame in relay.Sent.Where(f => f.Opcode is 4 or 5 or 6))
            Assert.Equal(1000000, BitConverter.ToInt32(frame.Body));
        Assert.True(seat.RelayInitialized);
        await lifecycle.NameMapAsync(relay, new(1000000, "host", room.Setting.Map.Unknown68), CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => lifecycle.NameMapAsync(relay,
            new(1100000, "host", room.Setting.Map.Unknown68), CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task ForgedTicketOrIdentityNeverReceivesAuthenticationReplies()
    {
        var (registry, _, seat) = Room();
        using var lifecycle = Lifecycle(registry);
        foreach (var message in new[] { Authentication(seat) with { UserNumber = 8 }, new MatchAuthenticate(new byte[20], 7, "host") })
        {
            var relay = new Session(10);
            await Assert.ThrowsAsync<InvalidOperationException>(() => lifecycle.AuthenticateAsync(relay, message, CancellationToken.None).AsTask());
            Assert.Empty(relay.Sent);
            Assert.True(relay.Connection.IsClosed);
            Assert.False(registry.IsMatchBound(relay));
        }
    }

    [Fact]
    public async Task ResultKeepsTheExistingLeaseButRejectsNewAuthentication()
    {
        var (registry, room, seat) = Room();
        using var lifecycle = Lifecycle(registry);
        var relay = new Session(10);
        await lifecycle.AuthenticateAsync(relay, Authentication(seat), CancellationToken.None);
        await lifecycle.RegisterSceneAsync(relay, new(7, 1000000, "host"), CancellationToken.None);
        var binding = new MatchSeatBinding(room.Id, seat.Slot, seat.UserNum, 1000000, seat.Nickname,
            room.Setting.Map.Unknown68, room.StartGeneration);
        room.MarkResult();
        Assert.True(registry.IsCurrent(relay, binding));
        await Assert.ThrowsAsync<InvalidOperationException>(() => lifecycle.AuthenticateAsync(
            new Session(11), Authentication(seat), CancellationToken.None).AsTask());
        Assert.Same(relay, seat.Relay);
        Assert.False(relay.Connection.IsClosed);
        room.ResetToWaiting();
        Assert.False(registry.IsCurrent(relay, binding));
    }

    [Fact]
    public async Task ReplacementAndOldDisconnectNeverRemoveTheGameSeatOrNewBinding()
    {
        var (registry, room, seat) = Room();
        using var lifecycle = Lifecycle(registry);
        var first = new Session(10);
        var second = new Session(11);
        await lifecycle.AuthenticateAsync(first, Authentication(seat), CancellationToken.None);
        await lifecycle.AuthenticateAsync(second, Authentication(seat), CancellationToken.None);
        Assert.True(first.Connection.IsClosed);
        Assert.Null(registry.TryGet(first.Id));
        Assert.False(registry.TryGetSessionOfConnection(first.Connection.Id, out _));
        Assert.Same(room, registry.FindByConnection(second.Connection.Id));
        lifecycle.Disconnect(first.Connection.Id);
        registry.Detach(first.Id);
        Assert.Same(second, seat.Relay);
        Assert.Single(room.Seats);
        await Assert.ThrowsAsync<InvalidOperationException>(() => lifecycle.RegisterSceneAsync(first, new(7, 1000000, "host"), CancellationToken.None).AsTask());
        lifecycle.Disconnect(second.Connection.Id);
        Assert.Null(seat.Relay);
        Assert.Single(room.Seats);
        Assert.False(registry.TryGetSessionOfConnection(second.Connection.Id, out _));
        Assert.Same(room, registry.TryGet(seat.Game.Id));
    }

    [Fact]
    public async Task CapacityAndUnloadRevokeRetainedConnections()
    {
        var (registry, _, seat) = Room();
        var lifecycle = Lifecycle(registry, new() { MaxConnections = 1 });
        var first = new Session(10);
        await lifecycle.AuthenticateAsync(first, Authentication(seat), CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => lifecycle.AuthenticateAsync(new Session(11), Authentication(seat), CancellationToken.None).AsTask());
        lifecycle.Dispose();
        Assert.Null(seat.Relay);
        Assert.False(registry.IsMatchBound(first));
        Assert.True(first.Connection.IsClosed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => lifecycle.AuthenticateAsync(new Session(12), Authentication(seat), CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task UnregisteredConnectionCannotNameMapAndFailedSendReleasesCapacity()
    {
        var (registry, _, seat) = Room();
        using var lifecycle = Lifecycle(registry, new() { MaxConnections = 1 });
        var failed = new Session(10) { FailSend = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() => lifecycle.NameMapAsync(failed, new(1000000, "host", "map"), CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<IOException>(() => lifecycle.AuthenticateAsync(failed, Authentication(seat), CancellationToken.None).AsTask());
        Assert.Null(seat.Relay);
        await lifecycle.AuthenticateAsync(new Session(11), Authentication(seat), CancellationToken.None);
    }

    [Fact]
    public async Task HandshakeExpiresAndReleasesTheLease()
    {
        var (registry, _, seat) = Room();
        using var lifecycle = Lifecycle(registry, new() { HandshakeTimeoutMs = 1000 });
        var relay = new Session(10);
        await lifecycle.AuthenticateAsync(relay, Authentication(seat), CancellationToken.None);
        await ((Connection)relay.Connection).Closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(seat.Relay);
        Assert.False(registry.IsMatchBound(relay));
    }

    [Fact]
    public void SceneCodecConsumesAllNestedFieldsAndRejectsTruncationTrailingBytesAndOversizedCounts()
    {
        var bytes = Bytes(w =>
        {
            w.WriteInt32(7); w.WriteInt32(1000000); w.WriteWideStringPrefixed("host");
            w.WriteInt32(4300); w.WriteInt32(300001);
            for (var i = 0; i < 5; i++) w.WriteInt32(0);
            w.WriteInt32(0); w.WriteInt32(0); w.WriteInt32(0);
            w.WriteWideStringPrefixed("scene");
            w.WriteByte(0); w.WriteUInt16(1); w.WriteUInt16(0); w.WriteUInt16(0);
            w.WriteUInt16(0); w.WriteByte(0); w.WriteInt32(0); w.WriteInt32(0);
        });
        Assert.Equal(92, bytes.Length);
        Assert.Equal(new MatchSceneRegistered(7, 1000000, "host"), RelayHandshakeWire.ReadScene(Tables, Build, bytes));
        for (var length = 0; length < bytes.Length; length++)
            Assert.Throws<PacketFormatException>(() => RelayHandshakeWire.ReadScene(Tables, Build, bytes.AsSpan(0, length)));
        Assert.Throws<PacketFormatException>(() => RelayHandshakeWire.ReadScene(Tables, Build, [.. bytes, 0]));
        BitConverter.GetBytes(uint.MaxValue).CopyTo(bytes, 28);
        Assert.Throws<PacketFormatException>(() => RelayHandshakeWire.ReadScene(Tables, Build, bytes));
    }

    [Fact]
    public void AuthenticationCodecConsumesTheTwentyByteTicketBodyAndRejectsMalformed()
    {
        // kr-1256 sub_95FD30: 20-byte key, u8 unknown20, i32 unknown21, i32 account, wstring nickname.
        var body = Bytes(w =>
        {
            w.WriteBytes(new byte[20]);
            w.WriteByte(0);
            w.WriteInt32(0);
            w.WriteInt32(7);
            w.WriteWideStringPrefixed("host");
        });
        Assert.Equal(20 + 1 + 4 + 4 + 4 + (2 * 4), body.Length);
        Assert.Equal(41, body.Length);

        var parsed = RelayHandshakeWire.ReadAccountJoin(Tables, Build, body);
        Assert.Equal(20, parsed.Ticket.Length);
        Assert.Equal(7, parsed.UserNumber);
        Assert.Equal("host", parsed.Nickname);

        for (var length = 0; length < body.Length; length++)
            Assert.Throws<PacketFormatException>(() => RelayHandshakeWire.ReadAccountJoin(Tables, Build, body.AsSpan(0, length)));
        Assert.Throws<PacketFormatException>(() => RelayHandshakeWire.ReadAccountJoin(Tables, Build, [.. body, 0]));
    }

    [Fact]
    public void MapCodecRequiresThe1256MarkerAndConsumesExactly()
    {
        // kr-1256 sub_9F02F0: i32 version, i32 flag, wstring map, i32 entity, wstring nickname,
        // i32 scene id, i32 mode.
        var body = Bytes(w =>
        {
            w.WriteInt32(1256);
            w.WriteInt32(0);
            w.WriteWideStringPrefixed("map");
            w.WriteInt32(1000000);
            w.WriteWideStringPrefixed("host");
            w.WriteInt32(0);
            w.WriteInt32(0);
        });
        Assert.Equal(4 + 4 + (4 + 2 * 3) + 4 + (4 + 2 * 4) + 4 + 4, body.Length);
        Assert.Equal(42, body.Length);

        Assert.Equal(new MatchMapNamed(1000000, "host", "map"), RelayHandshakeWire.ReadMap(Tables, Build, 1256, body));

        var wrongVersion = (byte[])body.Clone();
        BitConverter.GetBytes(812).CopyTo(wrongVersion, 0);
        Assert.Throws<PacketFormatException>(() => RelayHandshakeWire.ReadMap(Tables, Build, 1256, wrongVersion));

        for (var length = 0; length < body.Length; length++)
            Assert.Throws<PacketFormatException>(() => RelayHandshakeWire.ReadMap(Tables, Build, 1256, body.AsSpan(0, length)));
        Assert.Throws<PacketFormatException>(() => RelayHandshakeWire.ReadMap(Tables, Build, 1256, [.. body, 0]));
    }

    [Fact]
    public async Task APreviousRoundsTicketAndLeaseCannotAuthenticateTheNextRound()
    {
        var (registry, room, seat) = Room();
        using var lifecycle = Lifecycle(registry);
        var oldTicket = Authentication(seat);
        var oldRelay = new Session(10);
        await lifecycle.AuthenticateAsync(oldRelay, oldTicket, CancellationToken.None);
        var binding = new MatchSeatBinding(room.Id, seat.Slot, seat.UserNum, 1000000, seat.Nickname,
            room.Setting.Map.Unknown68, room.StartGeneration);
        room.ResetToWaiting();
        room.MarkStarting();
        seat.EndpointAdvertised = true;
        Assert.False(registry.IsCurrent(oldRelay, binding));
        await Assert.ThrowsAsync<InvalidOperationException>(() => lifecycle.AuthenticateAsync(new Session(11), oldTicket, CancellationToken.None).AsTask());
        var next = new Session(12);
        await lifecycle.AuthenticateAsync(next, Authentication(seat), CancellationToken.None);
        Assert.Same(next, seat.Relay);
    }

    [Fact]
    public async Task CanceledAuthenticationReleasesCapacityAndSceneIsVisibleBeforeSuccessReply()
    {
        var (registry, _, seat) = Room();
        using var lifecycle = Lifecycle(registry, new() { MaxConnections = 1 });
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lifecycle.AuthenticateAsync(new Session(10), Authentication(seat), canceled.Token).AsTask());
        var relay = new Session(11) { BeforeSend = opcode => { if (opcode == 13) Assert.True(seat.RelayInitialized); } };
        await lifecycle.AuthenticateAsync(relay, Authentication(seat), CancellationToken.None);
        await lifecycle.RegisterSceneAsync(relay, new(7, 1000000, "host"), CancellationToken.None);
    }

    [Fact]
    public async Task UnloadDuringAnAwaitedBindCannotResurrectTheLeaseOrSendReplies()
    {
        var (registry, _, seat) = Room();
        var blocked = new DelayedDirectory(registry);
        var lifecycle = new MatchConnectionLifecycle(blocked, new Sender(), new Sender(), new(), NullLogger.Instance);
        var relay = new Session(10);
        var pending = lifecycle.AuthenticateAsync(relay, Authentication(seat), CancellationToken.None).AsTask();
        await blocked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        lifecycle.Dispose();
        blocked.Continue.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Null(seat.Relay);
        Assert.Empty(relay.Sent);
        Assert.True(relay.Connection.IsClosed);
    }

    private sealed class DelayedDirectory(IMatchRoomDirectory inner) : IMatchRoomDirectory
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<MatchSeatBinding> BindAsync(ISession session, MatchAuthenticate message, CancellationToken ct)
        {
            Entered.TrySetResult();
            await Continue.Task;
            return await inner.BindAsync(session, message, CancellationToken.None);
        }
        public bool IsCurrent(ISession session, MatchSeatBinding binding) => inner.IsCurrent(session, binding);
        public void MarkInitialized(ISession session, MatchSeatBinding binding) => inner.MarkInitialized(session, binding);
        public void Release(ISession session) => inner.Release(session);
    }

    private static (RoomRegistry Registry, PlayRoom Room, RoomSeat Seat) Room()
    {
        var registry = new RoomRegistry(new InGameOptions());
        var game = new Session(1);
        var setting = new SessionSettingBlob(0, 0, "", 0, 0, 0, 0, "", 2,
            MapMetaBlob.Empty() with { Unknown4 = 1, Unknown68 = "rel_Village01_Byway" }, 0, 0, 0, 0, "", 0, 0, 0, 0, 0);
        var (room, seat) = registry.Create(game, setting, 7, "host");
        room.MarkStarting();
        seat.EndpointAdvertised = true;
        return (registry, room, seat);
    }

    private static MatchAuthenticate Authentication(RoomSeat seat) => new(Bytes(seat.Key.Write), seat.UserNum, seat.Nickname);
    private static byte[] Bytes(Action<PacketWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        write(new PacketWriter(buffer));
        return buffer.WrittenSpan.ToArray();
    }
    private static MatchConnectionLifecycle Lifecycle(RoomRegistry registry, MatchConnectionOptions? options = null) =>
        new(registry, new Sender(), new Sender(), options ?? new(), NullLogger.Instance);
    // The lifecycle's replies through the tables, the frames dialect.match-replies sends on kr-1256: the
    // accept pair (S2C 22 settings then S2C 1 accepted) and the scene accept (S2C 4/5/6 seeds, then S2C 13).
    private sealed class Sender : IMessageSender<MatchAuthenticationAccepted>, IMessageSender<MatchSceneAccepted>
    {
        public string Protocol => "kr-1256/relay";

        public async ValueTask SendAsync(ISession session, MatchAuthenticationAccepted message, CancellationToken ct)
        {
            await session.SendAsync(22, PacketCodec.Write(Tables, Build, "RelayHandshakeSettings", new Dictionary<string, object?>()), ct);
            await session.SendAsync(1, PacketCodec.Write(Tables, Build, "RelayJoinAcceptedSignal", new Dictionary<string, object?>()), ct);
        }

        public async ValueTask SendAsync(ISession session, MatchSceneAccepted message, CancellationToken ct)
        {
            var seeds = new Dictionary<string, object?> { ["entity_id"] = (uint)message.EntityId };
            await session.SendAsync(4, PacketCodec.Write(Tables, Build, "RelayCriticalSeedTable",
                new Dictionary<string, object?>(seeds) { ["unknown_8"] = Zeros(32), ["unknown_24"] = Zeros(32) }), ct);
            await session.SendAsync(5, PacketCodec.Write(Tables, Build, "RelayDefenceSeedTable",
                new Dictionary<string, object?>(seeds) { ["unknown_8"] = Zeros(1000) }), ct);
            await session.SendAsync(6, PacketCodec.Write(Tables, Build, "RelayCriticalSeedCancelTable",
                new Dictionary<string, object?>(seeds) { ["unknown_8"] = Zeros(32) }), ct);
            await session.SendAsync(13, PacketCodec.Write(Tables, Build, "RelaySceneAcceptedSignal", new Dictionary<string, object?>()), ct);
        }

        private static object?[] Zeros(int count) => Enumerable.Repeat<object?>(0u, count).ToArray();
    }
    private sealed class Session(long id) : ISession
    {
        public long Id => id;
        public IConnection Connection { get; } = new Connection(id);
        public ClientProfile Profile { get; } = new("test", "kr", 1256, 0, 0, "test");
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public List<(ushort Opcode, byte[] Body)> Sent { get; } = [];
        public bool FailSend { get; init; }
        public Action<ushort>? BeforeSend { get; init; }
        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (FailSend) throw new IOException("Test send failure.");
            BeforeSend?.Invoke(opcode);
            Sent.Add((opcode, payload.ToArray()));
            return ValueTask.CompletedTask;
        }
    }
    private sealed class Connection(long id) : IConnection
    {
        public long Id => id;
        public EndPoint RemoteEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 1);
        public PipeReader Input => throw new NotSupportedException();
        public PipeWriter Output => throw new NotSupportedException();
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public bool IsClosed { get; private set; }
        public TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask CloseAsync(string reason) { IsClosed = true; Closed.TrySetResult(); return ValueTask.CompletedTask; }
    }
}
