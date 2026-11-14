using System.Reflection;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Enhancement;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Party;
using BubbleFighter.Fantasy.Contracts.Pets;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Kernel;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.ItemEnhancement.Tests;

/// <summary>Checks the separately mounted Article metadata and explicit enhancement economy over real plugin contexts.</summary>
public sealed class EnhancementPluginActivationTests
{
    /// <summary>Both disabled rows need no providers and leave metadata preparation and presentation untouched.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisabledRowsPublishNoServicesOrEffects(bool economy)
    {
        var services = new ServiceRegistry();
        var events = new EventBus(NullLogger.Instance);
        IPlugin plugin = economy ? new ItemEnhancementPlugin() : new EnhancementArticlesPlugin();
        var context = new PluginContext(new() { Id = "disabled", Plugin = "disabled", Source = "tests" },
            services, events, NullLogger.Instance);
        context.BindServices(plugin.GetType().GetCustomAttribute<PluginAttribute>()!);
        try
        {
            await plugin.ActivateAsync(context, default);
            Assert.False(services.TryGet<IEquipmentArticleResolver>(out _));
            var prepared = await events.WaterfallAsync<InventoryMetadataPreparing, PetInventorySnapshot?>(
                new("enhancement-test", 1), _ => ValueTask.FromResult<PetInventorySnapshot?>(null), default);
            Assert.Null(prepared);
            var row = new InventorySlotRecord(1, InventoryBag.CharacterBag, 1, 202020, 1, 0);
            var presentation = await events.WaterfallAsync<InventoryPresentationRequested, InventoryItemPresentation>(
                new(EnhancementPluginFixture.Protocol, row), _ => ValueTask.FromResult(InventoryItemPresentation.Empty), default);
            Assert.Same(InventoryItemPresentation.Empty, presentation);
        }
        finally { await context.UnwindAsync(); }
    }

    /// <summary>Article metadata operates without any economic policy, message router or pet provider and unloads cleanly.</summary>
    [Fact]
    public async Task MetadataOnlyActivationPreparesBothBagsWithoutAnEconomy()
    {
        await using var fixture = await EnhancementPluginFixture.CreateAsync(messages: false);
        var context = fixture.Articles();
        await new EnhancementArticlesPlugin().ActivateAsync(context, default);
        Assert.False(fixture.Services.TryGet<IPetEntitlements>(out _));
        var resolver = fixture.Services.Get<IEquipmentArticleResolver>();
        var snapshot = await fixture.PrepareAsync();
        Assert.NotNull(snapshot);
        var command = Assert.Single(fixture.Metadata.Calls);
        Assert.Null(command.PetInitialization);
        Assert.Equal(new[] { 202020, 202021, 300201 }, command.ArticleInitialization!.EligibleItemIds.Order());
        foreach (var itemId in new[] { 202020, 300201 })
        {
            var character = snapshot.CharacterBag.Single(row => row.ItemId == itemId);
            var equipped = snapshot.EquippedBag.Single(row => row.ItemId == itemId);
            Assert.NotNull(character.ArticleIdentity);
            Assert.Equal(character.ArticleIdentity, equipped.ArticleIdentity);
            Assert.Equal(character, await resolver.ResolveAsync(1, character.ArticleIdentity.ArticleKey, default));
        }
        Assert.All(snapshot.CharacterBag.Where(row => row.ItemId is not (202020 or 300201)), row => Assert.Null(row.ArticleIdentity));
        var before = snapshot.CharacterBag;
        await context.UnwindAsync();
        Assert.False(fixture.Services.TryGet<IEquipmentArticleResolver>(out _));
        Assert.Null(await fixture.PrepareAsync());
        Assert.Single(fixture.Metadata.Calls);
        Assert.Equal(before, (await fixture.Store.ReadEnhancementInventoryAsync(1, fixture.Scope, default)).CharacterBag);
    }

