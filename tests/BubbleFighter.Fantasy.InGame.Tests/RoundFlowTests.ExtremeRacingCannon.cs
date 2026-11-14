using System.Numerics;
using System.Text;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.InGame.Sessions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class RoundFlowTests
{
    private static readonly byte[] CannonPayload = new byte[38];

    /// <summary>
    /// A relayed cannon shot is forwarded once, to the other initialized participants only, when the actor is the
    /// sender's own seat, its vectors are finite and it answers an outstanding command exactly.
    /// </summary>
    [Fact]
    public async Task ExtremeRacingCannonShotIsForwardedOnceAfterEveryCheck()
    {
        var (rig, room, ledger) = await CannonRoundAsync();
        try
        {
            int host, guest;
            lock (room.Gate)
            {
                host = RoomRegistry.SceneEntity(room.FindByGame(rig.Host.Id)!.Slot);
                guest = RoomRegistry.SceneEntity(room.FindByGame(rig.Guest.Id)!.Slot);
                Assert.True(ledger.TryIssue(100, 50004));
            }
            var rejected = new[]
            {
                (rig.Host, Shot(guest, 50004, 100)),
                (rig.Host, Shot(int.MaxValue, 50004, 100)),
                (rig.Host, Shot(host, 50004, 100) with { Origin = new(float.NaN, 0, 0) }),
                (rig.Host, Shot(host, 50004, 100) with { Direction = new(0, float.PositiveInfinity, 0) }),
                (rig.Host, Shot(host, 50007, 100)),
                (rig.Host, Shot(host, 50004, 101)),
                (rig.Host, Shot(host, 50004, 102)),
                (rig.Host, Shot(host, 50004, 9_999)),
            };
            foreach (var (sender, shot) in rejected)
                await rig.Round.HandleFeedAsync(sender, Feed(shot), default);
            Assert.Empty(rig.Feed.To(rig.Guest));
            Assert.Empty(rig.Feed.To(rig.Host));
            lock (room.Gate) Assert.Equal(1, ledger.Outstanding);

            await rig.Round.HandleFeedAsync(rig.Host, Feed(Shot(host, 50004, 100)), default);
            var received = Assert.Single(rig.Feed.To(rig.Guest));
            Assert.Equal((ushort)32902, received.Code);
            Assert.Equal(CannonPayload, received.Payload.ToArray());
            Assert.Empty(rig.Feed.To(rig.Host));
            lock (room.Gate) Assert.Equal(0, ledger.Outstanding);

            // The command is spent: a replay, a re-send and a frame from the other seat all stop here.
            await rig.Round.HandleFeedAsync(rig.Host, Feed(Shot(host, 50004, 100)), default);
            await rig.Round.HandleFeedAsync(rig.Guest, Feed(Shot(guest, 50004, 100)), default);
            Assert.Single(rig.Feed.To(rig.Guest));
            Assert.Empty(rig.Feed.To(rig.Host));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>A sender or recipient that never received the initialization takes no part in cannon traffic.</summary>
    [Fact]
    public async Task ExtremeRacingCannonShotNeedsInitializedParticipants()
    {
        var (rig, room, ledger) = await CannonRoundAsync();
        try
        {
            int host;
            lock (room.Gate)
            {
                host = RoomRegistry.SceneEntity(room.FindByGame(rig.Host.Id)!.Slot);
                Assert.True(ledger.TryIssue(100, 50004));
                room.RacingInitialization!.Recipients.Remove(rig.Guest);
            }
            await rig.Round.HandleFeedAsync(rig.Host, Feed(Shot(host, 50004, 100)), default);
            Assert.Empty(rig.Feed.To(rig.Guest));
            lock (room.Gate)
            {
                Assert.Equal(0, ledger.Outstanding);
                Assert.True(ledger.TryIssue(100, 50004));
                room.RacingInitialization!.Recipients.Remove(rig.Host);
            }
            await rig.Round.HandleFeedAsync(rig.Host, Feed(Shot(host, 50004, 100)), default);
            Assert.Empty(rig.Feed.To(rig.Guest));
            lock (room.Gate) Assert.Equal(1, ledger.Outstanding);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>A cancelled or stopped round and a round of another generation forward nothing.</summary>
    [Fact]
    public async Task ExtremeRacingCannonShotIsDroppedOutsideTheGeneration()
    {
        var (rig, room, ledger) = await CannonRoundAsync();
        try
        {
            int host;
            lock (room.Gate)
            {
                host = RoomRegistry.SceneEntity(room.FindByGame(rig.Host.Id)!.Slot);
                Assert.True(ledger.TryIssue(100, 50004));
                room.RoundCancelled = true;
            }
            await rig.Round.HandleFeedAsync(rig.Host, Feed(Shot(host, 50004, 100)), default);
            Assert.Empty(rig.Feed.To(rig.Guest));
            lock (room.Gate) Assert.Equal(1, ledger.Outstanding);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>The cannon option loads the map's data and fails when its count differs from the initialized pairs.</summary>
    [Fact]
    public async Task ExtremeRacingCannonConfigurationRequiresMatchingData()
    {
        var (rig, initialization) = RacingInitializationRig(mount: false, configure: options => options.ExtremeRacingCannon = true);
        try
        {
            Assert.Throws<FileNotFoundException>(() => rig.Round.ConfigureExtremeRacingInitialization(
                ExtremeRacingMapDataTests.ForStage("map"), initialization));
            var three = ExtremeRacingCannonScheduleTests.TwoCannons.Replace("CANNONCOUNT=2", "CANNONCOUNT=3", StringComparison.Ordinal);
            Assert.Throws<InvalidDataException>(() => rig.Round.ConfigureExtremeRacingInitialization(
                ExtremeRacingMapDataTests.ForStage("map", cannons: Encoding.ASCII.GetBytes(three)), initialization));
            rig.Round.ConfigureExtremeRacingInitialization(
                ExtremeRacingMapDataTests.ForStage("map", cannons: Encoding.ASCII.GetBytes(ExtremeRacingCannonScheduleTests.TwoCannons)),
                initialization);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Without the option a shot frame is not forwarded even when a route decoded it.</summary>
    [Fact]
    public async Task ExtremeRacingCannonShotIsIgnoredWhenTheOptionIsOff()
    {
        var (rig, initialization) = RacingInitializationRig();
        try
        {
            var room = await PrepareRacingInitializationRoundAsync(rig);
            await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
            await rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default);
            await rig.WaitStartedAsync(rig.Host);
            await rig.WaitStartedAsync(rig.Guest);
            int host;
            lock (room.Gate)
            {
                host = RoomRegistry.SceneEntity(room.FindByGame(rig.Host.Id)!.Slot);
                Assert.Null(room.RacingInitialization!.CannonLedger);
            }
            await rig.Round.HandleFeedAsync(rig.Host, Feed(Shot(host, 50004, 100)), default);
            Assert.Empty(rig.Feed.To(rig.Guest));
            Assert.Single(initialization.To(rig.Guest));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    private static RoundExtremeRacingCannonShot Shot(int actor, ushort weapon, uint identity) =>
        new(actor, weapon, new Vector3(1, 2, 3), new Vector3(0, 0, -1), identity, 100_001);

    private static RoundFeedSent Feed(RoundExtremeRacingCannonShot shot) => new(32902, CannonPayload)
    {
        // The rig has no match relay, so the frame travels on the game links like the checkpoint tests above.
        ReportedActors = [shot.Actor], ExtremeRacingCannonShot = shot,
    };

    private static async Task<(Rig Rig, PlayRoom Room, ExtremeRacingCannonLedger Ledger)> CannonRoundAsync()
    {
        var (rig, initialization) = RacingInitializationRig(mount: false, configure: options => options.ExtremeRacingCannon = true);
        rig.Round.ConfigureExtremeRacingInitialization(
            ExtremeRacingMapDataTests.ForStage("map", cannons: Encoding.ASCII.GetBytes(ExtremeRacingCannonScheduleTests.TwoCannons)),
            initialization);
        var room = await PrepareRacingInitializationRoundAsync(rig);
        await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
        await rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default);
        await rig.WaitStartedAsync(rig.Host);
        await rig.WaitStartedAsync(rig.Guest);
        lock (room.Gate)
        {
            Assert.Equal(RoomPhase.Playing, room.Phase);
            return (rig, room, room.RacingInitialization!.CannonLedger!);
        }
    }
}
