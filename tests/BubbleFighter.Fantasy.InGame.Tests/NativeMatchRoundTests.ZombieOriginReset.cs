using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.InGame.Sessions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class NativeMatchRoundTests
{
    /// <summary>A private cleaned-death grant alone does not convert a fighter. Only its exact echo on the granted link does.</summary>
    [Fact]
    public async Task ZombieOriginConversionBindsTheGrantToItsFormLifeOwnerAndMatchLink()
    {
        var grants = new Recorder<RoundPlayerRespawned>();
        await using var rig = new Rig("kr-1256", 20, fallback: true, respawned: grants);
        rig.Round.ZombieOriginRolls = new Recorder<RoundZombieOriginPrototypeRoll>();
        await rig.StartAsync(43);
        await rig.LoadAsync(rig.Host);
        var link = new Session(101, "kr-1256");
        rig.Registry.MarkInitialized(link, await rig.Registry.BindCapabilityAsync(link, new(rig.Offers.Sent[0].Message.Ticket), default));
        await rig.Round.HandleMatchConnectionReportedAsync(rig.Host, new(true), default);
        await rig.LoadAsync(rig.Guest);
        await rig.Round.HandleMatchConnectionReportedAsync(rig.Guest, new(false), default);
        var room = rig.Room;
        lock (room.Gate) { room.MarkPlaying(); rig.Round.StartZombieOrigin(room); }
        var victim = room.FindByGame(rig.Host.Id)!;
        var team = victim.Team;
        var member = room.ZombieOrigin!.MemberFor(victim)!;
        var actor = RoomRegistry.SceneEntity(victim.Slot);
        var foreign = RoomRegistry.SceneEntity(room.FindByGame(rig.Guest.Id)!.Slot);
        RoundSpawnAccepted Reset(int handle, int? first, int? kind) => new(handle, 0)
        {
            TaggedReset = new(first, kind), Request = Bytes(handle, first, kind, false),
            Rules = new(RoundRespawnAudience.Requester, RoundRespawnCompletion.MatchConfirmation, RoundRespawnAdmission.CleanedDeath),
        };
        Task Request(ISession s, RoundSpawnAccepted request) => rig.Round.HandleSpawnAcceptedAsync(s, request, default).AsTask();
        Task Life(ISession s, RoundLifeEvent e) => rig.Round.HandleLifeReportedAsync(s, new(e, actor), default).AsTask();
        async Task Confirm(ISession s, int handle, int? first, int? kind)
        {
            await rig.Round.HandleFeedAsync(s, new(32926, Bytes(handle, first, kind, true))
                { OnRelay = true, ReportedActors = [handle], TaggedReset = new(first, kind) }, default);
            // The binding also dispatches the typed, actor-only life notice after the peer feed.
            await rig.Round.HandleLifeReportedAsync(s, new(RoundLifeEvent.ResetConfirmed, handle), default);
        }
        var request = Reset(actor, null, 211);
        await Request(rig.Host, request);
        await Life(link, RoundLifeEvent.Died);
        await Request(rig.Host, request);
        await Life(link, RoundLifeEvent.DeathCleaned);
        await Request(link, request);
        await Request(rig.Guest, request);
        await Request(new Session(rig.Host.Id, "kr-1256"), request);
        await Request(rig.Host, Reset(foreign, null, 211));
        await Request(rig.Host, Reset(actor, null, 212));
        await Request(rig.Host, Reset(actor, null, null));
        await Request(rig.Host, Reset(actor, 5, 211));
        await Request(rig.Host, request with { TaggedReset = null });
        await Request(rig.Host, request with { Rules = RoundRespawnRules.RoomGrant });
        Assert.Empty(grants.Sent);
        Assert.Equal(SeatLife.Dead, victim.Life.State);
        grants.Fail = true;
        await Assert.ThrowsAsync<IOException>(() => Request(rig.Host, request));
        Assert.Null(member.Pending);
        Assert.True(victim.Life.CanGrantReset);
        grants.Fail = false;
        await Task.WhenAll(Request(rig.Host, request), Request(rig.Host, request));
        var grant = Assert.Single(grants.Sent);
        Assert.Equal(rig.Host.Id, grant.Session);
        Assert.Equal(request.Request.ToArray(), grant.Message.Request.ToArray());
        await Life(link, RoundLifeEvent.ResetConfirmed);
        await Confirm(rig.Host, actor, null, 211);
        await Confirm(rig.Guest, actor, null, 211);
        await Confirm(new Session(link.Id, "kr-1256"), actor, null, 211);
        await Confirm(link, foreign, null, 211);
        await Confirm(link, actor, null, 212);
        await Confirm(link, actor, 5, 211);
        Assert.Empty(rig.Feed.Sent);
        Assert.Equal(SeatLife.ResetPending, victim.Life.State);
        Assert.Equal(team, victim.Team);
        Assert.Equal(ZombieOriginRole.Fighter, member.Role);
        Assert.Null(member.Kind);
        await Task.WhenAll(Confirm(link, actor, null, 211), Confirm(link, actor, null, 211));
        var peer = Assert.Single(rig.Feed.Sent);
        Assert.Equal(rig.Guest.Id, peer.Session);
        Assert.Equal(Bytes(actor, null, 211, true), peer.Message.Payload.ToArray());
        Assert.Equal(SeatLife.Alive, victim.Life.State);
        Assert.Equal(34, (int)victim.Team);
        Assert.Equal(ZombieOriginRole.ConvertedFighter, member.Role);
        Assert.Equal(211, member.Kind);
        Assert.Null(member.Pending);
        // A previously converted fighter takes the ordinary reset branch; replaying its conversion cannot grant or revive this life.
        await Life(link, RoundLifeEvent.Died);
        await Life(link, RoundLifeEvent.DeathCleaned);
        await Request(rig.Host, request);
        await Request(rig.Host, Reset(actor, 5, null));
        Assert.Single(grants.Sent);
        await Request(rig.Host, Reset(actor, null, null));
        Assert.Equal(2, grants.Sent.Count);
        await Confirm(link, actor, null, 211);
        Assert.Equal(SeatLife.ResetPending, victim.Life.State);
        await Confirm(link, actor, null, null);
        Assert.Equal(SeatLife.Alive, victim.Life.State);
        Assert.Equal(ZombieOriginRole.ConvertedFighter, member.Role);
        Assert.Equal(34, (int)victim.Team);
        Assert.Equal(2, rig.Feed.Sent.Count);
        // An already queued confirmation for an outstanding grant must not complete in the next generation.
        await Life(link, RoundLifeEvent.Died);
        await Life(link, RoundLifeEvent.DeathCleaned);
        await Request(rig.Host, Reset(actor, null, null));
        Assert.NotNull(member.Pending);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = room.Executor.ExecuteAsync(async ct => { entered.SetResult(); await release.Task.WaitAsync(ct); });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var stale = Confirm(link, actor, null, null);
            Assert.False(stale.IsCompleted);
            room.ResetToWaiting();
            Assert.Equal(team, victim.Team);
            lock (room.Gate) { room.MarkStarting(); room.MarkPlaying(); rig.Round.StartZombieOrigin(room); }
            release.SetResult();
            await Task.WhenAll(held, stale).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { release.TrySetResult(); }
        await Confirm(link, actor, null, 211);
        Assert.Equal(2, rig.Feed.Sent.Count);
        Assert.Equal(ZombieOriginRole.Fighter, room.ZombieOrigin!.MemberFor(victim)!.Role);
        Assert.Equal(team, victim.Team);

        static byte[] Bytes(int handle, int? first, int? kind, bool confirmation)
        {
            static byte[] Tagged(int? value) => value is int word ? [0x3A, .. BitConverter.GetBytes(word)] : [0xBA];
            return [.. BitConverter.GetBytes(handle), .. Tagged(first), .. Tagged(kind), .. new byte[confirmation ? 5 : 4]];
        }
    }
}