    /// <summary>Authenticated laboratory routes use configured materials and publish earned native attributes without pets.</summary>
    [Theory]
    [InlineData(202020, EnhancementPluginFixture.WeaponSelector, ItemEnhancementKind.Weapon)]
    [InlineData(300201, EnhancementPluginFixture.ClothingSelector, ItemEnhancementKind.Clothing)]
    public async Task ActivatedEconomyPublishesCompleteInventoryAndNativePresenceAttributes(int itemId, int selector, ItemEnhancementKind kind)
    {
        await using var fixture = await EnhancementPluginFixture.CreateAsync();
        await new EnhancementArticlesPlugin().ActivateAsync(fixture.Articles(), default);
        var initial = await fixture.PrepareAsync();
        Assert.NotNull(initial);
        var key = initial.CharacterBag.Single(row => row.ItemId == itemId).ArticleIdentity!.ArticleKey;
        var economy = fixture.Economy();
        await new ItemEnhancementPlugin().ActivateAsync(economy, default);
        Assert.False(fixture.Services.TryGet<IPetEntitlements>(out _));
        Assert.Equal(2, fixture.Router.Handlers.Count);
        Assert.True(await fixture.Router.DispatchAsync(EnhancementPluginFixture.Protocol, fixture.Session,
            new ScienceLabConfigurationRequested(), default));
        var configuration = Assert.Single(fixture.Configurations);
        Assert.Equal([190111, 190106, 190107, 190108, 190109], configuration.EnchantMaterials);
        Assert.Equal(101745, configuration.AdditiveItemId);
        Assert.True(await fixture.Router.DispatchAsync(EnhancementPluginFixture.Protocol, fixture.Session,
            new LabOperationRequested(LabOperation.Enchant, [checked((long)key)], 0), default));

        Assert.Equal(["configuration", "inventory", "completed"], fixture.Order);
        Assert.True(Assert.Single(fixture.Completed).Succeeded);
        Assert.Equal([key], fixture.Completed[0].Targets);
        var inventory = Assert.Single(fixture.Inventories);
        Assert.True(inventory.IsCompleteSnapshot);
        Assert.Equal(inventory.CharacterBag, (await fixture.Store.ReadEnhancementInventoryAsync(1, fixture.Scope, default)).CharacterBag);
        var target = inventory.CharacterBag.Single(row => row.ItemId == itemId);
        Assert.Equal(kind, target.Enhancements!.Find(fixture.Scope.Key)!.Kind);
        Assert.Equal(7, inventory.CharacterBag.Single(row => row.ItemId == 190111).Quantity);
        Assert.All(inventory.CharacterBag.Where(row => row.ItemId is >= 190106 and <= 190109), row => Assert.Equal(6, row.Quantity));
        var complete = await fixture.PublishAsync(new(EnhancementPluginFixture.Protocol, 1,
            inventory.CharacterBag, inventory.EquippedBag, null, true));
        Assert.NotNull(complete);
        Assert.Same(inventory.CharacterBag, complete.CharacterBag);
        Assert.Single(fixture.Metadata.Calls);
        var deadline = DateTimeOffset.UtcNow.AddMinutes(1);
        var previous = new InventoryItemPresentation([new RoomItemAttribute(itemId, 901, [7, 9], 0, "")], deadline);
        var presentation = await fixture.PresentationAsync(target, previous);
        Assert.Equal(2, presentation.Attributes.Count);
        Assert.Equal(previous.Attributes[0], presentation.Attributes[0]);
        Assert.Equal(checked((uint)selector), presentation.Attributes[1].Kind);
        Assert.Equal(new uint[] { 0 }, presentation.Attributes[1].Values);
        Assert.Equal(deadline, presentation.RemainingUntil);
        Assert.Same(previous, await fixture.PresentationAsync(target, previous, "other/game"));

        await economy.UnwindAsync();
        Assert.Empty(fixture.Router.Handlers);
        Assert.True(fixture.Services.TryGet<IEquipmentArticleResolver>(out _));
        Assert.False(await fixture.Router.DispatchAsync(EnhancementPluginFixture.Protocol, fixture.Session,
            new ScienceLabConfigurationRequested(), default));
    }

