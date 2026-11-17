using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Clans;
using BubbleFighter.Fantasy.Protocol;
using Xunit;

namespace BubbleFighter.Fantasy.Clan.Tests;

/// <summary>The disband on the mounted rows: scheduled by the master, cancellable, then final through the row's sweep.</summary>
public sealed partial class ClanMountTests
{
    /// <summary>
    /// The master's request stores a date seven days ahead, answers 1 and tells the members; the home then states
    /// that date; a second request is refused. Cancelling clears it; a second cancel is refused. A plain member's
    /// request is refused. Once the date has passed, the sweep deletes the clan with its rows and tells the
    /// connected ex-members with an empty home.
    /// </summary>
    [Fact]
    public async Task DisbandIsScheduledCancellableAndThenFinal()
    {
        await using var rig = await Rig.StartAsync();
        var directory = rig.Capture.Clans;
        var master = await LoginClanAsync(rig);
        await rig.DispatchAsync(ClanProtocol, master, rig.ClanTable.C2S("ClanCreate"), CreateBody("Wolves", "hi", Card30, 30));
        var id = (await directory.FindByNameAsync("Wolves", CancellationToken.None))!.ClanId;
        Assert.True(await directory.AddMemberAsync(id, 43, "Aide", 1, ClanRole.Applicant, CancellationToken.None));
        Assert.True(await directory.SeatAsync(id, 43, ClanRole.Member, CancellationToken.None));
        var aide = await LoginOtherClanAsync(rig, 43, "Aide", 20);

        var before = DateTime.UtcNow;
        Assert.Equal([(rig.ClanTable.S2C("ClanDisbandResult"), Word(1) + Word(1)), (rig.ClanTable.S2C("ClanDisbandChanged"), Word(1) + Word(1))],
            await SendAsync(rig, master, "ClanDisband", Word(ClanDisbandRequested.Request)));
        Assert.Equal([(rig.ClanTable.S2C("ClanDisbandChanged"), Word(1) + Word(1))], aide.Pins());
        var date = (await directory.FindAsync(id, CancellationToken.None))!.DisbandAt!.Value;
        Assert.InRange(date, before.AddDays(7).AddSeconds(-5), DateTime.UtcNow.AddDays(7).AddSeconds(5));
        var home = Assert.Single(await SendAsync(rig, master, "ClanHomeRequest", ""));
        Assert.EndsWith(Convert.ToHexString(BitConverter.GetBytes(ClanDirectoryOle(date))), home.Hex);
        Assert.Equal([(rig.ClanTable.S2C("ClanDisbandResult"), Word(0) + Word(1))], await SendAsync(rig, master, "ClanDisband", Word(ClanDisbandRequested.Request)));

        aide.Frames.Clear();
        Assert.Equal([(rig.ClanTable.S2C("ClanDisbandResult"), Word(1) + Word(2)), (rig.ClanTable.S2C("ClanDisbandChanged"), Word(1) + Word(2))],
            await SendAsync(rig, master, "ClanDisband", Word(ClanDisbandRequested.Cancel)));
        Assert.Null((await directory.FindAsync(id, CancellationToken.None))!.DisbandAt);
        Assert.Equal([(rig.ClanTable.S2C("ClanDisbandResult"), Word(0) + Word(2))], await SendAsync(rig, master, "ClanDisband", Word(ClanDisbandRequested.Cancel)));
        Assert.Equal([(rig.ClanTable.S2C("ClanDisbandResult"), Word(0) + Word(1))], await SendAsync(rig, aide, "ClanDisband", Word(ClanDisbandRequested.Request)));

        master.Frames.Clear();
        aide.Frames.Clear();
        Assert.True(await directory.UpdateAsync((await directory.FindAsync(id, CancellationToken.None))! with { DisbandAt = DateTime.UtcNow.AddMinutes(-1) },
            CancellationToken.None));
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (await directory.FindAsync(id, CancellationToken.None) is not null && DateTime.UtcNow < deadline)
            await Task.Delay(100);
        Assert.Null(await directory.FindAsync(id, CancellationToken.None));
        Assert.Null(await directory.FindMemberAsync(UserNum, CancellationToken.None));
        var empty = (rig.ClanTable.S2C("ClanHome"), "00" + Word(0) + Word(0) + "0000" + NoDisband);
        await WaitForFramesAsync(master, aide);
        Assert.Equal([empty], master.Pins());
        Assert.Equal([empty], aide.Pins());
    }

    // The client's 8-byte date (days since 1899-12-30), as the home writes it.
    private static double ClanDirectoryOle(DateTime utc) => (utc - new DateTime(1899, 12, 30, 0, 0, 0, DateTimeKind.Utc)).TotalDays;

    // The sweep deletes the clan and then sends the homes, so a frame can trail the deletion by a moment.
    private static async Task WaitForFramesAsync(params RecordingSession[] sessions)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (sessions.Any(session => session.Frames.Count == 0) && DateTime.UtcNow < deadline)
            await Task.Delay(50);
    }
}
