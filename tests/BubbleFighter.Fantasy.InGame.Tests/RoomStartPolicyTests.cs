using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.InGame.Handlers;
using BubbleFighter.Fantasy.InGame.Sessions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>Room policies decide a start in the same locked section that leaves waiting, the automatic start included.</summary>
public sealed class RoomStartPolicyTests
{
    private sealed class StartPolicy(Func<RoomPolicyRoom, RoomStartDecision> decide) : IRoomPolicy
    {
        internal List<RoomPolicyRoom> Evaluated { get; } = [];
        internal List<bool> Decided { get; } = [];
        internal int Explained { get; private set; }

        public void OnStartDecided(RoomPolicyRoom room, long requesterSessionId, bool proceeding) => Decided.Add(proceeding);
        internal Action<RoomPolicyRoom>? OnEvaluate { get; init; }

        public RoomStartDecision EvaluateStart(RoomPolicyRoom room, long requesterSessionId)
        {
            Evaluated.Add(room);
            OnEvaluate?.Invoke(room);
            return decide(room);
        }

        public ValueTask OnStartHeldAsync(RoomPolicyRoom room, long requesterSessionId, CancellationToken ct)
        {
            Explained++;
            return ValueTask.CompletedTask;
        }

        public ValueTask OnMemberEnteredAsync(RoomPolicyRoom room, long sessionId, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask OnAppearanceChangedAsync(RoomPolicyRoom room, long sessionId, CancellationToken ct) => ValueTask.CompletedTask;
        public void OnRoomClosed(Guid instance) { }
    }

    /// <summary>
    /// A lookup that races a proceeding start waits for the room lock and then sees the room already starting: there is
    /// no window in which a policy said proceed while the room still reads as waiting.
    /// </summary>
    [Fact]
    public async Task ALookupRacingAProceedingStartNeverSeesTheRoomStillWaiting()
    {
        var rig = new RoomFlowTests.Rig();
        var policies = new RoomPolicyHost(rig.Registry, "test/game", "kr-812");
        rig.Flow.PolicyHost = policies;
        var host = RoomFlowTests.Rig.Player(941, "host");
        Task<RoomPolicyRoom?>? racer = null;
        using var registration = policies.Register(new StartPolicy(_ => RoomStartDecision.Proceed)
        {
            // Runs under the room lock: another thread's lookup must block until the start has left waiting.
            OnEvaluate = _ => racer = Task.Run(() => policies.Find(host.Id)),
        });
        try
        {
            await rig.CreateAsync(host);
            await rig.Flow.HandleStartAsync(host, new("rel_Village07_Easy02") { Force = true }, default);
            var seen = await racer!.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(Assert.IsType<RoomPolicyRoom>(seen).Waiting);
            Assert.Single(rig.Starting.To(host));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Every policy sees the start; only the ones that held it are asked to explain.</summary>
    [Fact]
    public async Task OnlyHoldingPoliciesExplainAHeldStart()
    {
        var rig = new RoomFlowTests.Rig();
        var policies = new RoomPolicyHost(rig.Registry, "test/game", "kr-812");
        rig.Flow.PolicyHost = policies;
        var holding = new StartPolicy(_ => RoomStartDecision.Hold);
        var passing = new StartPolicy(_ => RoomStartDecision.Proceed);
        using var first = policies.Register(holding);
        using var second = policies.Register(passing);
        var host = RoomFlowTests.Rig.Player(942, "host");
        try
        {
            await rig.CreateAsync(host);
            await rig.Flow.HandleStartAsync(host, new("rel_Village07_Easy02") { Force = true }, default);
            Assert.Single(holding.Evaluated);
            Assert.Single(passing.Evaluated);
            Assert.Equal(1, holding.Explained);
            Assert.Equal(0, passing.Explained);
            // Both learn the start did not go ahead, so neither consumes one-shot consent.
            Assert.Equal([false], holding.Decided);
            Assert.Equal([false], passing.Decided);
            Assert.Empty(rig.Starting.To(host));
            Assert.Equal(RoomPhase.Waiting, rig.Registry.TryGet(host.Id)!.Phase);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>
    /// The native automatic start is asked too, flagged as automatic; a safety hold keeps the room waiting and its owner
    /// resumes the start once.
    /// </summary>
    [Fact]
    public async Task TheAutomaticStartIsAskedFlaggedAndCanBeHeldAndResumed()
    {
        var rig = new RoomFlowTests.Rig(options: new InGameOptions { AutoStartModeIds = [16] });
        var policies = new RoomPolicyHost(rig.Registry, "test/game", "kr-812") { Start = rig.Flow.ResumeHeldStartAsync };
        rig.Flow.PolicyHost = policies;
        var busy = true;
        var policy = new StartPolicy(_ => busy ? RoomStartDecision.Hold : RoomStartDecision.Proceed);
        using var registration = policies.Register(policy);
        var host = RoomFlowTests.Rig.Player(943, "host");
        await rig.Flow.HandleCreateAsync(host, new("boss", "", 16, "Random"), default);
        var room = rig.Registry.TryGet(host.Id)!;
        try
        {
            await rig.Flow.HandleChatAsync(host, new(0, "!start"), default);
            await rig.Flow.HandleStartAsync(host, new("rel_zombie05_Tear"), default);
            var first = Assert.Single(policy.Evaluated);
            Assert.True(first.AutomaticStart);
            Assert.False(first.ResumedStart);
            Assert.Equal(RoomPhase.Waiting, room.Phase);
            Assert.Empty(rig.Starting.To(host));

            busy = false;
            Assert.True(await policies.ResumeHeldStartAsync(room.InstanceId, host.Id, default));
            Assert.Equal("rel_zombie05_Tear", Assert.Single(rig.Starting.To(host)).MapKey);
            // The replay is flagged, and only the start that went ahead is reported as proceeding.
            Assert.True(policy.Evaluated[1].ResumedStart);
            Assert.Equal([false, true], policy.Decided);
            Assert.False(await policies.ResumeHeldStartAsync(room.InstanceId, host.Id, default));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }
}
