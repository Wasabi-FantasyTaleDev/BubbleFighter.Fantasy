using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Pets;
using BubbleFighter.Fantasy.Contracts.Players;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.Dialect.Messages;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Lobby.Data;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using BubbleFighter.Fantasy.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Lobby.Tests;

/// <summary>Checks that login publishes the complete native inventory commit after selection normalization.</summary>
public sealed class LoginCommittedInventoryTests
{
    private const string Build = "kr-1256";
    private const string Protocol = Build + "/game";

    /// <summary>The pet commit owns both result vectors even when normalization returned a different character bag.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResolverReturnsBothCommittedBagsWithoutReadingAfterCommit(bool normalize)
    {
        var state = new SnapshotState(42);
        var inventory = new Inventory(state);
        var pets = new Entitlements(state);
        var snapshot = await new LoginInventoryResolver(inventory, false,
            normalize ? new Selection(state) : null, petEntitlements: pets).ResolveAsync(state.UserNum, default);

        Assert.True(snapshot.IsComplete);
        Assert.Same(state.Complete.CharacterBag, snapshot.CharacterBag);
        Assert.Same(state.Complete.EquippedBag, snapshot.EquippedBag);
        Assert.Equal(normalize ? new[] { "character", "equipped", "selection", "commit" }
            : new[] { "character", "equipped", "commit" }, state.Operations);
        Assert.Equal(1, pets.Preparations);
    }

    /// <summary>The actual login handler and dialect preserve completeness and publish the committed rows without another read.</summary>
    [Fact]
    public async Task HostedLoginCarriesCommittedSnapshotThroughInventoryAndEquipmentPublication()
    {
        var services = new ServiceRegistry();
        var events = new EventBus(NullLogger.Instance);
        var players = new InMemoryPlayerRepository();
        var player = await players.CreateAsync("account", "fighter", default);
        var state = new SnapshotState(player.UserNum);
        var inventory = new Inventory(state);
        var pets = new Entitlements(state);
        var session = new Session();
        var tables = PacketTables.Load(Path.Combine(Root(), "packets"));
        var opcodes = OpcodeTableLoader.Load(Path.Combine(Root(), "profiles", Build, "opcodes.yaml"), session.Profile);
        var inventoryBinding = new InventoryBinding(Protocol, opcodes, tables, Build, null, events);
        var binding = new LoginBinding(Protocol, LoginValues.From(Build, tables.ValuesOf(Build)),
            opcodes.S2C("gspRepLogin"), opcodes, tables, Build, services, events, inventoryBinding, null, NullLogger.Instance);
        using var playerRegistration = services.Register<IPlayerRepository>(players);
        using var inventoryRegistration = services.Register<IInventoryRepository>(inventory);
        using var selectionRegistration = services.Register<IEquipmentSelection>(new Selection(state));
        using var petRegistration = services.Register<IPetEntitlements>(pets);
        using var inboundRegistration = services.RegisterMany<IInboundMessageBinding<LoginRequested>>(new LoginInbound());
        using var successRegistration = services.RegisterMany<IMessageSender<LoginSucceeded>>(binding);
        using var rejectionRegistration = services.RegisterMany<IMessageSender<LoginRejected>>(binding);
        var publications = 0;
        using var publication = events.Intercept<InventoryPublicationPreparing, InventoryReplaced?>(async (request, next, ct) =>
        {
            Assert.True(state.Committed);
            Assert.True(request.IsComplete);
            Assert.Equal(Protocol, request.Protocol);
            Assert.Equal(state.UserNum, request.UserNum);
            Assert.Null(request.Changes);
            Assert.Equal(state.Complete.CharacterBag, request.CharacterBag);
            Assert.Equal(state.Complete.EquippedBag, request.EquippedBag);
            publications++;
            return await next(ct);
        });
        var equipmentPublications = 0;
        using var equipment = events.Intercept<LoginEquipmentRequested, bool>(async (request, next, ct) =>
        {
            Assert.True(request.Login.InventoryIsComplete);
            Assert.Equal(state.Complete.CharacterBag, request.Login.CharacterBag);
            Assert.Equal(state.Complete.EquippedBag, request.Login.EquippedBag);
            Assert.Equal(300007, request.Login.CharacterItemId);
            Assert.Equal(new[] { 300007, 301001 }, request.Login.EquippedItemIds);
            equipmentPublications++;
            return await next(ct);
        });
        using var projection = events.Intercept<InventoryPresentationRequested, InventoryItemPresentation>((request, _, _) =>
        {
            Assert.True(state.Committed);
            Assert.Contains(request.Slot, state.Complete.CharacterBag.Concat(state.Complete.EquippedBag));
            if (request.Slot.ItemId != 301001) return ValueTask.FromResult(InventoryItemPresentation.Empty);
            Assert.Equal(state.Complete.CharacterBag[1].PetIdentity, request.Slot.PetIdentity);
            Assert.Equal(37, request.Slot.Pet!.Find(Build)!.Experience);
            return ValueTask.FromResult(new InventoryItemPresentation([new(301001, 7, [1, 37, 0, 0], 0, "")], null));
        });
        var routerContext = Context("core.message-router", services, events);
        var lobbyContext = Context("lobby", services, events, new()
        {
            ["protocol"] = Protocol, ["authentication"] = "open", ["inventory_source"] = "mysql",
            ["seed_starter_inventory"] = false, ["expire_items"] = false,
        });
        try
        {
            await new MessageRouterPlugin().ActivateAsync(routerContext, default);
            await new LobbyPlugin().ActivateAsync(lobbyContext, default);
            Assert.True(await services.Get<IMessageRouter>().DispatchAsync(Protocol, session, new LoginRequested("account"), default));
            Assert.Equal(new[] { "character", "equipped", "selection", "commit" }, state.Operations);
            Assert.Equal(1, pets.Preparations);
            Assert.Equal(1, publications);
            Assert.Equal(1, equipmentPublications);
            Assert.Contains(opcodes.S2C("gspUpdateAllArticleSlot"), session.Sent);
            Assert.Contains(opcodes.S2C("gspGotoRoom"), session.Sent);
            Assert.Equal(SessionPhase.MainMenu, session.Features.Get<IPlayerSession>().Phase);
        }
        finally
        {
            await lobbyContext.UnwindAsync();
            await routerContext.UnwindAsync();
        }
    }

