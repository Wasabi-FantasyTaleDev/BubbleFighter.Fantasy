using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.InGame.Sessions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class RoundFlowTests
{
    /// <summary>
    /// A legacy death report and its spawn acceptance move the same life the modern reports do: the death is Dead
    /// with the reported handle, the acceptance brings it back Alive for everyone, and a new round starts Alive.
    /// </summary>
    [Fact]
    public async Task ALegacyDeathAndSpawnAcceptanceMoveTheSharedLife()
    {
        var rig = new Rig();
        try
        {
            await rig.StartAsync();
            await rig.Round.HandleLoadReportedAsync(rig.Host, new RoundLoadReported(), CancellationToken.None);
            await rig.Round.HandleLoadReportedAsync(rig.Guest, new RoundLoadReported(), CancellationToken.None);
            await rig.WaitStartedAsync(rig.Guest);
            var room = rig.Registry.Find(rig.RoomId)!;
            var guest = room.FindByGame(rig.Guest.Id)!;

            await rig.Round.HandleDeathReportedAsync(rig.Guest, new RoundDeathReported(1100000, 3200), CancellationToken.None);
            lock (room.Gate)
                Assert.Equal((SeatLife.Dead, (int?)1100000, false), (guest.Life.State, guest.Life.DeathActor, guest.Life.ServerTimedWait));
            await rig.Round.HandleSpawnAcceptedAsync(rig.Guest, new RoundSpawnAccepted(1100000, 1), CancellationToken.None);
            lock (room.Gate)
                Assert.Equal((SeatLife.Alive, (int?)null), (guest.Life.State, guest.Life.DeathActor));
            foreach (var member in new ISession[] { rig.Host, rig.Guest })
                Assert.Equal(new RoundPlayerRespawned(rig.RoomId, 1, 1100000, 1), Assert.Single(rig.Respawned.To(member)));

            await rig.Round.HandleDeathReportedAsync(rig.Guest, new RoundDeathReported(1100000, 3200), CancellationToken.None);
            lock (room.Gate)
            {
                room.ResetToWaiting();
                room.MarkStarting();
                Assert.Equal((SeatLife.Alive, (int?)null), (guest.Life.State, guest.Life.DeathActor));
            }
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>
    /// A cooperative revival of a server-timed death is one offer without a match link: the repeated death report
    /// changes nothing while it is outstanding, the timed wait offers nothing more, and the spawn acceptance revives
    /// the player for the whole room. A revival that arrives before its death report is answered by that report.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ACooperativeRevivalIsOneLinklessOfferCompletedByTheSpawnAcceptance(bool early)
    {
        var rig = new Rig(configure: options => options.PveRevivalModes = [4]);
        try
        {
            await rig.StartAsync();
            await rig.Round.HandleLoadReportedAsync(rig.Host, new RoundLoadReported(), CancellationToken.None);
            await rig.Round.HandleLoadReportedAsync(rig.Guest, new RoundLoadReported(), CancellationToken.None);
            await rig.WaitStartedAsync(rig.Guest);
            var room = rig.Registry.Find(rig.RoomId)!;
            lock (room.Gate) room.ModeId = 4;
            var guest = room.FindByGame(rig.Guest.Id)!;

            if (early)
            {
                await rig.Round.HandlePveRevivalAsync(rig.Guest, new RoundPveRevivalRequested(), CancellationToken.None);
                Assert.Empty(rig.SpawnOffered.To(rig.Guest));
                lock (room.Gate) Assert.True(guest.Life.RevivalRequestedEarly);
                await rig.Round.HandleDeathReportedAsync(rig.Guest, new RoundDeathReported(1100000, 20_000, ServerRevives: true), CancellationToken.None);
            }
            else
            {
                await rig.Round.HandleDeathReportedAsync(rig.Guest, new RoundDeathReported(1100000, 20_000, ServerRevives: true), CancellationToken.None);
                await rig.Round.HandlePveRevivalAsync(rig.Guest, new RoundPveRevivalRequested(), CancellationToken.None);
            }
            Assert.Equal(new RoundSpawnOffered(1100000, 1), Assert.Single(rig.SpawnOffered.To(rig.Guest)));
            lock (room.Gate)
            {
                Assert.True(guest.Life.RevivalOffered);
                Assert.False(guest.Life.RevivalRequestedEarly);
            }

            await rig.Round.HandleDeathReportedAsync(rig.Guest, new RoundDeathReported(1100001, 200, ServerRevives: true), CancellationToken.None);
            await rig.Round.HandlePveRevivalAsync(rig.Guest, new RoundPveRevivalRequested(), CancellationToken.None);
            await Task.Delay(400);
            Assert.Single(rig.SpawnOffered.To(rig.Guest));
            lock (room.Gate)
                Assert.Equal((SeatLife.ResetPending, (int?)1100000), (guest.Life.State, guest.Life.DeathActor));

            await rig.Round.HandleSpawnAcceptedAsync(rig.Guest, new RoundSpawnAccepted(1100000, 1), CancellationToken.None);
            lock (room.Gate)
                Assert.Equal((SeatLife.Alive, false), (guest.Life.State, guest.Life.RevivalOffered));
            foreach (var member in new ISession[] { rig.Host, rig.Guest })
                Assert.Equal(new RoundPlayerRespawned(rig.RoomId, 1, 1100000, 1), Assert.Single(rig.Respawned.To(member)));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }
}
