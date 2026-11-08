using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Cluster;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Players;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Contracts.Party;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.Tests.Shared;
using BubbleFighter.Fantasy.Lobby.Data;
using BubbleFighter.Fantasy.Cluster;
using BubbleFighter.Fantasy.Persistence.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Lobby.Tests;

public sealed class MigrationAdmissionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostRoomInstallsMenuAndWaitsForDispatchBeforeReturning(bool acknowledge)
    {
        await using var f = new Fixture(acknowledge, recovery: true);
        f.Rooms.Accept = false;
        await f.MoveAsync();
        if (acknowledge) await f.Handler.AcknowledgeAsync(f.Session, new(), default);
        Assert.Equal(SessionPhase.MainMenu, f.Session.Feature<IPlayerSession>().Phase);
        Assert.False(f.Session.Connection.IsClosed);
        Assert.False(f.Parties.Reserved);
        Assert.Equal(1, f.Home.Entries);
        Assert.Equal(1, f.Rooms.Cancels);
        Assert.Equal(0, f.Accounts.Releases);
        await f.Handler.SweepAsync(default);
        Assert.Equal(0, f.Home.Returns);
        f.ContinuationScope.Dispose();
        await f.Handler.SweepAsync(default);
        Assert.Equal(1, f.Home.Returns);
        Assert.False(f.Session.Connection.IsClosed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedSceneOrUnloadDoesNotRevokeAnInstalledFallbackPlayer(bool unload)
    {
        await using var f = new Fixture(acknowledge: false, recovery: true);
        f.Rooms.Accept = false;
        await f.MoveAsync();
        f.ContinuationScope.Dispose();
        if (unload) await f.Handler.DisposeAsync();
        else
        {
            f.Session.Feature<IPlayerSession>().MoveTo(SessionPhase.Plaza);
            await f.Handler.SweepAsync(default);
        }
        Assert.Equal(0, f.Home.Returns);
        Assert.Equal(0, f.Accounts.Releases);
        Assert.False(f.Session.Connection.IsClosed);
        Assert.True(f.Session.TryFeature<IPlayerSession>(out _));
    }

    [Fact]
    public async Task PendingRecoveryKeepsCapacityWithoutHoldingTheAdmissionGate()
    {
        await using var f = new Fixture(acknowledge: false, recovery: true);
        f.Options.MaxMigrations = 1;
        f.Rooms.Accept = false;
        await f.MoveAsync();
        f.ContinuationScope.Dispose();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Home.BeforeReturn = async ct => { entered.SetResult(); await finish.Task.WaitAsync(ct); };
        var sweep = f.Handler.SweepAsync(default).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var refused = new Session(2);
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Handler.MoveAsync(refused, new(8, 0), default)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(refused.Connection.IsClosed);
            Assert.False(sweep.IsCompleted);
        }
        finally { finish.TrySetResult(); await sweep; }
        Assert.False(f.Session.Connection.IsClosed);
        Assert.Equal(0, f.Accounts.Releases);
    }

    [Fact]
    public async Task FailedRecoveryMenuClosesAndReleasesTheClaimedArrival()
    {
        await using var f = new Fixture(acknowledge: false, recovery: true);
        f.Rooms.Accept = false;
        f.Home.Fail = true;
        await Assert.ThrowsAsync<IOException>(() => f.MoveAsync().AsTask());
        Assert.True(f.Session.Connection.IsClosed);
        Assert.Equal(1, f.Accounts.Releases);
        Assert.Equal(1, f.Rooms.Cancels);
        Assert.False(f.Parties.Reserved);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HomeArrivalClaimsBeforePromotionAndInstallsMenuWithoutRoomAdmission(bool acknowledge)
    {
        await using var f = new Fixture(acknowledge, home: true);
        var arrival = f.Arrivals.Next!;
        f.Arrivals.Next = arrival with { Request = arrival.Request with { Kind = MigrationKind.Home, Room = null, HomeNode = "destination" }, Reservation = null };
        await f.MoveAsync();
        Assert.Equal(acknowledge, f.Parties.Reserved);
        if (acknowledge) await f.Handler.AcknowledgeAsync(f.Session, new(), default);
        var player = f.Session.Feature<IPlayerSession>();
        Assert.Equal(SessionPhase.MainMenu, player.Phase);
        Assert.Equal(new MigrationHome("destination", 50), player.MigrationHome);
        Assert.Equal(1, f.Home.Entries);
        Assert.Equal(SessionPhase.MainMenu, f.Home.Scene);
        Assert.Equal(0, f.Rooms.Joins);
        Assert.Equal(0, f.Rooms.Cancels);
        Assert.False(f.Parties.Reserved);
    }

    [Fact]
    public async Task FailedHomeSceneClosesTheArrivalAndReleasesAccountAndSoloGuard()
    {
        await using var f = new Fixture(home: true);
        var arrival = f.Arrivals.Next!;
        f.Arrivals.Next = arrival with { Request = arrival.Request with { Kind = MigrationKind.Home, Room = null, HomeNode = "destination" }, Reservation = null };
        f.Home.Fail = true;
        await f.MoveAsync();
        await Assert.ThrowsAsync<IOException>(() => f.Handler.AcknowledgeAsync(f.Session, new(), default).AsTask());
        Assert.True(f.Session.Connection.IsClosed);
        Assert.False(f.Parties.Reserved);
        Assert.Equal(1, f.Accounts.Releases);
        Assert.False(f.Session.TryFeature<IPlayerSession>(out _));
    }

    [Fact]
    public async Task AMemberArrivingAfterItsLeaderRejoinsTheLeadersParty()
    {
        await using var f = new Fixture(acknowledge: false);
        await f.ArriveAsync(f.Session, 7, 7);
        Assert.Empty(f.Parties.Rejoins);
        await f.ArriveAsync(new Session(2), 8, 7);
        Assert.Equal(new[] { (1L, 2L) }, f.Parties.Rejoins);
    }

    [Fact]
    public async Task AMemberArrivingBeforeItsLeaderWaitsForIt()
    {
        await using var f = new Fixture(acknowledge: false);
        await f.ArriveAsync(f.Session, 7, 8);
        Assert.Empty(f.Parties.Rejoins);
        await f.ArriveAsync(new Session(2), 8, 8);
        Assert.Equal(new[] { (2L, 1L) }, f.Parties.Rejoins);
    }

    [Fact]
    public async Task AWaitingMemberExpiresWithItsArrivalAndASoloArrivalNeverRejoins()
    {
        await using var f = new Fixture(acknowledge: false);
        await f.ArriveAsync(f.Session, 7, 8);
        f.Clock.Advance(10_000 + f.Options.TimeoutMs);
        await f.Handler.SweepAsync(default);
        await f.ArriveAsync(new Session(2), 8, 8);
        Assert.Empty(f.Parties.Rejoins);
        await using var solo = new Fixture(acknowledge: false);
        await solo.MoveAsync();
        Assert.Empty(solo.Parties.Rejoins);
    }

    [Fact]
    public async Task PendingPromotionHoldsSoloReservationUntilTheDestinationSceneIsInstalled()
    {
        await using var f = new Fixture();
        await f.MoveAsync();
        Assert.True(f.Parties.Reserved);
        Assert.Equal(new MigrationHome("source", 50), f.Session.Feature<IPlayerSession>().MigrationHome);
        await f.Handler.AcknowledgeAsync(f.Session, new(), default);
        Assert.False(f.Parties.Reserved);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClaimAndStateInstallationPrecedeContinuationAndNativePromotion(bool acknowledge)
    {
        await using var f = new Fixture(acknowledge);
        await f.MoveAsync();
        // The reply always follows the step that clears the native readiness flag: the promotion on legacy
        // (kr-812 sub_81C550), the completion after the acknowledgement on kr-1256 (sub_9D2FF0 case 10).
        Assert.Equal(acknowledge ? new[] { "consume", "claim", "lookup", "load", "held", "promote" }
            : new[] { "consume", "claim", "lookup", "load", "held", "promote", "reply" }, f.Calls.Take(acknowledge ? 6 : 7));
        Assert.Equal(1, f.Continuation.Calls);
        Assert.Equal(7, f.Session.Feature<IPlayerSession>().Identity.UserNum);
        Assert.Equal((ushort)14, f.Session.Feature<IPlayerSession>().Identity.ColorIndex);
        Assert.Equal(acknowledge ? 0 : 1, f.Rooms.Joins);
        if (acknowledge) Assert.Equal(SessionPhase.Authenticated, f.Session.Feature<IPlayerSession>().Phase);
        if (acknowledge)
        {
            await f.Handler.AcknowledgeAsync(f.Session, new(), default);
            Assert.Equal(new[] { "held", "complete", "reply", "join" }, f.Calls.TakeLast(4));
        }
        Assert.Equal(SessionPhase.Room, f.Session.Feature<IPlayerSession>().Phase);
        Assert.Equal(1, f.Rooms.Joins);
        await f.Handler.DisposeAsync();
        Assert.False(f.Session.Connection.IsClosed);
        Assert.Equal(0, f.Accounts.Releases);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(2147483648L)]
    [InlineData(4294967295L)]
    public async Task UnsupportedIdentityNeverConsumesAnArrivalOrWrapsToSignedAccount(long identity)
    {
        await using var f = new Fixture();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.MoveAsync(identity).AsTask());
        Assert.Empty(f.Calls);
        Assert.Equal(0, f.Continuation.Calls);
        Assert.True(f.Session.Connection.IsClosed);
        Assert.NotNull(f.Arrivals.Next);
    }

    [Fact]
    public async Task UnmatchedArrivalNeverClaimsOrLoadsAnAccount()
    {
        await using var f = new Fixture();
        f.Arrivals.Match = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.MoveAsync().AsTask());
        Assert.Equal(new[] { "consume" }, f.Calls);
        Assert.NotNull(f.Arrivals.Next);
        Assert.Equal(0, f.Rooms.Cancels);
    }

    [Theory]
    [InlineData("guest")]
    [InlineData("home")]
    [InlineData("lease")]
    [InlineData("channel")]
    [InlineData("reservation")]
    public async Task IneligibleArrivalsReleaseTheirSeatWithoutAccountAccess(string reason)
    {
        await using var f = new Fixture();
        var arrival = f.Arrivals.Next!;
        f.Arrivals.Next = reason switch
        {
            "guest" => arrival with { Request = arrival.Request with { Guest = new("Visitor", "Source", 1, [], 0) } },
            "home" => arrival with { Request = arrival.Request with { Kind = MigrationKind.Home } },
            "lease" => arrival with { Request = arrival.Request with { DestinationEpoch = 2 } },
            "channel" => arrival with { Request = arrival.Request with { SourceChannel = 91 } },
            _ => arrival with { Reservation = arrival.Reservation! with { UserNumber = 8 } },
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.MoveAsync().AsTask());
        Assert.Equal(new[] { "consume", "cancel" }, f.Calls);
        Assert.Equal(0, f.Continuation.Calls);
    }

    [Theory]
    [InlineData("claim")]
    [InlineData("lookup")]
    [InlineData("load")]
    [InlineData("hold")]
    [InlineData("reply")]
    [InlineData("promote")]
    [InlineData("join")]
    public async Task FailureNeverContinuesAndReleasesPlayerSeatAndAuthority(string failure)
    {
        await using var f = new Fixture(acknowledge: false);
        f.Accounts.Accept = failure != "claim";
        f.Players.Found = failure != "lookup";
        f.FailLoad = failure == "load";
        f.Accounts.Held = failure != "hold";
        f.FailSend = failure;
        f.Rooms.Accept = failure != "join";
        await Assert.ThrowsAnyAsync<Exception>(() => f.MoveAsync().AsTask());
        Assert.Equal(0, f.Continuation.Calls);
        Assert.False(f.Session.TryFeature<IPlayerSession>(out _));
        Assert.True(f.Session.Connection.IsClosed);
        Assert.Equal(1, f.Accounts.Releases);
        Assert.Equal(1, f.Rooms.Cancels);
        if (failure == "claim") Assert.DoesNotContain("lookup", f.Calls);
        if (failure is "claim" or "lookup") Assert.DoesNotContain("load", f.Calls);
    }

    [Fact]
    public async Task LocalDuplicateDoesNotEvictExistingPlayer()
    {
        await using var f = new Fixture();
        var existing = new Session(9);
        f.PlayersScope.Attach(existing, new(7, "account", "existing", 1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.MoveAsync().AsTask());
        Assert.Same(existing, f.PlayersScope.FindByUserNum(7));
        Assert.False(existing.Connection.IsClosed);
        Assert.Equal(1, f.Accounts.Releases);
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("closed")]
    [InlineData("lease")]
    public async Task PendingPromotionRetiresOnTimeoutDisconnectOrLeaseChange(string reason)
    {
        await using var f = new Fixture();
        await f.MoveAsync();
        if (reason == "timeout") f.Clock.Advance(10001);
        if (reason == "closed") await f.Session.Connection.CloseAsync("test");
        if (reason == "lease") f.Lease.Epoch++;
        await f.Handler.SweepAsync(default);
        Assert.True(f.Session.Connection.IsClosed);
        Assert.Equal(1, f.Rooms.Cancels);
        Assert.Equal(1, f.Accounts.Releases);
        Assert.False(f.Session.TryFeature<IPlayerSession>(out _));
        Assert.Equal(0, f.Rooms.Joins);
    }

    [Fact]
    public async Task DuplicateAcknowledgementCannotJoinTwice()
    {
        await using var f = new Fixture();
        await f.MoveAsync();
        await f.Handler.AcknowledgeAsync(f.Session, new(), default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Handler.AcknowledgeAsync(f.Session, new(), default).AsTask());
        Assert.Equal(1, f.Rooms.Joins);
        Assert.Single(f.Calls, call => call == "complete");
    }

    [Fact]
    public async Task LostAuthorityAtAcknowledgementPreventsRoomInstallation()
    {
        await using var f = new Fixture();
        await f.MoveAsync();
        f.Accounts.Held = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Handler.AcknowledgeAsync(f.Session, new(), default).AsTask());
        Assert.DoesNotContain("complete", f.Calls);
        Assert.Equal(0, f.Rooms.Joins);
        Assert.Equal(1, f.Accounts.Releases);
    }

    [Fact]
    public async Task FailedCancellationRetainsCapacityUntilRetrySucceeds()
    {
        await using var f = new Fixture();
        f.Options.MaxMigrations = 1;
        await f.MoveAsync();
        f.Rooms.FailCancel = true;
        f.Clock.Advance(10001);
        await f.Handler.SweepAsync(default);
        var next = new Session(2);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Handler.MoveAsync(next, new(7, 0), default).AsTask());
        Assert.Equal(1, f.Accounts.Releases);
        Assert.Equal(1, f.Rooms.Cancels);
        f.Rooms.FailCancel = false;
        await f.Handler.SweepAsync(default);
        Assert.Equal(2, f.Rooms.Cancels);
        Assert.Equal(1, f.Accounts.Releases);
    }

    [Fact]
    public async Task CancellationAfterClaimReleasesAuthorityWithoutContinuation()
    {
        await using var f = new Fixture();
        using var canceled = new CancellationTokenSource();
        f.AfterLoad = canceled.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Handler.MoveAsync(f.Session, new(7, 0), canceled.Token).AsTask());
        Assert.Equal(0, f.Continuation.Calls);
        Assert.Equal(1, f.Accounts.Releases);
        Assert.Equal(1, f.Rooms.Cancels);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CoordinatorActivationRequiresRecoveryAndUnloadRetiresPendingPromotion(bool missingRecovery)
    {
        await using var f = new Fixture();
        var services = new ServiceRegistry();
        var events = new EventBus(NullLogger.Instance);
        using var player = services.Register<IRetainedPlayerAdmission>(f.Admission);
        using var arrival = services.Register<IMigrationArrivals>(f.Arrivals);
        using var lease = services.Register<INodeLeaseSource>(f.Lease);
        using var handoff = services.Register<IAccountSessionHandoff>(f.Accounts);
        using var accounts = services.Register<IAccountSessionAdmission>(f.Accounts);
        using var rooms = services.RegisterMany<IRoomAdmissionDirectory>(f.Rooms);
        using var recovery = missingRecovery ? null : services.RegisterMany<IMigrationRoomRecovery>(f.Home);
        using var move = services.RegisterMany<IInboundMessageBinding<MoveIntoRequested>>(new Binding<MoveIntoRequested>());
        using var ack = services.RegisterMany<IInboundMessageBinding<PendingPromotionAcknowledged>>(new Binding<PendingPromotionAcknowledged>());
        using var reply = services.RegisterMany<IMessageSender<MoveIntoReplied>>(new Sender<MoveIntoReplied>(() => { }));
        using var promote = services.RegisterMany<IMessageSender<PendingConnectionPromoted>>(new Sender<PendingConnectionPromoted>(() => { }));
        using var complete = services.RegisterMany<IMessageSender<PendingPromotionCompleted>>(new Sender<PendingPromotionCompleted>(() => { }));
        var routerContext = new PluginContext(new PluginRow { Id = "router", Plugin = "core.message-router", Source = "test" },
            services, events, NullLogger.Instance);
        var context = new PluginContext(new PluginRow { Id = "migration", Plugin = "cluster.migration-admission", Source = "test",
            Config = new Dictionary<string, object?> { ["channels"] = new object[] { 50 } } }, services, events, NullLogger.Instance);
        try
        {
            await new MessageRouterPlugin().ActivateAsync(routerContext, default);
            if (missingRecovery)
            {
                var failure = await Assert.ThrowsAsync<ConfigException>(() => new MigrationAdmissionPlugin().ActivateAsync(context, default).AsTask());
                Assert.Contains("room recovery", failure.Message);
                return;
            }
            await new MigrationAdmissionPlugin().ActivateAsync(context, default);
            var router = services.Get<IMessageRouter>();
            Assert.True(await router.DispatchAsync("kr-1256/game", f.Session, new MoveIntoRequested(7, 0), default));
            Assert.Equal(1, f.Continuation.Calls);
            Assert.Equal(0, f.Rooms.Joins);
            await context.UnwindAsync();
            Assert.True(f.Session.Connection.IsClosed);
            Assert.Equal(1, f.Rooms.Cancels);
            Assert.Equal(1, f.Accounts.Releases);
            Assert.False(await router.DispatchAsync("kr-1256/game", new Session(2), new MoveIntoRequested(7, 0), default));
        }
        finally { await context.UnwindAsync(); await routerContext.UnwindAsync(); }
    }

    private sealed class Binding<T> : IInboundMessageBinding<T> where T : IMessage
    {
        public string Protocol => "kr-1256/game";
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetainedInstallationChecksAuthorityBeforeInventoryOrWalletWrites(bool held)
    {
        var calls = new List<string>();
        var accounts = new Accounts(calls) { Held = held };
        var session = new Session(1);
        await accounts.TryClaimAsync(session, 7, new string('a', 32), default);
        using var scope = new PlayerSessionScope();
        var table = new MemoryPlayerTable(8);
        var inventory = new MemoryInventoryRepository(table);
        var wallet = new MemoryPlayerWalletRepository(table);
        var retained = new RetainedPlayerAdmission("kr-1256/game", new Players(calls), scope,
            new LoginInventoryResolver(inventory, true), new LoginMoneyResolver(wallet, 17, 4));
        if (!held)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => retained.InstallAsync(session, 7, default).AsTask());
            Assert.Empty(await inventory.ListSlotsAsync(7, InventoryBag.CharacterBag, InventoryLimits.MaxInventorySlotsPerBag, default));
            Assert.DoesNotContain("lookup", calls);
            Assert.False(session.TryFeature<IPlayerSession>(out _));
        }
        else
        {
            var player = await retained.InstallAsync(session, 7, default);
            Assert.Equal(new[] { "claim", "held", "lookup", "held" }, calls);
            Assert.Equal(SessionPhase.Authenticated, player.Phase);
            Assert.Equal(1, player.LoginConnectionId);
            Assert.NotEmpty(await inventory.ListSlotsAsync(7, InventoryBag.CharacterBag, InventoryLimits.MaxInventorySlotsPerBag, default));
            Assert.Equal(17, (await wallet.GetOrCreateAsync(new(7, 0, 0), default)).Lucci);
            retained.Remove(session);
            Assert.False(session.TryFeature<IPlayerSession>(out _));
        }
    }

    [Fact]
    public void MigrationOptionsRequireExplicitChannelsAndOwnershipBackedLobby()
    {
        Assert.False(new LobbyOptions().NativeMigration);
        Assert.NotEmpty(new MigrationAdmissionOptions().Validate());
        Assert.NotEmpty(new MigrationAdmissionOptions { Channels = [50, 50] }.Validate());
        Assert.NotEmpty(new MigrationAdmissionOptions { Channels = [256] }.Validate());
        Assert.Empty(new MigrationAdmissionOptions { Channels = [50, 60, 80] }.Validate());
        Assert.NotEmpty(new LobbyOptions { NativeMigration = true }.Validate());
        Assert.Empty(new LobbyOptions { NativeMigration = true, RequireAccountOwnership = true,
            DuplicateLogin = "reject_new", InventorySource = "mysql", MoneySource = "mysql" }.Validate());
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly List<string> Calls = [];
        internal readonly MigrationAdmissionOptions Options = new() { Channels = [50] };
        internal readonly PlayerSessionScope PlayersScope = new();
        internal readonly Session Session = new(1);
        internal readonly Clock Clock = new();
        internal readonly Lease Lease = new();
        internal readonly Continuation Continuation = new();
        internal readonly IDisposable ContinuationScope;
        internal readonly HomeAdmission Home = new();
        internal readonly Parties Parties = new();
        internal readonly Accounts Accounts;
        internal readonly Players Players;
        internal readonly Arrivals Arrivals;
        internal readonly Rooms Rooms;
        internal readonly MigrationAdmission Handler;
        internal readonly IRetainedPlayerAdmission Admission;
        internal bool FailLoad;
        internal string? FailSend;
        internal Action? AfterLoad;

        internal Fixture(bool acknowledge = true, bool home = false, bool recovery = false)
        {
            Accounts = new(Calls);
            Players = new(Calls);
            Rooms = new(Calls);
            var instance = Guid.NewGuid();
            Arrivals = new(Calls)
            {
                Next = new(new(new string('a', 32), MigrationKind.Room, "source", "destination", 1, "kr-1256", 7,
                    IPAddress.Loopback, new(1, instance, 7, "")) { SourceChannel = 50, HomeNode = "source" },
                    new(Guid.NewGuid(), 1, instance, 7), TimeSpan.FromSeconds(10)) { ListenerId = "kr-1256.tcp" },
            };
            ContinuationScope = Session.RegisterFeature<IInitialHandshakeContinuation>(Continuation);
            Admission = new Retained(this);
            Handler = new(Options, Admission, Arrivals, Lease, Accounts, Accounts, Rooms,
                new Sender<MoveIntoReplied>(() => Send("reply")), new Sender<PendingConnectionPromoted>(() => Send("promote")),
                acknowledge ? new Sender<PendingPromotionCompleted>(() => Send("complete")) : null,
                null, Clock, NullLogger.Instance, home ? Home : null, Parties, recovery ? Home : null);
        }

        private sealed class Retained(Fixture f) : IRetainedPlayerAdmission
        {
            public string Protocol => "kr-1256/game";
            public async ValueTask<IPlayerSession> InstallAsync(ISession session, long userNum, CancellationToken ct)
            {
                Assert.True(f.Accounts.Claimed);
                var player = await f.Players.FindByUserNumAsync(userNum, ct);
                if (player is null) throw new InvalidOperationException("missing identity");
                f.Calls.Add("load");
                if (f.FailLoad) throw new IOException("state load failed");
                f.AfterLoad?.Invoke();
                ct.ThrowIfCancellationRequested();
                if (!await f.Accounts.IsHeldAsync(ct)) throw new InvalidOperationException("lost authority");
                return f.PlayersScope.Attach(session, new(checked((int)userNum), player.Username, player.Nickname, player.Level) { ColorIndex = player.ColorIndex });
            }
            public void Remove(ISession session) => f.PlayersScope.Remove(session.Connection.Id);
        }

        private void Send(string kind)
        {
            Assert.True(Session.TryFeature<IPlayerSession>(out _));
            Calls.Add(kind);
            if (FailSend == kind) throw new IOException("send failed");
        }

        internal ValueTask MoveAsync(long identity = 7) => Handler.MoveAsync(Session, new(identity, 0), default);

        // A legacy arrival (no promotion acknowledgement) of one party traveller on its own connection.
        internal async ValueTask ArriveAsync(Session session, int userNum, int leader)
        {
            var instance = Guid.NewGuid();
            Arrivals.Next = new(new(userNum.ToString("x32"), MigrationKind.Room, "source", "destination", 1, "kr-1256", userNum,
                IPAddress.Loopback, new(1, instance, userNum, "")) { SourceChannel = 50, HomeNode = "source", PartyLeader = leader },
                new(Guid.NewGuid(), 1, instance, userNum), TimeSpan.FromSeconds(10)) { ListenerId = "kr-1256.tcp" };
            if (!session.TryFeature<IInitialHandshakeContinuation>(out _))
                session.RegisterFeature<IInitialHandshakeContinuation>(new Continuation());
            await Handler.MoveAsync(session, new(userNum, 0), default);
        }
        public async ValueTask DisposeAsync() { await Handler.DisposeAsync(); PlayersScope.Dispose(); }
    }

    private sealed class HomeAdmission : IMigrationHomeAdmission, IMigrationRoomRecovery
    {
        public string ClientBuild => "kr-1256";
        internal int Entries;
        internal int Returns;
        internal Func<CancellationToken, Task>? BeforeReturn;
        internal bool Fail;
        internal SessionPhase Scene;
        public ValueTask<bool> InstallMenuAsync(ISession session, CancellationToken cancellationToken) => EnterAsync(session, SessionPhase.MainMenu, cancellationToken);
        public async ValueTask ReturnHomeAsync(ISession session, long generation, CancellationToken cancellationToken)
        {
            Assert.Equal(generation, session.Feature<IPlayerSession>().Generation);
            Returns++;
            if (BeforeReturn is not null) await BeforeReturn(cancellationToken);
        }
        public ValueTask<bool> EnterAsync(ISession session, SessionPhase scene, CancellationToken cancellationToken)
        {
            if (Fail) throw new IOException("menu install failed");
            Entries++;
            Scene = scene;
            var player = session.Feature<IPlayerSession>();
            player.MoveTo(SessionPhase.MainMenu);
            if (scene == SessionPhase.Plaza) player.MoveTo(scene);
            return ValueTask.FromResult(true);
        }
    }

    private sealed class Parties : IPartyDirectory
    {
        internal bool Reserved;
        internal readonly List<(long Leader, long Member)> Rejoins = [];
        public ValueTask<bool> RejoinAsync(ISession leader, ISession member, CancellationToken cancellationToken)
        {
            Assert.False(Reserved);
            Rejoins.Add((leader.Id, member.Id));
            return ValueTask.FromResult(true);
        }
        public bool SupportsSoloReservation => true;
        public PartySnapshot? PartyOf(int userNum) => null;
        public IDisposable? TryReserveSolo(int userNum)
        {
            if (Reserved) return null;
            Reserved = true;
            return new Release(this);
        }
        private sealed class Release(Parties parties) : IDisposable { public void Dispose() => parties.Reserved = false; }
    }

    private sealed class Continuation : IInitialHandshakeContinuation
    {
        internal int Calls;
        public void ContinueWithoutVersionVerification() => Calls++;
    }

    private sealed class Accounts(List<string> calls) : IAccountSessionHandoff, IAccountSessionAdmission, IAccountSession
    {
        internal bool Accept = true, Held = true, Claimed;
        internal int Releases;
        private IDisposable? _feature;
        public long UserNum => 7;
        public ValueTask<bool> TryClaimAsync(ISession session, long userNum, string migrationId, CancellationToken ct)
        {
            calls.Add("claim");
            Assert.True(userNum is 7 or 8);
            Assert.False(session.TryFeature<IPlayerSession>(out _));
            if (Accept) { Claimed = true; _feature = session.RegisterFeature<IAccountSession>(this); }
            return ValueTask.FromResult(Accept);
        }
        public ValueTask<bool> IsHeldAsync(CancellationToken ct) { calls.Add("held"); return ValueTask.FromResult(Held); }
        public ValueTask ReleaseAsync(ISession session)
        {
            Releases++; calls.Add("release"); _feature?.Dispose(); Claimed = false;
            return ValueTask.CompletedTask;
        }
        public ValueTask<bool> TryAdmitAsync(ISession session, long userNum, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask HandOffAsync(ISession session, AccountHandoff handoff, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<bool> TryWithdrawAsync(ISession session, string migrationId, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class Players(List<string> calls) : IPlayerRepository
    {
        internal bool Found = true;
        public ValueTask<PlayerRecord?> FindByUserNumAsync(long userNum, CancellationToken ct)
        {
            calls.Add("lookup");
            return ValueTask.FromResult(Found ? new PlayerRecord(userNum, "account", "Stored fighter", 3) { ColorIndex = 14 } : null);
        }
        public ValueTask<PlayerRecord?> FindByUsernameAsync(string username, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<bool> HasCreatedCharacterAsync(long userNum, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<PlayerRecord> CreateAsync(string username, string nickname, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<bool> NicknameExistsAsync(string nickname, CancellationToken ct, string? exceptUsername = null) => throw new NotSupportedException();
        public ValueTask<PlayerRecord?> CreateCharacterAsync(string username, string nickname, StarterKit kit, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<bool> NeedsTutorialAsync(long userNum, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask CompleteTutorialAsync(long userNum, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class Arrivals(List<string> calls) : IMigrationArrivals
    {
        internal MigrationArrival? Next;
        internal bool Match = true;
        public MigrationArrival? TryConsume(string build, long userNum, IConnection connection)
        {
            calls.Add("consume");
            if (!Match) return null;
            var result = Next; Next = null; return result;
        }
        public void Expect(MigrationArrival arrival) => throw new NotSupportedException();
        public MigrationArrival? Cancel(string migrationId) => throw new NotSupportedException();
    }

    private sealed class Rooms(List<string> calls) : IRoomAdmissionDirectory
    {
        internal int Joins, Cancels;
        internal bool Accept = true, FailCancel;
        public string ClientBuild => "kr-1256";
        public ValueTask<bool> JoinAsync(ISession session, RoomAdmissionReservation reservation, CancellationToken ct)
        {
            calls.Add("join"); Joins++;
            var player = session.Feature<IPlayerSession>();
            Assert.Equal(SessionPhase.MainMenu, player.Phase);
            Assert.Equal(reservation.UserNumber, player.Identity.UserNum);
            if (Accept) player.MoveTo(SessionPhase.Room);
            return ValueTask.FromResult(Accept);
        }
        public ValueTask CancelAsync(RoomAdmissionReservation reservation, CancellationToken ct)
        {
            calls.Add("cancel"); Cancels++;
            if (FailCancel) throw new IOException("seat store unavailable");
            return ValueTask.CompletedTask;
        }
        public ValueTask<RoomAdmissionPreparation> PrepareAsync(RoomAdmissionRequest request, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class Lease : INodeLeaseSource
    {
        internal long Epoch = 1;
        public string NodeId => "destination";
        public string DirectoryUrl => "https://directory.invalid";
        public NodeLeaseCredentials? Current => new("private", Epoch);
    }

    private sealed class Clock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => _timestamp;
        internal void Advance(long milliseconds) => _timestamp += milliseconds;
    }

    private sealed class Sender<T>(Action send) : IMessageSender<T> where T : IMessage
    {
        public string Protocol => "kr-1256/game";
        public ValueTask SendAsync(ISession session, T message, CancellationToken ct) { send(); return ValueTask.CompletedTask; }
    }

    private sealed class Session(long id) : ISessionWithFeatures
    {
        public ISessionFeatures Features { get; } = new TestSessionFeatures();
        public long Id => id;
        public IConnection Connection { get; } = new Connection(id);
        public ClientProfile Profile { get; } = new("kr-1256", "kr", 1256, 0, 0, "test");
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class Connection(long id) : IConnection
    {
        public long Id => id;
        public EndPoint RemoteEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 1);
        public PipeReader Input => throw new NotSupportedException();
        public PipeWriter Output => throw new NotSupportedException();
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public bool IsClosed { get; private set; }
        public ValueTask CloseAsync(string reason) { IsClosed = true; return ValueTask.CompletedTask; }
    }
}
