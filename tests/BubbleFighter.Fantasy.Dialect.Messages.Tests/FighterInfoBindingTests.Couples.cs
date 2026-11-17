using BubbleFighter.Fantasy.Contracts.Couples;
using BubbleFighter.Fantasy.Contracts.Players;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

public sealed partial class FighterInfoBindingTests
{
    /// <summary>The partner block retains the actual offline identity, date and ring instead of empty defaults or the requester.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    [InlineData("cn-324")]
    [InlineData("tw-410")]
    [InlineData("cn-404")]
    [InlineData("cn-406")]
    [InlineData("cn-408")]
    [InlineData("cn-705")]
    [InlineData("cn-706")]
    [InlineData("cn-717")]
    [InlineData("id-400")]
    [InlineData("id-1809")]
    public void InspectionPublishesTheTargetsPersistentPartner(string build)
    {
        var binding = Binding(build, out _);
        var created = new DateTimeOffset(new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Local));
        var reply = new FighterInfoReplied(new(77, "target", "Target", 5), new(300007, [300007]))
        {
            CouplePartner = new(new(88, "Partner"), new(created, 100605, false)),
        };
        var fields = PacketCodec.Read(TestBuilds.Packets, build, "FighterInfoReply", binding.Write(reply));
        var target = Assert.IsType<Dictionary<string, object?>>(fields["fighter"]);
        var partner = Assert.IsType<Dictionary<string, object?>>(fields["partner"]);
        Assert.Equal(77, Assert.IsType<int>(target["user_number"]));
        Assert.Equal("Target", target["nickname"]);
        Assert.Equal(88, Assert.IsType<int>(partner["user_number"]));
        Assert.Equal("Partner", partner["nickname"]);
        var relationship = build == "kr-1256"
            ? Assert.IsType<Dictionary<string, object?>>(partner["unknown_48"]) : partner;
        Assert.Equal(BitConverter.DoubleToInt64Bits(2d), Assert.IsType<long>(relationship["couple_since"]));
        Assert.Equal(100605, Assert.IsType<int>(relationship["couple_ring_item"]));
        Assert.False(Assert.IsType<bool>(relationship["couple_partner_online"]));
        if (build == "kr-1256")
        {
            var social = Assert.IsType<Dictionary<string, object?>>(partner["social"]);
            Assert.False(Assert.IsType<bool>(social["unknown_48"]));
        }

        var absent = PacketCodec.Read(TestBuilds.Packets, build, "FighterInfoReply", binding.Write(reply with { CouplePartner = null }));
        Assert.Equal(0, Assert.IsType<int>(Assert.IsType<Dictionary<string, object?>>(absent["partner"])["user_number"]));
    }
}
