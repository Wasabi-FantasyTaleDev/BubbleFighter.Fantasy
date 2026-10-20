using System.Buffers;
using System.Security.Cryptography;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Dialect.Rooms;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins complete entry envelopes against native layouts and retained score-block bytes.</summary>
public sealed class EnterGameTableTests
{
    [Theory]
    [InlineData("kr-812", 2)]
    [InlineData("cn-717", 3)]
    [InlineData("id-1809", 1)]
    [InlineData("cn-706", 1)]
    public void ScalarEntriesMatchNativeWordAndFlagOrder(string build, int bools)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new PacketWriter(buffer);
        writer.WriteInt32(7);
        writer.WriteWideStringPrefixed("rel_prologue_01");
        writer.WriteInt32(11);
        writer.WriteInt32(13);
        writer.WriteInt32(3);
        for (var i = 0; i < bools; i++)
            writer.WriteBool32(false);
        Assert.False(TutorialWire.EntryNeedsScoreboard(TestBuilds.Packets, build));
        Assert.Equal(buffer.WrittenSpan.ToArray(), TutorialWire.WriteRoomEnterGame(
            TestBuilds.Packets, build, 7, "rel_prologue_01", 11, 13, 3));
    }

    [Theory]
    [InlineData("cn-324")]
    [InlineData("cn-279")]
    public void EarlyCnEntriesRetainTheExactScoreBlockWithoutAScalarTail(string build)
    {
        var scoreValues = PlayRoomCodec.ScoreboardValues(TestBuilds.Packets, build,
            new RoundScoreboard(0, 0, [0, 0], []));
        var body = TutorialWire.WriteRoomEnterGame(TestBuilds.Packets, build, 0, "ab", unknown4: 3, scoreboardValues: scoreValues);
        Assert.Equal(new byte[] { 0, 0, 0, 0, 2, 0, 0, 0, 97, 0, 98, 0 }, body[..12]);
        Assert.Equal(BubbleFighter.Fantasy.Packets.PacketCodec.Write(TestBuilds.Packets, build, "ScoreData", scoreValues), body[12..]);
        Assert.Throws<InvalidOperationException>(() => TutorialWire.WriteRoomEnterGame(
            TestBuilds.Packets, build, 0, "ab"));
    }

    [Fact]
    public void Id2014KeepsItsDefaultScoreBlockBeforeTheThreeWordSuffix()
    {
        var body = TutorialWire.WriteRoomEnterGame(TestBuilds.Packets, "id-400",
            0, "", 11, 13, 3);
        Assert.Equal(2272, body.Length);
        Assert.Equal(17, BitConverter.ToInt32(body, 8));
        Assert.Equal(-1, BitConverter.ToInt32(body, 20));
        Assert.Equal(-1, BitConverter.ToInt32(body, 300));
        // Prior encoder's complete block, with only the sixteen native sentinel words corrected.
        Assert.Equal("631BDA1B50470B4E136ACACDFFE6F9D27D0850ECEC88ED498763D35E554EC09C",
            Convert.ToHexString(SHA256.HashData(body.AsSpan(8, 2252))));
        Assert.Equal(11, BitConverter.ToInt32(body, body.Length - 12));
        Assert.Equal(13, BitConverter.ToInt32(body, body.Length - 8));
        Assert.Equal(3, BitConverter.ToInt32(body, body.Length - 4));
    }

    [Fact]
    public void Cn2013KeepsItsOwnDefaultScoreBlockBeforeTheSingleWordSuffix()
    {
        // cn-406 sub_6FF0A0 reads 16 teams, eight rows and eight tail words;
        // cn-406 sub_6FEA10 resets both sentinel words in each row to all-one bits.
        var expected = new byte[2088];
        BitConverter.GetBytes(16).CopyTo(expected, 8);
        BitConverter.GetBytes(-1).CopyTo(expected, 20);
        for (var row = 0; row < 8; row++)
        {
            var position = 8 + 276 + row * 221;
            BitConverter.GetBytes(-1).CopyTo(expected, position);
            BitConverter.GetBytes(-1).CopyTo(expected, position + 193);
        }
        BitConverter.GetBytes(11).CopyTo(expected, 2084);
        Assert.Equal(expected, TutorialWire.WriteRoomEnterGame(
            TestBuilds.Packets, "cn-406", 0, "", 11, 13, 3));
    }

    [Theory]
    [InlineData("kr-812", false, false)]
    [InlineData("cn-717", false, false)]
    [InlineData("cn-324", false, true)]
    [InlineData("cn-279", false, true)]
    [InlineData("id-400", true, false)]
    [InlineData("cn-406", true, false)]
    [InlineData("id-1809", false, false)]
    [InlineData("cn-706", false, false)]
    public void CompleteCompositionMatchesIndependentNativeSections(string build, bool initialScore, bool scoreboard)
    {
        var values = new Dictionary<string, object?>
        {
            ["unknown_4"] = 7, ["map_key"] = "m",
            ["unknown_36"] = 11, ["unknown_40"] = 13, ["unknown_44"] = 4,
            ["unknown_3204"] = 11, ["unknown_3208"] = 13, ["unknown_3212"] = 4,
            ["unknown_3028"] = 11,
        };
        var board = new RoundScoreboard(0, 1, [10, 20], []);
        var expected = new ArrayBufferWriter<byte>();
        var writer = new PacketWriter(expected);
        BubbleFighter.Fantasy.Packets.PacketCodec.Write(writer, TestBuilds.Packets, build, "RoomEnterGameHeader", values);
        if (initialScore)
            BubbleFighter.Fantasy.Packets.PacketCodec.Write(writer, TestBuilds.Packets, build, "ScoreData",
                PlayRoomCodec.ScoreboardValues(TestBuilds.Packets, build, new RoundScoreboard(0, 0, [0, 0], [])));
        if (scoreboard)
            writer.WriteBytes(PlayRoomCodec.WriteScoreboard(TestBuilds.Packets, build, board));
        else
            BubbleFighter.Fantasy.Packets.PacketCodec.Write(writer, TestBuilds.Packets, build, "RoomEnterGameTail", values);
        Assert.Equal(expected.WrittenSpan.ToArray(), TutorialWire.WriteRoomEnterGame(
            TestBuilds.Packets, build, 7, "m", 11, 13, 4,
            scoreboard ? PlayRoomCodec.ScoreboardValues(TestBuilds.Packets, build, board) : null));
        Assert.Equal(scoreboard, TutorialWire.EntryNeedsScoreboard(TestBuilds.Packets, build));
    }

    [Fact]
    public void CompleteEntryRejectsBodiesBeyondTheNativeFrameBudget()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TutorialWire.WriteRoomEnterGame(
            TestBuilds.Packets, "kr-812", 0, new string('x', ushort.MaxValue)));
    }

}
