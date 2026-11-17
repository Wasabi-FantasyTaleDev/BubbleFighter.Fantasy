using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Clans;
using BubbleFighter.Fantasy.Protocol;
using Xunit;

namespace BubbleFighter.Fantasy.Clan.Tests;

/// <summary>
/// Member changes and leaving on the mounted rows. The connected player is the actor; the rows it changes are
/// stored ones, and every notice to seated members reaches the actor itself, which is how the notices are seen.
/// </summary>
public sealed partial class ClanMountTests
{
    /// <summary>
    /// The master approves an applicant (notice with the joined flag, result 1), is refused a second approval while
    /// the clan is full (result 2), turns an applicant away (-10), is told an applicant who already withdrew has done
    /// so (-50), promotes a member to deputy, is refused a second deputy (-102), demotes the deputy, removes a member
    /// (-30, notice first), and can neither change the own row nor leave.
    /// </summary>
    [Fact]
    public async Task MemberChangesAndLeaving()
    {
        await using var rig = await Rig.StartAsync();
        var directory = rig.Capture.Clans;
        var clan = await LoginClanAsync(rig);
        await rig.DispatchAsync(ClanProtocol, clan, rig.ClanTable.C2S("ClanCreate"), CreateBody("Wolves", "hi", Card30, 30));
        var wolves = (await directory.FindByNameAsync("Wolves", CancellationToken.None))!;
        foreach (var (user, name) in new[] { (43L, "Aide"), (44L, "Bob"), (45L, "Cleo") })
            Assert.True(await directory.AddMemberAsync(wolves.ClanId, user, name, 1, ClanRole.Applicant, CancellationToken.None));

        Assert.Equal([Notice("Wolves", 43, "Aide", ClanRole.Member, 1), Result(1, "Aide", ClanRole.Member, 1)],
            await ChangeAsync(rig, clan, 43, "Aide", ClanRole.Applicant, ClanRole.Member));
        Assert.True(await directory.UpdateAsync((await directory.FindAsync(wolves.ClanId, CancellationToken.None))! with { Capacity = 2 }, CancellationToken.None));
        Assert.Equal([Result(ClanMemberChangeAnswered.Full, "Bob", ClanRole.Member, 0)],
            await ChangeAsync(rig, clan, 44, "Bob", ClanRole.Applicant, ClanRole.Member));
        Assert.Equal([Result(1, "Bob", ClanMemberEvent.Rejected, 0)], await ChangeAsync(rig, clan, 44, "Bob", ClanRole.Applicant, ClanMemberEvent.Rejected));
        Assert.Null(await directory.FindMemberAsync(44, CancellationToken.None));
        Assert.Equal([Result(1, "Bob", ClanMemberEvent.Withdrawn, 0)], await ChangeAsync(rig, clan, 44, "Bob", ClanRole.Applicant, ClanRole.Member));

        Assert.Equal([Notice("Wolves", 43, "Aide", ClanRole.Deputy, 0), Result(1, "Aide", ClanRole.Deputy, 0)],
            await ChangeAsync(rig, clan, 43, "Aide", ClanRole.Member, ClanRole.Deputy));
        Assert.True(await directory.UpdateAsync((await directory.FindAsync(wolves.ClanId, CancellationToken.None))! with { Capacity = 30 }, CancellationToken.None));
        Assert.True(await directory.SeatAsync(wolves.ClanId, 45, ClanRole.Member, CancellationToken.None));
        Assert.Equal([Result(ClanMemberChangeAnswered.DeputyTaken, "Cleo", ClanRole.Deputy, 0)],
            await ChangeAsync(rig, clan, 45, "Cleo", ClanRole.Member, ClanRole.Deputy));
        Assert.Equal([Notice("Wolves", 43, "Aide", ClanRole.Member, 0), Result(1, "Aide", ClanRole.Member, 0)],
            await ChangeAsync(rig, clan, 43, "Aide", ClanRole.Deputy, ClanRole.Member));
        Assert.Equal(ClanRole.Member, (await directory.FindMemberAsync(43, CancellationToken.None))!.Role);

        Assert.Equal([Notice("Wolves", 45, "Cleo", ClanMemberEvent.Removed, 0), Result(1, "Cleo", ClanMemberEvent.Removed, 0)],
            await ChangeAsync(rig, clan, 45, "Cleo", ClanRole.Member, ClanMemberEvent.Removed));
        Assert.Null(await directory.FindMemberAsync(45, CancellationToken.None));

        Assert.Equal([Result(0, "clanner", ClanMemberEvent.Removed, 0)], await ChangeAsync(rig, clan, (int)UserNum, "clanner", ClanRole.Master, ClanMemberEvent.Removed));
        clan.Frames.Clear();
        await rig.DispatchAsync(ClanProtocol, clan, rig.ClanTable.C2S("ClanLeave"), []);
        Assert.Empty(clan.Frames);
        Assert.Equal(ClanRole.Master, (await directory.FindMemberAsync(UserNum, CancellationToken.None))!.Role);
    }

