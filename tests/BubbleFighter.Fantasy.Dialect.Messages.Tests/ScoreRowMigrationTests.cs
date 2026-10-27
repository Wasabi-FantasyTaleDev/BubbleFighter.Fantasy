using System.Security.Cryptography;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins complete scoreboard bodies and the audited CN2015 header correction.</summary>
public sealed class ScoreRowMigrationTests
{
    // Fixtures follow audited native readers, including CN2011's progress/display correction.
    [Theory]
    [InlineData("kr-812", 4500, "85757B21EBA83E2E5B47CE006760CA22F75733194702BD5A300B7D3163BEAD8D", 4536, "9B97BB7AABBF06AA6F2912C0A9B0728D569E68C6F358EFC3083A2A14FF1BC298")]
    [InlineData("cn-717", 4492, "4386775334E5872F21730EF4BCD472E1B3B71F9DD7A51CDB5AFAC1A84B7DFC1A", 4528, "86EBEA8A1D95681C6CA01BDDA9ED7F99B813F8D86530A68D734374123EE4B1EA")]
    [InlineData("id-400", 2252, "B0FA06DFD52E0002762A239A5C2FEDC1C38B717690A15447E211C2300900762C", 2288, "2E12B65799D9CF0BCCE440BBA5F21D5435D5E13B62336FEE206D534773DA88AC")]
    [InlineData("id-1809", 4276, "14D6141D0C7625B5407712BBA2A7482824A0BB3CD71E84047C88B1FEA9FEBA4C", 4312, "2F81DECFBDE09F52ACA274D046C6E11B7D2EA8DAAA946DEC0A37B3F554497229")]
    [InlineData("cn-706", 3328, "89FE34E4C6874AC10610657291729A193BDAF0C6F528F6327721D3D24AF9892B", 3364, "43CDC1782B8602F835FCEAA3A6DAF47E056D178A6E4E2F3F8CAEF8477FEAD4AB")]
    [InlineData("cn-324", 1996, "9256FC5195F0B07A09ABC0C535606F91F204DE281D0EFB3C946918D2F2246EC6", 2032, "FF3FD9205DB8F6A5F050BA6A0F86E224F4548D4B3F6D9295015450DB30321622")]
    [InlineData("cn-279", 1408, "B89665D58E69B4E326A9F8948E81CC19D2D8F7536DB8251C347051C0E4FBC0FB", 1422, "57FBD7AD555E72442496DFFFD1266E76A33505C5B161BC5C6D18A9110DC73062")]
    public void FullBodiesPinVacantAndRewardedRows(string build, int vacantLength, string vacantHash,
        int occupiedLength, string occupiedHash)
    {
        var vacant = new RoundScoreboard(0, 0, [11, 1], []);
        var row = new RoundScoreboardRow(0, "Fighter", 2, 12,
            new RoundSeatStats(0, Kills: 7, Knockdowns: 13, Score: 29, Deaths: 0),
            new RoundRewardRow(12, 100, 123, 200, 21, 43, false, ["bonus.dds"]))
        {
            AwardDisplayId = 1013192,
        };
        var occupied = vacant with { Rows = [row] };

        AssertBody(vacant, vacantLength, vacantHash);
        AssertBody(occupied, occupiedLength, occupiedHash);

        void AssertBody(RoundScoreboard message, int length, string hash)
        {
            var body = PlayRoomCodec.WriteScoreboard(TestBuilds.Packets, build, message);
            Assert.Equal(length, body.Length);
            Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(body)));
        }
    }
    [Fact]
    public void Cn2015HeaderPlacesWinnerAndFlagImmediatelyAfterTeamCount()
    {
        // cn-717 sub_4195B0: count, winner, bool, 17 four-word team rows, 16 player rows.
        var body = PlayRoomCodec.WriteScoreboard(TestBuilds.Packets, "cn-717",
            new RoundScoreboard(0, 2, [11, 1], []));
        Assert.Equal(17, BitConverter.ToInt32(body));
        Assert.Equal(2, BitConverter.ToInt32(body, 4));
        Assert.Equal(0, BitConverter.ToInt32(body, 8));
        Assert.Equal(11, BitConverter.ToInt32(body, 16));
        Assert.Equal(1, BitConverter.ToInt32(body, 32));
        Assert.Equal(-1, BitConverter.ToInt32(body, 12 + 17 * 16));
        Assert.DoesNotContain(TestBuilds.Packets.Resolve("cn-717", "ScoreData"),
            field => field.Type == "empty_list");
        Assert.Equal(2, TestBuilds.Packets.Resolve("cn-735", "ScoreData")
            .Count(field => field.Type == "empty_list"));
    }

    [Theory]
    [InlineData("id-400", 8, 241, 1)]
    [InlineData("id-1809", 16, 241, 2)]
    [InlineData("cn-706", 12, 245, 2)]
    public void NativeFixedRowsAndTailsEndExactlyAtTheBodyBoundary(string build, int seats, int rowBytes, int tailWords)
    {
        // id-400 sub_781180, id-1809 sub_415730 and cn-706 sub_786E90 use these fixed loops.
        var body = PlayRoomCodec.WriteScoreboard(TestBuilds.Packets, build,
            new RoundScoreboard(0, 2, [11, 1], []));
        const int rowStart = 20 + 17 * 16;
        Assert.Equal(2, BitConverter.ToInt32(body, 12));
        Assert.Equal(11, BitConverter.ToInt32(body, 24));
        Assert.Equal(1, BitConverter.ToInt32(body, 40));
        for (var slot = 0; slot < seats; slot++)
            Assert.Equal(-1, BitConverter.ToInt32(body, rowStart + slot * rowBytes));
        var tailStart = rowStart + seats * rowBytes;
        Assert.Equal(tailStart + seats * tailWords * 4, body.Length);
        for (var slot = 0; slot < seats; slot++)
        {
            Assert.Equal(0, BitConverter.ToInt32(body, tailStart + slot * tailWords * 4));
            if (tailWords == 2)
                Assert.Equal(-1, BitConverter.ToInt32(body, tailStart + slot * tailWords * 4 + 4));
        }
    }

    [Theory]
    [InlineData("id-400")]
    [InlineData("id-1809")]
    [InlineData("cn-706")]
    public void SeatedRowIsMarkedPresentSoTheResultDoesNotShowItAsGone(string build)
    {
        // The room-intrude handler sets row +164 for a seat in the round (id-400 sub_78EE50), and the result
        // screen leaves a row without it out (id-400 sub_6DE480). In the written row the flag follows slot,
        // unknown_4, the nickname, 24 words and the five experience/Lucci words.
        var row = new RoundScoreboardRow(0, "Fighter", 1, 12, new RoundSeatStats(0, 0, 0, 0, 0), null);
        var body = PlayRoomCodec.WriteScoreboard(TestBuilds.Packets, build, new RoundScoreboard(0, 0, [0, 0], [row]));
        const int rowStart = 20 + 17 * 16;
        var flag = rowStart +4 + 4 + (4 + 2 * "Fighter".Length) + 24 * 4 + 5 * 4;
        Assert.Equal(1, body[flag]);
    }

    [Fact]
    public void Id2014VacantScoreboardSharesTheNativeInitialBlockDefaults()
    {
        var body = PlayRoomCodec.WriteScoreboard(TestBuilds.Packets, "id-400",
            new RoundScoreboard(0, 0, [0, 0], []));
        Assert.Equal(PacketCodec.Write(TestBuilds.Packets, "id-400", "ScoreData",
            PlayRoomCodec.ScoreboardValues(TestBuilds.Packets, "id-400", new RoundScoreboard(0, 0, [0, 0], []))), body);
        Assert.Equal(PlayValues.TimeOverEndBody.Scoreboard, TestBuilds.Of("id-400").Play.Score.TimeOverEnd);
        Assert.Equal(PlayValues.TimeOverEndBody.TeamTotals, TestBuilds.Of("cn-706").Play.Score.TimeOverEnd);
    }
}
