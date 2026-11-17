using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Clans;
using BubbleFighter.Fantasy.Protocol;
using Xunit;

namespace BubbleFighter.Fantasy.Clan.Tests;

/// <summary>The master transfer on the mounted rows, with the master and the member each on a live clan connection.</summary>
public sealed partial class ClanMountTests
{
    /// <summary>
    /// An offer to a member who is not connected is refused. An offer reaches the member with the master's name
    /// and id; a decline tells the master and changes nothing. A second offer, accepted, hands the clan over at once
    /// (both roles and the master id), answers the member 1 and the master "accepted"; the master's confirmation then
    /// tells both of the new master. Answering again finds no offer.
    /// </summary>
    [Fact]
    public async Task MasterTransferNeedsTheTargetsAcceptance()
    {
        await using var rig = await Rig.StartAsync();
        var directory = rig.Capture.Clans;
        var master = await LoginClanAsync(rig);
        await rig.DispatchAsync(ClanProtocol, master, rig.ClanTable.C2S("ClanCreate"), CreateBody("Wolves", "hi", Card30, 30));
        var id = (await directory.FindByNameAsync("Wolves", CancellationToken.None))!.ClanId;
        foreach (var (user, name) in new[] { (43L, "Aide"), (44L, "Away") })
        {
            Assert.True(await directory.AddMemberAsync(id, user, name, 1, ClanRole.Applicant, CancellationToken.None));
            Assert.True(await directory.SeatAsync(id, user, ClanRole.Member, CancellationToken.None));
        }
        var aide = await LoginOtherClanAsync(rig, 43, "Aide", 20);

        Assert.Equal([(rig.ClanTable.S2C("ClanTransferRequestResult"), "00" + Word(2))], await SendAsync(rig, master, "ClanTransferRequest", Word(44)));
        Assert.Equal([(rig.ClanTable.S2C("ClanTransferRequestResult"), "01" + Word(1))], await SendAsync(rig, master, "ClanTransferRequest", Word(43)));
        Assert.Equal([(rig.ClanTable.S2C("ClanTransferOffer"), Wc("clanner") + Word((int)UserNum))], aide.Pins());

        master.Frames.Clear();
        Assert.Empty(await SendAsync(rig, aide, "ClanTransferAnswer", "00" + Word((int)UserNum)));
        Assert.Equal([(rig.ClanTable.S2C("ClanTransferAnswered"), "00" + Word(43) + Wc("Aide"))], master.Pins());
        Assert.Equal(UserNum, (await directory.FindAsync(id, CancellationToken.None))!.MasterUserNum);

        await SendAsync(rig, master, "ClanTransferRequest", Word(43));
        master.Frames.Clear();
        Assert.Equal([(rig.ClanTable.S2C("ClanTransferAnswerResult"), "01")], await SendAsync(rig, aide, "ClanTransferAnswer", "01" + Word((int)UserNum)));
        Assert.Equal([(rig.ClanTable.S2C("ClanTransferAnswered"), "01" + Word(43) + Wc("Aide"))], master.Pins());
        Assert.Equal(43, (await directory.FindAsync(id, CancellationToken.None))!.MasterUserNum);
        Assert.Equal((ClanRole.Member, ClanRole.Master), ((await directory.FindMemberAsync(UserNum, CancellationToken.None))!.Role,
            (await directory.FindMemberAsync(43, CancellationToken.None))!.Role));

        aide.Frames.Clear();
        var changed = (rig.ClanTable.S2C("ClanMasterChanged"), Word(0) + Word((int)UserNum) + Wc("clanner") + Word(43) + Wc("Aide"));
        Assert.Equal([changed], await SendAsync(rig, master, "ClanTransferConfirm", Word(43) + Wc("Aide")));
        Assert.Equal([changed], aide.Pins());
        Assert.Equal([(rig.ClanTable.S2C("ClanTransferAnswerResult"), "00")], await SendAsync(rig, aide, "ClanTransferAnswer", "01" + Word((int)UserNum)));
        Assert.Empty(await SendAsync(rig, master, "ClanTransferConfirm", Word(43) + Wc("Aide")));
    }

    private static async Task<(ushort Opcode, string Hex)[]> SendAsync(Rig rig, RecordingSession clan, string request, string bodyHex)
    {
        clan.Frames.Clear();
        await rig.DispatchAsync(ClanProtocol, clan, rig.ClanTable.C2S(request), Convert.FromHexString(bodyHex));
        return clan.Pins();
    }
}
