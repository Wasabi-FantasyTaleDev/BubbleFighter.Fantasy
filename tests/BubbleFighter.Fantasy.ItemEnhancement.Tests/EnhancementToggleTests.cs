using System.Reflection;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Enhancement;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Party;
using BubbleFighter.Fantasy.Equipment.Handlers;
using BubbleFighter.Fantasy.Equipment.Sessions;
using BubbleFighter.Fantasy.InventoryView;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.ItemEnhancement.Tests;

/// <summary>Preserves durable equipment and earned effects when Article allocation and the enhancement economy are disabled.</summary>
public sealed class EnhancementToggleTests
{
    /// <summary>Previously initialized weapons and clothes remain selectable through the actual equipment plugin services after the economy is turned off.</summary>
    [Theory]
    [InlineData(202020, false, EnhancementPluginFixture.WeaponSelector)]
    [InlineData(202020, true, EnhancementPluginFixture.WeaponSelector)]
    [InlineData(300201, false, EnhancementPluginFixture.ClothingSelector)]
    [InlineData(300201, true, EnhancementPluginFixture.ClothingSelector)]
    public async Task PersistedArticlesRemainEquipableAfterDisablingEnhancement(int itemId, bool enhanced, int selector)
    {
        await using var fixture = await EnhancementPluginFixture.CreateAsync(equipment: true);
        var articles = fixture.Articles();
        await new EnhancementArticlesPlugin().ActivateAsync(articles, default);
        var economy = fixture.Economy();
        await new ItemEnhancementPlugin().ActivateAsync(economy, default);
        var initialized = await fixture.PrepareAsync();
        Assert.NotNull(initialized);
        var key = initialized.CharacterBag.Single(row => row.ItemId == itemId).ArticleIdentity!.ArticleKey;
        if (enhanced)
        {
            await fixture.Router.DispatchAsync(EnhancementPluginFixture.Protocol, fixture.Session,
                new LabOperationRequested(LabOperation.Enchant, [checked((long)key)], 0), default);
            Assert.True(Assert.Single(fixture.Completed).Succeeded);
        }
        var beforeDisable = await fixture.Store.ReadEnhancementInventoryAsync(1, fixture.Scope, default);
        var original = beforeDisable.CharacterBag.Single(row => row.ItemId == itemId);
        var metadataCalls = fixture.Metadata.Calls.Count;
        await economy.UnwindAsync();
        await articles.UnwindAsync();
        await new EnhancementArticlesPlugin().ActivateAsync(fixture.Articles(new() { ["initialize_articles"] = false }), default);
        await new ItemEnhancementPlugin().ActivateAsync(fixture.Economy(new() { ["enabled"] = false }), default);
        Assert.Empty(fixture.Router.Handlers);
        Assert.False(await fixture.Router.DispatchAsync(EnhancementPluginFixture.Protocol, fixture.Session,
            new LabOperationRequested(LabOperation.Enchant, [checked((long)key)], 0), default));
        Assert.False(await fixture.Router.DispatchAsync(EnhancementPluginFixture.Protocol, fixture.Session,
            new ScienceLabConfigurationRequested(), default));

        var resolver = fixture.Services.Get<IEquipmentArticleResolver>();
        Assert.Equal(original, await resolver.ResolveAsync(1, key, default));
        Assert.Empty(resolver.CreateInitialization(1, DateTimeOffset.UtcNow).EligibleItemIds);
        var prepared = await fixture.PrepareAsync();
        Assert.NotNull(prepared);
        Assert.Equal(beforeDisable.CharacterBag, prepared.CharacterBag);
        Assert.Equal(beforeDisable.EquippedBag, prepared.EquippedBag);
        await AppendLateItemAsync(fixture);
        var views = await MountEquipmentAsync(fixture);
        var replies = new Capture<EquipmentChangeReplied>();
        using var equipped = new EquippedItemScope(4, 8);
        var handler = new EquipmentHandlers(equipped, fixture.Store, new ForbiddenPartialPush(), NullLogger.Instance,
            replies, fixture.Services.Get<IEquipmentSelection>(), fixture.Services.Get<IEquippedViewPush>(), fixture.Events,
            inventories: fixture.Services.GetAll<IMessageSender<InventoryReplaced>>()
                .Single(sender => sender.Protocol == EnhancementPluginFixture.Protocol), articles: resolver);

        foreach (var worn in new[] { true, false })
        {
            var request = new EquipmentChangeRequested(checked((long)key), null, worn);
            await handler.HandleAsync(fixture.Session, request, default);
            Assert.True(replies.Messages[^1].Succeeded);
            Assert.Same(request, replies.Messages[^1].Request);
            var committed = fixture.Inventories[^1];
            Assert.True(committed.IsCompleteSnapshot);
            foreach (var bag in new[] { committed.CharacterBag, committed.EquippedBag })
            {
                var target = bag.Single(row => row.ItemId == itemId);
                Assert.Equal(worn, (target.Flags & InventorySlotRecord.WornFlagBit) != 0);
                Assert.Equal(original.ArticleIdentity, target.ArticleIdentity);
                Assert.Equal(original.Enhancements, target.Enhancements);
                Assert.Null(bag.Single(row => row.ItemId == 202021).ArticleIdentity);
                Assert.Equal(beforeDisable.CharacterBag.Single(row => row.ItemId == 190111).Quantity,
                    bag.Single(row => row.ItemId == 190111).Quantity);
            }
            var targetRow = committed.CharacterBag.Single(row => row.ItemId == itemId);
            var presentation = await fixture.PresentationAsync(targetRow);
            if (enhanced)
                Assert.Equal(checked((uint)selector), Assert.Single(presentation.Attributes).Kind);
            else
                Assert.Empty(presentation.Attributes);
            var attributes = views.Messages[^1].ItemAttributes;
            Assert.NotNull(attributes);
            if (enhanced && worn)
                Assert.Equal(checked((uint)selector), Assert.Single(attributes).Kind);
            else
                Assert.Empty(attributes);
            var saved = await fixture.Store.ReadEnhancementInventoryAsync(1, fixture.Scope, default);
            Assert.Equal(committed.CharacterBag, saved.CharacterBag);
            Assert.Equal(committed.EquippedBag, saved.EquippedBag);
        }
        Assert.Equal(2, replies.Messages.Count);
        Assert.Equal(metadataCalls, fixture.Metadata.Calls.Count);
        Assert.Equal(0, fixture.Enhancements.Initializations);
    }

