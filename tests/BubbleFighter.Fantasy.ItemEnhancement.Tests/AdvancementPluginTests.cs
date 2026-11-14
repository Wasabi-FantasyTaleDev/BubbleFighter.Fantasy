using System.Text.Json;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Enhancement;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Party;
using BubbleFighter.Fantasy.Contracts.Sessions;
using Xunit;

namespace BubbleFighter.Fantasy.ItemEnhancement.Tests;

/// <summary>
/// The laboratory's clothing grade-up tab: rules advertised as the client's rule engine reads them, an atomic card debit, the
/// advancement attribute on one member, and the complete inventory published before the set and member reply.
/// </summary>
public sealed class AdvancementPluginTests
{
    private const int HcSet = EnhancementPluginFixture.HcSet, AdvancedSet = EnhancementPluginFixture.AdvancedHcSet, L4Set = EnhancementPluginFixture.L4Set;
    private const int HcCard = EnhancementPluginFixture.HcCard, AdvancedCard = EnhancementPluginFixture.AdvancedHcCard, L4Card = EnhancementPluginFixture.L4Card;
    private const int HcA = 300301, HcB = 300302, AdvA = 300311, AdvB = 300312, L4A = 300321, L4B = 300322;

    private static Dictionary<string, object?> Enabled(int? tabCard = HcCard, int hcQuantity = 2) => new()
    {
        ["advancement"] = new Dictionary<string, object?>
        {
            ["enabled"] = true,
            ["tab_card_item_id"] = tabCard,
            ["rules"] = new object[]
            {
                new Dictionary<string, object?> { ["card_item_id"] = HcCard, ["source_tier"] = 3, ["card_quantity"] = hcQuantity },
                new Dictionary<string, object?> { ["card_item_id"] = AdvancedCard, ["source_tier"] = 4, ["card_quantity"] = 1 },
                new Dictionary<string, object?> { ["card_item_id"] = L4Card, ["source_tier"] = 5, ["card_quantity"] = 3 },
            },
        },
    };

    private static async Task<(EnhancementPluginFixture Fixture, Func<int, long> Key)> StartAsync(Dictionary<string, object?>? config)
    {
        var fixture = await EnhancementPluginFixture.CreateAsync(equipment: true, advancement: true);
        await new EnhancementArticlesPlugin().ActivateAsync(fixture.Articles(), default);
        await new ItemEnhancementPlugin().ActivateAsync(fixture.Economy(config), default);
        var prepared = await fixture.PrepareAsync();
        Assert.NotNull(prepared);
        return (fixture, item => checked((long)prepared.CharacterBag.Single(row => row.ItemId == item).ArticleIdentity!.ArticleKey));
    }

    private static ValueTask<bool> RequestAsync(EnhancementPluginFixture fixture, long set, long[] members, long card) =>
        fixture.Router.DispatchAsync(EnhancementPluginFixture.Protocol, fixture.Session,
            new LabOperationRequested(LabOperation.MakeItemAdvanced, [set, .. members], card), default);

    private static InventorySlotRecord Member(EnhancementPluginFixture fixture, int item) =>
        fixture.Inventories[^1].CharacterBag.Single(row => row.ItemId == item);

