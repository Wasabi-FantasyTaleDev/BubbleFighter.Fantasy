using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins modern cooperative readers' collection prefixes and native-record wire widths.</summary>
public sealed class ModernCooperativeTableTests
{
    /// <summary>A mission has six words, a string vector and bool32, with only one enclosing count.</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void MissionVectorKeepsMultipleArgumentsAndFullBoolean(string build)
    {
        var record = new Dictionary<string, object?>
        {
            ["unknown_4"] = 1, ["unknown_8"] = 2, ["unknown_12"] = 3,
            ["unknown_16"] = 4, ["unknown_20"] = 5, ["unknown_24"] = 6,
            ["arguments"] = new[] { "a", "bc" }, ["unknown_40"] = true,
        };
        Pin(build, "MapMissionInfoNotice", new() { ["missions"] = new[] { record } },
            "01000000010000000200000003000000040000000500000006000000" +
            "02000000010000006100020000006200630001000000");
        Pin(build, "MapMissionInfoNotice", new(), "00000000");
    }

    /// <summary>Rank vectors each own one count; a native 24-byte rank object transmits five words.</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void RankListsDoNotDuplicateTheirCounts(string build)
    {
        var rank = new Dictionary<string, object?>
        {
            ["seat"] = 1, ["unknown_8"] = 2, ["unknown_12"] = 3,
            ["unknown_16"] = 4, ["unknown_20"] = 5,
        };
        Pin(build, "NotifyRankInfoNotice", new()
        {
            ["state"] = 9, ["vector_values"] = new[] { 7, 8 }, ["ranks"] = new[] { rank },
        }, "09000000020000000700000008000000010000000100000002000000030000000400000005000000");
        Pin(build, "NotifyRankInfoNotice", new(), "000000000000000000000000");
    }

    /// <summary>Counted item records carry three words without transmitting their native vtable.</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void ItemVectorHasOneCountAndTwelveByteEntries(string build)
    {
        var item = new Dictionary<string, object?> { ["unknown_4"] = 1, ["unknown_8"] = 2, ["unknown_12"] = 3 };
        Pin(build, "PveItemInfoNotice", new() { ["items"] = new[] { item, item } },
            "02000000010000000200000003000000010000000200000003000000");
        Pin(build, "PveItemInfoNotice", new(), "00000000");
    }

    /// <summary>Scalar cooperative bodies retain their complete request and notification widths.</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void ScalarBodiesPinFieldOrder(string build)
    {
        Pin(build, "PveUseCountItemRequest", new() { ["slot"] = 3 }, "03000000");
        Pin(build, "PvePangUnitRequest", new() { ["unit"] = 7 }, "07000000");
        Pin(build, "MissionProcessRequest", new() { ["mission_index"] = 2, ["npc_kind"] = 100 }, "020000006400000000000000");
        Pin(build, "ResMissionStartNotice", new() { ["unknown_4"] = 2 }, "02000000");
        Pin(build, "ResMissionTimeOutNotice", new() { ["mission_index"] = 3 }, "03000000");
        Pin(build, "ResMissionOkNotice", new() { ["flag"] = 0, ["mission_index"] = 2 }, "0000000002000000");
        Pin(build, "NotifyGamePointNotice", new() { ["seat"] = 1, ["subtract_flag"] = 0, ["amount"] = 9 }, "010000000000000009000000");
        Pin(build, "PveStatusNotice", new() { ["message"] = "a" }, "010000006100");
    }

    private static void Pin(string build, string structure, Dictionary<string, object?> values, string hex)
    {
        var tables = TestBuilds.All.Tables;
        var body = PacketCodec.Write(tables, build, structure, values);
        Assert.Equal(hex, Convert.ToHexString(body));
        Assert.Equal(body, PacketCodec.Write(tables, build, structure, PacketCodec.Read(tables, build, structure, body)));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(tables, build, structure, body[..^1]));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(tables, build, structure, [.. body, 0]));
    }
}
