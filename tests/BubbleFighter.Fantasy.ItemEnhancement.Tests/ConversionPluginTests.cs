using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Enhancement;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Party;
using Xunit;

namespace BubbleFighter.Fantasy.ItemEnhancement.Tests;

/// <summary>
/// The enhancement row's permanent-conversion tab: advertised only when enabled, it spends one card, makes the
/// individual timed item permanent with an equipment identity, and publishes inventory before the result.
/// </summary>
public sealed class ConversionPluginTests
{
    private const int Target = EnhancementPluginFixture.ConversionTarget;
    private const int Card = EnhancementPluginFixture.ConversionCard;

    [Fact]
    public async Task AConversionOnlyItemBecomesPermanentWithItsIdentityBeforeTheResult()
    {
        await using var fixture = await Seeded(TimeSpan.FromDays(40));
        await new EnhancementArticlesPlugin().ActivateAsync(fixture.Articles(), default);
        await new ItemEnhancementPlugin().ActivateAsync(fixture.Economy(Enabled(30)), default);

        Assert.True(await fixture.Router.DispatchAsync(EnhancementPluginFixture.Protocol, fixture.Session,
            new ScienceLabConfigurationRequested(), default));
        Assert.Equal(new ScienceLabConversionConfiguration(Card, 30), Assert.Single(fixture.Configurations).Conversion);

        Assert.True(await fixture.Router.DispatchAsync(EnhancementPluginFixture.Protocol, fixture.Session,
            new LabOperationRequested(LabOperation.ChangeUnlimited, [Target], 0), default));
        Assert.Equal(["configuration", "inventory", "converted"], fixture.Order);
        var inventory = Assert.Single(fixture.Inventories);
        Assert.True(inventory.IsCompleteSnapshot);
        var converted = inventory.CharacterBag.Single(row => row.ItemId == Target);
        Assert.Equal(InventoryLifetimeKind.Permanent, converted.Lifetime.Kind);
        Assert.Equal([converted.ArticleIdentity!.ArticleKey], Assert.Single(fixture.Converted).Keys);
        Assert.Equal(7, inventory.CharacterBag.Single(row => row.ItemId == Card).Quantity);

        // Asking again for the now permanent item is refused and spends nothing.
        Assert.True(await fixture.Router.DispatchAsync(EnhancementPluginFixture.Protocol, fixture.Session,
            new LabOperationRequested(LabOperation.ChangeUnlimited, [Target], 0), default));
        Assert.Equal(LabOperation.ChangeUnlimited, Assert.Single(fixture.Refused).Operation);
        Assert.Equal(7, (await fixture.Store.ReadLaboratoryInventoryAsync(1, fixture.Scope, default)).CharacterBag
            .Single(row => row.ItemId == Card).Quantity);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("article_key_selector")]
    [InlineData("argument")]
    [InlineData("two_keys")]
    [InlineData("not_a_target")]
    [InlineData("disabled")]
    public async Task UnadmittedRequestsAreRefusedWithoutSpending(string reason)
    {
        await using var fixture = await Seeded(reason == "short" ? TimeSpan.FromDays(29) : TimeSpan.FromDays(40));
        await new EnhancementArticlesPlugin().ActivateAsync(fixture.Articles(), default);
        await new ItemEnhancementPlugin().ActivateAsync(fixture.Economy(reason == "disabled" ? null : Enabled(30)), default);
        var before = (await fixture.Store.ReadLaboratoryInventoryAsync(1, fixture.Scope, default)).CharacterBag;
        long[] keys = reason switch
        {
            "article_key_selector" => [checked((long)PetArticleKeys.First)],
            "two_keys" => [Target, Target],
            "not_a_target" => [202020],
            _ => [Target],
        };
        Assert.True(await fixture.Router.DispatchAsync(EnhancementPluginFixture.Protocol, fixture.Session,
            new LabOperationRequested(LabOperation.ChangeUnlimited, keys, reason == "argument" ? 1 : 0), default));

        Assert.Equal(["refused"], fixture.Order);
        Assert.Empty(fixture.Converted);
        Assert.Equal(before, (await fixture.Store.ReadLaboratoryInventoryAsync(1, fixture.Scope, default)).CharacterBag);
    }

    [Theory]
    [InlineData(202020)]
    [InlineData(Target)]
    public async Task TheCardMustBeACountedNonTargetMaterial(int card)
    {
        await using var fixture = await Seeded(TimeSpan.FromDays(40));
        await new EnhancementArticlesPlugin().ActivateAsync(fixture.Articles(), default);
        var config = Enabled(30);
        config["conversion"] = new Dictionary<string, object?> { ["enabled"] = true, ["card_item_id"] = card, ["minimum_days"] = 30 };
        await Assert.ThrowsAsync<ConfigException>(() => new ItemEnhancementPlugin().ActivateAsync(fixture.Economy(config), default).AsTask());
    }

    private static Dictionary<string, object?> Enabled(int minimumDays) => new()
    {
        ["conversion"] = new Dictionary<string, object?> { ["enabled"] = true, ["card_item_id"] = Card, ["minimum_days"] = minimumDays },
    };

    private static async Task<EnhancementPluginFixture> Seeded(TimeSpan remaining, params int[] timed)
    {
        var fixture = await EnhancementPluginFixture.CreateAsync(conversion: true);
        timed = timed.Length == 0 ? [Target] : timed;
        var rows = (await fixture.Store.ReadLaboratoryInventoryAsync(1, fixture.Scope, default)).CharacterBag
            .Select(row => timed.Contains(row.ItemId)
                ? row with { Flags = 0, Quantity = 1, Lifetime = InventoryLifetime.Until(DateTimeOffset.UtcNow.Add(remaining)) }
                : row).ToArray();
        await fixture.Store.ReplaceSlotsAsync(1, InventoryBag.CharacterBag, rows, default);
        await fixture.Store.ReplaceSlotsAsync(1, InventoryBag.EquippedBag,
            rows.Where(row => !timed.Contains(row.ItemId)).Select(row => row with { Bag = InventoryBag.EquippedBag }).ToArray(), default);
        return fixture;
    }

