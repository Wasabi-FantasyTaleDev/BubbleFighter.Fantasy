using BubbleFighter.Fantasy.Abstractions.Protocol;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>
/// Byte-length pins for legacy C2S 106 <c>gcpReqGotoLobby</c> and S2C 109 <c>gspGotoLobby</c>.
/// </summary>
public sealed class GotoLobbyWireTests
{
    [Fact]
    public void SuccessReplyIsExactlyFourBytesWithInt32Zero()
    {
        var body = GotoLobbyCodec.WriteSuccess();
        Assert.Equal(4, body.Length);
        Assert.Equal(0, BitConverter.ToInt32(body));
    }

    [Fact]
    public void EmptyRequestIsAccepted()
    {
        GotoLobbyCodec.ReadRequest([]);
    }

    [Fact]
    public void NonEmptyRequestIsRejected()
    {
        Assert.Throws<PacketFormatException>(() => GotoLobbyCodec.ReadRequest([0]));
        Assert.Throws<PacketFormatException>(() => GotoLobbyCodec.ReadRequest([0, 0, 0, 0]));
    }
}
