using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

public sealed class MatchingBindingTests
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

    [Fact]
    public void NativeMatchingRequestPinsTwelveBytesAndKeepsTheQueueFlagSeparate()
    {
        var tables = Tables();
        var ordinary = Convert.FromHexString("040000002A00000000000000");
        var queue = Convert.FromHexString("100000000000000001000000");
        Assert.Equal(new RoomMatchRequested(0, false), MatchingBinding.Read(tables, "cn-324", new byte[12]));
        Assert.Equal(12, ordinary.Length);
        Assert.Equal(new RoomMatchRequested(4, false), MatchingBinding.Read(tables, "cn-324", ordinary));
        Assert.Equal(new RoomMatchRequested(16, true), MatchingBinding.Read(tables, "cn-324", queue));
        Assert.Throws<PacketFormatException>(() => MatchingBinding.Read(tables, "cn-324", ordinary[..11]));
        Assert.Throws<PacketFormatException>(() => MatchingBinding.Read(tables, "cn-324", ordinary.Concat(new byte[1]).ToArray()));
    }

    [Fact]
    public void NativeMatchingNotificationPinsTheSearchAndFailureResultWords()
    {
        var tables = Tables();
        Assert.Equal(new byte[4], MatchingBinding.Write(tables, "cn-324", new(true)));
        Assert.Equal(Convert.FromHexString("01000000"), MatchingBinding.Write(tables, "cn-324", new(false)));
    }

    /// <summary>Each audited native envelope selects the same typed mode while retaining its own tail widths.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-717")]
    [InlineData("id-400")]
    [InlineData("id-1809")]
    [InlineData("cn-406")]
    public void AuditedSelectorsKeepModeAndIndependentQueueFlags(string build)
    {
        var tables = Tables();
        Assert.True(tables.CapabilityOf(build, "quick_matching"));
        MatchingBinding.Validate(tables.Resolve(build, "RoomMatchRequest"), tables.Resolve(build, "RoomMatchNotification"));
        foreach (var (dedicated, retry) in new[] { (false, false), (true, false), (false, true), (true, true) })
        {
            var body = PacketCodec.Write(tables, build, "RoomMatchRequest", new Dictionary<string, object?>
            {
                ["mode_id"] = 101, ["dedicated_queue"] = dedicated, ["retry_queue"] = retry,
                ["unknown_8"] = 16, ["unknown_12"] = 32, ["unknown_16"] = "map filter",
                ["unknown_52"] = tables.Resolve(build, "RoomMatchRequest").Any(field => field.Name == "unknown_52" && field.Type == "wstr") ? "tag" : 7,
            });
            Assert.Equal(new RoomMatchRequested(101, dedicated, retry), MatchingBinding.Read(tables, build, body));
            Assert.Throws<PacketFormatException>(() => MatchingBinding.Read(tables, build, body[..^1]));
            Assert.Throws<PacketFormatException>(() => MatchingBinding.Read(tables, build, body.Concat(new byte[1]).ToArray()));
        }
        var replyLength = build is "id-400" or "cn-406" ? 8 : 12;
        Assert.Equal(new byte[replyLength], MatchingBinding.Write(tables, build, new(true)));
        var refused = MatchingBinding.Write(tables, build, new(false));
        Assert.Equal(replyLength, refused.Length);
        Assert.Equal(1, BitConverter.ToInt32(refused));
        Assert.All(refused[4..], value => Assert.Equal(0, value));
    }

    /// <summary>The binding rejects missing selectors and wrong flag types before registering effects.</summary>
    [Fact]
    public void UnauditedSelectorsFailBeforeRegisteringTheHandler()
    {
        var tables = Tables();
        var request = tables.Resolve("kr-812", "RoomMatchRequest");
        var reply = tables.Resolve("kr-812", "RoomMatchNotification");
        Assert.Throws<BubbleFighter.Fantasy.Abstractions.ConfigException>(() => MatchingBinding.Validate(request.Skip(1).ToArray(), reply));
        Assert.Throws<BubbleFighter.Fantasy.Abstractions.ConfigException>(() => MatchingBinding.Validate(request, reply.Skip(1).ToArray()));
        var unaudited = tables.Resolve("cn-324", "RoomMatchRequest").Select(field => field.Name == "dedicated_queue"
            ? field with { Type = "u32" } : field).ToArray();
        Assert.Throws<BubbleFighter.Fantasy.Abstractions.ConfigException>(() => MatchingBinding.Validate(unaudited, reply));
        Assert.False(tables.CapabilityOf("cn-735", "quick_matching"));
    }

    /// <summary>An ordinary KR2015 matching request carries mode 1 with no queue flags.</summary>
    [Fact]
    public void CapturedOrdinaryMatchingRequestUsesBaselineMode()
    {
        var body = new byte[40];
        body[0] = 1;
        Assert.Equal(new RoomMatchRequested(1, false, false), MatchingBinding.Read(Tables(), "kr-812", body));
    }

    /// <summary>Rejects negative and overflowing native mode selectors.</summary>
    [Theory]
    [InlineData("FFFFFFFF0000000000000000")]
    [InlineData("000001000000000000000000")]
    public void InvalidModeIsRejected(string body)
    {
        Assert.Throws<PacketFormatException>(() => MatchingBinding.Read(Tables(), "cn-324", Convert.FromHexString(body)));
    }

    /// <summary>The early client has no queue flag and keeps its mode independent from filter digits.</summary>
    [Theory]
    [InlineData(1, 0)]
    [InlineData(3, 11)]
    [InlineData(101, 12)]
    public void EarlyClientMatchingUsesTwoWords(int mode, int filter)
    {
        var tables = Tables();
        MatchingBinding.Validate(tables.Resolve("cn-279", "RoomMatchRequest"),
            tables.Resolve("cn-279", "RoomMatchNotification"));
        var body = BitConverter.GetBytes(mode).Concat(BitConverter.GetBytes(filter)).ToArray();
        Assert.Equal(new RoomMatchRequested(mode, false), MatchingBinding.Read(tables, "cn-279", body));
        Assert.Throws<PacketFormatException>(() => MatchingBinding.Read(tables, "cn-279", body[..^1]));
        Assert.Throws<PacketFormatException>(() => MatchingBinding.Read(tables, "cn-279", body.Concat(new byte[1]).ToArray()));
        Assert.Equal(new byte[4], MatchingBinding.Write(tables, "cn-279", new(true)));
        Assert.Equal("01000000", Convert.ToHexString(MatchingBinding.Write(tables, "cn-279", new(false))));
    }
}
