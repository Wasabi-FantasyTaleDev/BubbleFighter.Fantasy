using System.Text;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.InGame.Data;
using BubbleFighter.Fantasy.InGame.Sessions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class RoundFlowTests
{
    // Two cannons that share one interval: cannon 0 lists a single weapon, cannon 1 lists two.
    private static string FiringCannons(int intervalMs) =>
        $"[Count]\r\nCANNONCOUNT=2\r\n[Cannon0]\r\nWEAPONCOUNT=1\r\nINTERVAL={intervalMs}\r\nSTAGE=1\r\n"
        + "[Cannon0_Weapon0]\r\nWEAPONID=50004\r\nCOUNT=1\r\nSCALE=1.0\r\n"
        + $"[Cannon1]\r\nWEAPONCOUNT=2\r\nINTERVAL={intervalMs}\r\nSTAGE=1\r\n"
        + "[Cannon1_Weapon0]\r\nWEAPONID=50007\r\nCOUNT=1\r\nSCALE=1.0\r\n[Cannon1_Weapon1]\r\nWEAPONID=50002\r\nCOUNT=1\r\nSCALE=0.5\r\n";

    private sealed record FiringRound(Rig Rig, PlayRoom Room, ExtremeRacingInitializationState State,
        Sink<RoundExtremeRacingCannonCommand> Commands, List<string> Log)
    {
        internal ExtremeRacingCannonLedger Ledger => State.CannonLedger!;
    }

    // Starts a racing round with the cannon schedule on and waits until the first race round's cadence is armed.
    private static async Task<FiringRound> FiringRoundAsync(int intervalMs)
    {
        var (rig, initialization) = RacingInitializationRig(mount: false, configure: options =>
        {
            options.ExtremeRacingCannon = true;
            options.ExtremeRacingCannonFire = true;
        });
        var log = new List<string>();
        var sinks = new ExtremeRacingSinks();
        sinks.Timers.OnSending = (_, message, _) => { lock (log) log.Add($"timer {message.Timer}"); };
        rig.Round.ExtremeRacing = sinks.Senders;
        var commands = new Sink<RoundExtremeRacingCannonCommand>();
        commands.OnSending = (session, message, _) =>
        {
            lock (log) log.Add($"command {message.Identity} {message.Weapon}");
        };
        rig.Round.ConfigureExtremeRacingInitialization(
            ExtremeRacingMapDataTests.ForStage("map", cannons: Encoding.ASCII.GetBytes(FiringCannons(intervalMs))), initialization, commands);
        var room = await PrepareRacingInitializationRoundAsync(rig);
        await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
        await rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default);
        await rig.WaitStartedAsync(rig.Host);
        await rig.WaitStartedAsync(rig.Guest);
        ExtremeRacingInitializationState state;
        lock (room.Gate) state = room.RacingInitialization!;
        await WaitForAsync(() => { lock (room.Gate) return state.CannonCadence!.Active; });
        return new(rig, room, state, commands, log);
    }

    private static int Actor(FiringRound round, ISession session)
    {
        lock (round.Room.Gate) return RoomRegistry.SceneEntity(round.Room.FindByGame(session.Id)!.Slot);
    }

    /// <summary>
    /// The cadence arms only after the race clock (S2C 711) was sent, and the first command goes to the one origin seat,
    /// the lowest slot, with group zero, the cannon's first pair identity and scale one.
    /// </summary>
    [Fact]
    public async Task ExtremeRacingCannonsStartAfterTheRaceClockAndFireAtTheOriginOnly()
    {
        var round = await FiringRoundAsync(30);
        try
        {
            await WaitForAsync(() => round.Commands.To(round.Rig.Host).Length >= 2);
            lock (round.Room.Gate) Assert.Equal(0, round.State.CannonOrigin!.Slot);
            Assert.Empty(round.Commands.To(round.Rig.Guest));
            var first = round.Commands.To(round.Rig.Host);
            Assert.Equal(new RoundExtremeRacingCannonCommand(0, 100, 50004, 1.0f), first.Single(command => command.Identity == 100));
            // The second cannon's identity is the pair after the first, and its first listed weapon leads.
            Assert.Equal(new RoundExtremeRacingCannonCommand(0, 102, 50007, 1.0f), first.Single(command => command.Identity == 102));
            lock (round.Log)
            {
                Assert.StartsWith("timer", round.Log[0], StringComparison.Ordinal);
                Assert.Contains(round.Log, entry => entry.StartsWith("command", StringComparison.Ordinal));
                Assert.True(round.Log.FindIndex(entry => entry.StartsWith("timer", StringComparison.Ordinal))
                    < round.Log.FindIndex(entry => entry.StartsWith("command", StringComparison.Ordinal)));
            }
        }
        finally { await round.Rig.Registry.StopAllAsync(); }
    }

    /// <summary>A long interval leaves the cadence armed but silent: nothing is sent before an interval has passed.</summary>
    [Fact]
    public async Task ExtremeRacingCannonsSendNothingBeforeTheirFirstInterval()
    {
        var round = await FiringRoundAsync(60_000);
        try
        {
            await Task.Delay(150);
            Assert.Empty(round.Commands.To(round.Rig.Host));
            Assert.Empty(round.Commands.To(round.Rig.Guest));
            lock (round.Room.Gate)
            {
                Assert.True(round.State.CannonCadence!.NextDue > Environment.TickCount64 + 50_000);
                Assert.Equal(0, round.Ledger.Outstanding);
            }
        }
        finally { await round.Rig.Registry.StopAllAsync(); }
    }

    /// <summary>
    /// A cannon pair with a command outstanding is skipped, never queued; an answering shot frees the pair and the
    /// next weapon of a multi-weapon cannon follows in file order.
    /// </summary>
    [Fact]
    public async Task ExtremeRacingCannonsIssueNothingWhileACommandIsOutstanding()
    {
        var round = await FiringRoundAsync(30);
        try
        {
            var host = round.Rig.Host;
            await WaitForAsync(() => round.Commands.To(host).Length >= 2);
            await Task.Delay(250);
            Assert.Equal(2, round.Commands.To(host).Length);
            lock (round.Room.Gate) Assert.Equal(2, round.Ledger.Outstanding);

            await round.Rig.Round.HandleFeedAsync(host, Feed(Shot(Actor(round, host), 50004, 100)), default);
            await WaitForAsync(() => round.Commands.To(host).Length == 3);
            await Task.Delay(250);
            Assert.Equal(3, round.Commands.To(host).Length);
            Assert.Equal(new RoundExtremeRacingCannonCommand(0, 100, 50004, 1.0f), round.Commands.To(host)[2]);

            await round.Rig.Round.HandleFeedAsync(host, Feed(Shot(Actor(round, host), 50007, 102)), default);
            await WaitForAsync(() => round.Commands.To(host).Length == 4);
            Assert.Equal(new RoundExtremeRacingCannonCommand(0, 102, 50002, 1.0f), round.Commands.To(host)[3]);
            Assert.Equal(2, round.Rig.Feed.To(round.Rig.Guest).Length);
        }
        finally { await round.Rig.Registry.StopAllAsync(); }
    }

    /// <summary>
    /// Closing the race round stops the cadence and revokes its commands; the next race round arms it again from
    /// its own clock.
    /// </summary>
    [Fact]
    public async Task ExtremeRacingCannonsStopWhenTheRaceRoundClosesAndResumeWithTheNextOne()
    {
        var round = await FiringRoundAsync(30);
        try
        {
            var host = round.Rig.Host;
            await WaitForAsync(() => round.Commands.To(host).Length >= 2);
            await round.Rig.Round.HandleExtremeRacingGoalAsync(host, new(1), default);
            await round.Rig.Round.HandleExtremeRacingGoalAsync(round.Rig.Guest, new(1), default);
            lock (round.Room.Gate)
            {
                Assert.False(round.State.CannonCadence!.Active);
                Assert.Equal(0, round.Ledger.Outstanding);
            }
            // The intermission lasts longer than this wait; no command is issued during it.
            var before = round.Commands.To(host).Length;
            await Task.Delay(300);
            Assert.Equal(before, round.Commands.To(host).Length);
            // The revoked commands can no longer be answered.
            await round.Rig.Round.HandleFeedAsync(host, Feed(Shot(Actor(round, host), 50004, 100)), default);
            Assert.Empty(round.Rig.Feed.To(round.Rig.Guest));

            await WaitForAsync(() => { lock (round.Room.Gate) return round.Room.ExtremeRacing.Round == 2 && round.State.CannonCadence!.Active; });
            await WaitForAsync(() => round.Commands.To(host).Length > before);
        }
        finally { await round.Rig.Registry.StopAllAsync(); }
    }

    /// <summary>Stopping the room cancels and drains the cannon clock; nothing is sent afterwards.</summary>
    [Fact]
    public async Task ExtremeRacingCannonsStopWithTheRoom()
    {
        var round = await FiringRoundAsync(30);
        var host = round.Rig.Host;
        await WaitForAsync(() => round.Commands.To(host).Length >= 2);
        RoomDeadlineClock clock;
        lock (round.Room.Gate) clock = round.Room.ExtremeRacingCannonClock!;
        await round.Rig.Registry.StopAllAsync();
        Assert.True(clock.Completion.IsCompleted);
        var sent = round.Commands.To(host).Length;
        await Task.Delay(200);
        Assert.Equal(sent, round.Commands.To(host).Length);
    }

    /// <summary>A command step of an earlier generation's state does nothing once the room holds another state.</summary>
    [Fact]
    public async Task ExtremeRacingCannonsIgnoreAReplacedGeneration()
    {
        var round = await FiringRoundAsync(30);
        try
        {
            var host = round.Rig.Host;
            await WaitForAsync(() => round.Commands.To(host).Length >= 2);
            lock (round.Room.Gate) round.Room.RacingInitialization = new(round.Room.StartGeneration, round.State.Map);
            await round.Room.Executor.ExecuteAsync(_ => ValueTask.CompletedTask);
            var sent = round.Commands.To(host).Length;
            await Task.Delay(250);
            Assert.Equal(sent, round.Commands.To(host).Length);
        }
        finally { await round.Rig.Registry.StopAllAsync(); }
    }

    /// <summary>
    /// When the origin leaves, its outstanding commands are revoked and another initialized seat becomes the origin
    /// for later commands only; the departed seat's commands are never repeated to it or to anyone else.
    /// </summary>
    [Fact]
    public async Task ExtremeRacingCannonOriginDepartureHandsOverWithoutResending()
    {
        var round = await FiringRoundAsync(60_000);
        try
        {
            lock (round.Room.Gate)
            {
                Assert.True(round.Ledger.TryIssue(100, 50004));
                Assert.Equal(0, round.State.CannonOrigin!.Slot);
            }
            await round.Rig.Rooms.HandleLeaveAsync(round.Rig.Host, new(), default);
            lock (round.Room.Gate)
            {
                Assert.Equal(0, round.Ledger.Outstanding);
                Assert.Same(round.Room.FindByGame(round.Rig.Guest.Id), round.State.CannonOrigin);
            }
            Assert.Empty(round.Commands.To(round.Rig.Host));
            Assert.Empty(round.Commands.To(round.Rig.Guest));
        }
        finally { await round.Rig.Registry.StopAllAsync(); }
    }

    /// <summary>After a handover the remaining seat receives later commands, and the departed seat receives none.</summary>
    [Fact]
    public async Task ExtremeRacingCannonsContinueAtTheNewOrigin()
    {
        var round = await FiringRoundAsync(30);
        try
        {
            await WaitForAsync(() => round.Commands.To(round.Rig.Host).Length >= 2);
            await round.Rig.Rooms.HandleLeaveAsync(round.Rig.Host, new(), default);
            var hostCommands = round.Commands.To(round.Rig.Host).Length;
            await WaitForAsync(() => round.Commands.To(round.Rig.Guest).Length >= 2);
            Assert.Equal(hostCommands, round.Commands.To(round.Rig.Host).Length);
            // Every command the new origin holds was issued after the handover: the old ones were revoked, not resent,
            // so only the fresh pair identities are outstanding and each is outstanding once.
            lock (round.Room.Gate) Assert.True(round.Ledger.Outstanding <= round.Ledger.Capacity);
            Assert.Equal(round.Commands.To(round.Rig.Guest).Select(command => command.Identity).Distinct().Count(),
                round.Commands.To(round.Rig.Guest).Length);
        }
        finally { await round.Rig.Registry.StopAllAsync(); }
    }

    /// <summary>A seat that never received the initialization is never chosen as the origin.</summary>
    [Fact]
    public async Task ExtremeRacingCannonOriginMustHaveBeenInitialized()
    {
        var round = await FiringRoundAsync(60_000);
        try
        {
            lock (round.Room.Gate) round.State.Recipients.Remove(round.Rig.Guest);
            await round.Rig.Rooms.HandleLeaveAsync(round.Rig.Host, new(), default);
            lock (round.Room.Gate)
            {
                Assert.Null(round.State.CannonOrigin);
                Assert.Equal(0, round.Ledger.Outstanding);
            }
            Assert.Empty(round.Commands.To(round.Rig.Guest));
        }
        finally { await round.Rig.Registry.StopAllAsync(); }
    }

    /// <summary>
    /// A failed send ends the generation's schedule: the command is revoked, nothing is retried, the clock stops and the
    /// race itself carries on.
    /// </summary>
    [Fact]
    public async Task ExtremeRacingCannonSendFailureCancelsTheSchedule()
    {
        var (rig, initialization) = RacingInitializationRig(mount: false, configure: options =>
        {
            options.ExtremeRacingCannon = true;
            options.ExtremeRacingCannonFire = true;
        });
        var commands = new Sink<RoundExtremeRacingCannonCommand>();
        var attempts = 0;
        commands.OnSending = (_, _, _) =>
        {
            Interlocked.Increment(ref attempts);
            throw new InvalidOperationException("Synthetic partial write.");
        };
        rig.Round.ConfigureExtremeRacingInitialization(
            ExtremeRacingMapDataTests.ForStage("map", cannons: Encoding.ASCII.GetBytes(FiringCannons(30))), initialization, commands);
        try
        {
            var room = await PrepareRacingInitializationRoundAsync(rig);
            await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
            await rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default);
            await rig.WaitStartedAsync(rig.Host);
            await rig.WaitStartedAsync(rig.Guest);
            ExtremeRacingInitializationState state;
            lock (room.Gate) state = room.RacingInitialization!;
            await WaitForAsync(() => { lock (room.Gate) return state.CannonScheduleFailed; });
            RoomDeadlineClock clock;
            lock (room.Gate) clock = room.ExtremeRacingCannonClock!;
            await clock.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(200);
            Assert.Equal(1, Volatile.Read(ref attempts));
            lock (room.Gate)
            {
                Assert.Equal(0, state.CannonLedger!.Outstanding);
                Assert.False(state.CannonCadence!.Active);
                Assert.True(room.ExtremeRacing.IsActive);
                Assert.False(room.RoundCancelled);
            }
            Assert.False(rig.Host.Connection.IsClosed);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>The fire option needs the relay option and the build's command sender.</summary>
    [Fact]
    public async Task ExtremeRacingCannonFireIsValidated()
    {
        Assert.Contains(new InGameOptions { ExtremeRacingCannonFire = true }.ValidateExtremeRacingInitialization(),
            result => result.ErrorMessage!.Contains("extreme_racing_cannon_fire", StringComparison.Ordinal));
        var (rig, initialization) = RacingInitializationRig(mount: false, configure: options =>
        {
            options.ExtremeRacingCannon = true;
            options.ExtremeRacingCannonFire = true;
        });
        try
        {
            Assert.Throws<ConfigException>(() => rig.Round.ConfigureExtremeRacingInitialization(
                ExtremeRacingMapDataTests.ForStage("map", cannons: Encoding.ASCII.GetBytes(FiringCannons(30))), initialization));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }
}

/// <summary>Exercises the proposed firing cadence with explicit times, so no clock is involved.</summary>
public sealed class ExtremeRacingCannonCadenceTests
{
    private static ExtremeRacingCannonCadence Cadence() => new(ExtremeRacingCannonSchedule.Parse(
        Encoding.ASCII.GetBytes(ExtremeRacingCannonScheduleTests.TwoCannons), 4096, 2, 3, 100));

    /// <summary>Each cannon is due one interval after the round's clock starts, and a disarmed cadence is never due.</summary>
    [Fact]
    public void CannonsAreDueOneIntervalAfterTheClockStarts()
    {
        var cadence = Cadence();
        Assert.Null(cadence.NextDue);
        Assert.Empty(cadence.TakeDue(10_000, _ => false));
        cadence.Begin(1_000);
        Assert.Equal(1_800L, cadence.NextDue);
        Assert.Empty(cadence.TakeDue(1_799, _ => false));
        Assert.Equal(new[] { (1, 50007) }, cadence.TakeDue(1_800, _ => false));
        Assert.Equal(2_600L, cadence.NextDue);
        cadence.End();
        Assert.Null(cadence.NextDue);
        Assert.Empty(cadence.TakeDue(100_000, _ => false));
    }

    /// <summary>A cannon with several weapons cycles through them in file order.</summary>
    [Fact]
    public void WeaponsCycleInFileOrder()
    {
        var cadence = Cadence();
        cadence.Begin(0);
        Assert.Equal(new[] { (1, 50007) }, cadence.TakeDue(800, _ => false));
        Assert.Equal(new[] { (1, 50002) }, cadence.TakeDue(1_600, _ => false));
        Assert.Equal(new[] { (1, 50007) }, cadence.TakeDue(2_400, _ => false));
    }

    /// <summary>A stall serves each due cannon once, a pair with an outstanding command is skipped without advancing its weapon.</summary>
    [Fact]
    public void StallsDoNotBurstAndOutstandingPairsAreSkipped()
    {
        var cadence = Cadence();
        cadence.Begin(0);
        Assert.Equal(new[] { (0, 50004), (1, 50007) }, cadence.TakeDue(10_000, _ => false));
        Assert.Equal(10_800L, cadence.NextDue);
        Assert.Empty(cadence.TakeDue(10_800, cannon => cannon == 1));
        Assert.Equal(11_600L, cadence.NextDue);
        Assert.Equal(new[] { (1, 50002) }, cadence.TakeDue(11_600, _ => false));
    }
}
