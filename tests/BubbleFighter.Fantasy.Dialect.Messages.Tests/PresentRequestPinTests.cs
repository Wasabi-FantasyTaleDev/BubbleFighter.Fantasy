using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>
/// Pins the gift-box request bodies added for the modern family: the page request both list pages use
/// (kr-1256 sub_E49DC0), the thank-you request (kr-812 reader sub_503BF0, kr-1256 writer sub_E4BB30),
/// the empty close request (kr-812 828, kr-1256 830) and the restated modern purchase request
/// (kr-1256 writer sub_650770). A body that changes on purpose updates its pin together with the
/// client function that decides it.
/// </summary>
public sealed class PresentRequestPinTests
{
    /// <summary>The page request is two words: the first index and the page size the dialog always sends.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("kr-1256")]
    public void PageRequestIsTwoWords(string build)
    {
        var body = PacketCodec.Write(TestBuilds.Packets, build, "PresentListRequest",
            new Dictionary<string, object?> { ["start"] = 8u, ["count"] = 4u });
        Assert.Equal(8, body.Length);
        Assert.Equal(Convert.FromHexString("0800000004000000"), body);
        var fields = PacketCodec.ReadScalars(TestBuilds.Packets, build, "PresentListRequest", body);
        Assert.Equal(8, fields["start"]);
        Assert.Equal(4, fields["count"]);
    }

    /// <summary>The thank-you request is a word, a counted string and a trailing word.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("kr-1256")]
    public void ThankRequestIsAWordAStringAndAWord(string build)
    {
        var body = PacketCodec.Write(TestBuilds.Packets, build, "PresentThankRequest",
            new Dictionary<string, object?> { ["id"] = 7u, ["message"] = "OK", ["unknown_36"] = 0u });
        Assert.Equal(16, body.Length);
        Assert.Equal(Convert.FromHexString("07000000020000004F004B0000000000"), body);
        var fields = PacketCodec.ReadScalars(TestBuilds.Packets, build, "PresentThankRequest", body);
        Assert.Equal(7, fields["id"]);
        Assert.Equal("OK", fields["message"]);
        Assert.Equal(0, fields["unknown_36"]);
    }

    /// <summary>Closing the gift box carries an empty body in both families.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("kr-1256")]
    public void CloseRequestIsEmpty(string build)
    {
        var body = PacketCodec.Write(TestBuilds.Packets, build, "PresentCloseRequest",
            new Dictionary<string, object?>());
        Assert.Empty(body);
    }

    /// <summary>The modern purchase request keeps the legacy order with the modern offsets.</summary>
    [Theory]
    [InlineData("kr-1256")]
    public void ModernPurchaseRequestIsPinned(string build)
    {
        var body = PacketCodec.Write(TestBuilds.Packets, build, "PresentSendRequest",
            new Dictionary<string, object?>
            {
                ["receiver_nickname"] = "AB", ["unknown_24"] = "", ["item_key"] = 7u, ["message"] = "",
            });
        Assert.Equal(20, body.Length);
        Assert.Equal(Convert.FromHexString("0200000041004200000000000700000000000000"), body);
        var fields = PacketCodec.ReadScalars(TestBuilds.Packets, build, "PresentSendRequest", body);
        Assert.Equal("AB", fields["receiver_nickname"]);
        Assert.Equal("", fields["unknown_24"]);
        Assert.Equal(7, fields["item_key"]);
        Assert.Equal("", fields["message"]);
    }
}
