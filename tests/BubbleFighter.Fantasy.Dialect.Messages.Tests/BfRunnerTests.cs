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
/// Pins the BFRunner frames of packets/bfrunner.yaml (kr-812 sub_844A30, sub_530A40 / sub_840530, sub_80A910 and
/// sub_6DB1B0 with their cn-705 and cn-706 matches) and exercises the exchange's activation and unload.
/// </summary>
public sealed class BfRunnerTests
{
    /// <summary>The goal and checkpoint reports are one word, the notices one and two words, on every audited build.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-705")]
    [InlineData("cn-706")]
    [InlineData("cn-717")]
    [InlineData("id-1809")]
    public void FramesPinTheirWords(string build)
    {
        var packets = TestBuilds.Packets;
        Assert.Equal(new RoundBfRunnerGoalReported(2), BfRunnerBinding.ReadGoal(packets, build, Convert.FromHexString("02000000")));
        Assert.Equal(new RoundBfRunnerCheckpointReported(17),
            BfRunnerBinding.ReadCheckpoint(packets, build, Convert.FromHexString("11000000")));
        Assert.Equal("0200000011000000",
            Convert.ToHexString(BfRunnerBinding.WriteCheckpointReached(packets, build, new(2, 17))));
        Assert.Equal("01000000", Convert.ToHexString(BfRunnerBinding.WriteFinished(packets, build, new(1))));
        foreach (var malformed in new[] { new byte[3], new byte[5] })
        {
            Assert.Throws<PacketFormatException>(() => BfRunnerBinding.ReadGoal(packets, build, malformed));
            Assert.Throws<PacketFormatException>(() => BfRunnerBinding.ReadCheckpoint(packets, build, malformed));
        }
    }

    /// <summary>
    /// The modern host writes the goal's team as one byte (kr-1256 sub_6E6A90, cn-774 sub_8A6FC0, tw-454 sub_71A3B0); the
    /// checkpoint stays one word.
    /// </summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void ModernGoalIsOneByte(string build)
    {
        var packets = TestBuilds.Packets;
        Assert.Equal(new RoundBfRunnerGoalReported(2), BfRunnerBinding.ReadGoal(packets, build, [2]));
        Assert.Throws<PacketFormatException>(() => BfRunnerBinding.ReadGoal(packets, build, Convert.FromHexString("02000000")));
        Assert.Equal(new RoundBfRunnerCheckpointReported(17),
            BfRunnerBinding.ReadCheckpoint(packets, build, Convert.FromHexString("11000000")));
        // The modern notices reuse 761 and 762: one sign-extended team byte (kr-1256 sub_6D55D0, cn-774 sub_897E80,
        // tw-454 sub_70B3F0) and the seat and region words.
        Assert.Equal("02", Convert.ToHexString(BfRunnerBinding.WriteFinished(packets, build, new(2))));
        Assert.Equal("0300000011000000",
            Convert.ToHexString(BfRunnerBinding.WriteCheckpointReached(packets, build, new(3, 17))));
        BfRunnerBinding.RequireLayouts(packets, build);
    }

    /// <summary>
    /// The track item touch is three words (kr-812 sub_7AA780, kr-1256 sub_6DBFF0) and the item box's answer is the
    /// three-word item-slot grant, on every build that runs the race.
    /// </summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-705")]
    [InlineData("cn-706")]
    [InlineData("cn-717")]
    [InlineData("id-1809")]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void ItemFramesPinThreeWords(string build)
    {
        var packets = TestBuilds.Packets;
        Assert.Equal(new RoundBfRunnerItemTouched(101, -1),
            BfRunnerBinding.ReadItem(packets, build, Convert.FromHexString("FFFFFFFF65000000FFFFFFFF")));
        Assert.Equal(new RoundBfRunnerItemTouched(0, 7),
            BfRunnerBinding.ReadItem(packets, build, Convert.FromHexString("FFFFFFFF0000000007000000")));
        Assert.Throws<PacketFormatException>(() => BfRunnerBinding.ReadItem(packets, build, new byte[8]));
        Assert.Equal("040000009200000001000000",
            Convert.ToHexString(BfRunnerBinding.WriteItemGranted(packets, build, new(4, 146, 1))));
    }