    private static PluginContext Context(string id, ServiceRegistry services, EventBus events,
        Dictionary<string, object?>? config = null) =>
        new(new PluginRow { Id = id, Plugin = id, Source = "test", Config = config }, services, events, NullLogger.Instance);

    private sealed class SnapshotState
    {
        internal SnapshotState(long userNum)
        {
            UserNum = userNum;
            var pet = new InventorySlotRecord(userNum, InventoryBag.CharacterBag, 12, 301001, 1, 1)
            {
                PetIdentity = new(5, PetArticleKeys.First + 5),
                Pet = PetProgressions.Empty.With(Build, new(1, 37, DateTimeOffset.MaxValue, null, null, null)),
            };
            Complete = new([new(userNum, InventoryBag.CharacterBag, 11, 300007, 1, 1), pet,
                new(userNum, InventoryBag.CharacterBag, 13, 200001, 17, 0)],
                [pet with { Bag = InventoryBag.EquippedBag, SlotKey = 22 }]);
        }

        internal long UserNum { get; }
        internal PetInventorySnapshot Complete { get; }
        internal List<string> Operations { get; } = [];
        internal bool Committed { get; set; }
    }

    private sealed class Inventory(SnapshotState state) : IInventoryRepository
    {
        /// <summary>Records each provisional bag read and rejects any reread after the complete login snapshot is committed.</summary>
        public ValueTask<IReadOnlyList<InventorySlotRecord>> ListSlotsAsync(long userNum, InventoryBag bag, int maxCount, CancellationToken ct)
        {
            Assert.False(state.Committed, "Login must not reread inventory after its complete snapshot is committed.");
            Assert.Equal(state.UserNum, userNum);
            Assert.Equal(InventoryLimits.MaxInventorySlotsPerBag, maxCount);
            state.Operations.Add(bag == InventoryBag.CharacterBag ? "character" : "equipped");
            return ValueTask.FromResult<IReadOnlyList<InventorySlotRecord>>([new(userNum, bag, 1, 300001, 1, 1)]);
        }
        /// <summary>Rejects inventory replacement because the fixture login must publish its prepared ownership snapshot.</summary>
        public ValueTask ReplaceSlotsAsync(long userNum, InventoryBag bag, IReadOnlyList<InventorySlotRecord> slots, CancellationToken ct) =>
            throw new InvalidOperationException("This login must not replace inventory.");
        /// <summary>Rejects direct worn-flag changes that bypass the configured canonical selection service.</summary>
        public ValueTask<InventorySlotRecord?> SetSlotWornAsync(long userNum, int itemId, bool worn, CancellationToken ct) =>
            throw new InvalidOperationException("Selection must use the configured canonical service.");
    }

    private sealed class Selection(SnapshotState state) : IEquipmentSelection
    {
        /// <summary>Records provisional equipment normalization before the final login snapshot is committed.</summary>
        public ValueTask<IReadOnlyList<InventorySlotRecord>> ReadAsync(long userNum, CancellationToken ct)
        {
            Assert.False(state.Committed);
            Assert.Equal(state.UserNum, userNum);
            state.Operations.Add("selection");
            return ValueTask.FromResult<IReadOnlyList<InventorySlotRecord>>([new(userNum, InventoryBag.CharacterBag, 2, 300005, 1, 1)]);
        }
        /// <summary>Rejects explicit equipment changes during login.</summary>
        public ValueTask<IReadOnlyList<InventorySlotRecord>?> ApplyAsync(long userNum, int itemId, bool worn, CancellationToken ct) =>
            throw new InvalidOperationException("Login must not make an explicit equipment selection.");
    }

