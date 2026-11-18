using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Dialect.Rooms;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins both request stamps, both reply routes and the complete native pages for every audited build.</summary>
public sealed class PirateAssaultRankingWireTests
{
    [Theory]
    [InlineData("kr-1256", 2081, 2082, 1554, 1555)]
    [InlineData("cn-774", 2079, 2080, 1551, 1552)]
    [InlineData("tw-454", 2075, 2076, 1549, 1550)]
    public async Task RoutesAndCompletePagesPreserveTheSourceOrder(string build, int top, int personal, int topReply, int personalReply)
    {
        var table = TestBuilds.Opcodes(build);
        var packets = TestBuilds.Packets;
        Assert.Equal((ushort)top, table.C2S("gcpReqPirateAssaultTopRanking_"));
        Assert.Equal((ushort)personal, table.C2S("gcpReqPirateAssaultUserRanking_"));
        var protocol = build + "/game";
        var binding = new PirateAssaultRankingBinding(protocol, table, packets, build);
        var ask = Convert.FromHexString("E9030000");
        var session = new MatchMessagesMountTests.Session(table.Profile);
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var context = fixture.Context(build, protocol, "dialect.rooms");
        try
        {
            binding.Mount(context);
            await fixture.Packets.DispatchAsync(protocol, session, new((ushort)top, ask), default);
            await fixture.Packets.DispatchAsync(protocol, session, new((ushort)personal, ask), default);
            Assert.Equal(new PirateAssaultRankingRequested(build, 1001, false), fixture.Messages.Received[0]);
            Assert.Equal(new PirateAssaultRankingRequested(build, 1001, true), fixture.Messages.Received[1]);
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(fixture.Packets.Handlers);
        Assert.Equal(uint.MaxValue, PirateAssaultRankingBinding.Read(packets, build, [255, 255, 255, 255], false).RankId);
        Assert.Throws<PacketFormatException>(() => PirateAssaultRankingBinding.Read(packets, build, ask[..^1], false));
        Assert.Throws<PacketFormatException>(() => PirateAssaultRankingBinding.Read(packets, build, [.. ask, 0], true));

        await binding.SendAsync(session, new(new(build, 1001, null), 45000.25,
            [new(0xFEDCBA98, "\u6d77\U00010400", 0x11223344, 17, ClearTimeMilliseconds: 23456, Rank: 8),
                new(2, "A", 7, 9, ClearTimeMilliseconds: 12345, Rank: 3)]), default);
        await binding.SendAsync(session, new(new(build, 1001, 10), 45000.25, []), default);
        Assert.Equal(new[] { (ushort)topReply, (ushort)personalReply }, session.Sent);
        Assert.Equal("E90300000200000098BADCFE03000000776D01D800DC4433221111000000A05B000008000000"
            + "02000000010000004100070000000900000039300000030000000000000008F9E540", Convert.ToHexString(session.Bodies[0]));
        Assert.Equal("E9030000000000000000000008F9E540", Convert.ToHexString(session.Bodies[1]));
    }

    [Fact]
    public async Task WithNoAuthoritativeSourceNoNativeListenerIsOpened()
    {
        const string build = "kr-1256";
        using var fixture = new MatchMessagesMountTests.Fixture(TestBuilds.Opcodes(build));
        var context = fixture.Context(build, build + "/game", "dialect.rooms",
            extra: new() { ["modern_rooms"] = true, ["pirate_assault_rankings"] = true });
        try
        {
            var refusal = await Assert.ThrowsAsync<ConfigException>(() => new RoomsPlugin().ActivateAsync(context, default).AsTask());
            Assert.Contains("IPirateAssaultRankingSource", refusal.Message);
            Assert.Empty(fixture.Packets.Handlers);
        }
        finally { await context.UnwindAsync(); }
    }

    [Fact]
    public void OldClientsDoNotInheritTheBoardLayouts()
    {
        foreach (var build in new[] { "kr-812", "cn-717", "tw-410" })
            foreach (var layout in new[] { "PirateAssaultRankingRequest", "PirateAssaultRankingReply", "PirateAssaultRankingRow" })
                Assert.Empty(TestBuilds.Packets.Resolve(build, layout));
    }
}
