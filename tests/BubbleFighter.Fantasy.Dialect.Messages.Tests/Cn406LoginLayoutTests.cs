using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins the CN2013 native readers' shorter login and main-menu structures.</summary>
public sealed class Cn406LoginLayoutTests
{
    /// <summary>The main-menu projection must retain an occupied local avatar rather than play-only zeros.</summary>
    [Fact]
    public void MenuProjectionKeepsTheLocalSeatOccupied()
    {
        var schema = TestBuilds.Of("cn-406").Schema;
        var body = BubbleFighter.Fantasy.Dialect.Rooms.RoomCodec.WriteMainMenuEnvelope(
            new MainMenuRoomOpened(42, "Player", 300001, [300001, 201003, 202003, 207001]),
            TestBuilds.Packets, "cn-406", schema);
        // cn-406 sub_57EBC0: empty-string session (182 bytes), two seat words and list count.
        var slot = body.AsSpan(182 + 8 + 4);
        Assert.Equal(1, BitConverter.ToInt32(slot));
        Assert.Equal(1, BitConverter.ToInt32(slot[4..]));
        Assert.Equal(1, BitConverter.ToInt32(slot[8..]));
        Assert.Equal(255, slot[12]);
        Assert.Equal(42, BitConverter.ToInt32(slot[13..]));
    }

    /// <summary>Native cn-406 readers stop before the later legacy extensions.</summary>
    [Theory]
    [InlineData("GameSetting", 124)] // cn-406 sub_569BE0
    [InlineData("SessionSetting", 182)] // cn-406 sub_56A160
    [InlineData("UserSlot", 112)] // cn-406 sub_57E8A0
    [InlineData("FighterInfo", 228)] // cn-406 sub_713260
    [InlineData("LoginReply", 48)] // cn-406 sub_714F50
    public void EmptyStructuresMatchNativeReaderLengths(string structure, int length)
    {
        var body = PacketCodec.Write(TestBuilds.Packets, "cn-406", structure,
            new Dictionary<string, object?>());
        Assert.Equal(length, body.Length);
    }

    /// <summary>The native login serializer has three strings and an adapter vector, with no trailer.</summary>
    [Fact]
    public void NativeAccountSurvivesLoginAndUnexpectedTrailerIsRejected()
    {
        var body = new List<byte>();
        body.AddRange(BitConverter.GetBytes(8));
        body.AddRange(System.Text.Encoding.Unicode.GetBytes("TestAcct"));
        body.AddRange(new byte[12]);
        var shape = TestLogin.OfLineage(["cn-406", "id-400", "kr-812"]);
        Assert.Equal("TestAcct", LoginMessageCodec.ReadRequest(body.ToArray(), shape).Account);
        Assert.Equal(32, body.Count);
        body.AddRange(new byte[4]);
        Assert.Throws<PacketFormatException>(() => LoginMessageCodec.ReadRequest(body.ToArray(), shape));
    }

    /// <summary>cn-406 sub_714F50 preserves the account, user number and nickname without a later-client tail.</summary>
    [Fact]
    public void AcceptedReplyRetainsTheExistingIdentity()
    {
        var body = LoginMessageCodec.WriteAccepted(new LoginAccepted(42, "TestAcct", "Player", DateTime.FromOADate(45000), 300001),
            TestLogin.Of("id-400"), TestBuilds.Packets, "cn-406");
        Assert.Equal(86, body.Length);
        var reader = new PacketReader(body);
        Assert.Equal(0, reader.ReadInt32());
        Assert.Equal("TestAcct", reader.ReadWideStringPrefixed());
        Assert.Equal(42, reader.ReadInt32());
        Assert.Equal("Player", reader.ReadWideStringPrefixed());
        Assert.Equal(1, reader.ReadInt32());
    }
}
