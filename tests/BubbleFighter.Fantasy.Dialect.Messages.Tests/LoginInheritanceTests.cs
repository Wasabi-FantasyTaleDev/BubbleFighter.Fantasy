using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Dialect.Messages;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins which login layout each legacy build ends up with through its base in packets/builds.yaml.</summary>
public sealed class LoginInheritanceTests
{
    [Theory]
    [InlineData("cn-404", "id-400")]
    [InlineData("cn-406", "id-400")]
    [InlineData("cn-279", "cn-324")]
    [InlineData("cn-717", "kr-812")]
    [InlineData("tw-410", "cn-324")]
    public void BuildsShareTheirBasesLayout(string build, string sameAs) =>
        Assert.Equal(TestLogin.Of(sameAs), TestLogin.Of(build));

    [Fact]
    public void TheStatedDifferencesHold()
    {
        Assert.Equal(new LoginShape(true), TestLogin.Of("kr-812"));
        Assert.Equal(new LoginShape(false), TestLogin.Of("id-400"));
        Assert.Equal(new LoginShape(false, RequestToken: true), TestLogin.Of("id-1809"));
        Assert.Equal(new LoginShape(false, RequestToken: true), TestLogin.Of("cn-706"));
        Assert.Equal(new LoginShape(true, LoginResponse: true), TestLogin.OfLineage(["cn-742", "cn-735", "cn-717"]));
        Assert.Equal(new LoginShape(false, RequestStrings: 1), TestLogin.Of("kr-186"));
    }

    /// <summary>
    /// kr-186 sub_418DC0 (Packet_gcpReqLogin slot 0) writes the account wstr, the adapter count and the addresses,
    /// with no further strings and no trailer: 4 + 2 * 8 + 4 + 4 bytes for one account and one adapter.
    /// </summary>
    [Fact]
    public void Kr186LoginIsTheAccountAndTheAdapterList()
    {
        var shape = TestLogin.Of("kr-186");
        var body = new List<byte>();
        body.AddRange(BitConverter.GetBytes(8));
        body.AddRange(System.Text.Encoding.Unicode.GetBytes("TestAcct"));
        body.AddRange(BitConverter.GetBytes(1));
        body.AddRange([127, 0, 0, 1]);
        Assert.Equal(28, body.Count);
        Assert.Equal("TestAcct", LoginMessageCodec.ReadRequest(body.ToArray(), shape).Account);

        // cn-279's request has two more (here empty) strings after the account; kr-186 reads the first as the
        // adapter count and leaves the rest as trailing bytes.
        var cn279 = new List<byte>();
        cn279.AddRange(BitConverter.GetBytes(8));
        cn279.AddRange(System.Text.Encoding.Unicode.GetBytes("TestAcct"));
        cn279.AddRange(new byte[8]);
        cn279.AddRange(body.Skip(20));
        Assert.Throws<PacketFormatException>(() => LoginMessageCodec.ReadRequest(cn279.ToArray(), shape));
        Assert.Equal("TestAcct", LoginMessageCodec.ReadRequest(cn279.ToArray(), TestLogin.Of("cn-279")).Account);
    }
}
