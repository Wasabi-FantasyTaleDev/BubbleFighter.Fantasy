using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Clans;
using BubbleFighter.Fantasy.Protocol;
using Xunit;

namespace BubbleFighter.Fantasy.Clan.Tests;

/// <summary>
/// The master's edits on the mounted rows. The connected master is the only member connected, so it receives what
/// the members are told (except the introduction notice, which goes to the other members only).
/// </summary>
public sealed partial class ClanMountTests
{
    /// <summary>
    /// The introduction edit answers its result to the editor alone; the notice edit answers its result and then the
    /// notice to every member; recruitment accepts only the client's three words for the own clan; the mark is
    /// stored and told. A plain member's edits change nothing and draw no frame.
    /// </summary>
    [Fact]
    public async Task MasterEditsTextsRecruitmentAndMark()
    {
        await using var rig = await Rig.StartAsync();
        var directory = rig.Capture.Clans;
        var clan = await LoginClanAsync(rig);
        await rig.DispatchAsync(ClanProtocol, clan, rig.ClanTable.C2S("ClanCreate"), CreateBody("Wolves", "hi", Card30, 30));
        var id = (int)(await directory.FindByNameAsync("Wolves", CancellationToken.None))!.ClanId;

        Assert.Equal([(rig.ClanTable.S2C("ClanIntroductionEditResult"), Word(1) + Wc("We hunt"))],
            await EditAsync(rig, clan, "ClanIntroductionEdit", Wc("We hunt")));
        Assert.Equal([(rig.ClanTable.S2C("ClanNoticeEditResult"), Word(1)), (rig.ClanTable.S2C("ClanNoticeChanged"), Wc("Meet at 9"))],
            await EditAsync(rig, clan, "ClanNoticeEdit", Wc("Meet at 9")));
        Assert.Equal([(rig.ClanTable.S2C("ClanRecruitmentEditResult"), Word(id) + Word(1) + Word(ClanRecruitment.Open))],
            await EditAsync(rig, clan, "ClanRecruitmentEdit", Word(id) + Word(ClanRecruitment.Open)));
        Assert.Equal([(rig.ClanTable.S2C("ClanRecruitmentEditResult"), Word(id) + Word(0) + Word(ClanRecruitment.Open))],
            await EditAsync(rig, clan, "ClanRecruitmentEdit", Word(id) + Word(3)));
        Assert.Equal([(rig.ClanTable.S2C("ClanRecruitmentEditResult"), Word(id) + Word(0) + Word(ClanRecruitment.Open))],
            await EditAsync(rig, clan, "ClanRecruitmentEdit", Word(id + 1) + Word(ClanRecruitment.Closed)));
        Assert.Equal([(rig.ClanTable.S2C("ClanMarkChanged"), Word(5))], await EditAsync(rig, clan, "ClanMarkEdit", Word(id) + Word(5)));
        Assert.Empty(await EditAsync(rig, clan, "ClanMarkEdit", Word(id + 1) + Word(6)));

        var stored = (await directory.FindAsync(id, CancellationToken.None))!;
        Assert.Equal(("We hunt", "Meet at 9", ClanRecruitment.Open, 5), (stored.Introduction, stored.Notice, stored.Recruitment, stored.Mark));

        Assert.True(await directory.TransferMasterAsync(id, UserNum, UserNum, CancellationToken.None) is false);
        Assert.True(await directory.AddMemberAsync(id, 43, "Aide", 1, ClanRole.Applicant, CancellationToken.None));
        Assert.True(await directory.SeatAsync(id, 43, ClanRole.Member, CancellationToken.None));
        Assert.True(await directory.TransferMasterAsync(id, UserNum, 43, CancellationToken.None));
        Assert.Empty(await EditAsync(rig, clan, "ClanIntroductionEdit", Wc("Mine now")));
        Assert.Empty(await EditAsync(rig, clan, "ClanRecruitmentEdit", Word(id) + Word(ClanRecruitment.Closed)));
        Assert.Equal("We hunt", (await directory.FindAsync(id, CancellationToken.None))!.Introduction);
    }

    private static async Task<(ushort Opcode, string Hex)[]> EditAsync(Rig rig, RecordingSession clan, string request, string bodyHex)
    {
        clan.Frames.Clear();
        await rig.DispatchAsync(ClanProtocol, clan, rig.ClanTable.C2S(request), Convert.FromHexString(bodyHex));
        return clan.Pins();
    }
}
