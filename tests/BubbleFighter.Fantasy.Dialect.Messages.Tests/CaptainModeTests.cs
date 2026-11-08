using System.Buffers.Binary;
using System.Reflection;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>
/// Pins the Captain frames of packets/mode-control.yaml (the four-byte announcement and death report) and the
/// kind-27 Captain kill of packets/score-reports.yaml, and exercises the guarded exchange's activation and unload.
/// </summary>
public sealed class CaptainModeTests
{
    /// <summary>The announcement and the death report are one seat word each, on every audited Captain build
    /// (packets/mode-control.yaml: appointments and deaths remain one word; kr-812 sub_4304C0 and the per-build
    /// dispatch cases consume exactly that word).</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    [InlineData("cn-404")]
    [InlineData("cn-408")]
    [InlineData("cn-706")]
    [InlineData("cn-717")]
    [InlineData("id-400")]
    [InlineData("id-1809")]
    public void CaptainFramesPinFourBytes(string build)
    {
        var body = CaptainModeBinding.WriteAppointed(TestBuilds.Packets, build, new RoundCaptainAppointed(5));
        Assert.Equal("05000000", Convert.ToHexString(body));
        Assert.Equal(new RoundCaptainDropped(7), CaptainModeBinding.ReadDrop(TestBuilds.Packets, build, Convert.FromHexString("07000000")));
        foreach (var malformed in new[] { new byte[3], new byte[5] })
            Assert.Throws<PacketFormatException>(() => CaptainModeBinding.ReadDrop(TestBuilds.Packets, build, malformed));
    }

