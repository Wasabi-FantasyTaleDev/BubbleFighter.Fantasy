using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>
/// Pins the room-intrude pair before any binding exists: the empty request of
/// <c>packets/room-intrude.yaml</c> and the two-word reply read by the kr-812 and cn-717 clients.
/// A body that changes on purpose updates its pin together with the client reader that decides it.
/// </summary>
public sealed class RoomIntrudeTablePins
{
    private static readonly PacketTables Tables = PacketTables.Load(TestBuilds.PacketsDirectory());

    /// <summary>The intrude request has no body in either inspected client.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-717")]
    public void IntrudeRequestIsEmpty(string build)
    {
        Assert.Empty(Tables.Resolve(build, "RoomIntrudeRequest"));
        Assert.Empty(PacketCodec.Read(Tables, build, "RoomIntrudeRequest", []));
        Assert.Empty(PacketCodec.Write(Tables, build, "RoomIntrudeRequest", new Dictionary<string, object?>()));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(Tables, build, "RoomIntrudeRequest", [0]));
    }

    /// <summary>Pins two distinct words including the second-word sentinel inspected by kr-812 sub_808BC0.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-717")]
    public void IntrudeReplyPinsTwoWords(string build)
    {
        var body = PacketCodec.Write(Tables, build, "RoomIntrudeReply", new Dictionary<string, object?>
        {
            ["unknown_4"] = 1,
            ["unknown_8"] = -1,
        });
        Assert.Equal(8, body.Length);
        Assert.Equal("01000000FFFFFFFF", Convert.ToHexString(body));

        var fields = PacketCodec.Read(Tables, build, "RoomIntrudeReply", body);
        Assert.Equal(2, fields.Count);
        Assert.Equal(1, fields["unknown_4"]);
        Assert.Equal(-1, fields["unknown_8"]);
        Assert.Equal(body, PacketCodec.Write(Tables, build, "RoomIntrudeReply", fields));

        for (var length = 0; length < body.Length; length++)
        {
            var prefix = body[..length];
            Assert.Throws<PacketFormatException>(() => PacketCodec.Read(Tables, build, "RoomIntrudeReply", prefix));
        }
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(Tables, build, "RoomIntrudeReply", [.. body, 0]));
    }

    /// <summary>
    /// Modern families are excluded from these legacy rows; empty resolution does not register a reply.
    /// </summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void ReplyIsNotClaimedForBuildsWhoseNumberIsAnotherPacket(string build) =>
        Assert.Empty(Tables.Resolve(build, "RoomIntrudeReply"));
}
