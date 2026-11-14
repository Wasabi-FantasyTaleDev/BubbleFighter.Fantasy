using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.InGame.Sessions;
using BubbleFighter.Fantasy.RoomCore;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>
/// The registry signals every change that a member's scene snapshot reports, after the change is made: joining and
/// leaving, and the room's phase, countdown and mode. Listeners read the scene afterwards, so only the members matter.
/// </summary>
public sealed class RoomSceneSignalTests
{
    [Fact]
    public void JoiningLeavingAndEveryRoomPhaseSignalTheAffectedMembers()
    {
        var registry = new RoomRegistry(new InGameOptions());
        var signals = new List<long>();
        registry.SessionSceneChanged = session => signals.Add(session.Id);
        var (room, _) = registry.Create(new RoomRegistryTests.FakeSession(1), RoomWireSupport.WaitingSetting(), 1, "host");
        Assert.Equal([1L], signals);
        registry.Join(new RoomRegistryTests.FakeSession(2), room.Id, 2, "guest");
        Assert.Equal([1L, 2L], signals);

        signals.Clear();
        lock (room.Gate)
        {
            room.CountdownGeneration = 1;
            room.MarkPlaying();
            room.MarkPlaying();
            room.MarkResult();
            room.ModeId = (ushort)(room.ModeId + 1);
        }
        room.ResetToWaiting();
        // Five changes for two members; a repeated phase is not a change.
        Assert.Equal(10, signals.Count);
        Assert.Equal(5, signals.Count(id => id == 2));

        signals.Clear();
        registry.Detach(2);
        Assert.Equal([2L], signals);
    }

    [Fact]
    public void ARoomWithoutAListenerStillWorks()
    {
        var registry = new RoomRegistry(new InGameOptions());
        var (room, _) = registry.Create(new RoomRegistryTests.FakeSession(1), RoomWireSupport.WaitingSetting(), 1, "host");
        lock (room.Gate) room.MarkPlaying();
        registry.Detach(1);
        Assert.Equal(RoomPhase.Closed, room.Phase);
    }
}