    /// <summary>Read-only Article support neither initializes fresh inventory nor requires the metadata mutation provider.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("metadata-store")]
    public async Task AllocationDisabledLeavesFreshInventoryUnchanged(string? missing)
    {
        Assert.False(new EnhancementArticleOptions().InitializeArticles);
        await using var fixture = await EnhancementPluginFixture.CreateAsync(messages: false, missing: missing);
        var original = await fixture.Store.ReadEnhancementInventoryAsync(1, fixture.Scope, default);
        await new EnhancementArticlesPlugin().ActivateAsync(fixture.Articles(new() { ["initialize_articles"] = false }), default);
        var resolver = fixture.Services.Get<IEquipmentArticleResolver>();
        var initialization = resolver.CreateInitialization(1, DateTimeOffset.UtcNow);
        initialization.Validate();
        Assert.Empty(initialization.EligibleItemIds);
        Assert.Null(await fixture.PrepareAsync());
        var inputs = new InventoryMetadataPreparationCommand(1, fixture.Scope);
        var actual = await fixture.Events.WaterfallAsync<InventoryMetadataInputsRequested, InventoryMetadataPreparationCommand>(
            new(1, fixture.Scope, DateTimeOffset.UtcNow), _ => ValueTask.FromResult(inputs), default);
        Assert.Same(inputs, actual);
        Assert.Null(await fixture.PublishAsync(new(EnhancementPluginFixture.Protocol, 1,
            original.CharacterBag, [], null, false)));
        var complete = await fixture.PublishAsync(new(EnhancementPluginFixture.Protocol, 1,
            original.CharacterBag, original.EquippedBag, null, true));
        Assert.NotNull(complete);
        Assert.Same(original.CharacterBag, complete.CharacterBag);
        Assert.Same(original.EquippedBag, complete.EquippedBag);
        Assert.Empty(fixture.Metadata.Calls);
        Assert.Equal(0, fixture.Enhancements.Initializations);
        var saved = await fixture.Store.ReadEnhancementInventoryAsync(1, fixture.Scope, default);
        Assert.Equal(original.CharacterBag, saved.CharacterBag);
        Assert.Equal(original.EquippedBag, saved.EquippedBag);
        Assert.All(saved.CharacterBag.Concat(saved.EquippedBag), row => Assert.Null(row.ArticleIdentity));
    }

