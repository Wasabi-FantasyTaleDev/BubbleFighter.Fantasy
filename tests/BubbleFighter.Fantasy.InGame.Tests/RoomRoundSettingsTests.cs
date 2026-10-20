using BubbleFighter.Fantasy.Contracts.Rooms;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>Round selections share waiting-room authority and retain at most the three native choices.</summary>
public sealed class RoomRoundSettingsTests
{
    /// <summary>A server-created room retains its selected mode when the first native selector supplies its lineage.</summary>
    [Fact]
    public async Task ServerCreatedRoomKeepsItsModeWithoutACreatorBlock()
    {
        var rig = new RoomFlowTests.Rig();
        var host = RoomFlowTests.Rig.Player(170, "host");
        try
        {
            await rig.Flow.HandleCreateAsync(host, new("room", "", 101, "server-map"), default);
            var room = rig.Registry.TryGet(host.Id)!;
            Assert.Null(room.BuildSettings);
            await rig.Flow.HandleRoundSettingsChangeAsync(host, new(2, "layout", new byte[] { 2 }), default);
            var snapshot = Assert.Single(rig.SettingsChanged.To(host)).Room;
            Assert.Equal(101, snapshot.ModeId);
            Assert.Equal("server-map", snapshot.MapKey);
            Assert.Equal(101, snapshot.BuildSettings!.Mode);
            Assert.True(snapshot.BuildSettings.Bytes.IsEmpty);
            var selection = Assert.Single(snapshot.BuildSettings.RoundSelections);
            Assert.Equal(2, selection.Index);
            Assert.Equal(new byte[] { 2 }, selection.Bytes.ToArray());
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Replacing one choice keeps the other rounds and preserves the creator's encoded options and prefix.</summary>
    [Fact]
    public async Task SelectionsAreBoundedSnapshotsAndSurviveOrdinarySettingsChanges()
    {
        var rig = new RoomFlowTests.Rig();
        var host = RoomFlowTests.Rig.Player(171, "host");
        var original = new RoomBuildSettings("layout", new byte[] { 1, 2, 3 }) { SessionPrefix = new byte[] { 7, 8 } };
        try
        {
            await rig.Flow.HandleCreateAsync(host, new("room", "", 4, "initial", original), default);
            var room = rig.Registry.TryGet(host.Id)!;
            foreach (var index in new[] { 2, 0, 1, 2 })
                await rig.Flow.HandleRoundSettingsChangeAsync(host, new(index, "layout", new byte[] { (byte)index }), default);
            var saved = rig.SettingsChanged.To(host)[^1].Room.BuildSettings!;
            Assert.Equal([0, 1, 2], saved.RoundSelections.Select(value => value.Index));
            Assert.Empty(original.RoundSelections);
            Assert.Equal(original.Bytes.ToArray(), saved.Bytes.ToArray());
            Assert.Equal(original.SessionPrefix.ToArray(), saved.SessionPrefix.ToArray());
            Assert.Equal("initial", room.MapKey);
            Assert.Equal(4, room.ModeId);
            await rig.Flow.HandleSettingsChangeAsync(host, new(5, "active", new("layout", default, new byte[] { 9 })), default);
            Assert.Equal(saved.RoundSelections, room.BuildSettings!.RoundSelections);
            Assert.Equal(new byte[] { 9 }, room.BuildSettings.Change.ToArray());
            byte[] replacement = [9, 8];
            await rig.Flow.HandleRoundSettingsChangeAsync(host, new(1, "layout", replacement), default);
            replacement[0] = 7;
            Assert.Equal(new byte[] { 1 }, saved.RoundSelections[1].Bytes.ToArray());
            Assert.Equal(new byte[] { 9, 8 }, room.BuildSettings.RoundSelections[1].Bytes.ToArray());
            Assert.Equal(5, room.ModeId);
            Assert.Equal("active", room.MapKey);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>A guest or a loading master cannot alter the waiting-room round selections.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuthorityAndRoundPhasePreventMutation(bool loading)
    {
        var rig = new RoomFlowTests.Rig();
        var host = RoomFlowTests.Rig.Player(181, "host");
        var guest = RoomFlowTests.Rig.Player(182, "guest");
        try
        {
            var id = await rig.CreateAsync(host);
            await rig.Flow.HandleJoinAsync(guest, new(id, ""), default);
            var room = rig.Registry.Find(id)!;
            var initial = room.BuildSettings;
            if (loading)
            {
                await rig.Flow.HandleReadyAsync(guest, new(true), default);
                await rig.Flow.HandleStartAsync(host, new(), default);
                Assert.Single(rig.Starting.To(host));
            }
            await rig.Flow.HandleRoundSettingsChangeAsync(loading ? host : guest, new(0, "layout", new byte[] { 1 }), default);
            Assert.Same(initial, room.BuildSettings);
            Assert.Empty(rig.SettingsChanged.To(host));
            Assert.Empty(rig.SettingsChanged.To(guest));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Invalid indices and foreign lineage never commit state or publish a partial setting.</summary>
    [Fact]
    public async Task InvalidRequestsDoNotCommitOrPublish()
    {
        var rig = new RoomFlowTests.Rig();
        var host = RoomFlowTests.Rig.Player(191, "host");
        var source = new RoomBuildSettings("layout", new byte[] { 1 });
        try
        {
            await rig.Flow.HandleCreateAsync(host, new("room", "", 4, "map", source), default);
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => rig.Flow.HandleRoundSettingsChangeAsync(host, new(3, "layout", new byte[] { 1 }), default).AsTask());
            await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Flow.HandleRoundSettingsChangeAsync(host, new(0, "foreign", new byte[] { 1 }), default).AsTask());
            await Assert.ThrowsAsync<ArgumentException>(() => rig.Flow.HandleRoundSettingsChangeAsync(host, new(0, "layout", default), default).AsTask());
            await Assert.ThrowsAsync<ArgumentException>(() => rig.Flow.HandleRoundSettingsChangeAsync(host, new(0, "layout", new byte[ushort.MaxValue + 1]), default).AsTask());
            Assert.Same(source, rig.Registry.TryGet(host.Id)!.BuildSettings);
            Assert.Empty(rig.SettingsChanged.To(host));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }
}
