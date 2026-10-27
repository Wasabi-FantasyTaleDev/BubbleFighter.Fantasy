using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>
/// Pins the gift thank-you frames (packets/present.yaml): the thank-you request (reader kr-812 sub_503BF0 and builder
/// kr-1256 sub_E4BB30: a word, a wide string and a word), the send-thanks reply (kr-812 sub_80A910 and kr-1256
/// sub_9BB8B0: one word) and the confirm reply (kr-812 sub_80A910 and the inline kr-1256 case kr-1256 0x9D4DC2: one
/// word). The thank-message view reply is not tabled yet, so it has no pin here.
/// </summary>
public sealed class PresentThanksWireTests
{
    /// <summary>The shared thank-you request preserves the gift id, counted message and trailing unknown word.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("kr-1256")]
    public void SendThanksRequestCarriesTheIdTheMessageAndOneWord(string build)
    {
        // Id 7, no message: 4 + 4 (empty wide-string count) + 4 = 12 bytes.
        var empty = Convert.FromHexString("0700000000000000" + "2A000000");
        Assert.Equal(12, empty.Length);
        var decoded = PacketCodec.Read(TestBuilds.Packets, build, "PresentThankRequest", empty);
        Assert.Equal(7, Assert.IsType<int>(decoded["id"]));
        Assert.Equal(string.Empty, Assert.IsType<string>(decoded["message"]));
        Assert.Equal(42, Assert.IsType<int>(decoded["unknown_36"]));
        Assert.Equal(empty, PacketCodec.Write(TestBuilds.Packets, build, "PresentThankRequest", decoded));

        // Id 7, message "hi" (two characters): 4 + 4 + 4 + 4 = 16 bytes.
        var message = Convert.FromHexString("07000000" + "02000000" + "68006900" + "2A000000");
        Assert.Equal(16, message.Length);
        var withMessage = PacketCodec.Read(TestBuilds.Packets, build, "PresentThankRequest", message);
        Assert.Equal("hi", Assert.IsType<string>(withMessage["message"]));
        Assert.Equal(message, PacketCodec.Write(TestBuilds.Packets, build, "PresentThankRequest", withMessage));

        foreach (var length in new[] { 0, 4, 8, 11, 13, 18 })
            Assert.Throws<PacketFormatException>(() =>
                PacketCodec.Read(TestBuilds.Packets, build, "PresentThankRequest", new byte[length]));
    }

    /// <summary>The thank-you and confirmation replies are exactly one word.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("kr-1256")]
    public void ReplyBodiesAreOneWord(string build)
    {
        foreach (var (structure, hex) in new[]
                 {
                     ("PresentSendThanksReply", "01000000"),
                     ("PresentSendThanksReply", "FFFFFFFF"),
                     ("PresentConfirmReply", "01000000"),
                     ("PresentConfirmReply", "00000000"),
                 })
        {
            var body = Convert.FromHexString(hex);
            Assert.Equal(4, body.Length);
            var decoded = PacketCodec.Read(TestBuilds.Packets, build, structure, body);
            Assert.Equal(body, PacketCodec.Write(TestBuilds.Packets, build, structure, decoded));
            foreach (var length in new[] { 0, 3, 5 })
                Assert.Throws<PacketFormatException>(() =>
                    PacketCodec.Read(TestBuilds.Packets, build, structure, new byte[length]));
        }
    }

    /// <summary>Viewing a thank-you reuses the existing single-gift-id request layout.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("kr-1256")]
    public void ViewThanksRequestReusesTheOneGiftIdBody(string build)
    {
        // C2S 820 (kr-812 sub_80A910) and C2S 822 (kr-1256 sub_E45350) carry one id, the same body as the other
        // one-gift requests, so the table reuses PresentIdRequest.
        var body = Convert.FromHexString("07000000");
        var decoded = PacketCodec.Read(TestBuilds.Packets, build, "PresentIdRequest", body);
        Assert.Equal(7, Assert.IsType<int>(decoded["id"]));
        Assert.Equal(body, PacketCodec.Write(TestBuilds.Packets, build, "PresentIdRequest", decoded));
        Assert.Throws<PacketFormatException>(() =>
            PacketCodec.Read(TestBuilds.Packets, build, "PresentIdRequest", new byte[5]));
    }
}
