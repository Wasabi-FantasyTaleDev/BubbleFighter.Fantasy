using System.Buffers.Binary;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using BubbleFighter.Fantasy.Tests;
using BubbleFighter.Fantasy.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>Checks that the composed Korean profile publishes the audited team AI result table.</summary>
public sealed class AiRoomResultProfileTests
{
    /// <summary>The native result sender includes team AI score rows before rewards while retaining unaudited mode gates.</summary>
    [Theory]
    [InlineData(38, true)]
    [InlineData(39, true)]
    [InlineData(1, true)]
    [InlineData(13, false)]
    [InlineData(99, false)]
    public async Task ComposedAiRoomResultModesPublishTheNativeScoreboard(int mode, bool supported)
    {
        var root = Directory.GetParent(SharedDialectRows.PacketsDirectory())!.FullName;
        var rows = new ProfileComposer(Path.Combine(root, "profiles"),
            key => key is "BFF_MYSQL_PASSWORD" or "BFF_ADMIN_TOKEN" ? "isolated-test-placeholder" : null)
            .Compose("kr-1256", []);
        var options = new PluginConfig(Assert.Single(rows, row => row.Id == "dialect.rooms")).Bind<RoomsOptions>();
        var packets = PacketTables.Load(SharedDialectRows.PacketsDirectory());
        var profile = TestWireDialects.Get("kr-1256").Profile;
        var table = OpcodeTableLoader.Load(Path.Combine(root, "profiles", "kr-1256", "opcodes.yaml"), profile);
        var context = new PluginContext(new PluginRow { Id = "ai-results", Plugin = "test", Source = "test" },
            new ServiceRegistry(), new EventBus(NullLogger.Instance), NullLogger.Instance);
        try
        {
            await new PacketRouterPlugin().ActivateAsync(context, default);
            await new MessageRouterPlugin().ActivateAsync(context, default);
            new RoundResultBinding("kr-1256/game", table, packets, "kr-1256", options.ResultScoreboardModes,
                NullLogger.Instance).Mount(context);
            var sender = Assert.IsType<RoundResultSender>(Assert.Single(context.Services.GetAll<IMessageSender<RoundScoreboard>>()));
            var session = new RecordingSession(11, profile);
            var board = new RoundScoreboard(7, 2, [4, 9],
            [
                new(3, "B", 2, 7, new(3, 0, 0, 0, 0), null) { UserNumber = 11 },
                new(1, "H", 1, 7, new(1, 0, 0, 0, 0), null) { UserNumber = 11 },
            ], WithRewards: true) { ModeId = mode };
            Assert.True(sender.EndBeforeScoreboard);
            Assert.True(sender.SingleScoreboard);
            await sender.SendAsync(session, new RoundEnded(7, RoundOutcome.Lost) { ModeId = mode }, default);
            await sender.SendAsync(session, board, default);
            await sender.SendAsync(session, new ResultStageAdvanced(7, ResultStage.Reward), default);

            var open = table.S2C("Unknown612");
            var scoreboard = table.S2C("Unknown614");
            var reward = table.S2C("Unknown615");
            Assert.Equal(supported ? new[] { open, scoreboard, reward } : new[] { open, reward }, session.Opcodes);
            Assert.Equal(supported, sender.SupportsScoreboard(mode));
            Assert.Equal(new byte[8], session.Body(open));
            Assert.Equal(new byte[1], session.Body(reward));
            if (!supported)
            {
                Assert.Empty(session.Bodies(scoreboard));
                return;
            }

            var body = Assert.Single(session.Bodies(scoreboard));
            Assert.Equal(559, body.Length);
            Assert.Equal(2, body[0]);
            Assert.Equal(2, body[1]);
            Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(2)));
            Assert.Equal(4, BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(6)));
            Assert.Equal(9, BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(18)));
            Assert.Equal(10UL, BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(30)));
            CheckRow(body.AsSpan(38, 244), 'H', 1);
            CheckRow(body.AsSpan(282, 244), 'B', 2);
            Assert.Equal(new byte[33], body[526..]);
        }
        finally { await context.UnwindAsync(); }

        static void CheckRow(ReadOnlySpan<byte> row, char nickname, byte team)
        {
            Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(row));
            Assert.Equal(11, BinaryPrimitives.ReadInt32LittleEndian(row[4..]));
            Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(row[8..]));
            Assert.Equal(nickname, (char)BinaryPrimitives.ReadUInt16LittleEndian(row[12..]));
            Assert.Equal(team, row[14]);
            Assert.Equal(7, BinaryPrimitives.ReadInt32LittleEndian(row[15..]));
        }
    }
}