    private sealed class Entitlements(SnapshotState state) : IPetEntitlements
    {
        internal int Preparations { get; private set; }
        /// <summary>Gets the build whose final inventory snapshot the fixture prepares.</summary>
        public string BuildKey => Build;
        /// <summary>Identifies the pet contained in the fixture's complete login snapshot.</summary>
        public bool IsPetItem(int itemId) => itemId == 301001;
        /// <summary>Commits the fixture's final inventory snapshot once and records when subsequent reads become forbidden.</summary>
        public ValueTask<PetInventorySnapshot> PrepareAsync(long userNum, CancellationToken ct)
        {
            Assert.False(state.Committed, "A committed login snapshot must not be prepared again during publication.");
            Assert.Equal(state.UserNum, userNum);
            Preparations++;
            state.Operations.Add("commit");
            state.Committed = true;
            return ValueTask.FromResult(state.Complete);
        }
        /// <summary>Rejects separate initialization commands because login uses one final complete preparation.</summary>
        public PetInitializationCommand CreateInitialization(long userNum, DateTimeOffset now) =>
            throw new InvalidOperationException("Login uses one final complete preparation.");
        /// <summary>Rejects Article resolution from another snapshot during login publication.</summary>
        public ValueTask<PetEquipmentTarget?> ResolveAsync(long userNum, ulong articleKey, CancellationToken ct) =>
            throw new InvalidOperationException("Login must not resolve another inventory snapshot.");
        /// <summary>Rejects pet selection changes during login.</summary>
        public ValueTask<PetInventorySnapshot?> SelectAsync(long userNum, PetEquipmentTarget target,
            IReadOnlyList<InventoryWearGroup> groups, bool worn, CancellationToken ct) =>
            throw new InvalidOperationException("Login must not change pet selection.");
        /// <summary>Rejects ordinary item selection changes during login.</summary>
        public ValueTask<PetInventorySnapshot?> SelectItemAsync(long userNum, int itemId,
            IReadOnlyList<InventoryWearGroup> groups, bool worn, CancellationToken ct) =>
            throw new InvalidOperationException("Login must not change item selection.");
    }

    private sealed class LoginInbound : IInboundMessageBinding<LoginRequested>
    {
        /// <summary>Gets the protocol advertising the fixture's inbound login contract.</summary>
        public string Protocol => LoginCommittedInventoryTests.Protocol;
    }

    private sealed class Session : ISessionWithFeatures
    {
        /// <summary>Gets the fixed session identity used for the committed-login publication.</summary>
        public long Id => 1;
        /// <summary>Gets the mutable test feature registry populated by the login flow.</summary>
        public ISessionFeatures Features { get; } = new TestSessionFeatures();
        /// <summary>Gets the open fixture connection backing the login session.</summary>
        public IConnection Connection { get; } = new Connection();
        /// <summary>Gets the modern Korean profile selected for the fixture's login flow.</summary>
        public ClientProfile Profile { get; } = new(Build, "kr", 1256, 0, 0, Build);
        /// <summary>Gets session-local state retained across the login publication sequence.</summary>
        public ConcurrentDictionary<string, object> Items { get; } = new();
        internal List<ushort> Sent { get; } = [];
        /// <summary>Records outgoing opcodes so the test can inspect the login publication order.</summary>
        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            Sent.Add(opcode);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Connection : IConnection
    {
        /// <summary>Gets the fixed connection identity paired with the login session.</summary>
        public long Id => 1;
        /// <summary>Gets a loopback remote address for the in-process login fixture.</summary>
        public EndPoint RemoteEndPoint => new IPEndPoint(IPAddress.Loopback, 1);
        /// <summary>Rejects raw transport reads because login is dispatched directly by the fixture.</summary>
        public PipeReader Input => throw new NotSupportedException();
        /// <summary>Rejects raw transport writes because the session records outgoing opcodes.</summary>
        public PipeWriter Output => throw new NotSupportedException();
        /// <summary>Gets connection-local storage available to the login flow.</summary>
        public ConcurrentDictionary<string, object> Items { get; } = new();
        /// <summary>Reports the fixture connection as open throughout login publication.</summary>
        public bool IsClosed => false;
        /// <summary>Completes fixture connection closure without opening or disposing a transport.</summary>
        public ValueTask CloseAsync(string reason) => ValueTask.CompletedTask;
    }

    private static string Root()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "packets", "builds.yaml"))) return directory.FullName;
        throw new InvalidOperationException("The test must run below the repository.");
    }
}
