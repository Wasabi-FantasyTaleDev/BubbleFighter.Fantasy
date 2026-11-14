using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Tests.Shared;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class RoundFlowTests
{
    /// <summary>A named report counts only its authenticated seat, during both loading and gameplay.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NamedStarReportAcceptsOwnSeatDuringStartingAndPlaying(bool playing)
    {
        var rig = StarRig(team: true);
        try
        {
            await rig.StartAsync();
            if (playing)
            {
                await FinishLoadingAsync(rig);
                await rig.WaitStartedAsync(rig.Host);
            }
            var room = rig.Registry.Find(rig.RoomId)!;
            var guestSlot = room.FindByGame(rig.Guest.Id)!.Slot;
            await rig.Round.HandleStarReportedAsync(rig.Guest,
                new(guestSlot, RoundStarChange.BigGained) { NamedReporterSlot = guestSlot }, default);
            Assert.Equal(10, room.Scoring.Stars(guestSlot));
            await rig.Round.HandleStarReportedAsync(rig.Guest,
                new(guestSlot, RoundStarChange.SmallLost) { NamedReporterSlot = guestSlot }, default);
            Assert.Equal(9, room.Scoring.Stars(guestSlot));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>A survivor's loss report naming another seat changes neither the survivor nor that named seat.</summary>
    [Theory]
    [InlineData(RoundStarChange.SmallLost)]
    [InlineData(RoundStarChange.BigLost)]
    public async Task ForeignNamedStarLossCannotDebitEitherSeat(RoundStarChange change)
    {
        var rig = StarRig(team: true);
        try
        {
            await StartZombiePvpAsync(rig);
            var room = rig.Registry.Find(rig.RoomId)!;
            var hostSlot = room.FindByGame(rig.Host.Id)!.Slot;
            var guestSlot = room.FindByGame(rig.Guest.Id)!.Slot;
            await ReportStarsAsync(rig, rig.Host, RoundStarChange.BigGained);
            await ReportStarsAsync(rig, rig.Guest, RoundStarChange.BigGained);
            await rig.Round.HandleStarReportedAsync(rig.Host,
                new(guestSlot, change) { NamedReporterSlot = guestSlot }, default);
            Assert.Equal(10, room.Scoring.Stars(hostSlot));
            Assert.Equal(10, room.Scoring.Stars(guestSlot));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>A drop creates the object; only the dropper's following named score report subtracts held stars.</summary>
    [Theory]
    [InlineData(1, RoundStarChange.SmallLost, 19)]
    [InlineData(2, RoundStarChange.BigLost, 10)]
    public async Task NamedStarDropCountsItsSeparateLossReportOnce(int kind, RoundStarChange change, int expected)
    {
        var rig = StarRig(team: true);
        try
        {
            await StartZombiePvpAsync(rig);
            var room = rig.Registry.Find(rig.RoomId)!;
            var slot = room.FindByGame(rig.Host.Id)!.Slot;
            await ReportStarsAsync(rig, rig.Host, RoundStarChange.BigGained, 2);
            await rig.Round.HandleStarDropAsync(rig.Host, new(StarPoint, kind, 1), default);
            Assert.Equal(20, room.Scoring.Stars(slot));
            Assert.Single(rig.StarCreated.To(rig.Host));
            await rig.Round.HandleStarReportedAsync(rig.Host,
                new(slot, change) { NamedReporterSlot = slot }, default);
            Assert.Equal(expected, room.Scoring.Stars(slot));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>An unaudited layout's original reporter byte cannot redirect its authenticated sender's stars.</summary>
    [Fact]
    public async Task UnnamedStarReportRetainsLegacySenderAttribution()
    {
        var rig = StarRig(team: true);
        try
        {
            await rig.StartAsync();
            var room = rig.Registry.Find(rig.RoomId)!;
            var hostSlot = room.FindByGame(rig.Host.Id)!.Slot;
            var guestSlot = room.FindByGame(rig.Guest.Id)!.Slot;
            await rig.Round.HandleStarReportedAsync(rig.Guest, new(hostSlot, RoundStarChange.BigGained), default);
            Assert.Equal(0, room.Scoring.Stars(hostSlot));
            Assert.Equal(10, room.Scoring.Stars(guestSlot));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Another session instance with the same identifier cannot submit a live seat's named star report.</summary>
    [Fact]
    public async Task ReplacedGameSessionCannotReportStarsForCurrentSeat()
    {
        var rig = StarRig(team: true);
        try
        {
            await rig.StartAsync();
            var room = rig.Registry.Find(rig.RoomId)!;
            var slot = room.FindByGame(rig.Host.Id)!.Slot;
            await rig.Round.HandleStarReportedAsync(new RecordingSession(rig.Host.Id),
                new(slot, RoundStarChange.BigGained) { NamedReporterSlot = slot }, default);
            Assert.Equal(0, room.Scoring.Stars(slot));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }
}
