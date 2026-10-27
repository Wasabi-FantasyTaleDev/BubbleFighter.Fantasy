using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins the second-password request and result bodies. The password in these tests is a dummy value.</summary>
public sealed class SecondPasswordTableTests
{
    private const string DummyPassword = "1234";

    private static readonly Dictionary<string, object?> Empty = new();

    /// <summary>The request is one counted UTF-16 string and nothing else.</summary>
    [Fact]
    public void RequestIsOneCountedWideString()
    {
        var body = PacketCodec.Write(TestBuilds.Packets, "kr-812", "SecondPasswordRequest",
            new Dictionary<string, object?> { ["password"] = DummyPassword });
        Assert.Equal(Convert.FromHexString("040000003100320033003400"), body);

        var fields = PacketCodec.ReadScalars(TestBuilds.Packets, "kr-812", "SecondPasswordRequest", body);
        Assert.Equal(DummyPassword, Assert.IsType<string>(fields["password"]));
        RejectIncompleteAndTrailing("kr-812", "SecondPasswordRequest", body);

        Assert.Equal(4, PacketCodec.Write(TestBuilds.Packets, "kr-812", "SecondPasswordRequest", Empty).Length);
    }

    /// <summary>The notify result is one word.</summary>
    [Fact]
    public void ResultIsOneWord()
    {
        var body = PacketCodec.Write(TestBuilds.Packets, "kr-812", "SecondPasswordResult",
            new Dictionary<string, object?> { ["result"] = 3 });
        Assert.Equal(Convert.FromHexString("03000000"), body);

        var fields = PacketCodec.ReadScalars(TestBuilds.Packets, "kr-812", "SecondPasswordResult", body);
        Assert.Equal(3, fields["result"]);
        RejectIncompleteAndTrailing("kr-812", "SecondPasswordResult", body);
        Assert.Equal(4, PacketCodec.Write(TestBuilds.Packets, "kr-812", "SecondPasswordResult", Empty).Length);
    }

    /// <summary>Legacy requests and shared results retain their lengths; modern requests are unestablished.</summary>
    [Fact]
    public void EveryBuildResolvesTheSecondPasswordShapes()
    {
        foreach (var build in TestBuilds.Packets.Builds)
        {
            Assert.Equal(new[] { "kr-1256", "cn-774", "tw-454" }.Contains(build) ? 0 : 4,
                PacketCodec.Write(TestBuilds.Packets, build, "SecondPasswordRequest", Empty).Length);
            Assert.Equal(4, PacketCodec.Write(TestBuilds.Packets, build, "SecondPasswordResult", Empty).Length);
        }
    }

    /// <summary>The request, result and notify rows state the expected numbers.</summary>
    [Fact]
    public void RowsStateTheSecondPasswordNames()
    {
        Assert.Equal((ushort)2001, TestBuilds.Opcodes("kr-812").C2S("gcpReqCompareSecondPassword"));
        Assert.Equal((ushort)2002, TestBuilds.Opcodes("kr-812").C2S("gcpReqSetSecondPassword"));
        Assert.Equal((ushort)2003, TestBuilds.Opcodes("kr-812").C2S("gcpReqDeleteSecondPassword"));
        Assert.Equal((ushort)966, TestBuilds.Opcodes("kr-812").S2C("gspNotifySecondPassword"));

        Assert.Equal((ushort)961, TestBuilds.Opcodes("kr-1256").S2C("gspNotifySecondPassword"));
        Assert.Equal((ushort)960, TestBuilds.Opcodes("cn-774").S2C("gspNotifySecondPassword"));
        Assert.Equal((ushort)960, TestBuilds.Opcodes("tw-454").S2C("gspNotifySecondPassword"));
    }

    private static void RejectIncompleteAndTrailing(string build, string structure, byte[] body)
    {
        for (var length = 0; length < body.Length; length++)
        {
            Assert.Throws<PacketFormatException>(() =>
                PacketCodec.ReadScalars(TestBuilds.Packets, build, structure, body[..length]));
        }
        Assert.Throws<PacketFormatException>(() =>
            PacketCodec.ReadScalars(TestBuilds.Packets, build, structure, [.. body, 0]));
    }
}
