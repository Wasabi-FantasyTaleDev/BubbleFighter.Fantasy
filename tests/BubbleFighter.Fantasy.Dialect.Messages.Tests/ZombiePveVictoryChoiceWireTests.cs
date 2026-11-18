using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins the preselected display, the alternative displays and the text-only notification of the four-box result.</summary>
public sealed class ZombiePveVictoryChoiceWireTests
{
    /// <summary>Checks each build's game opcodes and exact three bodies, without installing a Horde route or processing a grant.</summary>
    [Theory]
    [InlineData("kr-812", 929, 734, 743)]
    [InlineData("kr-1256", 926, 617, 621)]
    [InlineData("cn-774", 926, 632, 643)]
    [InlineData("tw-454", 926, 632, 643)]
    public async Task TerminalSendersDeliverOnlyTheRequestedBody(string build, int rank, int open, int follow)
    {
        var profile = new ClientProfile(build, build[..2], int.Parse(build[3..]), 0, 0, build);
        var root = Directory.GetParent(TestBuilds.PacketsDirectory())!.FullName;
        var table = OpcodeTableLoader.Load(Path.Combine(root, "profiles", build, "opcodes.yaml"), profile)
            .ForConnection(OpcodeConnection.Game);
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var protocol = build + "/game";
        var context = fixture.Context(build, protocol, "dialect.rooms");
        var session = new MatchMessagesMountTests.Session(profile);
        try
        {
            new ZombiePveVictoryChoiceBinding(protocol, table, TestBuilds.All.Tables, build).MountIfAudited(context);
            Assert.Empty(session.Sent);
            Assert.Empty(fixture.Packets.Handlers);
            Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundLotteryOpened>>());
            var ranks = Assert.Single(fixture.Services.GetAll<IMessageSender<RoundZombiePveVictoryRanks>>());
            var opener = Assert.Single(fixture.Services.GetAll<IMessageSender<RoundZombiePveVictoryOpened>>());
            var following = Assert.Single(fixture.Services.GetAll<IMessageSender<RoundZombiePveVictoryFollowUp>>());
            await ranks.SendAsync(session, new([new(2, 37, 1)], 0x01020304, 17, 42, 9), default);
            await opener.SendAsync(session, new(), default);
            await following.SendAsync(session, new(0x89ABCDEF, 0x01020304, 0xFEDCBA98), default);
            Assert.Equal(new ushort[] { (ushort)rank, (ushort)open, (ushort)follow }, session.Sent.ToArray());
            Assert.Equal("0403020103000000110000002A00000009000000010000000200000025000000000000000000000001000000",
                Convert.ToHexString(session.Bodies[0]));
            Assert.Empty(session.Bodies[1]);
            Assert.Equal("EFCDAB890403020198BADCFE", Convert.ToHexString(session.Bodies[2]));
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundZombiePveVictoryRanks>>());
        Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundZombiePveVictoryOpened>>());
        Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundZombiePveVictoryFollowUp>>());
    }

    /// <summary>Inheritance of a layout or a catalog name does not authorize a terminal sender in an unaudited region.</summary>
    [Fact]
    public void OtherBuildsDoNotRegisterTerminalSenders()
    {
        var tables = TestBuilds.All.Tables;
        foreach (var build in tables.Builds.Except(new[] { "kr-812", "kr-1256", "cn-774", "tw-454" }))
        {
            var profile = new ClientProfile(build, build[..2], int.Parse(build[3..]), 0, 0, build);
            var table = new OpcodeTable(profile, []);
            using var fixture = new MatchMessagesMountTests.Fixture(table);
            var protocol = build + "/game";
            new ZombiePveVictoryChoiceBinding(protocol, table, tables, build).MountIfAudited(fixture.Context(build, protocol, "dialect.rooms"));
            Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundZombiePveVictoryRanks>>());
            Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundZombiePveVictoryOpened>>());
            Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundZombiePveVictoryFollowUp>>());
        }
    }

    /// <summary>The chosen display precedes the alternatives; a separately counted five-word rank row follows.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-324")]
    [InlineData("cn-404")]
    [InlineData("cn-406")]
    [InlineData("cn-408")]
    [InlineData("cn-705")]
    [InlineData("cn-706")]
    [InlineData("cn-717")]
    [InlineData("cn-735")]
    [InlineData("cn-738")]
    [InlineData("cn-742")]
    [InlineData("id-400")]
    [InlineData("id-1809")]
    [InlineData("tw-410")]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void ChoiceSnapshotPreservesTheWinnerAndAlternativeOrder(string build)
    {
        var tables = TestBuilds.All.Tables;
        RoundPvePointRow[] rows = [new(2, 37, 1)];
        var body = PvePointsBinding.EncodeZombiePveVictoryRanks(tables, build, rows, 0x01020304, [17, 42, 9]);
        Assert.Equal(44, body.Length);
        Assert.Equal("0403020103000000110000002A00000009000000" +
            "010000000200000025000000000000000000000001000000", Convert.ToHexString(body));
        Assert.Equal(body, PacketCodec.Write(tables, build, "PveRankInfo", PacketCodec.Read(tables, build, "PveRankInfo", body)));
        Assert.Equal("0000000000000000010000000200000025000000000000000000000001000000",
            Convert.ToHexString(PvePointsBinding.EncodeRanks(tables, build, rows)));
    }

    /// <summary>An absent prize, empty box or wrong alternative count cannot produce a victory reveal.</summary>
    [Fact]
    public void IncompleteChoiceIsNotEncoded()
    {
        var tables = TestBuilds.All.Tables;
        foreach (int[] alternatives in new int[][] { [], [1, 2], [1, 2, 3, 4], [0, 2, 3], [1, -1, 3], [1, 2, 0] })
            Assert.Throws<InvalidOperationException>(() => PvePointsBinding.EncodeZombiePveVictoryRanks(tables, "kr-812", [], 7, alternatives));
        Assert.Throws<InvalidOperationException>(() => PvePointsBinding.EncodeZombiePveVictoryRanks(tables, "kr-812", [], 0, [1, 2, 3]));
    }

    /// <summary>The terminal snapshot fits exactly 32 native rows; a 33rd row or an out-of-range seat fails before sending.</summary>
    [Fact]
    public void TerminalRankRowsHaveANativeUpperBound()
    {
        var tables = TestBuilds.All.Tables;
        var rows = Enumerable.Range(0, 32).Select(seat => new RoundPvePointRow(seat, 0, 1)).ToArray();
        Assert.Equal(664, PvePointsBinding.EncodeZombiePveVictoryRanks(tables, "kr-812", rows, 1, [2, 3, 4]).Length);
        Assert.Throws<InvalidOperationException>(() =>
            PvePointsBinding.EncodeZombiePveVictoryRanks(tables, "kr-812", [.. rows, rows[0]], 1, [2, 3, 4]));
        Assert.Throws<InvalidOperationException>(() =>
            PvePointsBinding.EncodeZombiePveVictoryRanks(tables, "kr-812", [new(32, 0, 1)], 1, [2, 3, 4]));
    }

    /// <summary>Counted UTF-16 consumes the entire notification; neither a box index nor a trailing item ID belongs to it.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-735")]
    [InlineData("cn-738")]
    [InlineData("cn-742")]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void ClientNotifiesTextOnly(string build)
    {
        var tables = TestBuilds.All.Tables;
        const string structure = "ZombiePveCompensationText";
        var body = PacketCodec.Write(tables, build, structure, new Dictionary<string, object?> { ["message"] = "A\uAC00" });
        Assert.Equal("02000000410000AC", Convert.ToHexString(body));
        Assert.Equal("A\uAC00", PacketCodec.Read(tables, build, structure, body)["message"]);
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(tables, build, structure, body[..^1]));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(tables, build, structure, [.. body, 0, 0, 0, 0]));
    }
}
