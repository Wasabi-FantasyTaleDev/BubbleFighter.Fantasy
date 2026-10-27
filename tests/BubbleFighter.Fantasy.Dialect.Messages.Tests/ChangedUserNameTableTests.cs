using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>
/// Pins the nickname-change body: one four-byte word and one counted wide string
/// (<c>ChangedUserName</c> of <c>packets/name-change.yaml</c>, read by the kr-812 receive handler
/// kr-812 sub_82DE50 and the kr-1256 handler kr-1256 sub_9BE480). Empty, four-character and
/// twelve-character names pin 8, 16 and 32 bytes in both builds. A body that changes on purpose
/// updates its pin together with the client reader that decides it.
/// </summary>
public sealed class ChangedUserNameTableTests
{
    private const uint Word = 0x11223344u;
    private const string EmptyName = "4433221100000000";
    private const string FourCharacterName = "44332211040000004100420043004400";
    private const string TwelveCharacterName =
        "443322110C0000004100420043004400450046004700480049004A004B004C00";

    /// <summary>An empty name still carries the four-byte count: the body is 8 bytes, not 4.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("kr-1256")]
    public void EmptyNameIsEightBytes(string build)
    {
        var body = Write(build, "");
        Assert.Equal(8, body.Length);
        Assert.Equal(Convert.FromHexString(EmptyName), body);
    }

    /// <summary>A four-character name adds eight bytes of UTF-16, so the body is 16 bytes.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("kr-1256")]
    public void FourCharacterNameIsSixteenBytes(string build)
    {
        var body = Write(build, "ABCD");
        Assert.Equal(16, body.Length);
        Assert.Equal(Convert.FromHexString(FourCharacterName), body);
    }

    /// <summary>A twelve-character name adds twenty-four bytes of UTF-16, so the body is 32 bytes.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("kr-1256")]
    public void TwelveCharacterNameIsThirtyTwoBytes(string build)
    {
        var body = Write(build, "ABCDEFGHIJKL");
        Assert.Equal(32, body.Length);
        Assert.Equal(Convert.FromHexString(TwelveCharacterName), body);
    }

    /// <summary>The word and the name come back unchanged through the scalar reader in both builds.</summary>
    [Theory]
    [InlineData("kr-812", "")]
    [InlineData("kr-812", "ABCD")]
    [InlineData("kr-812", "ABCDEFGHIJKL")]
    [InlineData("kr-1256", "")]
    [InlineData("kr-1256", "ABCD")]
    [InlineData("kr-1256", "ABCDEFGHIJKL")]
    public void ScalarReadReturnsTheSameWordAndName(string build, string name)
    {
        var fields = PacketCodec.ReadScalars(TestBuilds.Packets, build, "ChangedUserName", Write(build, name));
        Assert.Equal(unchecked((int)Word), fields["unknown_4"]);
        Assert.Equal(name, fields["unknown_8"]);
    }

    private static byte[] Write(string build, string name)
        => PacketCodec.Write(TestBuilds.Packets, build, "ChangedUserName",
            new Dictionary<string, object?> { ["unknown_4"] = Word, ["unknown_8"] = name });
}
