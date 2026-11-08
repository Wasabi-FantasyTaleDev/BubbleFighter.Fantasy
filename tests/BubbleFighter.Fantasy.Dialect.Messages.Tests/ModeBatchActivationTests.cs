using System.Reflection;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Exercises the complete optional exchanges across independently audited native dialects.</summary>
public sealed class ModeBatchActivationTests
{
    /// <summary>Gets the audited builds with their own full-score kind and seat width.</summary>
    public static IEnumerable<object[]> Builds => new[]
    {
        ("kr-812", 27, false), ("cn-717", 27, false), ("id-400", 27, true),
        ("cn-404", 27, true), ("cn-408", 27, true), ("cn-706", 27, false),
        ("id-1809", 27, false), ("kr-1256", 75, false), ("cn-774", 27, false), ("tw-454", 27, false),
    }.SelectMany(row => new[] { false, true }.Select(remap => new object[] { row.Item1, row.Item2, row.Item3, remap }));

    /// <summary>Both exchanges dispatch with native or remapped opcodes and remove all effects on unload.</summary>
    [Theory]
    [MemberData(nameof(Builds))]
    public async Task CombinedExchangeDispatchesAndUnloads(string build, int kind, bool wide, bool remap)
    {
        var native = TestBuilds.Opcodes(build);
        var table = remap ? new OpcodeTable(native.Profile, native.All.Select(entry => entry with
            { Value = checked((ushort)(entry.Value + 1000)) })) : native;
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var modern = TestBuilds.Packets.Resolve(build, "RoomEnterGame").Any(field => field.Name == "room_state");
        var context = fixture.Context(build, build + "/game", "dialect.rooms", extra: new()
        {
            ["bomb_mission"] = true, ["captain_mode"] = true, ["modern_rooms"] = modern,
        });
        context.BindServices(typeof(RoomsPlugin).GetCustomAttribute<PluginAttribute>()!);
        var session = new MatchMessagesMountTests.Session(native.Profile);
        try
        {
            await new RoomsPlugin().ActivateAsync(context, default);
            var series = Assert.IsAssignableFrom<IRoundSeriesEntrySender>(Assert.Single(
                fixture.Services.GetAll<IMessageSender<RoomMatchStarting>>()));
            Assert.True(series.SupportsMode(19));
            Assert.Equal(modern ? 3 : 2, series.RoundsToWin);
            var state = Convert.FromHexString("04030201010000000000803F000020C00000C842");
            await Dispatch("gcpBombState", state);
            Assert.Equal(new RoundBombStateReported(RoundBombState.Planting, 0x01020304, 1, -2.5f, 100),
                fixture.Messages.Received[0]);
            await Dispatch("gcpBombMissionOver", Convert.FromHexString(modern
                ? "020500000009000000" : "020000000500000009000000"));
            Assert.Equal(new RoundBombMissionOverReported(2, 5, 9), fixture.Messages.Received[1]);
            await Dispatch("gcpDropCaptain", Convert.FromHexString("02000000"));
            Assert.Equal(new RoundCaptainDropped(2), fixture.Messages.Received[2]);
            var score = PacketCodec.Write(TestBuilds.Packets, build, "RoundScoreReport", new Dictionary<string, object?>
            {
                ["kind"] = kind, ["unknown_8"] = 1_200_000, ["unknown_12"] = 1_300_000,
                ["unknown_16"] = 2, [wide ? "unknown_20" : "reporter_slot"] = 3,
            });
            await Dispatch("gcpGameUpdateScore", score);
            var kill = Assert.IsType<RoundFinishingBlowReported>(fixture.Messages.Received[3]);
            Assert.True(kill.CaptainKill);
            Assert.True(kill.RequiresReporterOwnership);
            Assert.Equal(2, kill.Victim);
            Assert.Equal(3, kill.ReporterSlot);
            await Dispatch("gcpGameUpdateScore", score[..^1]);
            await Dispatch("gcpGameUpdateScore", score.Concat(new byte[1]).ToArray());
            Assert.Equal(4, fixture.Messages.Received.Count);
            await Assert.Single(fixture.Services.GetAll<IMessageSender<RoundBombStateChanged>>())
                .SendAsync(session, new(RoundBombState.Planting, 0x01020304, 1, -2.5f, 100), default);
            var aligned = TestBuilds.Packets.Resolve(build, "gspBombState").Any(field => field.Name == "timer_aligned");
            Assert.Equal(Convert.ToHexString(state) + (aligned ? "00000000" : ""), Convert.ToHexString(session.Bodies[0]));
            await Assert.Single(fixture.Services.GetAll<IMessageSender<RoundCaptainAppointed>>()).SendAsync(session, new(6), default);
            Assert.Equal("06000000", Convert.ToHexString(session.Bodies[1]));
            await Assert.Single(fixture.Services.GetAll<IRoundObservationSender>()).SendAsync(session, new(1_100_000), default);
            Assert.Equal("E0C81000", Convert.ToHexString(session.Bodies[2]));
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(fixture.Packets.Handlers);
        Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<RoundBombStateReported>>());
        Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<RoundCaptainDropped>>());
        Assert.Empty(fixture.Services.GetAll<IMessageSender<RoomMatchStarting>>());
        Assert.Empty(fixture.Services.GetAll<IRoundObservationSender>());

        ValueTask Dispatch(string name, byte[] body) => fixture.Packets.DispatchAsync(build + "/game", session,
            new(table.C2S(name), body), default);
    }
}
