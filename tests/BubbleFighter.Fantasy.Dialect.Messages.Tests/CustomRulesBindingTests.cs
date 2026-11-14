using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins custom-rule vectors, unsigned parameters, feedback widths and reversible named routes.</summary>
public sealed class CustomRulesBindingTests
{
    /// <summary>The native request preserves unsigned payload bits and requires both nested vector counts.</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    public void RequestHasBool32ThenCountedShortAndWordVectors(string build)
    {
        var binding = Binding(build);
        var body = Convert.FromHexString("0000000002000000010001000000960000000C00020000000200000004000000");
        Assert.Equal(32, body.Length);
        var request = binding.Read(body);
        Assert.Equal(build, request.Lineage);
        Assert.False(request.MatchesModeDefaults);
        Assert.Equal(new uint[] { 150 }, request.Values[0].Values);
        Assert.Equal((ushort)12, request.Values[1].ValueId);
        Assert.Equal(new uint[] { 2, 4 }, request.Values[1].Values);
        var highBit = binding.Read(Convert.FromHexString("00000000010000001F0001000000FFFFFFFF"));
        Assert.Equal(uint.MaxValue, Assert.Single(Assert.Single(highBit.Values).Values));
        var empty = Convert.FromHexString("0100000000000000");
        Assert.Equal(8, empty.Length);
        Assert.True(binding.Read(empty).MatchesModeDefaults);
        Assert.Empty(binding.Read(empty).Values);
        foreach (var invalid in new[] { body[..^1], body.Concat(new byte[1]).ToArray(),
                     Convert.FromHexString("0200000000000000"), Convert.FromHexString("00000000FFFFFFFF"),
                     Convert.FromHexString("0000000001000000010000000000"),
                     Convert.FromHexString("00000000020000000100010000009600000001000100000064000000") })
            Assert.Throws<PacketFormatException>(() => binding.Read(invalid));
    }

    /// <summary>Shared feedback supplies only the result; the dialect writes the unused native zero and empty fields.</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    public void FeedbackUsesNativeDefaultsForUnusedFields(string build)
    {
        var binding = Binding(build);
        Assert.Equal("0000000000000000", Convert.ToHexString(binding.Write(new(0))));
        var body = binding.Write(new(4));
        Assert.Equal(8, body.Length);
        Assert.Equal("0400000000000000", Convert.ToHexString(body));
        var decoded = PacketCodec.Read(TestBuilds.Packets, build, "CustomGameModeReply", body);
        Assert.Equal((ushort)4, decoded["result"]);
        Assert.Equal((ushort)0, decoded["unknown_2"]);
        Assert.Equal(string.Empty, decoded["unknown_4"]);
    }

    /// <summary>The packet table retains both shorts and counted UTF-16 text without exposing unused fields to room logic.</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    public void ReplyLayoutRetainsTwoShortsAndCountedText(string build)
    {
        var body = PacketCodec.Write(TestBuilds.Packets, build, "CustomGameModeReply", new Dictionary<string, object?>
        { ["result"] = (ushort)4, ["unknown_2"] = ushort.MaxValue, ["unknown_4"] = "AB" });
        Assert.Equal(12, body.Length);
        Assert.Equal("0400FFFF0200000041004200", Convert.ToHexString(body));
        var decoded = PacketCodec.Read(TestBuilds.Packets, build, "CustomGameModeReply", body);
        Assert.Equal((ushort)4, decoded["result"]);
        Assert.Equal((ushort)65535, decoded["unknown_2"]);
        Assert.Equal("AB", decoded["unknown_4"]);
    }

    /// <summary>Both route directions use verified profile names, survive remapping and unwind their effects.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RoutesUseProfileNamesAndUnwind(bool remap)
    {
        var full = TestBuilds.Opcodes("kr-1256").ForConnection(OpcodeConnection.Game);
        Assert.Equal(2086, full.C2S("gcpCustomGameModeSetting_"));
        Assert.Equal(1558, full.S2C("gspCustomGameModeSetting"));
        var table = remap ? new OpcodeTable(full.Profile,
            full.All.Select(row => row with { Value = unchecked((ushort)(row.Value + 1000)) })) : full;
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var context = fixture.Context("kr-1256", "kr-1256/game", "dialect.rooms");
        var session = new MatchMessagesMountTests.Session(full.Profile);
        try
        {
            new CustomRulesBinding("kr-1256/game", table, TestBuilds.Packets, "kr-1256", NullLogger.Instance).Mount(context);
            await fixture.Packets.DispatchAsync("kr-1256/game", session,
                new(table.C2S("gcpCustomGameModeSetting_"), Convert.FromHexString("0100000000000000")), default);
            Assert.IsType<RoomCustomRulesChangeRequested>(Assert.Single(fixture.Messages.Received));
            await Assert.Single(fixture.Services.GetAll<IMessageSender<RoomCustomRulesReplied>>()).SendAsync(session, new(2), default);
            Assert.Equal(table.S2C("gspCustomGameModeSetting"), Assert.Single(session.Sent));
            Assert.Equal(8, Assert.Single(session.Bodies).Length);
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(fixture.Packets.Handlers);
        Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<RoomCustomRulesChangeRequested>>());
        Assert.Empty(fixture.Services.GetAll<IMessageSender<RoomCustomRulesReplied>>());
    }

    /// <summary>A missing or inferred opcode cannot leave a partially installed custom-rule route.</summary>
    [Theory]
    [InlineData("gcpCustomGameModeSetting_")]
    [InlineData("gspCustomGameModeSetting")]
    public async Task RequiresVerifiedOpcodePair(string name)
    {
        var full = TestBuilds.Opcodes("kr-1256");
        foreach (var remove in new[] { true, false })
        {
            var table = new OpcodeTable(full.Profile, full.All.Where(row => !remove || row.Name != name)
                .Select(row => !remove && row.Name == name ? row with { Evidence = Evidence.Inferred } : row));
            using var fixture = new MatchMessagesMountTests.Fixture(table);
            var context = fixture.Context("kr-1256", "kr-1256/game", "dialect.rooms");
            try
            {
                Assert.Throws<ConfigException>(() =>
                    new CustomRulesBinding("kr-1256/game", table, TestBuilds.Packets, "kr-1256", NullLogger.Instance).Mount(context));
                Assert.Empty(fixture.Packets.Handlers);
                Assert.Empty(fixture.Services.GetAll<IMessageSender<RoomCustomRulesReplied>>());
            }
            finally { await context.UnwindAsync(); }
        }
    }

    /// <summary>Present packet code alone cannot enable a build whose native feature or mode resource is unavailable.</summary>
    [Theory]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    [InlineData("kr-812")]
    public async Task UnavailableNativeFeatureCannotMount(string build)
    {
        var table = TestBuilds.Opcodes(build);
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var context = fixture.Context(build, build + "/game", "dialect.rooms");
        try
        {
            Assert.Throws<ConfigException>(() => Binding(build).Mount(context));
            Assert.Empty(fixture.Packets.Handlers);
        }
        finally { await context.UnwindAsync(); }
    }

    private static CustomRulesBinding Binding(string build) => new(build + "/game", TestBuilds.Opcodes(build),
        TestBuilds.Packets, build, NullLogger.Instance);
}
