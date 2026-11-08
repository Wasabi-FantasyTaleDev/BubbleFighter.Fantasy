using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Kernel;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class RoundFlowTests
{
    private static Rig ResolvingItemRig(bool enabled = true, int mode = 4) => new(randomSupplyItemKinds: [112, 129],
        nativeItemSlotCount: 1, configure: options =>
        {
            options.ItemModeIds = [mode]; options.ItemModeKinds = [112, 129];
            options.ItemModeResolveInvalidRandomPickups = enabled; options.RenewFullSlotSupplies = true;
        });

    /// <summary>Each eligible random object grants one permitted kind, keeps a valid client roll and uses that same held kind.</summary>
    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 136)]
    [InlineData(false, -1)]
    [InlineData(false, 112)]
    [InlineData(true, 0)]
    [InlineData(true, 136)]
    [InlineData(true, -1)]
    [InlineData(true, 112)]
    public async Task RandomPickupResolutionGrantsAndUsesOneKindFromMapOrDrop(bool dropped, int requestedKind)
    {
        var rig = ResolvingItemRig();
        try
        {
            await rig.StartAsync();
            if (!dropped) await rig.Round.HandleSuppliesReportedAsync(rig.Host, new([new(1, 2, 3, 0, 10, 10)]), default);
            await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
            await rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default);
            await rig.WaitStartedAsync(rig.Host);
            if (dropped) await rig.Round.HandleSupplyDropAsync(rig.Guest, new(new(1, 2, 3, 0, 0, 0), 10000), default);
            var box = Assert.Single(await WaitSuppliesAsync(rig, rig.Host, 1));
            var room = rig.Registry.Find(rig.RoomId)!;
            lock (room.Gate) foreach (var point in room.Supplies!.Points) point.NextAt = long.MaxValue;
            await rig.Round.HandleSupplyPickupAsync(rig.Host, new(box.ObjectId, requestedKind), default);
            var granted = Assert.Single(rig.SupplyPicked.To(rig.Host));
            Assert.Contains(granted.ItemKind, new[] { 112, 129 });
            if (requestedKind == 112) Assert.Equal(112, granted.ItemKind);
            Assert.Equal(0, granted.ItemSlot);
            await rig.Round.HandleSupplyPickupAsync(rig.Guest, new(box.ObjectId, requestedKind), default);
            await rig.Round.HandleItemUseAsync(rig.Host, new(), default);
            await rig.Round.HandleItemUseAsync(rig.Host, new(), default);
            foreach (var member in new[] { rig.Host, rig.Guest })
            {
                Assert.Equal(granted, Assert.Single(rig.SupplyPicked.To(member)));
                Assert.Equal(granted.ItemKind, Assert.Single(rig.ItemUsed.To(member)).Kind);
            }
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Resolving an invalid roll cannot consume another kind, another team's object, a nonexistent id or a disabled policy's box.</summary>
    [Theory]
    [InlineData("fixed", 0)]
    [InlineData("fixed", 136)]
    [InlineData("team", 0)]
    [InlineData("unknown", 0)]
    [InlineData("disabled", 0)]
    [InlineData("disabled", 136)]
    [InlineData("disabled", -1)]
    [InlineData("other-mode", 0)]
    public async Task RandomPickupResolutionPreservesIneligibleObjects(string reason, int requestedKind)
    {
        var rig = ResolvingItemRig(enabled: reason != "disabled", mode: reason == "other-mode" ? 9 : 4);
        try
        {
            await rig.StartAsync();
            var room = rig.Registry.Find(rig.RoomId)!;
            lock (room.Gate) room.FindByGame(rig.Host.Id)!.Team = 1;
            await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
            await rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default);
            await rig.WaitStartedAsync(rig.Host);
            await rig.Round.HandleSupplyDropAsync(rig.Guest,
                new(new(1, 2, 3, reason == "fixed" ? 112 : 0, 0, 0, reason == "team" ? 2 : 0), 10000), default);
            var box = Assert.Single(rig.SupplyCreated.To(rig.Host));
            await rig.Round.HandleSupplyPickupAsync(rig.Host, new(reason == "unknown" ? int.MaxValue : box.ObjectId, requestedKind), default);
            Assert.Empty(rig.SupplyPicked.To(rig.Host));
            Assert.Empty(room.Supplies!.HeldItems);
            Assert.Empty(room.Supplies.FullSlotRefusals);
            Assert.Equal(box, Assert.Single(room.Supplies.Dropped).Value);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>A resolved result still observes single-slot capacity and waits for a replacement before granting the refused object.</summary>
    [Fact]
    public async Task RandomPickupResolutionDoesNotConsumeABoxWhileTheSingleSlotIsFull()
    {
        var rig = ResolvingItemRig();
        try
        {
            await rig.StartAsync();
            await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
            await rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default);
            await rig.WaitStartedAsync(rig.Host);
            await rig.Round.HandleSupplyDropAsync(rig.Guest, new(new(1, 2, 3, 112, 0, 0), 10000), default);
            await rig.Round.HandleSupplyPickupAsync(rig.Host, new(rig.SupplyCreated.To(rig.Host).Last().ObjectId, 112), default);
            await rig.Round.HandleSupplyDropAsync(rig.Guest, new(new(4, 5, 6, 0, 0, 0), 10000), default);
            var refused = rig.SupplyCreated.To(rig.Host).Last();
            await rig.Round.HandleSupplyPickupAsync(rig.Host, new(refused.ObjectId, 0), default);
            Assert.Single(rig.SupplyPicked.To(rig.Host));
            Assert.Contains(refused.ObjectId, rig.Registry.Find(rig.RoomId)!.Supplies!.Dropped.Keys);
            await rig.Round.HandleItemUseAsync(rig.Host, new(), default);
            var renewed = rig.SupplyCreated.To(rig.Host).Last();
            Assert.NotEqual(refused.ObjectId, renewed.ObjectId);
            await rig.Round.HandleSupplyPickupAsync(rig.Host, new(renewed.ObjectId, 136), default);
            var granted = rig.SupplyPicked.To(rig.Host).Last();
            Assert.Contains(granted.ItemKind, new[] { 112, 129 });
            await rig.Round.HandleItemUseAsync(rig.Host, new(), default);
            Assert.Equal(granted.ItemKind, rig.ItemUsed.To(rig.Host).Last().Kind);
            Assert.Equal(2, rig.SupplyPicked.To(rig.Host).Count(item => item.Seat >= 0));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    [Fact]
    public void RandomPickupResolutionRequiresASlottedPoolAndIsEnabledOnlyInCn279()
    {
        var invalid = new InGameOptions
        {
            ItemModeIds = [3], ItemModeKinds = [10], ImmediateSupplyItemKinds = [10],
            ItemModeResolveInvalidRandomPickups = true,
        };
        Assert.Contains(invalid.Validate(), error => error.ErrorMessage!.StartsWith("item_mode_resolve_invalid_random_pickups", StringComparison.Ordinal));
        var composer = new ProfileComposer(RepositoryProfiles(),
            key => key is "BFF_MYSQL_PASSWORD" or "BFF_ADMIN_TOKEN" ? "isolated-test-placeholder" : null);
        foreach (var build in new[] { "cn-279", "cn-324", "kr-812", "kr-1256" })
        {
            var row = Assert.Single(composer.Compose(build, []), candidate => candidate.Id == "ingame");
            var options = new PluginConfig(row).Bind<InGameOptions>();
            Assert.Equal(build == "cn-279", options.ItemModeResolveInvalidRandomPickups);
            if (build == "cn-279")
            {
                Assert.Equal(17, options.ItemModeKinds.Intersect(options.RandomSupplyItemKinds).Count());
                Assert.Equal(1, options.NativeItemSlotCount);
            }
        }
    }
}
