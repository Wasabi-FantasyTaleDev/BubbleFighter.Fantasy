using System.Numerics;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.InGame.Data;
using BubbleFighter.Fantasy.InGame.Sessions;
using BubbleFighter.Fantasy.Tests;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class RoundFlowTests
{
    private static Rig FallbackRig(int count = 4, int mode = 4) => new(randomSupplyItemKinds: [101, 112, 129],
        nativeItemSlotCount: 1, configure: options =>
        {
            options.ItemModeIds = [mode]; options.ItemModeKinds = [101, 112, 129];
            options.ItemModeFallbackPointCount = count; options.RenewFullSlotSupplies = true;
            options.SupplyDefaultMinMs = 10; options.SupplyDefaultMaxMs = 10;
        });

    private sealed class FallbackStore(byte[] bytes) : IClientDataStore
    {
        internal List<string> Reads { get; } = [];
        public bool Exists(string path) => path is "Maps/map/track.wb" or "Maps/stage/sub/track.wb";
        public byte[] Read(string path) { Reads.Add(path); return Exists(path) ? bytes : throw new FileNotFoundException(path); }
    }

    private static FallbackStore FallbackMap() => new(WorldBlockFixture.Create(
        ("#sp_star_1_00", new(10, 2, 10)), ("#sp_star_1_01", new(20, 2, 10)),
        ("#sp_star_1_02", new(10, 2, 20)), ("#sp_star_1_03", new(20, 2, 20)),
        ("#sp_star_1_duplicate", new(10, 2, 10)), ("#ic_waterBombZombie_00", new(900, 2, 900))));

    /// <summary>Neutral map boxes accept both teams' client rolls, refuse a full slot, then renew for a retry.</summary>
    [Fact]
    public async Task ItemModeFallbackCompletesSingleSlotGiantAndTeamMedipackLoop()
    {
        var rig = FallbackRig();
        var data = FallbackMap();
        rig.Round.ConfigureItemModeFallback(data);
        try
        {
            await rig.StartAsync();
            var room = rig.Registry.Find(rig.RoomId)!;
            lock (room.Gate) { room.RoundStageKey = "stage/sub"; room.FindByGame(rig.Host.Id)!.Team = 1; room.FindByGame(rig.Guest.Id)!.Team = 2; }
            await rig.Round.HandleSuppliesReportedAsync(rig.Guest, new([]), default);
            Assert.Empty(data.Reads);
            await rig.Round.HandleSuppliesReportedAsync(rig.Host, new([]), default);
            await rig.Round.HandleSuppliesReportedAsync(rig.Host, new([]), default);
            Assert.Equal("Maps/stage/sub/track.wb", Assert.Single(data.Reads));
            await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
            await rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default);
            await rig.WaitStartedAsync(rig.Host);
            var boxes = await WaitSuppliesAsync(rig, rig.Host, 4);
            Assert.Equal(4, boxes.Length);
            Assert.All(boxes, box => { Assert.Equal(0, box.Point.Kind); Assert.Equal(0, box.Point.Team); Assert.Equal(10, box.Point.MinimumMs); });
            Assert.Equal(4, boxes.Select(box => (box.Point.X, box.Point.Y, box.Point.Z)).Distinct().Count());
            lock (room.Gate) foreach (var point in room.Supplies!.Points) point.NextAt = long.MaxValue;
            await rig.Round.HandleSupplyPickupAsync(rig.Host, new(boxes[0].ObjectId, 136), default);
            Assert.Empty(rig.SupplyPicked.To(rig.Host));
            await rig.Round.HandleSupplyPickupAsync(rig.Host, new(boxes[0].ObjectId, 112), default);
            await rig.Round.HandleSupplyPickupAsync(rig.Host, new(boxes[1].ObjectId, 129), default);
            Assert.Single(rig.SupplyPicked.To(rig.Host));
            await rig.Round.HandleSupplyPickupAsync(rig.Guest, new(boxes[2].ObjectId, 129), default);
            await rig.Round.HandleItemUseAsync(rig.Host, new(), default);
            var replacement = Assert.Single(rig.SupplyCreated.To(rig.Host).Skip(4));
            Assert.NotEqual(boxes[1].ObjectId, replacement.ObjectId);
            Assert.Equal(boxes[1].Point, replacement.Point);
            await rig.Round.HandleSupplyPickupAsync(rig.Host, new(boxes[1].ObjectId, 129), default);
            Assert.Equal(2, rig.SupplyPicked.To(rig.Host).Count(picked => picked.Seat >= 0));
            await rig.Round.HandleSupplyPickupAsync(rig.Host, new(replacement.ObjectId, 129), default);
            await rig.Round.HandleItemUseAsync(rig.Host, new(), default);
            await rig.Round.HandleItemUseAsync(rig.Host, new(), default);
            await rig.Round.HandleItemUseAsync(rig.Guest, new(), default);
            foreach (var member in new[] { rig.Host, rig.Guest })
            {
                var used = rig.ItemUsed.To(member);
                Assert.Equal(new[] { 112, 129, 129 }, used.Select(item => item.Kind));
                Assert.All(used, item => Assert.Equal(0, item.ItemSlot));
                Assert.Contains(new RoundSupplyPicked(-1, boxes[1].ObjectId), rig.SupplyPicked.To(member));
            }
            Assert.Empty(room.Supplies!.FullSlotRefusals);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>The fallback is dormant outside its configured modes and when a host already supplies a random point.</summary>
    [Theory]
    [InlineData(0, 4, false)]
    [InlineData(4, 9, false)]
    [InlineData(4, 4, true)]
    public async Task ItemModeFallbackLeavesDisabledOtherModesAndNativeRandomPointsAlone(int count, int mode, bool native)
    {
        var rig = FallbackRig(count, mode);
        var data = FallbackMap();
        rig.Round.ConfigureItemModeFallback(data);
        try
        {
            await rig.StartAsync();
            RoundSupplyPoint[] points = native ? [new(3, 4, 5, 0, 10, 10)] : [];
            await rig.Round.HandleSuppliesReportedAsync(rig.Host, new(points), default);
            Assert.Empty(data.Reads);
            Assert.Equal(points, rig.Registry.Find(rig.RoomId)!.Supplies!.Points.Select(point => point.Point));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>A native action-gauge drop received before the host's map report cannot suppress fallback points or lose held items.</summary>
    [Fact]
    public async Task EarlyGaugeDropSurvivesFirstMapReportAndDoesNotReplayItsLaunchOnRetry()
    {
        var rig = FallbackRig();
        rig.Round.ConfigureItemModeFallback(FallbackMap());
        try
        {
            await rig.StartAsync();
            await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
            await rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default);
            await rig.WaitStartedAsync(rig.Host);
            await rig.Round.HandleSupplyDropAsync(rig.Host, new(new(1, 2, 3, 0, 0, 0), 10000) { Velocity = new(4, 5, 6) }, default);
            var first = Assert.Single(rig.SupplyCreated.To(rig.Host));
            Assert.Equal(new Vector3(4, 5, 6), first.Velocity);
            await rig.Round.HandleSupplyPickupAsync(rig.Host, new(first.ObjectId, 112), default);
            await rig.Round.HandleSupplyDropAsync(rig.Guest, new(new(2, 3, 4, 0, 0, 0), 10000) { Velocity = new(7, 8, 9) }, default);
            var refused = rig.SupplyCreated.To(rig.Host).Last();
            await rig.Round.HandleSupplyPickupAsync(rig.Host, new(refused.ObjectId, 129), default);
            await rig.Round.HandleSuppliesReportedAsync(rig.Host, new([]), default);
            var room = rig.Registry.Find(rig.RoomId)!;
            lock (room.Gate) foreach (var point in room.Supplies!.Points) point.NextAt = long.MaxValue;
            Assert.Equal(4, room.Supplies!.Points.Length);
            await rig.Round.HandleItemUseAsync(rig.Host, new(), default);
            var retry = rig.SupplyCreated.To(rig.Host).Last();
            Assert.Equal(refused.Point, retry.Point);
            Assert.Equal(Vector3.Zero, retry.Velocity);
            Assert.NotEqual(refused.ObjectId, retry.ObjectId);
            Assert.Equal(112, Assert.Single(rig.ItemUsed.To(rig.Host)).Kind);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Worker ownership ends with its generation, so a discarded plan cannot emit late boxes into another round.</summary>
    [Fact]
    public async Task ItemModeFallbackWorkerStopsWhenItsGenerationEnds()
    {
        var rig = FallbackRig();
        rig.Round.ConfigureItemModeFallback(FallbackMap());
        try
        {
            await rig.StartAsync();
            await rig.Round.HandleSuppliesReportedAsync(rig.Host, new([]), default);
            await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
            await rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default);
            await rig.WaitStartedAsync(rig.Host);
            await WaitSuppliesAsync(rig, rig.Host, 4);
            var room = rig.Registry.Find(rig.RoomId)!;
            Task worker;
            lock (room.Gate) { worker = room.Supplies!.Worker; room.ResetToWaiting(); room.MarkStarting(); room.MarkPlaying(); }
            await worker.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(4, rig.SupplyCreated.To(rig.Host).Length);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Reports queued before a new round, cancellation or connection replacement cannot install or create supplies.</summary>
    [Theory]
    [InlineData(false, "restart")]
    [InlineData(false, "cancel")]
    [InlineData(false, "replace")]
    [InlineData(true, "restart")]
    [InlineData(true, "cancel")]
    [InlineData(true, "replace")]
    public async Task QueuedItemMapReportAndDropKeepTheirOriginalRoundAndConnection(bool drop, string transition)
    {
        var rig = FallbackRig();
        var data = FallbackMap();
        rig.Round.ConfigureItemModeFallback(data);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? blocker = null;
        try
        {
            await rig.StartAsync();
            await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
            await rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default);
            await rig.WaitStartedAsync(rig.Host);
            var room = rig.Registry.Find(rig.RoomId)!;
            blocker = room.Executor.ExecuteAsync(async _ =>
            {
                entered.SetResult();
                await release.Task;
                lock (room.Gate)
                {
                    if (transition == "restart") { room.ResetToWaiting(); room.MarkStarting(); room.MarkPlaying(); }
                    else if (transition == "cancel") room.RoundCancelled = true;
                    else
                    {
                        var previous = room.FindByGame(rig.Host.Id)!;
                        room.RemoveSession(rig.Host.Id);
                        room.Add(CaptainPlayer(rig.Host.Id, "replacement"), previous.Key, previous.UserNum, previous.Nickname);
                    }
                }
            }, default);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var pending = drop
                ? rig.Round.HandleSupplyDropAsync(rig.Host, new(new(1, 2, 3, 0, 0, 0), 10000), default).AsTask()
                : rig.Round.HandleSuppliesReportedAsync(rig.Host, new([]), default).AsTask();
            Assert.False(pending.IsCompleted);
            release.SetResult();
            await Task.WhenAll(blocker, pending);
            Assert.Empty(data.Reads);
            Assert.Null(room.Supplies);
            Assert.Empty(rig.SupplyCreated.To(rig.Guest));
        }
        finally
        {
            release.TrySetResult();
            if (blocker is not null) await blocker;
            await rig.Registry.StopAllAsync();
        }
    }

    [Fact]
    public void ItemModeFallbackUsesSupplyHelpersOnlyWithoutStarHelpersAndSpreadsItsSelection()
    {
        var data = new FallbackStore(WorldBlockFixture.Create(("#ic_first", new(0, 1, 0)),
            ("#ic_near", new(1, 1, 0)), ("#ic_far", new(100, 1, 0)), ("#spawn_T11", new(200, 1, 0))));
        var selected = ItemModeMapPoints.Load(data, "map", 4096, 20, 2);
        Assert.Equal(2, selected.Count);
        Assert.Contains(new Vector3(100, 1, 0), selected);
        Assert.DoesNotContain(new Vector3(200, 1, 0), selected);
        Assert.Throws<InvalidDataException>(() => ItemModeMapPoints.Load(data, "../map", 4096, 20, 2));
    }

    /// <summary>Unavailable fallback input leaves the native drop ledger usable and allows a corrected report to retry.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ItemModeFallbackResourceFailureDoesNotReplaceExistingDroppedItems(bool missing)
    {
        var rig = FallbackRig();
        rig.Round.ConfigureItemModeFallback(new FallbackStore(WorldBlockFixture.Create(("#spawn_T11", new(1, 2, 3)))));
        try
        {
            await rig.StartAsync();
            await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
            await rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default);
            await rig.WaitStartedAsync(rig.Host);
            await rig.Round.HandleSupplyDropAsync(rig.Host, new(new(1, 2, 3, 0, 0, 0), 10000), default);
            var room = rig.Registry.Find(rig.RoomId)!;
            var state = room.Supplies!;
            if (missing) lock (room.Gate) room.RoundStageKey = "missing";
            await Assert.ThrowsAsync<InvalidDataException>(() => rig.Round.HandleSuppliesReportedAsync(rig.Host, new([]), default).AsTask());
            Assert.Same(state, room.Supplies);
            Assert.False(state.MapReported);
            Assert.Single(state.Dropped);
            var dropped = Assert.Single(rig.SupplyCreated.To(rig.Host));
            await rig.Round.HandleSupplyPickupAsync(rig.Host, new(dropped.ObjectId, 129), default);
            Assert.Equal(129, Assert.Single(rig.SupplyPicked.To(rig.Host)).ItemKind);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }
}