    /// <summary>A complete committed publication does not initialize items purchased after that snapshot was taken.</summary>
    [Fact]
    public async Task CompletePublicationUsesTheSuppliedSnapshotWithoutAnotherReadOrMutation()
    {
        await using var fixture = await EnhancementPluginFixture.CreateAsync(messages: false);
        await new EnhancementArticlesPlugin().ActivateAsync(fixture.Articles(), default);
        var snapshot = await fixture.PrepareAsync();
        Assert.NotNull(snapshot);
        var late = new InventorySlotRecord(1, InventoryBag.CharacterBag, 100, 202021, 1, 0)
            { Lifetime = InventoryLifetime.Permanent };
        await fixture.Store.AppendMissingItemsAsync(1, InventoryBag.CharacterBag, [late], default);
        var published = await fixture.PublishAsync(new(EnhancementPluginFixture.Protocol, 1,
            snapshot.CharacterBag, snapshot.EquippedBag, null, true));
        Assert.NotNull(published);
        Assert.Same(snapshot.CharacterBag, published.CharacterBag);
        Assert.DoesNotContain(published.CharacterBag, row => row.ItemId == 202021);
        Assert.Single(fixture.Metadata.Calls);
        Assert.Null((await fixture.Store.ListSlotsAsync(1, InventoryBag.CharacterBag, 100, default))
            .Single(row => row.ItemId == 202021).ArticleIdentity);
    }

