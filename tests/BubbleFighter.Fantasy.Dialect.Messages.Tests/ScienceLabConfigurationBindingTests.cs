using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Enhancement;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins the complete native laboratory configuration and its explicit build admission.</summary>
public sealed class ScienceLabConfigurationBindingTests
{
    private const string Build = "kr-1256";
    private const string Protocol = Build + "/game";
    private const string Request = "gcpReqSciendLabConfig";
    private const string Reply = "gspRepSciendLabConfig";
    private const string MinimalBody =
        "0000000000000000000000000000000000000000000000000000000000000000050000009FE602009AE602009BE602009CE602009DE60200718D01000000000000000000000000000100000001000000000000000100000000000000000000000000000000000000000000000000000000000000000000000000000000000000";

    private static ScienceLabConfigurationPublished Configuration() => new([190111, 190106, 190107, 190108, 190109], 101745);

    /// <summary>The empty native request dispatches once and every registration unwinds, including remapped opcodes.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyRequestUsesNamedRoutesAndUnloads(bool remap)
    {
        var original = Table(Build);
        Assert.Equal((ushort)141, original.C2S(Request));
        Assert.Equal((ushort)162, original.S2C(Reply));
        var table = remap ? new OpcodeTable(original.Profile, original.All.Select(row => row with
        { Value = checked((ushort)(row.Value + 1000)) })) : original;
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var context = fixture.Context(Build, Protocol, "dialect.messages");
        var session = new MatchMessagesMountTests.Session(table.Profile);
        var binding = new ScienceLabConfigurationBinding(Protocol, table, TestBuilds.Packets, Build);
        try
        {
            binding.Bind(context, fixture.Messages);
            Assert.Equal(Protocol, Assert.Single(fixture.Services.GetAll<IInboundMessageBinding<ScienceLabConfigurationRequested>>()).Protocol);
            Assert.Single(fixture.Services.GetAll<IMessageSender<ScienceLabConfigurationPublished>>());
            Assert.Empty(PacketCodec.Write(TestBuilds.Packets, Build, "ScienceLabConfigurationRequest", new Dictionary<string, object?>()));
            await fixture.Packets.DispatchAsync(Protocol, session, new(table.C2S(Request), ReadOnlyMemory<byte>.Empty), default);
            Assert.IsType<ScienceLabConfigurationRequested>(Assert.Single(fixture.Messages.Received));
            Assert.Empty(session.Sent);
            await binding.SendAsync(session, Configuration(), default);
            Assert.Equal(table.S2C(Reply), Assert.Single(session.Sent));
            Assert.Equal(MinimalBody, Convert.ToHexString(Assert.Single(session.Bodies)));
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(fixture.Packets.Handlers);
        Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<ScienceLabConfigurationRequested>>());
        Assert.Empty(fixture.Services.GetAll<IMessageSender<ScienceLabConfigurationPublished>>());
    }

    /// <summary>The native body includes all nine bool32 words and both empty random-table maps in exactly 128 bytes.</summary>
    [Fact]
    public async Task ConfigurationPinsCompleteNativeBody()
    {
        var table = Table(Build);
        var session = new MatchMessagesMountTests.Session(table.Profile);
        await new ScienceLabConfigurationBinding(Protocol, table, TestBuilds.Packets, Build).SendAsync(session, Configuration(), default);
        var body = Assert.Single(session.Bodies);
        Assert.Equal(128, body.Length);
        Assert.Equal(MinimalBody, Convert.ToHexString(body));
        var read = PacketCodec.Read(TestBuilds.Packets, Build, "ScienceLabConfiguration", body);
        Assert.Equal(new object?[] { 190111, 190106, 190107, 190108, 190109 }, (object?[])read["enchant_materials"]!);
        Assert.Equal(101745, read["additive_item_id"]);
        Assert.Equal(true, read["unknown_0"]);
        Assert.Equal(true, read["unknown_1"]);
        Assert.Equal(false, read["unknown_2"]);
        Assert.Equal(true, read["enchant_enabled"]);
        foreach (var field in new[] { "unknown_4", "unknown_5", "unknown_6", "unknown_7", "unknown_8" })
            Assert.Equal(false, read[field]);
        Assert.Equal(body, PacketCodec.Write(TestBuilds.Packets, Build, "ScienceLabConfiguration", read));
    }

