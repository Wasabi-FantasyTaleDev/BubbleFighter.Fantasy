using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.InGame.Sessions;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Tests;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class RoundFlowTests
{
    /// <summary>An unavailable peer match link cancels OneKill before life, score or feed publication can partially succeed.</summary>
    [Theory]
    [InlineData(RoundOneKillKind.Request, false)]
    [InlineData(RoundOneKillKind.Request, true)]
    [InlineData(RoundOneKillKind.Confirmation, false)]
    [InlineData(RoundOneKillKind.Confirmation, true)]
    public async Task OneKillMissingRecipientCannotCommitATerminalTransition(RoundOneKillKind kind, bool closed)
    {
        var rig = new Rig(configure: options => options.NativeOneKill = true);
        var eliminated = new Sink<RoundFinalZonePlayerEliminated>();
        rig.Round.FinalZoneEliminated = eliminated;
        try
        {
            var third = CaptainPlayer(33, "third");
            var room = await StartFinalZoneModeAsync(rig, GameModeId.Area32, third);
            var links = BindOneKillRelays(rig, room);
            var thirdSeat = room.FindByGame(third.Id)!;
            if (closed) await links[third.Id].Connection.CloseAsync("Synthetic match disconnect.");
            else rig.Registry.Release(links[third.Id]);
            Assert.False(thirdSeat.Game.Connection.IsClosed);
            var victim = room.FindByGame(rig.Guest.Id)!;
            var feed = OneKillRouteTests.ReadFeed(kind, RoomRegistry.SceneEntity(room.FindByGame(rig.Host.Id)!.Slot),
                RoomRegistry.SceneEntity(victim.Slot));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                rig.Round.HandleFeedAsync(kind == RoundOneKillKind.Request ? rig.Host : rig.Guest, feed, default).AsTask());
            Assert.True(room.RoundCancelled);
            Assert.True(room.FinalZoneCleanupTask.IsCompletedSuccessfully);
            Assert.Same(room.FinalZone!.CleanupTask, room.FinalZoneCleanupTask);
            Assert.Equal(SeatLife.Alive, victim.Life.State);
            Assert.All(room.Seats, seat =>
            {
                Assert.True(seat.Game.Connection.IsClosed);
                Assert.Equal(0, room.Scoring.Stats(seat.Slot).Kills);
                Assert.Empty(rig.Ended.To(seat.Game));
            });
            Assert.All(links.Values, link => Assert.Empty(rig.Feed.To(link)));
            Assert.Empty(eliminated.To(rig.Host));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>A successful sender call cannot hide a changed participant, and old publication never cancels a replacement state.</summary>
    [Theory]
    [InlineData("link")]
    [InlineData("life")]
    [InlineData("state")]
    [InlineData("round")]
    public async Task OneKillRechecksRecipientsAfterEachSuccessfulSend(string change)
    {
        var rig = new Rig(configure: options => options.NativeOneKill = true);
        rig.Round.FinalZoneEliminated = new Sink<RoundFinalZonePlayerEliminated>();
        try
        {
            var third = CaptainPlayer(33, "third");
            var room = await StartFinalZoneModeAsync(rig, GameModeId.Area32, third);
            var links = BindOneKillRelays(rig, room);
            var original = room.FinalZone!;
            var thirdSeat = room.FindByGame(third.Id)!;
            rig.Feed.OnSending = (_, _, _) =>
            {
                lock (room.Gate)
                {
                    switch (change)
                    {
                        case "link": thirdSeat.Relay = new RecordingSession(links[third.Id].Id); break;
                        case "life": thirdSeat.Life.Reset(); break;
                        case "state": room.FinalZone = new(room.StartGeneration, room.Seats); break;
                        case "round": ReplaceOneKillFailureRound(rig, room, links); break;
                    }
                }
            };
            var confirmation = OneKillRouteTests.ReadFeed(RoundOneKillKind.Confirmation,
                RoomRegistry.SceneEntity(room.FindByGame(rig.Host.Id)!.Slot), RoomRegistry.SceneEntity(room.FindByGame(rig.Guest.Id)!.Slot));
            await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Round.HandleFeedAsync(rig.Guest, confirmation, default).AsTask());
            Assert.Equal(1, links.Values.Sum(link => rig.Feed.To(link).Length));
            Assert.Empty(rig.Feed.To(links[third.Id]));
            Assert.Empty(rig.Ended.To(rig.Host));
            var cancelled = change is "link" or "life";
            Assert.Equal(cancelled, room.RoundCancelled);
            Assert.All(room.Seats, seat => Assert.Equal(cancelled, seat.Game.Connection.IsClosed));
            if (!cancelled)
            {
                Assert.NotSame(original, room.FinalZone);
                Assert.Null(original.CleanupTask);
                Assert.Null(room.FinalZone!.CleanupTask);
            }
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Generic deaths own elimination failure cleanup even when OneKill confirmation has not arrived yet.</summary>
    [Theory]
    [InlineData("life", false, false)]
    [InlineData("life", true, false)]
    [InlineData("death", false, false)]
    [InlineData("death", true, false)]
    [InlineData("life", false, true)]
    [InlineData("death", false, true)]
    public async Task GenericFinalZoneDeathFailureCannotStrandAnActiveRound(string input, bool cancel, bool contested)
    {
        var rig = new Rig(configure: options => options.NativeOneKill = true);
        var eliminated = new Sink<RoundFinalZonePlayerEliminated>();
        rig.Round.FinalZoneEliminated = eliminated;
        using var cancellation = new CancellationTokenSource();
        try
        {
            var room = await StartFinalZoneModeAsync(rig, GameModeId.Area32, contested ? [CaptainPlayer(33, "third")] : []);
            var links = BindOneKillRelays(rig, room);
            var victim = room.FindByGame(rig.Guest.Id)!;
            var actor = RoomRegistry.SceneEntity(victim.Slot);
            var sends = 0;
            eliminated.OnSending = (_, _, ct) =>
            {
                if (++sends != 2) return;
                if (cancel)
                {
                    cancellation.Cancel();
                    ct.ThrowIfCancellationRequested();
                }
                throw new IOException("Synthetic FinalZone elimination failure.");
            };
            Task Report() => input == "life"
                ? rig.Round.HandleLifeReportedAsync(rig.Guest, new(RoundLifeEvent.Died, actor), cancellation.Token).AsTask()
                : rig.Round.HandleDeathReportedAsync(rig.Guest, new(actor, 0, ServerRevives: true), cancellation.Token).AsTask();
            if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(Report);
            else await Assert.ThrowsAsync<IOException>(Report);
            Assert.True(room.RoundCancelled);
            Assert.Equal(!contested, room.FinalZone!.Finished);
            Assert.Equal(SeatLife.Dead, victim.Life.State);
            Assert.True(room.FinalZoneCleanupTask.IsCompletedSuccessfully);
            Assert.Same(room.FinalZone.CleanupTask, room.FinalZoneCleanupTask);
            Assert.All(room.Seats, seat =>
            {
                Assert.True(seat.Game.Connection.IsClosed);
                Assert.Empty(rig.Ended.To(seat.Game));
            });
            Assert.All(links.Values, link => Assert.True(link.Connection.IsClosed));
            await rig.Round.HandleFeedAsync(rig.Guest, OneKillRouteTests.ReadFeed(RoundOneKillKind.Confirmation,
                RoomRegistry.SceneEntity(room.FindByGame(rig.Host.Id)!.Slot), actor), default);
            await rig.Round.SynchronizeFinalZoneAsync(room, default);
            Assert.Equal(2, sends);
            Assert.All(links.Values, link => Assert.Empty(rig.Feed.To(link)));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>A generic life or finishing-blow report whose score publication fails cannot leave a dead participant in an active FinalZone round.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GenericFinalZoneScoreFailureCancelsBeforeElimination(bool scoreReport)
    {
        var services = new ServiceRegistry();
        var context = new PluginContext(new PluginRow { Id = "one-kill-failure", Plugin = "test", Source = "test" },
            services, new EventBus(NullLogger.Instance), NullLogger.Instance);
        var scores = new Sink<RoundScoreUpdated>();
        var results = new Sink<RoundEnded>();
        context.Effect(services.RegisterMany<IMessageSender<RoundScoreUpdated>>(scores));
        context.Effect(services.RegisterMany<IMessageSender<RoundScoreboard>>(new Sink<RoundScoreboard>()));
        context.Effect(services.RegisterMany<IMessageSender<RoundEnded>>(results));
        context.Effect(services.RegisterMany<IMessageSender<ResultStageAdvanced>>(new Sink<ResultStageAdvanced>()));
        var rig = new Rig(resultContext: context, configure: options => options.NativeOneKill = true);
        var eliminated = new Sink<RoundFinalZonePlayerEliminated>();
        rig.Round.FinalZoneEliminated = eliminated;
        try
        {
            var room = await StartFinalZoneModeAsync(rig, GameModeId.Area32);
            var links = BindOneKillRelays(rig, room);
            var host = room.FindByGame(rig.Host.Id)!;
            var guest = room.FindByGame(rig.Guest.Id)!;
            scores.OnSending = (recipient, _, _) =>
            {
                if (ReferenceEquals(recipient, rig.Guest)) throw new IOException("Synthetic FinalZone score failure.");
            };
            await Assert.ThrowsAsync<IOException>(() => scoreReport
                ? rig.Round.HandleFinishingBlowReportedAsync(rig.Host,
                    new(host.Slot) { Victim = guest.Slot, HasVictimReference = true, RequiresReporterOwnership = true }, default).AsTask()
                : rig.Round.HandleLifeReportedAsync(rig.Guest,
                    new(RoundLifeEvent.Died, RoomRegistry.SceneEntity(guest.Slot)) { Initiator = RoomRegistry.SceneEntity(host.Slot) }, default).AsTask());
            Assert.True(room.RoundCancelled);
            Assert.False(room.FinalZone!.Finished);
            Assert.Equal(SeatLife.Dead, guest.Life.State);
            Assert.Equal(1, room.Scoring.Stats(host.Slot).Kills);
            Assert.Equal(1, room.Scoring.Stats(guest.Slot).Deaths);
            Assert.Empty(eliminated.To(rig.Host));
            Assert.Empty(results.To(rig.Host));
            Assert.False(room.HasResultSequence);
            Assert.All(room.Seats, seat => Assert.True(seat.Game.Connection.IsClosed));
            Assert.All(links.Values, link => Assert.True(link.Connection.IsClosed));
        }
        finally
        {
            await rig.Registry.StopAllAsync();
            await context.UnwindAsync();
        }
    }

    /// <summary>An elimination sender retained by an old state cannot end or cancel a successfully replaced round.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinalZoneEliminationRechecksStateAfterSuccessfulPublication(bool newRound)
    {
        var rig = new Rig(configure: options => options.NativeOneKill = true);
        var eliminated = new Sink<RoundFinalZonePlayerEliminated>();
        rig.Round.FinalZoneEliminated = eliminated;
        try
        {
            var room = await StartFinalZoneModeAsync(rig, GameModeId.Area32);
            var links = BindOneKillRelays(rig, room);
            var original = room.FinalZone!;
            var actor = RoomRegistry.SceneEntity(room.FindByGame(rig.Guest.Id)!.Slot);
            eliminated.OnSending = (_, _, _) =>
            {
                lock (room.Gate)
                {
                    if (newRound) ReplaceOneKillFailureRound(rig, room, links);
                    else room.FinalZone = new(room.StartGeneration, room.Seats);
                }
            };
            await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Round.HandleLifeReportedAsync(rig.Guest,
                new(RoundLifeEvent.Died, actor), default).AsTask());
            Assert.NotSame(original, room.FinalZone);
            Assert.Null(original.CleanupTask);
            Assert.False(room.RoundCancelled);
            Assert.Equal(RoomPhase.Playing, room.Phase);
            Assert.All(room.Seats, seat => Assert.False(seat.Game.Connection.IsClosed));
            Assert.Single(eliminated.To(rig.Host));
            Assert.Empty(eliminated.To(rig.Guest));
            Assert.Empty(rig.Ended.To(rig.Host));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>A full room queue cannot bypass awaited cleanup, including shutdown of cleanup started outside the executor.</summary>
    [Theory]
    [InlineData("request")]
    [InlineData("confirmation")]
    [InlineData("life")]
    [InlineData("death")]
    public async Task FinalZoneQueueRejectionAwaitsSharedCleanupAndShutdown(string input)
    {
        var rig = new Rig(configure: options => { options.NativeOneKill = true; options.RoomQueueCapacity = 1; });
        rig.Round.FinalZoneEliminated = new Sink<RoundFinalZonePlayerEliminated>();
        var deferred = new OneKillDeferredCloseConnection(901);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task running = Task.CompletedTask;
        Task queued = Task.CompletedTask;
        Task? rejected = null;
        Task? duplicate = null;
        Task? shutdown = null;
        try
        {
            var room = await StartFinalZoneModeAsync(rig, GameModeId.Area32);
            var links = BindOneKillRelays(rig, room);
            var guest = room.FindByGame(rig.Guest.Id)!;
            var delayedRelay = new OneKillConnectionSession(new RecordingSession(deferred.Id), deferred);
            rig.Registry.BindRelay(delayedRelay, guest.Key);
            var attacker = RoomRegistry.SceneEntity(room.FindByGame(rig.Host.Id)!.Slot);
            var victim = RoomRegistry.SceneEntity(guest.Slot);
            running = room.Executor.ExecuteAsync(async ct =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
            });
            await entered.Task.WaitAsync(OneKillFailureTimeout);
            queued = room.Executor.ExecuteAsync(_ => ValueTask.CompletedTask);
            rejected = input switch
            {
                "request" => rig.Round.HandleFeedAsync(rig.Host, OneKillRouteTests.ReadFeed(RoundOneKillKind.Request, attacker, victim), default).AsTask(),
                "confirmation" => rig.Round.HandleFeedAsync(rig.Guest, OneKillRouteTests.ReadFeed(RoundOneKillKind.Confirmation, attacker, victim), default).AsTask(),
                "life" => rig.Round.HandleLifeReportedAsync(rig.Guest, new(RoundLifeEvent.Died, victim), default).AsTask(),
                _ => rig.Round.HandleDeathReportedAsync(rig.Guest, new(victim, 0, ServerRevives: true), default).AsTask(),
            };
            await deferred.Entered.Task.WaitAsync(OneKillFailureTimeout);
            Assert.True(room.RoundCancelled);
            Assert.False(rejected.IsCompleted);
            var cleanup = room.FinalZoneCleanupTask;
            duplicate = rig.Round.HandleLifeReportedAsync(rig.Host, new(RoundLifeEvent.Died, attacker), default).AsTask();
            Assert.False(duplicate.IsCompleted);
            Assert.Same(cleanup, room.FinalZoneCleanupTask);
            release.TrySetResult();
            await Task.WhenAll(running, queued).WaitAsync(OneKillFailureTimeout);
            shutdown = rig.Registry.StopAllAsync();
            Assert.False(shutdown.IsCompleted);
            deferred.Release.TrySetResult();
            await Assert.ThrowsAsync<InvalidOperationException>(() => rejected.WaitAsync(OneKillFailureTimeout));
            await Assert.ThrowsAsync<InvalidOperationException>(() => duplicate.WaitAsync(OneKillFailureTimeout));
            await shutdown.WaitAsync(OneKillFailureTimeout);
            Assert.True(cleanup.IsCompletedSuccessfully);
            Assert.Equal(1, deferred.CloseCalls);
            Assert.True(deferred.IsClosed);
            Assert.All(room.Seats, seat => Assert.True(seat.Game.Connection.IsClosed));
            Assert.Empty(rig.Ended.To(rig.Host));
            Assert.Equal(SeatLife.Alive, guest.Life.State);
        }
        finally
        {
            deferred.Release.TrySetResult();
            release.TrySetResult();
            await Task.WhenAll(running, queued).WaitAsync(OneKillFailureTimeout);
            foreach (var failed in new[] { rejected, duplicate }.OfType<Task>())
                try { await failed.WaitAsync(OneKillFailureTimeout); }
                catch (InvalidOperationException) { }
            if (shutdown is not null) await shutdown.WaitAsync(OneKillFailureTimeout);
            await rig.Registry.StopAllAsync();
        }
    }

    /// <summary>Cancellation before execution preserves a replacement FinalZone state and its live connections.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledQueuedOneKillPreservesReplacementState(bool newRound)
    {
        var rig = new Rig(configure: options => options.NativeOneKill = true);
        rig.Round.FinalZoneEliminated = new Sink<RoundFinalZonePlayerEliminated>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        Task running = Task.CompletedTask;
        try
        {
            var room = await StartFinalZoneModeAsync(rig, GameModeId.Area32);
            var links = BindOneKillRelays(rig, room);
            var original = room.FinalZone!;
            var feed = OneKillRouteTests.ReadFeed(RoundOneKillKind.Confirmation,
                RoomRegistry.SceneEntity(room.FindByGame(rig.Host.Id)!.Slot), RoomRegistry.SceneEntity(room.FindByGame(rig.Guest.Id)!.Slot));
            running = room.Executor.ExecuteAsync(async ct =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
            });
            await entered.Task.WaitAsync(OneKillFailureTimeout);
            var pending = rig.Round.HandleFeedAsync(rig.Guest, feed, cancellation.Token).AsTask();
            lock (room.Gate)
            {
                if (newRound) ReplaceOneKillFailureRound(rig, room, links);
                else room.FinalZone = new(room.StartGeneration, room.Seats);
            }
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(OneKillFailureTimeout));
            Assert.Null(original.CleanupTask);
            Assert.False(room.RoundCancelled);
            Assert.All(room.Seats, seat => Assert.False(seat.Game.Connection.IsClosed));
            Assert.All(links.Values, link => Assert.Empty(rig.Feed.To(link)));
        }
        finally
        {
            release.TrySetResult();
            await running.WaitAsync(OneKillFailureTimeout);
            await rig.Registry.StopAllAsync();
        }
    }

    /// <summary>Generic reports require an initialized FinalZone state and cannot cross a mode or state replacement while queued.</summary>
    [Theory]
    [InlineData(false, "missing-state")]
    [InlineData(true, "missing-state")]
    [InlineData(false, "other-mode")]
    [InlineData(true, "other-mode")]
    [InlineData(false, "state")]
    [InlineData(true, "state")]
    public async Task GenericFinalZoneReportsCannotAcquireAStateAfterAdmission(bool scoreReport, string boundary)
    {
        var services = new ServiceRegistry();
        var context = new PluginContext(new PluginRow { Id = "one-kill-state", Plugin = "test", Source = "test" },
            services, new EventBus(NullLogger.Instance), NullLogger.Instance);
        var scores = new Sink<RoundScoreUpdated>();
        context.Effect(services.RegisterMany<IMessageSender<RoundScoreUpdated>>(scores));
        context.Effect(services.RegisterMany<IMessageSender<RoundScoreboard>>(new Sink<RoundScoreboard>()));
        context.Effect(services.RegisterMany<IMessageSender<RoundEnded>>(new Sink<RoundEnded>()));
        context.Effect(services.RegisterMany<IMessageSender<ResultStageAdvanced>>(new Sink<ResultStageAdvanced>()));
        var rig = new Rig(resultContext: context, configure: options => options.NativeOneKill = true);
        var eliminated = new Sink<RoundFinalZonePlayerEliminated>();
        rig.Round.FinalZoneEliminated = eliminated;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task running = Task.CompletedTask;
        Task? pending = null;
        try
        {
            var room = await StartFinalZoneModeAsync(rig, GameModeId.Area32);
            var links = BindOneKillRelays(rig, room);
            var host = room.FindByGame(rig.Host.Id)!;
            var guest = room.FindByGame(rig.Guest.Id)!;
            lock (room.Gate)
            {
                if (boundary == "missing-state") room.FinalZone = null;
                if (boundary == "other-mode") room.ModeId = 4;
            }
            running = room.Executor.ExecuteAsync(async ct =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
            });
            await entered.Task.WaitAsync(OneKillFailureTimeout);
            pending = scoreReport
                ? rig.Round.HandleFinishingBlowReportedAsync(rig.Host,
                    new(host.Slot) { Victim = guest.Slot, HasVictimReference = true, RequiresReporterOwnership = true }, default).AsTask()
                : rig.Round.HandleLifeReportedAsync(rig.Guest,
                    new(RoundLifeEvent.Died, RoomRegistry.SceneEntity(guest.Slot)) { Initiator = RoomRegistry.SceneEntity(host.Slot) }, default).AsTask();
            Assert.Equal(boundary == "missing-state", pending.IsCompletedSuccessfully);
            lock (room.Gate)
            {
                room.ModeId = (int)GameModeId.Area32;
                if (boundary == "missing-state") ReplaceOneKillFailureRound(rig, room, links);
                else room.FinalZone = new(room.StartGeneration, room.Seats);
            }
            release.TrySetResult();
            await Task.WhenAll(running, pending).WaitAsync(OneKillFailureTimeout);
            Assert.Equal(SeatLife.Alive, guest.Life.State);
            Assert.Equal(0, room.Scoring.Stats(host.Slot).Kills);
            Assert.Empty(scores.To(rig.Host));
            Assert.Empty(eliminated.To(rig.Host));
            Assert.False(room.FinalZone!.Finished);
            Assert.False(room.RoundCancelled);
            Assert.All(room.Seats, seat => Assert.False(seat.Game.Connection.IsClosed));
        }
        finally
        {
            release.TrySetResult();
            await running.WaitAsync(OneKillFailureTimeout);
            if (pending is not null) await pending.WaitAsync(OneKillFailureTimeout);
            await rig.Registry.StopAllAsync();
            await context.UnwindAsync();
        }
    }

    private static void ReplaceOneKillFailureRound(Rig rig, PlayRoom room, IReadOnlyDictionary<long, RecordingSession> links)
    {
        room.ResetToWaiting();
        room.MarkStarting();
        room.MarkPlaying();
        rig.Round.InitializeFinalZone(room);
        foreach (var seat in room.Seats) rig.Registry.BindRelay(links[seat.Game.Id], seat.Key);
    }

    private static readonly TimeSpan OneKillFailureTimeout = TimeSpan.FromSeconds(10);

    private sealed class OneKillConnectionSession(RecordingSession inner, IConnection connection) : ISessionWithFeatures
    {
        public long Id => inner.Id;
        public IConnection Connection { get; } = connection;
        public ClientProfile Profile => inner.Profile;
        public ConcurrentDictionary<string, object> Items => inner.Items;
        public ISessionFeatures Features => inner.Features;
        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct) => inner.SendAsync(opcode, payload, ct);
    }

    private sealed class OneKillDeferredCloseConnection(long id) : IConnection
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int CloseCalls { get; private set; }
        public long Id { get; } = id;
        public EndPoint RemoteEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 1);
        public PipeReader Input => throw new NotSupportedException();
        public PipeWriter Output => throw new NotSupportedException();
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public bool IsClosed { get; private set; }
        public async ValueTask CloseAsync(string reason)
        {
            CloseCalls++;
            Entered.TrySetResult();
            await Release.Task.WaitAsync(OneKillFailureTimeout);
            IsClosed = true;
        }
    }
}
