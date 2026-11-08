using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.InGame.Sessions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class RoundFlowTests
{
    /// <summary>All script operations require the current master's real connection and echo to their producer.</summary>
    [Fact]
    public async Task ZombieEscapeMapScriptsAuthenticateMasterAndEchoOpaqueSequences()
    {
        var rig = new Rig();
        var visibility = new Sink<RoundMapObjectVisibilityChanged>();
        var breakable = new Sink<RoundBreakableMapObjectCreated>();
        var effects = new Sink<RoundFinalZoneMapEffectChanged>();
        rig.Round.MapObjectVisibility = visibility;
        rig.Round.BreakableMapObjects = breakable;
        rig.Round.FinalZoneMapEffects = effects;
        try
        {
            var room = await StartMapScriptRoundAsync(rig);
            var shown = new RoundMapObjectVisibilityChanged(new byte[] { 71 }, false, 900);
            var created = new RoundBreakableMapObjectCreated(new byte[] { 66 }, 200, 2);
            var effect = new RoundFinalZoneMapEffectChanged("#point", "fx", "", true, 0, 1);
            async Task Publish(ISession reporter)
            {
                await rig.Round.HandleMapObjectVisibilityAsync(reporter, shown, default);
                await rig.Round.HandleBreakableMapObjectAsync(reporter, created, default);
                await rig.Round.HandleFinalZoneMapEffectAsync(reporter, effect, default);
            }
            await Publish(rig.Guest);
            await Publish(CaptainPlayer(rig.Host.Id, "replaced"));
            Assert.Empty(visibility.To(rig.Guest));
            Assert.Empty(breakable.To(rig.Guest));
            Assert.Empty(effects.To(rig.Guest));
            await Publish(rig.Host);
            foreach (var recipient in new[] { rig.Host, rig.Guest })
            {
                Assert.Equal(shown, Assert.Single(visibility.To(recipient)));
                Assert.Equal(created, Assert.Single(breakable.To(recipient)));
                Assert.Equal(effect, Assert.Single(effects.To(recipient)));
            }
            // Native scripts may recreate the same obstacle; its name or sequence is not an idempotency key.
            await rig.Round.HandleBreakableMapObjectAsync(rig.Host, created, default);
            Assert.Equal(2, breakable.To(rig.Guest).Length);
            lock (room.Gate) room.TransferMaster(room.FindByGame(rig.Guest.Id)!.Slot);
            await Publish(rig.Host);
            await Publish(rig.Guest);
            Assert.Single(visibility.To(rig.Host));
            Assert.Equal(2, breakable.To(rig.Host).Length);
            Assert.Single(effects.To(rig.Host));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Queued operations cannot leak across cancellation, a new round, or a completed result.</summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 1)]
    [InlineData(0, 2)]
    [InlineData(1, 0)]
    [InlineData(1, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 0)]
    [InlineData(2, 1)]
    [InlineData(2, 2)]
    public async Task ZombieEscapeQueuedMapScriptRejectsObsoleteRound(int kind, int transition)
    {
        var rig = new Rig();
        var visibility = new Sink<RoundMapObjectVisibilityChanged>();
        var breakable = new Sink<RoundBreakableMapObjectCreated>();
        var effects = new Sink<RoundFinalZoneMapEffectChanged>();
        rig.Round.MapObjectVisibility = visibility;
        rig.Round.BreakableMapObjects = breakable;
        rig.Round.FinalZoneMapEffects = effects;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? blocker = null;
        try
        {
            var room = await StartMapScriptRoundAsync(rig);
            blocker = room.Executor.ExecuteAsync(async _ =>
            {
                entered.SetResult();
                await release.Task;
                lock (room.Gate)
                {
                    if (transition == 0) room.RoundCancelled = true;
                    else if (transition == 1)
                    {
                        room.ResetToWaiting();
                        room.MarkStarting();
                        rig.Round.InitializeZombieEscape(room);
                        room.MarkPlaying();
                    }
                    else room.MarkResult();
                }
            }, default);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var pending = kind switch
            {
                0 => rig.Round.HandleMapObjectVisibilityAsync(rig.Host, new(new byte[] { 71 }, false, 1), default).AsTask(),
                1 => rig.Round.HandleBreakableMapObjectAsync(rig.Host, new(new byte[] { 66 }, 200, 2), default).AsTask(),
                _ => rig.Round.HandleFinalZoneMapEffectAsync(rig.Host, new("#point", "fx", "", true, 0, 3), default).AsTask(),
            };
            Assert.False(pending.IsCompleted);
            release.SetResult();
            await Task.WhenAll(blocker, pending);
            Assert.Empty(visibility.To(rig.Host));
            Assert.Empty(breakable.To(rig.Host));
            Assert.Empty(effects.To(rig.Host));
        }
        finally
        {
            release.TrySetResult();
            if (blocker is not null) await blocker;
            await rig.Registry.StopAllAsync();
        }
    }

    private static async Task<PlayRoom> StartMapScriptRoundAsync(Rig rig)
    {
        rig.Round.ZombieEscapeEnabled = true;
        rig.Round.ScriptZombieTeams = new Sink<RoundScriptZombieTeamChanged>();
        rig.Round.ScriptZombieConversions = new Sink<RoundScriptZombieConversionReported>();
        rig.Rooms.ZombieEscapeStarting = rig.Round.InitializeZombieEscape;
        await rig.Rooms.HandleCreateAsync(rig.Host, new RoomCreateRequested("script room", "", (int)GameModeId.ZombieEscape, "map"), default);
        var roomId = rig.Entered.To(rig.Host)[0].Room.RoomId;
        await rig.Rooms.HandleJoinAsync(rig.Guest, new(roomId, ""), default);
        await rig.Rooms.HandleReadyAsync(rig.Guest, new(true), default);
        await rig.Rooms.HandleStartAsync(rig.Host, new("map"), default);
        await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
        await rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default);
        await rig.WaitStartedAsync(rig.Host);
        return rig.Registry.Find(roomId)!;
    }
}