    /// <summary>The tab is advertised with each tier's rules in the class the client keys them by, and only when enabled.</summary>
    [Fact]
    public async Task ConfigurationPublishesTheRulesTheClientEvaluates()
    {
        var (fixture, _) = await StartAsync(Enabled());
        await using var _1 = fixture;
        Assert.True(await fixture.Router.DispatchAsync(EnhancementPluginFixture.Protocol, fixture.Session, new ScienceLabConfigurationRequested(), default));
        var advancement = Assert.Single(fixture.Configurations).Advancement!;
        Assert.Equal(HcCard, advancement.TabCardItemId);
        Assert.Equal([1, 2, 3], advancement.Groups.Select(group => group.Key));
        var hc = advancement.Groups.Single(group => group.Key == 1).Rules;
        // An HC set that reached advanced HC keeps its class, so its next card lists effect 100 as required.
        Assert.Equal((HcCard, (byte)4), (hc[0].CardItemId, hc[0].TargetMarker));
        Assert.Equal(new[] { 100, 157 }, hc[0].BlockingEffects.ToArray());
        Assert.Empty(hc[0].RequiredEffects);
        Assert.Empty(hc[0].SetIds);
        Assert.Equal([AdvancedCard], hc.Skip(1).Select(rule => rule.CardItemId));
        Assert.Equal([100], hc[1].RequiredEffects);
        Assert.Equal(6, hc[1].TargetMarker);
        Assert.Equal([157], Assert.Single(advancement.Groups.Single(group => group.Key == 2).Rules).BlockingEffects);
        Assert.Equal([158], Assert.Single(advancement.Groups.Single(group => group.Key == 3).Rules).BlockingEffects);

        await using var off = await EnhancementPluginFixture.CreateAsync(equipment: true, advancement: true);
        await new EnhancementArticlesPlugin().ActivateAsync(off.Articles(), default);
        await new ItemEnhancementPlugin().ActivateAsync(off.Economy(), default);
        Assert.True(await off.Router.DispatchAsync(EnhancementPluginFixture.Protocol, off.Session, new ScienceLabConfigurationRequested(), default));
        Assert.Null(Assert.Single(off.Configurations).Advancement);
        Assert.True(await RequestAsync(off, HcSet, [1, 2], HcCard));
        Assert.Equal(["configuration", "refused"], off.Order);
    }

    /// <summary>One card spend installs advanced HC on the first member, then advanced L4HC replaces it, then nothing more applies.</summary>
    [Fact]
    public async Task AnHcSetClimbsToAdvancedL4ThroughItsEffects()
    {
        var (fixture, key) = await StartAsync(Enabled());
        await using var _1 = fixture;
        Assert.True(await RequestAsync(fixture, HcSet, [key(HcB), key(HcA)], HcCard));
        Assert.Equal(["inventory", "advanced"], fixture.Order);
        var inventory = Assert.Single(fixture.Inventories);
        Assert.True(inventory.IsCompleteSnapshot);
        Assert.Equal(6, inventory.CharacterBag.Single(row => row.ItemId == HcCard).Quantity);
        // The reply names the set and the member that carries the attribute: the first in the set row's column order.
        Assert.Equal(new LabAdvancementCompleted(HcSet, (ulong)key(HcA)), Assert.Single(fixture.Advanced));
        Assert.Equal(100, Member(fixture, HcA).Enhancements!.Find(fixture.Scope.Key)!.Advancement);
        Assert.Null(Member(fixture, HcB).Enhancements?.Find(fixture.Scope.Key));
        var shown = await fixture.PresentationAsync(Member(fixture, HcA));
        var attribute = Assert.Single(shown.Attributes);
        Assert.Equal(100u, attribute.Kind);
        Assert.Equal(new uint[] { 3, 3, 3 }, attribute.Values.ToArray());
        Assert.Empty((await fixture.PresentationAsync(Member(fixture, HcB))).Attributes);

        // The same card no longer applies: tier 3 is blocked once effect 100 is present.
        Assert.True(await RequestAsync(fixture, HcSet, [key(HcA), key(HcB)], HcCard));
        Assert.Equal("refused", fixture.Order[^1]);
        Assert.True(await RequestAsync(fixture, HcSet, [key(HcA), key(HcB)], AdvancedCard));
        Assert.Equal(["inventory", "advanced", "refused", "inventory", "advanced"], fixture.Order);
        Assert.Equal(7, Member(fixture, AdvancedCard).Quantity);
        Assert.Equal(157, Member(fixture, HcA).Enhancements!.Find(fixture.Scope.Key)!.Advancement);
        var top = Assert.Single((await fixture.PresentationAsync(Member(fixture, HcA))).Attributes);
        Assert.Equal(157u, top.Kind);
        Assert.Equal(new uint[] { 3, 3, 3, 4 }, top.Values.ToArray());
        Assert.True(await RequestAsync(fixture, HcSet, [key(HcA), key(HcB)], AdvancedCard));
        Assert.Equal("refused", fixture.Order[^1]);
        Assert.Equal(7, (await fixture.Store.ReadEnhancementInventoryAsync(1, fixture.Scope, default)).CharacterBag
            .Single(row => row.ItemId == AdvancedCard).Quantity);
    }

