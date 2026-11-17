using BubbleFighter.Fantasy.Contracts.Couples;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins CN324's older couple record without borrowing the later presence word at the losses offset.</summary>
public sealed class CoupleCn324ProfileTests
{
    /// <summary>The native date, ring and presence bytes preserve both the partner identity and the loss counter.</summary>
    [Theory]
    [InlineData(false, "00000000")]
    [InlineData(true, "01000000")]
    public void RelationshipDoesNotOverwriteTheLossCounter(bool online, string flag)
    {
        var fields = new Dictionary<string, object?>
        {
            ["user_number"] = 88, ["primary_losses"] = 29,
        };
        var created = new DateTimeOffset(new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Local));
        CoupleProfileWire.AddToFighter(TestBuilds.Packets, "cn-324", fields, new(created, 100604, online));
        Assert.False(fields.ContainsKey("couple_present"));
        var body = PacketCodec.Write(TestBuilds.Packets, "cn-324", "FighterInfo", fields);
        // cn-324 sub_6FBD60 reads the date at object+32 after 28 serialized bytes with an empty name.
        Assert.Equal(216, body.Length);
        Assert.Equal("58000000", Convert.ToHexString(body.AsSpan(0, 4)));
        Assert.Equal("0000000000000040FC880100" + flag, Convert.ToHexString(body.AsSpan(28, 16)));
        Assert.Equal("1D000000", Convert.ToHexString(body.AsSpan(52, 4)));
    }

    /// <summary>An own-stat refresh retains the old build's couple fields without changing its default identity.</summary>
    [Fact]
    public void OwnStatRetainsTheRelationshipWithoutInventingARecipientIdentity()
    {
        var created = new DateTimeOffset(new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Local));
        var body = UserStatWire.Write(TestBuilds.Packets, "cn-324", "Ann", 7, 25, 3,
            userNum: 42, couple: new CoupleProfileState(created, 0, false));
        var fighter = Assert.IsType<Dictionary<string, object?>>(
            PacketCodec.Read(TestBuilds.Packets, "cn-324", "UserStat", body)["fighter"]);
        Assert.Equal(0, Assert.IsType<int>(fighter["user_number"]));
        Assert.Equal("Ann", fighter["nickname"]);
        Assert.Equal(BitConverter.DoubleToInt64Bits(2d), Assert.IsType<long>(fighter["couple_since"]));
        Assert.Equal(0, Assert.IsType<int>(fighter["couple_ring_item"]));
        Assert.False(Assert.IsType<bool>(fighter["couple_partner_online"]));
        Assert.Equal(0, Assert.IsType<int>(fighter["primary_losses"]));
    }
}
