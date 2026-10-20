using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Protocol;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins the native counted cooperative item list, use index, and effect confirmation.</summary>
public sealed class CountedItemsTests
{
    /// <summary>Routes the native three-slot index and sends audited list/effect bodies through installed services.</summary>
    [Theory]
    [InlineData("kr-812", false)]
    [InlineData("cn-717", false)]
    [InlineData("id-400", false)]
    [InlineData("id-1809", false)]
    [InlineData("kr-812", true)]
    public async Task ThreeSlotSurfaceUsesReviewedOpcodesAndUnloads(string build, bool remap)
    {
        var profile = new ClientProfile(build, build[..2], int.Parse(build[3..]), 0, 0, build);
        var root = Directory.GetParent(TestBuilds.PacketsDirectory())!.FullName;
        var full = OpcodeTableLoader.Load(Path.Combine(root, "profiles", build, "opcodes.yaml"), profile).ForConnection(OpcodeConnection.Game);
        var table = remap ? new OpcodeTable(profile, full.All.Select(entry => entry with { Value = checked((ushort)(entry.Value + 1000)) })) : full;
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var protocol = build + "/game";
        var context = fixture.Context(build, protocol, "dialect.rooms");
        var session = new MatchMessagesMountTests.Session(profile);
        try
        {
            new CountedItemsBinding(protocol, table, TestBuilds.All.Tables, build).Mount(context, fixture.Messages);
            async Task Dispatch(byte[] body) => await fixture.Packets.DispatchAsync(protocol, session,
                new InboundPacket(table.C2S("gcpPveUseCountItem"), body), default);
            await Dispatch(BitConverter.GetBytes(3));
            Assert.Equal(new RoundCountedItemUseRequested(3), fixture.Messages.Received[^1]);
            await Assert.ThrowsAsync<PacketFormatException>(() => Dispatch(new byte[8]));
            fixture.Messages.HasHandler = false;
            await Assert.ThrowsAsync<InvalidOperationException>(() => Dispatch(BitConverter.GetBytes(1)));
            await Assert.Single(fixture.Services.GetAll<IMessageSender<RoundCountedItemsPrepared>>())
                .SendAsync(session, new([229, 146, 136]), default);
            Assert.Equal(table.S2C("gspPveItemInfo"), session.Sent[^1]);
            Assert.Equal(52, session.Bodies[^1].Length);
            Assert.Equal("0400000000000000000000000000000000000000E500000001000000000000009200000002000000000000008800000003000000",
                Convert.ToHexString(session.Bodies[^1]));
            await Assert.Single(fixture.Services.GetAll<IMessageSender<RoundCountedItemUsed>>())
                .SendAsync(session, new(2, 229), default);
            Assert.Equal(table.S2C("gspPveItemUsed"), session.Sent[^1]);
            Assert.Equal("02000000E500000000000000", Convert.ToHexString(session.Bodies[^1]));
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(fixture.Packets.Handlers);
        Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundCountedItemsPrepared>>());
        Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundCountedItemUsed>>());
    }

    /// <summary>Writes a vector count and three words per native slot entry.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-324")]
    [InlineData("cn-717")]
    [InlineData("id-400")]
    [InlineData("id-1809")]
    [InlineData("cn-706")]
    public void PreparedListHasAWordCountAndTwelveBytesPerEntry(string build)
    {
        var body = CountedItemsBinding.EncodePrepared(TestBuilds.All.Tables, build, [120, 102, 101, 146, 136]);
        Assert.Equal(76, body.Length);
        Assert.Equal(6, BitConverter.ToInt32(body));
        Assert.Equal(new byte[12], body.AsSpan(4, 12).ToArray());
        Assert.Equal("000000007800000001000000", Convert.ToHexString(body.AsSpan(16, 12)));
        Assert.Equal("000000008800000005000000", Convert.ToHexString(body.AsSpan(64, 12)));
    }

    /// <summary>Rejects truncated and trailing use data and preserves the UI slot index.</summary>
    [Fact]
    public void RequestIsOneSlotWordAndConfirmationHasThreeWords()
    {
        var tables = TestBuilds.All.Tables;
        Assert.Equal(4, PacketCodec.ReadScalars(tables, "cn-324", "PveItemUseRequest", BitConverter.GetBytes(4))["item_slot"]);
        Assert.Throws<PacketFormatException>(() => PacketCodec.ReadScalars(tables, "cn-324", "PveItemUseRequest", new byte[3]));
        Assert.Throws<PacketFormatException>(() => PacketCodec.ReadScalars(tables, "cn-324", "PveItemUseRequest", new byte[8]));
        var used = ScalarGolden.Write(TestBuilds.All.Tables.Resolve("cn-324", "PveItemUsed"),
            new() { ["seat"] = 1, ["kind"] = 146 });
        Assert.Equal(12, used.Length);
        Assert.Equal("010000009200000000000000", Convert.ToHexString(used));
    }
}