    /// <summary>A historical Article delta still resolves to one complete read-only snapshot after allocation is disabled.</summary>
    [Fact]
    public async Task DisabledAllocationCompletesPersistedArticlePublicationWithoutAllocatingNewItems()
    {
        await using var fixture = await EnhancementPluginFixture.CreateAsync(messages: false);
        var articles = fixture.Articles();
        await new EnhancementArticlesPlugin().ActivateAsync(articles, default);
        var initialized = await fixture.PrepareAsync();
        Assert.NotNull(initialized);
        await articles.UnwindAsync();
        await new EnhancementArticlesPlugin().ActivateAsync(fixture.Articles(new() { ["initialize_articles"] = false }), default);
        await AppendLateItemAsync(fixture);
        var before = await fixture.Store.ReadEnhancementInventoryAsync(1, fixture.Scope, default);
        var reads = fixture.Enhancements.Reads;
        var result = await fixture.PublishAsync(new(EnhancementPluginFixture.Protocol, 1, [], [],
            [initialized.CharacterBag.Single(row => row.ItemId == 202020)], false));
        Assert.NotNull(result);
        Assert.Equal(before.CharacterBag, result.CharacterBag);
        Assert.Equal(before.EquippedBag, result.EquippedBag);
        Assert.Null(result.CharacterBag.Single(row => row.ItemId == 202021).ArticleIdentity);
        Assert.Equal(reads + 1, fixture.Enhancements.Reads);
        Assert.Single(fixture.Metadata.Calls);
        Assert.Equal(0, fixture.Enhancements.Initializations);
    }

    /// <summary>Complete transactions and another provider's atomic metadata result are never replaced by a later inventory read.</summary>
    [Fact]
    public async Task DisabledAllocationPreservesSuppliedAndAlreadyPreparedSnapshots()
    {
        await using var fixture = await EnhancementPluginFixture.CreateAsync(messages: false);
        var articles = fixture.Articles();
        await new EnhancementArticlesPlugin().ActivateAsync(articles, default);
        var initialized = await fixture.PrepareAsync();
        Assert.NotNull(initialized);
        await articles.UnwindAsync();
        await new EnhancementArticlesPlugin().ActivateAsync(fixture.Articles(new() { ["initialize_articles"] = false }), default);
        await AppendLateItemAsync(fixture);
        var reads = fixture.Enhancements.Reads;
        var supplied = await fixture.PublishAsync(new(EnhancementPluginFixture.Protocol, 1,
            initialized.CharacterBag, initialized.EquippedBag, null, true));
        Assert.NotNull(supplied);
        Assert.Same(initialized.CharacterBag, supplied.CharacterBag);
        Assert.Same(initialized.EquippedBag, supplied.EquippedBag);
        var previous = new InventoryReplaced(initialized.CharacterBag, initialized.EquippedBag) { IsCompleteSnapshot = true };
        using var publication = fixture.Events.Intercept<InventoryPublicationPreparing, InventoryReplaced?>((_, _, _) =>
            ValueTask.FromResult<InventoryReplaced?>(previous));
        using var metadata = fixture.Events.Intercept<InventoryMetadataPreparing, PetInventorySnapshot?>((_, _, _) =>
            ValueTask.FromResult<PetInventorySnapshot?>(initialized));
        Assert.Same(previous, await fixture.PublishAsync(new(EnhancementPluginFixture.Protocol, 1, [], [], null, false)));
        Assert.Same(initialized, await fixture.PrepareAsync());
        Assert.Equal(reads, fixture.Enhancements.Reads);
        Assert.Single(fixture.Metadata.Calls);
        Assert.Null((await fixture.Store.ReadEnhancementInventoryAsync(1, fixture.Scope, default))
            .CharacterBag.Single(row => row.ItemId == 202021).ArticleIdentity);
    }

