using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Dialect.Rooms;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Compares complete nested room envelopes with the previous schema writer.</summary>
public sealed class GotoRoomTableTests
{
    /// <summary>Party notices occupy the existing bool32 tail, without rewriting identity, seat or equipment bytes.</summary>
    [Theory]
    [InlineData("kr-812", 8)]
    [InlineData("cn-717", 8)]
    [InlineData("cn-324", 4)]
    [InlineData("cn-279", 4)]
    [InlineData("cn-406", 4)]
    [InlineData("id-400", 8)]
    [InlineData("id-1809", 8)]
    [InlineData("cn-706", 8)]
    [InlineData("cn-735", 8)]
    public void PartyTransitionChangesOnlyTheNativeNoticeWord(string build, int tailBytes)
    {
        var schema = TestBuilds.Of(build).Schema;
        var room = new RoomValues(2,
            [new(true, false, 42, "leader", 300003, [300003, 201001], false),
             new(true, false, 43, "member", 300005, [300005, 201001], false)])
        {
            Play = true, RoomId = 8, MasterSlot = 0, LocalSlot = 1, OpenSeats = 8,
        };
        var ordinary = RoomCodec.WriteRoomEnvelope(TestBuilds.Packets, build, schema, room);
        var party = RoomCodec.WriteRoomEnvelope(TestBuilds.Packets, build, schema, room with { PartyTransition = true });
        var expected = ordinary.ToArray();
        expected[^tailBytes] = 1;
        Assert.Equal(0, BitConverter.ToInt32(ordinary, ordinary.Length - tailBytes));
        Assert.Equal(ordinary.Length, party.Length);
        Assert.Equal(expected, party);
        Assert.Equal(new byte[] { 1, 0, 0, 0 }, RoomCodec.WriteSceneTransition(TestBuilds.Packets, build, true));
        Assert.Equal(new byte[4], RoomCodec.WriteSceneTransition(TestBuilds.Packets, build));
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
    public void SinglePlayerScenesRetainNativeMenuSelectAndTutorialValues(string build)
    {
        var schema = TestBuilds.Of(build).Schema;
        var player = new MainMenuRoomOpened(42, "ab", 300003, [300003, 201001]);
        foreach (var kind in new[] { 1, 2, 5, 8 })
        {
            var expected = RoomCodec.WriteRoom(player, schema, kind, 103, "tutorial",
                "directory", "current", 3, "npc");
            var actual = RoomCodec.WriteSingleRoomEnvelope(player, TestBuilds.Packets, build,
                schema, kind, 103, "tutorial", "directory", "current", 3, "npc");
            Assert.Equal(expected.Length, actual.Length);
            Assert.Equal(expected, actual);
        }
        Assert.Equal(RoomCodec.WriteMainMenu(player, schema),
            RoomCodec.WriteMainMenuEnvelope(player, TestBuilds.Packets, build, schema));
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
    public void MultiplayerEnvelopePreservesEverySeatAndHostSettings(string build)
    {
        var schema = TestBuilds.Of(build).Schema;
        var occupied = new RoomSlotValues(true, false, 42, "ab", 300003, [300003, 201001], true)
        {
            Team = 2, Level = 42, SpawnIndex = 1,
        };
        var room = new RoomValues(2,
            [occupied, RoomSlotValues.Empty(false), RoomSlotValues.Empty(true),
                occupied with { UserNumber = 43, Nickname = "cd", Team = 1, SpawnIndex = 3 }])
        {
            Play = true, RoomId = 108, Title = "room", Mode = 101, SessionState = 2,
            MasterSlot = 3, LocalSlot = 0, OpenSeats = 3, HasPassword = true,
            MapName = "map", CurrentMap = "current", NpcTableKey = "npc", Difficulty = 3,
        };
        foreach (var hostSettings in new[] { false, true })
        {
            var values = hostSettings ? room with
            {
                GameSetting = RoomCodec.WriteGameSettingChange(schema, room),
            } : room;
            var expected = RoomCodec.WriteRoom(schema, values);
            var actual = RoomCodec.WriteRoomEnvelope(TestBuilds.Packets, build, schema, values);
            Assert.Equal(expected.Length, actual.Length);
            Assert.Equal(expected, actual);
        }
        var game = RoomCodec.WriteGameSettingEnvelope(TestBuilds.Packets, build, schema, room);
        Assert.Equal(RoomCodec.WriteGameSettingChange(schema, room), game);
    }

    [Fact]
    public void CompleteRoomRejectsBodyBeyondNativeFrameBudget()
    {
        var slots = Enumerable.Range(0, 16).Select(i =>
            new RoomSlotValues(true, false, i, new string('a', 2500), 300003, [], false)).ToArray();
        var room = new RoomValues(2, slots) { Play = true };
        Assert.Throws<ArgumentOutOfRangeException>(() => RoomCodec.WriteRoomEnvelope(
            TestBuilds.Packets, "kr-812", TestBuilds.Of("kr-812").Schema, room));
    }
}
