using System.Reflection;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Presents;
using BubbleFighter.Fantasy.Contracts.Titles;
using BubbleFighter.Fantasy.Kernel;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Titles.Tests;

/// <summary>Exercises declared service visibility, protocol ownership and reversible title plugin activation.</summary>
public sealed class TitlesPluginTests
{
    /// <summary>The shipped disabled row needs no native catalog, storage, dialect or equipment dependencies.</summary>
    [Fact]
    public async Task DisabledRowActivatesWithoutFeatureProviders()
    {
        var services = new ServiceRegistry();
        var context = new PluginContext(new() { Id = "titles", Plugin = "titles", Source = "tests" },
            services, new EventBus(NullLogger.Instance), NullLogger.Instance);
        context.BindServices(typeof(TitlesPlugin).GetCustomAttribute<PluginAttribute>()!);
        try
        {
            await new TitlesPlugin().ActivateAsync(context, default);
            Assert.False(services.TryGet<IMessageRouter>(out _));
            Assert.Empty(services.GetAll<IMessageSender<TitleMakeReplied>>());
        }
        finally { await context.UnwindAsync(); }
    }

    /// <summary>Both audited catalogs mount only their own routes and one explicit title equipment group.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActivationUsesDeclaredServicesAndOwnsItsRoutesAndWearGroup(bool modern)
    {
        await using var fixture = await Fixture.CreateAsync(modern);
        var context = fixture.Feature();
        await new TitlesPlugin().ActivateAsync(context, default);

        Assert.Equal(3, fixture.Router.Handlers.Count);
        Assert.All(fixture.Router.Handlers, route =>
        {
            Assert.Equal("titles", route.OwnerRowId);
            Assert.Equal(TitleTestRig.Protocol, route.Protocol);
        });
        var groups = await fixture.GroupsAsync(TitleTestRig.Protocol);
        var titles = Assert.Single(groups.Skip(TitleTestRig.BaseGroups.Length));
        Assert.Equal([400901, 400909], titles.ItemIds);
        Assert.False(titles.Contains(400905));
        Assert.Equal(TitleTestRig.BaseGroups, await fixture.GroupsAsync("another/game"));

        Assert.True(await fixture.Router.DispatchAsync(TitleTestRig.Protocol, fixture.Rig.Session, new TitleMakeRequested(1, 400001), default));
        Assert.Equal(0, Assert.Single(fixture.Rig.Made).Result);
        await fixture.Rig.AddTitleAsync(400909);
        Assert.True(await fixture.Router.DispatchAsync(TitleTestRig.Protocol, fixture.Rig.Session, new TitleSetRequested(1, 0, 400001), default));
        Assert.True(await fixture.Router.DispatchAsync(TitleTestRig.Protocol, fixture.Rig.Session, new TitleSetRequested(1, 0, 400009), default));
        var wornTitles = (await fixture.Rig.BagAsync()).Where(row => titles.Contains(row.ItemId)
            && (row.Flags & InventorySlotRecord.WornFlagBit) != 0).ToArray();
        Assert.Equal(400909, Assert.Single(wornTitles).ItemId);
        Assert.True(await fixture.Router.DispatchAsync(TitleTestRig.Protocol, fixture.Rig.Session, new MedalInfoRequested(1), default));
        Assert.Equal(2, fixture.Rig.Balances.Count);

        await context.UnwindAsync();
        Assert.Empty(fixture.Router.Handlers);
        Assert.Equal(TitleTestRig.BaseGroups, await fixture.GroupsAsync(TitleTestRig.Protocol));
        Assert.False(await fixture.Router.DispatchAsync(TitleTestRig.Protocol, fixture.Rig.Session, new TitleMakeRequested(1, 400001), default));
        Assert.False(await fixture.Router.DispatchAsync(TitleTestRig.Protocol, fixture.Rig.Session, new TitleSetRequested(1, 0, 400001), default));
        Assert.False(await fixture.Router.DispatchAsync(TitleTestRig.Protocol, fixture.Rig.Session, new MedalInfoRequested(1), default));
    }

