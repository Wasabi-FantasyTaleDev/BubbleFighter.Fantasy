using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Dialect.Messages;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Byte-length and field pins for the cn-735 two-frame main-menu GotoRoom body.</summary>
public sealed class GotoRoomGotoMenuCodecTests
{
    [Fact]
    public void MainMenuPinsSessionKindAtOffsetAndEmptyBodyLength()
    {
        var room = new MainMenuRoomOpened(11, "admin", 300001, [300001, 202001]);
        var body = GotoRoomGotoMenuCodec.WriteMainMenu(TestBuilds.Packets, "cn-735", room);
        // cn-735 SessionSetting sub_4254D0: u16, u32, wstr "", u16 x3, bool, wstr "", then kind.
        Assert.Equal(GotoRoomGotoMenuCodec.MainMenuKind, BitConverter.ToInt32(body, 24));
        // Empty GameSetting is 173 bytes; nickname "admin" and two equipped ids make the
        // full GotoRoom body 503 bytes on cn-735 / cn-738 / cn-742.
        Assert.Equal(503, body.Length);
        Assert.Equal(4, GotoRoomGotoMenuCodec.WriteGotoMenu(TestBuilds.Packets, "cn-735").Length);
    }

    [Fact]
    public void MainMenuWritesOccupiedSlotUserNumberAndNickname()
    {
        var room = new MainMenuRoomOpened(0x11A, "TestPlayer01", 300010, [300010]);
        var body = GotoRoomGotoMenuCodec.WriteMainMenu(TestBuilds.Packets, "cn-735", room);
        // After SessionSetting (237) + two u32 + slot count: UserSlot occupied bools, u8, userNum.
        var slot = 237 + 8 + 4;
        Assert.Equal(1, BitConverter.ToInt32(body, slot));
        Assert.Equal(1, BitConverter.ToInt32(body, slot + 4));
        Assert.Equal(1, BitConverter.ToInt32(body, slot + 8));
        Assert.Equal(0xFF, body[slot + 12]);
        Assert.Equal(0x11A, BitConverter.ToInt32(body, slot + 13));
        Assert.Equal(12, BitConverter.ToInt32(body, slot + 17));
        var nick = System.Text.Encoding.Unicode.GetString(body, slot + 21, 24);
        Assert.Equal("TestPlayer01", nick);
    }

    [Fact]
    public void GotoMenuIsFalseNotice()
    {
        var body = GotoRoomGotoMenuCodec.WriteGotoMenu(TestBuilds.Packets, "cn-735");
        Assert.Equal(0, BitConverter.ToInt32(body, 0));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(255)]
    public void TableMenuMatchesBothIndependentWritersAcrossPlayerData(int characterType)
    {
        foreach (var nickname in new[] { "", "admin", "玩家", "abcdefghijklmnopqrstuvwx" })
        foreach (var count in new[] { 0, 1, 19 })
        {
            var room = new MainMenuRoomOpened(-1, nickname, 300000 + characterType,
                Enumerable.Range(100000, count).ToArray());
            var actual = GotoRoomGotoMenuCodec.WriteMainMenu(TestBuilds.Packets, "cn-735", room);
            Assert.Equal(GotoRoomGotoMenuGoldenCodec.WriteMainMenu(room), actual);
            Assert.Equal(RoomCodec.WriteMainMenuEnvelope(room, TestBuilds.Packets,
                "cn-735", TestBuilds.Of("cn-735").Schema), actual);
        }
    }

    [Theory]
    [InlineData(299999)]
    [InlineData(300256)]
    public void CharacterTypeRetainsTheNativeByteRangeCheck(int itemId) =>
        Assert.Throws<OverflowException>(() => GotoRoomGotoMenuCodec.WriteMainMenu(
            TestBuilds.Packets, "cn-735", new MainMenuRoomOpened(1, "", itemId, [])));

    [Fact]
    public void MenuReferencesUseSourceValuesBeforeFixedOverrides()
    {
        var values = PacketMenuProjection.Project(TestBuilds.Packets, "cn-735", "UserSlot",
            new Dictionary<string, object?> { ["character_type"] = 3, ["occupied"] = false });
        Assert.Equal(10L, values["character_type"]);
        Assert.Equal(3, values["team"]);
        Assert.Equal(1L, values["occupied"]);
        Assert.Throws<ConfigException>(() => PacketMenuProjection.Project(TestBuilds.Packets,
            "cn-735", "UserSlot", new Dictionary<string, object?>()));
        var items = PacketMenuProjection.Project(TestBuilds.Packets, "cn-324", "UserSlot",
            new Dictionary<string, object?> { ["equipped_items"] = new[] { 202001 } });
        Assert.Empty(Assert.IsType<object[]>(items["equipped_items"]));
    }
}