    /// <summary>A disabled resolver row cannot silently accept a contradictory request to allocate new Article identities.</summary>
    [Fact]
    public async Task InitializationRequiresTheArticleProviderToBeEnabled()
    {
        await using var fixture = await EnhancementPluginFixture.CreateAsync(messages: false);
        var context = fixture.Articles(new() { ["enabled"] = false, ["initialize_articles"] = true });
        await Assert.ThrowsAsync<ConfigException>(() => new EnhancementArticlesPlugin().ActivateAsync(context, default).AsTask());
        Assert.False(fixture.Services.TryGet<IEquipmentArticleResolver>(out _));
        Assert.Empty(fixture.Metadata.Calls);
    }

    private static async ValueTask AppendLateItemAsync(EnhancementPluginFixture fixture)
    {
        foreach (var bag in new[] { InventoryBag.CharacterBag, InventoryBag.EquippedBag })
            await fixture.Store.AppendMissingItemsAsync(1, bag,
                [new InventorySlotRecord(1, bag, 100, 202021, 1, 0x40) { Lifetime = InventoryLifetime.Permanent }], default);
    }

    private static async ValueTask<Capture<LocalEquipmentViewUpdated>> MountEquipmentAsync(EnhancementPluginFixture fixture)
    {
        fixture.Setup.Effect(fixture.Services.Register<IInventoryRepository>(fixture.Store));
        fixture.Setup.Effect(fixture.Services.Register<IInventoryLoadoutStore>(fixture.Store));
        await MountAsync("packet-router", new PacketRouterPlugin());
        await MountAsync("opcode-registry", new OpcodeTableRegistryPlugin());
        fixture.Setup.Effect(fixture.Services.Get<IOpcodeTableRegistry>().Register(new OpcodeTable(fixture.Session.Profile, [])));
        var views = new Capture<LocalEquipmentViewUpdated>();
        fixture.Setup.Effect(fixture.Services.RegisterMany<IMessageSender<LocalEquipmentViewUpdated>>(views));
        fixture.Setup.Effect(fixture.Services.RegisterMany<IMessageSender<RemoteEquipmentViewUpdated>>(new Capture<RemoteEquipmentViewUpdated>()));
        await MountAsync("inventoryview", new InventoryViewPlugin(), new()
        {
            ["enabled"] = true, ["publish_equipped_view_push"] = true,
            ["protocol"] = EnhancementPluginFixture.Protocol, ["client_profile"] = fixture.Scope.Key,
        });
        return views;

        async ValueTask MountAsync(string id, IPlugin plugin, Dictionary<string, object?>? config = null)
        {
            var context = fixture.Context(id, config);
            context.BindServices(plugin.GetType().GetCustomAttribute<PluginAttribute>()!);
            await plugin.ActivateAsync(context, default);
        }
    }

    private sealed class Capture<T> : IMessageSender<T> where T : IMessage
    {
        internal List<T> Messages { get; } = [];
        /// <inheritdoc />
        public string Protocol => EnhancementPluginFixture.Protocol;
        /// <inheritdoc />
        public ValueTask SendAsync(ISession session, T message, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); Messages.Add(message); return ValueTask.CompletedTask; }
    }

    private sealed class ForbiddenPartialPush : IInventoryPushService
    {
        /// <inheritdoc />
        public ValueTask PushInventoryAsync(ISession session, long userNum, CancellationToken ct) =>
            throw new InvalidOperationException("Equipment must publish its committed complete inventory.");
        /// <inheritdoc />
        public ValueTask PushPurchaseAsync(ISession session, long userNum, IReadOnlyList<InventorySlotRecord> grants, CancellationToken ct) =>
            throw new InvalidOperationException("Equipment must not purchase inventory.");
        /// <inheritdoc />
        public ValueTask PushSlotUpdateAsync(ISession session, InventorySlotRecord slot, CancellationToken ct) =>
            throw new InvalidOperationException("Article equipment must not publish a partial inventory.");
    }
}
