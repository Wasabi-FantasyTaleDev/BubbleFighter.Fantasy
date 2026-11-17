using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Protocol;
using BubbleFighter.Fantasy.Contracts.Clans;
using Xunit;

namespace BubbleFighter.Fantasy.Clan.Tests;

/// <summary>
/// Applying from the search list on the mounted rows: each refusal carries its own result and the role the player
/// keeps, an application stores an applicant row the home then states, a withdrawal removes only that row, a join
/// request seats the player at once in a clan that recruits openly, and the applicant list is the master's.
/// </summary>
public sealed partial class ClanMountTests
{
    /// <summary>
    /// A closed, a disbanding and a missing clan refuse with -5, -3 and the general failure; a join request to a
    /// clan that does not recruit openly is -6. Applying stores role 1 and the clan in the home; a second application
    /// is refused with the applicant role kept; withdrawing from another clan fails and from the right one clears
    /// the row. Joining an open clan seats the player as a member, who is then refused with -2.
    /// </summary>
    [Fact]
    public async Task ApplyCancelAndApplicantList()
    {
        await using var rig = await Rig.StartAsync();
        var directory = rig.Capture.Clans;
        var wolves = (await directory.CreateAsync("Wolves", "", 30, 101, "Wolfgang", 1, CancellationToken.None))!;
        var open = (await directory.CreateAsync("Open", "", 30, 102, "Olga", 1, CancellationToken.None))!;
        var closed = (await directory.CreateAsync("Closed", "", 30, 103, "Carl", 1, CancellationToken.None))!;
        var doomed = (await directory.CreateAsync("Doomed", "", 30, 104, "Dora", 1, CancellationToken.None))!;
        Assert.True(await directory.UpdateAsync(open with { Recruitment = ClanApplicationHandlers.OpenRecruitment }, CancellationToken.None));
        Assert.True(await directory.UpdateAsync(closed with { Recruitment = 0 }, CancellationToken.None));
        Assert.True(await directory.UpdateAsync(doomed with { DisbandAt = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc) }, CancellationToken.None));
        var clan = await LoginClanAsync(rig);

        Assert.Equal(Refused(ClanApplyAnswered.NotRecruiting, 0), await ApplyAsync(rig, clan, "ClanApply", closed));
        Assert.Equal(Refused(ClanApplyAnswered.Disbanding, 0), await ApplyAsync(rig, clan, "ClanApply", doomed));
        Assert.Equal(Refused(ClanApplyAnswered.NotOpenRecruiting, 0), await ApplyAsync(rig, clan, "ClanJoin", wolves));
        Assert.Equal(Refused(ClanApplyAnswered.Failed, 0), await ApplyAsync(rig, clan, "ClanApply", wolves with { ClanId = 999 }));
        Assert.Null(await directory.FindMemberAsync(UserNum, CancellationToken.None));

        Assert.Equal(Word(1) + Word(1) + Word((int)wolves.ClanId) + Wc("Wolves"), await ApplyAsync(rig, clan, "ClanApply", wolves));
        Assert.Equal(ClanRole.Applicant, (await directory.FindMemberAsync(UserNum, CancellationToken.None))!.Role);
        Assert.Equal(Refused(ClanApplyAnswered.Failed, ClanRole.Applicant), await ApplyAsync(rig, clan, "ClanApply", open));
        clan.Frames.Clear();
        await rig.DispatchAsync(ClanProtocol, clan, rig.ClanTable.C2S("ClanHomeRequest"), []);
        Assert.Equal([(rig.ClanTable.S2C("ClanHome"), "00" + Word((int)wolves.ClanId) + Word(1) + Wc("Wolves") + NoDisband)], clan.Pins());

        Assert.Equal(Word(0) + Word((int)open.ClanId), await CancelAsync(rig, clan, open.ClanId));
        Assert.NotNull(await directory.FindMemberAsync(UserNum, CancellationToken.None));
        Assert.Equal(Word(1) + Word((int)wolves.ClanId), await CancelAsync(rig, clan, wolves.ClanId));
        Assert.Null(await directory.FindMemberAsync(UserNum, CancellationToken.None));

