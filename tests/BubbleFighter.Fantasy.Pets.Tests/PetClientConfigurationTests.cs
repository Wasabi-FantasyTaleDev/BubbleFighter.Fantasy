using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Pets;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Pets.Data;
using BubbleFighter.Fantasy.Pets.Mileage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Pets.Tests;

/// <summary>Verifies that native pet feeding publishes complete settings through one matching audited dialect.</summary>
public sealed class PetClientConfigurationTests
{
    /// <summary>Manual feeding publishes both UI settings without enabling mileage gains or requiring reset reward terms.</summary>
    [Fact]
    public async Task FeedingWithoutMileagePublishesTheConfiguredCooldownAndMaximum()
    {
        var options = PetTestData.Options(manual: true);
        options.SkillResetsEnabled = false;
        options.Mileage.DisplayMaximum = 317;
        options.FeedCooldownSeconds = 12600;
        var catalog = PetTestData.Catalog();
        var scope = new InventoryLoadoutScope(options.BuildKey, catalog.Items.Keys.ToArray());
        Assert.Null(PetMileagePolicy.Create(options, catalog, scope));
        var settings = PetsPlugin.CreateClientConfiguration(options, catalog, scope);
        Assert.NotNull(settings);
        Assert.Equal(317, settings.MileageDisplayMaximum);
        Assert.Equal(12600, settings.FeedCooldownSeconds);

        var services = new ServiceRegistry();
        var events = new EventBus(NullLogger.Instance);
        var context = new PluginContext(new PluginRow { Id = "pets", Plugin = "pets", Source = "test" },
            services, events, NullLogger.Instance);
        try
        {
            context.Effect(services.RegisterMany<IPetClientConfigurationSink>(new Sink(options.Protocol, false)));
            PetsPlugin.RegisterClientConfiguration(context, options.Protocol, settings);
            options.Mileage.DisplayMaximum = 999;
            options.FeedCooldownSeconds = 0;
            Assert.Equal(new PetClientConfiguration(317, 12600), await Query(events, options.Protocol));
        }
        finally { await context.UnwindAsync(); }
    }

    /// <summary>Display-only feeding policy still requires explicit complete settings within the native count bound.</summary>
    [Theory]
    [InlineData(null, 0)]
    [InlineData(0, 0)]
    [InlineData(1001, 0)]
    [InlineData(317, null)]
    public void FeedingRejectsMissingOrOutOfRangeSettings(int? maximum, int? cooldown)
    {
        var options = PetTestData.Options(manual: true);
        options.Mileage.DisplayMaximum = maximum;
        options.FeedCooldownSeconds = cooldown;
        var basis = PetTestData.Catalog();
        var items = basis.Items.ToDictionary();
        items[PetMileagePolicy.BalanceItemId] = new(PetMileagePolicy.BalanceItemId, 1114, 0, 0)
            { DurationType = "count", MaximumDuration = 1000 };
        var catalog = new PetCatalog(items, basis.Skills.ToDictionary(), basis.Buffs.ToDictionary(), basis.PetCategory);
        Assert.Throws<ConfigException>(() => PetsPlugin.CreateClientConfiguration(options, catalog,
            new(options.BuildKey, items.Keys.ToArray())));
    }

    /// <summary>Automatic-growth feeding without mileage retains its existing path without a modern settings provider.</summary>
    [Fact]
    public void AutomaticGrowthWithoutMileageRequiresNoModernConfiguration()
    {
        var options = PetTestData.Options();
        var catalog = PetTestData.Catalog();
        Assert.Null(PetsPlugin.CreateClientConfiguration(options, catalog, new(options.BuildKey, catalog.Items.Keys.ToArray())));
    }

    /// <summary>Absent, unrelated, duplicate and refusing dialects fail before registering a provider.</summary>
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(1, true)]
    public async Task ActivationRegistrationRequiresOneMatchingAcceptingSink(int count, bool refuses)
    {
        var services = new ServiceRegistry();
        var events = new EventBus(NullLogger.Instance);
        var context = new PluginContext(new PluginRow { Id = "pets", Plugin = "pets", Source = "test" },
            services, events, NullLogger.Instance);
        try
        {
            for (var index = 0; index < count; index++)
                context.Effect(services.RegisterMany<IPetClientConfigurationSink>(
                    new Sink(count == 1 && !refuses ? "other/game" : "cn-774/game", refuses)));
            Assert.Throws<ConfigException>(() => PetsPlugin.RegisterClientConfiguration(context, "cn-774/game", new(317, 12600)));
            Assert.Null(await Query(events, "cn-774/game"));
        }
        finally { await context.UnwindAsync(); }
    }

    /// <summary>A validated provider exposes exact immutable settings only on its protocol and disappears on row unload.</summary>
    [Fact]
    public async Task ValidatedSettingsAreProtocolScopedAndEffectOwned()
    {
        var services = new ServiceRegistry();
        var events = new EventBus(NullLogger.Instance);
        var context = new PluginContext(new PluginRow { Id = "pets", Plugin = "pets", Source = "test" },
            services, events, NullLogger.Instance);
        var sink = new Sink("cn-774/game", false);
        using var registration = services.RegisterMany<IPetClientConfigurationSink>(sink);
        var settings = new PetClientConfiguration(317, 12600);
        try
        {
            PetsPlugin.RegisterClientConfiguration(context, "cn-774/game", settings);
            Assert.Same(settings, sink.Validated);
            Assert.Same(settings, await Query(events, "cn-774/game"));
            Assert.Null(await Query(events, "kr-1256/game"));
        }
        finally { await context.UnwindAsync(); }
        Assert.Null(await Query(events, "cn-774/game"));
    }

    private static ValueTask<PetClientConfiguration?> Query(IEventBus events, string protocol) =>
        events.WaterfallAsync<PetClientConfigurationRequested, PetClientConfiguration?>(new(protocol),
            _ => ValueTask.FromResult<PetClientConfiguration?>(null), default);

    private sealed class Sink(string protocol, bool refuses) : IPetClientConfigurationSink
    {
        /// <summary>Identifies the game protocol whose pet configuration this fixture sink validates.</summary>
        public string Protocol => protocol;
        internal PetClientConfiguration? Validated { get; private set; }
        /// <summary>Either rejects configuration with the fixture dialect error or retains the exact accepted configuration.</summary>
        public void Validate(PetClientConfiguration configuration)
        {
            if (refuses) throw new ConfigException("The fixture dialect has no admitted category.");
            Validated = configuration;
        }
    }
}