    /// <summary>Complete snapshots must already belong to the configured owner and catalog and carry eligible Article identities.</summary>
    [Theory]
    [InlineData("owner")]
    [InlineData("catalog")]
    [InlineData("identity")]
    public async Task InvalidCompletePublicationFailsWithoutInitializingAnything(string fault)
    {
        await using var fixture = await EnhancementPluginFixture.CreateAsync(messages: false);
        await new EnhancementArticlesPlugin().ActivateAsync(fixture.Articles(), default);
        var initial = await fixture.PrepareAsync();
        Assert.NotNull(initial);
        var rows = initial.CharacterBag.Select(row => row.ItemId != 202020 ? row : fault switch
        {
            "owner" => row with { UserNum = 2 },
            "catalog" => row with { ItemId = 999999 },
            _ => row with { ArticleIdentity = null },
        }).ToArray();
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.PublishAsync(new(EnhancementPluginFixture.Protocol,
            1, rows, initial.EquippedBag, null, true)).AsTask());
        Assert.Single(fixture.Metadata.Calls);
        Assert.Equal(initial.CharacterBag, (await fixture.Store.ReadEnhancementInventoryAsync(1, fixture.Scope, default)).CharacterBag);
    }

    /// <summary>A duplicate metadata input rejects preparation before storage, and unrelated build scopes remain untouched.</summary>
    [Fact]
    public async Task MetadataInputsRejectDuplicateProvidersAndRespectTheBuildScope()
    {
        await using var fixture = await EnhancementPluginFixture.CreateAsync(messages: false);
        await new EnhancementArticlesPlugin().ActivateAsync(fixture.Articles(), default);
        Assert.Null(await fixture.PrepareAsync("other-build"));
        using var duplicate = fixture.Events.Intercept<InventoryMetadataInputsRequested, InventoryMetadataPreparationCommand>(async (request, next, ct) =>
            (await next(ct)) with { ArticleInitialization = new(request.UserNum, request.Scope, [202020], request.Now) });
        await Assert.ThrowsAsync<ConfigException>(() => fixture.PrepareAsync().AsTask());
        Assert.Empty(fixture.Metadata.Calls);
        Assert.All((await fixture.Store.ReadEnhancementInventoryAsync(1, fixture.Scope, default)).CharacterBag,
            row => Assert.Null(row.ArticleIdentity));
    }

    /// <summary>Presence attributes reject inconsistent earned kinds and duplicate native selectors.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidEarnedAttributesFailWithoutChangingStoredInventory(bool duplicate)
    {
        await using var fixture = await EnhancementPluginFixture.CreateAsync(messages: false);
        await new EnhancementArticlesPlugin().ActivateAsync(fixture.Articles(), default);
        var snapshot = await fixture.PrepareAsync();
        Assert.NotNull(snapshot);
        var target = snapshot.CharacterBag.Single(row => row.ItemId == 202020) with
        {
            Enhancements = ItemEnhancements.Empty.With(fixture.Scope.Key,
                new(duplicate ? ItemEnhancementKind.Weapon : ItemEnhancementKind.Clothing, 1)),
        };
        var previous = duplicate
            ? new InventoryItemPresentation([new RoomItemAttribute(202020, EnhancementPluginFixture.WeaponSelector, [0], 0, "")], null)
            : InventoryItemPresentation.Empty;
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.PresentationAsync(target, previous).AsTask());
        Assert.Equal(snapshot.CharacterBag, (await fixture.Store.ReadEnhancementInventoryAsync(1, fixture.Scope, default)).CharacterBag);
    }

    /// <summary>Every enabled metadata dependency is required, and rollback removes even a resolver published before failure.</summary>
    [Theory]
    [InlineData("client-data")]
    [InlineData("scope")]
    [InlineData("enhancement-store")]
    [InlineData("metadata-store")]
    public async Task MissingMetadataDependenciesFailAndUnwind(string missing)
    {
        await using var fixture = await EnhancementPluginFixture.CreateAsync(messages: false, missing: missing);
        var context = fixture.Articles();
        await Assert.ThrowsAsync<ServiceUnavailableException>(() => new EnhancementArticlesPlugin().ActivateAsync(context, default).AsTask());
        await context.UnwindAsync();
        Assert.False(fixture.Services.TryGet<IEquipmentArticleResolver>(out _));
        Assert.Null(await fixture.PrepareAsync());
        Assert.Empty(fixture.Metadata.Calls);
    }

    /// <summary>Both laboratory requests need exactly one native binding for the configured protocol.</summary>
    [Theory]
    [InlineData("binding.operation", false)]
    [InlineData("binding.operation", true)]
    [InlineData("binding.configuration", false)]
    [InlineData("binding.configuration", true)]
    public async Task MissingOrDuplicateBindingsRejectEconomyActivation(string key, bool duplicate)
    {
        await using var fixture = await EnhancementPluginFixture.CreateAsync(missing: duplicate ? null : key, duplicate: duplicate ? key : null);
        await new EnhancementArticlesPlugin().ActivateAsync(fixture.Articles(), default);
        await Assert.ThrowsAsync<ConfigException>(() => new ItemEnhancementPlugin().ActivateAsync(fixture.Economy(), default).AsTask());
        Assert.Empty(fixture.Router.Handlers);
    }

    /// <summary>All outgoing laboratory and inventory messages must have a single unambiguous sender before routes mount.</summary>
    [Theory]
    [InlineData("sender.inventory", false)]
    [InlineData("sender.inventory", true)]
    [InlineData("sender.completed", false)]
    [InlineData("sender.completed", true)]
    [InlineData("sender.refused", false)]
    [InlineData("sender.refused", true)]
    [InlineData("sender.configuration", false)]
    [InlineData("sender.configuration", true)]
    public async Task MissingOrDuplicateSendersRejectEconomyActivation(string key, bool duplicate)
    {
        await using var fixture = await EnhancementPluginFixture.CreateAsync(missing: duplicate ? null : key, duplicate: duplicate ? key : null);
        await new EnhancementArticlesPlugin().ActivateAsync(fixture.Articles(), default);
        await Assert.ThrowsAsync<ConfigException>(() => new ItemEnhancementPlugin().ActivateAsync(fixture.Economy(), default).AsTask());
        Assert.Empty(fixture.Router.Handlers);
    }

    /// <summary>An enabled economy cannot invent an unstated debit or success policy.</summary>
    [Theory]
    [InlineData("success_basis_points")]
    [InlineData("additive_success_basis_points")]
    [InlineData("card_quantity")]
    [InlineData("kit_quantity")]
    [InlineData("additive_quantity")]
    public async Task MissingExplicitPolicyRejectsEconomyActivation(string key)
    {
        await using var fixture = await EnhancementPluginFixture.CreateAsync();
        await new EnhancementArticlesPlugin().ActivateAsync(fixture.Articles(), default);
        await Assert.ThrowsAsync<ConfigException>(() => new ItemEnhancementPlugin()
            .ActivateAsync(fixture.Economy(new() { [key] = null }), default).AsTask());
        Assert.Empty(fixture.Router.Handlers);
        Assert.Empty(fixture.Metadata.Calls);
    }

    /// <summary>The economic row cannot operate without the separately enabled Article resolver.</summary>
    [Fact]
    public async Task EconomyRequiresTheArticleProvider()
    {
        await using var fixture = await EnhancementPluginFixture.CreateAsync();
        await Assert.ThrowsAsync<ServiceUnavailableException>(() => new ItemEnhancementPlugin().ActivateAsync(fixture.Economy(), default).AsTask());
        Assert.Empty(fixture.Router.Handlers);
    }

    /// <summary>Inventory scope and count-based material rules are checked before the laboratory accepts requests.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MismatchedCatalogOrNonCountMaterialsCannotActivate(bool material)
    {
        await using var fixture = await EnhancementPluginFixture.CreateAsync();
        await new EnhancementArticlesPlugin().ActivateAsync(fixture.Articles(), default);
        var overrides = material ? new Dictionary<string, object?> { ["card_item_id"] = 202020 }
            : new Dictionary<string, object?> { ["build_key"] = "other-build" };
        await Assert.ThrowsAsync<ConfigException>(() => new ItemEnhancementPlugin().ActivateAsync(fixture.Economy(overrides), default).AsTask());
        Assert.Empty(fixture.Router.Handlers);
    }

    /// <summary>Configuration requests require authentication even though they perform no economic mutation.</summary>
    [Fact]
    public async Task AnonymousConfigurationRequestIsRejected()
    {
        await using var fixture = await EnhancementPluginFixture.CreateAsync();
        await new EnhancementArticlesPlugin().ActivateAsync(fixture.Articles(), default);
        await new ItemEnhancementPlugin().ActivateAsync(fixture.Economy(), default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Router.DispatchAsync(EnhancementPluginFixture.Protocol,
            new EnhancementPluginFixture.TestSession(0), new ScienceLabConfigurationRequested(), default).AsTask());
        Assert.Empty(fixture.Configurations);
        Assert.Empty(fixture.Metadata.Calls);
    }

    /// <summary>Unload drains canceled publication before the runtime guard is disposed, and replay cannot repeat the debit.</summary>
    [Fact]
    public async Task UnloadCancelsPendingPublicationAndPreservesCommittedOutcome()
    {
        await using var fixture = await EnhancementPluginFixture.CreateAsync();
        await new EnhancementArticlesPlugin().ActivateAsync(fixture.Articles(), default);
        var snapshot = await fixture.PrepareAsync();
        Assert.NotNull(snapshot);
        var key = checked((long)snapshot.CharacterBag.Single(row => row.ItemId == 202020).ArticleIdentity!.ArticleKey);
        var economy = fixture.Economy();
        await new ItemEnhancementPlugin().ActivateAsync(economy, default);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.BeforeInventory = async ct => { entered.TrySetResult(); await release.Task.WaitAsync(ct); };
        var pending = fixture.Router.DispatchAsync(EnhancementPluginFixture.Protocol, fixture.Session,
            new LabOperationRequested(LabOperation.Enchant, [key], 0), default).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await economy.UnwindAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        finally { release.TrySetResult(); }
        Assert.Empty(fixture.Router.Handlers);
        Assert.Empty(fixture.Inventories);
        Assert.Empty(fixture.Completed);
        var saved = await fixture.Store.ReadEnhancementInventoryAsync(1, fixture.Scope, default);
        Assert.Equal(ItemEnhancementKind.Weapon, saved.CharacterBag.Single(row => row.ItemId == 202020).Enhancements!.Find(fixture.Scope.Key)!.Kind);
        Assert.Equal(7, saved.CharacterBag.Single(row => row.ItemId == 190111).Quantity);
        await new ItemEnhancementPlugin().ActivateAsync(fixture.Economy(), default);
        await fixture.Router.DispatchAsync(EnhancementPluginFixture.Protocol, fixture.Session,
            new LabOperationRequested(LabOperation.Enchant, [key], 0), default);
        Assert.Single(fixture.Refused);
        Assert.Equal(saved.CharacterBag, (await fixture.Store.ReadEnhancementInventoryAsync(1, fixture.Scope, default)).CharacterBag);
    }
}
