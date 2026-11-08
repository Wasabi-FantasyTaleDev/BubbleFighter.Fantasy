using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Kernel;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.InventoryView.Tests;

public sealed class EquipmentWearGroupsTests
{
    [Fact]
    public async Task StableContributionsReuseTheSnapshotAndRemovalDropsTheirRules()
    {
        var events = new EventBus(NullLogger.Instance);
        var resolver = new EquipmentWearGroups(events, "kr-1256/game");
        InventoryWearGroup[] baseline = [new(300001, 300031, 300001)];
        var pet = Explicit(301001, 301002);
        using var effect = Register(events, pet);
        var first = await resolver.ResolveAsync(baseline, default);

        Assert.Same(first, await resolver.ResolveAsync(baseline, default));
        Assert.Collection(first, group => Assert.Same(baseline[0], group), group => Assert.True(group.RejectWhenOccupied));
        effect.Dispose();
        Assert.Same(baseline, await resolver.ResolveAsync(baseline, default));

        using var replacement = Register(events, Explicit(301003, 301004));
        var changed = await resolver.ResolveAsync(baseline, default);
        Assert.NotSame(first, changed);
        Assert.False(changed[1].Contains(301001));
        Assert.True(changed[1].Contains(301003));
    }

    [Fact]
    public async Task ContributionsStayWithTheirProtocol()
    {
        var events = new EventBus(NullLogger.Instance);
        using var effect = Register(events, Explicit(301001, 301002));
        InventoryWearGroup[] baseline = [];

        Assert.Same(baseline, await new EquipmentWearGroups(events, "cn-774/game").ResolveAsync(baseline, default));
        Assert.Single(await new EquipmentWearGroups(events, "kr-1256/game").ResolveAsync(baseline, default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AddedPositionsCannotOverlapExistingRangesOrCatalogGroups(bool explicitBaseline)
    {
        var events = new EventBus(NullLogger.Instance);
        using var effect = Register(events, Explicit(301001, 301002));
        InventoryWearGroup[] baseline = [explicitBaseline ? Explicit(301001, 301010) : new(301000, 301100, 301001)];

        var error = await Assert.ThrowsAsync<ConfigException>(() =>
            new EquipmentWearGroups(events, "kr-1256/game").ResolveAsync(baseline, default).AsTask());
        Assert.Contains("overlap", error.Message);
    }

    [Fact]
    public async Task AddedPositionsCannotOverlapEachOther()
    {
        var events = new EventBus(NullLogger.Instance);
        using var effect = Register(events, Explicit(301001, 301002), Explicit(301002, 301003));

        var error = await Assert.ThrowsAsync<ConfigException>(() =>
            new EquipmentWearGroups(events, "kr-1256/game").ResolveAsync([], default).AsTask());
        Assert.Contains("overlap", error.Message);
    }

    [Fact]
    public async Task AddedPositionsCannotRepeatAnIdentity()
    {
        var events = new EventBus(NullLogger.Instance);
        using var effect = Register(events, Explicit(301001, 301001));

        var error = await Assert.ThrowsAsync<ConfigException>(() =>
            new EquipmentWearGroups(events, "kr-1256/game").ResolveAsync([], default).AsTask());
        Assert.Contains("repeated", error.Message);
    }

    [Fact]
    public async Task CombinedPositionCountIsBounded()
    {
        var events = new EventBus(NullLogger.Instance);
        using var effect = Register(events, Explicit(301001));
        var baseline = Enumerable.Range(1, InventoryLimits.MaxWearGroups).Select(id => new InventoryWearGroup(id, id, id)).ToArray();

        var error = await Assert.ThrowsAsync<ConfigException>(() =>
            new EquipmentWearGroups(events, "kr-1256/game").ResolveAsync(baseline, default).AsTask());
        Assert.Contains("combined limit", error.Message);
    }

    [Fact]
    public async Task ContributionsShareABoundedMembershipBudget()
    {
        var events = new EventBus(NullLogger.Instance);
        using var effect = Register(events, Explicit(Enumerable.Range(1, InventoryLimits.MaxItemsPerWearGroup).ToArray()),
            Explicit(InventoryLimits.MaxItemsPerWearGroup + 1));

        var error = await Assert.ThrowsAsync<ConfigException>(() =>
            new EquipmentWearGroups(events, "kr-1256/game").ResolveAsync([], default).AsTask());
        Assert.Contains("bounded explicit", error.Message);
    }

    private static InventoryWearGroup Explicit(params int[] ids) => new(ids.Min(), ids.Max(), ids[0])
    {
        ItemIds = Array.AsReadOnly(ids),
        RejectWhenOccupied = true,
    };

    private static IDisposable Register(EventBus events, params InventoryWearGroup[] groups) =>
        events.Intercept<EquipmentWearGroupsRequested, IReadOnlyList<InventoryWearGroup>>((request, next, ct) =>
            request.Protocol == "kr-1256/game" ? ValueTask.FromResult<IReadOnlyList<InventoryWearGroup>>(groups.ToArray()) : next(ct));
}
