using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins the kr-812 room chat line byte for byte.</summary>
public sealed class RoomChatWireTests
{
    [Theory]
    [InlineData("kr-812", 34)]
    [InlineData("cn-717", 34)]
    [InlineData("cn-324", 30)]
    [InlineData("cn-279", 30)]
    [InlineData("id-400", 30)]
    [InlineData("id-1809", 34)]
    [InlineData("cn-706", 34)]
    [InlineData("cn-406", 30)]
    public void ChatSuffixMatchesEachOwnClientReader(string build, int length)
    {
        var body = PlayRoomCodec.WriteChat(TestBuilds.Packets, build, 2, "A", -1, "B");
        Assert.Equal(length, body.Length);
        const string prefix = "0200010000004100FFFFFFFF00000000010000004200FFFFFFFF";
        Assert.Equal(prefix + (length == 34 ? "00000000" : "") + "00000000", Convert.ToHexString(body));
    }

    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-717")]
    [InlineData("cn-324")]
    [InlineData("cn-279")]
    [InlineData("id-400")]
    [InlineData("id-1809")]
    [InlineData("cn-706")]
    [InlineData("cn-406")]
    public void NativeRoomControlBodiesKeepSignedWordsAndStrictFrameBounds(string build)
    {
        var tables = TestBuilds.Packets;
        var chat = Convert.FromHexString("FFFFFFFF0200000041004200");
        var fields = PacketCodec.ReadScalars(tables, build, "RoomChatRequest", chat);
        Assert.Equal(-1, fields["channel"]);
        Assert.Equal("AB", fields["text"]);
        Assert.Throws<PacketFormatException>(() => PacketCodec.ReadScalars(tables, build, "RoomChatRequest", chat[..^1]));
        Assert.Throws<PacketFormatException>(() => PacketCodec.ReadScalars(tables, build, "RoomChatRequest", [.. chat, 0]));

        var locked = PacketCodec.Write(tables, build, "RoomSeatLock",
            new Dictionary<string, object?> { ["slot"] = -1, ["closed"] = true });
        Assert.Equal("FFFFFFFF01000000", Convert.ToHexString(locked));
        var lockFields = PacketCodec.ReadScalars(tables, build, "RoomSeatLock", locked);
        Assert.Equal(-1, lockFields["slot"]);
        Assert.Equal(true, lockFields["closed"]);
        Assert.Throws<PacketFormatException>(() => PacketCodec.ReadScalars(tables, build, "RoomSeatLock", locked[..^1]));
        Assert.Throws<PacketFormatException>(() => PacketCodec.ReadScalars(tables, build, "RoomSeatLock", [.. locked, 0]));

        var kick = Convert.FromHexString("0200000041004200");
        Assert.Equal("AB", PacketCodec.ReadScalars(tables, build, "RoomKickRequest", kick)["nickname"]);
        Assert.Throws<PacketFormatException>(() => PacketCodec.ReadScalars(tables, build, "RoomKickRequest", kick[..^1]));
        Assert.Throws<PacketFormatException>(() => PacketCodec.ReadScalars(tables, build, "RoomKickRequest", [.. kick, 0]));
        var kicked = PacketCodec.Write(tables, build, "RoomMemberKicked",
            new Dictionary<string, object?> { ["by_nickname"] = "A", ["nickname"] = "B", ["result"] = 0 });
        Assert.Equal(16, kicked.Length);
        Assert.Equal("01000000410001000000420000000000", Convert.ToHexString(kicked));
    }

    [Fact]
    public void AChatLineMatchesTheRecordedLength()
    {
        // u16 slot + wstr (4 + 7 x 2) + u32 + empty wstr (4) + wstr (4 + 2 x 2) + u32 + u32 + bool32 = 48.
        var body = PlayRoomCodec.WriteChat(TestBuilds.Packets, "kr-812", 2, "보신탕집애완견", 0, "님아");

        Assert.Equal(48, body.Length);
        Assert.Equal(2, BitConverter.ToUInt16(body, 0));
        Assert.Equal(7, BitConverter.ToInt32(body, 2));
        Assert.Equal(-1, BitConverter.ToInt32(body, 36));
        Assert.Equal(0, BitConverter.ToInt32(body, 40));
        Assert.Equal(0, BitConverter.ToInt32(body, 44));
    }
}
