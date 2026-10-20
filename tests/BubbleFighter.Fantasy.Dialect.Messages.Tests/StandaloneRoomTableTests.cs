using System.Buffers;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins standalone plaza records and empty game settings against the previous schema writer.</summary>
public sealed class StandaloneRoomTableTests
{
    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-717")]
    [InlineData("cn-324")]
    [InlineData("cn-279")]
    [InlineData("id-400")]
    [InlineData("id-1809")]
    [InlineData("cn-406")]
    [InlineData("cn-706")]
    [InlineData("cn-735")]
    public void SceneTransitionKeepsOneFourByteNoticeFlag(string build)
    {
        Assert.Equal(new byte[4], RoomCodec.WriteSceneTransition(TestBuilds.Packets, build));
        var notice = PacketCodec.Write(TestBuilds.Packets, build, "RoomSceneTransition",
            new Dictionary<string, object?> { ["notice"] = true });
        Assert.Equal(new byte[] { 1, 0, 0, 0 }, notice);
        Assert.True((bool)PacketCodec.ReadScalars(TestBuilds.Packets, build, "RoomSceneTransition", notice)["notice"]);
        Assert.Throws<PacketFormatException>(() => PacketCodec.ReadScalars(
            TestBuilds.Packets, build, "RoomSceneTransition", notice[..3]));
        Assert.Throws<PacketFormatException>(() => PacketCodec.ReadScalars(
            TestBuilds.Packets, build, "RoomSceneTransition", new byte[5]));
    }

    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-717")]
    [InlineData("cn-324")]
    [InlineData("cn-279")]
    [InlineData("id-400")]
    [InlineData("id-1809")]
    [InlineData("cn-706")]
    [InlineData("cn-735")]
    public void StandalonePlazaRecordPreservesIdentityLevelAndNativeDefaults(string build)
    {
        var schema = TestBuilds.Of(build).Schema;
        foreach (var level in new[] { 0, 1, 42, 240 })
        {
            var member = new PlazaMember(3, 282, "plaza", level, 300010, []);
            var slot = new RoomSlotValues(true, false, member.UserNumber, member.Nickname,
                member.CharacterItemId, [member.CharacterItemId], false)
            {
                Level = Math.Max(1, member.Level),
            };
            // The plaza record is the member's main-menu record: the table's menu values with their identity and level.
            var values = RoomCodec.MenuSeatValues(schema, slot);
            values["level"] = Math.Max(1, level);
            var expected = PacketCodec.Write(TestBuilds.Packets, build, "UserSlot", values);
            var actual = PlayRoomCodec.WritePlazaSeatRecord(TestBuilds.Packets, build, schema, member);
            Assert.Equal(expected, actual);
            var read = PacketCodec.Read(TestBuilds.Packets, build, "UserSlot", actual, new PacketReadLimits());
            Assert.Equal(282, Convert.ToInt32(read["user_number"]));
            Assert.Equal(Math.Max(1, level), Convert.ToInt32(read["level"]));
            Assert.Equal(true, read["occupied"]);
        }
    }

    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-717")]
    [InlineData("cn-324")]
    [InlineData("cn-279")]
    [InlineData("id-400")]
    [InlineData("id-1809")]
    [InlineData("cn-706")]
    [InlineData("cn-735")]
    public void EmptySettingsRetainEveryNativeDefaultAndExistingBlocksRemainUnchanged(string build)
    {
        var schema = TestBuilds.Of(build).Schema;
        var buffer = new ArrayBufferWriter<byte>();
        RoomCodec.WriteGameSetting(new PacketWriter(buffer), schema);
        var actual = PlazaRoomWire.ApplyGameSettingChange(schema, TestBuilds.Packets, build, default, default);
        Assert.Equal(buffer.WrittenCount, actual.Length);
        Assert.Equal(buffer.WrittenSpan.ToArray(), actual);
        Assert.Equal(actual, PlazaRoomWire.ApplyGameSettingChange(schema, TestBuilds.Packets, build, actual, default));
    }
}
