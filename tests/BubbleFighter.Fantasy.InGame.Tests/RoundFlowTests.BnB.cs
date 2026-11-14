using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using System.Text;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.InGame.Data;
using BubbleFighter.Fantasy.InGame.Handlers;
using BubbleFighter.Fantasy.InGame.Sessions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class RoundFlowTests
{
    /// <summary>A cleaned BnB actor receives one game reply and stays dead without a reset grant or a new incarnation.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BnBTerminalRemovalDoesNotGrantOrReviveTheActor(bool relay)
    {
        await using var test = new BnBTestRig();
        var room = await test.StartAsync(relay);
        var seat = room.FindByGame(test.Rig.Host.Id)!;
        var life = seat.Life.Generation;
        var request = BnBRemovalRequest(BnBHostActor);
        await test.Rig.Round.HandleSpawnAcceptedAsync(test.Rig.Host, request, default);
        Assert.Empty(test.Rig.Respawned.To(test.Rig.Host));
        await test.Rig.Round.HandleLifeReportedAsync(test.HostLink, new(RoundLifeEvent.Died, BnBHostActor), default);
        await test.Rig.Round.HandleSpawnAcceptedAsync(test.Rig.Host, request, default);
        Assert.Empty(test.Rig.Respawned.To(test.Rig.Host));
        await test.Rig.Round.HandleLifeReportedAsync(test.HostLink, new(RoundLifeEvent.DeathCleaned, BnBHostActor), default);
        var score = room.Scoring.TeamTotals.ToArray();
        test.Rig.Respawned.OnSending = (recipient, _, _) =>
        {
            Assert.Same(test.Rig.Host, recipient);
            Assert.Equal(SeatLife.Dead, seat.Life.State);
            Assert.True(seat.Life.ActorRemovalSent);
            Assert.Null(seat.Life.GrantLink);
            Assert.Equal(life, seat.Life.Generation);
        };
        await test.Rig.Round.HandleSpawnAcceptedAsync(test.Rig.Host, request, default);
        await test.Rig.Round.HandleSpawnAcceptedAsync(test.Rig.Host, request, default);
        await test.Rig.Round.HandleLifeReportedAsync(test.HostLink, new(RoundLifeEvent.ResetConfirmed, BnBHostActor), default);
        await test.Rig.Round.HandleLifeReportedAsync(test.HostLink, new(RoundLifeEvent.Died, BnBHostActor), default);
        await test.Rig.Round.HandleLifeReportedAsync(test.HostLink, new(RoundLifeEvent.DeathCleaned, BnBHostActor), default);
        await test.Rig.Round.HandleSpawnAcceptedAsync(test.Rig.Host, request, default);
        await test.Rig.Round.HandleBnBPlacementAsync(test.HostLink, new(BnBHostActor, 0), default);
        test.Move(test.Rig.Host, 4);
        await test.Rig.Round.HandleBnBPickupAsync(test.HostLink, new(BnBHostActor, 4), default);

        var removal = Assert.Single(test.Rig.Respawned.To(test.Rig.Host));
        Assert.Equal(request.Request.ToArray(), removal.Request.ToArray());
        Assert.Equal(BnBHostActor, removal.Actor);
        Assert.Equal(SeatLife.Dead, seat.Life.State);
        Assert.Equal(life, seat.Life.Generation);
        Assert.True(seat.Life.DeathCleaned);
        Assert.True(seat.Life.ActorRemovalSent);
        Assert.False(seat.Life.CanGrantReset);
        Assert.Null(seat.Life.GrantLink);
        Assert.Equal(score, room.Scoring.TeamTotals.ToArray());
        Assert.Equal(1, room.LifeChecks.Count(LifeCheck.ResetConfirmedWithoutGrant));
        Assert.Empty(test.Rig.Respawned.To(test.Rig.Guest));
        if (relay) Assert.Empty(test.Rig.Respawned.To(test.HostLink));
        Assert.Empty(test.Rig.Observed.To(test.Rig.Host));
        Assert.Empty(test.Game.Placement.To(test.Rig.Host));
        Assert.Empty(test.Relay.Placement.To(test.HostLink));
        Assert.Empty(test.Game.Pickup.To(test.Rig.Host));
        Assert.Empty(test.Relay.Pickup.To(test.HostLink));
        Assert.False(room.RoundCancelled);
    }

    /// <summary>Ordinary native resets retain their confirmation path outside the enabled BnB capability and mode.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BnBTerminalRemovalDoesNotChangeOtherRespawnPaths(bool disableCapability)
    {
        await using var test = new BnBTestRig();
        var room = await test.StartAsync();
        lock (room.Gate)
        {
            if (disableCapability) test.Options.BnB = false;
            else
            {
                room.ModeId = 4;
                test.Options.NativeLifeModeIds = [4];
            }
        }
        var seat = room.FindByGame(test.Rig.Host.Id)!;
        var life = seat.Life.Generation;
        await CleanBnBHostDeathAsync(test);
        await test.Rig.Round.HandleSpawnAcceptedAsync(test.Rig.Host, BnBRemovalRequest(BnBHostActor), default);
        Assert.Equal(SeatLife.ResetPending, seat.Life.State);
        Assert.Same(test.HostLink, seat.Life.GrantLink);
        Assert.False(seat.Life.ActorRemovalSent);
        await test.Rig.Round.HandleLifeReportedAsync(test.HostLink, new(RoundLifeEvent.ResetConfirmed, BnBHostActor), default);
        Assert.Equal(SeatLife.Alive, seat.Life.State);
        Assert.Equal(life + 1, seat.Life.Generation);
        Assert.Single(test.Rig.Respawned.To(test.Rig.Host));
    }

    /// <summary>Terminal removal requires the exact game owner, native actor and current ready match link.</summary>
    [Theory]
    [InlineData("foreign-actor")]
    [InlineData("unknown-actor")]
    [InlineData("relay-request")]
    [InlineData("same-id-game")]
    [InlineData("match-generation")]
    [InlineData("unconfirmed")]
    [InlineData("other-member")]
    [InlineData("cancelled")]
    [InlineData("not-playing")]
    public async Task BnBTerminalRemovalRejectsUnownedOrStaleRequests(string invalid)
    {
        await using var test = new BnBTestRig();
        var room = await test.StartAsync(relay: true);
        await CleanBnBHostDeathAsync(test);
        var seat = room.FindByGame(test.Rig.Host.Id)!;
        ISession requester = test.Rig.Host;
        var actor = BnBHostActor;
        lock (room.Gate)
        {
            switch (invalid)
            {
                case "foreign-actor": actor = BnBGuestActor; break;
                case "unknown-actor": actor++; break;
                case "relay-request": requester = test.HostLink; break;
                case "same-id-game": requester = new RecordingSession(test.Rig.Host.Id); break;
                case "match-generation": seat.RelayGeneration = room.StartGeneration - 1; break;
                case "unconfirmed": seat.RelayConfirmed = false; break;
                case "other-member": room.FindByGame(test.Rig.Guest.Id)!.RelayConfirmed = false; break;
                case "cancelled": room.RoundCancelled = true; break;
                case "not-playing": room.ResetToWaiting(); break;
            }
        }
        await test.Rig.Round.HandleSpawnAcceptedAsync(requester, BnBRemovalRequest(actor), default);
        Assert.Empty(test.Rig.Respawned.To(test.Rig.Host));
        Assert.Empty(test.Rig.Respawned.To(test.Rig.Guest));
        Assert.Empty(test.Rig.Respawned.To(test.HostLink));
        Assert.False(seat.Life.ActorRemovalSent);
        Assert.Null(seat.Life.GrantLink);
    }

    /// <summary>A queued removal cannot cross a round, state, actor incarnation or exact match-link replacement.</summary>
    [Theory]
    [InlineData("round")]
    [InlineData("state")]
    [InlineData("life")]
    [InlineData("link")]
    public async Task BnBQueuedTerminalRemovalRechecksItsParticipant(string changed)
    {
        await using var test = new BnBTestRig();
        var room = await test.StartAsync(relay: true);
        await CleanBnBHostDeathAsync(test);
        var seat = room.FindByGame(test.Rig.Host.Id)!;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = room.Executor.ExecuteAsync(async ct =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
        });
        Task? pending = null;
        try
        {
            await entered.Task.WaitAsync(BnBWaitTimeout);
            pending = test.Rig.Round.HandleSpawnAcceptedAsync(test.Rig.Host, BnBRemovalRequest(BnBHostActor), default).AsTask();
            Assert.False(pending.IsCompleted);
            lock (room.Gate)
            {
                switch (changed)
                {
                    case "round": ReplaceBnBRound(test, room); break;
                    case "state": _ = test.Rig.Round.PrepareBnBRound(room); break;
                    case "life": seat.Life.Reset(); seat.Life.Die(); seat.Life.CleanDeath(); break;
                    case "link": seat.Relay = new RecordingSession(test.HostLink.Id); break;
                }
            }
            release.TrySetResult();
            await Task.WhenAll(running, pending).WaitAsync(BnBWaitTimeout);
            Assert.Empty(test.Rig.Respawned.To(test.Rig.Host));
            Assert.False(seat.Life.ActorRemovalSent);
            Assert.Null(seat.Life.GrantLink);
            Assert.False(room.RoundCancelled);
            Assert.False(test.Rig.Host.Connection.IsClosed);
            Assert.False(test.Rig.Guest.Connection.IsClosed);
        }
        finally
        {
            release.TrySetResult();
            await running.WaitAsync(BnBWaitTimeout);
            if (pending is not null) await pending.WaitAsync(BnBWaitTimeout);
        }
    }

    /// <summary>Late life reports cannot kill a new BnB incarnation or accept a same-ID substitute for its connection.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BnBLifeReportsRequireCurrentIncarnationAndSession(bool replacedSession)
    {
        await using var test = new BnBTestRig();
        var room = await test.StartAsync(relay: true);
        if (replacedSession)
        {
            await test.Rig.Round.HandleLifeReportedAsync(new RecordingSession(test.HostLink.Id), new(RoundLifeEvent.Died, BnBHostActor), default);
        }
        else
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var running = room.Executor.ExecuteAsync(async ct =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
            });
            Task? pending = null;
            try
            {
                await entered.Task.WaitAsync(BnBWaitTimeout);
                pending = test.Rig.Round.HandleLifeReportedAsync(test.HostLink, new(RoundLifeEvent.Died, BnBHostActor), default).AsTask();
                Assert.False(pending.IsCompleted);
                ReplaceBnBRound(test, room);
                release.TrySetResult();
                await Task.WhenAll(running, pending).WaitAsync(BnBWaitTimeout);
            }
            finally
            {
                release.TrySetResult();
                await running.WaitAsync(BnBWaitTimeout);
                if (pending is not null) await pending.WaitAsync(BnBWaitTimeout);
            }
        }
        Assert.Equal(SeatLife.Alive, room.FindByGame(test.Rig.Host.Id)!.Life.State);
        Assert.False(room.RoundCancelled);
    }

    /// <summary>A removal publication failure cancels only its own state, including changes detected after a successful send.</summary>
    [Theory]
    [InlineData("failure")]
    [InlineData("round")]
    [InlineData("state")]
    [InlineData("life")]
    [InlineData("link")]
    public async Task BnBTerminalRemovalPublicationFailureIsFenced(string changed)
    {
        await using var test = new BnBTestRig();
        var room = await test.StartAsync(relay: true);
        await CleanBnBHostDeathAsync(test);
        var original = room.BnBRound!;
        var seat = room.FindByGame(test.Rig.Host.Id)!;
        test.Rig.Respawned.OnSending = (_, _, _) =>
        {
            Assert.True(seat.Life.ActorRemovalSent);
            lock (room.Gate)
            {
                switch (changed)
                {
                    case "failure": throw new IOException("Synthetic BnB actor removal failure.");
                    case "round": ReplaceBnBRound(test, room); break;
                    case "state": _ = test.Rig.Round.PrepareBnBRound(room); break;
                    case "life": seat.Life.Reset(); break;
                    case "link": seat.Relay = new RecordingSession(test.HostLink.Id); break;
                }
            }
        };
        Task Remove() => test.Rig.Round.HandleSpawnAcceptedAsync(test.Rig.Host, BnBRemovalRequest(BnBHostActor), default).AsTask();
        if (changed == "failure") await Assert.ThrowsAsync<IOException>(Remove);
        else await Assert.ThrowsAsync<InvalidOperationException>(Remove);
        var cancelled = changed is not ("round" or "state");
        Assert.Equal(cancelled, room.RoundCancelled);
        Assert.Equal(cancelled, test.Rig.Host.Connection.IsClosed);
        Assert.Equal(cancelled, test.Rig.Guest.Connection.IsClosed);
        Assert.Null(seat.Life.GrantLink);
        if (cancelled)
        {
            Assert.Same(original.CleanupTask, room.BnBCleanupTask);
            Assert.True(room.BnBCleanupTask.IsCompletedSuccessfully);
        }
        else
        {
            Assert.NotSame(original, room.BnBRound);
            Assert.Null(original.CleanupTask);
            Assert.Null(room.BnBRound!.CleanupTask);
        }
    }

    /// <summary>A real new round clears terminal removal and permits that new actor's independent terminal reply.</summary>
    [Fact]
    public async Task BnBTerminalRemovalResetsOnlyWithANewActorIncarnation()
    {
        await using var test = new BnBTestRig();
        var room = await test.StartAsync();
        await CleanBnBHostDeathAsync(test);
        var seat = room.FindByGame(test.Rig.Host.Id)!;
        var life = seat.Life.Generation;
        await test.Rig.Round.HandleSpawnAcceptedAsync(test.Rig.Host, BnBRemovalRequest(BnBHostActor), default);
        ReplaceBnBRound(test, room);
        Assert.False(seat.Life.ActorRemovalSent);
        Assert.True(seat.Life.Generation > life);
        Assert.Equal(SeatLife.Alive, seat.Life.State);
        await CleanBnBHostDeathAsync(test);
        await test.Rig.Round.HandleSpawnAcceptedAsync(test.Rig.Host, BnBRemovalRequest(BnBHostActor), default);
        Assert.Equal(2, test.Rig.Respawned.To(test.Rig.Host).Length);
        Assert.True(seat.Life.ActorRemovalSent);
        Assert.Equal(SeatLife.Dead, seat.Life.State);
    }

    private static RoundSpawnAccepted BnBRemovalRequest(int actor)
    {
        byte[] request = [.. BitConverter.GetBytes(actor), 0xBA, 0xBA, 0, 0, 0, 0];
        return new(actor, 0)
        {
            Request = request,
            Rules = new(RoundRespawnAudience.Requester, RoundRespawnCompletion.MatchConfirmation, RoundRespawnAdmission.CleanedDeath),
        };
    }

    private static async Task CleanBnBHostDeathAsync(BnBTestRig test)
    {
        await test.Rig.Round.HandleLifeReportedAsync(test.HostLink, new(RoundLifeEvent.Died, BnBHostActor), default);
        await test.Rig.Round.HandleLifeReportedAsync(test.HostLink, new(RoundLifeEvent.DeathCleaned, BnBHostActor), default);
    }

    /// <summary>A failed terminal reply waits for asynchronous peer cleanup before returning its publication error.</summary>
    [Fact]
    public async Task BnBTerminalRemovalPublicationFailureAwaitsConnectionCleanup()
    {
        await using var test = new BnBTestRig();
        BnBDeferredCloseConnection? deferred = null;
        test.RelaySessionFactory = (id, profile) => id == test.Rig.Guest.Id + 1000
            ? new BnBConnectionSession(new RecordingSession(id, profile), deferred = new(id))
            : new RecordingSession(id, profile);
        var room = await test.StartAsync(relay: true);
        await CleanBnBHostDeathAsync(test);
        var seat = room.FindByGame(test.Rig.Host.Id)!;
        var life = seat.Life.Generation;
        test.Rig.Respawned.OnSending = (_, _, _) => throw new IOException("Synthetic BnB actor removal failure.");
        var pending = test.Rig.Round.HandleSpawnAcceptedAsync(test.Rig.Host, BnBRemovalRequest(BnBHostActor), default).AsTask();
        var closing = Assert.IsType<BnBDeferredCloseConnection>(deferred);
        try
        {
            await closing.Entered.Task.WaitAsync(BnBWaitTimeout);
            Assert.True(room.RoundCancelled);
            Assert.False(pending.IsCompleted);
            Assert.False(room.BnBCleanupTask.IsCompleted);
            Assert.Equal(SeatLife.Dead, seat.Life.State);
            Assert.True(seat.Life.ActorRemovalSent);
            Assert.Null(seat.Life.GrantLink);
            Assert.Equal(life, seat.Life.Generation);
            closing.Release.TrySetResult();
            await Assert.ThrowsAsync<IOException>(() => pending.WaitAsync(BnBWaitTimeout));
            Assert.True(room.BnBCleanupTask.IsCompletedSuccessfully);
            Assert.Equal(1, closing.CloseCalls);
            foreach (var member in new[] { test.Rig.Host, test.Rig.Guest, test.HostLink, test.GuestLink })
                Assert.True(member.Connection.IsClosed);
            Assert.Empty(test.Rig.Respawned.To(test.Rig.Host));
        }
        finally
        {
            closing.Release.TrySetResult();
            try { await pending.WaitAsync(BnBWaitTimeout); }
            catch (IOException) { }
        }
    }

    /// <summary>A successful countdown publishes both start messages before activating the current BnB clock.</summary>
    [Fact]
    public async Task BnBCountdownStartsTheCurrentGenerationAfterItsDelay()
    {
        await using var test = new BnBTestRig();
        var delay = new BnBCountdownGate();
        test.Rig.Round.CountdownDelay = delay.WaitAsync;
        var room = await test.StartAsync(load: false);
        var original = Assert.IsType<BnBRuntimeState>(room.BnBRound);
        await test.Rig.Round.HandleLoadReportedAsync(test.Rig.Host, new(), default);
        await test.Rig.Round.HandleLoadReportedAsync(test.Rig.Guest, new(), default);
        var countdown = room.CountdownTask;
        try
        {
            await delay.Entered.Task.WaitAsync(BnBWaitTimeout);
            Assert.False(countdown.IsCompleted);
            Assert.Equal(RoomPhase.Starting, room.Phase);
            Assert.Null(room.BnBClock);
            foreach (var member in new[] { test.Rig.Host, test.Rig.Guest })
            {
                Assert.Single(test.Rig.Countdown.To(member));
                Assert.Empty(test.Rig.Started.To(member));
            }
            delay.Release.TrySetResult();
            await countdown.WaitAsync(BnBWaitTimeout);
            Assert.Same(original, room.BnBRound);
            Assert.Equal(RoomPhase.Playing, room.Phase);
            Assert.NotNull(room.BnBClock);
            Assert.False(room.RoundCancelled);
            foreach (var member in new[] { test.Rig.Host, test.Rig.Guest })
            {
                Assert.Single(test.Rig.Started.To(member));
                Assert.False(member.Connection.IsClosed);
            }
        }
        finally
        {
            delay.Release.TrySetResult();
            await countdown.WaitAsync(BnBWaitTimeout);
        }
    }

    /// <summary>A partial start failure closes only its own generation, including disposal of an active sender.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task BnBCountdownPublicationFailureCancelsOnlyItsProducingGeneration(bool disposed, bool replaceGeneration)
    {
        await using var test = new BnBTestRig();
        var room = await test.StartAsync(load: false);
        var original = Assert.IsType<BnBRuntimeState>(room.BnBRound);
        test.Rig.Started.OnSending = (session, _, _) =>
        {
            if (!ReferenceEquals(session, test.Rig.Guest)) return;
            Assert.Single(test.Rig.Started.To(test.Rig.Host));
            Assert.Null(room.BnBClock);
            if (replaceGeneration)
            {
                lock (room.Gate)
                {
                    room.ResetToWaiting();
                    room.MarkStarting();
                    room.RoundMapKey = room.RoundStageKey = "test";
                    _ = test.Rig.Round.PrepareBnBRound(room);
                    foreach (var member in room.Seats)
                    {
                        member.GameLinkGeneration = room.StartGeneration;
                        member.SceneReady = true;
                    }
                    test.Move(test.Rig.Host, 0);
                    test.Move(test.Rig.Guest, 8);
                    room.MarkPlaying();
                }
            }
            if (disposed) throw new ObjectDisposedException("Synthetic BnB start sender");
            throw new IOException("Synthetic BnB start publication failure.");
        };
        await test.Rig.Round.HandleLoadReportedAsync(test.Rig.Host, new(), default);
        await test.Rig.Round.HandleLoadReportedAsync(test.Rig.Guest, new(), default);
        await room.CountdownTask.WaitAsync(BnBWaitTimeout);
        Assert.Single(test.Rig.Started.To(test.Rig.Host));
        Assert.Empty(test.Rig.Started.To(test.Rig.Guest));
        Assert.Equal(!replaceGeneration, room.RoundCancelled);
        Assert.Equal(!replaceGeneration, test.Rig.Host.Connection.IsClosed);
        Assert.Equal(!replaceGeneration, test.Rig.Guest.Connection.IsClosed);
        Assert.Null(room.BnBClock);
        Assert.Equal(0, original.Ledger.NextGeneration);
        if (replaceGeneration)
        {
            var replacement = Assert.IsType<BnBRuntimeState>(room.BnBRound);
            Assert.NotSame(original, replacement);
            Assert.Equal(original.Generation + 1, replacement.Generation);
            Assert.Equal(RoomPhase.Playing, room.Phase);
            await test.Rig.Round.HandleBnBPlacementAsync(test.HostLink, new(BnBHostActor, 0), default);
            Assert.Equal(1, replacement.Ledger.NextGeneration);
            Assert.Single(test.Game.Placement.To(test.HostLink));
            Assert.Single(test.Game.Placement.To(test.GuestLink));
        }
        else
        {
            Assert.Same(original, room.BnBRound);
            await test.Rig.Round.HandleBnBPlacementAsync(test.HostLink, new(BnBHostActor, 0), default);
            Assert.Empty(test.Game.Placement.To(test.HostLink));
            Assert.Empty(test.Game.Placement.To(test.GuestLink));
        }
    }

    /// <summary>A full executor cannot strand the countdown, and its completion awaits asynchronous connection cleanup.</summary>
    [Fact]
    public async Task BnBCountdownQueueRejectionCancelsWithoutEnqueueingAndAwaitsCleanup()
    {
        await using var test = new BnBTestRig(options => options.RoomQueueCapacity = 1);
        var delay = new BnBCountdownGate();
        test.Rig.Round.CountdownDelay = delay.WaitAsync;
        BnBDeferredCloseConnection? deferred = null;
        test.RelaySessionFactory = (id, profile) => id == test.Rig.Host.Id + 1000
            ? new BnBConnectionSession(new RecordingSession(id, profile), deferred = new(id))
            : new RecordingSession(id, profile);
        var room = await test.StartAsync(relay: true, load: false);
        var original = Assert.IsType<BnBRuntimeState>(room.BnBRound);
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWork = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = Task.CompletedTask;
        var queued = Task.CompletedTask;
        var countdown = Task.CompletedTask;
        try
        {
            await test.Rig.Round.HandleLoadReportedAsync(test.Rig.Host, new(), default);
            await test.Rig.Round.HandleLoadReportedAsync(test.Rig.Guest, new(), default);
            await test.Rig.Round.HandleMatchConnectionReportedAsync(test.Rig.Host, new(true), default);
            await test.Rig.Round.HandleMatchConnectionReportedAsync(test.Rig.Guest, new(true), default);
            countdown = room.CountdownTask;
            await delay.Entered.Task.WaitAsync(BnBWaitTimeout);
            running = room.Executor.ExecuteAsync(async ct =>
            {
                blocked.TrySetResult();
                await releaseWork.Task.WaitAsync(ct);
            });
            await blocked.Task.WaitAsync(BnBWaitTimeout);
            var queuedRan = false;
            queued = room.Executor.ExecuteAsync(_ =>
            {
                queuedRan = true;
                return ValueTask.CompletedTask;
            });
            delay.Release.TrySetResult();
            var closing = Assert.IsType<BnBDeferredCloseConnection>(deferred);
            await closing.Entered.Task.WaitAsync(BnBWaitTimeout);
            Assert.True(room.RoundCancelled);
            Assert.Equal(RoomPhase.Starting, room.Phase);
            Assert.Equal(original.Generation, room.CountdownGeneration);
            Assert.False(countdown.IsCompleted);
            Assert.False(queued.IsCompleted);
            Assert.False(running.IsCompleted);
            Assert.False(queuedRan);
            closing.Release.TrySetResult();
            await countdown.WaitAsync(BnBWaitTimeout);
            Assert.True(closing.IsClosed);
            Assert.False(queued.IsCompleted);
            Assert.False(running.IsCompleted);
            Assert.False(queuedRan);
            Assert.Same(original, room.BnBRound);
            Assert.Null(room.BnBClock);
            foreach (var member in new[] { test.Rig.Host, test.Rig.Guest, test.HostLink, test.GuestLink })
                Assert.True(member.Connection.IsClosed);
            Assert.Empty(test.Rig.Started.To(test.Rig.Host));
            Assert.Empty(test.Rig.Started.To(test.Rig.Guest));
        }
        finally
        {
            deferred?.Release.TrySetResult();
            delay.Release.TrySetResult();
            releaseWork.TrySetResult();
            await Task.WhenAll(running, queued, countdown).WaitAsync(BnBWaitTimeout);
        }
    }

    /// <summary>Every loading recipient receives the exact seed used by the new generation's initial item ledger.</summary>
    [Fact]
    public async Task BnBStartPublishesOneSharedSeedBeforeLoadingAndReplacesTheRoundLedger()
    {
        await using var test = new BnBTestRig();
        var room = await test.StartAsync(load: false);
        var previous = Assert.IsType<BnBRuntimeState>(room.BnBRound);
        foreach (var member in new[] { test.Rig.Host, test.Rig.Guest })
        {
            Assert.Equal(previous.Ledger.Seed, Assert.Single(test.Rig.Starting.To(member)).MapSeed);
            Assert.Empty(test.Rig.Loaded.To(member));
            Assert.Empty(test.Rig.Countdown.To(member));
        }
        Assert.Equal(BnBItemKind.Bomb, previous.Ledger.GetCell(4).Item);
        lock (room.Gate) room.ResetToWaiting();
        await test.Rig.Rooms.HandleReadyAsync(test.Rig.Guest, new(true), default);
        await test.Rig.Rooms.HandleStartAsync(test.Rig.Host, new("test"), default);
        var replacement = Assert.IsType<BnBRuntimeState>(room.BnBRound);
        Assert.NotSame(previous, replacement);
        Assert.Equal(previous.Generation + 1, replacement.Generation);
        Assert.Equal(0, replacement.Ledger.NextGeneration);
        foreach (var member in new[] { test.Rig.Host, test.Rig.Guest })
        {
            var starts = test.Rig.Starting.To(member);
            Assert.Equal(2, starts.Length);
            Assert.Equal(replacement.Ledger.Seed, starts[1].MapSeed);
        }
    }

    /// <summary>Concurrent requests commit in one recipient-inclusive order on both admitted transport families.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BnBPlacementSerializesPublicationAndCommitsAfterEveryRecipient(bool relay)
    {
        await using var test = new BnBTestRig();
        var room = await test.StartAsync(relay);
        var output = relay ? test.Relay : test.Game;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new ConcurrentQueue<(long Session, int Grid)>();
        output.Placement.OnSending = async (session, message, _) =>
        {
            if (ReferenceEquals(session, test.HostLink) && message.GridIndex == 0)
            {
                entered.TrySetResult();
                await release.Task;
            }
            order.Enqueue((session.Id, message.GridIndex));
        };
        Task? first = null;
        Task? second = null;
        try
        {
            first = test.Rig.Round.HandleBnBPlacementAsync(test.HostLink, new(BnBHostActor, 0), default).AsTask();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, room.BnBRound!.Ledger.NextGeneration);
            second = test.Rig.Round.HandleBnBPlacementAsync(test.GuestLink, new(BnBGuestActor, 8), default).AsTask();
            Assert.False(second.IsCompleted);
            release.TrySetResult();
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(new[]
            {
                (test.HostLink.Id, 0), (test.GuestLink.Id, 0),
                (test.HostLink.Id, 8), (test.GuestLink.Id, 8),
            }, order.ToArray());
            foreach (var member in new[] { test.HostLink, test.GuestLink })
                Assert.Equal(new[] { new RoundBnBPlacementPublished(BnBHostActor, 0), new(BnBGuestActor, 8) }, output.Placement.To(member));
            Assert.Equal(0, room.BnBRound.Ledger.GetCell(0).Generation);
            Assert.Equal(1, room.BnBRound.Ledger.GetCell(8).Generation);
            Assert.Equal(2, room.BnBRound.Ledger.NextGeneration);
            var unused = relay ? test.Game : test.Relay;
            Assert.Empty(unused.Placement.To(test.HostLink));
            Assert.Empty(unused.Placement.To(test.GuestLink));
        }
        finally
        {
            release.TrySetResult();
            if (first is not null) await first;
            if (second is not null) await second;
        }
    }

    /// <summary>An occupied-cell report consumes no shared generation even when its owner has spare capacity.</summary>
    [Fact]
    public async Task BnBOccupiedPlacementDoesNotPublishOrConsumeGeneration()
    {
        await using var test = new BnBTestRig();
        var room = await test.StartAsync();
        await test.Rig.Round.HandleBnBPlacementAsync(test.HostLink, new(BnBHostActor, 0), default);
        test.Move(test.Rig.Guest, 0);
        await test.Rig.Round.HandleBnBPlacementAsync(test.GuestLink, new(BnBGuestActor, 0), default);
        Assert.Equal(1, room.BnBRound!.Ledger.NextGeneration);
        Assert.Single(test.Game.Placement.To(test.HostLink));
        Assert.Single(test.Game.Placement.To(test.GuestLink));
        test.Move(test.Rig.Guest, 8);
        await test.Rig.Round.HandleBnBPlacementAsync(test.GuestLink, new(BnBGuestActor, 8), default);
        Assert.Equal(1, room.BnBRound.Ledger.GetCell(8).Generation);
        Assert.Equal(2, test.Game.Placement.To(test.HostLink).Length);
    }

    /// <summary>Explosion admission pins ownership, generation and fuse time, then publishes a dead owner's root only once.</summary>
    [Fact]
    public async Task BnBExplosionRejectsForgedAndEarlyRootsAndSuppressesDuplicates()
    {
        await using var test = new BnBTestRig();
        var room = await test.StartAsync();
        await test.Rig.Round.HandleBnBPlacementAsync(test.HostLink, new(BnBHostActor, 0), default);
        await test.Rig.Round.HandleBnBPlacementAsync(test.GuestLink, new(BnBGuestActor, 8), default);
        var original = room.BnBRound!.Ledger;
        await test.Rig.Round.HandleBnBExplosionAsync(test.HostLink, new(BnBHostActor, [new(0, 0)]), default);
        test.Milliseconds += 3000;
        await test.Rig.Round.HandleBnBExplosionAsync(test.HostLink, new(BnBHostActor, [new(1, 8)]), default);
        await test.Rig.Round.HandleBnBExplosionAsync(test.HostLink, new(BnBHostActor, [new(1, 0)]), default);
        await test.Rig.Round.HandleBnBExplosionAsync(test.HostLink, new(BnBHostActor, [new(0, 0), new(0, 0)]), default);
        Assert.Same(original, room.BnBRound.Ledger);
        Assert.Empty(test.Game.Explosion.To(test.HostLink));
        lock (room.Gate) Assert.True(room.FindByGame(test.Rig.Host.Id)!.Life.Die());
        var report = new RoundBnBExplosionReported(BnBHostActor, [new(0, 0)]);
        await test.Rig.Round.HandleBnBExplosionAsync(test.HostLink, report, default);
        var committed = room.BnBRound.Ledger;
        await test.Rig.Round.HandleBnBExplosionAsync(test.HostLink, report, default);
        Assert.Same(committed, room.BnBRound.Ledger);
        Assert.Equal(BnBBombState.Flame, committed.GetCell(0).Bomb);
        Assert.Equal(BnBBombState.Installed, committed.GetCell(8).Bomb);
        foreach (var member in new[] { test.HostLink, test.GuestLink })
        {
            var sent = Assert.Single(test.Game.Explosion.To(member));
            Assert.Equal(BnBHostActor, sent.Actor);
            Assert.Equal(new[] { new RoundBnBBomb(0, 0) }, sent.Bombs);
        }
        Assert.False(room.RoundCancelled);
    }

    /// <summary>A contested or replayed pickup still clears its reporter's native latch while awarding the item once.</summary>
    [Fact]
    public async Task BnBCompetingPickupsAcknowledgeEveryReporterButAwardOnlyTheFirst()
    {
        await using var test = new BnBTestRig();
        var room = await test.StartAsync();
        test.Move(test.Rig.Host, 4);
        test.Move(test.Rig.Guest, 4);
        await test.Rig.Round.HandleBnBPickupAsync(test.HostLink, new(BnBHostActor, 4), default);
        test.Move(test.Rig.Guest, 0);
        await test.Rig.Round.HandleBnBPickupAsync(test.GuestLink, new(BnBGuestActor, 4), default);
        await test.Rig.Round.HandleBnBPickupAsync(test.HostLink, new(BnBHostActor, 4), default);
        Assert.Equal(BnBItemKind.None, room.BnBRound!.Ledger.GetCell(4).Item);
        Assert.Equal(1, room.BnBRound.Ledger.ItemCount(BnBHostActor, BnBItemKind.Bomb));
        Assert.Equal(0, room.BnBRound.Ledger.ItemCount(BnBGuestActor, BnBItemKind.Bomb));
        foreach (var member in new[] { test.HostLink, test.GuestLink })
            Assert.Equal(new[]
            {
                new RoundBnBPickupPublished(BnBHostActor, 4), new(BnBGuestActor, 4), new(BnBHostActor, 4),
            }, test.Game.Pickup.To(member));
    }

    /// <summary>A pickup sent after the same update's bubble report is acknowledged once per request and awarded once across release.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BnBPickupAfterBubbleReportSurvivesReleaseWithoutDuplicatingTheItem(bool relay)
    {
        await using var test = new BnBTestRig();
        var room = await test.StartAsync(relay);
        var seat = room.FindByGame(test.Rig.Host.Id)!;
        var life = seat.Life.Generation;
        test.Move(test.Rig.Host, 4);
        await test.Rig.Round.HandleLifeReportedAsync(test.HostLink, new(RoundLifeEvent.Bubbled, BnBHostActor), default);
        Assert.Equal(SeatLife.Bubble, seat.Life.State);
        await test.Rig.Round.HandleBnBPickupAsync(test.HostLink, new(BnBHostActor, 4), default);
        await test.Rig.Round.HandleBnBPickupAsync(test.HostLink, new(BnBHostActor, 4), default);
        await test.Rig.Round.HandleBnBPlacementAsync(test.HostLink, new(BnBHostActor, 4), default);
        var senders = relay ? test.Relay : test.Game;
        Assert.Equal(SeatLife.Bubble, seat.Life.State);
        Assert.Equal(life, seat.Life.Generation);
        Assert.Empty(senders.Placement.To(test.HostLink));
        Assert.Equal(0, room.BnBRound!.Ledger.NextGeneration);
        await test.Rig.Round.HandleLifeReportedAsync(test.HostLink, new(RoundLifeEvent.Released, BnBHostActor), default);
        await test.Rig.Round.HandleBnBPickupAsync(test.HostLink, new(BnBHostActor, 4), default);
        Assert.Equal(SeatLife.Alive, seat.Life.State);
        Assert.Equal(life, seat.Life.Generation);
        Assert.Equal(BnBItemKind.None, room.BnBRound.Ledger.GetCell(4).Item);
        Assert.Equal(1, room.BnBRound.Ledger.ItemCount(BnBHostActor, BnBItemKind.Bomb));
        Assert.Equal(0, room.BnBRound.Ledger.ItemCount(BnBGuestActor, BnBItemKind.Bomb));
        foreach (var member in new[] { test.HostLink, test.GuestLink })
            Assert.Equal(Enumerable.Repeat(new RoundBnBPickupPublished(BnBHostActor, 4), 3).ToArray(), senders.Pickup.To(member));
        await test.Rig.Round.HandleBnBPlacementAsync(test.HostLink, new(BnBHostActor, 4), default);
        Assert.Equal(1, room.BnBRound.Ledger.NextGeneration);
        Assert.Single(senders.Placement.To(test.HostLink));
        Assert.False(room.RoundCancelled);
    }

    /// <summary>A pickup keeps its item untouched when its actor is dead, reset-pending or removed at admission or execution.</summary>
    [Theory]
    [InlineData("dead", false)]
    [InlineData("reset-pending", false)]
    [InlineData("removed", false)]
    [InlineData("dead", true)]
    [InlineData("reset-pending", true)]
    [InlineData("removed", true)]
    public async Task BnBPickupRejectsTerminalLifeBeforeAndAfterQueueing(string state, bool queued)
    {
        await using var test = new BnBTestRig();
        var room = await test.StartAsync();
        var seat = room.FindByGame(test.Rig.Host.Id)!;
        var life = seat.Life.Generation;
        test.Move(test.Rig.Host, 4);
        await test.Rig.Round.HandleLifeReportedAsync(test.HostLink, new(RoundLifeEvent.Bubbled, BnBHostActor), default);
        var original = room.BnBRound!.Ledger;
        void ChangeLife()
        {
            lock (room.Gate)
            {
                Assert.True(seat.Life.Burst());
                if (state == "reset-pending") seat.Life.Grant(test.HostLink);
                if (state == "removed")
                {
                    Assert.True(seat.Life.CleanDeath());
                    Assert.True(seat.Life.TryRemoveActor());
                }
            }
        }
        if (queued)
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var running = room.Executor.ExecuteAsync(async ct =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
                ChangeLife();
            });
            Task? pending = null;
            try
            {
                await entered.Task.WaitAsync(BnBWaitTimeout);
                pending = test.Rig.Round.HandleBnBPickupAsync(test.HostLink, new(BnBHostActor, 4), default).AsTask();
                Assert.False(pending.IsCompleted);
                release.TrySetResult();
                await Task.WhenAll(running, pending).WaitAsync(BnBWaitTimeout);
            }
            finally
            {
                release.TrySetResult();
                await running.WaitAsync(BnBWaitTimeout);
                if (pending is not null) await pending.WaitAsync(BnBWaitTimeout);
            }
        }
        else
        {
            ChangeLife();
            await test.Rig.Round.HandleBnBPickupAsync(test.HostLink, new(BnBHostActor, 4), default);
        }
        Assert.Equal(life, seat.Life.Generation);
        Assert.Same(original, room.BnBRound.Ledger);
        Assert.Equal(BnBItemKind.Bomb, room.BnBRound.Ledger.GetCell(4).Item);
        Assert.Equal(0, room.BnBRound.Ledger.ItemCount(BnBHostActor, BnBItemKind.Bomb));
        Assert.Empty(test.Game.Pickup.To(test.HostLink));
        Assert.Empty(test.Game.Pickup.To(test.GuestLink));
        Assert.False(room.RoundCancelled);
    }

    /// <summary>Queued pickup accepts a bubble in the same life but rejects a replacement incarnation or match link.</summary>
    [Theory]
    [InlineData("bubble")]
    [InlineData("incarnation")]
    [InlineData("link")]
    public async Task BnBQueuedPickupPreservesLifeAndConnectionOwnership(string change)
    {
        await using var test = new BnBTestRig();
        var room = await test.StartAsync(relay: true);
        var seat = room.FindByGame(test.Rig.Host.Id)!;
        test.Move(test.Rig.Host, 4);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = room.Executor.ExecuteAsync(async ct =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            lock (room.Gate)
            {
                if (change == "incarnation") seat.Life.Reset();
                if (change == "link") seat.Relay = new RecordingSession(test.HostLink.Id);
                Assert.True(seat.Life.Bubble());
            }
        });
        Task? pending = null;
        try
        {
            await entered.Task.WaitAsync(BnBWaitTimeout);
            pending = test.Rig.Round.HandleBnBPickupAsync(test.HostLink, new(BnBHostActor, 4), default).AsTask();
            Assert.False(pending.IsCompleted);
            release.TrySetResult();
            await Task.WhenAll(running, pending).WaitAsync(BnBWaitTimeout);
            var accepted = change == "bubble";
            Assert.Equal(accepted ? 1 : 0, room.BnBRound!.Ledger.ItemCount(BnBHostActor, BnBItemKind.Bomb));
            Assert.Equal(accepted ? BnBItemKind.None : BnBItemKind.Bomb, room.BnBRound.Ledger.GetCell(4).Item);
            Assert.Equal(accepted ? 1 : 0, test.Relay.Pickup.To(test.HostLink).Length);
            Assert.Equal(accepted ? 1 : 0, test.Relay.Pickup.To(test.GuestLink).Length);
            Assert.False(room.RoundCancelled);
        }
        finally
        {
            release.TrySetResult();
            await running.WaitAsync(BnBWaitTimeout);
            if (pending is not null) await pending.WaitAsync(BnBWaitTimeout);
        }
    }

    /// <summary>A bubbled actor still needs its owned actor and match link, and an available pickup must match its admitted position.</summary>
    [Theory]
    [InlineData("actor", false)]
    [InlineData("same-id-session", false)]
    [InlineData("stale-link", false)]
    [InlineData("position", true)]
    [InlineData("invalid-grid", true)]
    public async Task BnBBubbledPickupRetainsActorPositionAndGridValidation(string invalid, bool cancels)
    {
        await using var test = new BnBTestRig();
        var room = await test.StartAsync();
        var seat = room.FindByGame(test.Rig.Host.Id)!;
        test.Move(test.Rig.Host, 4);
        await test.Rig.Round.HandleLifeReportedAsync(test.HostLink, new(RoundLifeEvent.Bubbled, BnBHostActor), default);
        var original = room.BnBRound!.Ledger;
        var actor = BnBHostActor;
        var grid = 4;
        ISession reporter = test.HostLink;
        lock (room.Gate)
        {
            switch (invalid)
            {
                case "actor": actor = BnBGuestActor; break;
                case "same-id-session": reporter = new RecordingSession(test.HostLink.Id); break;
                case "stale-link": seat.GameLinkGeneration = room.StartGeneration - 1; break;
                case "position": test.Move(test.Rig.Host, 0); break;
                case "invalid-grid": grid = -1; break;
            }
        }
        Task Pickup() => test.Rig.Round.HandleBnBPickupAsync(reporter, new(actor, grid), default).AsTask();
        if (cancels) await Assert.ThrowsAsync<InvalidOperationException>(Pickup);
        else await Pickup();
        Assert.Same(original, room.BnBRound.Ledger);
        Assert.Equal(BnBItemKind.Bomb, original.GetCell(4).Item);
        Assert.Equal(0, original.ItemCount(BnBHostActor, BnBItemKind.Bomb));
        Assert.Empty(test.Game.Pickup.To(test.HostLink));
        Assert.Empty(test.Game.Pickup.To(test.GuestLink));
        Assert.Equal(cancels, room.RoundCancelled);
    }

    /// <summary>Forged actors, displaced positions and stale session or round leases cannot introduce a placement.</summary>
    [Theory]
    [InlineData("actor")]
    [InlineData("position")]
    [InlineData("missing-position")]
    [InlineData("nonfinite-position")]
    [InlineData("same-id-session")]
    [InlineData("stale-link")]
    [InlineData("stale-round")]
    [InlineData("scene-not-ready")]
    [InlineData("dead")]
    public async Task BnBPlacementRequiresTheCurrentActorPositionLifeAndRound(string invalid)
    {
        await using var test = new BnBTestRig();
        var room = await test.StartAsync();
        ISession session = test.HostLink;
        var actor = BnBHostActor;
        lock (room.Gate)
        {
            var seat = room.FindByGame(test.Rig.Host.Id)!;
            switch (invalid)
            {
                case "actor": actor = BnBGuestActor; break;
                case "position": test.Move(test.Rig.Host, 8); break;
                case "missing-position": seat.Movement = null; break;
                case "nonfinite-position": seat.Movement = new(float.NaN, 0, 99.5f, 0, 0, 0); break;
                case "same-id-session": session = new RecordingSession(test.HostLink.Id); break;
                case "stale-link": seat.GameLinkGeneration = room.StartGeneration - 1; break;
                case "stale-round": room.BnBRound = new(room.StartGeneration - 1, room.BnBRound!.Ledger, room.Seats.ToArray()); break;
                case "scene-not-ready": seat.SceneReady = false; break;
                case "dead": Assert.True(seat.Life.Die()); break;
            }
        }
        await test.Rig.Round.HandleBnBPlacementAsync(session, new(actor, 0), default);
        Assert.Empty(test.Game.Placement.To(test.HostLink));
        Assert.Empty(test.Game.Placement.To(test.GuestLink));
        Assert.Equal(0, room.BnBRound!.Ledger.NextGeneration);
        Assert.False(room.RoundCancelled);
    }

    /// <summary>The match-link ownership fence remains active even when the general native readiness option is disabled.</summary>
    [Fact]
    public async Task BnBCurrentMatchLinkIsRequiredIndependentlyOfNativeReadinessOption()
    {
        await using var test = new BnBTestRig();
        var room = await test.StartAsync();
        test.Options.NativeMatchRelay = false;
        lock (room.Gate) room.FindByGame(test.Rig.Host.Id)!.GameLinkGeneration = room.StartGeneration - 1;
        await test.Rig.Round.HandleBnBPlacementAsync(test.HostLink, new(BnBHostActor, 0), default);
        Assert.Empty(test.Game.Placement.To(test.HostLink));
        Assert.Empty(test.Game.Placement.To(test.GuestLink));
        Assert.Equal(0, room.BnBRound!.Ledger.NextGeneration);
    }

    /// <summary>Native reports require every participant's current capability and confirmation, and the reporter's exact relay instance.</summary>
    [Theory]
    [InlineData("capability")]
    [InlineData("relay-generation")]
    [InlineData("initialized")]
    [InlineData("confirmed")]
    [InlineData("endpoint")]
    [InlineData("other-member")]
    [InlineData("game-session")]
    [InlineData("same-id-session")]
    public async Task BnBRelayReportsRequireTheCompleteCurrentMatchLease(string invalid)
    {
        await using var test = new BnBTestRig();
        var room = await test.StartAsync(relay: true);
        ISession reporter = test.HostLink;
        lock (room.Gate)
        {
            var seat = room.FindByGame(test.Rig.Host.Id)!;
            switch (invalid)
            {
                case "capability": seat.NativeCapability = seat.NativeCapability! with { Generation = room.StartGeneration - 1 }; break;
                case "relay-generation": seat.RelayGeneration = room.StartGeneration - 1; break;
                case "initialized": seat.RelayInitialized = false; break;
                case "confirmed": seat.RelayConfirmed = false; break;
                case "endpoint": seat.EndpointAdvertised = false; break;
                case "other-member": room.FindByGame(test.Rig.Guest.Id)!.RelayConfirmed = false; break;
                case "game-session": reporter = test.Rig.Host; break;
                case "same-id-session": reporter = new RecordingSession(test.HostLink.Id); break;
            }
        }
        await test.Rig.Round.HandleBnBPlacementAsync(reporter, new(BnBHostActor, 0), default);
        Assert.Empty(test.Relay.Placement.To(test.HostLink));
        Assert.Empty(test.Relay.Placement.To(test.GuestLink));
        Assert.Empty(test.Game.Placement.To(test.Rig.Host));
        Assert.Equal(0, room.BnBRound!.Ledger.NextGeneration);
    }

    /// <summary>An admitted report waiting behind another room operation cannot cross a character incarnation change.</summary>
    [Fact]
    public async Task BnBQueuedPlacementRechecksTheCharacterIncarnation()
    {
        await using var test = new BnBTestRig();
        var room = await test.StartAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocking = room.Executor.ExecuteAsync(async _ =>
        {
            entered.TrySetResult();
            await release.Task;
        }, default);
        Task? placement = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            placement = test.Rig.Round.HandleBnBPlacementAsync(test.HostLink, new(BnBHostActor, 0), default).AsTask();
            Assert.False(placement.IsCompleted);
            lock (room.Gate) room.FindByGame(test.Rig.Host.Id)!.Life.Reset();
            release.TrySetResult();
            await blocking.WaitAsync(TimeSpan.FromSeconds(5));
            await placement.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(test.Game.Placement.To(test.HostLink));
            Assert.Equal(0, room.BnBRound!.Ledger.NextGeneration);
        }
        finally
        {
            release.TrySetResult();
            await blocking;
            if (placement is not null) await placement;
        }
    }

    /// <summary>A partial publication cannot commit; cancellation and connection closure belong only to its original generation.</summary>
    [Theory]
    [InlineData("placement", false)]
    [InlineData("explosion", false)]
    [InlineData("pickup", false)]
    [InlineData("placement", true)]
    [InlineData("explosion", true)]
    [InlineData("pickup", true)]
    public async Task BnBPublicationFailureIsLimitedToItsProducingGeneration(string kind, bool replaceGeneration)
    {
        await using var test = new BnBTestRig();
        var room = await test.StartAsync();
        if (kind == "explosion")
        {
            await test.Rig.Round.HandleBnBPlacementAsync(test.HostLink, new(BnBHostActor, 0), default);
            test.Milliseconds += 3000;
        }
        if (kind == "pickup") test.Move(test.Rig.Host, 4);
        var original = room.BnBRound!;
        var originalLedger = original.Ledger;
        ValueTask Fail(ISession session)
        {
            if (!ReferenceEquals(session, test.GuestLink)) return ValueTask.CompletedTask;
            if (replaceGeneration)
            {
                lock (room.Gate)
                {
                    room.ResetToWaiting();
                    room.MarkStarting();
                    room.RoundMapKey = room.RoundStageKey = "test";
                    _ = test.Rig.Round.PrepareBnBRound(room);
                    foreach (var member in room.Seats)
                    {
                        member.GameLinkGeneration = room.StartGeneration;
                        member.SceneReady = true;
                    }
                    test.Move(test.Rig.Host, 0);
                    test.Move(test.Rig.Guest, 8);
                    room.MarkPlaying();
                }
            }
            return ValueTask.FromException(new IOException("Synthetic BnB publication failure."));
        }
        test.Game.Placement.OnSending = kind == "placement" ? (session, _, _) => Fail(session) : null;
        test.Game.Explosion.OnSending = kind == "explosion" ? (session, _, _) => Fail(session) : null;
        test.Game.Pickup.OnSending = kind == "pickup" ? (session, _, _) => Fail(session) : null;
        Task Publish() => kind switch
        {
            "placement" => test.Rig.Round.HandleBnBPlacementAsync(test.HostLink, new(BnBHostActor, 0), default).AsTask(),
            "explosion" => test.Rig.Round.HandleBnBExplosionAsync(test.HostLink, new(BnBHostActor, [new(0, 0)]), default).AsTask(),
            _ => test.Rig.Round.HandleBnBPickupAsync(test.HostLink, new(BnBHostActor, 4), default).AsTask(),
        };
        await Assert.ThrowsAsync<IOException>(Publish);
        Assert.Same(originalLedger, original.Ledger);
        Assert.Equal(!replaceGeneration, room.RoundCancelled);
        Assert.Equal(!replaceGeneration, test.Rig.Host.Connection.IsClosed);
        Assert.Equal(!replaceGeneration, test.Rig.Guest.Connection.IsClosed);
        Assert.Equal(1, PublicationCount(test.HostLink));
        Assert.Equal(0, PublicationCount(test.GuestLink));
        if (replaceGeneration)
        {
            Assert.NotSame(original, room.BnBRound);
            Assert.Equal(original.Generation + 1, room.BnBRound!.Generation);
            Assert.Equal(0, room.BnBRound.Ledger.NextGeneration);
            test.Game.Placement.OnSending = null;
            await test.Rig.Round.HandleBnBPlacementAsync(test.HostLink, new(BnBHostActor, 0), default);
            Assert.Equal(1, room.BnBRound.Ledger.NextGeneration);
        }
        else
        {
            await Publish();
            Assert.Equal(1, PublicationCount(test.HostLink));
            Assert.Equal(0, PublicationCount(test.GuestLink));
        }
        int PublicationCount(ISession member) => kind switch
        {
            "placement" => test.Game.Placement.To(member).Length,
            "explosion" => test.Game.Explosion.To(member).Length,
            _ => test.Game.Pickup.To(member).Length,
        };
    }

    /// <summary>A relay close cancels its round before release erases indexes, even when the room cannot accept a departure.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task BnBRelayDisconnectCancelsBeforeTransportIndexesAreRemoved(bool releaseFirst, bool fullQueue)
    {
        await using var test = new BnBTestRig(options => options.RoomQueueCapacity = 1);
        var room = await test.StartAsync(relay: true);
        var original = Assert.IsType<BnBRuntimeState>(room.BnBRound);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = Task.CompletedTask;
        var queued = Task.CompletedTask;
        try
        {
            if (fullQueue)
            {
                running = room.Executor.ExecuteAsync(async ct =>
                {
                    entered.TrySetResult();
                    await release.Task.WaitAsync(ct);
                });
                await entered.Task.WaitAsync(BnBWaitTimeout);
                queued = room.Executor.ExecuteAsync(_ => ValueTask.CompletedTask);
            }
            await test.HostLink.Connection.CloseAsync("Synthetic relay disconnect.");
            if (releaseFirst)
            {
                test.Rig.Registry.Release(test.HostLink);
                Assert.Null(test.Rig.Registry.FindByConnection(test.HostLink.Connection.Id));
            }
            test.Rig.Rooms.OnConnectionClosed(new(test.HostLink.Connection, "Synthetic relay disconnect."));
            Assert.True(room.RoundCancelled);
            var cleanup = Assert.IsAssignableFrom<Task>(original.CleanupTask);
            Assert.Same(cleanup, room.BnBCleanupTask);
            await cleanup.WaitAsync(BnBWaitTimeout);
            foreach (var member in new[] { test.Rig.Host, test.Rig.Guest, test.HostLink, test.GuestLink })
                Assert.True(member.Connection.IsClosed);
            Assert.Equal(0, original.Ledger.NextGeneration);
            if (fullQueue)
            {
                Assert.False(running.IsCompleted);
                Assert.False(queued.IsCompleted);
            }
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(running, queued).WaitAsync(BnBWaitTimeout);
        }
    }

    /// <summary>Transport release outside active BnB leaves the game seats and unrelated mode state intact.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BnBRelayDisconnectIgnoresWaitingAndOtherModes(bool waiting)
    {
        await using var test = new BnBTestRig();
        var room = await test.StartAsync(relay: true, load: false);
        lock (room.Gate)
        {
            if (waiting) room.ResetToWaiting();
            else room.ModeId = 4;
        }
        await test.HostLink.Connection.CloseAsync("Synthetic unrelated relay disconnect.");
        test.Rig.Registry.Release(test.HostLink);
        test.Rig.Rooms.OnConnectionClosed(new(test.HostLink.Connection, "Synthetic unrelated relay disconnect."));
        Assert.False(room.RoundCancelled);
        Assert.True(room.BnBCleanupTask.IsCompletedSuccessfully);
        Assert.False(test.Rig.Host.Connection.IsClosed);
        Assert.False(test.Rig.Guest.Connection.IsClosed);
        Assert.False(test.GuestLink.Connection.IsClosed);
    }

    /// <summary>Replacing an open relay or releasing a prior-round relay cannot cancel the current BnB generation.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task BnBRelayReplacementAndOldGenerationReleasePreserveCurrentRound(bool newGeneration, bool closedBeforeReplacement)
    {
        await using var test = new BnBTestRig();
        var room = await test.StartAsync(relay: true);
        var original = Assert.IsType<BnBRuntimeState>(room.BnBRound);
        var oldRelay = test.HostLink;
        ISession reporter;
        if (newGeneration)
        {
            ReplaceBnBRound(test, room);
            reporter = test.Rig.Host;
            await oldRelay.Connection.CloseAsync("Synthetic previous-round relay disconnect.");
            test.Rig.Registry.Release(oldRelay);
        }
        else
        {
            if (closedBeforeReplacement) await oldRelay.Connection.CloseAsync("Synthetic superseded relay disconnect.");
            var ticket = room.FindByGame(test.Rig.Host.Id)!.NativeCapability!.Bytes.ToArray();
            var replacement = new RecordingSession(oldRelay.Id + 1000, test.Rig.Host.Profile);
            var binding = await test.Rig.Registry.BindCapabilityAsync(replacement, new(ticket), default);
            test.Rig.Registry.MarkInitialized(replacement, binding);
            await test.Rig.Round.HandleMatchConnectionReportedAsync(test.Rig.Host, new(true), default);
            reporter = replacement;
        }
        test.Rig.Rooms.OnConnectionClosed(new(oldRelay.Connection, "Synthetic obsolete relay close event."));
        Assert.False(room.RoundCancelled);
        Assert.Null(original.CleanupTask);
        Assert.True(room.BnBCleanupTask.IsCompletedSuccessfully);
        Assert.False(test.Rig.Host.Connection.IsClosed);
        Assert.False(test.Rig.Guest.Connection.IsClosed);
        await test.Rig.Round.HandleBnBPlacementAsync(reporter, new(BnBHostActor, 0), default);
        Assert.Equal(1, room.BnBRound!.Ledger.NextGeneration);
        Assert.Equal(original.Generation + (newGeneration ? 1 : 0), room.BnBRound.Generation);
    }

    /// <summary>Reports and terminal removal await direct cleanup when a full executor rejects their admission.</summary>
    [Theory]
    [InlineData("placement")]
    [InlineData("explosion")]
    [InlineData("pickup")]
    [InlineData("removal")]
    public async Task BnBReportQueueRejectionAwaitsCleanupWithoutReenteringExecutor(string kind)
    {
        await using var test = new BnBTestRig(options => options.RoomQueueCapacity = 1);
        BnBDeferredCloseConnection? deferred = null;
        test.RelaySessionFactory = (id, profile) => id == test.Rig.Guest.Id + 1000
            ? new BnBConnectionSession(new RecordingSession(id, profile), deferred = new(id))
            : new RecordingSession(id, profile);
        var room = await test.StartAsync(relay: true);
        if (kind == "removal") await CleanBnBHostDeathAsync(test);
        var original = Assert.IsType<BnBRuntimeState>(room.BnBRound);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = Task.CompletedTask;
        var queued = Task.CompletedTask;
        Task? rejected = null;
        try
        {
            running = room.Executor.ExecuteAsync(async ct =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
            });
            await entered.Task.WaitAsync(BnBWaitTimeout);
            queued = room.Executor.ExecuteAsync(_ => ValueTask.CompletedTask);
            rejected = kind switch
            {
                "placement" => test.Rig.Round.HandleBnBPlacementAsync(test.HostLink, new(BnBHostActor, 0), default).AsTask(),
                "explosion" => test.Rig.Round.HandleBnBExplosionAsync(test.HostLink, new(BnBHostActor, [new(0, 0)]), default).AsTask(),
                "removal" => test.Rig.Round.HandleSpawnAcceptedAsync(test.Rig.Host, BnBRemovalRequest(BnBHostActor), default).AsTask(),
                _ => test.Rig.Round.HandleBnBPickupAsync(test.HostLink, new(BnBHostActor, 0), default).AsTask(),
            };
            var closing = Assert.IsType<BnBDeferredCloseConnection>(deferred);
            await closing.Entered.Task.WaitAsync(BnBWaitTimeout);
            Assert.True(room.RoundCancelled);
            Assert.False(rejected.IsCompleted);
            Assert.False(queued.IsCompleted);
            Assert.False(running.IsCompleted);
            closing.Release.TrySetResult();
            await Assert.ThrowsAsync<InvalidOperationException>(() => rejected.WaitAsync(BnBWaitTimeout));
            Assert.True(room.BnBCleanupTask.IsCompletedSuccessfully);
            Assert.Same(original.CleanupTask, room.BnBCleanupTask);
            Assert.Equal(1, closing.CloseCalls);
            foreach (var member in new[] { test.Rig.Host, test.Rig.Guest, test.HostLink, test.GuestLink })
                Assert.True(member.Connection.IsClosed);
            Assert.Equal(0, original.Ledger.NextGeneration);
            Assert.Empty(test.Relay.Placement.To(test.HostLink));
            Assert.Empty(test.Relay.Explosion.To(test.HostLink));
            Assert.Empty(test.Relay.Pickup.To(test.HostLink));
            Assert.Empty(test.Rig.Respawned.To(test.Rig.Host));
        }
        finally
        {
            deferred?.Release.TrySetResult();
            release.TrySetResult();
            await Task.WhenAll(running, queued).WaitAsync(BnBWaitTimeout);
            if (rejected is not null)
                try { await rejected.WaitAsync(BnBWaitTimeout); }
                catch (InvalidOperationException) { }
        }
    }

    /// <summary>Cancellation of a previously admitted report cannot close a replacement round before that report runs.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BnBQueuedReportCancellationPreservesReplacementGeneration(bool removal)
    {
        await using var test = new BnBTestRig(options => options.RoomQueueCapacity = 2);
        var room = await test.StartAsync();
        if (removal) await CleanBnBHostDeathAsync(test);
        var original = Assert.IsType<BnBRuntimeState>(room.BnBRound);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var running = room.Executor.ExecuteAsync(async ct =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
        });
        try
        {
            await entered.Task.WaitAsync(BnBWaitTimeout);
            var pending = removal
                ? test.Rig.Round.HandleSpawnAcceptedAsync(test.Rig.Host, BnBRemovalRequest(BnBHostActor), cancellation.Token).AsTask()
                : test.Rig.Round.HandleBnBPlacementAsync(test.HostLink, new(BnBHostActor, 0), cancellation.Token).AsTask();
            ReplaceBnBRound(test, room);
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(BnBWaitTimeout));
            Assert.False(room.RoundCancelled);
            Assert.Null(original.CleanupTask);
            Assert.Null(room.BnBRound!.CleanupTask);
            Assert.False(test.Rig.Host.Connection.IsClosed);
            Assert.False(test.Rig.Guest.Connection.IsClosed);
        }
        finally
        {
            release.TrySetResult();
            await running.WaitAsync(BnBWaitTimeout);
        }
        await room.Executor.ExecuteAsync(_ => ValueTask.CompletedTask);
        await test.Rig.Round.HandleBnBPlacementAsync(test.HostLink, new(BnBHostActor, 0), default);
        Assert.Equal(original.Generation + 1, room.BnBRound!.Generation);
        Assert.Equal(1, room.BnBRound.Ledger.NextGeneration);
    }

    /// <summary>Shutdown drains detached cleanup, and inline close events share its task without recursively closing peers.</summary>
    [Fact]
    public async Task BnBShutdownAwaitsCleanupWithSynchronousNestedCloseEvents()
    {
        await using var test = new BnBTestRig();
        BnBDeferredCloseConnection? deferred = null;
        test.RelaySessionFactory = (id, profile) => id == test.Rig.Guest.Id + 1000
            ? new BnBConnectionSession(new RecordingSession(id, profile), deferred = new(id))
            : new RecordingSession(id, profile);
        var room = await test.StartAsync(relay: true);
        var closing = Assert.IsType<BnBDeferredCloseConnection>(deferred);
        closing.Closed = () =>
        {
            test.Rig.Registry.Release(test.GuestLink);
            test.Rig.Rooms.OnConnectionClosed(new(closing, "Synthetic inline close event."));
        };
        Task? shutdown = null;
        try
        {
            await test.HostLink.Connection.CloseAsync("Synthetic relay disconnect.");
            test.Rig.Registry.Release(test.HostLink);
            var cleanup = room.BnBCleanupTask;
            await closing.Entered.Task.WaitAsync(BnBWaitTimeout);
            Assert.True(room.RoundCancelled);
            Assert.Throws<InvalidOperationException>(() => test.Rig.Round.ValidateModeMap(room, "test"));
            shutdown = test.Rig.Registry.StopAllAsync();
            Assert.False(shutdown.IsCompleted);
            Assert.False(cleanup.IsCompleted);
            closing.Release.TrySetResult();
            await shutdown.WaitAsync(BnBWaitTimeout);
            Assert.Same(cleanup, room.BnBCleanupTask);
            Assert.True(cleanup.IsCompletedSuccessfully);
            Assert.True(closing.IsClosed);
            Assert.Equal(1, closing.CloseCalls);
        }
        finally
        {
            closing.Release.TrySetResult();
            if (shutdown is not null) await shutdown.WaitAsync(BnBWaitTimeout);
        }
    }

    private static void ReplaceBnBRound(BnBTestRig test, PlayRoom room)
    {
        lock (room.Gate)
        {
            room.ResetToWaiting();
            room.MarkStarting();
            room.RoundMapKey = room.RoundStageKey = "test";
            _ = test.Rig.Round.PrepareBnBRound(room);
            foreach (var member in room.Seats)
            {
                member.GameLinkGeneration = room.StartGeneration;
                member.SceneReady = true;
            }
            test.Move(test.Rig.Host, 0);
            test.Move(test.Rig.Guest, 8);
            room.MarkPlaying();
        }
    }

    private const int BnBHostActor = 1_000_000;
    private const int BnBGuestActor = 1_100_000;
    private static readonly TimeSpan BnBWaitTimeout = TimeSpan.FromSeconds(10);

    private sealed class BnBCountdownGate
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task WaitAsync(int milliseconds, CancellationToken ct)
        {
            Assert.True(milliseconds > 0);
            Entered.TrySetResult();
            return Release.Task.WaitAsync(ct);
        }
    }

    private sealed class BnBConnectionSession(RecordingSession inner, IConnection connection) : ISessionWithFeatures
    {
        public long Id => inner.Id;
        public IConnection Connection { get; } = connection;
        public ClientProfile Profile => inner.Profile;
        public ConcurrentDictionary<string, object> Items => inner.Items;
        public ISessionFeatures Features => inner.Features;
        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct) => inner.SendAsync(opcode, payload, ct);
    }

    private sealed class BnBDeferredCloseConnection(long id) : IConnection
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Action? Closed { get; set; }
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
            await Release.Task.WaitAsync(BnBWaitTimeout);
            IsClosed = true;
            Closed?.Invoke();
        }
    }

    private sealed class BnBTestRig : IAsyncDisposable
    {
        internal Rig Rig { get; }
        internal InGameOptions Options { get; }
        internal BnBTestSenders Game { get; } = new();
        internal BnBTestSenders Relay { get; } = new();
        internal ISession HostLink { get; private set; } = null!;
        internal ISession GuestLink { get; private set; } = null!;
        internal Func<long, ClientProfile, ISession>? RelaySessionFactory { get; set; }
        internal long Milliseconds { get; set; } = 1_000_000;
        private PlayRoom _room = null!;

        internal BnBTestRig(Action<InGameOptions>? configure = null)
        {
            InGameOptions? captured = null;
            Rig = new Rig(configure: options =>
            {
                captured = options;
                options.StartTimerMs = 1;
                options.BnB = true;
                options.NativeLifeModeIds = [(int)GameModeId.BnB];
                options.BnBMaximumPositionCellDistance = 0;
                options.BnBExplosionReportGraceMs = 10_000;
                options.BnBStages = [new() { StageDirectory = "test", TileLayerMinimumX = 0, TileLayerMaximumZ = 100 }];
                configure?.Invoke(options);
            });
            Options = captured!;
            // The common rig has no endpoint encoder; these tests explicitly admit each current-round link below.
            Options.NativeMatchRelay = true;
            var store = BnBTestData.Store(3, 3, items: [0, 0, 0, 0, 1, 0, 0, 0, 0]);
            store.Files[BnBRoundRules.Path] = Encoding.UTF8.GetBytes(BnBTestData.RuleYaml);
            Rig.Round.ConfigureBnB(store, Game.Senders, Relay.Senders);
            Rig.Round.BnBMilliseconds = () => Milliseconds;
            Rig.Rooms.ValidateRoundMap = Rig.Round.ValidateModeMap;
            Rig.Rooms.MapSeedPreparing = Rig.Round.PrepareBnBRound;
        }

        internal async Task<PlayRoom> StartAsync(bool relay = false, bool load = true)
        {
            await Rig.Rooms.HandleCreateAsync(Rig.Host, new("BnB room", "", (int)GameModeId.BnB, "test"), default);
            var roomId = Assert.Single(Rig.Entered.To(Rig.Host)).Room.RoomId;
            await Rig.Rooms.HandleJoinAsync(Rig.Guest, new(roomId, ""), default);
            await Rig.Rooms.HandleReadyAsync(Rig.Guest, new(true), default);
            await Rig.Rooms.HandleStartAsync(Rig.Host, new("test"), default);
            _room = Rig.Registry.Find(roomId)!;
            HostLink = await AdmitAsync(Rig.Host, relay);
            GuestLink = await AdmitAsync(Rig.Guest, relay);
            Move(Rig.Host, 0);
            Move(Rig.Guest, 8);
            if (!load) return _room;
            await Rig.Round.HandleLoadReportedAsync(Rig.Host, new(), default);
            await Rig.Round.HandleLoadReportedAsync(Rig.Guest, new(), default);
            if (relay)
            {
                await Rig.Round.HandleMatchConnectionReportedAsync(Rig.Host, new(true), default);
                await Rig.Round.HandleMatchConnectionReportedAsync(Rig.Guest, new(true), default);
            }
            await Rig.WaitStartedAsync(Rig.Host);
            await Rig.WaitStartedAsync(Rig.Guest);
            await _room.Executor.ExecuteAsync(_ => ValueTask.CompletedTask, default);
            Assert.Equal(RoomPhase.Playing, _room.Phase);
            return _room;
        }

        private async Task<ISession> AdmitAsync(ISession member, bool relay)
        {
            if (!relay)
            {
                lock (_room.Gate) _room.FindByGame(member.Id)!.GameLinkGeneration = _room.StartGeneration;
                return member;
            }
            var ticket = await Rig.Registry.IssueCapabilityAsync(member, 16, default);
            lock (_room.Gate) _room.FindByGame(member.Id)!.EndpointAdvertised = true;
            var link = RelaySessionFactory?.Invoke(member.Id + 1000, member.Profile) ?? new RecordingSession(member.Id + 1000, member.Profile);
            var binding = await Rig.Registry.BindCapabilityAsync(link, new(ticket), default);
            Rig.Registry.MarkInitialized(link, binding);
            return link;
        }

        internal void Move(ISession member, int grid)
        {
            lock (_room.Gate)
                _room.FindByGame(member.Id)!.Movement = new(grid % 3 + 0.5f, 0, 100 - grid / 3 - 0.5f, 0, 0, 0);
        }

        public ValueTask DisposeAsync() => new(Rig.Registry.StopAllAsync());
    }

    private sealed class BnBTestSenders
    {
        internal BnBPublicationSink<RoundBnBPlacementPublished> Placement { get; } = new();
        internal BnBPublicationSink<RoundBnBExplosionPublished> Explosion { get; } = new();
        internal BnBPublicationSink<RoundBnBPickupPublished> Pickup { get; } = new();
        internal BnBSenders Senders => new(Placement, Explosion, Pickup);
    }

    private sealed class BnBPublicationSink<T> : IMessageSender<T> where T : IMessage
    {
        private readonly Sink<T> _sent = new();
        internal Func<ISession, T, CancellationToken, ValueTask>? OnSending { get; set; }
        public string Protocol => "test/game";
        public async ValueTask SendAsync(ISession session, T message, CancellationToken ct)
        {
            if (OnSending is { } sending) await sending(session, message, ct);
            await _sent.SendAsync(session, message, ct);
        }
        internal T[] To(ISession session) => _sent.To(session);
    }
}