    /// <summary>Advanced HC and L4HC sets take their own cards and effects directly; a card of another tier is refused.</summary>
    [Theory]
    [InlineData(AdvancedSet, AdvA, AdvB, AdvancedCard, 157)]
    [InlineData(L4Set, L4A, L4B, L4Card, 158)]
    public async Task EachTierTakesItsOwnCardAndEffect(int set, int first, int second, int card, int effect)
    {
        var (fixture, key) = await StartAsync(Enabled());
        await using var _1 = fixture;
        Assert.True(await RequestAsync(fixture, set, [key(first), key(second)], HcCard));
        Assert.Equal(["refused"], fixture.Order);
        Assert.True(await RequestAsync(fixture, set, [key(second), key(first)], card));
        Assert.Equal(["refused", "inventory", "advanced"], fixture.Order);
        Assert.Equal(effect, Member(fixture, first).Enhancements!.Find(fixture.Scope.Key)!.Advancement);
        Assert.Equal(new LabAdvancementCompleted(set, (ulong)key(first)), Assert.Single(fixture.Advanced));
    }

    /// <summary>Requests the client could not have sent, or whose data is stale, spend nothing and change nothing.</summary>
    [Theory]
    [InlineData("one_member")]
    [InlineData("foreign_member")]
    [InlineData("repeated_member")]
    [InlineData("unknown_set")]
    [InlineData("zero_card")]
    [InlineData("not_an_article")]
    [InlineData("worn")]
    [InlineData("insufficient")]
    [InlineData("phase")]
    public async Task UnadmittedRequestsAreRefusedWithoutSpending(string reason)
    {
        var (fixture, key) = await StartAsync(Enabled());
        await using var _1 = fixture;
        if (reason == "phase") fixture.Session.Features.Get<IPlayerSession>().MoveTo(SessionPhase.Room);
        if (reason == "worn") await fixture.Store.UpdateLoadoutSelectionAsync(1, fixture.Scope, [], HcA, true, default);
        if (reason == "insufficient")
        {
            var rows = (await fixture.Store.ListSlotsAsync(1, InventoryBag.CharacterBag, 200, default)).Select(row =>
                row.ItemId == HcCard ? row with { Quantity = 1 } : row).ToArray();
            await fixture.Store.ReplaceSlotsAsync(1, InventoryBag.CharacterBag, rows, default);
        }
        var before = await fixture.Store.ReadEnhancementInventoryAsync(1, fixture.Scope, default);
        long[] members = reason switch
        {
            "one_member" => [key(HcA)],
            "foreign_member" => [key(HcA), key(AdvA)],
            "repeated_member" => [key(HcA), key(HcA)],
            "not_an_article" => [key(HcA), HcB],
            _ => [key(HcA), key(HcB)],
        };
        Assert.True(await RequestAsync(fixture, reason == "unknown_set" ? 2999999 : HcSet, members, reason == "zero_card" ? 0 : HcCard));
        Assert.Equal(["refused"], fixture.Order);
        Assert.Empty(fixture.Advanced);
        Assert.Equal(before.CharacterBag, (await fixture.Store.ReadEnhancementInventoryAsync(1, fixture.Scope, default)).CharacterBag);
    }

    /// <summary>An enhancement attempt after a grade-up keeps the grade-up, and both are shown.</summary>
    [Fact]
    public async Task EnhancingAGradedMemberKeepsItsGradeUp()
    {
        var (fixture, key) = await StartAsync(Enabled());
        await using var _1 = fixture;
        Assert.True(await RequestAsync(fixture, HcSet, [key(HcA), key(HcB)], HcCard));
        Assert.True(await fixture.Router.DispatchAsync(EnhancementPluginFixture.Protocol, fixture.Session,
            new LabOperationRequested(LabOperation.Enchant, [key(HcA)], 0), default));
        var state = Member(fixture, HcA).Enhancements!.Find(fixture.Scope.Key)!;
        Assert.Equal((ItemEnhancementKind.Clothing, 100), (state.Kind, state.Advancement));
        Assert.Equal(new[] { (uint)EnhancementPluginFixture.ClothingSelector, 100u },
            (await fixture.PresentationAsync(Member(fixture, HcA))).Attributes.Select(attribute => attribute.Kind).ToArray());
    }

