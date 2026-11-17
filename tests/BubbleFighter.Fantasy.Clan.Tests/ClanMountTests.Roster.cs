using System.Buffers.Binary;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Protocol;
using Xunit;

namespace BubbleFighter.Fantasy.Clan.Tests;

/// <summary>
/// The member lists on the mounted rows: a seated member's lists carry its own last report and role, a player
/// without a seat gets empty lists, and the report itself is answered with nothing.
/// </summary>
public sealed partial class ClanMountTests
{
    /// <summary>
    /// The founder reports two words, then asks for both lists: the short list echoes the report in record order
    /// (second word, first word) and the master role; the detailed list carries the first word, the role and zeroes.
    /// </summary>
    [Fact]
    public async Task RostersListTheClanMembersOfTheCaller()
    {
        await using var rig = await Rig.StartAsync();
        var clan = await LoginClanAsync(rig);
        await rig.DispatchAsync(ClanProtocol, clan, rig.ClanTable.C2S("ClanCreate"), CreateBody("Wolves", "hi", 100819, 30));
        await rig.DispatchAsync(ClanProtocol, clan, rig.ClanTable.C2S("ClanMemberReport"), Convert.FromHexString(Word(5) + Word(3)));
        clan.Frames.Clear();

        await rig.DispatchAsync(ClanProtocol, clan, rig.ClanTable.C2S("ClanRosterRequest"), []);
        await rig.DispatchAsync(ClanProtocol, clan, rig.ClanTable.C2S("ClanMemberDetailsRequest"), Convert.FromHexString(Word(1)));
        Assert.Equal([
            (rig.ClanTable.S2C("ClanRoster"), Word(1) + Word((int)UserNum) + Wc("clanner") + Word(3) + Word(5) + Word(101)),
            (rig.ClanTable.S2C("ClanMemberDetails"), Word(0) + Word(1) + Word((int)UserNum) + Wc("clanner") + Word(5) + Word(101) + new string('0', 96)),
        ], clan.Pins());
    }

    /// <summary>A player without a seat is answered empty lists, and a report draws no answer.</summary>
    [Fact]
    public async Task RostersOfAPlayerWithoutASeatAreEmpty()
    {
        await using var rig = await Rig.StartAsync();
        var clan = await LoginClanAsync(rig);
        await rig.DispatchAsync(ClanProtocol, clan, rig.ClanTable.C2S("ClanMemberReport"), Convert.FromHexString(Word(5) + Word(3)));
        Assert.Empty(clan.Frames);
        await rig.DispatchAsync(ClanProtocol, clan, rig.ClanTable.C2S("ClanRosterRequest"), []);
        await rig.DispatchAsync(ClanProtocol, clan, rig.ClanTable.C2S("ClanMemberDetailsRequest"), Convert.FromHexString(Word(1)));
        Assert.Equal([(rig.ClanTable.S2C("ClanRoster"), Word(0)), (rig.ClanTable.S2C("ClanMemberDetails"), Word(0) + Word(0))], clan.Pins());
    }

    private static string Word(int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return Convert.ToHexString(bytes);
    }
}