    /// <summary>The enabled exchange delivers the death report and a consistent Captain kill, serializes the announcement and unloads.</summary>
    [Theory]
    [InlineData("kr-812", "kr", 812, false)]
    [InlineData("kr-812", "kr", 812, true)]
    [InlineData("cn-717", "cn", 717, false)]
    [InlineData("cn-717", "cn", 717, true)]
    [InlineData("cn-706", "cn", 706, false)]
    public async Task EnabledExchangeDispatchesAndUnloads(string build, string region, int version, bool remap)
    {
        var protocol = build + "/game";
        var profile = new ClientProfile(build, region, version, 0, 0, build);
        var root = Directory.GetParent(TestBuilds.PacketsDirectory())!.FullName;
        var full = OpcodeTableLoader.Load(Path.Combine(root, "profiles", build, "opcodes.yaml"), profile)
            .ForConnection(OpcodeConnection.Game);
        var table = remap ? new OpcodeTable(profile, full.All.Select(entry => entry with
            { Value = checked((ushort)(entry.Value + 1000)) })) : full;
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var context = fixture.Context(build, protocol, "dialect.rooms", extra: new() { ["captain_mode"] = true });
        context.BindServices(typeof(RoomsPlugin).GetCustomAttribute<PluginAttribute>()!);
        var session = new MatchMessagesMountTests.Session(profile);
        try
        {
            await new RoomsPlugin().ActivateAsync(context, default);
            Assert.Single(fixture.Services.GetAll<IInboundMessageBinding<RoundCaptainDropped>>());
            await fixture.Packets.DispatchAsync(protocol, session, new(table.C2S("gcpDropCaptain"), Convert.FromHexString("02000000")), default);
            Assert.Equal(new RoundCaptainDropped(2), Assert.Single(fixture.Messages.Received));

            var score = CaptainKill(2, 3);
            Assert.Equal("1B804F120020D6130002030900001B00", Convert.ToHexString(score));
            await fixture.Packets.DispatchAsync(protocol, session, new(table.C2S("gcpGameUpdateScore"), score), default);
            var kill = Assert.IsType<RoundFinishingBlowReported>(fixture.Messages.Received[1]);
            Assert.True(kill.CaptainKill);
            Assert.True(kill.RequiresReporterOwnership);
            Assert.Equal(3, kill.ReporterSlot);
            Assert.Equal(2, kill.Victim);
            // Seat bytes that disagree with the character words name no consistent pair.
            var inconsistent = CaptainKill(2, 3);
            inconsistent[10] = 4;
            await fixture.Packets.DispatchAsync(protocol, session, new(table.C2S("gcpGameUpdateScore"), inconsistent), default);
            Assert.Equal(2, fixture.Messages.Received.Count);
            foreach (var malformed in new[] { score[..^1], score.Concat(new byte[1]).ToArray() })
                await fixture.Packets.DispatchAsync(protocol, session, new(table.C2S("gcpGameUpdateScore"), malformed), default);
            Assert.Equal(2, fixture.Messages.Received.Count);

            var sender = Assert.Single(fixture.Services.GetAll<IMessageSender<RoundCaptainAppointed>>());
            await sender.SendAsync(session, new(6), default);
            Assert.Equal(table.S2C("gspCreateCaptain"), Assert.Single(session.Sent));
            Assert.Equal("06000000", Convert.ToHexString(Assert.Single(session.Bodies)));
            fixture.Messages.HasHandler = false;
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await fixture.Packets.DispatchAsync(protocol, session, new(table.C2S("gcpDropCaptain"), Convert.FromHexString("02000000")), default));
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(fixture.Packets.Handlers);
        Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<RoundCaptainDropped>>());
        Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundCaptainAppointed>>());
    }

    /// <summary>Without the option the Captain kill kind is no score report.</summary>
    [Fact]
    public async Task DisabledExchangeLeavesTheCaptainKillUnread()
    {
        const string build = "kr-812";
        const string protocol = "kr-812/game";
        var profile = new ClientProfile(build, "kr", 812, 0, 0, build);
        var root = Directory.GetParent(TestBuilds.PacketsDirectory())!.FullName;
        var table = OpcodeTableLoader.Load(Path.Combine(root, "profiles", build, "opcodes.yaml"), profile)
            .ForConnection(OpcodeConnection.Game);
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var context = fixture.Context(build, protocol, "dialect.rooms");
        context.BindServices(typeof(RoomsPlugin).GetCustomAttribute<PluginAttribute>()!);
        var session = new MatchMessagesMountTests.Session(profile);
        try
        {
            await new RoomsPlugin().ActivateAsync(context, default);
            Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<RoundCaptainDropped>>());
            await fixture.Packets.DispatchAsync(protocol, session, new(table.C2S("gcpGameUpdateScore"), CaptainKill(2, 3)), default);
            Assert.Empty(fixture.Messages.Received);
        }
        finally { await context.UnwindAsync(); }
    }

    /// <summary>Unsupported surfaces reject the option before any route or service is published.</summary>
    [Theory]
    [InlineData("waiting_rooms_only")]
    [InlineData("menu_only")]
    [InlineData("browser_only")]
    [InlineData("typed_play_rooms")]
    public async Task UnsupportedSurfaceFailsBeforeMount(string option)
    {
        var profile = new ClientProfile("kr-812", "kr", 812, 0, 0, "kr-812");
        using var fixture = new MatchMessagesMountTests.Fixture(new OpcodeTable(profile, []));
        var context = fixture.Context("kr-812", "kr-812/game", "dialect.rooms", extra: new()
            { ["captain_mode"] = true, [option] = option != "typed_play_rooms" });
        context.BindServices(typeof(RoomsPlugin).GetCustomAttribute<PluginAttribute>()!);
        try
        {
            var error = await Assert.ThrowsAsync<ConfigException>(async () => await new RoomsPlugin().ActivateAsync(context, default));
            Assert.Contains("captain_mode", error.Message);
            Assert.Empty(fixture.Packets.Handlers);
        }
        finally { await context.UnwindAsync(); }
    }

    /// <summary>An unaudited build rejects the option before it can publish a Captain route.</summary>
    [Theory]
    [InlineData("cn-324", "cn", 324)]
    [InlineData("cn-406", "cn", 406)]
    public async Task UnauditedBuildRejectsCaptainActivation(string build, string region, int version)
    {
        var profile = new ClientProfile(build, region, version, 0, 0, build);
        using var fixture = new MatchMessagesMountTests.Fixture(new OpcodeTable(profile, []));
        var context = fixture.Context(build, build + "/game", "dialect.rooms", extra: new() { ["captain_mode"] = true });
        context.BindServices(typeof(RoomsPlugin).GetCustomAttribute<PluginAttribute>()!);
        try
        {
            var error = await Assert.ThrowsAsync<ConfigException>(async () => await new RoomsPlugin().ActivateAsync(context, default));
            Assert.Contains("captain_mode", error.Message);
            Assert.Empty(fixture.Packets.Handlers);
            Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<RoundCaptainDropped>>());
            Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundCaptainAppointed>>());
        }
        finally { await context.UnwindAsync(); }
    }

    /// <summary>Only independently audited builds enable the exchange; older clients retain their capability boundary.</summary>
    [Theory]
    [InlineData("kr-812", true)]
    [InlineData("kr-1256", true)]
    [InlineData("cn-774", true)]
    [InlineData("tw-454", true)]
    [InlineData("cn-279", false)]
    [InlineData("cn-324", false)]
    [InlineData("cn-404", true)]
    [InlineData("cn-406", false)]
    [InlineData("cn-408", true)]
    [InlineData("cn-705", false)]
    [InlineData("cn-706", true)]
    [InlineData("cn-717", true)]
    [InlineData("cn-738", false)]
    [InlineData("cn-742", false)]
    [InlineData("id-400", true)]
    [InlineData("id-1809", true)]
    public void CapabilityRequiresIndependentEvidence(string build, bool supported) =>
        Assert.Equal(supported, TestBuilds.Packets.CapabilityOf(build, "captain_mode"));

    // kr-812 sub_430640 sets object members; kr-812 sub_530960 packs them without native alignment bytes.
    private static byte[] CaptainKill(int captain, int killer)
    {
        var body = new byte[16];
        body[0] = 27;
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(1), 1_000_000 + 100_000 * captain);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(5), 1_000_000 + 100_000 * killer);
        body[9] = (byte)captain;
        body[10] = (byte)killer;
        body[11] = 9;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(14), 27);
        return body;
    }
}
