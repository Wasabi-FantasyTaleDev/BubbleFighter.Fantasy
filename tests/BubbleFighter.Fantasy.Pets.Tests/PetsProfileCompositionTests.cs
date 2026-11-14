using System.Reflection;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Pets;
using BubbleFighter.Fantasy.InventoryView;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Pets.Combat;
using Xunit;

namespace BubbleFighter.Fantasy.Pets.Tests;

/// <summary>Composes the profiles' pet rows without loading client archives or opening listeners.</summary>
public sealed class PetsProfileCompositionTests
{
    /// <summary>Lists the builds whose profiles mount the pet rows.</summary>
    public static IEnumerable<object[]> AuditedBuilds => new[]
    {
        "kr-812", "cn-717", "cn-404", "cn-406", "cn-408", "cn-706", "id-400", "id-1809", "kr-1256",
    }.Select(build => new object[] { build });

    /// <summary>cn-774 and tw-454 mount no native inventory-use handler, so their profiles do not include the pet rows yet.</summary>
    [Theory]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void BuildsWithoutInventoryUseDoNotMountPets(string build) =>
        Assert.DoesNotContain(Composer().Compose(build, []), row => row.Plugin is "pets" or "pets.entitlements" or "pets.combat-effects");

    /// <summary>Every audited build's profile mounts growth, skills, rewards and combat on, with its own clock.</summary>
    [Theory, MemberData(nameof(AuditedBuilds))]
    public void ProfilesMountPetsOnWithMatchedPoliciesAndNoParentCycle(string build)
    {
        var rows = Composer().Compose(build, []);
        var loadouts = One(rows, "inventory.loadouts");
        var entitlements = One(rows, "pets.entitlements");
        var main = One(rows, "pets");
        var combat = One(rows, "pets.combat-effects");
        Assert.Equal("inventory.loadouts", entitlements.ParentId);
        var inventoryParent = One(rows, "inventoryview").ParentId;
        if (build == "kr-1256")
        {
            var articles = One(rows, "item-enhancement.articles");
            Assert.Equal(entitlements.Id, articles.ParentId);
            Assert.Equal(articles.Id, inventoryParent);
        }
        else
            Assert.Equal(entitlements.Id, inventoryParent);
        Assert.Equal("ingame", main.ParentId);
        Assert.Equal("clientdata", combat.ParentId);
        Assert.Equal("pets.combat-effects", One(rows, "dialect.scene-seeds").ParentId);
        Assert.Contains(typeof(IInventoryBuildScope).FullName!, loadouts.IsolatedServices);
        Assert.Contains(typeof(IPetEntitlements).FullName!, entitlements.IsolatedServices);
        var left = new PluginConfig(entitlements).Bind<PetsOptions>();
        var right = new PluginConfig(main).Bind<PetsOptions>();
        Assert.True(left.Enabled);
        Assert.True(right.Enabled);
        Assert.True(right.Rewards.Enabled);
        Assert.Contains(1, right.Rewards.ModeIds);
        Assert.False(right.Mileage.Enabled);
        Assert.True(new PluginConfig(combat).Bind<PetCombatEffectsOptions>().Enabled);
        Assert.Equal("true", One(rows, "dialect.messages").Config!["pets"]);
        Assert.Equal("true", One(rows, "equipment").Config!["pet_shared_slot"]);
        foreach (var property in typeof(PetsOptions).GetProperties().Where(property => property.Name != nameof(PetsOptions.Rewards)))
            Assert.Equivalent(property.GetValue(left), property.GetValue(right), strict: true);
        Assert.Equal(build, left.BuildKey);
        Assert.Equal(build + "/game", left.Protocol);
        Assert.Equal(IsModern(build), left.ManualLevelUp);
        Assert.Equal(IsModern(build), left.GradeCapFromPetParameter);
        Assert.Equal(IsModern(build), left.SharedSkillPool);
        Assert.Equal(IsModern(build), left.ExperienceFromFoodParameter);
        Assert.Equal(IsModern(build) ? 25200 : (int?)null, left.FeedCooldownSeconds);
        Assert.Equal(build == "tw-454" ? PetClientConfigurationSource.NativePetConfig
            : IsModern(build) ? PetClientConfigurationSource.DynamicConfig : PetClientConfigurationSource.None,
            left.ClientConfigurationSource);
        Assert.Equal(build == "tw-454" ? "Config/PetConfig.xml" : null, left.NativePetConfigPath);
        Assert.Equal(build == "kr-1256" ? 110 : (int?)null, left.Mileage.DisplayMaximum);
        Assert.Equal(build.StartsWith("kr-", StringComparison.Ordinal) ? 540 : build.StartsWith("id-", StringComparison.Ordinal) ? 420 : 480,
            left.ClientUtcOffsetMinutes);
        foreach (var row in rows)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal) { row.Id };
            for (var parent = row.ParentId; parent is not null; parent = One(rows, parent).ParentId)
                Assert.True(seen.Add(parent), $"Parent cycle at {row.Id} through {parent}.");
        }
    }

    /// <summary>BFF_PETS_ENABLED=false turns growth, skills and the single shared pet slot off together.</summary>
    [Theory, MemberData(nameof(AuditedBuilds))]
    public void OptOutDisablesPetsAndTheSharedSlot(string build)
    {
        var rows = Composer(enabled: false).Compose(build, []);
        Assert.Equal("false", One(rows, "pets").Config!["enabled"]);
        Assert.Equal("false", One(rows, "pets.entitlements").Config!["enabled"]);
        Assert.Equal("false", One(rows, "equipment").Config!["pet_shared_slot"]);
        Assert.Equal("false", One(rows, "dialect.messages").Config!["pets"]);
    }

    /// <summary>Canonical scope publication has no dependency on descendants that consume its filtered catalog.</summary>
    [Fact]
    public void ScopeAndEntitlementDeclarationsDoNotCreateTheOldDependencyCycle()
    {
        var inventory = typeof(InventoryLoadoutsPlugin).GetCustomAttribute<PluginAttribute>()!;
        var entitlements = typeof(PetEntitlementsPlugin).GetCustomAttribute<PluginAttribute>()!;
        var main = typeof(PetsPlugin).GetCustomAttribute<PluginAttribute>()!;
        Assert.Contains(typeof(IInventoryBuildScope), inventory.Provides);
        Assert.DoesNotContain(typeof(IPetEntitlements), inventory.Requires.Concat(inventory.Optional));
        Assert.Contains(typeof(IInventoryBuildScope), entitlements.Optional);
        Assert.Contains(typeof(IPetEntitlements), entitlements.ConditionalProvides);
        Assert.Contains(typeof(IInventoryBuildScope), main.Optional);
        Assert.Contains(typeof(IPetEntitlements), main.Optional);
    }

    /// <summary>Dynamic configuration still refuses activation when the cooldown or client clock is removed.</summary>
    [Fact]
    public void DynamicActivationNeedsExplicitSettingsAndCivilClock()
    {
        var row = One(Composer().Compose("cn-774", Patches("cn-774")), "pets");
        var config = row.Config!.ToDictionary(pair => pair.Key, pair => pair.Value);
        Assert.Equal(110, Assert.IsType<Dictionary<string, object?>>(config["mileage"])["display_maximum"] is { } maximum
            ? Convert.ToInt32(maximum, System.Globalization.CultureInfo.InvariantCulture) : 0);
        config.Remove("client_utc_offset_minutes");
        var missing = Assert.Throws<ConfigException>(() => new PluginConfig(row with { Config = config }).Bind<PetsOptions>());
        Assert.Contains("feed_cooldown_seconds", missing.Message);
        Assert.Contains("client_utc_offset_minutes", missing.Message);
        config["feed_cooldown_seconds"] = 0;
        config["client_utc_offset_minutes"] = 0;
        Assert.Equal(0, new PluginConfig(row with { Config = config }).Bind<PetsOptions>().FeedCooldownSeconds);
        config["feed_cooldown_seconds"] = -1;
        Assert.Throws<ConfigException>(() => new PluginConfig(row with { Config = config }).Bind<PetsOptions>());
    }

    /// <summary>TW reads its native workbook, has no mileage maximum, and still refuses a missing client clock.</summary>
    [Fact]
    public void TaiwaneseActivationSelectsNativeWorkbookAndStillRequiresClockPolicy()
    {
        var row = One(Composer().Compose("tw-454", Patches("tw-454")), "pets");
        var withClock = row.Config!.ToDictionary(pair => pair.Key, pair => pair.Value);
        withClock["client_utc_offset_minutes"] = 480;
        var options = new PluginConfig(row with { Config = withClock }).Bind<PetsOptions>();
        Assert.Equal(PetClientConfigurationSource.NativePetConfig, options.ClientConfigurationSource);
        Assert.Equal("Config/PetConfig.xml", options.NativePetConfigPath);
        Assert.Equal(25200, options.FeedCooldownSeconds);
        Assert.Null(options.Mileage.DisplayMaximum);
        Assert.False(options.Mileage.Enabled);
        var config = row.Config!.ToDictionary(pair => pair.Key, pair => pair.Value);
        config.Remove("client_utc_offset_minutes");
        var missing = Assert.Throws<ConfigException>(() => new PluginConfig(row with { Config = config }).Bind<PetsOptions>());
        Assert.Contains("client_utc_offset_minutes", missing.Message);
        Assert.DoesNotContain("display_maximum", missing.Message);
        Assert.DoesNotContain("feed_cooldown_seconds", missing.Message);
    }

    /// <summary>The proven KR duration and clock compose, and removing the clock still fails loudly.</summary>
    [Fact]
    public void KoreanCooldownDoesNotInventAClientTimezone()
    {
        var row = One(Composer().Compose("kr-1256", []), "pets");
        Assert.Equal(540, new PluginConfig(row).Bind<PetsOptions>().ClientUtcOffsetMinutes);
        var config = row.Config!.ToDictionary(pair => pair.Key, pair => pair.Value);
        config.Remove("client_utc_offset_minutes");
        var error = Assert.Throws<ConfigException>(() => new PluginConfig(row with { Config = config }).Bind<PetsOptions>());
        Assert.Contains("client_utc_offset_minutes", error.Message);
        Assert.DoesNotContain("feed_cooldown_seconds", error.Message);
    }

    /// <summary>Low-level rule and projection calls cannot bypass required modern clock configuration.</summary>
    [Fact]
    public void DirectNativeOperationsFailLoudlyWithoutClockPolicy()
    {
        var catalog = PetTestData.Catalog();
        var options = PetTestData.Options(manual: true);
        options.FeedCooldownSeconds = null;
        Assert.Throws<InvalidOperationException>(() => new PetRules(catalog, options, new Random(1)).Feed(
            PetTestData.State(0, 0), catalog.Items[PetTestData.PetId], catalog.Items[PetTestData.NormalFood], PetTestData.Now));
        options.FeedCooldownSeconds = 0;
        options.ClientUtcOffsetMinutes = null;
        Assert.Throws<InvalidOperationException>(() => new PetPresentation(catalog, options, new PetTestData.Clock()).Project(
            PetTestData.Slot(PetTestData.State(0, 0) with { LastFedAt = PetTestData.Now })));
        options.ClientUtcOffsetMinutes = 0;
        Assert.NotNull(new PetPresentation(catalog, options, new PetTestData.Clock()).Project(
            PetTestData.Slot(PetTestData.State(0, 0) with { LastFedAt = PetTestData.Now })));
        var legacy = PetTestData.Options();
        Assert.Null(legacy.FeedCooldownSeconds);
        Assert.Empty(legacy.Validate());
        legacy.FeedCooldownSeconds = 1;
        Assert.Contains(legacy.Validate(), error => error.MemberNames.Contains(nameof(PetsOptions.FeedCooldownSeconds)));
    }

    private static bool IsModern(string build) => build is "kr-1256" or "cn-774" or "tw-454";
    private static PluginRow One(IReadOnlyList<PluginRow> rows, string id) => Assert.Single(rows, row => row.Id == id);
    private static string[] Patches(string build) => new[] { "pets.patch.yaml", "pets-modern.patch.yaml" }
        .Concat(build == "tw-454" ? ["pets-tw-454.patch.yaml"] : Array.Empty<string>())
        .Select(file => Path.Combine(Profiles(), "bundles", file)).ToArray();
    // An unset BFF_PETS_ENABLED takes the profiles' default (on); false is the operator's opt-out.
    private static ProfileComposer Composer(bool? enabled = null) => new(Profiles(), key => key switch
    {
        "BFF_MYSQL_PASSWORD" or "BFF_ADMIN_TOKEN" => "isolated-test-placeholder",
        "BFF_PETS_ENABLED" => enabled switch { true => "true", false => "false", null => null },
        _ => null,
    });
    private static string Profiles()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "BubbleFighter.Fantasy.sln")))
                return Path.Combine(directory.FullName, "profiles");
        throw new InvalidOperationException("Repository profiles directory is unavailable.");
    }
}
