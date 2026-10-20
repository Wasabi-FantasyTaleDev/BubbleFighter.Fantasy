using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins the expired-item action independently of the item-use and article-update numeric spaces.</summary>
public sealed class InventoryActionTableTests
{
    /// <summary>The audited clients send one item ID and consume an identity/result pair.</summary>
    [Theory]
    [InlineData("kr-812", 807, 812)]
    [InlineData("cn-279", 807, 812)]
    [InlineData("cn-324", 807, 812)]
    [InlineData("id-400", 807, 812)]
    [InlineData("id-1809", 807, 812)]
    [InlineData("cn-717", 807, 812)]
    [InlineData("cn-406", 807, 812)]
    [InlineData("cn-404", 807, 812)]
    [InlineData("cn-408", 807, 812)]
    [InlineData("cn-706", 807, 812)]
    [InlineData("kr-1256", 808, 815)]
    [InlineData("cn-774", 808, 815)]
    [InlineData("tw-454", 808, 815)]
    public void ExpiredItemActionsUseTheirOwnDirectionalOpcodes(string build, int requestOpcode, int replyOpcode)
    {
        var root = Directory.GetParent(TestBuilds.PacketsDirectory())!.FullName;
        var profile = new ClientProfile(build, build[..2], int.Parse(build[3..]), 0, 0, build);
        var table = OpcodeTableLoader.Load(Path.Combine(root, "profiles", build, "opcodes.yaml"), profile)
            .ForConnection(OpcodeConnection.Game);
        Assert.Equal(requestOpcode, table.C2S("gcpReqInventoryThrowItem"));
        Assert.Equal(replyOpcode, table.S2C("gspRepInventoryThrowItem"));

        var request = Convert.FromHexString("11150300");
        var fields = PacketCodec.ReadScalars(TestBuilds.Packets, build, "InventoryActionRequest", request);
        Assert.Equal(202001, fields["item_id"]);
        Assert.Equal(request, PacketCodec.Write(TestBuilds.Packets, build, "InventoryActionRequest",
            fields.ToDictionary(pair => pair.Key, pair => (object?)pair.Value)));

        foreach (var result in new[] { 0, 1, 2 })
        {
            var reply = PacketCodec.Write(TestBuilds.Packets, build, "InventoryThrowReply",
                new Dictionary<string, object?> { ["item_id"] = 202001, ["result"] = result });
            Assert.Equal(8, reply.Length);
            Assert.Equal(request, reply[..4]);
            Assert.Equal(new byte[] { (byte)result, 0, 0, 0 }, reply[4..]);
            var read = PacketCodec.ReadScalars(TestBuilds.Packets, build, "InventoryThrowReply", reply);
            Assert.Equal(202001, read["item_id"]);
            Assert.Equal(result, read["result"]);
            for (var length = 0; length < reply.Length; length++)
                Assert.Throws<PacketFormatException>(() =>
                    PacketCodec.ReadScalars(TestBuilds.Packets, build, "InventoryThrowReply", reply[..length]));
            Assert.Throws<PacketFormatException>(() =>
                PacketCodec.ReadScalars(TestBuilds.Packets, build, "InventoryThrowReply", [.. reply, 0]));
        }

        if (requestOpcode == 808)
        {
            Assert.Equal(806, table.C2S("gcpReqInventoryUseItem"));
            Assert.Equal(808, table.S2C("gspUpdateAllArticleSlot"));
            Assert.Equal(815, table.C2S("gcpReqPresentReceiveSubList"));
        }
    }
}
