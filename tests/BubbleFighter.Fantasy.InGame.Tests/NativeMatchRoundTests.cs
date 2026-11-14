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
public sealed partial class NativeMatchRoundTests
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
        await rig.Round.HandleMatchConnectionReportedAsync(rig.Host, new(true), default);
        Assert.False(rig.Room.Seats[0].RelayConfirmed);
        Assert.False(rig.Host.Connection.IsClosed);
        Assert.Empty(rig.Countdowns.Sent);
        var hostRelay = new Session(101, build);
        var hostBinding = await rig.Registry.BindCapabilityAsync(hostRelay, new(hostOffer.Ticket), default);
        await rig.Round.HandleMatchConnectionReportedAsync(rig.Host, new(true), default);
        Assert.False(rig.Room.Seats[0].RelayConfirmed);
        Assert.False(rig.Host.Connection.IsClosed);
        Assert.Empty(rig.Countdowns.Sent);
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
        await rig.LoadAsync(hostRelay);
        await rig.Round.HandleMatchConnectionReportedAsync(hostRelay, new(true), default);
        Assert.False(hostRelay.Connection.IsClosed);
        Assert.Equal(2, rig.Countdowns.Sent.Count);
        Assert.Equal(2, rig.Offers.Sent.Count);
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
        await rig.Round.HandleMatchConnectionReportedAsync(rig.Host, new(true), default);
        Assert.False(rig.Room.Seats[0].RelayConfirmed);
        Assert.False(rig.Host.Connection.IsClosed);
        Assert.Empty(rig.Countdowns.Sent);
        rig.Registry.MarkInitialized(replacement, replacementBinding);
        await rig.Round.HandleMatchConnectionReportedAsync(rig.Host, new(true), default);
        rig.Registry.Release(oldRelay);
        Assert.Same(replacement, rig.Room.Seats[0].Relay);
        await rig.Room.Executor.ExecuteAsync(_ => { rig.Room.ResetToWaiting(); rig.Room.MarkStarting(); return ValueTask.CompletedTask; });
        await rig.LoadAsync(rig.Host);
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Registry.BindCapabilityAsync(new Session(103, "kr-812"), new(oldKey), default).AsTask());
        await rig.Round.HandleMatchConnectionReportedAsync(rig.Host, new(true), default);
        Assert.False(rig.Room.Seats[0].RelayConfirmed);
        Assert.False(rig.Host.Connection.IsClosed);
        Assert.Empty(rig.Countdowns.Sent);
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

    /// <summary>
    /// A legacy round with one seat on its relay and one fallen back to its game connection: the relay seat's game
    /// session is linked from its relay so the dialect can hand relay frames to the game handlers, peer feed that
    /// arrives as the sender's game session reaches each seat on its match link, and the countdown requests seed
    /// tables only for the relay seat, keyed by its scene entity.
    /// </summary>
    [Fact]
    public async Task MixedRelayAndFallbackSeatsGetFeedOnTheirMatchLinkAndSeedsOnlyOnTheRelay()
    {
        await using var rig = new Rig("cn-324", 18, fallback: true);
        await rig.StartAsync();
        await rig.LoadAsync(rig.Host);
        var hostRelay = new Session(101, "cn-324");
        rig.Registry.MarkInitialized(hostRelay, await rig.Registry.BindCapabilityAsync(hostRelay, new(rig.Offers.Sent[0].Message.Ticket), default));
        Assert.True(hostRelay.TryFeature<IMatchGameLink>(out var link));
        Assert.Same(rig.Host, link.Game);
        await rig.Round.HandleMatchConnectionReportedAsync(rig.Host, new(true), default);
        await rig.LoadAsync(rig.Guest);
        Assert.Empty(rig.Seeds.Sent);

        await rig.Round.HandleMatchConnectionReportedAsync(rig.Guest, new(false), default);
        Assert.Equal([false, true], rig.Countdowns.Sent.Select(sent => sent.MatchOverGame));
        var seed = Assert.Single(rig.Seeds.Sent);
        Assert.Equal(hostRelay.Id, seed.Session);
        Assert.Equal(RoomRegistry.SceneEntity(rig.Room.Seats[0].Slot), seed.Message.EntityId);
        Assert.Equal(rig.Room.Id, seed.Message.RoomId);
        Assert.Equal(MatchPreviousRound.None, seed.Message.PreviousRound);

        // Bridged relay frames carry the game session and no relay flag; the native relay still routes them per seat.
        await rig.Round.HandleFeedAsync(rig.Host, new RoundFeedSent(32885, new byte[] { 1 }), default);
        await rig.Round.HandleFeedAsync(rig.Guest, new RoundFeedSent(32885, new byte[] { 2 }), default);
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

    [Fact]
    public async Task PeerRosterSnapshotsExposeOnlyCurrentRoundRelayAndKeepOldViewsImmutable()
    {
        await using var rig = new Rig("cn-324", 18);
        await rig.StartAsync();
        var roster = (IRoomPeerRoster)rig.Registry;
        Assert.Equal("cn-324/game", roster.Protocol);
        var before = Assert.IsType<RoomRosterView>(roster.FindBySession(rig.Host.Id));
        Assert.Equal(RoomStage.Starting, before.Stage);
        Assert.All(before.Seats, seat => Assert.Null(seat.Relay));
        await rig.LoadAsync(rig.Host);
        var relay = new Session(101, "cn-324");
        var binding = await rig.Registry.BindCapabilityAsync(relay, new(rig.Offers.Sent[0].Message.Ticket), default);
        rig.Registry.MarkInitialized(relay, binding);
        var current = Assert.IsType<RoomRosterView>(roster.FindBySession(relay.Id));
        Assert.Same(relay, current.Seats[0].Relay);
        Assert.True(current.Seats[0].RelayInitialized);
        Assert.Null(before.Seats[0].Relay);
        await rig.Room.Executor.ExecuteAsync(_ =>
        {
            rig.Room.ResetToWaiting();
            rig.Room.MarkStarting();
            return ValueTask.CompletedTask;
        });
        var next = Assert.IsType<RoomRosterView>(roster.Find(rig.Room.Id));
        Assert.True(next.Generation > current.Generation);
        Assert.All(next.Seats, seat => { Assert.Null(seat.Relay); Assert.False(seat.RelayInitialized); });
        Assert.Same(relay, current.Seats[0].Relay);
        Assert.Null(roster.FindBySession(9999));
    }

    [Fact]
    public async Task DirectMovementExclusionPreservesActorValidationAndOtherFeeds()
    {
        await using var rig = new Rig("cn-324", 18);
        await rig.StartAsync();
        foreach (var game in new[] { rig.Host, rig.Guest })
        {
            await rig.LoadAsync(game);
            var relay = new Session(100 + game.Id, "cn-324");
            rig.Registry.MarkInitialized(relay, await rig.Registry.BindCapabilityAsync(relay,
                new(rig.Offers.Sent[^1].Message.Ticket), default));
        }
        var hostActor = RoomRegistry.SceneEntity(rig.Room.Seats[0].Slot);
        var guestActor = RoomRegistry.SceneEntity(rig.Room.Seats[1].Slot);
        await rig.Round.HandleFeedAsync(rig.Host, new(32885, new byte[] { 1 })
        { ReportedActors = [hostActor], DirectPeerRecipients = new HashSet<long> { rig.Guest.Id } }, default);
        Assert.Empty(rig.Feed.Sent);
        await rig.Round.HandleFeedAsync(rig.Host,
            new(32885, new byte[] { 2 })
            { ReportedActors = [guestActor], DirectPeerRecipients = new HashSet<long> { rig.Guest.Id } }, default);
        Assert.Empty(rig.Feed.Sent);
        Assert.False(rig.Host.Connection.IsClosed);
        await rig.Round.HandleFeedAsync(rig.Host, new(32900, new byte[] { 3 }) { ReportedActors = [hostActor] }, default);
        var received = Assert.Single(rig.Feed.Sent);
        Assert.Equal(rig.Room.Seats[1].Relay!.Id, received.Session);
        Assert.Equal((ushort)32900, received.Message.Code);
    }

    [Theory]
    [InlineData("cn-324", 18)]
    [InlineData("kr-812", 20)]
    public async Task LateMapLoadingReportsOutsideARoomDoNotDisconnectOrCreateMatchOffers(string build, int ticketLength)
    {
        await using var rig = new Rig(build, ticketLength);
        await rig.LoadAsync(rig.Host);
        Assert.False(rig.Host.Connection.IsClosed);
        Assert.Empty(rig.Offers.Sent);
        await rig.StartAsync();
        await rig.LoadAsync(rig.Host);
        Assert.Single(rig.Offers.Sent);
        await rig.Rooms.HandleLeaveAsync(rig.Host, new(), default);
        await rig.LoadAsync(rig.Host);
        Assert.Null(rig.Registry.TryGet(rig.Host.Id));
        Assert.False(rig.Host.Connection.IsClosed);
        Assert.Single(rig.Offers.Sent);
        Assert.Empty(rig.Started.Sent);
    }

    [Fact]
    public async Task RoundNotificationsBeforeJoiningOrAfterLeavingKeepTheGameConnectionAlive()
    {
        await using var rig = new Rig("cn-324", 18);
        async Task ReportAsync()
        {
            await rig.LoadAsync(rig.Host);
            await rig.Round.HandleObjectAddRequestedAsync(rig.Host, new(1000000), default);
            await rig.Round.HandleMatchConnectionReportedAsync(rig.Host, new(true), default);
            await rig.Round.HandleMatchConnectionReportedAsync(rig.Host, new(false), default);
            await rig.Round.HandleWaveReportedAsync(rig.Host, new(1, true), new Sink<RoundWaveChanged>(), default);
            await rig.Round.HandleSupplyDropAsync(rig.Host, new(new(0, 0, 0, 1, 1000, 2000), 0), default);
            await rig.Round.HandleSupplyPickupAsync(rig.Host, new(1, 1), default);
            await rig.Round.HandleItemUseAsync(rig.Host, new(), default);
            await rig.Round.HandleItemSlotSwitchAsync(rig.Host, new(), default);
            await rig.Round.HandleCountedItemUseAsync(rig.Host, new(1), default);
            await rig.Round.HandleRescueProgressAsync(rig.Host, new(0, 0), default);
            await rig.Round.HandleLucciDropAsync(rig.Host, new(new(0, 0, 0, 0, 0, 0), 1, 0), default);
            await rig.Round.HandleLucciPickupAsync(rig.Host, new(1000000, 1), default);
            await rig.Round.HandleStarDropAsync(rig.Host, new(new(0, 0, 0, 0, 0, 0), 1, 0), default);
            await rig.Round.HandleStarPickupAsync(rig.Host, new(1000000, 1), default);
            Assert.False(rig.Host.Connection.IsClosed);
        }
        await ReportAsync();
        Assert.Empty(rig.Offers.Sent);
        await rig.StartAsync();
        await rig.LoadAsync(rig.Host);
        var offers = rig.Offers.Sent.Count;
        await rig.Rooms.HandleLeaveAsync(rig.Host, new(), default);
        await ReportAsync();
        Assert.Equal(offers, rig.Offers.Sent.Count);
        Assert.Empty(rig.Countdowns.Sent);
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
        internal Recorder<MatchSeedTablesRequested> Seeds { get; } = new();
        internal Recorder<RoomLeft> Departures { get; } = new();
        internal Recorder<RoundWeaponCreated> WeaponDrops { get; } = new();
        internal Recorder<RoundWeaponPicked> WeaponPickups { get; } = new();
        internal Session Host { get; }
        internal Session Guest { get; }
        internal PlayRoom Room => Registry.TryGet(Host.Id)!;
        internal Rig(string build, int length, bool enabled = true, int countdownMs = 60000, bool fallback = false,
            IMatchSeedInputValidator? seedInputs = null, bool weaponObjects = false, IMatchCriticalSeedAuthority? criticalAuthority = null)
        {
            Host = new(11, build); Host.SetLobbyPlayer(11, "host");
            Guest = new(22, build); Guest.SetLobbyPlayer(22, "guest");
            Offers = new(build + "/game", length);
            var options = new InGameOptions { NativeMatchRelay = enabled, GameProtocol = build + "/game",
                RelayProtocol = build + "/relay", RelayAdvertiseIpv4 = "10.20.30.40", RelayAdvertisePort = 7111, StartTimerMs = countdownMs,
                MatchTransportFallback = fallback };
            Registry = new(options);
            Rooms = new(Registry, new RoomSenders(new Sink<RoomEntered>(), new Sink<RoomSeatUpdated>(), new Sink<RoomSeatVacated>(),
                new Sink<RoomMasterChanged>(), Left: Departures, MatchStarting: new Sink<RoomMatchStarting>()), NullLogger.Instance, options: options);
            Round = new(Registry, options, new RoundSenders(new Sink<RoundPlayerLoaded>(), Countdowns, Started, Feed,
                    WeaponCreated: weaponObjects ? WeaponDrops : null, WeaponPicked: weaponObjects ? WeaponPickups : null),
                NullLogger.Instance, _lifetime.Token, Rooms.ReturnToRoomAsync, capabilityOffers: Offers, seedTables: Seeds,
                seedInputs: seedInputs, criticalAuthority: criticalAuthority);
        }
        internal async Task StartAsync(int mode = 3)
        {
            await Rooms.HandleCreateAsync(Host, new("room", "", mode, "map"), default);
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
        internal bool Fail { get; set; }
        public ValueTask SendAsync(ISession session, T message, CancellationToken ct)
        {
            if (Fail) throw new IOException("Publication failed.");
            Sent.Add((session.Id, message));
            return ValueTask.CompletedTask;
        }
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
        internal List<(ushort Opcode, byte[] Body)> Sent { get; } = [];
        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> body, CancellationToken ct)
        {
            Sent.Add((opcode, body.ToArray()));
            return ValueTask.CompletedTask;
        }
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
