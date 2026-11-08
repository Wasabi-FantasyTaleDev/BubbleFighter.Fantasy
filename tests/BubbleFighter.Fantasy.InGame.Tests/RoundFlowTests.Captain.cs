using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.InGame.Sessions;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class RoundFlowTests
{
    /// <summary>Once the round plays, each team gets one Captain from its own seats and every member hears both.</summary>
    [Fact]
    public async Task CaptainRoundAppointsOneCaptainPerTeam()
    {
        var rig = new Rig();
        var third = CaptainPlayer(33, "third");
        var fourth = CaptainPlayer(44, "fourth");
        var captains = new Sink<RoundCaptainAppointed>();
        rig.Round.CaptainAppointed = captains;
        try
        {
            var room = await StartCaptainRoundAsync(rig, [1, 1, 2, 2], third, fourth);
            foreach (var member in new ISession[] { rig.Host, rig.Guest, third, fourth })
                Assert.Equal(2, captains.To(member).Length);
            lock (room.Gate)
            {
                var first = room.Captains.CaptainOf(1);
                var second = room.Captains.CaptainOf(2);
                Assert.Equal(1, room.Seats.Single(seat => seat.Slot == first).Team);
                Assert.Equal(2, room.Seats.Single(seat => seat.Slot == second).Team);
                Assert.Equal(new[] { first!.Value, second!.Value }.Order(), captains.To(rig.Host).Select(message => message.Slot).Order());
            }
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Without the build's exchange, or outside mode 20, a round names no Captain.</summary>
    [Fact]
    public async Task CaptainsNeedTheExchangeAndTheMode()
    {
        var plain = new Rig();
        try
        {
            var room = await StartCaptainRoundAsync(plain, [1, 2]);
            lock (room.Gate) Assert.False(room.Captains.Any);
            await plain.Round.HandleCaptainDroppedAsync(plain.Host, new(room.FindByGame(plain.Host.Id)!.Slot), default);
        }
        finally { await plain.Registry.StopAllAsync(); }

        var other = new Rig();
        var captains = new Sink<RoundCaptainAppointed>();
        other.Round.CaptainAppointed = captains;
        try
        {
            await other.StartAsync();
            await other.Round.HandleLoadReportedAsync(other.Host, new(), default);
            await other.Round.HandleLoadReportedAsync(other.Guest, new(), default);
            await other.WaitStartedAsync(other.Guest);
            Assert.Empty(captains.To(other.Host));
        }
        finally { await other.Registry.StopAllAsync(); }
    }

    /// <summary>
    /// Only the current Captain's report about its own seat hands the Captaincy on; a teammate's death, a report
    /// naming another seat and a repeated report change nothing.
    /// </summary>
    [Fact]
    public async Task CaptainDeathHandsTheCaptaincyToATeammate()
    {
        var rig = new Rig();
        var third = CaptainPlayer(33, "third");
        var captains = new Sink<RoundCaptainAppointed>();
        rig.Round.CaptainAppointed = captains;
        try
        {
            var room = await StartCaptainRoundAsync(rig, [1, 1, 2], third);
            int captain, teammate;
            lock (room.Gate)
            {
                captain = room.Captains.CaptainOf(1)!.Value;
                teammate = room.Seats.Single(seat => seat.Team == 1 && seat.Slot != captain).Slot;
            }
            var captainSession = Member(rig, room, captain, third);
            var teammateSession = Member(rig, room, teammate, third);
            var heard = captains.To(third).Length;

            await rig.Round.HandleCaptainDroppedAsync(teammateSession, new(teammate), default);
            await rig.Round.HandleCaptainDroppedAsync(captainSession, new(teammate), default);
            lock (room.Gate) Assert.Equal(captain, room.Captains.CaptainOf(1));
            Assert.Equal(heard, captains.To(third).Length);

            await rig.Round.HandleCaptainDroppedAsync(captainSession, new(captain), default);
            lock (room.Gate) Assert.Equal(teammate, room.Captains.CaptainOf(1));
            Assert.Equal(new RoundCaptainAppointed(teammate), captains.To(third)[^1]);

            await rig.Round.HandleCaptainDroppedAsync(captainSession, new(captain), default);
            lock (room.Gate) Assert.Equal(teammate, room.Captains.CaptainOf(1));
            Assert.Equal(heard + 1, captains.To(third).Length);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>
    /// The Captain's ULTRA request (no object, the configured kind) is confirmed to the current Captain alone, on
    /// every press, without repeating the appointment; a teammate's request, another kind and a scene object get
    /// nothing, and a fallen Captain loses the answer to its successor.
    /// </summary>
    [Fact]
    public async Task CaptainUltraIsConfirmedToTheCurrentCaptainAlone()
    {
        var rig = new Rig(configure: options => options.CaptainUltraItemKind = 104);
        var third = CaptainPlayer(33, "third");
        var captains = new Sink<RoundCaptainAppointed>();
        rig.Round.CaptainAppointed = captains;
        try
        {
            var room = await StartCaptainRoundAsync(rig, [1, 1, 2], third);
            int captain, teammate, opponent;
            lock (room.Gate)
            {
                captain = room.Captains.CaptainOf(1)!.Value;
                teammate = room.Seats.Single(seat => seat.Team == 1 && seat.Slot != captain).Slot;
                opponent = room.Captains.CaptainOf(2)!.Value;
            }
            var captainSession = Member(rig, room, captain, third);
            var teammateSession = Member(rig, room, teammate, third);
            var opponentSession = Member(rig, room, opponent, third);
            var heard = captains.To(third).Length;

            await rig.Round.HandleSupplyPickupAsync(teammateSession, new(-1, 104), default);
            await rig.Round.HandleSupplyPickupAsync(captainSession, new(-1, 105), default);
            await rig.Round.HandleSupplyPickupAsync(captainSession, new(7, 104), default);
            Assert.Empty(rig.CountedItemUsed.To(captainSession));
            Assert.Empty(rig.CountedItemUsed.To(teammateSession));

            await rig.Round.HandleSupplyPickupAsync(captainSession, new(-1, 104), default);
            await rig.Round.HandleSupplyPickupAsync(captainSession, new(-1, 104), default);
            await rig.Round.HandleSupplyPickupAsync(opponentSession, new(-1, 104), default);
            Assert.Equal(new[] { new RoundCountedItemUsed(captain, 104), new RoundCountedItemUsed(captain, 104) },
                rig.CountedItemUsed.To(captainSession));
            Assert.Equal(new RoundCountedItemUsed(opponent, 104), Assert.Single(rig.CountedItemUsed.To(opponentSession)));
            Assert.Empty(rig.CountedItemUsed.To(teammateSession));
            Assert.Equal(heard, captains.To(third).Length);

            await rig.Round.HandleCaptainDroppedAsync(captainSession, new(captain), default);
            await rig.Round.HandleSupplyPickupAsync(captainSession, new(-1, 104), default);
            Assert.Equal(2, rig.CountedItemUsed.To(captainSession).Length);
            lock (room.Gate) Assert.Equal(teammate, room.Captains.CaptainOf(1));
            await rig.Round.HandleSupplyPickupAsync(teammateSession, new(-1, 104), default);
            Assert.Equal(new RoundCountedItemUsed(teammate, 104), Assert.Single(rig.CountedItemUsed.To(teammateSession)));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>A queued ULTRA cannot be applied to a later round or a new life at the same seat.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueuedCaptainUltraCannotCrossRoundOrLife(bool nextRound)
    {
        var rig = new Rig(configure: options => options.CaptainUltraItemKind = 104);
        rig.Round.CaptainAppointed = new Sink<RoundCaptainAppointed>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? blocker = null;
        try
        {
            var room = await StartCaptainRoundAsync(rig, [1, 2]);
            var seat = room.FindByGame(rig.Host.Id)!;
            blocker = room.Executor.ExecuteAsync(async _ =>
            {
                entered.SetResult();
                await release.Task;
                lock (room.Gate)
                {
                    if (nextRound)
                    {
                        room.ResetToWaiting();
                        room.MarkStarting();
                        room.MarkPlaying();
                    }
                    else seat.Life.Revive();
                    room.Captains.Appoint(seat.Team, seat);
                }
            }, default);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var pending = rig.Round.HandleSupplyPickupAsync(rig.Host, new(-1, 104), default).AsTask();
            Assert.False(pending.IsCompleted);
            release.SetResult();
            await Task.WhenAll(blocker, pending);
            Assert.Empty(rig.CountedItemUsed.To(rig.Host));

            await rig.Round.HandleSupplyPickupAsync(rig.Host, new(-1, 104), default);
            Assert.Equal(new RoundCountedItemUsed(seat.Slot, 104), Assert.Single(rig.CountedItemUsed.To(rig.Host)));
        }
        finally
        {
            release.TrySetResult();
            if (blocker is not null) await blocker;
            await rig.Registry.StopAllAsync();
        }
    }

    /// <summary>Without the option, without the build's exchange, or outside mode 20, a Captain's request is not answered.</summary>
    [Fact]
    public async Task CaptainUltraNeedsTheOptionTheExchangeAndTheMode()
    {
        var off = new Rig();
        off.Round.CaptainAppointed = new Sink<RoundCaptainAppointed>();
        try
        {
            var room = await StartCaptainRoundAsync(off, [1, 2]);
            lock (room.Gate) Assert.True(room.Captains.Serves(room.FindByGame(off.Host.Id)!));
            await off.Round.HandleSupplyPickupAsync(off.Host, new(-1, 104), default);
            Assert.Empty(off.CountedItemUsed.To(off.Host));
        }
        finally { await off.Registry.StopAllAsync(); }

        var plain = new Rig(configure: options => options.CaptainUltraItemKind = 104);
        try
        {
            await StartCaptainRoundAsync(plain, [1, 2]);
            await plain.Round.HandleSupplyPickupAsync(plain.Host, new(-1, 104), default);
            Assert.Empty(plain.CountedItemUsed.To(plain.Host));
        }
        finally { await plain.Registry.StopAllAsync(); }

        var other = new Rig(configure: options => options.CaptainUltraItemKind = 104);
        other.Round.CaptainAppointed = new Sink<RoundCaptainAppointed>();
        try
        {
            await other.StartAsync();
            await other.Round.HandleLoadReportedAsync(other.Host, new(), default);
            await other.Round.HandleLoadReportedAsync(other.Guest, new(), default);
            await other.WaitStartedAsync(other.Guest);
            await other.Round.HandleSupplyPickupAsync(other.Host, new(-1, 104), default);
            Assert.Empty(other.CountedItemUsed.To(other.Host));
        }
        finally { await other.Registry.StopAllAsync(); }
    }

    /// <summary>A sole Captain is restored after the common respawn grant; repeated death reports do not appoint it.</summary>
    [Fact]
    public async Task SoloCaptainWaitsForRespawnBeforeReappointment()
    {
        var rig = new Rig();
        var captains = new Sink<RoundCaptainAppointed>();
        rig.Round.CaptainAppointed = captains;
        try
        {
            var room = await StartCaptainRoundAsync(rig, [1, 2]);
            var slot = room.FindByGame(rig.Host.Id)!.Slot;
            var actor = RoomRegistry.SceneEntity(slot);
            var generation = room.FindByGame(rig.Host.Id)!.Life.Generation;
            await rig.Round.HandleCaptainDroppedAsync(rig.Host, new(slot), default);
            await rig.Round.HandleCaptainDroppedAsync(rig.Host, new(slot), default);
            Assert.Equal(2, captains.To(rig.Guest).Length);
            lock (room.Gate) Assert.Equal(SeatLife.Dead, room.FindByGame(rig.Host.Id)!.Life.State);
            await rig.Round.HandleSpawnAcceptedAsync(rig.Host, new(actor, slot), default);
            Assert.Single(rig.Respawned.To(rig.Guest));
            Assert.Equal(3, captains.To(rig.Guest).Length);
            Assert.Equal(new RoundCaptainAppointed(slot), captains.To(rig.Guest)[^1]);
            lock (room.Gate) Assert.Equal(generation + 1, room.FindByGame(rig.Host.Id)!.Life.Generation);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>
    /// A Captain kill scores once for the opposing killer, whether it arrives before or after the fallen Captain's own
    /// death report; repeats, kills of ordinary members and friendly reports score nothing.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaptainKillIsCreditedOncePerFall(bool dropFirst)
    {
        var (rig, context) = CaptainResultRig();
        var third = CaptainPlayer(33, "third");
        rig.Round.CaptainAppointed = new Sink<RoundCaptainAppointed>();
        try
        {
            var room = await StartCaptainRoundAsync(rig, [1, 1, 2], third);
            int captain, teammate, killer;
            lock (room.Gate)
            {
                captain = room.Captains.CaptainOf(1)!.Value;
                teammate = room.Seats.Single(seat => seat.Team == 1 && seat.Slot != captain).Slot;
                killer = room.Seats.Single(seat => seat.Team == 2).Slot;
            }
            var killerSession = Member(rig, room, killer, third);
            RoundFinishingBlowReported Kill(int reporter, int victim) => new(reporter)
                { Victim = victim, HasVictimReference = true, RequiresReporterOwnership = true, CaptainKill = true };

            await rig.Round.HandleFinishingBlowReportedAsync(killerSession, Kill(killer, teammate), default);
            await rig.Round.HandleFinishingBlowReportedAsync(Member(rig, room, teammate, third), Kill(teammate, captain), default);
            lock (room.Gate) Assert.Equal(0, room.Scoring.TeamTotals[1]);

            if (dropFirst)
                await rig.Round.HandleCaptainDroppedAsync(Member(rig, room, captain, third), new(captain), default);
            await rig.Round.HandleFinishingBlowReportedAsync(killerSession, Kill(killer, captain), default);
            await rig.Round.HandleFinishingBlowReportedAsync(killerSession, Kill(killer, captain), default);
            if (!dropFirst)
                await rig.Round.HandleCaptainDroppedAsync(Member(rig, room, captain, third), new(captain), default);
            await rig.Round.HandleFinishingBlowReportedAsync(killerSession, Kill(killer, captain), default);
            lock (room.Gate)
            {
                Assert.Equal(1, room.Scoring.TeamTotals[1]);
                Assert.Equal(1, room.Scoring.Stats(killer).Kills);
                Assert.Equal(teammate, room.Captains.CaptainOf(1));
            }
        }
        finally { await rig.Registry.StopAllAsync(); await context.UnwindAsync(); }
    }

    /// <summary>A Captain who leaves the round is replaced by a teammate; a later round starts with a fresh roster.</summary>
    [Fact]
    public async Task DepartedCaptainIsReplacedAndRoundsDoNotShareCaptains()
    {
        var rig = new Rig();
        var third = CaptainPlayer(33, "third");
        var captains = new Sink<RoundCaptainAppointed>();
        rig.Round.CaptainAppointed = captains;
        try
        {
            var room = await StartCaptainRoundAsync(rig, [1, 1, 2], third);
            int captain, teammate;
            lock (room.Gate)
            {
                captain = room.Captains.CaptainOf(1)!.Value;
                teammate = room.Seats.Single(seat => seat.Team == 1 && seat.Slot != captain).Slot;
            }
            var leaving = Member(rig, room, captain, third);
            var staying = Member(rig, room, teammate, third);
            await rig.Rooms.HandleLeaveAsync(leaving, new(), default);
            lock (room.Gate) Assert.Equal(teammate, room.Captains.CaptainOf(1));
            Assert.Equal(new RoundCaptainAppointed(teammate), captains.To(staying)[^1]);

            lock (room.Gate)
            {
                room.ResetToWaiting();
                room.MarkStarting();
                Assert.False(room.Captains.Any);
            }
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Ordinary and Captain reports share one death credit, regardless of order or same-seat reappointment.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaptainAndOrdinaryKillsShareLifeCredit(bool ordinaryFirst)
    {
        var (rig, context) = CaptainResultRig();
        rig.Round.CaptainAppointed = new Sink<RoundCaptainAppointed>();
        try
        {
            var room = await StartCaptainRoundAsync(rig, [1, 2]);
            var victim = room.FindByGame(rig.Host.Id)!;
            var killer = room.FindByGame(rig.Guest.Id)!;
            RoundFinishingBlowReported Report(bool captain) => new(killer.Slot)
                { Victim = victim.Slot, HasVictimReference = true, RequiresReporterOwnership = true, CaptainKill = captain };
            for (var life = 0; life < 2; life++)
            {
                await rig.Round.HandleFinishingBlowReportedAsync(rig.Guest, Report(!ordinaryFirst), default);
                await rig.Round.HandleCaptainDroppedAsync(rig.Host, new(victim.Slot), default);
                await rig.Round.HandleFinishingBlowReportedAsync(rig.Guest, Report(ordinaryFirst), default);
                await rig.Round.HandleFinishingBlowReportedAsync(rig.Guest, Report(!ordinaryFirst), default);
                await rig.Round.HandleCaptainDroppedAsync(rig.Host, new(victim.Slot), default);
                lock (room.Gate)
                {
                    Assert.Equal(life + 1, room.Scoring.TeamTotals[1]);
                    Assert.Equal(life + 1, room.Scoring.Stats(victim.Slot).Deaths);
                }
                await rig.Round.HandleSpawnAcceptedAsync(rig.Host, new(RoomRegistry.SceneEntity(victim.Slot), victim.Slot), default);
            }
        }
        finally { await rig.Registry.StopAllAsync(); await context.UnwindAsync(); }
    }

    /// <summary>A team whose characters are dead receives its replacement only after a native respawn completes.</summary>
    [Fact]
    public async Task CaptainReplacementWaitsForALivingTeammate()
    {
        var rig = new Rig();
        var third = CaptainPlayer(33, "third");
        var captains = new Sink<RoundCaptainAppointed>();
        rig.Round.CaptainAppointed = captains;
        try
        {
            var room = await StartCaptainRoundAsync(rig, [1, 1, 2], third);
            RoomSeat captain, other;
            lock (room.Gate)
            {
                captain = room.Seats.Single(seat => seat.Slot == room.Captains.CaptainOf(1));
                other = room.Seats.Single(seat => seat.Team == 1 && seat != captain);
                other.Life.Die();
            }
            var count = captains.To(third).Length;
            await rig.Round.HandleCaptainDroppedAsync(Member(rig, room, captain.Slot, third), new(captain.Slot), default);
            Assert.Equal(count, captains.To(third).Length);
            await rig.Round.HandleSpawnAcceptedAsync(Member(rig, room, other.Slot, third),
                new(RoomRegistry.SceneEntity(other.Slot), other.Slot), default);
            Assert.Equal(count + 1, captains.To(third).Length);
            Assert.Equal(new RoundCaptainAppointed(other.Slot), captains.To(third)[^1]);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Queued death and score reports cannot mutate a later round or a later life of the same seat.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task QueuedCaptainReportsCannotCrossRoundOrLife(bool nextRound, bool scoreReport)
    {
        var (rig, context) = CaptainResultRig();
        var captains = new Sink<RoundCaptainAppointed>();
        rig.Round.CaptainAppointed = captains;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? blocker = null;
        try
        {
            var room = await StartCaptainRoundAsync(rig, [1, 2]);
            var victim = room.FindByGame(rig.Host.Id)!;
            var killer = room.FindByGame(rig.Guest.Id)!;
            var count = captains.To(rig.Guest).Length;
            blocker = room.Executor.ExecuteAsync(async _ =>
            {
                entered.SetResult();
                await release.Task;
                lock (room.Gate)
                {
                    if (nextRound)
                    {
                        room.ResetToWaiting();
                        room.MarkStarting();
                        room.MarkPlaying();
                    }
                    else victim.Life.Revive();
                    room.Captains.Appoint(victim.Team, victim);
                    room.Captains.Appoint(killer.Team, killer);
                }
            }, default);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var pending = scoreReport
                ? rig.Round.HandleFinishingBlowReportedAsync(rig.Guest, new(killer.Slot)
                    { Victim = victim.Slot, HasVictimReference = true, RequiresReporterOwnership = true, CaptainKill = true }, default).AsTask()
                : rig.Round.HandleCaptainDroppedAsync(rig.Host, new(victim.Slot), default).AsTask();
            release.SetResult();
            await blocker;
            await pending;
            Assert.Equal(count, captains.To(rig.Guest).Length);
            lock (room.Gate)
            {
                Assert.Equal(SeatLife.Alive, victim.Life.State);
                Assert.Equal(0, room.Scoring.TeamTotals[1]);
            }
        }
        finally
        {
            release.TrySetResult();
            if (blocker is not null) await blocker;
            await rig.Registry.StopAllAsync();
            await context.UnwindAsync();
        }
    }

    /// <summary>A score sent by an observer cannot spend the killer's shared death credit.</summary>
    [Fact]
    public async Task CaptainObserverCannotClaimAnotherPlayersKill()
    {
        var (rig, context) = CaptainResultRig();
        var observer = CaptainPlayer(33, "observer");
        rig.Round.CaptainAppointed = new Sink<RoundCaptainAppointed>();
        try
        {
            var room = await StartCaptainRoundAsync(rig, [1, 2, 2], observer);
            var victim = room.FindByGame(rig.Host.Id)!;
            var killer = room.FindByGame(rig.Guest.Id)!;
            var report = new RoundFinishingBlowReported(killer.Slot)
                { Victim = victim.Slot, HasVictimReference = true, RequiresReporterOwnership = true, CaptainKill = true };
            await rig.Round.HandleFinishingBlowReportedAsync(observer, report, default);
            lock (room.Gate) Assert.Equal(0, room.Scoring.TeamTotals[1]);
            await rig.Round.HandleFinishingBlowReportedAsync(rig.Guest, report, default);
            lock (room.Gate) Assert.Equal(1, room.Scoring.TeamTotals[1]);
        }
        finally { await rig.Registry.StopAllAsync(); await context.UnwindAsync(); }
    }

    /// <summary>Captain rounds retain ordinary timed team outcomes and publish their final result only once.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaptainRoundsUseTheCommonTimedWinnerAndDraw(bool draw)
    {
        var (rig, context) = CaptainResultRig(timeLimitMs: 1200);
        var ended = Assert.IsType<Sink<RoundEnded>>(Assert.Single(context.Services.GetAll<IMessageSender<RoundEnded>>()));
        rig.Round.CaptainAppointed = new Sink<RoundCaptainAppointed>();
        try
        {
            var room = await StartCaptainRoundAsync(rig, [1, 2]);
            var victim = room.FindByGame(rig.Host.Id)!;
            var killer = room.FindByGame(rig.Guest.Id)!;
            await rig.Round.HandleFinishingBlowReportedAsync(rig.Guest, new(killer.Slot)
                { Victim = victim.Slot, HasVictimReference = true, RequiresReporterOwnership = true, CaptainKill = true }, default);
            if (draw)
                await rig.Round.HandleFinishingBlowReportedAsync(rig.Host, new(victim.Slot)
                    { Victim = killer.Slot, HasVictimReference = true, RequiresReporterOwnership = true, CaptainKill = true }, default);
            for (var i = 0; i < 500 && ended.To(rig.Host).Length == 0; i++) await Task.Delay(10);
            Assert.Equal(draw ? RoundOutcome.Draw : RoundOutcome.Lost, Assert.Single(ended.To(rig.Host)).Outcome);
            Assert.Equal(draw ? RoundOutcome.Draw : RoundOutcome.Won, Assert.Single(ended.To(rig.Guest)).Outcome);
            Assert.Equal(RoundEndReason.TimeLimit, Assert.Single(ended.To(rig.Host)).Reason);
            await rig.Round.HandleEndRequestedAsync(rig.Host, new(), default);
            await room.ResultTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Single(ended.To(rig.Host));
            Assert.Single(ended.To(rig.Guest));
        }
        finally { await rig.Registry.StopAllAsync(); await context.UnwindAsync(); }
    }

    /// <summary>A different connection object cannot report a death using a seated connection's numeric identity.</summary>
    [Fact]
    public async Task CaptainDropRequiresTheOwningConnectionObject()
    {
        var rig = new Rig();
        var captains = new Sink<RoundCaptainAppointed>();
        rig.Round.CaptainAppointed = captains;
        try
        {
            var room = await StartCaptainRoundAsync(rig, [1, 2]);
            var victim = room.FindByGame(rig.Host.Id)!;
            var impostor = CaptainPlayer(rig.Host.Id, "impostor");
            await rig.Round.HandleCaptainDroppedAsync(impostor, new(victim.Slot), default);
            Assert.Equal(2, captains.To(rig.Guest).Length);
            lock (room.Gate) Assert.Equal(SeatLife.Alive, victim.Life.State);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Cancelled rounds ignore both newly arriving reports and reports already queued behind cancellation.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledCaptainRoundsDoNotRotateOrScore(bool queued)
    {
        var (rig, context) = CaptainResultRig();
        var captains = new Sink<RoundCaptainAppointed>();
        rig.Round.CaptainAppointed = captains;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? blocker = null;
        try
        {
            var room = await StartCaptainRoundAsync(rig, [1, 2]);
            var victim = room.FindByGame(rig.Host.Id)!;
            var killer = room.FindByGame(rig.Guest.Id)!;
            if (!queued) lock (room.Gate) room.RoundCancelled = true;
            blocker = room.Executor.ExecuteAsync(async _ =>
            {
                entered.SetResult();
                await release.Task;
                lock (room.Gate) room.RoundCancelled = true;
            }, default);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var drop = rig.Round.HandleCaptainDroppedAsync(rig.Host, new(victim.Slot), default).AsTask();
            var score = rig.Round.HandleFinishingBlowReportedAsync(rig.Guest, new(killer.Slot)
                { Victim = victim.Slot, HasVictimReference = true, RequiresReporterOwnership = true, CaptainKill = true }, default).AsTask();
            release.SetResult();
            await Task.WhenAll(blocker, drop, score);
            Assert.Equal(2, captains.To(rig.Guest).Length);
            lock (room.Gate)
            {
                Assert.Equal(SeatLife.Alive, victim.Life.State);
                Assert.Equal(0, room.Scoring.TeamTotals[1]);
            }
        }
        finally
        {
            release.TrySetResult();
            if (blocker is not null) await blocker;
            await rig.Registry.StopAllAsync();
            await context.UnwindAsync();
        }
    }

    /// <summary>A loading-stage death cannot wait behind the start callback and act on newly appointed Captains.</summary>
    [Fact]
    public async Task LoadingCaptainReportIsRejectedBeforeQueueing()
    {
        var rig = new Rig();
        var captains = new Sink<RoundCaptainAppointed>();
        rig.Round.CaptainAppointed = captains;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? blocker = null;
        try
        {
            var room = await StartCaptainRoundAsync(rig, [1, 2]);
            var victim = room.FindByGame(rig.Host.Id)!;
            blocker = room.Executor.ExecuteAsync(async _ =>
            {
                lock (room.Gate) { room.ResetToWaiting(); room.MarkStarting(); }
                entered.SetResult();
                await release.Task;
                lock (room.Gate) { room.MarkPlaying(); room.Captains.Appoint(victim.Team, victim); }
            }, default);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await rig.Round.HandleCaptainDroppedAsync(rig.Host, new(victim.Slot), default).AsTask().WaitAsync(TimeSpan.FromSeconds(1));
            release.SetResult();
            await blocker;
            Assert.Equal(2, captains.To(rig.Guest).Length);
            lock (room.Gate) Assert.Equal(SeatLife.Alive, victim.Life.State);
        }
        finally
        {
            release.TrySetResult();
            if (blocker is not null) await blocker;
            await rig.Registry.StopAllAsync();
        }
    }

    private static RecordingSession CaptainPlayer(long id, string nickname)
    {
        var session = new RecordingSession(id);
        session.SetLobbyPlayer(id, nickname);
        return session;
    }

    private static ISession Member(Rig rig, PlayRoom room, int slot, params ISession[] extra) =>
        new ISession[] { rig.Host, rig.Guest }.Concat(extra).Single(session => room.FindByGame(session.Id)?.Slot == slot);

    private static (Rig Rig, PluginContext Context) CaptainResultRig(int timeLimitMs = 180000)
    {
        var services = new ServiceRegistry();
        var context = new PluginContext(new PluginRow { Id = "captain-result", Plugin = "captain-result", Source = "test" },
            services, new EventBus(NullLogger.Instance), NullLogger.Instance);
        context.Effect(services.RegisterMany<IMessageSender<RoundScoreUpdated>>(new Sink<RoundScoreUpdated>()));
        context.Effect(services.RegisterMany<IMessageSender<RoundScoreboard>>(new Sink<RoundScoreboard>()));
        context.Effect(services.RegisterMany<IMessageSender<RoundEnded>>(new Sink<RoundEnded>()));
        context.Effect(services.RegisterMany<IMessageSender<ResultStageAdvanced>>(new Sink<ResultStageAdvanced>()));
        return (new Rig(timeLimitMs: timeLimitMs, resultContext: context, configure: options =>
            { options.ResultTimeOverDelayMs = 0; options.ResultRewardDelayMs = 0; }), context);
    }

    /// <summary>Creates a Captain round (mode 20), seats the players on the given teams, plays it and returns the room.</summary>
    private static async Task<PlayRoom> StartCaptainRoundAsync(Rig rig, byte[] teams, params ISession[] extra)
    {
        await rig.Rooms.HandleCreateAsync(rig.Host, new RoomCreateRequested("captain room", "", (int)GameModeId.Captain, "map"), default);
        var roomId = rig.Entered.To(rig.Host)[0].Room.RoomId;
        foreach (var member in extra.Prepend(rig.Guest))
        {
            await rig.Rooms.HandleJoinAsync(member, new RoomJoinRequested(roomId, ""), default);
            await rig.Rooms.HandleReadyAsync(member, new RoomReadyRequested(true), default);
        }
        await rig.Rooms.HandleStartAsync(rig.Host, new RoomStartRequested("map"), default);
        var room = rig.Registry.Find(roomId)!;
        var members = extra.Prepend(rig.Guest).Prepend(rig.Host).ToArray();
        lock (room.Gate)
            for (var i = 0; i < members.Length; i++)
                room.FindByGame(members[i].Id)!.Team = teams[i];
        foreach (var member in members)
            await rig.Round.HandleLoadReportedAsync(member, new(), default);
        foreach (var member in members)
            await rig.WaitStartedAsync(member);
        // The announcement follows the start on the room executor.
        for (var i = 0; i < 200 && rig.Round.CaptainAppointed is Sink<RoundCaptainAppointed> sink
            && sink.To(rig.Host).Length < teams.Distinct().Count(); i++)
            await Task.Delay(10);
        return room;
    }
}
