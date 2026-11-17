using System.Buffers.Binary;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Match;
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
    /// <summary>A native cleaned death publishes one observer without requiring the legacy death handle or reviving the actor.</summary>
    [Theory]
    [InlineData("direct")]
    [InlineData("pop")]
    [InlineData("expiry")]
    public async Task NativeObservationCompletesEachAcceptedDeathPath(string path)
    {
        var rig = NativeObservationRig();
        try
        {
            var (room, _, guest) = await StartNativeObservationAsync(rig);
            var request = await NativeObservationRequestAsync(RoomRegistry.SceneEntity(guest.Slot));
            await CompleteNativeObservationDeathAsync(rig, guest, path);
            Assert.Null(guest.Life.DeathActor);
            await rig.Round.HandleSpawnAcceptedAsync(rig.Guest, request, default);
            await rig.Round.HandleSpawnAcceptedAsync(rig.Guest, request, default);
            await rig.Round.HandleLifeReportedAsync(rig.Guest, new(RoundLifeEvent.ResetConfirmed, request.Actor), default);
            Assert.Equal(SeatLife.Dead, guest.Life.State);
            Assert.True(guest.Life.ObservationSent);
            Assert.Null(guest.Life.GrantLink);
            Assert.Equal(RoomPhase.Playing, room.Phase);
            foreach (var member in new[] { rig.Host, rig.Guest })
            {
                Assert.Equal(new RoundPlayerObserved(request.Actor), Assert.Single(rig.Observed.To(member)));
                Assert.Empty(rig.Respawned.To(member));
                Assert.Empty(rig.Ended.To(member));
            }
            await rig.Round.HandleBombMissionOverAsync(rig.Host, new(1, -1, -1), default);
            Assert.Equal(1, room.DecidedWinnerTeam);
            Assert.Single(rig.Ended.To(rig.Host));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Alive, bubbled, uncleaned and cleanup-only characters do not satisfy native observation admission.</summary>
    [Theory]
    [InlineData("alive")]
    [InlineData("bubble")]
    [InlineData("uncleaned")]
    [InlineData("cleanup-only")]
    public async Task NativeObservationRejectsEarlyReset(string state)
    {
        var rig = NativeObservationRig();
        try
        {
            var (_, _, guest) = await StartNativeObservationAsync(rig);
            var actor = RoomRegistry.SceneEntity(guest.Slot);
            if (state != "alive")
                await rig.Round.HandleLifeReportedAsync(rig.Guest, new(state switch
                {
                    "bubble" => RoundLifeEvent.Bubbled,
                    "uncleaned" => RoundLifeEvent.Died,
                    _ => RoundLifeEvent.DeathCleaned,
                }, actor), default);
            await rig.Round.HandleSpawnAcceptedAsync(rig.Guest, await NativeObservationRequestAsync(actor), default);
            Assert.False(guest.Life.ObservationSent);
            Assert.Empty(rig.Observed.To(rig.Host));
            Assert.Empty(rig.Respawned.To(rig.Guest));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>A native request cannot borrow another actor, an unknown actor or a reused numeric connection identity.</summary>
    [Theory]
    [InlineData("foreign")]
    [InlineData("unknown")]
    [InlineData("connection")]
    public async Task NativeObservationRequiresExactActorAndConnection(string kind)
    {
        var rig = NativeObservationRig();
        try
        {
            var (_, host, guest) = await StartNativeObservationAsync(rig);
            await CompleteNativeObservationDeathAsync(rig, guest, "direct");
            var actor = RoomRegistry.SceneEntity(guest.Slot);
            var requestedActor = kind == "foreign" ? RoomRegistry.SceneEntity(host.Slot) : kind == "unknown" ? actor + 1 : actor;
            var reporter = kind == "connection" ? new RecordingSession(rig.Guest.Id) : rig.Guest;
            await rig.Round.HandleSpawnAcceptedAsync(reporter, await NativeObservationRequestAsync(requestedActor), default);
            Assert.False(guest.Life.ObservationSent);
            Assert.Empty(rig.Observed.To(rig.Host));
            await rig.Round.HandleSpawnAcceptedAsync(rig.Guest, await NativeObservationRequestAsync(actor), default);
            Assert.Single(rig.Observed.To(rig.Host));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Owner-admitted legacy observation still requires the exact handle recorded by its separate death report.</summary>
    [Fact]
    public async Task NativeObservationPreservesLegacyDeathHandleAdmission()
    {
        var rig = NativeObservationRig();
        try
        {
            var (_, _, guest) = await StartNativeObservationAsync(rig);
            var actor = RoomRegistry.SceneEntity(guest.Slot);
            await rig.Round.HandleDeathReportedAsync(rig.Guest, new(actor, 0) { ActorSeat = guest.Slot }, default);
            await rig.Round.HandleSpawnAcceptedAsync(rig.Guest, new(actor + 1, 1) { ActorSeat = guest.Slot }, default);
            Assert.Empty(rig.Observed.To(rig.Host));
            await rig.Round.HandleSpawnAcceptedAsync(rig.Guest, new(actor, 1) { ActorSeat = guest.Slot }, default);
            Assert.Single(rig.Observed.To(rig.Host));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Observation leaves the native armed-bomb decision intact: eliminated attackers wait, eliminated defenders lose.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task NativeObservationRetainsArmedBombOutcome(int eliminatedTeam)
    {
        var rig = NativeObservationRig();
        try
        {
            var (room, host, guest) = await StartNativeObservationAsync(rig);
            await rig.Round.HandleBombStateAsync(rig.Host, new(RoundBombState.Planting, 11, 0, 0, 0), rig.BombChanged, default);
            await rig.Round.HandleBombStateAsync(rig.Host, new(RoundBombState.Countdown, 11, 0, 0, 0), rig.BombChanged, default);
            var victim = eliminatedTeam == 1 ? host : guest;
            await CompleteNativeObservationDeathAsync(rig, victim, "direct");
            await rig.Round.HandleSpawnAcceptedAsync(victim.Game,
                await NativeObservationRequestAsync(RoomRegistry.SceneEntity(victim.Slot)), default);
            Assert.Single(rig.Observed.To(rig.Host));
            Assert.Equal(RoomPhase.Playing, room.Phase);
            Assert.Equal(RoundBombState.Countdown, room.Bomb.State);
            Assert.Empty(rig.Ended.To(rig.Host));
            await rig.Round.HandleBombMissionOverAsync(rig.Host, new(3 - eliminatedTeam, 11, -1), default);
            Assert.Equal(eliminatedTeam == 1 ? RoomPhase.Playing : RoomPhase.Result, room.Phase);
            Assert.Equal(eliminatedTeam == 1 ? 0 : 1, rig.Ended.To(rig.Host).Length);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>An old queued request cannot observe a newly dead replacement scene; a fresh request can.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeObservationRejectsReplacementGeneration(bool native)
    {
        var rig = NativeObservationRig();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? transition = null;
        try
        {
            var (room, _, guest) = await StartNativeObservationAsync(rig);
            var actor = RoomRegistry.SceneEntity(guest.Slot);
            var request = native ? await NativeObservationRequestAsync(actor) : new RoundSpawnAccepted(actor, 1) { ActorSeat = guest.Slot };
            if (native) await CompleteNativeObservationDeathAsync(rig, guest, "direct");
            else await rig.Round.HandleDeathReportedAsync(rig.Guest, new(actor, 0) { ActorSeat = guest.Slot }, default);
            transition = room.Executor.ExecuteAsync(async _ =>
            {
                entered.SetResult();
                await release.Task;
                lock (room.Gate)
                {
                    room.ResetToWaiting();
                    room.MarkStarting();
                    room.MarkPlaying();
                    rig.Registry.FallBackToGameCore(room, guest);
                    if (native) { guest.Life.Die(); guest.Life.CleanDeath(); }
                    else guest.Life.RecordDeath(actor, false);
                }
            });
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var stale = rig.Round.HandleSpawnAcceptedAsync(rig.Guest, request, default).AsTask();
            Assert.False(stale.IsCompleted);
            release.SetResult();
            await Task.WhenAll(transition, stale).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(guest.Life.ObservationSent);
            Assert.Empty(rig.Observed.To(rig.Host));
            await rig.Round.HandleSpawnAcceptedAsync(rig.Guest, request, default);
            Assert.Single(rig.Observed.To(rig.Host));
        }
        finally
        {
            release.TrySetResult();
            if (transition is not null) await transition;
            await rig.Registry.StopAllAsync();
        }
    }

    /// <summary>A cleanup queued before a reset supplies its state before observation admission is evaluated.</summary>
    [Fact]
    public async Task NativeObservationAcceptsCleanupAlreadyQueuedAhead()
    {
        var rig = NativeObservationRig();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? blocker = null;
        try
        {
            var (room, _, guest) = await StartNativeObservationAsync(rig);
            var actor = RoomRegistry.SceneEntity(guest.Slot);
            var request = await NativeObservationRequestAsync(actor);
            await rig.Round.HandleLifeReportedAsync(rig.Guest, new(RoundLifeEvent.Died, actor), default);
            blocker = room.Executor.ExecuteAsync(async _ =>
            {
                entered.SetResult();
                await release.Task;
            });
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var cleanup = rig.Round.HandleLifeReportedAsync(rig.Guest, new(RoundLifeEvent.DeathCleaned, actor), default).AsTask();
            var reset = rig.Round.HandleSpawnAcceptedAsync(rig.Guest, request, default).AsTask();
            Assert.False(cleanup.IsCompleted);
            Assert.False(reset.IsCompleted);
            Assert.False(guest.Life.DeathCleaned);
            release.SetResult();
            await Task.WhenAll(blocker, cleanup, reset).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(guest.Life.DeathCleaned);
            Assert.True(guest.Life.ObservationSent);
            Assert.Single(rig.Observed.To(rig.Host));
            Assert.Empty(rig.Respawned.To(rig.Guest));
        }
        finally
        {
            release.TrySetResult();
            if (blocker is not null) await blocker;
            await rig.Registry.StopAllAsync();
        }
    }

    /// <summary>A cancelled native round never emits observation even when its last death completed.</summary>
    [Fact]
    public async Task NativeObservationRejectsCancelledRound()
    {
        var rig = NativeObservationRig();
        try
        {
            var (room, _, guest) = await StartNativeObservationAsync(rig);
            await CompleteNativeObservationDeathAsync(rig, guest, "direct");
            lock (room.Gate) room.RoundCancelled = true;
            await rig.Round.HandleSpawnAcceptedAsync(rig.Guest,
                await NativeObservationRequestAsync(RoomRegistry.SceneEntity(guest.Slot)), default);
            Assert.False(guest.Life.ObservationSent);
            Assert.Empty(rig.Observed.To(rig.Host));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>The native observation correction does not deny a reset when the deployment permits respawning.</summary>
    [Fact]
    public async Task NativeObservationLeavesAllowedRespawnUnchanged()
    {
        var rig = NativeObservationRig(denied: false);
        try
        {
            var (_, _, guest) = await StartNativeObservationAsync(rig);
            await CompleteNativeObservationDeathAsync(rig, guest, "direct");
            await rig.Round.HandleSpawnAcceptedAsync(rig.Guest,
                await NativeObservationRequestAsync(RoomRegistry.SceneEntity(guest.Slot)), default);
            Assert.Equal(SeatLife.ResetPending, guest.Life.State);
            Assert.Single(rig.Respawned.To(rig.Guest));
            Assert.Empty(rig.Observed.To(rig.Host));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    private static Rig NativeObservationRig(bool denied = true) => new(observation: true, configure: options =>
    {
        options.NativeLifeModeIds = [19];
        options.RespawnDisabledModeIds = denied ? [19] : [];
        options.ResultCloseGraceMs = 60_000;
    });

    private static async Task<(PlayRoom Room, RoomSeat Host, RoomSeat Guest)> StartNativeObservationAsync(Rig rig)
    {
        await rig.Rooms.HandleCreateAsync(rig.Host, new("native bomb", "", 19, "map"), default);
        var room = rig.Registry.TryGet(rig.Host.Id)!;
        await rig.Rooms.HandleJoinAsync(rig.Guest, new(room.Id, ""), default);
        await rig.Rooms.HandleReadyAsync(rig.Guest, new(true), default);
        await rig.Rooms.HandleStartAsync(rig.Host, new("map"), default);
        await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
        await rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default);
        await rig.WaitStartedAsync(rig.Guest);
        var host = room.FindByGame(rig.Host.Id)!;
        var guest = room.FindByGame(rig.Guest.Id)!;
        Assert.Equal(1, host.Team);
        Assert.Equal(2, guest.Team);
        rig.Registry.FallBackToGameCore(room, host);
        rig.Registry.FallBackToGameCore(room, guest);
        return (room, host, guest);
    }

    private static async Task CompleteNativeObservationDeathAsync(Rig rig, RoomSeat victim, string path)
    {
        var actor = RoomRegistry.SceneEntity(victim.Slot);
        if (path == "direct")
            await rig.Round.HandleLifeReportedAsync(victim.Game, new(RoundLifeEvent.Died, actor), default);
        else
        {
            await rig.Round.HandleLifeReportedAsync(victim.Game, new(RoundLifeEvent.Bubbled, actor), default);
            if (path == "expiry")
                await rig.Round.HandleLifeReportedAsync(victim.Game, new(RoundLifeEvent.BubbleExpired, actor), default);
            else
                await rig.Round.HandleLifeReportedAsync(rig.Host,
                    new(RoundLifeEvent.Finished, actor) { Initiator = RoomRegistry.SceneEntity(0) }, default);
        }
        await rig.Round.HandleLifeReportedAsync(victim.Game, new(RoundLifeEvent.DeathCleaned, actor), default);
        Assert.True(victim.Life.CanGrantReset);
    }

    private static async Task<RoundSpawnAccepted> NativeObservationRequestAsync(int actor)
    {
        var packets = PacketTables.Load(SharedDialectRows.PacketsDirectory());
        var profile = TestWireDialects.Get("kr-1256").Profile;
        var root = Directory.GetParent(SharedDialectRows.PacketsDirectory())!.FullName;
        var table = OpcodeTableLoader.Load(Path.Combine(root, "profiles", "kr-1256", "opcodes.yaml"), profile);
        var context = new PluginContext(new PluginRow { Id = "native-observation", Plugin = "test", Source = "test" },
            new ServiceRegistry(), new EventBus(NullLogger.Instance), NullLogger.Instance);
        try
        {
            var binding = new RoundRespawnBinding("kr-1256/game", table, packets, "kr-1256");
            binding.Mount(context);
            var body = new byte[10];
            BinaryPrimitives.WriteInt32LittleEndian(body, actor);
            body[4] = body[5] = 0xBA;
            var request = binding.Read(body);
            Assert.Equal(actor, request.Actor);
            Assert.Equal(RoundRespawnAdmission.CleanedDeath, request.Rules.Admission);
            Assert.Equal(body, request.Request.ToArray());
            return request;
        }
        finally { await context.UnwindAsync(); }
    }
}
