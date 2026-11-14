using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Pipelines;
using System.Net;
using System.Text;
using System.Xml.Linq;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Pets;
using BubbleFighter.Fantasy.Contracts.Players;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.InventoryView;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Persistence.Memory;
using BubbleFighter.Fantasy.Pets.Data;
using BubbleFighter.Fantasy.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Pets.Tests;

/// <summary>Exercises canonical inventory filtering through real hosted pet activations and independent build namespaces.</summary>
public sealed class PetCanonicalScopeHostTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);
    private static readonly IReadOnlyDictionary<string, string> Versions = new Dictionary<string, string>
    {
        ["BubbleFighter.Fantasy.Abstractions"] = typeof(IPlugin).Assembly.GetName().Version!.ToString(),
        ["BubbleFighter.Fantasy.Contracts"] = typeof(IInventoryBuildScope).Assembly.GetName().Version!.ToString(),
    };

    /// <summary>Metadata, mutations and publication reuse FighterConfig filtering while another build retains ownership and selection.</summary>
    [Fact]
    public async Task HostedPetsUseTheInventoryScopeAndUnwindWithoutDisturbingAnotherBuild()
    {
        await using var rig = new Rig();
        await rig.SeedAsync();
        await rig.Host.StartAsync(default).AsTask().WaitAsync(Limit);
        Assert.All(rig.Host.Instances, row => Assert.True(row.State == PluginState.Active, $"{row.RowId}: {row.State}: {row.Error}"));
        var left = rig.Left;
        var right = rig.Right;
        Assert.Equal(1, left.Rooms.PolicyCount);
        Assert.Equal(1, right.Rooms.PolicyCount);
        var leftScope = left.Scope;
        var rightScope = right.Scope;
        var leftEntitlements = left.Entitlements;
        Assert.NotSame(leftScope, rightScope);
        Assert.NotSame(leftEntitlements, right.Entitlements);
        Assert.DoesNotContain(300029, leftScope.SupportedItemIds);
        Assert.Contains(300029, rightScope.SupportedItemIds);
        Assert.Equal("kr-812", leftScope.Key);
        Assert.Equal("cn-717", rightScope.Key);
        Assert.Same(leftScope, leftEntitlements.CreateInitialization(1, PetTestData.Now).Scope);
        Assert.Same(rightScope, right.Entitlements.CreateInitialization(1, PetTestData.Now).Scope);

        var before = await rig.RawAsync();
        var unsupported = Assert.Single(before, row => row.ItemId == 300029);
        Assert.Null(Assert.Single(before, row => row.ItemId == PetTestData.PetId).PetIdentity);
        var leftRows = await left.Inventory.ListSlotsAsync(1, InventoryBag.CharacterBag, 32, default);
        var rightRows = await right.Inventory.ListSlotsAsync(1, InventoryBag.CharacterBag, 32, default);
        Assert.DoesNotContain(leftRows, row => row.ItemId == 300029);
        Assert.Contains(rightRows, row => row.ItemId == 300029);
        var initializedLeft = Assert.Single(leftRows, row => row.ItemId == PetTestData.PetId);
        var initializedRight = Assert.Single(rightRows, row => row.ItemId == PetTestData.PetId);
        Assert.NotNull(initializedLeft.PetIdentity);
        Assert.Equal(initializedLeft.PetIdentity, initializedRight.PetIdentity);
        Assert.NotNull(initializedLeft.Pet!.Find(left.Build));
        var preservedProgress = initializedRight.Pet!.Find(right.Build);
        Assert.Equal(PetTestData.State(1, 37), preservedProgress);

        InventoryWearGroup[] characters = [new(300001, 300031, 300001, true)];
        Assert.NotNull(await right.Entitlements.SelectItemAsync(1, 300029, characters, true, default));
        Assert.NotNull(await left.Entitlements.SelectItemAsync(1, 300001, characters, true, default));
        var rightSelection = await rig.Store.GetLoadoutAsync(1, rightScope, default);
        Assert.Contains(300029, rightSelection.WornItemIds!);
        Assert.DoesNotContain(300001, rightSelection.WornItemIds!);

        var metadata = await rig.MetadataAsync(left.Build);
        Assert.NotNull(metadata);
        Assert.DoesNotContain(metadata.CharacterBag.Concat(metadata.EquippedBag), row => row.ItemId == 300029);
        var partial = await rig.PublicationAsync(new(left.Protocol, 1, before, [], null, false));
        Assert.NotNull(partial);
        Assert.DoesNotContain(partial.CharacterBag.Concat(partial.EquippedBag), row => row.ItemId == 300029);
        await Assert.ThrowsAsync<InvalidDataException>(() => rig.PublicationAsync(
            new(left.Protocol, 1, before, [], null, true)).AsTask());
        Assert.Null(await rig.MetadataAsync("id-400"));
        Assert.Null(await rig.PublicationAsync(new("id-400/game", 1, [], [], null, false)));

        Assert.True(await left.FeedAsync(rig.Router));
        Assert.Equal(0, Assert.Single(left.Uses.Messages).Result);
        Assert.Equal(0, left.BaseCalls);
        var mutation = Assert.Single(rig.Mutations.Commands);
        Assert.Same(leftScope, mutation.Scope);
        Assert.Same(leftScope, mutation.Initialization!.Scope);
        var published = Assert.Single(left.Inventories.Messages);
        Assert.True(published.IsCompleteSnapshot);
        Assert.DoesNotContain(published.CharacterBag.Concat(published.EquippedBag), row => row.ItemId == 300029);
        var accepted = await rig.PublicationAsync(new(left.Protocol, 1, published.CharacterBag, published.EquippedBag, null, true));
        Assert.NotNull(accepted);
        Assert.Equal(published.CharacterBag, accepted.CharacterBag);
        Assert.DoesNotContain(Assert.Single(left.Equipment.Selections), id => id == 300029);
        Assert.Empty(right.Uses.Messages);
        Assert.Empty(right.Inventories.Messages);
        var after = await rig.RawAsync();
        Assert.Equal(unsupported, Assert.Single(after, row => row.ItemId == 300029));
        Assert.Equal(2, Assert.Single(after, row => row.ItemId == PetTestData.NormalFood).Quantity);
        var pet = Assert.Single(after, row => row.ItemId == PetTestData.PetId);
        Assert.Equal(5, pet.Pet!.Find(left.Build)!.Experience);
        Assert.Equal(preservedProgress, pet.Pet.Find(right.Build));
        Assert.Equal(initializedLeft.PetIdentity, pet.PetIdentity);
        Assert.True(InventoryLoadouts.SameState(rightSelection, await rig.Store.GetLoadoutAsync(1, rightScope, default)));
        Assert.Single((await rig.PresentationAsync(left.Protocol, pet)).Attributes);
        Assert.Equal(2, (await rig.PresentationAsync(right.Protocol, pet)).Attributes.Count);

        await rig.Host.UnloadAsync(left.Id("pets"), default).AsTask().WaitAsync(Limit);
        Assert.Equal(0, left.Rooms.PolicyCount);
        Assert.Equal(1, right.Rooms.PolicyCount);
        Assert.Same(InventoryItemPresentation.Empty, await rig.PresentationAsync(left.Protocol, pet));
        Assert.Equal(2, (await rig.PresentationAsync(right.Protocol, pet)).Attributes.Count);
        Assert.True(await left.FeedAsync(rig.Router));
        Assert.Equal(1, left.BaseCalls);
        Assert.Single(rig.Mutations.Commands);
        foreach (var row in new[] { "operations", "entitlements", "loadouts" })
            await rig.Host.UnloadAsync(left.Id(row), default).AsTask().WaitAsync(Limit);
        Assert.Null(await rig.MetadataAsync(left.Build));
        Assert.Null(await rig.PublicationAsync(new(left.Protocol, 1, [], [], null, false)));
        Assert.DoesNotContain(rig.Router.Handlers, handler => handler.Protocol == left.Protocol);
        Assert.NotNull(await rig.MetadataAsync(right.Build));
        Assert.Contains((await rig.PublicationAsync(new(right.Protocol, 1, [], [], null, false)))!.CharacterBag,
            row => row.ItemId == 300029 && (row.Flags & InventorySlotRecord.WornFlagBit) != 0);

        foreach (var row in new[] { "loadouts", "entitlements", "operations", "pets" })
            await rig.Host.LoadAsync(left.Id(row), default).AsTask().WaitAsync(Limit);
        Assert.NotSame(leftScope, left.Scope);
        Assert.NotSame(leftEntitlements, left.Entitlements);
        Assert.Equal(1, left.Rooms.PolicyCount);
        Assert.Equal(1, right.Rooms.PolicyCount);
        Assert.Same(rightScope, right.Scope);
        Assert.Same(left.Scope, left.Entitlements.CreateInitialization(1, PetTestData.Now).Scope);
        Assert.Single(rig.Router.Handlers, handler => handler.Protocol == left.Protocol);
        Assert.Single((await rig.PresentationAsync(left.Protocol, pet)).Attributes);
        Assert.DoesNotContain((await rig.MetadataAsync(left.Build))!.CharacterBag, row => row.ItemId == 300029);
        Assert.Equal(initializedLeft.PetIdentity, Assert.Single(await rig.RawAsync(), row => row.ItemId == PetTestData.PetId).PetIdentity);
        Assert.True(InventoryLoadouts.SameState(rightSelection, await rig.Store.GetLoadoutAsync(1, rightScope, default)));
        await rig.Host.ShutdownAsync(default).AsTask().WaitAsync(Limit);
        Assert.All(rig.Host.Instances, row => Assert.Equal(PluginState.Unloaded, row.State));
        Assert.Empty(rig.Router.Handlers);
        Assert.Equal(0, left.Rooms.PolicyCount);
        Assert.Equal(0, right.Rooms.PolicyCount);
        Assert.Null(await rig.MetadataAsync(left.Build));
        Assert.Null(await rig.MetadataAsync(right.Build));
        Assert.Same(InventoryItemPresentation.Empty, await rig.PresentationAsync(right.Protocol, pet));
    }

    /// <summary>Neither the full-hunger refusal nor premium food can conceal an unset modern cooldown in direct rule calls.</summary>
    [Theory]
    [InlineData(PetTestData.NormalFood)]
    [InlineData(PetTestData.PremiumFood)]
    public void MissingModernCooldownFailsBeforeFullHungerChecks(int foodId)
    {
        var catalog = PetTestData.Catalog();
        var options = PetTestData.Options(manual: true);
        options.FeedCooldownSeconds = null;
        var full = PetTestData.State(0, 0) with { FullnessExpiresAt = PetTestData.Now.AddDays(365) };
        Assert.Throws<InvalidOperationException>(() => new PetRules(catalog, options, new Random(1))
            .Feed(full, catalog.Items[PetTestData.PetId], catalog.Items[foodId], PetTestData.Now));
    }

    /// <summary>The hosted middleware holds its room through inventory and appearance publication without holding another build's room.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HostedRoomStartWaitsForTheLastPetPublication(bool pauseEquipment)
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var left = rig.Left;
        left.Rooms.EnterWaiting();
        rig.Right.Rooms.EnterWaiting();
        var pause = new PublicationPause();
        if (pauseEquipment) left.Equipment.AfterSend = pause.WaitAsync;
        else left.Inventories.AfterSend = pause.WaitAsync;
        var feed = left.FeedAsync(rig.Router).AsTask();
        try
        {
            await pause.Entered.Task.WaitAsync(Limit);
            Assert.Single(rig.Mutations.Commands);
            Assert.Equal(2, Assert.Single(await rig.RawAsync(), row => row.ItemId == PetTestData.NormalFood).Quantity);
            Assert.Empty(left.Uses.Messages);
            Assert.True(await left.Rooms.StartAsync());
            Assert.Equal(0, left.Rooms.StartedCount);
            Assert.Equal(0, left.Rooms.ResumeCount);
            Assert.Equal(left.Build + ":pets.start.waiting", Assert.Single(left.Notices.Messages).Text);
            Assert.False(await rig.Right.Rooms.StartAsync());
            Assert.Equal(1, rig.Right.Rooms.StartedCount);
            Assert.Empty(rig.Right.Notices.Messages);
            pause.Release();
            Assert.True(await feed.WaitAsync(Limit));
            await left.Rooms.Resumed.Task.WaitAsync(Limit);
            Assert.Equal(1, left.Rooms.ResumeCount);
            Assert.Equal(1, left.Rooms.StartedCount);
            Assert.Equal(0, Assert.Single(left.Uses.Messages).Result);
            Assert.Single(left.Inventories.Messages);
            Assert.Single(left.Equipment.Selections);
            Assert.Equal(0, left.BaseCalls);
        }
        finally { pause.Release(); await feed.WaitAsync(Limit); }
    }

    /// <summary>A started room refuses pet food before metadata initialization while ordinary inventory use still reaches the base handler.</summary>
    [Fact]
    public async Task HostedStartedRoomReturnsTypedRefusalWithoutMutation()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var left = rig.Left;
        left.Rooms.EnterWaiting();
        Assert.False(await left.Rooms.StartAsync());
        var before = (await rig.RawAsync()).ToArray();

        Assert.True(await left.FeedAsync(rig.Router));

        var refused = Assert.Single(left.Uses.Messages);
        Assert.Equal(1, refused.Result);
        Assert.Equal(PetTestData.NormalFood, refused.ItemId);
        Assert.Empty(rig.Mutations.Commands);
        Assert.Empty(left.Inventories.Messages);
        Assert.Empty(left.Equipment.Selections);
        Assert.Equal(before, await rig.RawAsync());
        Assert.True(await rig.Router.DispatchAsync(left.Protocol, left.Player, new InventoryItemUseRequested(200001, 1, ""), default));
        Assert.Equal(1, left.BaseCalls);
        Assert.Single(left.Uses.Messages);
    }

    /// <summary>A second same-session pet request receives a typed refusal without ending the first request's publication hold.</summary>
    [Fact]
    public async Task HostedDuplicatePetRequestCannotReleaseTheFirstMutation()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var left = rig.Left;
        left.Rooms.EnterWaiting();
        var pause = new PublicationPause();
        left.Inventories.AfterSend = pause.WaitAsync;
        var first = left.FeedAsync(rig.Router).AsTask();
        try
        {
            await pause.Entered.Task.WaitAsync(Limit);
            Assert.True(await left.FeedAsync(rig.Router).AsTask().WaitAsync(Limit));
            Assert.Equal(1, Assert.Single(left.Uses.Messages).Result);
            Assert.Single(rig.Mutations.Commands);
            Assert.True(await left.Rooms.StartAsync());
            Assert.Equal(0, left.Rooms.ResumeCount);
            Assert.False(first.IsCompleted);
            pause.Release();
            Assert.True(await first.WaitAsync(Limit));
            await left.Rooms.Resumed.Task.WaitAsync(Limit);
            Assert.Equal(new[] { 1, 0 }, left.Uses.Messages.Select(reply => reply.Result));
            Assert.Single(rig.Mutations.Commands);
            Assert.Equal(1, left.Rooms.StartedCount);
        }
        finally { pause.Release(); await first.WaitAsync(Limit); }
    }

    /// <summary>Publication failure and caller cancellation release the hosted admission mark after the committed mutation.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HostedPublicationFailureOrCancellationReleasesRoomStart(bool cancel)
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var left = rig.Left;
        left.Rooms.EnterWaiting();
        using var cancellation = new CancellationTokenSource();
        var pause = new PublicationPause();
        left.Equipment.AfterSend = pause.WaitAsync;
        var feed = left.FeedAsync(rig.Router, cancellation.Token).AsTask();
        try
        {
            await pause.Entered.Task.WaitAsync(Limit);
            Assert.True(await left.Rooms.StartAsync());
            if (cancel) cancellation.Cancel();
            else { pause.Failure = new IOException("equipment publication failed"); pause.Release(); }
            if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => feed.WaitAsync(Limit));
            else await Assert.ThrowsAsync<IOException>(() => feed.WaitAsync(Limit));
            await left.Rooms.Resumed.Task.WaitAsync(Limit);
            Assert.Equal(1, left.Rooms.StartedCount);
            Assert.Single(rig.Mutations.Commands);
            Assert.Empty(left.Uses.Messages);
            Assert.Equal(2, Assert.Single(await rig.RawAsync(), row => row.ItemId == PetTestData.NormalFood).Quantity);
        }
        finally
        {
            cancellation.Cancel();
            pause.Release();
            try { await feed.WaitAsync(Limit); }
            catch (IOException) { }
            catch (OperationCanceledException) { }
        }
    }

    /// <summary>Unloading one hosted pet row cancels its pending publication and removes its policy and middleware without touching another build.</summary>
    [Fact]
    public async Task HostedUnloadDrainsPublicationAndRemovesOnlyItsOwnPolicy()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var left = rig.Left;
        left.Rooms.EnterWaiting();
        using var cancellation = new CancellationTokenSource();
        var pause = new PublicationPause();
        left.Inventories.AfterSend = pause.WaitAsync;
        var feed = left.FeedAsync(rig.Router, cancellation.Token).AsTask();
        try
        {
            await pause.Entered.Task.WaitAsync(Limit);
            var unload = rig.Host.UnloadAsync(left.Id("pets"), default).AsTask();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => feed.WaitAsync(Limit));
            await unload.WaitAsync(Limit);
            Assert.Equal(0, left.Rooms.PolicyCount);
            Assert.Equal(1, rig.Right.Rooms.PolicyCount);
            Assert.Equal(PluginState.Unloaded, Assert.Single(rig.Host.Instances, row => row.RowId == left.Id("pets")).State);
            Assert.True(await left.FeedAsync(rig.Router));
            Assert.Equal(1, left.BaseCalls);
            Assert.Single(rig.Mutations.Commands);
            left.Rooms.EnterWaiting();
            Assert.False(await left.Rooms.StartAsync());
        }
        finally
        {
            cancellation.Cancel();
            pause.Release();
            try { await feed.WaitAsync(Limit); }
            catch (OperationCanceledException) { }
        }
    }

    private sealed class Rig : IAsyncDisposable
    {
        internal Rig()
        {
            Mutations = new(Store);
            var rows = new List<PluginRow> { Row("router", "core.message-router"), Row("store", "fixture.store", "router") };
            var definitions = new List<PluginDescriptor>
            {
                Native<MessageRouterPlugin>("core.message-router"), Native<InventoryLoadoutsPlugin>("inventory.loadouts"),
                Native<PetEntitlementsPlugin>("pets.entitlements"), Native<PetsPlugin>("pets"),
                Fixture(new("fixture.store")
                {
                    Requires = [typeof(IMessageRouter)],
                    Provides = [typeof(IInventoryMetadataStore), typeof(IInventoryLoadoutStore), typeof(IPetEntitlementStore), typeof(IPetMutationStore), typeof(IOnlinePlayers)],
                }, context =>
                {
                    Router = context.Services.Get<IMessageRouter>();
                    Events = context.Events;
                    context.Effect(context.Services.Register<IInventoryLoadoutStore>(Store));
                    context.Effect(context.Services.Register<IInventoryMetadataStore>(Store));
                    context.Effect(context.Services.Register<IPetEntitlementStore>(Store));
                    context.Effect(context.Services.Register<IPetMutationStore>(Mutations));
                    context.Effect(context.Services.Register<IOnlinePlayers>(new NoOnlinePlayers()));
                }),
            };
            foreach (var branch in new[] { Left, Right }) branch.Add(rows, definitions);
            Host = new(rows.AsEnumerable().Reverse().ToArray(), PluginCatalog.FromDefinitions(definitions), NullLoggerFactory.Instance);
        }

        internal Branch Left { get; } = new("kr-812", false);
        internal Branch Right { get; } = new("cn-717", true);
        internal MemoryInventoryRepository Store { get; } = new(new MemoryPlayerTable(4));
        internal RecordingMutations Mutations { get; }
        internal PluginHost Host { get; }
        internal IMessageRouter Router { get; private set; } = null!;
        private IEventBus Events { get; set; } = null!;

        internal async Task SeedAsync()
        {
            InventorySlotRecord[] slots =
            [
                PetTestData.Slot(PetTestData.State(1, 37), Right.Build),
                new(1, InventoryBag.CharacterBag, 9002, 300001, 1, 1) { Lifetime = InventoryLifetime.Permanent },
                new(1, InventoryBag.CharacterBag, 9003, 300029, 1, 0) { Lifetime = InventoryLifetime.Permanent },
                new(1, InventoryBag.CharacterBag, 9004, PetTestData.NormalFood, 3, 0) { Lifetime = InventoryLifetime.Permanent },
            ];
            foreach (var bag in new[] { InventoryBag.CharacterBag, InventoryBag.EquippedBag })
                await Store.ReplaceSlotsAsync(1, bag, slots.Select(row => row with { Bag = bag }).ToArray(), default);
        }

        internal async Task StartAsync()
        {
            await SeedAsync();
            await Host.StartAsync(default).AsTask().WaitAsync(Limit);
            Assert.All(Host.Instances, row => Assert.True(row.State == PluginState.Active, $"{row.RowId}: {row.State}: {row.Error}"));
        }

        internal ValueTask<IReadOnlyList<InventorySlotRecord>> RawAsync() => Store.ListSlotsAsync(1, InventoryBag.CharacterBag, 32, default);
        internal ValueTask<PetInventorySnapshot?> MetadataAsync(string build) =>
            Events.WaterfallAsync<InventoryMetadataPreparing, PetInventorySnapshot?>(new(build, 1),
                _ => ValueTask.FromResult<PetInventorySnapshot?>(null), default);
        internal ValueTask<InventoryReplaced?> PublicationAsync(InventoryPublicationPreparing request) =>
            Events.WaterfallAsync<InventoryPublicationPreparing, InventoryReplaced?>(request,
                _ => ValueTask.FromResult<InventoryReplaced?>(null), default);
        internal ValueTask<InventoryItemPresentation> PresentationAsync(string protocol, InventorySlotRecord slot) =>
            Events.WaterfallAsync<InventoryPresentationRequested, InventoryItemPresentation>(new(protocol, slot),
                _ => ValueTask.FromResult(InventoryItemPresentation.Empty), default);
        /// <summary>Shuts down the real plugin host and waits for every fixture row to unwind within the test deadline.</summary>
        public async ValueTask DisposeAsync() => await Host.ShutdownAsync(default).AsTask().WaitAsync(Limit);
    }

    private sealed class NoOnlinePlayers : IOnlinePlayers
    {
        /// <summary>Reports no online owner when the scope tests request an account lookup.</summary>
        public ISession? FindByUserNum(int userNum) => null;
        /// <summary>Reports no online player when the scope tests request a nickname lookup.</summary>
        public ISession? FindByNickname(string nickname) => null;
    }

    private sealed class Branch(string build, bool hasCharacter29)
    {
        internal string Build => build;
        internal string Protocol => build + "/game";
        internal InventoryLoadoutScope Scope { get; private set; } = null!;
        internal IPetEntitlements Entitlements { get; private set; } = null!;
        internal IInventoryRepository Inventory { get; private set; } = null!;
        internal Sender<InventoryItemUseReplied> Uses { get; } = new(build + "/game");
        internal Sender<InventoryReplaced> Inventories { get; } = new(build + "/game");
        internal Sender<ServerNotice> Notices { get; } = new(build + "/game");
        internal EquippedPush Equipment { get; } = new();
        internal Session Player { get; } = new(build);
        internal RoomHost Rooms { get; } = new(build);
        internal int BaseCalls { get; private set; }
        internal string Id(string suffix) => build + "." + suffix;
        internal ValueTask<bool> FeedAsync(IMessageRouter router, CancellationToken ct = default) => router.DispatchAsync(Protocol, Player,
            new InventoryItemUseRequested(PetTestData.NormalFood, 1, PetTestData.PetId.ToString(CultureInfo.InvariantCulture)), ct);

        internal void Add(List<PluginRow> rows, List<PluginDescriptor> definitions)
        {
            var dataId = Id("data");
            var operationsId = Id("operations");
            rows.Add(Row(dataId, dataId, "store", isolation: [typeof(IClientDataStore)]));
            rows.Add(Row(Id("loadouts"), "inventory.loadouts", dataId,
                new Dictionary<string, object?> { ["build_key"] = build },
                [typeof(IInventoryBuildScope), typeof(IInventoryRepository), typeof(IPlayerInventoryState)]));
            rows.Add(Row(Id("entitlements"), "pets.entitlements", Id("loadouts"), Options(), [typeof(IPetEntitlements)]));
            rows.Add(Row(operationsId, operationsId, Id("entitlements"), isolation:
            [
                typeof(IEquippedViewPush), typeof(IInboundMessageBinding<InventoryItemUseRequested>),
                typeof(IMessageSender<InventoryItemUseReplied>), typeof(IMessageSender<InventoryReplaced>), typeof(IMessageSender<PetEvolved>),
                typeof(IGameText), typeof(IRoomPolicyHost), typeof(IMessageSender<ServerNotice>),
            ]));
            rows.Add(Row(Id("pets"), "pets", operationsId, Options()));
            definitions.Add(Fixture(new(dataId) { Provides = [typeof(IClientDataStore)] }, context =>
                context.Effect(context.Services.Register<IClientDataStore>(CatalogData(hasCharacter29)))));
            definitions.Add(Fixture(new(operationsId)
            {
                Requires = [typeof(IMessageRouter), typeof(IInventoryBuildScope), typeof(IInventoryRepository), typeof(IPetEntitlements)],
                Provides = [typeof(IEquippedViewPush), typeof(IGameText)],
                ProvidesMany = [typeof(IInboundMessageBinding<InventoryItemUseRequested>), typeof(IMessageSender<InventoryItemUseReplied>),
                    typeof(IMessageSender<InventoryReplaced>), typeof(IMessageSender<PetEvolved>), typeof(IRoomPolicyHost),
                    typeof(IMessageSender<ServerNotice>)],
            }, context =>
            {
                Scope = context.Services.Get<IInventoryBuildScope>().Scope;
                Entitlements = context.Services.Get<IPetEntitlements>();
                Inventory = context.Services.Get<IInventoryRepository>();
                context.Effect(context.Services.Register<IEquippedViewPush>(Equipment));
                context.Effect(context.Services.Register<IGameText>(new Texts(Build)));
                context.Effect(context.Services.RegisterMany<IRoomPolicyHost>(Rooms));
                context.Effect(context.Services.RegisterMany<IMessageSender<ServerNotice>>(Notices));
                context.Effect(context.Services.RegisterMany<IInboundMessageBinding<InventoryItemUseRequested>>(new Binding(Protocol)));
                context.Effect(context.Services.RegisterMany<IMessageSender<InventoryItemUseReplied>>(Uses));
                context.Effect(context.Services.RegisterMany<IMessageSender<InventoryReplaced>>(Inventories));
                context.Effect(context.Services.RegisterMany<IMessageSender<PetEvolved>>(new Sender<PetEvolved>(Protocol)));
                context.Effect(context.Services.Get<IMessageRouter>().For(context).On<InventoryItemUseRequested>(Protocol,
                    (_, _, _) => { BaseCalls++; return ValueTask.CompletedTask; }));
            }));
        }

        private Dictionary<string, object?> Options() => new()
        {
            ["enabled"] = true, ["build_key"] = build, ["protocol"] = Protocol, ["client_utc_offset_minutes"] = 0,
            ["initial_fullness"] = 0,
        };
    }

    private static PluginRow Row(string id, string plugin, string? parent = null,
        IReadOnlyDictionary<string, object?>? config = null, Type[]? isolation = null) => new()
    {
        Id = id, Plugin = plugin, ParentId = parent, Config = config, Source = "pet-canonical-scope-test",
        IsolatedServices = isolation?.Select(type => type.FullName!).ToArray() ?? [], Lifecycle = new() { EffectCapacity = 32 },
    };
    private static PluginDescriptor Native<T>(string id) where T : IPlugin => new(id, null, typeof(T), "pet-canonical-scope-test", null);
    private static PluginDescriptor Fixture(PluginAttribute declaration, Action<IPluginContext> activate) =>
        new(declaration, new Factory(declaration, activate), "pet-canonical-scope-test", "1.0.0", Versions);

    private static Data CatalogData(bool hasCharacter29)
    {
        var catalog = PetTestData.Catalog();
        List<string[]> items = [["itemId", "function", "parameter", "subCategory"]];
        items.AddRange(catalog.Items.Values.OrderBy(item => item.ItemId).Select(item => new[]
        {
            Number(item.ItemId), Number(item.Function), Number(item.Parameter), item.Category == 0 ? "" : "pet",
        }));
        items.Add(["300029", "0", "0", ""]);
        List<string[]> skills = [["index", "value", "category", "grade", "weight", "buffIndex_1", "buffIndex_2", "buffIndex_3", "buffIndex_4"]];
        skills.AddRange(catalog.Skills.Values.Select(skill => new[]
            { skill.Selector, skill.Value, skill.Category, skill.Grade, skill.Weight }.Concat(skill.BuffIndices).Select(Number).ToArray()));
        List<string[]> buffs = [["buffIndex", "buffName", "randomGroup", "min", "max", "conv"]];
        buffs.AddRange(catalog.Buffs.Values.Select(buff => new[]
        {
            Number(buff.BuffIndex), buff.BuffName, Number(buff.RandomGroup), buff.RawMinimum.ToString(CultureInfo.InvariantCulture),
            buff.RawMaximum.ToString(CultureInfo.InvariantCulture), buff.Conversion.ToString(CultureInfo.InvariantCulture),
        }));
        return new(new Dictionary<string, byte[]>
        {
            ["Config/ShopItem2.xml"] = Workbook(new Dictionary<string, IEnumerable<string[]>>
            {
                ["ShopItem"] = items, ["SubCategory"] = new string[][] { ["id", "name"], ["504", "pet"] },
                ["ItemAttr"] = skills, ["RandomTable"] = buffs,
            }),
            ["Config/FighterConfig.xml"] = Workbook(new Dictionary<string, IEnumerable<string[]>>
            {
                ["FighterConfig"] = new string[][] { ["[Field]", "", "1", "29"], ["name", "", "Dao", hasCharacter29 ? "Rhea" : ""] },
            }),
        });
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
    private static byte[] Workbook(IReadOnlyDictionary<string, IEnumerable<string[]>> sheets)
    {
        XNamespace ns = "urn:schemas-microsoft-com:office:spreadsheet";
        return Encoding.UTF8.GetBytes(new XDocument(new XElement(ns + "Workbook", sheets.Select(sheet =>
            new XElement(ns + "Worksheet", new XAttribute(ns + "Name", sheet.Key), new XElement(ns + "Table",
                sheet.Value.Select(row => new XElement(ns + "Row", row.Select(cell => new XElement(ns + "Cell",
                    new XElement(ns + "Data", cell)))))))))).ToString());
    }

    private sealed class Data(Dictionary<string, byte[]> files) : IClientDataStore
    {
        /// <summary>Checks whether the fixture contains the requested client workbook path.</summary>
        public bool Exists(string path) => files.ContainsKey(path);
        /// <summary>Returns the fixture workbook bytes for the requested path and rejects an absent dictionary entry.</summary>
        public byte[] Read(string path) => files[path];
    }

    private sealed class RecordingMutations(IPetMutationStore store) : IPetMutationStore
    {
        internal List<PetMutationCommand> Commands { get; } = [];
        /// <summary>Records the mutation command before delegating the inventory-list write to the supplied store.</summary>
        public ValueTask<IReadOnlyList<InventorySlotRecord>?> MutatePetAsync(PetMutationCommand command, CancellationToken ct)
        { lock (Commands) Commands.Add(command); return store.MutatePetAsync(command, ct); }
        /// <summary>Records the mutation command before delegating the complete inventory snapshot write to the supplied store.</summary>
        public ValueTask<PetInventorySnapshot?> MutatePetSnapshotAsync(PetMutationCommand command, CancellationToken ct)
        { lock (Commands) Commands.Add(command); return store.MutatePetSnapshotAsync(command, ct); }
        /// <summary>Records the mutation command before delegating the write and conversion-result request to the supplied store.</summary>
        public ValueTask<PetMutationResult?> MutatePetWithResultAsync(PetMutationCommand command, CancellationToken ct)
        { lock (Commands) Commands.Add(command); return store.MutatePetWithResultAsync(command, ct); }
    }

    private sealed class Binding(string protocol) : IInboundMessageBinding<InventoryItemUseRequested>
    {
        /// <summary>Binds inventory-use requests to the game protocol of this fixture branch.</summary>
        public string Protocol => protocol;
    }

    private sealed class Sender<T>(string protocol) : IMessageSender<T> where T : IMessage
    {
        /// <summary>Associates recorded typed publications with the game protocol of this fixture branch.</summary>
        public string Protocol => protocol;
        internal List<T> Messages { get; } = [];
        internal Func<CancellationToken, ValueTask>? AfterSend { get; set; }
        /// <summary>Records a typed message after checking cancellation, then awaits the optional publication barrier.</summary>
        public async ValueTask SendAsync(ISession session, T message, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            lock (Messages) Messages.Add(message);
            if (AfterSend is { } afterSend) await afterSend(ct);
        }
    }

    private sealed class EquippedPush : IEquippedViewPush
    {
        internal List<int[]> Selections { get; } = [];
        internal Func<CancellationToken, ValueTask>? AfterSend { get; set; }
        /// <summary>Copies the published equipment selection after checking cancellation, then awaits the optional publication barrier.</summary>
        public async ValueTask SendAsync(ISession session, int userNum, int characterId, IReadOnlyList<int> equippedIds, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            lock (Selections) Selections.Add(equippedIds.ToArray());
            if (AfterSend is { } afterSend) await afterSend(ct);
        }
    }

    private sealed class PublicationPause
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Exception? Failure { get; set; }
        internal void Release() => _released.TrySetResult();
        internal async ValueTask WaitAsync(CancellationToken ct)
        {
            Entered.TrySetResult();
            await _released.Task.WaitAsync(ct);
            if (Failure is { } failure) throw failure;
        }
    }

    private sealed class Texts(string build) : IGameText
    {
        /// <inheritdoc />
        public string Format(string region, string key, IReadOnlyDictionary<string, string>? arguments = null) => build + ":" + key;
        /// <inheritdoc />
        public bool TryFormat(string region, string key, out string text, IReadOnlyDictionary<string, string>? arguments = null)
        { text = Format(region, key, arguments); return true; }
    }

    private sealed class RoomHost(string build) : IRoomPolicyHost
    {
        private readonly object _gate = new();
        private readonly List<IRoomPolicy> _policies = [];
        private readonly PetCanonicalScopeHostTests.Session _session = new(build);
        private RoomPolicyRoom? _room;
        private bool _held;
        private bool _automatic;
        private int _started;
        private int _resumes;
        internal int PolicyCount { get { lock (_gate) return _policies.Count; } }
        internal int StartedCount => Volatile.Read(ref _started);
        internal int ResumeCount => Volatile.Read(ref _resumes);
        internal TaskCompletionSource Resumed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <inheritdoc />
        public string Protocol => build + "/game";
        /// <inheritdoc />
        public string ClientBuild => build;

        internal void EnterWaiting()
        {
            lock (_gate)
            {
                _room = new(Guid.NewGuid(), 1, 4, false, true, [new(1, 0, 1, "host", 300001, [], true)]);
                _held = false;
            }
        }

        /// <inheritdoc />
        public IDisposable Register(IRoomPolicy policy)
        {
            lock (_gate) _policies.Add(policy);
            return new Registration(() => { lock (_gate) _policies.Remove(policy); });
        }

        /// <inheritdoc />
        public RoomPolicyRoom? Find(long sessionId)
        {
            lock (_gate) return sessionId == 1 ? _room : null;
        }

        /// <inheritdoc />
        public ISession? Session(long sessionId) => Find(sessionId) is null ? null : _session;

        internal async ValueTask<bool> StartAsync(bool automatic = false, CancellationToken ct = default)
        {
            IRoomPolicy[] holders;
            RoomPolicyRoom snapshot;
            lock (_gate)
            {
                ct.ThrowIfCancellationRequested();
                snapshot = _room ?? throw new InvalidOperationException("The fixture has no waiting room.");
                if (!snapshot.Waiting) return false;
                snapshot = snapshot with { AutomaticStart = automatic };
                holders = _policies.Where(policy => policy.EvaluateStart(snapshot, 1) == RoomStartDecision.Hold).ToArray();
                if (holders.Length == 0)
                {
                    _room = snapshot with { Waiting = false };
                    _held = false;
                    Interlocked.Increment(ref _started);
                    return false;
                }
                _held = true;
                _automatic = automatic;
            }
            foreach (var policy in holders) await policy.OnStartHeldAsync(snapshot, 1, ct);
            return true;
        }

        /// <inheritdoc />
        public async ValueTask<bool> ResumeHeldStartAsync(Guid instance, long requesterSessionId, CancellationToken ct)
        {
            bool automatic;
            lock (_gate)
            {
                ct.ThrowIfCancellationRequested();
                if (!_held || _room is not { Waiting: true } room || room.Instance != instance || requesterSessionId != 1)
                    return false;
                automatic = _automatic;
                Interlocked.Increment(ref _resumes);
            }
            try { await StartAsync(automatic, ct); return true; }
            finally { Resumed.TrySetResult(); }
        }
    }

    private sealed class Registration(Action remove) : IDisposable
    {
        private Action? _remove = remove;
        /// <summary>Removes one hosted test registration at most once.</summary>
        public void Dispose() => Interlocked.Exchange(ref _remove, null)?.Invoke();
    }

    private sealed class Factory(PluginAttribute declaration, Action<IPluginContext> activate) : IPluginExecutionFactory
    {
        /// <summary>Leaves independent fixture activations without a shared reload coordination key.</summary>
        public string? CoordinationKey => null;
        /// <summary>Creates a fresh activation wrapper around the fixture declaration and activation delegate.</summary>
        public ValueTask<IPluginExecution> CreateAsync(bool reload, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IPluginExecution>(new Execution(declaration, activate));
    }

    private sealed class Execution(PluginAttribute declaration, Action<IPluginContext> activate) : IPluginExecution
    {
        /// <summary>Exposes the service declaration used to register this fixture with the real plugin host.</summary>
        public PluginAttribute Declaration => declaration;
        /// <summary>Reports the fixed fixture version used by the plugin host.</summary>
        public string Version => "1.0.0";
        /// <summary>Reports the loaded abstraction and contract assembly versions used by the fixture.</summary>
        public IReadOnlyDictionary<string, string> ContractVersions => Versions;
        /// <summary>Uses delegate activation without exposing a concrete plugin implementation type.</summary>
        public Type? ImplementationType => null;
        /// <summary>Runs the fixture activation delegate against the host-provided plugin context.</summary>
        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
        { activate(context); return ValueTask.CompletedTask; }
        /// <summary>Completes without additional cleanup because the activation context owns the fixture effects.</summary>
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Session(string build) : ISessionWithFeatures
    {
        /// <summary>Identifies the fixture branch's sole game session.</summary>
        public long Id => 1;
        /// <summary>Supplies an inert connection for the fixture branch's typed-message session.</summary>
        public IConnection Connection { get; } = new Connection();
        /// <summary>Derives the client region and numeric version from the branch's configured build identifier.</summary>
        public ClientProfile Profile { get; } = new(build, build[..2], int.Parse(build[3..], CultureInfo.InvariantCulture), 0, 0, build);
        /// <summary>Provides the authenticated main-menu player state for account one.</summary>
        public ISessionFeatures Features { get; } = TestSessionFeatures.WithPlayer(1);
        /// <summary>Keeps session-local values separate for each fixture branch.</summary>
        public ConcurrentDictionary<string, object> Items { get; } = new();
        /// <summary>Fails any raw packet send so hosted pet operations must use their registered typed senders.</summary>
        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct) =>
            throw new InvalidOperationException("The pet plugin must publish typed messages.");
    }

    private sealed class Connection : IConnection
    {
        /// <summary>Identifies the inert connection attached to the branch's sole session.</summary>
        public long Id => 1;
        /// <summary>Supplies a loopback endpoint without opening a network socket.</summary>
        public EndPoint RemoteEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 1);
        /// <summary>Rejects network input because these hosted tests dispatch typed requests directly.</summary>
        public PipeReader Input => throw new NotSupportedException();
        /// <summary>Rejects network output because the fixture records typed publications.</summary>
        public PipeWriter Output => throw new NotSupportedException();
        /// <summary>Provides connection-local storage without sharing session state between fixture branches.</summary>
        public ConcurrentDictionary<string, object> Items { get; } = new();
        /// <summary>Keeps the inert connection open throughout hosted scope operations.</summary>
        public bool IsClosed => false;
        /// <summary>Accepts close requests without changing the inert connection's always-open state.</summary>
        public ValueTask CloseAsync(string reason) => ValueTask.CompletedTask;
    }
}
