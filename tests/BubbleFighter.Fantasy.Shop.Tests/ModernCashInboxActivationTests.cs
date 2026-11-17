using System.ComponentModel.DataAnnotations;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.CashInbox;
using BubbleFighter.Fantasy.Dialect.Messages;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Shop;
using Xunit;

namespace BubbleFighter.Fantasy.Shop.Tests;

/// <summary>Validates composed cash-inbox activation and option validation across enabled and disabled builds.</summary>
public sealed class ModernCashInboxActivationTests
{
    public static IEnumerable<object[]> ModernBuilds() =>
    [
        ["kr-1256"],
        ["cn-774"],
        ["tw-454"],
    ];

    public static IEnumerable<object[]> LegacyEnabledBuilds() =>
    [
        ["kr-812"],
        ["id-400"],
        ["id-1809"],
        ["cn-324"],
        ["tw-410"],
        ["cn-406"],
        ["cn-404"],
        ["cn-408"],
        ["cn-705"],
        ["cn-706"],
        ["cn-717"],
    ];

    public static IEnumerable<object[]> DisabledBuilds() =>
    [
        ["kr-186"],
        ["cn-279"],
        ["cn-735"],
        ["cn-738"],
        ["cn-742"],
    ];

    [Theory]
    [MemberData(nameof(ModernBuilds))]
    public void ModernProfilesComposeBothCashInboxRowsWithValidOptionsAndInventorySource(string build)
    {
        var rows = Compose(build);

        var shop = new PluginConfig(Assert.Single(rows, row => row.Id == "shop")).Bind<ShopOptions>();
        Assert.True(shop.CashInbox, $"shop cash_inbox={shop.CashInbox}");
        Assert.True(shop.GrantPurchasedItems);
        Assert.False(shop.InventoryOnly);
        Assert.InRange(shop.MaxCashInboxEntriesPerPlayer, 1, CashInboxLimits.MaxEntriesPerPlayerCap);
        Assert.InRange(shop.MaxCashInboxFrameBytes, CashInboxLimits.MinFrameBytes, CashInboxLimits.MaxFrameBytesCap);
        Assert.Empty(shop.Validate());
        Assert.True(Validator.TryValidateObject(shop, new ValidationContext(shop), new List<ValidationResult>(), true));

        var messages = new PluginConfig(Assert.Single(rows, row => row.Id == "dialect.messages")).Bind<MessagesOptions>();
        Assert.True(messages.CashInbox, $"messages cash_inbox={messages.CashInbox}");

        // Pickups update the durable inventory bag and push through the shared inventory service.
        var lobby = Assert.Single(rows, row => row.Id == "lobby");
        Assert.Equal("mysql", $"{lobby.Config!["inventory_source"]}");
    }

    [Theory]
    [MemberData(nameof(LegacyEnabledBuilds))]
    public void LegacyEnabledProfilesComposeBothCashInboxRowsWithValidOptions(string build)
    {
        var rows = Compose(build);

        var shop = new PluginConfig(Assert.Single(rows, row => row.Id == "shop")).Bind<ShopOptions>();
        Assert.True(shop.CashInbox, $"{build} shop cash_inbox={shop.CashInbox}");
        Assert.True(shop.GrantPurchasedItems);
        Assert.False(shop.InventoryOnly);
        Assert.Empty(shop.Validate());
        Assert.True(Validator.TryValidateObject(shop, new ValidationContext(shop), new List<ValidationResult>(), true));

        var messages = new PluginConfig(Assert.Single(rows, row => row.Id == "dialect.messages")).Bind<MessagesOptions>();
        Assert.True(messages.CashInbox, $"{build} messages cash_inbox={messages.CashInbox}");
    }

    [Theory]
    [MemberData(nameof(DisabledBuilds))]
    public void DisabledProfilesKeepCashInboxOff(string build)
    {
        var rows = Compose(build);

        var messages = new PluginConfig(Assert.Single(rows, row => row.Id == "dialect.messages")).Bind<MessagesOptions>();
        Assert.False(messages.CashInbox, $"{build} messages cash_inbox={messages.CashInbox}");

        var shopRow = rows.SingleOrDefault(row => row.Id == "shop");
        if (shopRow is not null)
        {
            var shop = new PluginConfig(shopRow).Bind<ShopOptions>();
            Assert.False(shop.CashInbox, $"{build} shop cash_inbox={shop.CashInbox}");
        }
    }

