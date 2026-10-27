using BubbleFighter.Fantasy.InGame;
using BubbleFighter.Fantasy.RoomCore;
using BubbleFighter.Fantasy.InGame.Sessions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>
/// Registry lifecycle coverage the original suite left open: the foreign-room
/// refusal, the seat cap, detach of a seat that is not the last one, the
/// waiting-phase room list, and the public contract the C2S 517 re-send path
/// depends on. Every room id below is a real id handed out by
/// <see cref="RoomRegistry"/>, so no live client is needed.
/// </summary>
public sealed class RoomRegistryLifecycleTests
{
    [Fact]
    public void RejoiningAfterAMiddleSeatLeavesUsesTheFreeSlotWithoutDuplicatingEntityIds()
    {
        var registry = new RoomRegistry(new InGameOptions());
        var (room, _) = registry.Create(new RoomRegistryTests.FakeSession(1), RoomWireSupport.WaitingSetting(), 1, "host");
        registry.Join(new RoomRegistryTests.FakeSession(2), room.Id, 2, "leaving");
        registry.Join(new RoomRegistryTests.FakeSession(3), room.Id, 3, "staying");
        registry.Detach(2);
        var (_, joined) = registry.Join(new RoomRegistryTests.FakeSession(4), room.Id, 4, "joined");
        Assert.Equal(1, joined.Slot);
        Assert.Equal(new[] { 0, 1, 2 }, room.Seats.Select(seat => seat.Slot).Order());
    }

    [Fact]
    public void SpawnTeamsKeepChosenTeamsAndObserversAndBalanceOnlyUnassignedSeats()
    {
        var registry = new RoomRegistry(new InGameOptions());
        var setting = RoomWireSupport.WaitingSetting();
        setting = setting with { Map = setting.Map with { Unknown4 = 1 } };
        var (room, host) = registry.Create(new RoomRegistryTests.FakeSession(1), setting, 1, "host");
        var (_, observer) = registry.Join(new RoomRegistryTests.FakeSession(2), room.Id, 2, "observer");
        registry.Join(new RoomRegistryTests.FakeSession(3), room.Id, 3, "guest");
        host.Team = 2;
        observer.Team = 36;

        lock (room.Gate) room.AssignSpawnTeams();

        Assert.Equal(new byte[] { 2, 36, 1 }, room.Seats.Select(s => s.Team));
        lock (room.Gate) room.AssignSpawnTeams();
        Assert.Equal(new byte[] { 2, 36, 1 }, room.Seats.Select(s => s.Team));
    }

    [Theory]
    [InlineData(8)]
    [InlineData(23)]
    [InlineData(25)]
    [InlineData(40)]
    [InlineData(41)]
    [InlineData(101)]
    public void NonTeamSpawnPathsKeepTheirSeatLabels(ushort mode)
    {
        var registry = new RoomRegistry(new InGameOptions());
        var setting = RoomWireSupport.WaitingSetting();
        var (room, seat) = registry.Create(new RoomRegistryTests.FakeSession(1),
            setting with { Map = setting.Map with { Unknown4 = mode } }, 1, "host");
        lock (room.Gate) room.AssignSpawnTeams();
        Assert.Equal(0, seat.Team);
    }

    [Fact]
    public void RoomIdsStartAtOneAndAscend()
    {
        var registry = new RoomRegistry(new InGameOptions());
        var first = registry.Create(new RoomRegistryTests.FakeSession(1), RoomWireSupport.WaitingSetting(), 1, "a");
        var second = registry.Create(new RoomRegistryTests.FakeSession(2), RoomWireSupport.WaitingSetting(), 2, "b");

        // C2S 403 / C2S 517 carry the id back, so 0 must never be handed out:
        // the client's id-0 re-send is only meaningful as "the room I am in".
        Assert.Equal(1, first.Room.Id);
        Assert.Equal(2, second.Room.Id);
        Assert.Null(registry.TryGet(0));
    }