        Assert.Equal(Word(1) + Word(ClanRole.Member) + Word((int)open.ClanId) + Wc("Open"), await ApplyAsync(rig, clan, "ClanJoin", open));
        Assert.Equal((open.ClanId, ClanRole.Member), (await directory.FindMemberAsync(UserNum, CancellationToken.None)) is { } seated
            ? (seated.ClanId, seated.Role) : (0, 0));
        Assert.Equal(2, (await directory.FindAsync(open.ClanId, CancellationToken.None))!.MemberCount);
        Assert.Equal(Refused(ClanApplyAnswered.AlreadyInClan, ClanRole.Member), await ApplyAsync(rig, clan, "ClanApply", wolves));
        // A seated member who is not the master or the deputy is sent an empty applicant list.
        Assert.Equal(Word(0), await ApplicantsAsync(rig, clan));
    }

    /// <summary>The master lists the applicants of the own clan only, each with the applicant role and zeroes for the words no reader uses.</summary>
    [Fact]
    public async Task ApplicantListIsTheMastersOwnClan()
    {
        await using var rig = await Rig.StartAsync();
        var directory = rig.Capture.Clans;
        var clan = await LoginClanAsync(rig);
        await rig.DispatchAsync(ClanProtocol, clan, rig.ClanTable.C2S("ClanCreate"), CreateBody("Wolves", "hi", Card30, 30));
        var own = (await directory.FindByNameAsync("Wolves", CancellationToken.None))!;
        var other = (await directory.CreateAsync("Bears", "", 30, 200, "Bruno", 1, CancellationToken.None))!;
        Assert.True(await directory.AddMemberAsync(own.ClanId, 43, "Aide", 3, ClanRole.Applicant, CancellationToken.None));
        Assert.True(await directory.AddMemberAsync(other.ClanId, 44, "Elsewhere", 3, ClanRole.Applicant, CancellationToken.None));

        Assert.Equal(Word(1) + Word(43) + Wc("Aide") + Word(0) + Word(ClanRole.Applicant) + new string('0', 16) + new string('0', 32),
            await ApplicantsAsync(rig, clan));
    }

    private static string Refused(int result, int role) => Word(result) + Word(role) + Word(0) + Wc("");

    private static async Task<string> ApplyAsync(Rig rig, RecordingSession clan, string request, ClanRecord target)
    {
        clan.Frames.Clear();
        var body = Word((int)target.ClanId) + Wc(target.Name) + (request == "ClanJoin" ? Wc("clanner") : "");
        await rig.DispatchAsync(ClanProtocol, clan, rig.ClanTable.C2S(request), Convert.FromHexString(body));
        var frame = Assert.Single(clan.Frames);
        Assert.Equal(rig.ClanTable.S2C("ClanApplyResult"), frame.Opcode);
        return Convert.ToHexString(frame.Payload);
    }

    private static async Task<string> CancelAsync(Rig rig, RecordingSession clan, long clanId)
    {
        clan.Frames.Clear();
        await rig.DispatchAsync(ClanProtocol, clan, rig.ClanTable.C2S("ClanCancelApplication"), Convert.FromHexString(Word((int)clanId)));
        var frame = Assert.Single(clan.Frames);
        Assert.Equal(rig.ClanTable.S2C("ClanCancelApplicationResult"), frame.Opcode);
        return Convert.ToHexString(frame.Payload);
    }

    private static async Task<string> ApplicantsAsync(Rig rig, RecordingSession clan)
    {
        clan.Frames.Clear();
        await rig.DispatchAsync(ClanProtocol, clan, rig.ClanTable.C2S("ClanApplicantsRequest"), []);
        var frame = Assert.Single(clan.Frames);
        Assert.Equal(rig.ClanTable.S2C("ClanApplicants"), frame.Opcode);
        return Convert.ToHexString(frame.Payload);
    }
}
