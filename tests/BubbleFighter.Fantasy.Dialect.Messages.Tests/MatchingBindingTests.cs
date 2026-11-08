using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using System.Reflection;
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

    /// <summary>The modern family passes the audited selector check, pins the base body and answers
    /// with the shared notification body.</summary>
    [Fact]
    public void ModernRequestFamilyPassesValidationAndPinsTheBaseBody()
    {
        var tables = Tables();
        Assert.True(tables.CapabilityOf("kr-1256", "quick_matching"));
        TypedMatchingBinding.Validate(tables.Resolve("kr-1256", "RoomMatchRequest"));
        // The legacy selector layout is not the audited modern family.
        Assert.Throws<ConfigException>(() => TypedMatchingBinding.Validate(tables.Resolve("kr-812", "RoomMatchRequest")));
        var start = Convert.FromHexString("0100000000");
        var values = PacketCodec.ReadScalars(tables, "kr-1256", "RoomMatchRequest", start);
        Assert.Equal(1, Convert.ToInt32(values["type"]));
        Assert.Throws<PacketFormatException>(() =>
            PacketCodec.ReadScalars(tables, "kr-1256", "RoomMatchRequest", start[..^1]));
        Assert.Throws<PacketFormatException>(() =>
            PacketCodec.ReadScalars(tables, "kr-1256", "RoomMatchRequest", start.Concat(new byte[1]).ToArray()));
        Assert.Equal(new byte[12], TypedMatchingBinding.Write(tables, "kr-1256", new RoomMatchNotified(true)));
        var refused = TypedMatchingBinding.Write(tables, "kr-1256", new RoomMatchNotified(false));
        Assert.Equal(12, refused.Length);
        Assert.Equal(1, BitConverter.ToInt32(refused));
        Assert.All(refused[4..], value => Assert.Equal(0, value));
    }

    /// <summary>The route reader consumes the complete native body for every typed request.</summary>
    [Theory]
    [InlineData("0100000000", 1)]
    [InlineData("02000000007800", 2)]
    [InlineData("03000000000400", 3)]
    [InlineData("04000000002A0000001000", 4)]
    public void ModernRouteReaderAcceptsEachNativeTail(string hex, int type)
    {
        var body = Convert.FromHexString(hex);
        Assert.Equal(type, TypedMatchingBinding.ReadType(Tables(), "kr-1256", body));
        Assert.Throws<PacketFormatException>(() => TypedMatchingBinding.ReadType(Tables(), "kr-1256", body[..^1]));
        Assert.Throws<PacketFormatException>(() => TypedMatchingBinding.ReadType(Tables(), "kr-1256", [.. body, 0]));
    }

    /// <summary>Missing and unknown discriminators cannot be treated as ordinary match starts.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("0000000000")]
    [InlineData("0500000000")]
    public void ModernRouteReaderRejectsUnknownRequestTypes(string hex)
    {
        Assert.Throws<PacketFormatException>(() =>
            TypedMatchingBinding.ReadType(Tables(), "kr-1256", Convert.FromHexString(hex)));
    }

    /// <summary>Native keepalives reach the mounted matcher; unsupported flows return a complete refusal.</summary>
    [Fact]
    public async Task ModernMountedRouteDispatchesKeepaliveAndRefusesUnimplementedFlows()
    {
        var profile = new ClientProfile("kr-1256", "kr", 1256, 0, 0, "kr-1256");
        var root = Directory.GetParent(TestBuilds.PacketsDirectory())!.FullName;
        var table = OpcodeTableLoader.Load(Path.Combine(root, "profiles", "kr-1256", "opcodes.yaml"), profile)
            .ForConnection(OpcodeConnection.Game);
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var context = fixture.Context("kr-1256", "kr-1256/game", "dialect.rooms");
        context.BindServices(typeof(RoomsPlugin).GetCustomAttribute<PluginAttribute>()!);
        var session = new MatchMessagesMountTests.Session(profile);
        try
        {
            new TypedMatchingBinding("kr-1256/game", table, Tables(), "kr-1256", 16).Mount(context, fixture.Messages);
            await Request("0100000000");
            await Request("02000000007800");
            Assert.Equal(2, fixture.Messages.Received.Count);
            Assert.All(fixture.Messages.Received, message => Assert.Equal(new RoomMatchRequested(16, false), message));
            await Request("03000000000400");
            await Request("04000000002A0000001000");
            Assert.Equal(2, fixture.Messages.Received.Count);
            Assert.Equal(2, session.Sent.Count);
            Assert.All(session.Sent, opcode => Assert.Equal(table.S2C("gspNotifyMatch"), opcode));
            Assert.All(session.Bodies, body => Assert.Equal("010000000000000000000000", Convert.ToHexString(body)));
            await Assert.ThrowsAsync<PacketFormatException>(() => Request("0200000000"));
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(fixture.Packets.Handlers);
        Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<RoomMatchRequested>>());
        Assert.Empty(fixture.Services.GetAll<IMessageSender<RoomMatchNotified>>());

        Task Request(string hex) => fixture.Packets.DispatchAsync("kr-1256/game", session,
            new(table.C2S("gcpReqMatch"), Convert.FromHexString(hex)), default).AsTask();
    }
}