    /// <summary>Each incoming operation requires exactly one binding for the configured protocol before routes are installed.</summary>
    [Theory]
    [InlineData("binding.make", false)]
    [InlineData("binding.make", true)]
    [InlineData("binding.set", false)]
    [InlineData("binding.set", true)]
    [InlineData("binding.medals", false)]
    [InlineData("binding.medals", true)]
    public async Task MissingOrDuplicateNativeBindingsRejectActivation(string key, bool duplicate)
    {
        await using var fixture = await Fixture.CreateAsync(missing: duplicate ? null : key, duplicate: duplicate ? key : null);
        var context = fixture.Feature();
        await Assert.ThrowsAsync<ConfigException>(() => new TitlesPlugin().ActivateAsync(context, default).AsTask());
        Assert.Empty(fixture.Router.Handlers);
        Assert.Equal(TitleTestRig.BaseGroups, await fixture.GroupsAsync(TitleTestRig.Protocol));
    }

    /// <summary>Missing or ambiguous outputs fail configuration before any partially functional request routes exist.</summary>
    [Theory]
    [InlineData("sender.inventory", false)]
    [InlineData("sender.inventory", true)]
    [InlineData("sender.make", false)]
    [InlineData("sender.make", true)]
    [InlineData("sender.set", false)]
    [InlineData("sender.set", true)]
    [InlineData("sender.medals", false)]
    [InlineData("sender.medals", true)]
    public async Task MissingOrDuplicateOutputSendersRejectActivation(string key, bool duplicate)
    {
        await using var fixture = await Fixture.CreateAsync(missing: duplicate ? null : key, duplicate: duplicate ? key : null);
        var context = fixture.Feature();
        await Assert.ThrowsAsync<ConfigException>(() => new TitlesPlugin().ActivateAsync(context, default).AsTask());
        Assert.Empty(fixture.Router.Handlers);
        Assert.Equal(TitleTestRig.BaseGroups, await fixture.GroupsAsync(TitleTestRig.Protocol));
    }

    /// <summary>A mismatched canonical scope, unsupported native grant or unknown schema cannot mount title routes.</summary>
    [Theory]
    [InlineData("scope")]
    [InlineData("grant")]
    [InlineData("schema")]
    public async Task InvalidCatalogConfigurationRejectsActivation(string condition)
    {
        await using var fixture = await Fixture.CreateAsync(unsupportedGrant: condition == "grant");
        var context = fixture.Feature(condition switch
        {
            "scope" => new() { ["build_key"] = "different-catalog" },
            "schema" => new() { ["catalog_schema"] = "Unknown" },
            _ => null,
        });
        await Assert.ThrowsAsync<ConfigException>(() => new TitlesPlugin().ActivateAsync(context, default).AsTask());
        Assert.Empty(fixture.Router.Handlers);
    }

    /// <summary>The title group consumes the shared bounded group budget and is withdrawn when the row unloads.</summary>
    [Fact]
    public async Task GroupCapacityFailureDoesNotLeaveAContributionAfterUnload()
    {
        await using var fixture = await Fixture.CreateAsync();
        var context = fixture.Feature();
        await new TitlesPlugin().ActivateAsync(context, default);
        IReadOnlyList<InventoryWearGroup> full = Enumerable.Range(0, InventoryLimits.MaxWearGroups)
            .Select(index => new InventoryWearGroup(500000 + index, 500000 + index, 500000 + index)).ToArray();
        await Assert.ThrowsAsync<ConfigException>(() => fixture.Rig.Events
            .WaterfallAsync<EquipmentWearGroupsRequested, IReadOnlyList<InventoryWearGroup>>(
                new(TitleTestRig.Protocol), _ => ValueTask.FromResult(full), default).AsTask());
        await context.UnwindAsync();
        Assert.Equal(TitleTestRig.BaseGroups, await fixture.GroupsAsync(TitleTestRig.Protocol));
    }

