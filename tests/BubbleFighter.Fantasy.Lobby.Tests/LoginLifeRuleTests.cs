using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Persistence.Memory;
using Xunit;

namespace BubbleFighter.Fantasy.Lobby.Tests;

/// <summary>
/// The login half of the life rule over the in-process store, followed by the expiry sweep the way a login
/// runs them: life items lose a life per whole UTC day, an item at zero is removed and reported, and other
/// players, other item kinds and the repeat login of the same day are untouched.
/// </summary>
public sealed class LoginLifeRuleTests
{
    private static readonly DateTimeOffset Monday = new(2026, 10, 12, 23, 59, 0, TimeSpan.Zero);

    private static InventorySlotRecord Row(long user, InventoryBag bag, int itemId, int quantity, InventoryLifetime lifetime) =>
        new(user, bag, itemId, itemId, quantity, 0) { Lifetime = lifetime };

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; }
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static async Task<(MemoryInventoryRepository Store, Clock Clock, LoginLifeRule Rule, InventoryExpirySweep Sweep)> Setup(
        long user, params (int Id, int Quantity, InventoryLifetime Lifetime)[] rows)
    {
        var store = new MemoryInventoryRepository(new MemoryPlayerTable(8));
        foreach (var bag in new[] { InventoryBag.CharacterBag, InventoryBag.EquippedBag })
            await store.ReplaceSlotsAsync(user, bag, rows.Select(r => Row(user, bag, r.Id, r.Quantity, r.Lifetime)).ToArray(), default);
        var clock = new Clock { Now = Monday };
        return (store, clock, new LoginLifeRule(store, clock), new InventoryExpirySweep(store, null, StarterKit.Default, clock));
    }

    private static async Task<int> Life(MemoryInventoryRepository store, long user, int itemId) =>
        (await store.ListSlotsAsync(user, InventoryBag.CharacterBag, 512, default)).Single(row => row.ItemId == itemId).Quantity;

    [Fact]
    public async Task TheFirstLoginEverAndARepeatLoginTheSameDayChargeNothing()
    {
        var (store, clock, rule, _) = await Setup(7, (201350, 15, InventoryLifetime.Life));

        Assert.Equal(0, await rule.ApplyAsync(7, default));
        clock.Now = Monday.AddMinutes(1); // already Tuesday UTC
        Assert.Equal(1, await rule.ApplyAsync(7, default));
        clock.Now = Monday.AddHours(10);
        Assert.Equal(0, await rule.ApplyAsync(7, default));

        Assert.Equal(14, await Life(store, 7, 201350));
    }

    [Fact]
    public async Task OneMinuteAcrossMidnightIsOneDayAndTwentyThreeHoursInsideADayIsNone()
    {
        var (store, clock, rule, _) = await Setup(7, (201350, 15, InventoryLifetime.Life));
        clock.Now = new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.Zero);
        await rule.ApplyAsync(7, default);

        clock.Now = new DateTimeOffset(2026, 10, 12, 23, 0, 0, TimeSpan.Zero);
        Assert.Equal(0, await rule.ApplyAsync(7, default));
        clock.Now = new DateTimeOffset(2026, 10, 13, 0, 1, 0, TimeSpan.Zero);
        Assert.Equal(1, await rule.ApplyAsync(7, default));

        Assert.Equal(14, await Life(store, 7, 201350));
    }

    [Fact]
    public async Task SeveralMissedDaysAreChargedTogetherAndOtherPlayersAreUntouched()
    {
        var (store, clock, rule, _) = await Setup(7, (201350, 15, InventoryLifetime.Life), (100104, 20, InventoryLifetime.Permanent));
        await store.ReplaceSlotsAsync(8, InventoryBag.CharacterBag, [Row(8, InventoryBag.CharacterBag, 201350, 15, InventoryLifetime.Life)], default);
        await rule.ApplyAsync(7, default);
        await rule.ApplyAsync(8, default);

        clock.Now = Monday.AddDays(6);
        Assert.Equal(6, await rule.ApplyAsync(7, default));

        Assert.Equal(9, await Life(store, 7, 201350));
        Assert.Equal(20, await Life(store, 7, 100104));
        Assert.Equal(15, await Life(store, 8, 201350));
    }

    [Fact]
    public async Task AnItemWhoseLastLifeWentIsRemovedAndReportedByTheSweepOnceAndOnlyThatItem()
    {
        var (store, clock, rule, sweep) = await Setup(7, (300001, 1, InventoryLifetime.Permanent),
            (201350, 3, InventoryLifetime.Life), (202386, 20, InventoryLifetime.Life));
        await rule.ApplyAsync(7, default);
        Assert.Empty(await sweep.SweepAsync(7, default));

        clock.Now = Monday.AddDays(3); // exactly the three lives of the first item
        Assert.Equal(3, await rule.ApplyAsync(7, default));
        var ended = await sweep.SweepAsync(7, default);

        Assert.Equal([201350], ended);
        foreach (var bag in new[] { InventoryBag.CharacterBag, InventoryBag.EquippedBag })
            Assert.Equal([300001, 202386], (await store.ListSlotsAsync(7, bag, 512, default)).Select(row => row.ItemId));
        Assert.Equal(17, await Life(store, 7, 202386));
        Assert.Empty(await sweep.SweepAsync(7, default));
    }

    [Fact]
    public async Task LifeFloorsAtZeroEvenAfterAYearAway()
    {
        var (store, clock, rule, sweep) = await Setup(7, (201350, 15, InventoryLifetime.Life));
        await rule.ApplyAsync(7, default);

        clock.Now = Monday.AddDays(400);
        Assert.Equal(400, await rule.ApplyAsync(7, default));
        foreach (var bag in new[] { InventoryBag.CharacterBag, InventoryBag.EquippedBag })
            Assert.Equal(0, Assert.Single(await store.ListSlotsAsync(7, bag, 512, default)).Quantity);

        Assert.Equal([201350], await sweep.SweepAsync(7, default));
        foreach (var bag in new[] { InventoryBag.CharacterBag, InventoryBag.EquippedBag })
        {
            var character = Assert.Single(await store.ListSlotsAsync(7, bag, 512, default));
            Assert.Equal(StarterKit.DaoCharacterItemId, character.ItemId);
            Assert.Equal(InventoryLifetime.Permanent, character.Lifetime);
            if (bag == InventoryBag.CharacterBag)
                Assert.NotEqual(0, character.Flags & InventorySlotRecord.WornFlagBit);
        }
        Assert.Empty(await sweep.SweepAsync(7, default));
    }

    [Fact]
    public async Task ALifeRowWithLifeLeftIsNeverRemovedByTheSweep()
    {
        var (store, _, _, sweep) = await Setup(7, (201350, 1, InventoryLifetime.Life));

        Assert.Empty(await sweep.SweepAsync(7, default));
        Assert.Equal(1, await Life(store, 7, 201350));
    }

    [Theory]
    [InlineData("kr-812", true)]
    [InlineData("cn-717", true)]
    [InlineData("cn-324", false)]
    [InlineData("cn-279", false)]
    [InlineData("id-400", false)]
    [InlineData("id-1809", false)]
    [InlineData("cn-706", false)]
    [InlineData("cn-406", false)]
    [InlineData("cn-404", false)]
    [InlineData("cn-408", false)]
    [InlineData("kr-1256", false)]
    [InlineData("cn-735", false)]
    public void OnlyABuildWithTheLifeItemsCapabilityMayTurnTheRuleOn(string build, bool allowed)
    {
        var options = new LobbyOptions { BuildValues = true, LifeItems = true, PacketsDirectory = PacketsDirectory() };
        if (allowed)
            Assert.Equal(build, LobbyBuildValues.Apply(options, [build]));
        else
            Assert.Throws<ConfigException>(() => LobbyBuildValues.Apply(options, [build]));
    }

    [Fact]
    public void ABuildWithoutTheCapabilityStaysValidWhileTheRuleIsOff()
    {
        var options = new LobbyOptions { BuildValues = true, LifeItems = false, PacketsDirectory = PacketsDirectory() };
        Assert.Equal("cn-324", LobbyBuildValues.Apply(options, ["cn-324"]));
    }

    private static string PacketsDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "packets");
            if (File.Exists(Path.Combine(candidate, "builds.yaml")))
                return candidate;
        }
        throw new InvalidOperationException("The repository's packets directory was not found above the test output folder.");
    }
}
