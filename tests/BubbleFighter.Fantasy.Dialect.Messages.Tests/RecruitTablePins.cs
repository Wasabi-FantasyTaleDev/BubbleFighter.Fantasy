using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>
/// Pins the room amplifier and recruit bodies stated in <c>packets/recruit.yaml</c>: the amplifier request
/// shared by the legacy and modern families (writers kr-812 sub_845040 and kr-1256 sub_9D7350), the kr-812
/// recruit request (writer kr-812 sub_845640) and the kr-812 recruit notice (reader kr-812 sub_80AC10). The
/// recruit pair is legacy-only because the kr-1256 dispatcher has no recruit label and its C2S 2027 carries
/// a different body.
/// </summary>
public sealed class RecruitTablePins
{
    /// <summary>The amplifier request is a word, a counted string and two words: 16 + 2*len bytes.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("kr-1256")]
    public void AmplifierRequestIsPinned(string build)
    {
        var body = PacketCodec.Write(TestBuilds.Packets, build, "RoomAmplifierRequest",
            new Dictionary<string, object?>
            {
                ["kind"] = 101u, ["message"] = "A", ["unknown_28"] = 0x11223344u, ["unknown_32"] = 0x55667788u,
            });
        Assert.Equal(18, body.Length);
        Assert.Equal(Convert.FromHexString("650000000100000041004433221188776655"), body);
        var fields = PacketCodec.ReadScalars(TestBuilds.Packets, build, "RoomAmplifierRequest", body);
        Assert.Equal(101, fields["kind"]);
        Assert.Equal("A", fields["message"]);
        Assert.Equal(unchecked((int)0x11223344), Convert.ToInt32(fields["unknown_28"]));
        Assert.Equal(unchecked((int)0x55667788), Convert.ToInt32(fields["unknown_32"]));
    }

    /// <summary>The recruit request is a counted string and one byte: 5 + 2*len bytes, the legacy family.</summary>
    [Fact]
    public void RecruitRequestIsPinned()
    {
        var body = PacketCodec.Write(TestBuilds.Packets, "kr-812", "RoomRecruitRequest",
            new Dictionary<string, object?> { ["message"] = "Hi", ["unknown_32"] = (byte)7 });
        Assert.Equal(9, body.Length);
        Assert.Equal(Convert.FromHexString("020000004800690007"), body);
        var fields = PacketCodec.ReadScalars(TestBuilds.Packets, "kr-812", "RoomRecruitRequest", body);
        Assert.Equal("Hi", fields["message"]);
        Assert.Equal((byte)7, fields["unknown_32"]);
    }

    /// <summary>The recruit notice is a member identifier and a counted string: 6 + 2*len bytes, the legacy family.</summary>
    [Fact]
    public void RecruitReplyIsPinned()
    {
        var body = PacketCodec.Write(TestBuilds.Packets, "kr-812", "RoomRecruitReply",
            new Dictionary<string, object?> { ["member_id"] = (ushort)0x1234, ["message"] = "A" });
        Assert.Equal(8, body.Length);
        Assert.Equal(Convert.FromHexString("3412010000004100"), body);
        var fields = PacketCodec.ReadScalars(TestBuilds.Packets, "kr-812", "RoomRecruitReply", body);
        Assert.Equal((ushort)0x1234, fields["member_id"]);
        Assert.Equal("A", fields["message"]);
    }

    /// <summary>The recruit pair resolves for the legacy family; the modern family states no form for it.</summary>
    [Fact]
    public void RecruitPairIsLegacyOnly()
    {
        Assert.NotEmpty(TestBuilds.Packets.Resolve("kr-812", "RoomRecruitRequest"));
        Assert.NotEmpty(TestBuilds.Packets.Resolve("kr-812", "RoomRecruitReply"));
        Assert.NotEmpty(TestBuilds.Packets.Resolve("cn-717", "RoomRecruitReply"));
        Assert.Empty(TestBuilds.Packets.Resolve("kr-1256", "RoomRecruitRequest"));
        Assert.Empty(TestBuilds.Packets.Resolve("kr-1256", "RoomRecruitReply"));
        Assert.NotEmpty(TestBuilds.Packets.Resolve("kr-1256", "RoomAmplifierRequest"));
    }
}