    private static Dictionary<string, object?> WithSets(int? quantity) => new()
    {
        ["conversion"] = new Dictionary<string, object?>
        {
            ["enabled"] = true, ["card_item_id"] = Card, ["minimum_days"] = 30, ["set_card_quantity"] = quantity,
        },
    };

    private static LabOperationRequested SetRequest(int set = EnhancementPluginFixture.ConversionSet, long argument = 0) =>
        new(LabOperation.ChangeUnlimited, [set], argument);

    /// <summary>A native set converts every member for the stated cards, with the inventory published before the reply.</summary>
    [Fact]
    public async Task ASetBecomesPermanentMemberByMemberForItsCards()
    {
        const int A = EnhancementPluginFixture.SetMemberA, B = EnhancementPluginFixture.SetMemberB;
        await using var fixture = await Seeded(TimeSpan.FromDays(40), A, B);
        await new EnhancementArticlesPlugin().ActivateAsync(fixture.Articles(), default);
        await new ItemEnhancementPlugin().ActivateAsync(fixture.Economy(WithSets(2)), default);

        Assert.True(await fixture.Router.DispatchAsync(EnhancementPluginFixture.Protocol, fixture.Session, SetRequest(), default));
        Assert.Equal(["inventory", "converted"], fixture.Order);
        var inventory = Assert.Single(fixture.Inventories);
        Assert.True(inventory.IsCompleteSnapshot);
        var first = inventory.CharacterBag.Single(row => row.ItemId == A);
        var second = inventory.CharacterBag.Single(row => row.ItemId == B);
        Assert.All(new[] { first, second }, row =>
        {
            Assert.Equal(InventoryLifetimeKind.Permanent, row.Lifetime.Kind);
            Assert.NotNull(row.ArticleIdentity);
        });
        Assert.Equal([(ulong)EnhancementPluginFixture.ConversionSet, first.ArticleIdentity!.ArticleKey],
            Assert.Single(fixture.Converted).Keys);
        Assert.Equal(6, inventory.CharacterBag.Single(row => row.ItemId == Card).Quantity);

        // Both members are permanent now, so the same set is refused and spends nothing.
        fixture.Order.Clear();
        Assert.True(await fixture.Router.DispatchAsync(EnhancementPluginFixture.Protocol, fixture.Session, SetRequest(), default));
        Assert.Equal(["refused"], fixture.Order);
        Assert.Equal(6, (await fixture.Store.ReadLaboratoryInventoryAsync(1, fixture.Scope, default)).CharacterBag
            .Single(row => row.ItemId == Card).Quantity);
    }

    /// <summary>Any member that fails its guard, a short card stock or a missing policy leaves the whole set untouched.</summary>
    [Theory]
    [InlineData("no_policy")]
    [InlineData("short_member")]
    [InlineData("member_not_timed")]
    [InlineData("too_few_cards")]
    [InlineData("argument")]
    [InlineData("not_unlimited_set")]
    public async Task AnUnadmittedSetIsRefusedWithoutSpending(string reason)
    {
        const int A = EnhancementPluginFixture.SetMemberA, B = EnhancementPluginFixture.SetMemberB;
        await using var fixture = await Seeded(TimeSpan.FromDays(40), reason == "member_not_timed" ? [A] : [A, B]);
        if (reason == "short_member")
        {
            var rows = (await fixture.Store.ReadLaboratoryInventoryAsync(1, fixture.Scope, default)).CharacterBag
                .Select(row => row.ItemId == B ? row with { Lifetime = InventoryLifetime.Until(DateTimeOffset.UtcNow.AddDays(5)) } : row).ToArray();
            await fixture.Store.ReplaceSlotsAsync(1, InventoryBag.CharacterBag, rows, default);
        }
        await new EnhancementArticlesPlugin().ActivateAsync(fixture.Articles(), default);
        await new ItemEnhancementPlugin().ActivateAsync(
            fixture.Economy(WithSets(reason == "no_policy" ? null : reason == "too_few_cards" ? 9 : 2)), default);
        var before = (await fixture.Store.ReadLaboratoryInventoryAsync(1, fixture.Scope, default)).CharacterBag;

        Assert.True(await fixture.Router.DispatchAsync(EnhancementPluginFixture.Protocol, fixture.Session,
            reason == "not_unlimited_set" ? SetRequest(EnhancementPluginFixture.PlainSet) : SetRequest(argument: reason == "argument" ? 1 : 0), default));

        Assert.Equal(["refused"], fixture.Order);
        Assert.Empty(fixture.Converted);
        Assert.Equal(before, (await fixture.Store.ReadLaboratoryInventoryAsync(1, fixture.Scope, default)).CharacterBag);
    }

    /// <summary>A set card quantity of zero is a configuration error, never a free conversion.</summary>
    [Fact]
    public async Task ASetQuantityMustBePositive()
    {
        await using var fixture = await Seeded(TimeSpan.FromDays(40));
        await new EnhancementArticlesPlugin().ActivateAsync(fixture.Articles(), default);
        await Assert.ThrowsAnyAsync<Exception>(() => new ItemEnhancementPlugin().ActivateAsync(fixture.Economy(WithSets(0)), default).AsTask());
    }
}
