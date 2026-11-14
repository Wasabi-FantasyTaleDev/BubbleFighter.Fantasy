using System.Collections.Concurrent;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Pets;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Kernel;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Pets.Tests;

/// <summary>Checks pet request admission, typed refusals and publication lifetime through the real message router.</summary>
public sealed class PetRoomMutationRoutesTests
{
    private const string Protocol = "kr-1256/game";
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    /// <summary>Each pet route refuses an already-started room with its own reply and the original request correlation.</summary>
    [Theory]
    [InlineData("legacy-wear")]
    [InlineData("legacy-remove")]
    [InlineData("article-wear")]
    [InlineData("article-remove")]
    [InlineData("level")]
    [InlineData("ability")]
    public async Task PetRequestsRefuseWithTheirTypedReplyAfterRoomStart(string kind)
    {
        await using var rig = await Rig.CreateAsync(waiting: false);
        var request = Request(kind);

        Assert.True(await rig.DispatchAsync(request).WaitAsync(Limit));

        Assert.Equal(0, rig.NextCount);
        Assert.Null(rig.SeenRequest);
        Assert.Equal(1, rig.Rooms.FindCount);
        var reply = Assert.Single(rig.Replies);
        Assert.Same(rig.Session, reply.Session);
        switch (request)
        {
            case EquipmentChangeRequested equipment:
                var equipmentReply = Assert.IsType<EquipmentChangeReplied>(reply.Message);
                Assert.Same(equipment, equipmentReply.Request);
                Assert.False(equipmentReply.Succeeded);
                break;
            case PetLevelUpRequested level:
                Assert.Equal(new PetLevelUpReplied(level.Target, 1), Assert.IsType<PetLevelUpReplied>(reply.Message));
                break;
            case PetAbilityChangeRequested ability:
                Assert.Equal(new PetAbilityChangeReplied(ability.Target, 1, ability.NewSelector),
                    Assert.IsType<PetAbilityChangeReplied>(reply.Message));
                break;
            default:
                throw new InvalidOperationException("The test request has no expected refusal contract.");
        }
    }

    /// <summary>Explicit ordinary item IDs and unaddressable references bypass pet admission even after a room starts.</summary>
    [Theory]
    [InlineData(200001L, 200001)]
    [InlineData(100000000L, 200001)]
    [InlineData(99999999L, null)]
    [InlineData(-1L, null)]
    public async Task OrdinaryEquipmentDoesNotEnterPetAdmission(long reference, int? itemId)
    {
        await using var rig = await Rig.CreateAsync(waiting: false);
        var request = new EquipmentChangeRequested(reference, itemId, true);

        Assert.True(await rig.DispatchAsync(request).WaitAsync(Limit));

        Assert.Equal(1, rig.NextCount);
        Assert.Same(request, rig.SeenRequest);
        Assert.Equal(0, rig.Rooms.FindCount);
        Assert.Empty(rig.Replies);
    }

    /// <summary>Every accepted pet route keeps start admission held until downstream publication finishes and then resumes once.</summary>
    [Theory]
    [InlineData("legacy-wear")]
    [InlineData("legacy-remove")]
    [InlineData("article-wear")]
    [InlineData("article-remove")]
    [InlineData("level")]
    [InlineData("ability")]
    public async Task PetRequestsHoldRoomStartUntilDownstreamPublicationReturns(string kind)
    {
        await using var rig = await Rig.CreateAsync(waiting: true);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.PublishAsync = async ct =>
        {
            entered.TrySetResult();
            await released.Task.WaitAsync(ct);
        };
        var request = Request(kind);
        var dispatch = rig.DispatchAsync(request);
        try
        {
            await entered.Task.WaitAsync(Limit);
            Assert.True(await rig.Rooms.StartAsync().AsTask().WaitAsync(Limit));
            Assert.True(rig.Rooms.Waiting);
            Assert.Equal(0, rig.Rooms.ResumeCount);
            Assert.Equal(0, rig.Rooms.StartedCount);
            Assert.False(dispatch.IsCompleted);
            Assert.Equal(1, rig.NextCount);
            Assert.Same(request, rig.SeenRequest);
            Assert.Empty(rig.Replies);

            released.TrySetResult();
            Assert.True(await dispatch.WaitAsync(Limit));
            await rig.Rooms.Resumed.Task.WaitAsync(Limit);

            Assert.False(rig.Rooms.Waiting);
            Assert.Equal(1, rig.Rooms.ResumeCount);
            Assert.Equal(1, rig.Rooms.StartedCount);
            Assert.Equal(1, rig.NextCount);
            Assert.Empty(rig.Replies);
        }
        finally
        {
            released.TrySetResult();
            await dispatch.WaitAsync(Limit);
        }
    }

