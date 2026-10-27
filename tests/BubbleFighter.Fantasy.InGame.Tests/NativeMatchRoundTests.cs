using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using System.Runtime.InteropServices;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.InGame.Handlers;
using BubbleFighter.Fantasy.InGame.Sessions;
using BubbleFighter.Fantasy.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>Exercises issued native offers and authenticated loading gates through real shared room and round flows.</summary>
public sealed class NativeMatchRoundTests
{
    [Theory]
    [InlineData("kr-812", 20)]
    [InlineData("cn-717", 20)]
    [InlineData("cn-324", 18)]
    [InlineData("cn-279", 12)]
    [InlineData("cn-406", 20)]
    [InlineData("id-400", 20)]
    [InlineData("id-1809", 20)]
    public async Task LoadedSeatsReceiveDistinctRoundKeysAndRequireRealRelayInitialization(string build, int length)
    {
        await using var rig = new Rig(build, length);
        await rig.StartAsync();
        Assert.Empty(rig.Offers.Sent);
        await rig.LoadAsync(rig.Host);
        var hostOffer = Assert.Single(rig.Offers.Sent).Message;
        Assert.Equal(length, hostOffer.Ticket.Length);
        Assert.Equal(IPAddress.Parse("10.20.30.40"), hostOffer.Address);
        Assert.Equal(7111, hostOffer.Port);
        await rig.LoadAsync(rig.Host);
        Assert.Single(rig.Offers.Sent);
        await rig.LoadAsync(rig.Guest);
        Assert.NotEqual(hostOffer.Ticket.ToArray(), rig.Offers.Sent[1].Message.Ticket.ToArray());
        Assert.Empty(rig.Countdowns.Sent);
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Round.HandleMatchConnectionReportedAsync(rig.Host, new(true), default).AsTask());
        var hostRelay = new Session(101, build);
        var hostBinding = await rig.Registry.BindCapabilityAsync(hostRelay, new(hostOffer.Ticket), default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Round.HandleMatchConnectionReportedAsync(rig.Host, new(true), default).AsTask());
        rig.Registry.MarkInitialized(hostRelay, hostBinding);
        await rig.Round.HandleMatchConnectionReportedAsync(rig.Host, new(true), default);
        Assert.Empty(rig.Countdowns.Sent);
        var guestRelay = new Session(102, build);
        var guestBinding = await rig.Registry.BindCapabilityAsync(guestRelay, new(rig.Offers.Sent[1].Message.Ticket), default);
        rig.Registry.MarkInitialized(guestRelay, guestBinding);
        await rig.Round.HandleMatchConnectionReportedAsync(rig.Guest, new(true), default);
        Assert.Equal(2, rig.Countdowns.Sent.Count);
        await rig.Round.HandleMatchConnectionReportedAsync(rig.Guest, new(true), default);
        await rig.Round.HandleMatchConnectionReportedAsync(rig.Host, new(false), default);
        Assert.Same(hostRelay, rig.Room.Seats[0].Relay);
        Assert.True(rig.Room.Seats[0].RelayConfirmed);
        Assert.Equal(2, rig.Countdowns.Sent.Count);
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.LoadAsync(hostRelay));
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Round.HandleMatchConnectionReportedAsync(hostRelay, new(true), default).AsTask());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOfferRevokesItsKeyAndCanRetryEvenIfTheSenderMutatesItsBuffer(bool mutate)
    {
        await using var rig = new Rig("cn-324", 18);
        await rig.StartAsync();
        rig.Offers.Fail = true;
        rig.Offers.Mutate = mutate;
        await Assert.ThrowsAsync<IOException>(() => rig.LoadAsync(rig.Host));
        var failed = rig.Offers.Sent[0].Message.Ticket.ToArray();
        var seat = rig.Room.Seats[0];
        Assert.False(seat.SceneReady);
        Assert.False(seat.EndpointAdvertised);
        Assert.Null(seat.NativeCapability);
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Registry.BindCapabilityAsync(new Session(101, "cn-324"), new(failed), default).AsTask());
        rig.Offers.Fail = rig.Offers.Mutate = false;
        await rig.LoadAsync(rig.Host);
        var next = rig.Offers.Sent[^1].Message.Ticket;
        Assert.NotEqual(failed, next.ToArray());
        Assert.True(seat.EndpointAdvertised);
        var relay = new Session(102, "cn-324");
        await rig.Registry.BindCapabilityAsync(relay, new(next), default);
        Assert.Same(relay, seat.Relay);
        Assert.Empty(rig.Countdowns.Sent);
    }

    [Fact]
    public async Task LaterRoundsAndReplacementLeasesRejectStaleSuccessAndKeys()
    {
        await using var rig = new Rig("kr-812", 20);
        await rig.StartAsync();
        await rig.LoadAsync(rig.Host);
        var oldKey = rig.Offers.Sent[0].Message.Ticket;
        var oldRelay = new Session(101, "kr-812");
        var oldBinding = await rig.Registry.BindCapabilityAsync(oldRelay, new(oldKey), default);
        rig.Registry.MarkInitialized(oldRelay, oldBinding);
        await rig.Round.HandleMatchConnectionReportedAsync(rig.Host, new(true), default);
        var replacement = new Session(102, "kr-812");
        var replacementBinding = await rig.Registry.BindCapabilityAsync(replacement, new(oldKey), default);
        Assert.True(oldRelay.Connection.IsClosed);
        Assert.False(rig.Room.Seats[0].RelayConfirmed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Round.HandleMatchConnectionReportedAsync(rig.Host, new(true), default).AsTask());
        rig.Registry.MarkInitialized(replacement, replacementBinding);
        await rig.Round.HandleMatchConnectionReportedAsync(rig.Host, new(true), default);
        rig.Registry.Release(oldRelay);
        Assert.Same(replacement, rig.Room.Seats[0].Relay);
        await rig.Room.Executor.ExecuteAsync(_ => { rig.Room.ResetToWaiting(); rig.Room.MarkStarting(); return ValueTask.CompletedTask; });
        await rig.LoadAsync(rig.Host);
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Registry.BindCapabilityAsync(new Session(103, "kr-812"), new(oldKey), default).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Round.HandleMatchConnectionReportedAsync(rig.Host, new(true), default).AsTask());
        await rig.Round.HandleMatchConnectionReportedAsync(rig.Host, new(false), default);
        Assert.Empty(rig.Countdowns.Sent);
    }

    [Fact]
    public async Task DefaultGameFeedUsesOnlyTheExistingLoadBarrier()
    {
        await using var rig = new Rig("cn-324", 18, enabled: false);
        await rig.StartAsync();
        await rig.LoadAsync(rig.Host);
        await rig.LoadAsync(rig.Guest);
        Assert.Empty(rig.Offers.Sent);
        Assert.Equal(2, rig.Countdowns.Sent.Count);
    }

    [Fact]
    public async Task LostTransportDuringCountdownPreventsStartAndAValidReplacementCanRearmIt()
    {
        await using var rig = new Rig("kr-812", 20, countdownMs: 100);
        await rig.StartAsync();
        foreach (var game in new[] { rig.Host, rig.Guest })
        {
            await rig.LoadAsync(game);
            var relay = new Session(100 + game.Id, "kr-812");
            var binding = await rig.Registry.BindCapabilityAsync(relay, new(rig.Offers.Sent[^1].Message.Ticket), default);
            rig.Registry.MarkInitialized(relay, binding);
            await rig.Round.HandleMatchConnectionReportedAsync(game, new(true), default);
        }
        await rig.Room.Seats[1].Relay!.Connection.CloseAsync("test loss before countdown ends");
        await rig.Room.CountdownTask.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Empty(rig.Started.Sent);
        Assert.Equal(RoomPhase.Starting, rig.Room.Phase);
        Assert.Equal(0, rig.Room.CountdownGeneration);
        var replacement = new Session(200, "kr-812");
        var next = await rig.Registry.BindCapabilityAsync(replacement, new(rig.Offers.Sent[1].Message.Ticket), default);
        rig.Registry.MarkInitialized(replacement, next);
        await rig.Round.HandleMatchConnectionReportedAsync(rig.Guest, new(true), default);
        Assert.Equal(4, rig.Countdowns.Sent.Count);
        await rig.Room.CountdownTask.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(RoomPhase.Playing, rig.Room.Phase);
        Assert.Equal(2, rig.Started.Sent.Count);
    }

    /// <summary>
    /// A seat whose relay reports failure while loading plays over its game connection: the countdown starts once the
    /// other seat's relay is confirmed, only that seat's countdown clears the relay selection, its unused key can no
    /// longer bind a relay, and relay-borne feed reaches it on the game connection while the other seat keeps its relay.
    /// </summary>
    [Fact]
    public async Task FailedRelayMovesOnlyThatSeatToItsGameConnection()
    {
        await using var rig = new Rig("kr-812", 20, fallback: true);
        await rig.StartAsync();
        await rig.LoadAsync(rig.Host);
        var hostRelay = new Session(101, "kr-812");
        rig.Registry.MarkInitialized(hostRelay, await rig.Registry.BindCapabilityAsync(hostRelay, new(rig.Offers.Sent[0].Message.Ticket), default));
        await rig.Round.HandleMatchConnectionReportedAsync(rig.Host, new(true), default);
        await rig.LoadAsync(rig.Guest);
        Assert.Empty(rig.Countdowns.Sent);

        await rig.Round.HandleMatchConnectionReportedAsync(rig.Guest, new(false), default);
        Assert.Equal(2, rig.Countdowns.Sent.Count);
        Assert.False(rig.Countdowns.Sent[0].MatchOverGame);
        Assert.True(rig.Countdowns.Sent[1].MatchOverGame);
        await Assert.ThrowsAnyAsync<Exception>(() =>
            rig.Registry.BindCapabilityAsync(new Session(102, "kr-812"), new(rig.Offers.Sent[1].Message.Ticket), default).AsTask());
        // A repeated failure report after the countdown changes nothing.
        await rig.Round.HandleMatchConnectionReportedAsync(rig.Guest, new(false), default);
        Assert.Equal(2, rig.Countdowns.Sent.Count);

        await rig.Round.HandleFeedAsync(hostRelay, new RoundFeedSent(32885, new byte[] { 1 }) { OnRelay = true }, default);
        await rig.Round.HandleFeedAsync(rig.Guest, new RoundFeedSent(32885, new byte[] { 2 }) { OnRelay = true }, default);
        Assert.Equal([(rig.Guest.Id, (byte)1), (hostRelay.Id, (byte)2)],
            rig.Feed.Sent.Select(sent => (sent.Session, sent.Message.Payload.Span[0])).ToArray());
    }

    /// <summary>Without the fallback a failure report keeps the loading barrier closed, as before.</summary>
    [Fact]
    public async Task FailedRelayWithoutFallbackKeepsWaiting()
    {
        await using var rig = new Rig("kr-812", 20);
        await rig.StartAsync();
        await rig.LoadAsync(rig.Host);
        await rig.LoadAsync(rig.Guest);
        await rig.Round.HandleMatchConnectionReportedAsync(rig.Guest, new(false), default);
        Assert.Empty(rig.Countdowns.Sent);
        Assert.Throws<ArgumentException>(() => Validate(new InGameOptions { MatchTransportFallback = true }));
    }

    private static void Validate(InGameOptions options)
    {
        if (options.Validate().FirstOrDefault(result => result.MemberNames.Contains(nameof(InGameOptions.MatchTransportFallback))) is { } failure)
            throw new ArgumentException(failure.ErrorMessage);
    }

    [Fact]
    public async Task NativePolicyRejectsMissingWrongScopedOrOversizedOfferEncoders()
    {
        var options = new InGameOptions { NativeMatchRelay = true, GameProtocol = "kr-812/game" };
        var registry = new RoomRegistry(options);
        var senders = new RoundSenders(new Sink<RoundPlayerLoaded>(), new Sink<RoundCountdownStarted>(), new Sink<RoundStarted>());
        Assert.Throws<ConfigException>(() => new RoundFlow(registry, options, senders, NullLogger.Instance, default, (_, _) => ValueTask.CompletedTask));
        foreach (var sender in new[] { new Offers("cn-324/game", 20), new Offers("kr-812/game", 0), new Offers("kr-812/game", 65) })
            Assert.Throws<ConfigException>(() => new RoundFlow(registry, options, senders, NullLogger.Instance, default,
                (_, _) => ValueTask.CompletedTask, capabilityOffers: sender));
        await registry.StopAllAsync();
    }

    private sealed class Rig : IAsyncDisposable
    {
        private readonly CancellationTokenSource _lifetime = new();
        internal RoomRegistry Registry { get; }
        internal RoomFlow Rooms { get; }
        internal RoundFlow Round { get; }
        internal Offers Offers { get; }
        internal Sink<RoundCountdownStarted> Countdowns { get; } = new();
        internal Sink<RoundStarted> Started { get; } = new();
        internal Recorder<RoundFeedReceived> Feed { get; } = new();
        internal Session Host { get; }
        internal Session Guest { get; }
        internal PlayRoom Room => Registry.TryGet(Host.Id)!;
        internal Rig(string build, int length, bool enabled = true, int countdownMs = 60000, bool fallback = false)
        {
            Host = new(11, build); Host.SetLobbyPlayer(11, "host");
            Guest = new(22, build); Guest.SetLobbyPlayer(22, "guest");
            Offers = new(build + "/game", length);
            var options = new InGameOptions { NativeMatchRelay = enabled, GameProtocol = build + "/game",
                RelayProtocol = build + "/relay", RelayAdvertiseIpv4 = "10.20.30.40", RelayAdvertisePort = 7111, StartTimerMs = countdownMs,
                MatchTransportFallback = fallback };
            Registry = new(options);
            Rooms = new(Registry, new RoomSenders(new Sink<RoomEntered>(), new Sink<RoomSeatUpdated>(), new Sink<RoomSeatVacated>(),
                new Sink<RoomMasterChanged>(), MatchStarting: new Sink<RoomMatchStarting>()), NullLogger.Instance, options: options);
            Round = new(Registry, options, new RoundSenders(new Sink<RoundPlayerLoaded>(), Countdowns, Started, Feed),
                NullLogger.Instance, _lifetime.Token, Rooms.ReturnToRoomAsync, capabilityOffers: Offers);
        }
        internal async Task StartAsync()
        {
            await Rooms.HandleCreateAsync(Host, new("room", "", 3, "map"), default);
            await Rooms.HandleJoinAsync(Guest, new(Room.Id, ""), default);
            await Rooms.HandleReadyAsync(Guest, new(true), default);
            await Rooms.HandleStartAsync(Host, new("map"), default);
        }
        internal Task LoadAsync(ISession session) => Round.HandleLoadReportedAsync(session, new(), default).AsTask();
        public async ValueTask DisposeAsync()
        {
            await _lifetime.CancelAsync();
            await Registry.StopAllAsync();
            _lifetime.Dispose();
        }
    }

    private sealed class Sink<T> : IMessageSender<T> where T : IMessage
    {
        public string Protocol => "test/game";
        internal List<T> Sent { get; } = [];
        public ValueTask SendAsync(ISession session, T message, CancellationToken ct) { Sent.Add(message); return ValueTask.CompletedTask; }
    }
    private sealed class Recorder<T> : IMessageSender<T> where T : IMessage
    {
        public string Protocol => "test/game";
        internal List<(long Session, T Message)> Sent { get; } = [];
        public ValueTask SendAsync(ISession session, T message, CancellationToken ct) { Sent.Add((session.Id, message)); return ValueTask.CompletedTask; }
    }
    private sealed class Offers(string protocol, int length) : IMatchCapabilityOfferSender
    {
        public string Protocol => protocol;
        public int TicketLength => length;
        internal List<(long Session, MatchCapabilityOffered Message)> Sent { get; } = [];
        internal bool Fail { get; set; }
        internal bool Mutate { get; set; }
        public ValueTask SendAsync(ISession session, MatchCapabilityOffered message, CancellationToken ct)
        {
            Sent.Add((session.Id, message));
            if (Mutate && MemoryMarshal.TryGetArray(message.Ticket, out var bytes)) bytes.Array![bytes.Offset] ^= 0xFF;
            if (Fail) throw new IOException("Offer send failed.");
            return ValueTask.CompletedTask;
        }
    }
    private sealed class Session(long id, string build) : ISessionWithFeatures
    {
        public long Id => id;
        public IConnection Connection { get; } = new Connection(id);
        public ClientProfile Profile { get; } = new(build, build[..2], int.Parse(build[3..]), 0, 0, build);
        public ISessionFeatures Features { get; } = new TestSessionFeatures();
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> body, CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class Connection(long id) : IConnection
    {
        public long Id => id;
        public EndPoint RemoteEndPoint => new IPEndPoint(IPAddress.Loopback, 1);
        public PipeReader Input => throw new NotSupportedException();
        public PipeWriter Output => throw new NotSupportedException();
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public bool IsClosed { get; private set; }
        public ValueTask CloseAsync(string reason) { IsClosed = true; return ValueTask.CompletedTask; }
    }
}
