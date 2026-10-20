using BubbleFighter.Fantasy.Contracts.Lobby;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>
/// Pins the kr-1256 family's table-driven <c>gspRepLogin</c>. These are the exact bytes the modern login codec
/// wrote before slice S3 moved the reply onto <c>LoginMessageCodec</c> (golden keys <c>repLogin</c> and
/// <c>repLoginFail</c>): a server-issued session identity, user number 1001, nickname "nick", the Dao
/// character type, channel 99, a zero server time and the national flag.
/// </summary>
public sealed class LoginReplyTableTests
{
    private static readonly LoginAccepted Accepted = new(1001, "user", "nick", new DateTime(2026, 10, 16), 300001);

    /// <summary>The three builds that share the reply layout write the pinned accepted body.</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void ModernAcceptedReplyKeepsThePinnedBytes(string build)
    {
        var body = LoginMessageCodec.WriteTableModernAccepted(Accepted, "bff-7", TestBuilds.Packets, build, 99);
        Assert.Equal(
            "00000000050000006200660066002D003700E9030000040000006E00690063006B00010000000000000063000000000000000000000000000000000000000100000000",
            Convert.ToHexString(body));
    }

    /// <summary>The refused reply keeps the result word and the table's national tag.</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void ModernRefusalKeepsThePinnedBytes(string build)
    {
        var body = LoginMessageCodec.WriteTableModernRejected(
            new LoginRejected(LoginRejection.AccountNotFound), TestBuilds.Packets, build);
        // The kr-1256 refusal row applies to the builds based on it, so all three write the same body.
        Assert.Equal("01000000000000000100000000", Convert.ToHexString(body));
    }
}