    private static IMessage Request(string kind) => kind switch
    {
        "legacy-wear" => new EquipmentChangeRequested(PetTestData.PetId, PetTestData.PetId, true),
        "legacy-remove" => new EquipmentChangeRequested(PetTestData.PetId, PetTestData.PetId, false),
        "article-wear" => new EquipmentChangeRequested((long)PetArticleKeys.First, null, true),
        "article-remove" => new EquipmentChangeRequested((long)PetArticleKeys.First, null, false),
        "level" => new PetLevelUpRequested(PetArticleKeys.First),
        "ability" => new PetAbilityChangeRequested(PetArticleKeys.First, 11, 12, PetTestData.ChangeFood),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private sealed class Rig(bool waiting) : IAsyncDisposable
    {
        private readonly PluginContext _context = new(new PluginRow
        {
            Id = "pet-routes", Plugin = "pets", Source = "test", Lifecycle = new() { EffectCapacity = 64 },
        }, new ServiceRegistry(), new EventBus(NullLogger.Instance), NullLogger.Instance);
        private IMessageRouter _router = null!;
        private int _nextCount;
        internal RoomHost Rooms { get; } = new(waiting);
        internal Session Session { get; } = new();
        internal List<(ISession Session, IMessage Message)> Replies { get; } = [];
        internal Func<CancellationToken, ValueTask>? PublishAsync { get; set; }
        internal IMessage? SeenRequest { get; private set; }
        internal int NextCount => Volatile.Read(ref _nextCount);

        internal static async Task<Rig> CreateAsync(bool waiting)
        {
            var rig = new Rig(waiting);
            var context = rig._context;
            try
            {
                await new MessageRouterPlugin().ActivateAsync(context, default);
                rig._router = context.Services.Get<IMessageRouter>();
                var routes = rig._router.For(context);
                context.Effect(routes.On<EquipmentChangeRequested>(Protocol, (_, request, ct) => rig.NextAsync(request, ct)));
                context.Effect(routes.On<PetLevelUpRequested>(Protocol, (_, request, ct) => rig.NextAsync(request, ct)));
                context.Effect(routes.On<PetAbilityChangeRequested>(Protocol, (_, request, ct) => rig.NextAsync(request, ct)));
                context.Effect(routes.On<InventoryItemUseRequested>(Protocol, (_, request, ct) => rig.NextAsync(request, ct)));
                context.Effect(context.Services.Register<IGameText>(new Texts()));
                context.Effect(context.Services.RegisterMany<IRoomPolicyHost>(rig.Rooms));
                context.Effect(context.Services.RegisterMany<IInboundMessageBinding<PetAbilityChangeRequested>>(new AbilityBinding()));
                rig.AddSender<ServerNotice>();
                rig.AddSender<EquipmentChangeReplied>();
                rig.AddSender<InventoryItemUseReplied>();
                rig.AddSender<PetLevelUpReplied>();
                rig.AddSender<PetAbilityChangeReplied>();
                await PetRoomMutationRoutes.RegisterAsync(context, PetTestData.Options(manual: true),
                    PetTestData.Catalog(), new Entitlements(), routes);
                return rig;
            }
            catch
            {
                await rig.DisposeAsync();
                throw;
            }
        }

        internal Task<bool> DispatchAsync(IMessage request) => request switch
        {
            EquipmentChangeRequested equipment => _router.DispatchAsync(Protocol, Session, equipment, default).AsTask(),
            PetLevelUpRequested level => _router.DispatchAsync(Protocol, Session, level, default).AsTask(),
            PetAbilityChangeRequested ability => _router.DispatchAsync(Protocol, Session, ability, default).AsTask(),
            _ => throw new ArgumentOutOfRangeException(nameof(request)),
        };

        private void AddSender<T>() where T : IMessage =>
            _context.Effect(_context.Services.RegisterMany<IMessageSender<T>>(new Sender<T>(Replies)));

        private async ValueTask NextAsync(IMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            SeenRequest = request;
            Interlocked.Increment(ref _nextCount);
            if (PublishAsync is { } publish) await publish(ct);
        }

        /// <summary>Cancels and drains the route owner, then removes its policy, worker and fixture services.</summary>
        public async ValueTask DisposeAsync() => await _context.UnwindAsync().AsTask().WaitAsync(Limit);
    }

    private sealed class RoomHost(bool waiting) : IRoomPolicyHost
    {
        private readonly object _gate = new();
        private readonly List<IRoomPolicy> _policies = [];
        private RoomPolicyRoom _room = new(Guid.NewGuid(), 1, 4, false, waiting,
            [new(1, 0, 1, "host", 300001, [], true)]);
        private bool _held;
        private int _finds;
        private int _resumes;
        private int _started;
        internal int FindCount => Volatile.Read(ref _finds);
        internal int ResumeCount => Volatile.Read(ref _resumes);
        internal int StartedCount => Volatile.Read(ref _started);
        internal bool Waiting { get { lock (_gate) return _room.Waiting; } }
        internal TaskCompletionSource Resumed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <inheritdoc />
        public string Protocol => PetRoomMutationRoutesTests.Protocol;
        /// <inheritdoc />
        public string ClientBuild => "kr-1256";

        /// <inheritdoc />
        public IDisposable Register(IRoomPolicy policy)
        {
            lock (_gate) _policies.Add(policy);
            return new Registration(() => { lock (_gate) _policies.Remove(policy); });
        }

        /// <inheritdoc />
        public RoomPolicyRoom? Find(long sessionId)
        {
            Interlocked.Increment(ref _finds);
            lock (_gate) return sessionId == 1 ? _room : null;
        }

        /// <inheritdoc />
        public ISession? Session(long sessionId) => null;

        internal async ValueTask<bool> StartAsync(CancellationToken ct = default)
        {
            IRoomPolicy[] holders;
            RoomPolicyRoom room;
            lock (_gate)
            {
                ct.ThrowIfCancellationRequested();
                room = _room;
                if (!room.Waiting) return false;
                holders = _policies.Where(policy => policy.EvaluateStart(room, 1) == RoomStartDecision.Hold).ToArray();
                _held = holders.Length != 0;
                if (!_held)
                {
                    _room = room with { Waiting = false };
                    Interlocked.Increment(ref _started);
                    return false;
                }
            }
            foreach (var policy in holders) await policy.OnStartHeldAsync(room, 1, ct);
            return true;
        }

        /// <inheritdoc />
        public async ValueTask<bool> ResumeHeldStartAsync(Guid instance, long requesterSessionId, CancellationToken ct)
        {
            lock (_gate)
            {
                ct.ThrowIfCancellationRequested();
                if (!_held || !_room.Waiting || _room.Instance != instance || requesterSessionId != 1) return false;
                Interlocked.Increment(ref _resumes);
            }
            try { await StartAsync(ct); return true; }
            finally { Resumed.TrySetResult(); }
        }
    }

    private sealed class Entitlements : IPetEntitlements
    {
        /// <inheritdoc />
        public string BuildKey => "kr-1256";
        /// <inheritdoc />
        public bool IsPetItem(int itemId) => itemId == PetTestData.PetId;
        /// <inheritdoc />
        public PetInitializationCommand CreateInitialization(long userNum, DateTimeOffset now) => throw new NotSupportedException();
        /// <inheritdoc />
        public ValueTask<PetInventorySnapshot> PrepareAsync(long userNum, CancellationToken ct) => throw new NotSupportedException();
        /// <inheritdoc />
        public ValueTask<PetEquipmentTarget?> ResolveAsync(long userNum, ulong articleKey, CancellationToken ct) => throw new NotSupportedException();
        /// <inheritdoc />
        public ValueTask<PetInventorySnapshot?> SelectAsync(long userNum, PetEquipmentTarget target,
            IReadOnlyList<InventoryWearGroup> groups, bool worn, CancellationToken ct) => throw new NotSupportedException();
        /// <inheritdoc />
        public ValueTask<PetInventorySnapshot?> SelectItemAsync(long userNum, int itemId,
            IReadOnlyList<InventoryWearGroup> groups, bool worn, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class Sender<T>(List<(ISession Session, IMessage Message)> replies) : IMessageSender<T> where T : IMessage
    {
        /// <inheritdoc />
        public string Protocol => PetRoomMutationRoutesTests.Protocol;
        /// <inheritdoc />
        public ValueTask SendAsync(ISession session, T message, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            replies.Add((session, message));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class AbilityBinding : IInboundMessageBinding<PetAbilityChangeRequested>
    {
        /// <inheritdoc />
        public string Protocol => PetRoomMutationRoutesTests.Protocol;
    }

    private sealed class Texts : IGameText
    {
        /// <inheritdoc />
        public string Format(string region, string key, IReadOnlyDictionary<string, string>? arguments = null) => key;
        /// <inheritdoc />
        public bool TryFormat(string region, string key, out string text, IReadOnlyDictionary<string, string>? arguments = null)
        { text = Format(region, key, arguments); return true; }
    }

    private sealed class Registration(Action remove) : IDisposable
    {
        private Action? _remove = remove;
        /// <summary>Removes the fixture's room policy registration at most once.</summary>
        public void Dispose() => Interlocked.Exchange(ref _remove, null)?.Invoke();
    }

    private sealed class Session : ISession
    {
        /// <inheritdoc />
        public long Id => 1;
        /// <inheritdoc />
        public IConnection Connection => throw new NotSupportedException();
        /// <inheritdoc />
        public ClientProfile Profile { get; } = new("kr-1256", "kr", 1256, 0, 0, "pet route test");
        /// <inheritdoc />
        public ConcurrentDictionary<string, object> Items { get; } = new();
        /// <inheritdoc />
        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct) => throw new NotSupportedException();
    }
}
