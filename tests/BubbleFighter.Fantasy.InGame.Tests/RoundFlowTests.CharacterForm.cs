using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.InGame.Sessions;
using BubbleFighter.Fantasy.Tests.Shared;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class RoundFlowTests
{
    /// <summary>Activation and the native expiry request reach owner and peers; repeats do not replay peer effects.</summary>
    [Fact]
    public async Task RheaFormAppliesToOwnerAndPeersAndAcknowledgesRepeatedEndOnlyToOwner()
    {
        var rig = new Rig();
        var changed = new Sink<RoundCharacterFormChanged>();
        try
        {
            var (room, seat) = await StartRheaRoundAsync(rig);
            var actor = RoomRegistry.SceneEntity(seat.Slot);
            await rig.Round.HandleCharacterFormAsync(rig.Guest, new(actor, true), changed, default);
            await rig.Round.HandleCharacterFormAsync(rig.Guest, new(actor, true), changed, default);
            Assert.Equal(new RoundCharacterFormChanged(actor, true), Assert.Single(changed.To(rig.Guest)));
            Assert.Equal(new RoundCharacterFormChanged(actor, true), Assert.Single(changed.To(rig.Host)));
            Assert.True(seat.Life.CharacterFormActive);
            await rig.Round.HandleCharacterFormAsync(rig.Guest, new(actor, false), changed, default);
            await rig.Round.HandleCharacterFormAsync(rig.Guest, new(actor, false), changed, default);
            Assert.Equal(3, changed.To(rig.Guest).Length);
            Assert.Equal(2, changed.To(rig.Host).Length);
            Assert.Equal(new RoundCharacterFormChanged(actor, false), changed.To(rig.Guest)[^1]);
            Assert.False(seat.Life.CharacterFormActive);
            Assert.Equal(RoomPhase.Playing, room.Phase);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Only the equipped Rhea's living actor on its exact game connection may activate in a playing round.</summary>
    [Fact]
    public async Task RheaFormRejectsWrongCharacterActorConnectionAndUnavailableLife()
    {
        var rig = new Rig();
        var changed = new Sink<RoundCharacterFormChanged>();
        try
        {
            var (room, seat) = await StartRheaRoundAsync(rig);
            var actor = RoomRegistry.SceneEntity(seat.Slot);
            await rig.Round.HandleCharacterFormAsync(rig.Guest, new(RoomRegistry.SceneEntity(room.MasterSlot), true), changed, default);
            await rig.Round.HandleCharacterFormAsync(new RecordingSession(rig.Guest.Id), new(actor, true), changed, default);
            await rig.Round.HandleCharacterFormAsync(new RecordingSession(998), new(actor, true), changed, default);
            lock (room.Gate) seat.CharacterId = 300001;
            await rig.Round.HandleCharacterFormAsync(rig.Guest, new(actor, true), changed, default);
            lock (room.Gate) { seat.CharacterId = 300029; seat.Life.Bubble(); }
            await rig.Round.HandleCharacterFormAsync(rig.Guest, new(actor, true), changed, default);
            lock (room.Gate) seat.Life.Burst();
            await rig.Round.HandleCharacterFormAsync(rig.Guest, new(actor, true), changed, default);
            lock (room.Gate) seat.Life.Grant(null);
            await rig.Round.HandleCharacterFormAsync(rig.Guest, new(actor, true), changed, default);
            lock (room.Gate) { seat.Life.Revive(); room.RoundCancelled = true; }
            await rig.Round.HandleCharacterFormAsync(rig.Guest, new(actor, true), changed, default);
            lock (room.Gate) { room.ResetToWaiting(); room.MarkStarting(); }
            await rig.Round.HandleCharacterFormAsync(rig.Guest, new(actor, true), changed, default);
            Assert.Empty(changed.To(rig.Host));
            Assert.Empty(changed.To(rig.Guest));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>A native end racing death releases the active form; respawn and conversion start with no form.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RheaFormEndsAfterDeathAndResetsWithANewLife(bool converted)
    {
        var rig = new Rig();
        var changed = new Sink<RoundCharacterFormChanged>();
        try
        {
            var (room, seat) = await StartRheaRoundAsync(rig);
            var actor = RoomRegistry.SceneEntity(seat.Slot);
            await rig.Round.HandleCharacterFormAsync(rig.Guest, new(actor, true), changed, default);
            lock (room.Gate) seat.Life.Die();
            await rig.Round.HandleCharacterFormAsync(rig.Guest, new(actor, false), changed, default);
            Assert.Equal(new RoundCharacterFormChanged(actor, false), changed.To(rig.Host)[^1]);
            lock (room.Gate) seat.Life.Revive();
            await rig.Round.HandleCharacterFormAsync(rig.Guest, new(actor, true), changed, default);
            Assert.True(seat.Life.CharacterFormActive);
            lock (room.Gate)
            {
                if (converted) seat.Life.EndDeathByConversion();
                else seat.Life.Revive();
            }
            Assert.False(seat.Life.CharacterFormActive);
            await rig.Round.HandleCharacterFormAsync(rig.Guest, new(actor, true), changed, default);
            Assert.Equal(4, changed.To(rig.Host).Length);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Queued requests cannot change a replaced seat, later round, later life, cancelled room or departed member.</summary>
    [Theory]
    [InlineData("round", true)]
    [InlineData("life", true)]
    [InlineData("life", false)]
    [InlineData("leave", true)]
    [InlineData("replace", true)]
    [InlineData("cancel", true)]
    public async Task RheaFormQueuedRequestsStayWithTheirOriginalIncarnation(string change, bool active)
    {
        var rig = new Rig();
        var changed = new Sink<RoundCharacterFormChanged>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? blocker = null;
        try
        {
            var (room, seat) = await StartRheaRoundAsync(rig);
            var actor = RoomRegistry.SceneEntity(seat.Slot);
            blocker = room.Executor.ExecuteAsync(async _ =>
            {
                entered.SetResult();
                await release.Task;
                lock (room.Gate)
                {
                    switch (change)
                    {
                        case "round": room.ResetToWaiting(); room.MarkStarting(); room.MarkPlaying(); break;
                        case "life": seat.Life.Revive(); break;
                        case "cancel": room.RoundCancelled = true; break;
                        case "leave": room.RemoveSession(rig.Guest.Id); break;
                        case "replace":
                            room.RemoveSession(rig.Guest.Id);
                            room.Add(rig.Guest, seat.Key, seat.UserNum, seat.Nickname).CharacterId = 300029;
                            break;
                    }
                }
            }, default);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var pending = rig.Round.HandleCharacterFormAsync(rig.Guest, new(actor, active), changed, default).AsTask();
            release.SetResult();
            await blocker;
            await pending;
            Assert.Empty(changed.To(rig.Host));
            Assert.Empty(changed.To(rig.Guest));
        }
        finally
        {
            release.TrySetResult();
            if (blocker is not null) await blocker;
            await rig.Registry.StopAllAsync();
        }
    }

    private static async Task<(PlayRoom Room, RoomSeat Seat)> StartRheaRoundAsync(Rig rig)
    {
        await rig.StartAsync();
        await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
        await rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default);
        await rig.WaitStartedAsync(rig.Host);
        var room = rig.Registry.Find(rig.RoomId)!;
        var seat = room.FindByGame(rig.Guest.Id)!;
        lock (room.Gate) seat.CharacterId = 300029;
        return (room, seat);
    }
}