    /// <summary>Nonempty nested data pins every audited reader, including the final byte after its wide string.</summary>
    [Fact]
    public void ConfigurationPinsNestedReaders()
    {
        var values = new Dictionary<string, object?>
        {
            ["enchant_materials"] = new object[] { 190111, 190106, 190107, 190108, 190109 },
            ["additive_item_id"] = 101745,
            ["modification_configurations"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["unknown_4"] = 0x11, ["unknown_8"] = 0x22, ["unknown_12"] = 0x33,
                    ["unknown_16"] = new object[] { 0x44 }, ["unknown_28"] = 0x55,
                    ["unknown_32"] = new object[] { new Dictionary<string, object?> { ["key"] = 0x66, ["value"] = 0x77 } },
                },
            },
            ["modification_random_table"] = new Dictionary<string, object?>
            {
                ["unknown_4"] = new object[] { new Dictionary<string, object?> { ["key"] = 0x88, ["values"] = new object[] { 0x99, 0xAA } } },
                ["unknown_12"] = new object[] { new Dictionary<string, object?> { ["key"] = 0xBB, ["values"] = new object[] { 0xCC } } },
            },
            ["unknown_124"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["key"] = 0xDD,
                    ["values"] = new object[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["unknown_0"] = 0xEE,
                            ["unknown_4"] = new object[]
                            {
                                new Dictionary<string, object?>
                                {
                                    ["unknown_0"] = 0x101, ["unknown_4"] = 0x102,
                                    ["unknown_8"] = new object[] { 0x103 }, ["unknown_20"] = 0x104, ["unknown_24"] = "A",
                                },
                            },
                            ["unknown_16"] = new object[] { 0x105 }, ["unknown_32"] = 0x106, ["unknown_48"] = 0x7F,
                        },
                    },
                },
            },
        };
        const string expected =
            "0000000000000000000000000000000000000000000000000000000000000000050000009FE602009AE602009BE602009CE602009DE60200718D0100000000000000000000000000010000000100000000000000010000000000000000000000000000000000000000000000000000000100000011000000220000003300000001000000440000005500000001000000660000007700000001000000880000000200000099000000AA00000001000000BB00000001000000CC00000001000000DD00000001000000EE00000001000000010100000201000001000000030100000401000001000000410001000000050100000000000006010000000000007F";
        var body = PacketCodec.Write(TestBuilds.Packets, Build, "ScienceLabConfiguration", values);
        Assert.Equal(255, body.Length);
        Assert.Equal(expected, Convert.ToHexString(body));
        var read = PacketCodec.Read(TestBuilds.Packets, Build, "ScienceLabConfiguration", body);
        Assert.Equal(body, PacketCodec.Write(TestBuilds.Packets, Build, "ScienceLabConfiguration", read));
    }

    /// <summary>Layout inheritance cannot activate a configuration exchange without the exact build in the audited list.</summary>
    [Theory]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    [InlineData("cn-735")]
    [InlineData("cn-738")]
    [InlineData("cn-742")]
    [InlineData("kr-812")]
    public async Task UnauditedBuildFailsBeforeRegistration(string build)
    {
        var table = Table(build);
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var context = fixture.Context(build, build + "/game", "dialect.messages");
        var binding = new ScienceLabConfigurationBinding(build + "/game", table, TestBuilds.Packets, build);
        try
        {
            Assert.Throws<ConfigException>(() => binding.Bind(context, fixture.Messages));
            Assert.Empty(fixture.Packets.Handlers);
            Assert.Empty(fixture.Services.GetAll<IMessageSender<ScienceLabConfigurationPublished>>());
            var session = new MatchMessagesMountTests.Session(table.Profile);
            await Assert.ThrowsAsync<ConfigException>(async () => await binding.SendAsync(session, Configuration(), default));
            Assert.Empty(session.Sent);
        }
        finally { await context.UnwindAsync(); }
    }

    /// <summary>Missing or inferred opcode evidence refuses admission before publishing any route.</summary>
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task MissingOrUnverifiedOpcodeFailsBeforeRegistration(bool request, bool missing)
    {
        var original = Table(Build);
        var name = request ? Request : Reply;
        var rows = original.All.Where(row => !missing || row.Name != name)
            .Select(row => row.Name == name ? row with { Evidence = Evidence.Inferred } : row);
        var table = new OpcodeTable(original.Profile, rows);
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var context = fixture.Context(Build, Protocol, "dialect.messages");
        try
        {
            Assert.Throws<ConfigException>(() => new ScienceLabConfigurationBinding(Protocol, table, TestBuilds.Packets, Build)
                .Bind(context, fixture.Messages));
            Assert.Empty(fixture.Packets.Handlers);
            Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<ScienceLabConfigurationRequested>>());
        }
        finally { await context.UnwindAsync(); }
    }

    /// <summary>Malformed admission and missing native fields fail before either a route or a sender can be observed.</summary>
    [Theory]
    [InlineData("builds.yaml", "laboratory_enchant_builds: [kr-1256]", "laboratory_enchant_builds: kr-1256")]
    [InlineData("builds.yaml", "laboratory_enchant_builds: [kr-1256]", "laboratory_enchant_builds: [kr-1256, kr-1256]")]
    [InlineData("builds.yaml", "laboratory_enchant_builds: [kr-1256]", "laboratory_enchant_builds: [kr-1256, missing-build]")]
    [InlineData("builds.yaml", "laboratory_enchant_builds: [kr-1256]", "laboratory_enchant_builds: []")]
    [InlineData("science-lab-configuration.yaml", "ScienceLabConfigurationRequest: []", "ScienceLabConfigurationRequest:\n    - unexpected: u32")]
    [InlineData("science-lab-configuration.yaml", "enchant_materials: list<u32>", "missing_materials: list<u32>")]
    [InlineData("science-lab-configuration.yaml", "enchant_materials: list<u32>", "enchant_materials: list<u64>")]
    [InlineData("science-lab-configuration.yaml", "additive_item_id: u32", "additive_item_id: u64")]
    public async Task InvalidConfigurationFailsBeforeRegistration(string file, string before, string after)
    {
        var packets = LabTestTables.Modified(file, before, after);
        var table = Table(Build);
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var context = fixture.Context(Build, Protocol, "dialect.messages");
        var binding = new ScienceLabConfigurationBinding(Protocol, table, packets, Build);
        try
        {
            Assert.Throws<ConfigException>(() => binding.Bind(context, fixture.Messages));
            Assert.Empty(fixture.Packets.Handlers);
            Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<ScienceLabConfigurationRequested>>());
            Assert.Empty(fixture.Services.GetAll<IMessageSender<ScienceLabConfigurationPublished>>());
            var session = new MatchMessagesMountTests.Session(table.Profile);
            await Assert.ThrowsAsync<ConfigException>(async () => await binding.SendAsync(session, Configuration(), default));
            Assert.Empty(session.Sent);
        }
        finally { await context.UnwindAsync(); }
    }

    /// <summary>Trailing request bytes, missing feature handlers, cancellation and transport errors remain visible.</summary>
    [Fact]
    public async Task InvalidRequestAndOperationalFailuresDoNotDisappear()
    {
        var table = Table(Build);
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var context = fixture.Context(Build, Protocol, "dialect.messages");
        var session = new MatchMessagesMountTests.Session(table.Profile);
        var binding = new ScienceLabConfigurationBinding(Protocol, table, TestBuilds.Packets, Build);
        try
        {
            binding.Bind(context, fixture.Messages);
            await Assert.ThrowsAsync<PacketFormatException>(async () => await fixture.Packets.DispatchAsync(Protocol,
                session, new(table.C2S(Request), new byte[] { 0 }), default));
            Assert.Empty(fixture.Messages.Received);
            fixture.Messages.HasHandler = false;
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await fixture.Packets.DispatchAsync(Protocol,
                session, new(table.C2S(Request), ReadOnlyMemory<byte>.Empty), default));
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await binding.SendAsync(session, Configuration(), cancelled.Token));
            session.FailSend = true;
            await Assert.ThrowsAsync<IOException>(async () => await binding.SendAsync(session, Configuration(), default));
            Assert.Empty(session.Sent);
        }
        finally { await context.UnwindAsync(); }
    }

    /// <summary>Malformed material configurations fail before the native reader could dereference their first element.</summary>
    [Fact]
    public async Task InvalidMaterialsDoNotSend()
    {
        var table = Table(Build);
        var session = new MatchMessagesMountTests.Session(table.Profile);
        var binding = new ScienceLabConfigurationBinding(Protocol, table, TestBuilds.Packets, Build);
        ScienceLabConfigurationPublished[] invalid =
        [
            new([], 101745), new([1], 101745), new([1, 2, 3, 4, 5, 6], 101745),
            new([1, 2, 3, 4, 0], 101745), new([1, 2, 3, 4, 4], 101745),
            new([1, 2, 3, 4, 5], 0), new([1, 2, 3, 4, 5], 5),
        ];
        foreach (var message in invalid)
            await Assert.ThrowsAsync<ArgumentException>(async () => await binding.SendAsync(session, message, default));
        Assert.Empty(session.Sent);
    }

    private static IOpcodeTable Table(string build)
    {
        var profile = new ClientProfile(build, build[..2], int.Parse(build[3..]), 0, 0, build);
        var root = Directory.GetParent(TestBuilds.PacketsDirectory())!.FullName;
        return OpcodeTableLoader.Load(Path.Combine(root, "profiles", build, "opcodes.yaml"), profile)
            .ForConnection(OpcodeConnection.Game);
    }
}
