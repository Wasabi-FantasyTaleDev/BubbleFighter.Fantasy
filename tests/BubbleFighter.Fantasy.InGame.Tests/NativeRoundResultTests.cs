using System.Buffers.Binary;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using BubbleFighter.Fantasy.Tests;
using BubbleFighter.Fantasy.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>Pins the shared modern result adapter against native result opcodes, row words and return sequences.</summary>
public sealed class NativeRoundResultTests
{
    /// <summary>Final team results announce draw/red/blue while live rows retain an unspecified winner.</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public async Task FinalScoreboardPublishesDecidedTeam(string build)
    {
        var packets = PacketTables.Load(SharedDialectRows.PacketsDirectory());
        var profile = TestWireDialects.Get(build).Profile;
        var table = OpcodeTableLoader.Load(Path.Combine(Directory.GetParent(SharedDialectRows.PacketsDirectory())!.FullName,
            "profiles", build, "opcodes.yaml"), profile);
        var adapter = new RoundResultBinding(build + "/game", table, packets, build, [1], NullLogger.Instance);
        var context = new PluginContext(new PluginRow { Id = "result", Plugin = "result", Source = "test" },
            new ServiceRegistry(), new EventBus(NullLogger.Instance), NullLogger.Instance);
        try
        {
            await new PacketRouterPlugin().ActivateAsync(context, default);
            await new MessageRouterPlugin().ActivateAsync(context, default);
            adapter.Mount(context);
            var code = table.S2C(build == "kr-1256" ? "Unknown614" : "gspRoundScoreboard_");
            foreach (var winner in new[] { 0, 1, 2 })
            {
                var session = new RecordingSession(7, profile);
                await adapter.SendAsync(session, new RoundScoreboard(7, winner, [3, 2], [], WithRewards: true) { ModeId = 1 }, default);
                var body = session.Body(code);
                Assert.Equal(winner, body[1]);
                Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(2)));
                Assert.Equal(3, BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(6)));
                Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(18)));
            }
            var invalid = new RecordingSession(7, profile);
            await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.SendAsync(invalid,
                new RoundScoreboard(7, 36, [0, 0], [], WithRewards: true) { ModeId = 1 }, default).AsTask());
            Assert.Empty(invalid.Opcodes);
        }
        finally { await context.UnwindAsync(); }
    }

    /// <summary>Native result adapters retain reward word positions, the final mask bit, tails and ordered room return.</summary>
    [Theory]
    [InlineData("kr-1256", 985, 612, 614, 615, 620, 244, 22, 17, false)]
    [InlineData("cn-774", 985, 603, 627, 631, 634, 250, 23, 32, false)]
    [InlineData("tw-454", 984, 603, 627, 631, 634, 243, 23, 16, false)]
    [InlineData("cn-774", 985, 603, 627, 631, 634, 250, 23, 32, true)]
    [InlineData("tw-454", 984, 603, 627, 631, 634, 243, 23, 16, true)]
    public async Task SharedResultsKeepNativeBodiesAndReturnOrder(string build, int live, int open, int board, int reward,
        int reset, int rowLength, int progressWord, int tailLength, bool remap)
    {
        var tables = PacketTables.Load(SharedDialectRows.PacketsDirectory());
        var profile = TestWireDialects.Get(build).Profile;
        var root = Directory.GetParent(SharedDialectRows.PacketsDirectory())!.FullName;
        IOpcodeTable table = OpcodeTableLoader.Load(Path.Combine(root, "profiles", build, "opcodes.yaml"), profile);
        var shift = remap ? 1000 : 0;
        if (remap)
            table = new OpcodeTable(profile, table.All.Select(entry => entry with { Value = checked((ushort)(entry.Value + shift)) }));
        var protocol = build + "/game";
        var services = new ServiceRegistry();
        var events = new EventBus(NullLogger.Instance);
        var infrastructure = Context("infrastructure");
        var binding = Context("results");
        var session = new RecordingSession(7, profile);
        var completed = 0;
        try
        {
            await new PacketRouterPlugin().ActivateAsync(infrastructure, default);
            await new MessageRouterPlugin().ActivateAsync(infrastructure, default);
            infrastructure.Effect(services.Get<IMessageRouter>().For(infrastructure).On<RoundEndRequested>(protocol, (_, _, _) =>
            {
                completed++;
                return ValueTask.CompletedTask;
            }));
            new RoomCommandsBinding(protocol, table, tables, build, NullLogger.Instance, roundsBound: true).Mount(binding);
            var adapter = new RoundResultBinding(protocol, table, tables, build, [1], NullLogger.Instance);
            adapter.Mount(binding);
            Assert.True(adapter.EndBeforeScoreboard);
            Assert.True(adapter.SingleScoreboard);

            var end = table.C2S(build == "kr-1256" ? "Unknown625" : "gcpReqEndStage_");
            await services.Get<IPacketRouter>().DispatchAsync(protocol, session, new(end, ReadOnlyMemory<byte>.Empty), default);
            Assert.Equal(1, completed);
            await Assert.ThrowsAsync<PacketFormatException>(() => services.Get<IPacketRouter>()
                .DispatchAsync(protocol, session, new(end, new byte[] { 0 }), default).AsTask());
            Assert.Equal(1, completed);

            await adapter.SendAsync(session, new RoundScoreUpdated(7, 1, [11, 22], []), default);
            Assert.Equal(new byte[] { 1, 11, 0, 255, 255, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, session.Bodies((ushort)(live + shift))[0]);
            Assert.Equal(new byte[] { 2, 22, 0, 255, 255, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, session.Bodies((ushort)(live + shift))[1]);
            await adapter.SendAsync(session, new RoundEnded(7, RoundOutcome.Won) { TeamTotals = [11, 22] }, default);
            Assert.Equal(Convert.FromHexString("0B00000016000000"), session.Body((ushort)(open + shift)));

            var rows = new RoundScoreboardRow[]
            {
                new(35, "Q", 1, 9, new(35, 0, 0, 0, 0), null) { UserNumber = 7 },
                new(0, "P", 2, 3, new(0, 0, 0, 0, 0), new(42, 1001, 1002, 1003, 1004, 1005, false, [])) { UserNumber = 42 },
            };
            var scoreboard = new RoundScoreboard(7, 2, [11, 22], rows) { ModeId = 1 };
            await adapter.SendAsync(session, scoreboard, default);
            var body = session.Body((ushort)(board + shift));
            Assert.Equal(38 + rowLength * 2 + 16 + tailLength, body.Length);
            Assert.Equal(Convert.FromHexString("02FF000000000B00000000000000000000001600000000000000000000000100000008000000"), body[..38]);
            CheckRow(body.AsSpan(38, rowLength), "P", 42, 2, 42, 1001);
            CheckRow(body.AsSpan(38 + rowLength, rowLength), "Q", 7, 1, 9, 0);
            Assert.Equal(new byte[16 + tailLength], body[(38 + rowLength * 2)..]);
            var boardCount = session.Bodies((ushort)(board + shift)).Length;
            await adapter.SendAsync(session, scoreboard with { ModeId = 99 }, default);
            Assert.Equal(boardCount, session.Bodies((ushort)(board + shift)).Length);
            foreach (var slot in new[] { -1, 36, 63 })
                await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.SendAsync(session,
                    scoreboard with { Rows = [rows[0] with { Slot = slot }] }, default).AsTask());
            await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.SendAsync(session,
                scoreboard with { Rows = [rows[0], rows[0]] }, default).AsTask());
            Assert.Equal(boardCount, session.Bodies((ushort)(board + shift)).Length);

            await adapter.SendAsync(session, new ResultStageAdvanced(7, ResultStage.Reward), default);
            Assert.Equal(new byte[1], session.Body((ushort)(reward + shift)));
            var room = new RoomView(7, "test", 1, "test", 4, 0, false, [], []);
            await Assert.Single(services.GetAll<IMessageSender<RoomEntered>>()).SendAsync(session,
                new RoomEntered(room, 0) { Reason = RoomEntryReason.RoundReturned }, default);
            Assert.Equal(new ushort[] { (ushort)(live + shift), (ushort)(live + shift), (ushort)(open + shift),
                (ushort)(board + shift), (ushort)(reward + shift), table.S2C("gspUdpStop"), (ushort)(reset + shift) }, session.Opcodes);
            Assert.Empty(session.Body(table.S2C("gspUdpStop")));
            Assert.Empty(session.Body((ushort)(reset + shift)));
            await binding.UnwindAsync();
            Assert.Empty(services.GetAll<IMessageSender<RoundScoreboard>>());
            Assert.Empty(services.GetAll<IInboundMessageBinding<RoundEndRequested>>());
            await services.Get<IPacketRouter>().DispatchAsync(protocol, session, new(end, ReadOnlyMemory<byte>.Empty), default);
            Assert.Equal(1, completed);
        }
        finally
        {
            await binding.UnwindAsync();
            await infrastructure.UnwindAsync();
        }

        PluginContext Context(string id) => new(new PluginRow { Id = id, Plugin = id, Source = "test" },
            services, events, NullLogger.Instance);
        void CheckRow(ReadOnlySpan<byte> bytes, string nickname, int user, byte team, int level, int firstReward)
        {
            Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(bytes));
            Assert.Equal(user, BinaryPrimitives.ReadInt32LittleEndian(bytes[4..]));
            Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(bytes[8..]));
            Assert.Equal(nickname[0], (char)BinaryPrimitives.ReadUInt16LittleEndian(bytes[12..]));
            Assert.Equal(team, bytes[14]);
            Assert.Equal(level, BinaryPrimitives.ReadInt32LittleEndian(bytes[15..]));
            for (var word = 1; word < progressWord; word++)
                Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(bytes[(15 + word * 4)..]));
            for (var word = 0; word < 5; word++)
                Assert.Equal(firstReward == 0 ? 0 : firstReward + word,
                    BinaryPrimitives.ReadInt32LittleEndian(bytes[(15 + (progressWord + word) * 4)..]));
        }
    }

    /// <summary>Star modes carry each seat's absolute count in the third live-score list and stat 19 on the named seat.</summary>
    [Fact]
    public async Task LiveStarSlotsProjectAbsoluteCounts()
    {
        var tables = PacketTables.Load(SharedDialectRows.PacketsDirectory());
        var profile = TestWireDialects.Get("kr-1256").Profile;
        var root = Directory.GetParent(SharedDialectRows.PacketsDirectory())!.FullName;
        var table = OpcodeTableLoader.Load(Path.Combine(root, "profiles", "kr-1256", "opcodes.yaml"), profile);
        var services = new ServiceRegistry();
        var context = new PluginContext(new PluginRow { Id = "result", Plugin = "result", Source = "test" },
            services, new EventBus(NullLogger.Instance), NullLogger.Instance);
        try
        {
            await new PacketRouterPlugin().ActivateAsync(context, default);
            await new MessageRouterPlugin().ActivateAsync(context, default);
            var adapter = new RoundResultBinding("kr-1256/game", table, tables, "kr-1256", [41], NullLogger.Instance);
            adapter.Mount(context);
            var rows = new RoundScoreboardRow[]
            {
                new(0, "A", 1, 1, new RoundSeatStats(0, 0, 0, 0, 0) { Stars = 5 }, null),
                new(1, "B", 2, 1, new RoundSeatStats(1, 0, 0, 0, 0) { Stars = 7 }, null),
            };
            var board = new RoundScoreboard(7, 1, [5, 7], rows)
            {
                ModeId = 41,
                Presentation = RoundScoreboardPresentation.StarTeam,
            };
            var session = new RecordingSession(7, profile);
            await adapter.SendAsync(session, new RoundScoreUpdated(7, 1, [5, 7], [rows[0].Stats], board), default);
            var bodies = session.Bodies(table.S2C("gspUpdateScoreData"));
            Assert.Equal(4, bodies.Length);
            Assert.All(bodies.Take(2), body => Assert.Equal(23, body.Length));
            Assert.Equal(Convert.FromHexString("010500FFFF000000000000000002000000010500020700"), bodies[0]);
            Assert.Equal(Convert.FromHexString("020700FFFF000000000000000002000000010500020700"), bodies[1]);
            Assert.Equal(Convert.FromHexString("00000000FF010000001305000000000000000000"), bodies[2]);
            Assert.Equal(Convert.FromHexString("00000001FF010000001307000000000000000000"), bodies[3]);

            await adapter.SendAsync(session, board with
            {
                TimeOver = true,
                BannerFirstScore = 5,
                BannerSecondScore = 7,
            }, default);
            var timeOverBody = session.Body(table.S2C("Unknown614"));
            var timeOverValues = PacketCodec.Read(tables, "kr-1256", "ScoreRow", timeOverBody.AsSpan(38, 244));
            Assert.Equal(5, Convert.ToInt32(timeOverValues["unknown_84"]));
            Assert.Equal(5, Convert.ToInt32(timeOverValues["unknown_120"]));
            // Two rows then two gated words per row place the tail at 38 + 2*244 + 4*4.
            var tailValues = PacketCodec.Read(tables, "kr-1256", "ScoreDataTail", timeOverBody.AsSpan(542));
            Assert.Equal((byte)5, tailValues["unknown_19196"]);
            Assert.Equal((byte)7, tailValues["unknown_19197"]);

            await adapter.SendAsync(session, new RoundResultBanner(7, 5, 7), default);
            Assert.Equal(Convert.FromHexString("00000000000000000507"),
                session.Body(table.S2C("Unknown613")));

            await adapter.SendAsync(session, board with { WithRewards = true, TimeOver = true }, default);
            var finalBody = session.Body(table.S2C("Unknown614"));
            var finalLead = PacketCodec.Read(tables, "kr-1256", "ScoreDataLead", finalBody.AsSpan(0, 6));
            Assert.Equal(false, finalLead["banner_gate"]);
            var finalValues = PacketCodec.Read(tables, "kr-1256", "ScoreRow", finalBody.AsSpan(38, 244));
            Assert.Equal(5, Convert.ToInt32(finalValues["unknown_120"]));
            Assert.Equal(5, Convert.ToInt32(finalValues["unknown_84"]));
        }
        finally { await context.UnwindAsync(); }
    }

    /// <summary>Individual star results name the winning seat and carry its stars in the row's +0x78 star field.</summary>
    [Fact]
    public async Task IndividualStarResultCarriesWinnerSeatAndStarWord()
    {
        var tables = PacketTables.Load(SharedDialectRows.PacketsDirectory());
        var profile = TestWireDialects.Get("kr-1256").Profile;
        var root = Directory.GetParent(SharedDialectRows.PacketsDirectory())!.FullName;
        var table = OpcodeTableLoader.Load(Path.Combine(root, "profiles", "kr-1256", "opcodes.yaml"), profile);
        var services = new ServiceRegistry();
        var context = new PluginContext(new PluginRow { Id = "result", Plugin = "result", Source = "test" },
            services, new EventBus(NullLogger.Instance), NullLogger.Instance);
        try
        {
            await new PacketRouterPlugin().ActivateAsync(context, default);
            await new MessageRouterPlugin().ActivateAsync(context, default);
            var adapter = new RoundResultBinding("kr-1256/game", table, tables, "kr-1256", [42], NullLogger.Instance);
            adapter.Mount(context);
            var row = new RoundScoreboardRow(3, "C", 1, 1,
                new RoundSeatStats(3, 0, 0, 0, 0) { Stars = 9 }, null) { UserNumber = 3 };
            var session = new RecordingSession(7, profile);
            await adapter.SendAsync(session, new RoundScoreboard(7, 0, [0, 0], [row], WithRewards: true)
            {
                ModeId = 42,
                Presentation = RoundScoreboardPresentation.StarIndividual,
                WinnerSlot = 3,
            }, default);
            var body = session.Body(table.S2C("Unknown614"));
            Assert.Equal(4, body[1]);

            // Lead (6 bytes) + two 12-byte head entries + presence mask (8 bytes).
            var values = PacketCodec.Read(tables, "kr-1256", "ScoreRow", body.AsSpan(38, 244));
            Assert.Equal(9, Convert.ToInt32(values["unknown_84"]));
            Assert.Equal(9, Convert.ToInt32(values["unknown_120"]));

        }
        finally { await context.UnwindAsync(); }
    }
}