    /// <summary>Unclear or inconsistent policy is a load error rather than a silent default.</summary>
    [Theory]
    [InlineData("tab_card_outside_rules")]
    [InlineData("missing_tab_card")]
    [InlineData("duplicate_card")]
    [InlineData("bad_tier")]
    [InlineData("missing_quantity")]
    [InlineData("card_not_counted")]
    [InlineData("set_not_clothing")]
    [InlineData("set_of_another_class")]
    public async Task PolicyErrorsFailTheLoad(string reason)
    {
        await using var fixture = await EnhancementPluginFixture.CreateAsync(equipment: true, advancement: true);
        await new EnhancementArticlesPlugin().ActivateAsync(fixture.Articles(), default);
        var config = Enabled(reason == "missing_tab_card" ? null : reason == "tab_card_outside_rules" ? 190111 : HcCard);
        var rules = (object[])((Dictionary<string, object?>)config["advancement"]!)["rules"]!;
        var first = (Dictionary<string, object?>)rules[0];
        switch (reason)
        {
            case "duplicate_card": ((Dictionary<string, object?>)rules[1])["card_item_id"] = HcCard; break;
            case "bad_tier": first["source_tier"] = 6; break;
            case "missing_quantity": first["card_quantity"] = null; break;
            case "card_not_counted": first["card_item_id"] = HcA; ((Dictionary<string, object?>)config["advancement"]!)["tab_card_item_id"] = HcA; break;
            case "set_not_clothing": first["set_ids"] = new[] { EnhancementPluginFixture.ConversionSet }; break;
            // The first rule upgrades tier 3 (class 1 sets); an L4 set is class 3 and could never match it.
            case "set_of_another_class": first["set_ids"] = new[] { L4Set }; break;
        }
        await Assert.ThrowsAsync<ConfigException>(() => new ItemEnhancementPlugin().ActivateAsync(fixture.Economy(config), default).AsTask());
    }

    /// <summary>A rule limited to named sets keeps its card from any other set, and the limit is published.</summary>
    [Fact]
    public async Task ARuleLimitedToSetsAdmitsOnlyThose()
    {
        var config = Enabled();
        var rules = (object[])((Dictionary<string, object?>)config["advancement"]!)["rules"]!;
        ((Dictionary<string, object?>)rules[2])["set_ids"] = new[] { L4Set };
        ((Dictionary<string, object?>)rules[2])["card_item_id"] = L4Card;
        var (fixture, key) = await StartAsync(config);
        await using var _1 = fixture;
        Assert.True(await fixture.Router.DispatchAsync(EnhancementPluginFixture.Protocol, fixture.Session, new ScienceLabConfigurationRequested(), default));
        Assert.Equal([L4Set], Assert.Single(Assert.Single(fixture.Configurations).Advancement!.Groups.Single(group => group.Key == 3).Rules).SetIds);
        Assert.True(await RequestAsync(fixture, L4Set, [key(L4A), key(L4B)], L4Card));
        Assert.Equal("advanced", fixture.Order[^1]);
    }

    /// <summary>The stored grade-up round trips, and a row stored before it existed reads as none.</summary>
    [Fact]
    public void TheStoredGradeUpRoundTripsAndOlderRowsReadAsNone()
    {
        var states = ItemEnhancements.Empty.With("kr-1256", new(ItemEnhancementKind.Clothing, 3, 157));
        var json = JsonSerializer.Serialize(states);
        Assert.Equal("""{"kr-1256":{"Kind":2,"Revision":3,"Advancement":157}}""", json);
        Assert.Equal(states, JsonSerializer.Deserialize<ItemEnhancements>(json));
        var older = JsonSerializer.Deserialize<ItemEnhancements>("""{"kr-1256":{"Kind":1,"Revision":2}}""")!;
        Assert.Equal(new(ItemEnhancementKind.Weapon, 2), older.Find("kr-1256"));
        Assert.Equal("""{"kr-1256":{"Kind":1,"Revision":2}}""", JsonSerializer.Serialize(older));
        Assert.Throws<InvalidDataException>(() => ItemEnhancements.Empty.With("kr-1256", new(ItemEnhancementKind.None, 1, -1)));
    }
}
