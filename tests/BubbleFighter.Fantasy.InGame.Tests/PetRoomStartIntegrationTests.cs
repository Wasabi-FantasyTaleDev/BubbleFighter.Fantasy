using System.Text;
using System.Threading.Channels;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.BanPick;
using BubbleFighter.Fantasy.Contracts.BanPick;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.Contracts.Companion;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.InGame.Handlers;
using BubbleFighter.Fantasy.InGame.Sessions;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Pets;
using BubbleFighter.Fantasy.RoomCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>Exercises pet mutation admission against the real room flow, policy host, executor and ban-pick room plugin.</summary>
public sealed class PetRoomStartIntegrationTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);
    private const string ManualMap = "rel_Village07_Easy02";
    private const string AutomaticMap = "rel_zombie05_Tear";
    private const int PetItem = 301001;

    /// <summary>Appearance publication leaves both manual and native automatic starts held until the owning mutation ends.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ASeatedPetMutationHoldsTheActualStartThroughAppearancePublication(bool automatic)
    {
        await using var rig = new IntegrationRig(automatic);
        var (host, member, room) = await rig.CreateRoomAsync(automatic);
        var published = Signal();
        var finishPublication = Signal();
        var mutation = PublishAsync();
        try
        {
            await published.Task.WaitAsync(Limit);
            await rig.StartAsync(host, automatic);
            AssertWaiting(rig, room, host, member);
            Assert.Equal(0, rig.ReplayCount);
            Assert.Equal(1, rig.Notices);
            Assert.False(rig.Policy.TryBegin(member.Id));
            Assert.Equal(new[] { PetItem }, room.FindByGame(member.Id)!.EquippedItemIds);

            finishPublication.TrySetResult();
            await mutation.WaitAsync(Limit);
            Assert.Equal(host.Id, await rig.NextReplayAsync());
            Assert.Equal(RoomPhase.Starting, room.Phase);
            foreach (var recipient in new[] { host, member })
            {
                var start = Assert.Single(rig.FlowRig.Starting.To(recipient));
                Assert.Equal(automatic ? AutomaticMap : ManualMap, start.MapKey);
                Assert.Contains(start.Room.Seats, seat => seat.EquippedItemIds.Contains(PetItem));
            }
            Assert.False(await rig.Rooms.ResumeHeldStartAsync(room.InstanceId, host.Id, default));
            Assert.Equal(1, rig.ReplayCount);
        }
        finally
        {
            finishPublication.TrySetResult();
            await mutation.WaitAsync(Limit);
        }

        async Task PublishAsync()
        {
            Assert.True(rig.Policy.TryBegin(member.Id));
            try
            {
                await rig.FlowRig.Flow.OnEquipmentSelectionCommittedAsync(new(member, checked((int)member.Id), 300001, [PetItem]), default);
                published.TrySetResult();
                await finishPublication.Task.WaitAsync(Limit);
            }
            finally { rig.Policy.End(member.Id); }
        }
    }

    /// <summary>The real room's transition out of waiting refuses every later pet write without leaking in-flight capacity.</summary>
    [Fact]
    public async Task AStartedRoomRefusesPetWritesAndLeavesCapacityForAnotherRoom()
    {
        await using var rig = new IntegrationRig(maxInFlight: 1);
        var (host, member, room) = await rig.CreateRoomAsync();
        await rig.StartAsync(host);
        Assert.Equal(RoomPhase.Starting, room.Phase);
        Assert.False(rig.Policy.TryBegin(host.Id));
        Assert.False(rig.Policy.TryBegin(member.Id));

        var waiting = RoomFlowTests.Rig.Player(953, "waiting");
        await rig.FlowRig.CreateAsync(waiting);
        Assert.True(rig.Policy.TryBegin(waiting.Id));
        rig.Policy.End(waiting.Id);
        Assert.Single(rig.FlowRig.Starting.To(host));
        Assert.Equal(0, rig.ReplayCount);
    }

    /// <summary>A failed or cancelled publication keeps the real start held until its cleanup releases the mutation.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedPublicationCleanupResumesTheHeldRoom(bool cancelled)
    {
        await using var rig = new IntegrationRig();
        var (host, member, room) = await rig.CreateRoomAsync();
        var failPublication = Signal();
        var cleanupEntered = Signal();
        var finishCleanup = Signal();
        var mutation = FailAsync();
        try
        {
            await rig.StartAsync(host);
            failPublication.TrySetResult();
            await cleanupEntered.Task.WaitAsync(Limit);
            AssertWaiting(rig, room, host, member);
            Assert.Equal(0, rig.ReplayCount);
            Assert.False(rig.Policy.TryBegin(member.Id));

            finishCleanup.TrySetResult();
            var error = await Record.ExceptionAsync(() => mutation.WaitAsync(Limit));
            if (cancelled) Assert.IsAssignableFrom<OperationCanceledException>(error);
            else Assert.IsType<IOException>(error);
            Assert.Equal(host.Id, await rig.NextReplayAsync());
            Assert.Equal(RoomPhase.Starting, room.Phase);
            Assert.Single(rig.FlowRig.Starting.To(host));
        }
        finally
        {
            failPublication.TrySetResult();
            finishCleanup.TrySetResult();
            await Record.ExceptionAsync(() => mutation.WaitAsync(Limit));
        }

        async Task FailAsync()
        {
            Assert.True(rig.Policy.TryBegin(member.Id));
            try
            {
                await failPublication.Task.WaitAsync(Limit);
                if (!cancelled) rig.FlowRig.SeatUpdated.Failure = new IOException("Pet appearance publication failed.");
                await rig.FlowRig.Flow.OnEquipmentSelectionCommittedAsync(new(member, checked((int)member.Id), 300001, [PetItem]),
                    cancelled ? new CancellationToken(true) : default);
            }
            finally
            {
                cleanupEntered.TrySetResult();
                try { await finishCleanup.Task.WaitAsync(Limit); }
                finally
                {
                    rig.FlowRig.SeatUpdated.Failure = null;
                    rig.Policy.End(member.Id);
                }
            }
        }
    }

    /// <summary>A departing host's old start stays invalid even if that session rejoins and regains the same room before publication ends.</summary>
    [Fact]
    public async Task HostDepartureInvalidatesTheHeldRequestAcrossRejoinAndHostRestoration()
    {
        await using var rig = new IntegrationRig();
        var (host, member, room) = await rig.CreateRoomAsync();
        Assert.True(rig.Policy.TryBegin(member.Id));
        await rig.StartAsync(host);
        await rig.FlowRig.Flow.HandleLeaveAsync(host, new(), default);
        Assert.Equal(member.Id, Assert.Single(rig.Rooms.Find(member.Id)!.Seats, seat => seat.Host).SessionId);
        Assert.False(rig.Policy.TryBegin(member.Id));
        await rig.FlowRig.Flow.HandleJoinAsync(host, new(room.Id, ""), default);
        await rig.FlowRig.Flow.HandleMasterChangeAsync(member, new("host"), default);
        Assert.Equal(host.Id, Assert.Single(rig.Rooms.Find(host.Id)!.Seats, seat => seat.Host).SessionId);

        var sentinel = RoomFlowTests.Rig.Player(953, "sentinel");
        await rig.FlowRig.CreateAsync(sentinel);
        Assert.True(rig.Policy.TryBegin(sentinel.Id));
        await rig.StartAsync(sentinel);
        rig.Policy.End(member.Id);
        rig.Policy.End(sentinel.Id);
        Assert.Equal(sentinel.Id, await rig.NextReplayAsync());
        AssertWaiting(rig, room, host, member);
        Assert.Equal(1, rig.ReplayCount);

        await rig.StartAsync(host);
        Assert.Equal(RoomPhase.Starting, room.Phase);
        Assert.Single(rig.FlowRig.Starting.To(host));
    }

    /// <summary>The worker can resume a real room while its construction callback is still active on another executor.</summary>
    [Fact]
    public async Task DetachedWorkerDoesNotInheritTheConstructingRoomExecutor()
    {
        await using var rig = new IntegrationRig(deferPolicy: true);
        var (host, member, room) = await rig.CreateRoomAsync();
        await using var origin = new RoomExecutor(1);
        var constructed = Signal();
        var finishOrigin = Signal();
        var constructing = origin.ExecuteAsync(async ct =>
        {
            Assert.Throws<InvalidOperationException>(() => { _ = room.Executor.ExecuteAsync(_ => ValueTask.CompletedTask); });
            rig.AttachPolicy();
            constructed.TrySetResult();
            await finishOrigin.Task.WaitAsync(ct);
        });
        try
        {
            await constructed.Task.WaitAsync(Limit);
            Assert.True(rig.Policy.TryBegin(member.Id));
            await rig.StartAsync(host);
            await room.Executor.ExecuteAsync(_ =>
            {
                rig.Policy.End(member.Id);
                return ValueTask.CompletedTask;
            }).WaitAsync(Limit);
            Assert.Equal(host.Id, await rig.NextReplayAsync());
            Assert.False(constructing.IsCompleted);
            Assert.False(rig.Policy.Completion.IsCompleted);
            Assert.Equal(RoomPhase.Starting, room.Phase);
            Assert.Single(rig.FlowRig.Starting.To(host));
        }
        finally
        {
            finishOrigin.TrySetResult();
            await constructing.WaitAsync(Limit);
        }
    }

    /// <summary>The actual ban-pick room plugin retains a manual block after pet release and exempts native automatic starts.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PetReleasePreservesTheBanPickPluginsManualAndAutomaticRules(bool automatic)
    {
        await using var rig = new IntegrationRig(automatic);
        var store = new PolicyStore(new([], [new("pet-block", null, null, BanPickScope.Everywhere,
            [new("pet", BanPickRuleTypes.Block, [PetItem], [])])]));
        await rig.ActivateBanPickAsync(store);
        var (host, member, room) = await rig.CreateRoomAsync(automatic);
        await rig.FlowRig.Flow.OnEquipmentSelectionCommittedAsync(new(member, checked((int)member.Id), 300001, [PetItem]), default);
        Assert.True(rig.Policy.TryBegin(member.Id));
        await rig.StartAsync(host, automatic);
        AssertWaiting(rig, room, host, member);
        Assert.Equal(0, rig.ReplayCount);
        Assert.Equal(1, rig.Notices);

        rig.Policy.End(member.Id);
        Assert.Equal(host.Id, await rig.NextReplayAsync());
        if (automatic)
        {
            Assert.Equal(RoomPhase.Starting, room.Phase);
            Assert.Equal(AutomaticMap, Assert.Single(rig.FlowRig.Starting.To(host)).MapKey);
            Assert.DoesNotContain(rig.FlowRig.Notices.To(host), notice => notice.Text.StartsWith("banpick.start.blocked", StringComparison.Ordinal));
        }
        else
        {
            AssertWaiting(rig, room, host, member);
            Assert.Equal(2, rig.FlowRig.Notices.To(host).Count(notice => notice.Text.StartsWith("banpick.start.blocked", StringComparison.Ordinal)));
            rig.Policy.End(member.Id);
            await rig.StopPolicyAsync();
            Assert.Equal(1, rig.ReplayCount);
            store.Replace(BanPickPolicy.Empty);
            await rig.StartAsync(host);
            Assert.Single(rig.FlowRig.Starting.To(host));
        }
    }

    /// <summary>Finishing pet publication cannot stand in for the host accepting the real ban-pick plugin's confirmation.</summary>
    [Fact]
    public async Task PetReplayDoesNotReplaceTheHostsBanPickConfirmation()
    {
        await using var rig = new IntegrationRig();
        var scope = await rig.ActivateBanPickAsync(ConfirmPetPolicy());
        var (host, member, room) = await rig.CreateRoomAsync();
        await rig.FlowRig.Flow.OnEquipmentSelectionCommittedAsync(new(member, checked((int)member.Id), 300001, [PetItem]), default);
        Assert.True(rig.Policy.TryBegin(member.Id));
        await rig.StartAsync(host);
        AssertWaiting(rig, room, host, member);

        rig.Policy.End(member.Id);
        Assert.Equal(host.Id, await rig.NextReplayAsync());
        AssertWaiting(rig, room, host, member);

        await scope.RoomCommandAsync(host, "en", ["start"], default);
        Assert.Equal(host.Id, await rig.NextReplayAsync());
        Assert.Equal(RoomPhase.Starting, room.Phase);
        Assert.Single(rig.FlowRig.Starting.To(host));
        Assert.Single(rig.FlowRig.Starting.To(member));
    }

    /// <summary>A second host click authorizes the held selection once, even when pet publication still prevents the room from starting.</summary>
    [Fact]
    public async Task AHostConfirmationWhilePetPublicationIsBusyRemainsValidForItsReplay()
    {
        await using var rig = new IntegrationRig();
        await rig.ActivateBanPickAsync(ConfirmPetPolicy());
        var (host, member, room) = await rig.CreateRoomAsync();
        await rig.FlowRig.Flow.OnEquipmentSelectionCommittedAsync(new(member, checked((int)member.Id), 300001, [PetItem]), default);
        Assert.True(rig.Policy.TryBegin(member.Id));
        await rig.StartAsync(host);
        await rig.StartAsync(host);
        AssertWaiting(rig, room, host, member);
        Assert.Equal(0, rig.ReplayCount);

        rig.Policy.End(member.Id);
        Assert.Equal(host.Id, await rig.NextReplayAsync());
        Assert.Equal(RoomPhase.Starting, room.Phase);
        Assert.Single(rig.FlowRig.Starting.To(host));
        Assert.Single(rig.FlowRig.Starting.To(member));
        Assert.Equal(1, rig.Notices);
    }

    private static PolicyStore ConfirmPetPolicy() => new(new([], [new("pet-confirm", null, null, BanPickScope.Everywhere,
        [new("pet", BanPickRuleTypes.Confirm, [PetItem], [])])]));

    private static void AssertWaiting(IntegrationRig rig, PlayRoom room, params ISession[] members)
    {
        Assert.Equal(RoomPhase.Waiting, room.Phase);
        foreach (var member in members) Assert.Empty(rig.FlowRig.Starting.To(member));
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class IntegrationRig : IAsyncDisposable
    {
        private readonly int _maxInFlight;
        private readonly Channel<long> _replayed = Channel.CreateBounded<long>(8);
        private readonly List<PluginContext> _contexts = [];
        private IDisposable? _registration;
        private int _notices;
        private int _replays;

        internal IntegrationRig(bool automatic = false, int maxInFlight = 4, bool deferPolicy = false)
        {
            _maxInFlight = maxInFlight;
            FlowRig = new(options: automatic ? new InGameOptions { AutoStartModeIds = [16] } : null);
            Rooms = new(FlowRig.Registry, "test/game", "kr-812");
            FlowRig.Flow.PolicyHost = Rooms;
            Rooms.Start = async (room, session, request, memberStart, ct) =>
            {
                await FlowRig.Flow.ResumeHeldStartAsync(room, session, request, memberStart, ct);
                Interlocked.Increment(ref _replays);
                if (!_replayed.Writer.TryWrite(session.Id)) throw new InvalidOperationException("The test replay channel is full.");
            };
            if (!deferPolicy) AttachPolicy();
        }

        internal RoomFlowTests.Rig FlowRig { get; }
        internal RoomPolicyHost Rooms { get; }
        internal PetRoomMutationPolicy Policy { get; private set; } = null!;
        internal int Notices => Volatile.Read(ref _notices);
        internal int ReplayCount => Volatile.Read(ref _replays);

        internal void AttachPolicy()
        {
            Policy = new(Rooms, _maxInFlight, 4, (_, _, _) =>
            {
                Interlocked.Increment(ref _notices);
                return ValueTask.CompletedTask;
            });
            _registration = Rooms.Register(Policy);
        }

        internal async Task<(RecordingSession Host, RecordingSession Member, PlayRoom Room)> CreateRoomAsync(bool automatic = false)
        {
            var host = RoomFlowTests.Rig.Player(951, "host");
            var member = RoomFlowTests.Rig.Player(952, "member");
            if (automatic)
                await FlowRig.Flow.HandleCreateAsync(host, new("boss", "", 16, "Random"), default);
            else await FlowRig.CreateAsync(host);
            var room = FlowRig.Registry.TryGet(host.Id)!;
            await FlowRig.Flow.HandleJoinAsync(member, new(room.Id, ""), default);
            return (host, member, room);
        }

        internal async Task StartAsync(ISession host, bool automatic = false)
        {
            if (automatic) await FlowRig.Flow.HandleChatAsync(host, new(0, "!start"), default);
            await FlowRig.Flow.HandleStartAsync(host, new(automatic ? AutomaticMap : ManualMap) { Force = !automatic }, default);
        }

        internal async Task<long> NextReplayAsync()
        {
            var replay = _replayed.Reader.ReadAsync().AsTask();
            if (await Task.WhenAny(replay, Policy.Completion).WaitAsync(Limit) == Policy.Completion)
                await Policy.Completion;
            return await replay.WaitAsync(Limit);
        }

        internal async Task<IBanPickScope> ActivateBanPickAsync(PolicyStore store)
        {
            var services = new ServiceRegistry();
            var events = new EventBus(NullLogger.Instance);
            var root = new PluginContext(new() { Id = "pet-room-fixture", Plugin = "test", Source = "test" },
                services, events, NullLogger.Instance);
            _contexts.Add(root);
            var hub = new PolicyHub(store);
            root.Effect(services.Register<IBanPickHub>(hub));
            root.Effect(services.Register<IClientDataStore>(new ItemData()));
            root.Effect(services.Register<IGameText>(new RoomFlowTests.TestTexts()));
            root.Effect(services.RegisterMany<IRoomPolicyHost>(Rooms));
            root.Effect(services.RegisterMany<IMessageSender<ServerNotice>>(FlowRig.Notices));
            var context = new PluginContext(new()
            {
                Id = "banpick-room", Plugin = "banpick.room", Source = "test",
                Config = new Dictionary<string, object?> { ["protocol"] = Rooms.Protocol },
            }, services, events, NullLogger.Instance);
            _contexts.Add(context);
            await new BanPickRoomPlugin().ActivateAsync(context, default);
            return hub.Scope;
        }

        internal async Task StopPolicyAsync()
        {
            Interlocked.Exchange(ref _registration, null)?.Dispose();
            if (Policy is not null) await Policy.DisposeAsync().AsTask().WaitAsync(Limit);
        }

        /// <summary>Stops pet replay before unmounting the other policies and draining every room executor.</summary>
        public async ValueTask DisposeAsync()
        {
            try { await StopPolicyAsync(); }
            finally
            {
                try
                {
                    for (var index = _contexts.Count - 1; index >= 0; index--)
                        await _contexts[index].UnwindAsync().AsTask().WaitAsync(Limit);
                }
                finally { await FlowRig.Registry.StopAllAsync().WaitAsync(Limit); }
            }
        }
    }

    private sealed class PolicyStore(BanPickPolicy policy) : IBanPickPolicyStore
    {
        private BanPickPolicyState _current = new(1, policy);

        /// <summary>Returns the test's current server policy to the real ban-pick evaluator.</summary>
        public BanPickPolicyState Current => Volatile.Read(ref _current);

        internal void Replace(BanPickPolicy value) => Volatile.Write(ref _current, new(Current.Revision + 1, value));

        /// <summary>Rejects persistence because these tests change only their in-memory fixture document.</summary>
        public ValueTask<BanPickPolicyState> SaveAsync(long expectedRevision, BanPickPolicy value, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class PolicyHub(IBanPickPolicyStore store) : IBanPickHub
    {
        private IBanPickScope? _scope;
        internal IBanPickScope Scope => Volatile.Read(ref _scope)
            ?? throw new InvalidOperationException("The ban-pick scope was not registered.");

        /// <summary>Supplies the fixture document while retaining the production room evaluator.</summary>
        public IBanPickPolicyStore Store => store;

        /// <summary>Bounds announcements and room lists for the mounted ban-pick plugin.</summary>
        public BanPickSettings Settings { get; } = new(25, 60_000, 60_000, 16, 10);

        /// <summary>Disables the optional overlay so the real plugin publishes native server notices.</summary>
        public ICompanionHub? Companion => null;

        /// <summary>Retains the plugin's command scope until its registration effect is unwound.</summary>
        public IDisposable Register(IBanPickScope scope)
        {
            if (Interlocked.CompareExchange(ref _scope, scope, null) is not null)
                throw new InvalidOperationException("A ban-pick scope is already registered.");
            return new Removal(() => Interlocked.CompareExchange(ref _scope, null, scope));
        }
    }

    private sealed class ItemData : IClientDataStore
    {
        /// <summary>Exposes exactly the workbook consumed by the production ban-pick room plugin.</summary>
        public bool Exists(string path) => path == "Config/ShopItem2.xml";

        /// <summary>Builds a minimal workbook containing the pet article used by the real room and ban-pick evaluator.</summary>
        public byte[] Read(string path)
        {
            if (!Exists(path)) throw new FileNotFoundException(path);
            static string Row(params string[] cells) => "<Row>" + string.Concat(cells.Select(cell =>
                $"<Cell><Data ss:Type=\"String\">{cell}</Data></Cell>")) + "</Row>";
            static string Sheet(string name, string rows) => $"<Worksheet ss:Name=\"{name}\"><Table>{rows}</Table></Worksheet>";
            return Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><Workbook xmlns=\"urn:schemas-microsoft-com:office:spreadsheet\" "
                + "xmlns:ss=\"urn:schemas-microsoft-com:office:spreadsheet\">"
                + Sheet("ShopItem", Row("itemId", "itemName", "mainCategory", "subCategory", "function")
                    + Row("301001", "Pet", "Etc", "Followers", "10001"))
                + Sheet("MainCategory", Row("id", "name") + Row("500", "Etc"))
                + Sheet("SubCategory", Row("id", "name") + Row("504", "Followers")) + "</Workbook>");
        }
    }

    private sealed class Removal(Action remove) : IDisposable
    {
        /// <summary>Releases the mounted ban-pick scope from the fixture hub.</summary>
        public void Dispose() => remove();
    }
}