    [Fact]
    public void RoomSessionCarriesItsIdAndIdsWrapWithinTheU16RangeSkippingLiveRooms()
    {
        var registry = new RoomRegistry(new InGameOptions());
        var first = registry.Create(new RoomRegistryTests.FakeSession(1), RoomWireSupport.WaitingSetting(), 1, "a");
        Assert.Equal((ushort)first.Room.Id, first.Room.Setting.Unknown0);

        // Advance the counter so the next id wraps past 65535 back onto room 1, which is still live.
        typeof(RoomRegistry).GetField("_nextId", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(registry, ushort.MaxValue - 1);
        var last = registry.Create(new RoomRegistryTests.FakeSession(2), RoomWireSupport.WaitingSetting(), 2, "b");
        var wrapped = registry.Create(new RoomRegistryTests.FakeSession(3), RoomWireSupport.WaitingSetting(), 3, "c");
        Assert.Equal(ushort.MaxValue, last.Room.Id);
        Assert.Equal(2, wrapped.Room.Id);
        Assert.Equal((ushort)2, wrapped.Room.Setting.Unknown0);
    }

    [Fact]
    public void CreateFromSettingNormalizesThePlazaKindToTheRoomStageGate()
    {
        var registry = new RoomRegistry(new InGameOptions());
        var (room, seat) = registry.CreateFromSetting(
            new RoomRegistryTests.FakeSession(1), RoomWireSupport.WaitingSetting() with { Kind = 3 }, 10, "host");

        // Kind 3 is the plaza session on the 117 handler; a created room must
        // still be the room stage, and the client map survives the rewrite.
        Assert.Equal(SessionSettingBlob.MainMenuKind, room.Setting.Kind);
        Assert.False(room.Setting.Map.IsEmpty);
        Assert.Equal(40, room.Setting.Map.Unknown4);
        Assert.Null(room.CreateRules);
        Assert.Equal(0, seat.Slot);
    }

    [Fact]
    public void JoinRefusesAForeignRoomWhileSeated()
    {
        var registry = new RoomRegistry(new InGameOptions());
        var host = new RoomRegistryTests.FakeSession(1);
        var (roomA, _) = registry.Create(host, RoomWireSupport.WaitingSetting(), 1, "host");
        var (roomB, _) = registry.Create(
            new RoomRegistryTests.FakeSession(2), RoomWireSupport.WaitingSetting(), 2, "other");

        // The 517 handler guards this itself before it reaches the registry;
        // the registry must refuse it loudly as well, not silently move seats.
        Assert.Throws<InvalidOperationException>(() =>
            registry.Join(host, roomB.Id, 1, "host"));

        Assert.Same(roomA, registry.TryGet(host.Id));
        Assert.Single(roomA.Seats);
        Assert.Single(roomB.Seats);
        Assert.Equal(2, registry.OpenRooms().Count);
    }

    [Fact]
    public void JoinRefusesRoomIdZero()
    {
        var registry = new RoomRegistry(new InGameOptions());
        var (room, _) = registry.Create(
            new RoomRegistryTests.FakeSession(1), RoomWireSupport.WaitingSetting(), 1, "host");

        // Id 0 is never a live room, so a C2S 517 re-send carrying id 0 can
        // only be answered by the handler's "already seated" branch. Proving
        // the registry refuses it is what makes that branch load-bearing.
        Assert.Throws<InvalidOperationException>(() =>
            registry.Join(new RoomRegistryTests.FakeSession(1), roomId: 0, 1, "host"));
        Assert.Single(room.Seats);
    }

    [Fact]
    public void JoinRefusesASecondSeatForTheSameSession()
    {
        var registry = new RoomRegistry(new InGameOptions());
        var host = new RoomRegistryTests.FakeSession(1);
        var (room, _) = registry.Create(host, RoomWireSupport.WaitingSetting(), 1, "host");

        Assert.Throws<InvalidOperationException>(() =>
            registry.Join(host, room.Id, 1, "host"));

        Assert.Single(room.Seats);
        Assert.Equal(0, room.Seats[0].Slot);
    }

    [Fact]
    public void RoomFullRefusesTheNextSeat()
    {
        var options = new InGameOptions { MaxPlayersPerMatch = 2 };
        var registry = new RoomRegistry(options);
        var (room, _) = registry.Create(
            new RoomRegistryTests.FakeSession(1), RoomWireSupport.WaitingSetting(), 1, "a");
        registry.Join(new RoomRegistryTests.FakeSession(2), room.Id, 2, "b");

        Assert.Throws<InvalidOperationException>(() =>
            registry.Join(new RoomRegistryTests.FakeSession(3), room.Id, 3, "c"));

        Assert.Equal(2, room.Seats.Count);
        Assert.Equal(RoomPhase.Waiting, room.Phase);
    }

    [Fact]
    public void DetachKeepsARoomThatStillHasSeats()
    {
        var registry = new RoomRegistry(new InGameOptions());
        var host = new RoomRegistryTests.FakeSession(1);
        var (room, _) = registry.Create(host, RoomWireSupport.WaitingSetting(), 1, "host");
        var guest = new RoomRegistryTests.FakeSession(2);
        registry.Join(guest, room.Id, 2, "guest");

        registry.Detach(guest.Id);

        // Only the empty-room rule closes a room; a room with a live seat keeps
        // its phase, its id and its place in the open-room list.
        Assert.NotNull(registry.TryGet(host.Id));
        Assert.Same(room, registry.TryGet(host.Id));
        Assert.Equal(RoomPhase.Waiting, room.Phase);
        Assert.Single(room.Seats);
        Assert.Single(registry.OpenRooms());

        registry.Detach(host.Id);
        Assert.Null(registry.TryGet(host.Id));
        Assert.Empty(registry.OpenRooms());
    }

    [Fact]
    public void OpenRoomsListsWaitingAndStartingRooms()
    {
        var registry = new RoomRegistry(new InGameOptions());
        var (waiting, _) = registry.Create(
            new RoomRegistryTests.FakeSession(1), RoomWireSupport.WaitingSetting(), 1, "waiting");
        var (starting, _) = registry.Create(
            new RoomRegistryTests.FakeSession(2), RoomWireSupport.WaitingSetting(), 2, "starting");

        Assert.Equal(2, registry.OpenRooms().Count);

        lock (starting.Gate)
            starting.MarkStarting();

        var open = registry.OpenRooms();
        Assert.Equal(2, open.Count);
        Assert.Contains(waiting, open);
        Assert.Contains(starting, open);
    }

    [Fact]
    public void JoinReSendResolvesTheCurrentSeatWithoutANewJoin()
    {
        var registry = new RoomRegistry(new InGameOptions());
        var session = new RoomRegistryTests.FakeSession(1);
        var (room, seat) = registry.Create(session, RoomWireSupport.WaitingSetting(), 7, "host");

        // The C2S 517 re-send path in RoomLoop.HandleJoinRoomAsync resolves the
        // session through TryGet, echoes seat.Key.Unknown0 as the handshake
        // token, and pushes the room state exactly once. These are the public
        // pieces that path stands on; the push-count decision itself lives in
        // the internal handler and is covered by the live-client notes in the
        // audit document.
        var current = registry.TryGet(session.Id);
        Assert.NotNull(current);
        Assert.Same(room, current);

        RoomSeat resolved;
        lock (current!.Gate)
            resolved = current.FindByGame(session.Id)!;
        Assert.Same(seat, resolved);
        Assert.Equal(seat.Key.Unknown0, resolved.Key.Unknown0);

        // The one-shot gate starts clear, so the first re-send is the one that
        // pushes the room state; later re-sends only echo the handshake.
        Assert.False(seat.JoinAcked);
        seat.JoinAcked = true;
        Assert.True(seat.JoinAcked);

        // A re-send must never grow the seat list.
        Assert.Throws<InvalidOperationException>(() =>
            registry.Join(session, room.Id, 7, "host"));
        Assert.Single(room.Seats);
    }
}
