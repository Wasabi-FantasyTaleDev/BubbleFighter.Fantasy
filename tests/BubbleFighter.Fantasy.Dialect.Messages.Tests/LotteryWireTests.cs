using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins native card selection and reveal bodies to the CN2012 consuming paths.</summary>
public sealed class LotteryWireTests
{
    /// <summary>The mounted modern scoreboard sender supplies only the KR dialog's proven primary reward field.</summary>
    [Theory]
    [InlineData("kr-1256", 244, 17, true)]
    [InlineData("cn-774", 250, 32, false)]
    [InlineData("tw-454", 243, 16, false)]
    public async Task MaskedScoreboardCarriesTheProvenAwardIdentity(string build, int rowLength, int tailLength, bool hasAward)
    {
        var profile = new ClientProfile(build, build[..2], int.Parse(build[3..]), 0, 0, build);
        var root = Directory.GetParent(TestBuilds.PacketsDirectory())!.FullName;
        var table = OpcodeTableLoader.Load(Path.Combine(root, "profiles", build, "opcodes.yaml"), profile)
            .ForConnection(OpcodeConnection.Game);
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var protocol = build + "/game";
        var context = fixture.Context(build, protocol, "dialect.rooms");
        try
        {
            new RoundResultBinding(protocol, table, TestBuilds.All.Tables, build, [101], NullLogger.Instance).Mount(context);
            var sender = Assert.Single(fixture.Services.GetAll<IMessageSender<RoundScoreboard>>());
            var row = new RoundScoreboardRow(0, "P", 1, 1, new(0, 0, 0, 0, 0), null) { UserNumber = 7 };
            var board = new RoundScoreboard(7, 0, [0, 0], [row], WithRewards: true) { ModeId = 101 };
            var empty = new MatchMessagesMountTests.Session(profile);
            var awarded = new MatchMessagesMountTests.Session(profile);
            await sender.SendAsync(empty, board, default);
            await sender.SendAsync(awarded, board with { Rows = [row with { AwardDisplayId = 1000711 }] }, default);
            var body = Assert.Single(awarded.Bodies);
            Assert.Equal(38 + rowLength + 8 + tailLength, body.Length);
            if (hasAward)
            {
                Assert.Equal(476, TestBuilds.All.Tables.Resolve(build, "ScoreRow")
                    .Single(field => field.Name == "award_display_id").Offset);
                var values = PacketCodec.Read(TestBuilds.All.Tables, build, "ScoreRow", body.AsSpan(38, rowLength));
                Assert.Equal(1000711, values["award_display_id"]);
                var before = PacketCodec.Read(TestBuilds.All.Tables, build, "ScoreRow",
                    Assert.Single(empty.Bodies).AsSpan(38, rowLength));
                Assert.Equal(0, before["award_display_id"]);
                var expected = Assert.Single(empty.Bodies).ToArray();
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(expected.AsSpan(181), 1000711);
                Assert.Equal(expected, body);
            }
            else
            {
                Assert.DoesNotContain(TestBuilds.All.Tables.Resolve(build, "ScoreRow"), field => field.Name == "award_display_id");
                Assert.Equal(Assert.Single(empty.Bodies), body);
            }
        }
        finally { await context.UnwindAsync(); }
    }

    /// <summary>The reward icon comes from each build's primary ScoreData field consumed by its award dialog.</summary>
    [Theory]
    [InlineData("cn-279", 124)]
    [InlineData("cn-324", 168)]
    [InlineData("cn-406", 220)]
    [InlineData("cn-705", 224)]
    [InlineData("cn-706", 224)]
    public void SelectedSaleIdentityTravelsInTheScoreboard(string build, int offset)
    {
        var (schema, play) = TestBuilds.Of(build);
        var field = schema.ScoreRow.Single(value => value.Name == "award_display_id");
        Assert.Equal(offset, field.Offset);
        var row = new BubbleFighter.Fantasy.Contracts.Match.RoundScoreboardRow(0, "player", 2, 1,
            new(0, 0, 0, 0, 0), null) { AwardDisplayId = 1013192 };
        var body = RoundResultWire.WriteScoreRowBody(TestBuilds.Packets, build, [field], row);
        Assert.Equal(4, body.Length);
        Assert.Equal(1013192, BitConverter.ToInt32(body));
        var complete = RoundResultWire.WriteScoreRowBody(TestBuilds.Packets, build, schema.ScoreRow, row);
        var values = PacketCodec.Read(TestBuilds.Packets, build, "ScoreRow", complete);
        Assert.Equal(1013192, values["award_display_id"]);
    }

    /// <summary>Reads the clicked card followed by the claimed seat and rejects extra or missing words.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("kr-1256")]
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
