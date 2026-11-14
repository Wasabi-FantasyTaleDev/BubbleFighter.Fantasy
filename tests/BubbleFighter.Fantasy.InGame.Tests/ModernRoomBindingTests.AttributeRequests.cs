using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class RoomCommandsBindingTests
{
    /// <summary>A real scene view completion remains observable without queueing an operation inside the active room callback.</summary>
    [Fact]
    public async Task MountedInventoryViewCompletesRoomInstallWithoutReenteringItsExecutor()
    {
        using var harness = await Harness.CreateAsync();
        await harness.MountRealViewsAsync();
        await harness.Dispatch(harness.Host, 402, CreateBody());
        var initial = Assert.Single(harness.CompletedViews);
        Assert.True(initial.IsSceneReplay);
        Assert.False(initial.SelectionChange);
        Assert.Equal(SessionPhase.Room, harness.Host.Features.Get<IPlayerSession>().Phase);

        var seat = harness.Room.Seats.Single();
        var modifiers = new RoomItemAttribute[] { new(203100, 7, [1, 2, 3, 4], 0, "") };
        seat.EquippedItemIds = [300001, 203100];
        seat.ItemAttributes = modifiers;
        var view = new RoomMemberView(seat.Slot, 11, "host", 1, 1, false, 300001, seat.EquippedItemIds)
            { ItemAttributes = modifiers };
        var settings = RoomProjection.Create(Tables, "kr-1256", CreateBody()).BuildSettings;
        var room = new RoomView(harness.Room.Id, "room", 1, "rel_Map", 4, seat.Slot, false, [], [view], settings);
        harness.CompletedViews.Clear();
        await harness.Room.Executor.ExecuteAsync(ct => harness.Sender<RoomEntered>().SendAsync(harness.Host,
            new(room, seat.Slot) { Reason = RoomEntryReason.Refreshed }, ct));
        var replayed = Assert.Single(harness.CompletedViews);
        Assert.True(replayed.IsSceneReplay);
        Assert.False(replayed.SelectionChange);
        Assert.Equal(new uint[] { 1, 2, 3, 4 }, Assert.Single(replayed.View.ItemAttributes).Values);
        Assert.Same(modifiers, seat.ItemAttributes);
    }

    /// <summary>Room and plaza follow-up views carry the exact installed attributes, including an explicit empty clear.</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public async Task SceneViewRequestsRetainTheInstalledAttributeSnapshot(string build)
    {
        using var harness = await Harness.CreateAsync(build: build);
        uint[] words = [1, 2, 3, 4];
        var self = new RoomMemberView(0, 11, "host", 1, 1, false, 300001, [300001, 203100])
        {
            ItemAttributes = [new(203100, 7, words, 0, "")],
        };
        var peer = new RoomMemberView(1, 22, "guest", 1, 2, false, 300001, [300001, 203101]);
        var settings = new RoomBuildSettings(build,
            PacketCodec.Write(Tables, build, "GameSetting", new Dictionary<string, object?>()))
        {
            SessionPrefix = PacketCodec.Write(Tables, build, "SessionSetting", new Dictionary<string, object?>()),
        };
        var room = new RoomView(1, "room", 1, "rel_Map", 4, 0, false, [], [self, peer], settings);
        await harness.Sender<RoomEntered>().SendAsync(harness.Host, new(room, 0), default);
        var ownView = Assert.Single(harness.RequestedViews, request => !request.Remote);
        var peerView = Assert.Single(harness.RequestedViews, request => request.Remote);
        words[3] = 99;
        Assert.Equal(new uint[] { 1, 2, 3, 4 }, Assert.Single(ownView.ItemAttributes!).Values);
        Assert.NotNull(peerView.ItemAttributes);
        Assert.Empty(peerView.ItemAttributes);
        Assert.Equal(peer.EquippedItemIds, peerView.EquippedItemIds);

        harness.RequestedViews.Clear();
        var member = new PlazaMember(1, 22, "guest", 1, 300001, [300001, 203101])
        {
            ItemAttributes = [new(203101, 7, [5, 6, 7, 8], 0, "")],
        };
        await harness.Sender<PlazaMemberEntered>().SendAsync(harness.Host, new(member), default);
        var entered = Assert.Single(harness.RequestedViews);
        Assert.True(entered.Remote);
        Assert.Equal(new uint[] { 5, 6, 7, 8 }, Assert.Single(entered.ItemAttributes!).Values);

        harness.RequestedViews.Clear();
        var ownMember = new PlazaMember(0, 11, "host", 1, 300001, [300001, 203100]) { ItemAttributes = [] };
        await harness.Sender<PlazaMenuReturnRequested>().SendAsync(harness.Host, new() { You = ownMember }, default);
        var returned = Assert.Single(harness.RequestedViews);
        Assert.False(returned.Remote);
        Assert.NotNull(returned.ItemAttributes);
        Assert.Empty(returned.ItemAttributes);
    }
}