    [Fact]
    public void CashInboxOptionsValidateDependenciesAndBounds()
    {
        Assert.False(new ShopOptions().CashInbox);

        var inventoryOnly = new ShopOptions
        {
            CatalogSchema = "legacy",
            ShopItemClientPath = "x.xml",
            InventoryOnly = true,
            GrantPurchasedItems = true,
            CashInbox = true,
        };
        Assert.Contains(inventoryOnly.Validate(), f => f.MemberNames.Contains(nameof(ShopOptions.CashInbox)));

        var noGrant = new ShopOptions
        {
            CatalogSchema = "legacy",
            ShopItemClientPath = "x.xml",
            GrantPurchasedItems = false,
            CashInbox = true,
        };
        Assert.Contains(noGrant.Validate(), f => f.MemberNames.Contains(nameof(ShopOptions.CashInbox)));

        var badWelcomeZero = ValidCashInboxOptions();
        badWelcomeZero.CashInboxWelcomeStocks = [1001, 0];
        Assert.Contains(badWelcomeZero.Validate(), f => f.MemberNames.Contains(nameof(ShopOptions.CashInboxWelcomeStocks)));

        var badWelcomeTooMany = ValidCashInboxOptions();
        badWelcomeTooMany.CashInboxWelcomeStocks = Enumerable.Range(1, CashInboxLimits.MaxEntriesPerPlayerCap + 1).ToArray();
        Assert.Contains(badWelcomeTooMany.Validate(), f => f.MemberNames.Contains(nameof(ShopOptions.CashInboxWelcomeStocks)));

        var badEntries = ValidCashInboxOptions();
        badEntries.MaxCashInboxEntriesPerPlayer = CashInboxLimits.MaxEntriesPerPlayerCap + 1;
        Assert.False(Validator.TryValidateObject(badEntries, new ValidationContext(badEntries), new List<ValidationResult>(), true));

        var badFrame = ValidCashInboxOptions();
        badFrame.MaxCashInboxFrameBytes = CashInboxLimits.MinFrameBytes - 1;
        Assert.False(Validator.TryValidateObject(badFrame, new ValidationContext(badFrame), new List<ValidationResult>(), true));

        var badTtl = ValidCashInboxOptions();
        badTtl.CashInboxTtlSeconds = CashInboxLimits.MaxTtlSeconds + 1;
        Assert.False(Validator.TryValidateObject(badTtl, new ValidationContext(badTtl), new List<ValidationResult>(), true));

        var badNote = ValidCashInboxOptions();
        badNote.CashInboxWelcomeNote = new string('n', CashInboxLimits.MaxNoteChars + 1);
        Assert.False(Validator.TryValidateObject(badNote, new ValidationContext(badNote), new List<ValidationResult>(), true));

        var ok = ValidCashInboxOptions();
        ok.CashInboxWelcomeStocks = [3000001];
        Assert.Empty(ok.Validate());
        Assert.True(Validator.TryValidateObject(ok, new ValidationContext(ok), new List<ValidationResult>(), true));
    }

    private static ShopOptions ValidCashInboxOptions() => new()
    {
        CatalogSchema = "legacy",
        ShopItemClientPath = "x.xml",
        GrantPurchasedItems = true,
        CashInbox = true,
    };

    private static IReadOnlyList<PluginRow> Compose(string build)
    {
        var composer = new ProfileComposer(RepositoryRoot("profiles"),
            key => key is "BFF_MYSQL_PASSWORD" or "BFF_ADMIN_TOKEN" ? "isolated-test-placeholder" : null);
        return composer.Compose(build, []);
    }

    private static string RepositoryRoot(string leaf)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "packets", "builds.yaml")))
                return Path.Combine(directory.FullName, leaf);
        throw new InvalidOperationException("Repository packet tables were not found.");
    }
}
