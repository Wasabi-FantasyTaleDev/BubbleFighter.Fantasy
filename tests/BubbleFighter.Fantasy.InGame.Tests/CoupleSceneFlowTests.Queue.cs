using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Players;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class CoupleSceneFlowTests
{
    [Fact]
    public async Task CouplePublicationSurvivesAFullRoomQueue()
    {
        var online = new PublicationDirectory();
        await using var rig = new Rig(online: online, queueCapacity: 1);
        var owner = Player(11, "owner");
        var peer = Player(22, "peer");
        online.Sessions[11] = owner;
        online.Sessions[22] = peer;
        var id = await rig.CreateAsync(owner);
        await rig.Flow.HandleJoinAsync(peer, new(id, ""), default);
        var executor = rig.Registry.RequireGame(owner).Room.Executor;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = executor.ExecuteAsync(async token =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(token);
        });
        Task queued = Task.CompletedTask;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            queued = executor.ExecuteAsync(_ => ValueTask.CompletedTask);
            Profile(owner).Scene = new(22, "peer");
            online.ObservePublication();
            rig.Flow.OnCoupleSceneChanged(new(owner, Profile(owner).Scene));
            var publication = rig.Flow.FlushCoupleChangesAsync();
            await online.PublicationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<TimeoutException>(() => publication.WaitAsync(TimeSpan.FromMilliseconds(100)));
            Assert.False(publication.IsCompleted);
            Assert.Empty(rig.RoomCouple.All());
            release.SetResult();
            await Task.WhenAll(running, queued, publication).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(Profile(owner).Scene, Assert.Single(rig.RoomCouple.To(owner)).Member.Couple);
            Assert.Equal(Profile(owner).Scene, Assert.Single(rig.RoomCouple.To(peer)).Member.Couple);
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(running, queued).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task StoppingCoupleWorkerCancelsFullQueueAdmissionWithoutStoppingUnrelatedRoomWork()
    {
        var online = new PublicationDirectory();
        await using var rig = new Rig(online: online, queueCapacity: 1);
        var owner = Player(11, "owner");
        online.Sessions[11] = owner;
        await rig.CreateAsync(owner);
        var executor = rig.Registry.RequireGame(owner).Room.Executor;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = executor.ExecuteAsync(async token =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(token);
        });
        Task queued = Task.CompletedTask;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            queued = executor.ExecuteAsync(_ => ValueTask.CompletedTask);
            Profile(owner).Scene = new(22, "peer");
            online.ObservePublication();
            rig.Flow.OnCoupleSceneChanged(new(owner, Profile(owner).Scene));
            // The second lookup occurs inside publication, after the worker removed the pending event.
            await online.PublicationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var stopping = rig.Flow.StopCoupleChangesAsync();
            Assert.Same(stopping, rig.Flow.StopCoupleChangesAsync());
            await stopping.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(running.IsCompleted);
            Assert.False(queued.IsCompleted);
            Assert.Empty(rig.RoomCouple.All());
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(running, queued).WaitAsync(TimeSpan.FromSeconds(5));
        }
        await rig.Flow.FlushCoupleChangesAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(rig.RoomCouple.All());
    }

    private sealed class PublicationDirectory : IOnlinePlayers
    {
        private int _lookups;
        private bool _observing;
        internal Dictionary<int, ISession> Sessions { get; } = [];
        internal TaskCompletionSource PublicationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void ObservePublication() => Volatile.Write(ref _observing, true);

        public ISession? FindByUserNum(int userNum)
        {
            if (Volatile.Read(ref _observing) && Interlocked.Increment(ref _lookups) == 2)
                PublicationStarted.TrySetResult();
            return Sessions.GetValueOrDefault(userNum);
        }

        public ISession? FindByNickname(string nickname) => null;
    }
}
