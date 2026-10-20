using System.Buffers;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Checks the complete CN2013 scoreboard against its own native fixed loops and variable fields.</summary>
public sealed class Cn406ScoreboardTests
{
    [Fact]
    public void VacantScoreboardMatchesTheNativeInitialBlockAndEveryRowSentinel()
    {
        // cn-406 sub_6FF0A0 reads 16 team rows, eight player rows and eight tail words.
        var expected = new byte[2076];
        BitConverter.GetBytes(16).CopyTo(expected, 0);
        BitConverter.GetBytes(-1).CopyTo(expected, 12);
        for (var row = 0; row < 8; row++)
        {
            BitConverter.GetBytes(-1).CopyTo(expected, 276 + row * 221);
            BitConverter.GetBytes(-1).CopyTo(expected, 276 + row * 221 + 193);
        }
        var body = PlayRoomCodec.WriteScoreboard(TestBuilds.Packets, "cn-406",
            new RoundScoreboard(0, 0, [0, 0], []));
        Assert.Equal(expected, body);
        Assert.Equal(expected, TutorialWire.WriteRoomEnterGame(TestBuilds.Packets, "cn-406", 0, "")[8..^4]);
    }

    [Theory]
    [InlineData("", false, 2076)]
    [InlineData("Fighter", true, 2112)]
    public void RewardedScoreboardPinsAllBytesIncludingVariableStrings(string nickname, bool includeBadge, int length)
    {
        var badges = includeBadge ? new[] { "bonus.dds" } : Array.Empty<string>();
        var row = new RoundScoreboardRow(0, nickname, 2, 12,
            new RoundSeatStats(0, Kills: 7, Knockdowns: 13, Score: 29, Deaths: 0),
            new RoundRewardRow(12, 100, 123, 200, 21, 43, false, badges));
        var body = PlayRoomCodec.WriteScoreboard(TestBuilds.Packets, "cn-406",
            new RoundScoreboard(0, 2, [11, 1], [row]));

        var expected = new ArrayBufferWriter<byte>();
        var writer = new PacketWriter(expected);
        var header = new byte[276];
        Put(header, 0, 16);
        Put(header, 12, 2);
        Put(header, 24, 11);
        Put(header, 40, 1);
        writer.WriteBytes(header);

        // Native wire offsets with both strings and the badge vector empty.
        // These are independent of the packet table and include bool32 expansion.
        var occupied = new byte[221];
        Put(occupied, 12, 2);
        Put(occupied, 16, 12);
        Put(occupied, 20, 13);
        Put(occupied, 24, 7);
        Put(occupied, 40, 29);
        Put(occupied, 108, 100);
        Put(occupied, 112, 123);
        Put(occupied, 116, 200);
        Put(occupied, 120, 21);
        Put(occupied, 124, 43);
        occupied[128] = 1; // row +164: the seat is in the round
        Put(occupied, 193, -1);
        writer.WriteBytes(occupied.AsSpan(0, 8));
        writer.WriteWideStringPrefixed(nickname);
        writer.WriteBytes(occupied.AsSpan(12, 145));
        writer.WriteUInt32((uint)badges.Length);
        foreach (var badge in badges)
            writer.WriteWideStringPrefixed(badge);
        writer.WriteBytes(occupied.AsSpan(161));
        var vacant = new byte[221];
        Put(vacant, 0, -1);
        Put(vacant, 193, -1);
        for (var slot = 1; slot < 8; slot++)
            writer.WriteBytes(vacant);
        writer.WriteBytes(new byte[32]);
        Assert.Equal(length, body.Length);
        Assert.Equal(expected.WrittenSpan.ToArray(), body);
    }

    private static void Put(byte[] bytes, int offset, int value) =>
        BitConverter.GetBytes(value).CopyTo(bytes, offset);
}
