using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Clans;
using BubbleFighter.Fantasy.Protocol;
using Xunit;

namespace BubbleFighter.Fantasy.Clan.Tests;

/// <summary>Clan chat and whispers on the mounted rows, with two members each on a live clan connection.</summary>
public sealed partial class ClanMountTests
{
    /// <summary>
    /// A line reaches both connected members under the sender's own name, whatever name the client wrote. A whisper
    /// to the other member reaches it and answers the sender "delivered"; a whisper to an unknown name, to a member
    /// who is not connected, or from a player without a seat answers "not found".
    /// </summary>
    [Fact]
    public async Task ChatAndWhisperReachConnectedMembers()
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

        var line = (rig.ClanTable.S2C("ClanChatMessage"), Word(0) + Wc("clanner") + Wc("hello"));
        Assert.Equal([line], await SendAsync(rig, master, "ClanChat", Word(2) + Wc("Impostor") + Wc("hello")));
        Assert.Equal([line], aide.Pins());
        Assert.Empty(await SendAsync(rig, master, "ClanChat", Word(2) + Wc("clanner") + Wc("")));

        aide.Frames.Clear();
        Assert.Equal([(rig.ClanTable.S2C("ClanWhisperResult"), Word(1) + Word(1) + Wc("clanner") + Wc("Aide") + Wc("psst"))],
            await SendAsync(rig, master, "ClanWhisper", Word(1) + Wc("aide") + Wc("psst") + "00"));
        Assert.Equal([(rig.ClanTable.S2C("ClanWhisperMessage"), Word(1) + Wc("clanner") + Wc("Aide") + Wc("psst") + "00")], aide.Pins());
        foreach (var target in new[] { "Nobody", "Away" })
            Assert.Equal([(rig.ClanTable.S2C("ClanWhisperResult"), Word(2) + Word(1) + Wc("clanner") + Wc(target) + Wc("psst"))],
                await SendAsync(rig, master, "ClanWhisper", Word(1) + Wc(target) + Wc("psst") + "00"));

        Assert.True(await directory.RemoveMemberAsync(id, 43, CancellationToken.None));
        Assert.Equal([(rig.ClanTable.S2C("ClanWhisperResult"), Word(2) + Word(1) + Wc("Aide") + Wc("clanner") + Wc("hi"))],
            await SendAsync(rig, aide, "ClanWhisper", Word(1) + Wc("clanner") + Wc("hi") + "00"));
        Assert.Empty(await SendAsync(rig, aide, "ClanChat", Word(2) + Wc("Aide") + Wc("hi")));
    }
}
