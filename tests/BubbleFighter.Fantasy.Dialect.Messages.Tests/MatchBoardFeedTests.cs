using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Protocol;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins the matching-board feed bodies: the pump family, the waiting-user list and the
/// match-waiting status line, per the builds that consume them.</summary>
public sealed class MatchBoardFeedTests
{
    private static PacketTables Tables()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "packets");
            if (File.Exists(Path.Combine(path, "builds.yaml")))
                return PacketTables.Load(path);
        }
        throw new InvalidOperationException("Repository packet tables were not found.");
    }

    /// <summary>The pump family keeps its per-build numbering: 173-178 on kr-1256 and cn-774,
    /// 170-175 on tw-454, resolved through the game connection's opcode catalog.</summary>
    [Theory]
    [InlineData("kr-1256", "MatchBoardDeny", 173)]
    [InlineData("kr-1256", "MatchBoardRoomUpsert", 174)]
    [InlineData("kr-1256", "MatchBoardCountFlag", 175)]
    [InlineData("kr-1256", "MatchBoardCount", 176)]
    [InlineData("kr-1256", "MatchBoardCountSet", 177)]
    [InlineData("cn-774", "MatchBoardDeny", 173)]
    [InlineData("cn-774", "MatchBoardRoomUpsert", 174)]
    [InlineData("cn-774", "MatchBoardCountFlag", 175)]
    [InlineData("cn-774", "MatchBoardCount", 176)]
    [InlineData("cn-774", "MatchBoardCountSet", 177)]
    [InlineData("tw-454", "MatchBoardDeny", 170)]
    [InlineData("tw-454", "MatchBoardRoomUpsert", 171)]
    [InlineData("tw-454", "MatchBoardCountFlag", 172)]
    [InlineData("tw-454", "MatchBoardCount", 173)]
    [InlineData("tw-454", "MatchBoardCountSet", 174)]
    public void PumpFamilyResolvesPerBuild(string build, string structure, int opcode)
    {
        var tables = Tables();
        var root = Directory.GetParent(TestBuilds.PacketsDirectory())!.FullName;
        var profile = new ClientProfile(build, build[..2], int.Parse(build[3..]), 0, 0, build);
        var catalog = OpcodeTableLoader.Load(Path.Combine(root, "profiles", build, "opcodes.yaml"), profile);
        Assert.Equal((ushort)opcode, catalog.ForConnection(OpcodeConnection.Game).S2C("gsp" + structure + "_"));
        Assert.NotEmpty(tables.Resolve(build, structure));
    }

    /// <summary>The Korean upsert retains its second optional record and rejects either missing tail byte.</summary>
    [Fact]
    public void KoreanUpsertCarriesTheWordVectorAfterTheTitle()
    {
        var tables = Tables();
        var body = PacketCodec.Write(tables, "kr-1256", "MatchBoardRoomUpsert", new Dictionary<string, object?>
        {
            ["room"] = 9,
            ["unknown_8"] = 1,
            ["unknown_12"] = 2,
            ["unknown_40"] = new Dictionary<string, object?> { ["unknown_0"] = 6, ["unknown_4"] = new[] { 7, 8 } },
        });
        Assert.Equal("090000000100000002000000BA3A06000000020000000700000008000000", Convert.ToHexString(body));
        Assert.Equal(body, PacketCodec.Write(tables, "kr-1256", "MatchBoardRoomUpsert",
            PacketCodec.Read(tables, "kr-1256", "MatchBoardRoomUpsert", body)));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(tables, "kr-1256", "MatchBoardRoomUpsert", body[..^1]));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(tables, "kr-1256", "MatchBoardRoomUpsert", new byte[13]));
    }

    /// <summary>All three builds consume four count words, with a trailing byte only on the flag update.</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void CountUpdatesPinAllConsumedWords(string build)
    {
        var tables = Tables();
        var values = new Dictionary<string, object?> { ["room"] = 1, ["unknown_8"] = 2, ["unknown_12"] = 3, ["unknown_16"] = 4, ["unknown_20"] = (byte)5 };
        foreach (var structure in new[] { "MatchBoardCount", "MatchBoardCountSet", "MatchBoardCountFlag" })
        {
            var body = PacketCodec.Write(tables, build, structure, values);
            Assert.Equal("01000000020000000300000004000000" + (structure == "MatchBoardCountFlag" ? "05" : ""), Convert.ToHexString(body));
            Assert.Throws<PacketFormatException>(() => PacketCodec.Read(tables, build, structure, body[..^1]));
        }
    }

    /// <summary>cn-774's deny body carries the optional 0x3A-tagged tail and the trailing word.</summary>
    [Fact]
    public void DenyBodyWritesTheOptionalTailAndTheTrailingWord()
    {
        var tables = Tables();
        var absent = PacketCodec.Write(tables, "cn-774", "MatchBoardDeny",
            new Dictionary<string, object?> { ["unknown_4"] = 1, ["unknown_8"] = 2, ["unknown_10"] = 3 });
        Assert.Equal(10, absent.Length);
        Assert.Equal("0100000002BA03000000", Convert.ToHexString(absent));
        var present = PacketCodec.Write(tables, "cn-774", "MatchBoardDeny",
            new Dictionary<string, object?>
            {
                ["unknown_4"] = 1, ["unknown_8"] = 2,
                ["deny_tail"] = new Dictionary<string, object?>
                {
                    ["unknown_tail_byte"] = 5, ["unknown_tail_word"] = 6, ["unknown_tail_word2"] = 7, ["unknown_tail_string"] = "ab",
                },
                ["unknown_10"] = 3,
            });
        Assert.Equal(27, present.Length);
        Assert.Equal("01000000023A050600000007000000020000006100620003000000", Convert.ToHexString(present));
    }

    /// <summary>The room upsert carries the room key and the optional 0x3A-tagged title.</summary>
    [Theory]
    [InlineData("kr-1256", "BA")]
    [InlineData("cn-774", "")]
    [InlineData("tw-454", "")]
    public void RoomUpsertWritesTheKeyAndTheOptionalTitle(string build, string suffix)
    {
        var tables = Tables();
        var absent = PacketCodec.Write(tables, build, "MatchBoardRoomUpsert",
            new Dictionary<string, object?> { ["room"] = 9, ["unknown_8"] = 1, ["unknown_12"] = 2 });
        Assert.Equal("090000000100000002000000BA" + suffix, Convert.ToHexString(absent));
        var present = PacketCodec.Write(tables, build, "MatchBoardRoomUpsert",
            new Dictionary<string, object?>
            {
                ["room"] = 9, ["unknown_8"] = 1, ["unknown_12"] = 2,
                ["title"] = new Dictionary<string, object?> { ["title"] = "ab" },
            });
        Assert.Equal("0900000001000000020000003A0200000061006200" + suffix, Convert.ToHexString(present));
        Assert.Equal(present, PacketCodec.Write(tables, build, "MatchBoardRoomUpsert",
            PacketCodec.Read(tables, build, "MatchBoardRoomUpsert", present)));
    }

    /// <summary>The waiting-user list keeps each build's element tail: kr-1256's {name, word},
    /// cn-774's and tw-454's elements with one and two trailing bytes.</summary>
    [Theory]
    [InlineData("kr-1256", 12)]
    [InlineData("cn-774", 13)]
    [InlineData("tw-454", 14)]
    public void WaitUserListElementTailsGrowPerBuild(string build, int length)
    {
        var body = PacketCodec.Write(Tables(), build, "WaitUserList",
            new Dictionary<string, object?>
            {
                ["users"] = new[]
                {
                    new Dictionary<string, object?> { ["name"] = "ab", ["unknown_tail"] = 160 },
                },
            });
        Assert.Equal(4 + length, body.Length);
        Assert.Equal(1, BitConverter.ToInt32(body));
        Assert.Equal(160, BitConverter.ToInt32(body, 4 + 4 + 2 + 2));
    }

    /// <summary>The status line body is the same four fields in all three builds, and kr-1256's
    /// name batch appends the second map of optional word-vector records after the first list.</summary>
    [Fact]
    public void StatusLineAndNameBatchKeepTheirShapes()
    {
        var tables = Tables();
        foreach (var build in new[] { "kr-1256", "cn-774", "tw-454" })
        {
            var info = PacketCodec.Write(tables, build, "MatchWaitingInfo",
                new Dictionary<string, object?>
                {
                    ["unknown_4"] = 1, ["unknown_8"] = 2, ["unknown_12"] = "a", ["unknown_16"] = 3,
                });
            Assert.Equal($"{build}:010000000200000001000000610003000000", $"{build}:" + Convert.ToHexString(info));
        }
        var names = PacketCodec.Write(tables, "cn-774", "MatchBoardNames",
            new Dictionary<string, object?>
            {
                ["names"] = new[] { new Dictionary<string, object?> { ["key"] = 5, ["name"] = "ab" } },
            });
        Assert.Equal("01000000050000000200000061006200", Convert.ToHexString(names));
        var krNames = PacketCodec.Write(tables, "kr-1256", "MatchBoardNames",
            new Dictionary<string, object?>
            {
                ["names"] = new[] { new Dictionary<string, object?> { ["key"] = 5, ["name"] = "ab" } },
                ["states"] = new[] { new Dictionary<string, object?> { ["key"] = 5, ["state"] = new Dictionary<string, object?> { ["unknown_0"] = 6, ["unknown_4"] = new[] { 7, 8 } } } },
            });
        Assert.Equal("0100000005000000020000006100620001000000050000003A06000000020000000700000008000000", Convert.ToHexString(krNames));
        Assert.Equal(krNames, PacketCodec.Write(tables, "kr-1256", "MatchBoardNames",
            PacketCodec.Read(tables, "kr-1256", "MatchBoardNames", krNames)));
    }
}
