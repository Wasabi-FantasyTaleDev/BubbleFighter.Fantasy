using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.InGame.Sessions;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using BubbleFighter.Fantasy.Tests;
using BubbleFighter.Fantasy.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class RoundFlowTests
{
    /// <summary>The native final-death frame reaches peers on the room's selected transport before settlement.</summary>
    [Theory]
    [InlineData("cn-324", false)]
    [InlineData("cn-279", false)]
    [InlineData("cn-324", true)]
    public async Task ZombieFinalDeathReachesPeersBeforeResult(string build, bool relay)
    {
        InGameOptions options = null!;
        var rig = new Rig(configure: configured =>
        {
            options = configured;
            configured.ZombiePvpModeIds = [4];
            configured.ZombieKingDelayMs = 60000;
        });
        var services = new ServiceRegistry();
        var events = new EventBus(NullLogger.Instance);
        var infrastructure = Context("infrastructure");
        var binding = Context("feed");
        try
        {
            await StartZombiePvpAsync(rig);
            await rig.Rooms.HandleTeamChangeAsync(rig.Guest, new(), default);
            var room = rig.Registry.Find(rig.RoomId)!;
            var king = room.FindByGame(rig.Guest.Id)!;
            var human = room.FindByGame(rig.Host.Id)!;
            ISession target = rig.Host;
            if (relay)
            {
                options.NativeMatchRelay = true;
                foreach (var seat in room.Seats)
                {
                    seat.Relay = new RecordingSession(seat.Game.Id + 90);
                    seat.RelayGeneration = room.StartGeneration;
                }
                target = human.Relay!;
            }
            var tables = PacketTables.Load(SharedDialectRows.PacketsDirectory());
            var root = Directory.GetParent(SharedDialectRows.PacketsDirectory())!.FullName;
            var game = OpcodeTableLoader.Load(Path.Combine(root, "profiles", build, "opcodes.yaml"), TestWireDialects.Get(build).Profile);
            await new PacketRouterPlugin().ActivateAsync(infrastructure, default);
            await new MessageRouterPlugin().ActivateAsync(infrastructure, default);
            var messages = services.Get<IMessageRouter>().For(infrastructure);
            infrastructure.Effect(messages.On<RoundFeedSent>(build + "/game", rig.Round.HandleFeedAsync));
            infrastructure.Effect(messages.On<RoundLifeReported>(build + "/game", async (sender, message, ct) =>
            {
                Assert.Single(rig.Feed.To(target));
                Assert.Equal(RoomPhase.Playing, room.Phase);
                await rig.Round.HandleLifeReportedAsync(sender, message, ct);
            }));
            new RoundFeedBinding(build + "/game", build + "/relay", game,
                game.ForConnection(OpcodeConnection.Relay), tables, build).Mount(binding);
            var body = BitConverter.GetBytes(RoomRegistry.SceneEntity(king.Slot));
            await services.Get<IPacketRouter>().DispatchAsync(build + "/game", rig.Guest,
                new(game.C2S("gppDead"), body), default);
            var received = Assert.Single(rig.Feed.To(target));
            Assert.Equal(body, received.Payload.ToArray());
            Assert.Equal(RoomPhase.Result, room.Phase);
            Assert.Equal(ZombieHuman, room.DecidedWinnerTeam);
            if (relay) Assert.Empty(rig.Feed.To(rig.Host));
        }
        finally
        {
            await binding.UnwindAsync();
            await infrastructure.UnwindAsync();
            await rig.Registry.StopAllAsync();
        }

        PluginContext Context(string id) => new(new PluginRow { Id = id, Plugin = "test", Source = "test" }, services, events, NullLogger.Instance);
    }

    /// <summary>Early king death evidence credits once but cannot skip the owner's later death completion.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ZombieKingWaitsForNativeDeathCompletion(bool scoreFirst)
    {
        var (rig, context) = ZombieScoringRig();
        try
        {
            await StartZombiePvpAsync(rig);
            await rig.Rooms.HandleTeamChangeAsync(rig.Guest, new(), default);
            var room = rig.Registry.Find(rig.RoomId)!;
            var human = room.FindByGame(rig.Host.Id)!;
            var king = room.FindByGame(rig.Guest.Id)!;
            var actor = RoomRegistry.SceneEntity(king.Slot);
            var score = new RoundFinishingBlowReported(human.Slot)
            {
                Victim = king.Slot, HasVictimReference = true,
            };
            if (scoreFirst)
                await rig.Round.HandleFinishingBlowReportedAsync(rig.Host, score, default);
            else
                await rig.Round.HandleLifeReportedAsync(rig.Guest, new(RoundLifeEvent.Died, actor)
                {
                    Initiator = RoomRegistry.SceneEntity(human.Slot),
                }, default);

            Assert.Equal(SeatLife.Dead, king.Life.State);
            Assert.False(king.Life.DeathCleaned);
            Assert.Equal(RoomPhase.Playing, room.Phase);
            Assert.Null(room.DecidedWinnerTeam);
            await rig.Round.HandleEndRequestedAsync(rig.Host, new() { StageSucceeded = true }, default);
            await rig.Round.HandleLifeReportedAsync(rig.Host, new(RoundLifeEvent.DeathCleaned, actor), default);
            Assert.Equal(RoomPhase.Playing, room.Phase);
            Assert.False(king.Life.DeathCleaned);

            await rig.Round.HandleFinishingBlowReportedAsync(rig.Host, score, default);
            Assert.Equal(1, room.Scoring.Stats(human.Slot).Kills);
            Assert.Equal(1, room.Scoring.Stats(king.Slot).Deaths);
            await rig.Round.HandleLifeReportedAsync(rig.Guest, new(RoundLifeEvent.DeathCleaned, actor), default);
            Assert.True(king.Life.DeathCleaned);
            Assert.Equal(RoomPhase.Result, room.Phase);
            Assert.Equal(1, room.DecidedWinnerTeam);
            Assert.Empty(rig.Respawned.To(rig.Guest));
        }
        finally { await rig.Registry.StopAllAsync(); await context.UnwindAsync(); }
    }

    /// <summary>The king's owned completion is sufficient when an earlier score was absent or used another connection.</summary>
    [Fact]
    public async Task ZombieKingCompletionDoesNotRequireAnEarlierScore()
    {
        var rig = ZombiePvpRig();
        try
        {
            await StartZombiePvpAsync(rig);
            await rig.Rooms.HandleTeamChangeAsync(rig.Guest, new(), default);
            var room = rig.Registry.Find(rig.RoomId)!;
            var king = room.FindByGame(rig.Guest.Id)!;
            await rig.Round.HandleLifeReportedAsync(rig.Guest,
                new(RoundLifeEvent.DeathCleaned, RoomRegistry.SceneEntity(king.Slot)), default);
            Assert.Equal(RoomPhase.Result, room.Phase);
            Assert.True(king.Life.DeathCleaned);
            Assert.Equal(1, room.DecidedWinnerTeam);
            Assert.Equal(0, room.Scoring.Stats(room.MasterSlot).Kills);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>A human's completed death still waits for the native conversion that removes the final human.</summary>
    [Fact]
    public async Task ZombieHumanCompletionStillWaitsForConversion()
    {
        var rig = ZombiePvpRig();
        try
        {
            await StartZombiePvpAsync(rig);
            await rig.Rooms.HandleTeamChangeAsync(rig.Guest, new(), default);
            var room = rig.Registry.Find(rig.RoomId)!;
            var human = room.FindByGame(rig.Host.Id)!;
            await rig.Round.HandleLifeReportedAsync(rig.Host,
                new(RoundLifeEvent.DeathCleaned, RoomRegistry.SceneEntity(human.Slot)), default);
            Assert.True(human.Life.DeathCleaned);
            Assert.Equal(RoomPhase.Playing, room.Phase);
            Assert.Null(room.DecidedWinnerTeam);
            await rig.Rooms.HandleTeamChangeAsync(rig.Host, new(), default);
            Assert.Equal(RoomPhase.Result, room.Phase);
            Assert.Equal(14, room.DecidedWinnerTeam);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>A respawn-wait report and anonymous end flag cannot stand in for a king's completed death presentation.</summary>
    [Fact]
    public async Task ZombieKingRespawnReportDoesNotCompleteDeath()
    {
        var rig = ZombiePvpRig();
        try
        {
            await StartZombiePvpAsync(rig);
            await rig.Rooms.HandleTeamChangeAsync(rig.Guest, new(), default);
            var room = rig.Registry.Find(rig.RoomId)!;
            var king = room.FindByGame(rig.Guest.Id)!;
            var actor = RoomRegistry.SceneEntity(king.Slot);
            await rig.Round.HandleDeathReportedAsync(rig.Guest, new(actor, 10000), default);
            await rig.Round.HandleEndRequestedAsync(rig.Host, new() { StageSucceeded = true }, default);
            Assert.Equal(RoomPhase.Playing, room.Phase);
            Assert.False(king.Life.DeathCleaned);
            await rig.Round.HandleLifeReportedAsync(rig.Guest, new(RoundLifeEvent.DeathCleaned, actor), default);
            Assert.Equal(RoomPhase.Result, room.Phase);
            Assert.Equal(1, room.DecidedWinnerTeam);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }
}
