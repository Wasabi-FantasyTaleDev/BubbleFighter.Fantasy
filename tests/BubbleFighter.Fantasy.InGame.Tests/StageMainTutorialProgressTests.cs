using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.InGame.Handlers;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Persistence.Memory;
using BubbleFighter.Fantasy.Protocol;
using BubbleFighter.Fantasy.Tests;
using BubbleFighter.Fantasy.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>Distinguishes a numbered goal from time-out, early exit and first-run; reloads the player's clear marks.</summary>
public sealed class StageMainTutorialProgressTests
{
    [Theory]
    [InlineData("kr-1256", false)]
    [InlineData("cn-774", true)]
    [InlineData("tw-454", true)]
    public async Task StageMainTutorialProgressOnlyRecordsPlayingGoalAndRestoresOwnNumberedRows(string build, bool picker)
    {
        var directory = (string)TestWireDialects.ModernRoomsConfig()["packets_directory"]!;
        var tables = PacketTables.Load(directory);
        Assert.Equal(picker, tables.CapabilityOf(build, "stage_main_tutorial_list"));
        var profile = TestWireDialects.Get(build).Profile;
        var table = OpcodeTableLoader.Load(Path.Combine(directory, "..", "profiles", build, "opcodes.yaml"), profile);
        var services = new ServiceRegistry();
        var context = new PluginContext(new PluginRow { Id = "tutorial-progress", Plugin = "test", Source = "test" },
            services, new EventBus(NullLogger.Instance), NullLogger.Instance);
        var rig = new RoomFlowTests.Rig();
        var stages = new MemoryTutorialStages(8);
        const string map = "sin_mission_wall_lv_03";
        var tutorials = new TutorialRoomFlow(rig.Registry, new Dictionary<string, int> { [map] = 26 }, stages);
        var protocol = build + "/game";
        try
        {
            await new PacketRouterPlugin().ActivateAsync(context, default);
            await new MessageRouterPlugin().ActivateAsync(context, default);
            var messages = services.Get<IMessageRouter>().For(context);
            var router = services.Get<IPacketRouter>();
            new StageResultBinding(protocol, table, tables, build, NullLogger.Instance).Mount(context);
            context.Effect(messages.On<RoundStageResultReported>(protocol,
                (s, report, ct) => rig.Flow.HandleSoloStageResultAsync(s, report, tutorials, ct)));
            if (picker)
            {
                var binding = new StageMainTutorialListBinding(protocol, table, tables, build, NullLogger.Instance);
                binding.Mount(context);
                context.Effect(messages.On<TutorialStageListRequested>(protocol, (s, _, ct) => tutorials.SendStageMainListAsync(s, binding, ct)));
            }
            async Task End(long user, int outcome, bool playing, int mode = 15)
            {
                var session = new RecordingSession(user, profile);
                var player = session.SetLobbyPlayer(user);
                await tutorials.OpenAsync(session, new(mode, map, 300001, [300001], 0, 0, 50000, true) { GameConnectionOnly = true }, default);
                player.MoveTo(SessionPhase.Room);
                player.MoveTo(SessionPhase.Match);
                if (playing) rig.Registry.TryGet(session.Id)!.MarkPlaying();
                if (picker)
                {
                    await router.DispatchAsync(protocol, session, new(916, Array.Empty<byte>()), default);
                    Assert.Empty(session.Opcodes);
                }
                var body = PacketCodec.Write(tables, build, "ScriptStageResult", new Dictionary<string, object?> { ["outcome"] = outcome });
                await router.DispatchAsync(protocol, session, new(624, body), default);
                Assert.Null(rig.Registry.TryGet(session.Id));
            }
            await End(10, 0, true); // the same report is sent on manual quit or time-out.
            await End(11, 1, false);
            await End(12, 1, true, 103);
            await End(13, 1, true);
            foreach (var user in new[] { 10, 11, 12 }) Assert.Empty(await stages.ListAsync(user, default));
            var saved = Assert.Single(await stages.ListAsync(13, default));
            Assert.Equal(26, saved.Stage);
            Assert.Equal(0, saved.BestCount);
            Assert.True(saved.BestSeconds > 0);

            if (picker)
            {
                Assert.Equal((ushort)916, table.C2S("gcpReqSingleStageInfo"));
                Assert.Equal((ushort)919, table.S2C("gspResSingleStageInfo"));
                // A later login on the same account restores a record; a different account gets an empty list.
                foreach (var user in new[] { 13, 10 })
                {
                    var session = new RecordingSession(user + 1000, profile);
                    session.SetLobbyPlayer(user);
                    await router.DispatchAsync(protocol, session, new(916, Array.Empty<byte>()), default);
                    Assert.Equal(new ushort[] { 919 }, session.Opcodes);
                    var fields = PacketCodec.Read(tables, build, "TutorialStageInfo", session.Body(919));
                    var rows = Assert.IsType<object?[]>(fields["cleared"]);
                    if (user == 10) Assert.Empty(rows);
                    else
                    {
                        var row = Assert.IsType<Dictionary<string, object?>>(Assert.Single(rows));
                        Assert.Equal(saved.Stage, row["stage"]);
                        Assert.Equal(saved.BestCount, row["best_count"]);
                        Assert.Equal(saved.BestSeconds, row["best_seconds"]);
                    }
                }
            }
        }
        finally
        {
            await rig.Registry.StopAllAsync();
            await context.UnwindAsync();
        }
    }
}
