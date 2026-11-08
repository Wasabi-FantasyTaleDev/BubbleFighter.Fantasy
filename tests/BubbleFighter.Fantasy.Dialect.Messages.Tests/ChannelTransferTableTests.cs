using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins the audited native channel-transfer bodies without granting migration authentication.</summary>
public sealed class ChannelTransferTableTests
{
    [Fact]
    public void MoveOfferCarriesTheIndependentGameEndpoint()
    {
        var body = PacketCodec.Write(TestBuilds.Packets, "kr-812", "ChannelMoveOffer", new Dictionary<string, object?>
        {
            ["result"] = 0,
            ["ipv4_word"] = 0x04030201,
            ["port"] = 7121,
        });
        Assert.Equal(Convert.FromHexString("0000000001020304D11B"), body);
        Assert.Equal(10, body.Length);
        AssertCompleteBody("ChannelMoveOffer", body);
    }

    [Fact]
    public void MoveIntoCarriesTwoWordsWithoutAnInventedCredential()
    {
        var body = PacketCodec.Write(TestBuilds.Packets, "kr-812", "ChannelMoveIntoRequest", new Dictionary<string, object?>
        {
            ["unknown_4"] = 0x11223344,
            ["unknown_8"] = 1,
        });
        Assert.Equal(Convert.FromHexString("4433221101000000"), body);
        Assert.Equal(8, body.Length);
        AssertCompleteBody("ChannelMoveIntoRequest", body);
    }

    private static void AssertCompleteBody(string structure, byte[] body)
    {
        var values = PacketCodec.ReadScalars(TestBuilds.Packets, "kr-812", structure, body);
        Assert.Equal(body, PacketCodec.Write(TestBuilds.Packets, "kr-812", structure,
            values.ToDictionary(pair => pair.Key, pair => (object?)pair.Value, StringComparer.Ordinal)));
        for (var length = 0; length < body.Length; length++)
            Assert.Throws<PacketFormatException>(() => PacketCodec.ReadScalars(
                TestBuilds.Packets, "kr-812", structure, body[..length]));
        Assert.Throws<PacketFormatException>(() => PacketCodec.ReadScalars(
            TestBuilds.Packets, "kr-812", structure, [.. body, 0]));
    }
}
