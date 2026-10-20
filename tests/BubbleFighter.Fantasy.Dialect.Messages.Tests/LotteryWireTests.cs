using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Dialect.Rooms;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins native card selection and reveal bodies to the CN2012 consuming paths.</summary>
public sealed class LotteryWireTests
{
    /// <summary>The reward icon comes from the primary ScoreData field consumed at native row +168.</summary>
    [Fact]
    public void SelectedSaleIdentityTravelsInTheScoreboard()
    {
        var (schema, play) = TestBuilds.Of("cn-324");
        var field = schema.ScoreRow.Single(value => value.Name == "award_display_id");
        Assert.Equal(168, field.Offset);
        var row = new BubbleFighter.Fantasy.Contracts.Match.RoundScoreboardRow(0, "player", 2, 1,
            new(0, 0, 0, 0, 0), null) { AwardDisplayId = 1013192 };
        var body = PlayRoomCodec.WriteScoreRowBody(TestBuilds.Packets, "cn-324", [field], row);
        Assert.Equal(4, body.Length);
        Assert.Equal(1013192, BitConverter.ToInt32(body));
    }

    /// <summary>Reads the clicked card followed by the claimed seat and rejects extra or missing words.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-324")]
    [InlineData("cn-279")]
    [InlineData("cn-717")]
    [InlineData("id-400")]
    [InlineData("id-1809")]
    [InlineData("cn-706")]
    public void SelectionRequestContainsCardThenSeat(string build)
    {
        var tables = TestBuilds.All.Tables;
        var body = Convert.FromHexString("0900000003000000");
        var request = LotteryBinding.Read(tables, build, body);
        Assert.Equal(9, request.Card);
        Assert.Equal(3, request.Seat);
        Assert.Throws<PacketFormatException>(() => LotteryBinding.Read(tables, build, body[..4]));
        Assert.Throws<PacketFormatException>(() => LotteryBinding.Read(tables, build, [.. body, 0]));
    }

    /// <summary>The third success word is an absent second card, and reveal is exactly ten sale identities.</summary>
    [Fact]
    public void SuccessAndRevealPinNativeCardSemantics()
    {
        var tables = TestBuilds.All.Tables;
        var body = ScalarGolden.Write(tables.Resolve("cn-324", "HordeLotterySelected"),
            new() { ["card"] = 9, ["seat"] = 3 });
        Assert.Equal(Convert.FromHexString("0900000003000000FFFFFFFF"), body);
        var ids = Enumerable.Range(1013060, 10).ToArray();
        ids[0] = 0; // Native reveal skips claimed cards, retaining their player names and highlight.
        var reveal = LotteryBinding.WriteReveal(tables, "cn-324", ids);
        Assert.Equal(44, reveal.Length);
        Assert.Equal(10, BitConverter.ToInt32(reveal));
        for (var i = 0; i < 10; i++) Assert.Equal(ids[i], BitConverter.ToInt32(reveal, 4 + 4 * i));
        Assert.Throws<BubbleFighter.Fantasy.Abstractions.ConfigException>(() =>
            LotteryBinding.WriteReveal(tables, "cn-324", ids[..9]));
    }

    /// <summary>CN2011 consumes the success notification as card and seat only.</summary>
    [Fact]
    public void Cn2011SelectionSuccessHasNoSecondCard()
    {
        var fields = TestBuilds.All.Tables.Resolve("cn-279", "HordeLotterySelected");
        var body = ScalarGolden.Write(fields, new() { ["card"] = 4, ["seat"] = 1 });
        Assert.Equal(Convert.FromHexString("0400000001000000"), body);
    }
}