    /// <summary>Activation rollback removes only the new row's partial routes and preserves an existing route owner.</summary>
    [Fact]
    public async Task FailedRouteInstallationUnwindsOnlyItsOwnEffects()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Setup.Effect(fixture.Router.For(fixture.Setup).On<TitleSetRequested>(TitleTestRig.Protocol,
            (_, _, _) => ValueTask.CompletedTask));
        var context = fixture.Feature();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new TitlesPlugin().ActivateAsync(context, default).AsTask());
        await context.UnwindAsync();
        var surviving = Assert.Single(fixture.Router.Handlers);
        Assert.Equal("setup", surviving.OwnerRowId);
        Assert.Equal(typeof(TitleSetRequested), surviving.MessageType);
        Assert.Equal(TitleTestRig.BaseGroups, await fixture.GroupsAsync(TitleTestRig.Protocol));
    }

    /// <summary>Unload cancels pending delivery and drains the handler before disposing its account guard.</summary>
    [Fact]
    public async Task UnloadCancelsPendingPublicationWithoutAcknowledgingOrRepeatingTheDebit()
    {
        await using var fixture = await Fixture.CreateAsync();
        var context = fixture.Feature();
        await new TitlesPlugin().ActivateAsync(context, default);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Rig.BeforeInventory = async ct => { entered.TrySetResult(); await release.Task.WaitAsync(ct); };
        var pending = fixture.Router.DispatchAsync(TitleTestRig.Protocol, fixture.Rig.Session,
            new TitleMakeRequested(1, 400001), default).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await context.UnwindAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        finally { release.TrySetResult(); }
        Assert.Empty(fixture.Router.Handlers);
        Assert.Empty(fixture.Rig.Made);
        Assert.Empty(fixture.Rig.Inventories);
        Assert.Equal(10, (await fixture.Rig.Store.ReadAsync(1, fixture.Rig.Scope.Key, default))[0].Count);
        Assert.Single(await fixture.Rig.BagAsync(), row => row.ItemId == 400901);
    }

    /// <summary>Favorite titles need supported native titles, one progress reader and one progress sender, and then follow login and Favorite commits.</summary>
    [Fact]
    public async Task FavoriteTitlesRequireTheirProvidersAndRunAtLoginAndAfterFavoriteCommits()
    {
        await using var fixture = await Fixture.CreateAsync(modern: true);
        var overrides = new Dictionary<string, object?> { ["favorite_titles"] = true };
        await Assert.ThrowsAsync<ConfigException>(() => new TitlesPlugin().ActivateAsync(fixture.Feature(overrides), default).AsTask());
        fixture.Rig.CatalogData.AddFavoriteTitles();
        await Assert.ThrowsAsync<ConfigException>(() => new TitlesPlugin().ActivateAsync(fixture.Feature(overrides), default).AsTask());
        var progress = new List<TitleProgressNotified>();
        var reader = new Reader();
        fixture.RegisterFavorite(reader, new TitleTestSender<TitleProgressNotified>(TitleTestRig.Protocol,
            (message, _) => { progress.Add(message); return ValueTask.CompletedTask; }));
        var context = fixture.Feature(overrides);
        await new TitlesPlugin().ActivateAsync(context, default);

        await fixture.Rig.Events.SerialAsync(new LoginMenuInstalled("another/game", fixture.Rig.Session, null!), default);
        Assert.Empty(progress);
        await fixture.Rig.Events.SerialAsync(new LoginMenuInstalled(TitleTestRig.Protocol, fixture.Rig.Session, null!), default);
        Assert.Single(progress);
        reader.Value = 1;
        await fixture.Rig.Events.SerialAsync(new FavoriteProgressCommitted(TitleTestRig.Protocol, fixture.Rig.Session), default);
        Assert.Equal(2, progress.Count);
        Assert.Single(await fixture.Rig.BagAsync(), row => row.ItemId == 400906);
        await context.UnwindAsync();
        await fixture.Rig.Events.SerialAsync(new FavoriteProgressCommitted(TitleTestRig.Protocol, fixture.Rig.Session), default);
        Assert.Equal(2, progress.Count);
    }

    private sealed class Reader : IFavoriteWeaponProgress
    {
        internal int Value { get; set; }
        public string Protocol => TitleTestRig.Protocol;
        public ValueTask<int> CountMasterWeaponsAsync(long userNum, CancellationToken ct) => ValueTask.FromResult(Value);
    }

    private sealed class Fixture(TitleTestRig rig) : IAsyncDisposable
    {
        private readonly ServiceRegistry _services = new();
        private readonly List<PluginContext> _contexts = [];
        internal TitleTestRig Rig => rig;
        internal PluginContext Setup { get; private set; } = null!;
        internal IMessageRouter Router => _services.Get<IMessageRouter>();

        internal static async Task<Fixture> CreateAsync(bool modern = false, string? missing = null,
            string? duplicate = null, bool unsupportedGrant = false)
        {
            var fixture = new Fixture(await TitleTestRig.CreateAsync(modern));
            var rig = fixture.Rig;
            rig.Selection.IncludeTitleGroup = false;
            fixture.Setup = fixture.Context("setup");
            fixture.Setup.Effect(fixture._services.Register<IClientDataStore>(rig.CatalogData));
            fixture.Setup.Effect(fixture._services.Register<ITitleMedalStore>(rig.Medals));
            fixture.Setup.Effect(fixture._services.Register<IInventoryLoadoutStore>(rig.Inventory));
            fixture.Setup.Effect(fixture._services.Register<IInventoryBuildScope>(new BuildScope(unsupportedGrant
                ? new(rig.Scope.Key, rig.Scope.SupportedItemIds.Where(id => id != 400909).ToArray()) : rig.Scope)));
            fixture.Setup.Effect(fixture._services.Register<IEquipmentSelection>(rig.Selection));
            fixture.Setup.Effect(fixture._services.Register<IEquippedViewPush>(rig.View));
            var router = fixture.Context("router");
            router.BindServices(typeof(MessageRouterPlugin).GetCustomAttribute<PluginAttribute>()!);
            await new MessageRouterPlugin().ActivateAsync(router, default);
            Binding<TitleMakeRequested>("binding.make");
            Binding<TitleSetRequested>("binding.set");
            Binding<MedalInfoRequested>("binding.medals");
            Sender("sender.inventory", rig.InventorySender);
            Sender("sender.make", rig.MakeSender);
            Sender("sender.set", rig.SetSender);
            Sender("sender.medals", rig.MedalSender);
            return fixture;

            void Binding<T>(string key) where T : IMessage
            {
                fixture.Setup.Effect(fixture._services.RegisterMany<IInboundMessageBinding<T>>(new Inbound<T>("another/game")));
                if (missing == key) return;
                fixture.Setup.Effect(fixture._services.RegisterMany<IInboundMessageBinding<T>>(new Inbound<T>(TitleTestRig.Protocol)));
                if (duplicate == key)
                    fixture.Setup.Effect(fixture._services.RegisterMany<IInboundMessageBinding<T>>(new Inbound<T>(TitleTestRig.Protocol)));
            }

            void Sender<T>(string key, IMessageSender<T> sender) where T : IMessage
            {
                fixture.Setup.Effect(fixture._services.RegisterMany<IMessageSender<T>>(
                    new TitleTestSender<T>("another/game", (_, _) => throw new InvalidOperationException("A foreign sender was selected."))));
                if (missing == key) return;
                fixture.Setup.Effect(fixture._services.RegisterMany(sender));
                if (duplicate == key)
                    fixture.Setup.Effect(fixture._services.RegisterMany<IMessageSender<T>>(
                        new TitleTestSender<T>(TitleTestRig.Protocol, (_, _) => ValueTask.CompletedTask)));
            }
        }

        internal void RegisterFavorite(IFavoriteWeaponProgress reader, IMessageSender<TitleProgressNotified> sender)
        {
            Setup.Effect(_services.RegisterMany(reader));
            Setup.Effect(_services.RegisterMany(sender));
        }

        internal PluginContext Feature(Dictionary<string, object?>? overrides = null)
        {
            Dictionary<string, object?> config = new()
            {
                ["enabled"] = true, ["protocol"] = TitleTestRig.Protocol,
                ["build_key"] = rig.Scope.Key, ["catalog_schema"] = rig.CatalogData.Schema.ToString(),
            };
            foreach (var (key, value) in overrides ?? []) config[key] = value;
            var context = Context("titles", config);
            context.BindServices(typeof(TitlesPlugin).GetCustomAttribute<PluginAttribute>()!);
            return context;
        }

        internal ValueTask<IReadOnlyList<InventoryWearGroup>> GroupsAsync(string protocol) =>
            rig.Events.WaterfallAsync<EquipmentWearGroupsRequested, IReadOnlyList<InventoryWearGroup>>(
                new(protocol), _ => ValueTask.FromResult<IReadOnlyList<InventoryWearGroup>>(TitleTestRig.BaseGroups), default);

        private PluginContext Context(string id, Dictionary<string, object?>? config = null)
        {
            var context = new PluginContext(new() { Id = id, Plugin = id, Source = "tests", Config = config },
                _services, rig.Events, NullLogger.Instance);
            _contexts.Add(context);
            return context;
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            for (var index = _contexts.Count - 1; index >= 0; index--) await _contexts[index].UnwindAsync();
            rig.Dispose();
        }
    }

    private sealed class BuildScope(InventoryLoadoutScope scope) : IInventoryBuildScope
    {
        /// <inheritdoc />
        public InventoryLoadoutScope Scope => scope;
    }

    private sealed class Inbound<T>(string protocol) : IInboundMessageBinding<T> where T : IMessage
    {
        /// <inheritdoc />
        public string Protocol => protocol;
    }
}