    /// <summary>
    /// A deputy removes a plain member but neither the master nor a promotion; a deputy who leaves is told with the
    /// leave notice (event -20 in the last word) and loses the row.
    /// </summary>
    [Fact]
    public async Task DeputyRulesAndLeaving()
    {
        await using var rig = await Rig.StartAsync();
        var directory = rig.Capture.Clans;
        var bears = (await directory.CreateAsync("Bears", "", 30, 101, "Bruno", 1, CancellationToken.None))!;
        foreach (var (user, name, role) in new[] { (UserNum, "clanner", ClanRole.Deputy), (43L, "Aide", ClanRole.Member), (44L, "Bob", ClanRole.Member) })
        {
            Assert.True(await directory.AddMemberAsync(bears.ClanId, user, name, 1, ClanRole.Applicant, CancellationToken.None));
            Assert.True(await directory.SeatAsync(bears.ClanId, user, role, CancellationToken.None));
        }
        var clan = await LoginClanAsync(rig);

        Assert.Equal([Result(0, "Bruno", ClanMemberEvent.Removed, 0)], await ChangeAsync(rig, clan, 101, "Bruno", ClanRole.Master, ClanMemberEvent.Removed));
        Assert.Equal([Result(0, "Bob", ClanRole.Deputy, 0)], await ChangeAsync(rig, clan, 44, "Bob", ClanRole.Member, ClanRole.Deputy));
        Assert.Equal([Notice("Bears", 43, "Aide", ClanMemberEvent.Removed, 0), Result(1, "Aide", ClanMemberEvent.Removed, 0)],
            await ChangeAsync(rig, clan, 43, "Aide", ClanRole.Member, ClanMemberEvent.Removed));

        clan.Frames.Clear();
        await rig.DispatchAsync(ClanProtocol, clan, rig.ClanTable.C2S("ClanLeave"), []);
        Assert.Equal([(rig.ClanTable.S2C("ClanMemberLeft"), Wc("Bears") + Word((int)UserNum) + Wc("clanner") + Word(0) + Word(ClanMemberEvent.Left))],
            clan.Pins());
        Assert.Null(await directory.FindMemberAsync(UserNum, CancellationToken.None));
        Assert.Equal(2, (await directory.FindAsync(bears.ClanId, CancellationToken.None))!.MemberCount);
    }

    private static string Notice(string clanName, long user, string nickname, int role, int joined) =>
        "N" + Wc(clanName) + Word((int)user) + Wc(nickname) + Word(role) + Word(joined);

    private static string Result(int result, string nickname, int role, int joined) =>
        "R" + Word(result) + Wc(nickname) + Word(role) + Word(joined);

    // The frames a change produced, notices marked N and the result R, in send order.
    private static async Task<string[]> ChangeAsync(Rig rig, RecordingSession clan, int user, string nickname, int oldRole, int newRole)
    {
        clan.Frames.Clear();
        await rig.DispatchAsync(ClanProtocol, clan, rig.ClanTable.C2S("ClanMemberChange"),
            Convert.FromHexString(Word(user) + Wc(nickname) + Word(oldRole) + Word(newRole)));
        var notice = rig.ClanTable.S2C("ClanMemberChanged");
        var result = rig.ClanTable.S2C("ClanMemberChangeResult");
        Assert.All(clan.Frames, frame => Assert.Contains(frame.Opcode, new[] { notice, result }));
        return clan.Frames.Select(frame => (frame.Opcode == notice ? "N" : "R") + Convert.ToHexString(frame.Payload)).ToArray();
    }
}