    /// <summary>The progress list is a word count, then a seat word and a progress word per element.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-705")]
    [InlineData("cn-706")]
    [InlineData("cn-717")]
    [InlineData("id-1809")]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void ProgressListPinsCountAndPairs(string build)
    {
        var body = Convert.FromHexString("02000000" + "00000000" + "35750000" + "03000000" + "10270000");
        var report = BfRunnerBinding.ReadProgress(TestBuilds.Packets, build, body);
        Assert.Equal([new BfRunnerSeatProgress(0, 30005), new BfRunnerSeatProgress(3, 10000)], report.Seats);
        Assert.Empty(BfRunnerBinding.ReadProgress(TestBuilds.Packets, build, new byte[4]).Seats);
        Assert.Throws<PacketFormatException>(() => BfRunnerBinding.ReadProgress(TestBuilds.Packets, build, body[..^1]));
        // kr-812 sub_50DFC0 walks 32 seats, so a longer list is not a client's.
        var oversized = new byte[4 + 33 * 8];
        oversized[0] = 33;
        Assert.Throws<PacketFormatException>(() => BfRunnerBinding.ReadProgress(TestBuilds.Packets, build, oversized));
    }

    /// <summary>The enabled exchange delivers the three reports, writes both notices and unloads.</summary>
    [Theory]
    [InlineData("kr-812", "kr", 812)]
    [InlineData("cn-706", "cn", 706)]
    public async Task EnabledExchangeDispatchesAndUnloads(string build, string region, int version)
    {
        var protocol = build + "/game";
        var profile = new ClientProfile(build, region, version, 0, 0, build);
        var root = Directory.GetParent(TestBuilds.PacketsDirectory())!.FullName;
        var table = OpcodeTableLoader.Load(Path.Combine(root, "profiles", build, "opcodes.yaml"), profile)
            .ForConnection(OpcodeConnection.Game);
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var context = fixture.Context(build, protocol, "dialect.rooms", extra: new() { ["bf_runner"] = true });
        context.BindServices(typeof(RoomsPlugin).GetCustomAttribute<PluginAttribute>()!);
        var session = new MatchMessagesMountTests.Session(profile);
        try
        {
            await new RoomsPlugin().ActivateAsync(context, default);
            Assert.Equal(745, table.C2S("gcpBFRunnerOver"));
            Assert.Equal(746, table.C2S("gcpBFRunnerCheckPoint"));
            Assert.Equal(747, table.C2S("gcpBFRunnerCheckPointValue"));
            await fixture.Packets.DispatchAsync(protocol, session, new(745, Convert.FromHexString("01000000")), default);
            await fixture.Packets.DispatchAsync(protocol, session, new(746, Convert.FromHexString("04000000")), default);
            await fixture.Packets.DispatchAsync(protocol, session, new(747, Convert.FromHexString("010000000500000020A10700")), default);
            Assert.Equal(new RoundBfRunnerGoalReported(1), fixture.Messages.Received[0]);
            Assert.Equal(new RoundBfRunnerCheckpointReported(4), fixture.Messages.Received[1]);
            var progress = Assert.IsType<RoundBfRunnerProgressReported>(fixture.Messages.Received[2]);
            Assert.Equal([new BfRunnerSeatProgress(5, 500000)], progress.Seats);

            await Assert.Single(fixture.Services.GetAll<IMessageSender<RoundBfRunnerCheckpointReached>>())
                .SendAsync(session, new(5, 4), default);
            await Assert.Single(fixture.Services.GetAll<IMessageSender<RoundBfRunnerFinished>>()).SendAsync(session, new(2), default);
            Assert.Equal([(ushort)751, (ushort)750], session.Sent);
            Assert.Equal(["0500000004000000", "02000000"], session.Bodies.Select(Convert.ToHexString));
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(fixture.Packets.Handlers);
        Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<RoundBfRunnerGoalReported>>());
        Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundBfRunnerFinished>>());
    }

    /// <summary>Unsupported surfaces reject the option before any route or service is published.</summary>
    [Theory]
    [InlineData("waiting_rooms_only")]
    [InlineData("menu_only")]
    [InlineData("typed_play_rooms")]
    public async Task UnsupportedSurfaceFailsBeforeMount(string option)
    {
        var profile = new ClientProfile("kr-812", "kr", 812, 0, 0, "kr-812");
        using var fixture = new MatchMessagesMountTests.Fixture(new OpcodeTable(profile, []));
        var context = fixture.Context("kr-812", "kr-812/game", "dialect.rooms", extra: new()
            { ["bf_runner"] = true, [option] = option != "typed_play_rooms" });
        context.BindServices(typeof(RoomsPlugin).GetCustomAttribute<PluginAttribute>()!);
        try
        {
            var error = await Assert.ThrowsAsync<ConfigException>(async () => await new RoomsPlugin().ActivateAsync(context, default));
            Assert.Contains("bf_runner", error.Message);
            Assert.Empty(fixture.Packets.Handlers);
        }
        finally { await context.UnwindAsync(); }
    }
}
