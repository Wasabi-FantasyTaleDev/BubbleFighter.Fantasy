using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Kernel;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class RoundFlowTests
{
    /// <summary>The audited CN2014 profiles admit two ordinary items, switch and reuse the freed slot.</summary>
    [Theory]
    [InlineData("cn-705")]
    [InlineData("cn-706")]
    public async Task Cn2014ComposedItemPoolSupportsItsNativeTwoSlotLifecycle(string build)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "packets", "builds.yaml")))
            root = root.Parent ?? throw new InvalidOperationException("Repository packet tables were not found.");
        var rows = new ProfileComposer(Path.Combine(root.FullName, "profiles"),
            key => key is "BFF_MYSQL_PASSWORD" or "BFF_ADMIN_TOKEN" ? "isolated-test-placeholder" : null).Compose(build, []);
        var options = new PluginConfig(Assert.Single(rows, row => row.Id == "ingame")).Bind<InGameOptions>();
        Assert.True(new PluginConfig(Assert.Single(rows, row => row.Id == "dialect.rooms")).Bind<RoomsOptions>().SupplyItems);
        Assert.Equal(2, options.NativeItemSlotCount);
        Assert.Equal([3], options.ItemModeIds);
        Assert.Equal([101, 102, 108, 109, 115, 120, 123, 127, 128, 130, 136, 137, 138, 139, 141], options.ItemModeKinds);
        Assert.Equal(16, options.CountedItemModeId);
        Assert.Equal(3, options.CountedItemSlotCount);
        Assert.Equal([229, 146, 136], options.CountedItemKinds);
        var rig = new Rig(nativeItemSlotCount: options.NativeItemSlotCount,
            randomSupplyItemKinds: options.RandomSupplyItemKinds, configure: configured =>
        {
            configured.ItemModeIds = options.ItemModeIds;
            configured.ItemModeKinds = options.ItemModeKinds;
        });
        try
        {
            await rig.StartAsync();
            var room = rig.Registry.Find(rig.RoomId)!;
            lock (room.Gate) room.ModeId = 3;
            await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
            await rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default);
            await rig.WaitStartedAsync(rig.Host);
            async Task<int> Drop(int kind)
            {
                await rig.Round.HandleSupplyDropAsync(rig.Guest, new(new(1, 2, 3, kind, 0, 0), 10000), default);
                return rig.SupplyCreated.To(rig.Host).Last().ObjectId;
            }
            foreach (var kind in new[] { 101, 102 })
                await rig.Round.HandleSupplyPickupAsync(rig.Host, new(await Drop(kind), kind), default);
            Assert.Equal(new[] { 0, 1 }, rig.SupplyPicked.To(rig.Host).Select(item => item.ItemSlot));
            var waiting = await Drop(108);
            await rig.Round.HandleSupplyPickupAsync(rig.Host, new(waiting, 108), default);
            Assert.Equal(2, rig.SupplyPicked.To(rig.Host).Length);
            await rig.Round.HandleItemSlotSwitchAsync(rig.Host, new(), default);
            Assert.Equal(new RoundItemSlotChanged(0, 1, 102), Assert.Single(rig.ItemSlotChanged.To(rig.Guest)));
            await rig.Round.HandleItemUseAsync(rig.Host, new(), default);
            Assert.Equal(new RoundItemUsed(0) { Kind = 102, ItemSlot = 1 }, Assert.Single(rig.ItemUsed.To(rig.Guest)));
            await rig.Round.HandleSupplyPickupAsync(rig.Host, new(waiting, 108), default);
            Assert.Equal(new RoundSupplyPicked(0, waiting) { ItemKind = 108, ItemSlot = 1 }, rig.SupplyPicked.To(rig.Guest).Last());
        }
        finally { await rig.Registry.StopAllAsync(); }
    }
}
