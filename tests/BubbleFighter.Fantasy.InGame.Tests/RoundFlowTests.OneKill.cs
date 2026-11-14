using BubbleFighter.Fantasy.Abstractions;
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
    /// <summary>OneKill remains opt-in and cannot load without its native elimination exchange.</summary>
    [Fact]
    public async Task OneKillRequiresAnExplicitOptionAndFinalZoneExchange()
    {
        Assert.False(new InGameOptions().NativeOneKill);
        var disabled = new Rig();
        var enabled = new Rig(configure: options => options.NativeOneKill = true);
        try
        {
            disabled.Round.ValidateOneKill();
            Assert.Throws<ConfigException>(enabled.Round.ValidateOneKill);
            enabled.Round.FinalZoneEliminated = new Sink<RoundFinalZonePlayerEliminated>();
            enabled.Round.ValidateOneKill();
        }
        finally
        {
            await disabled.Registry.StopAllAsync();
            await enabled.Registry.StopAllAsync();
        }
    }

    /// <summary>Only the victim's confirmation kills, scores and eliminates once; neither stage is echoed to its sender.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task OneKillConfirmationSharesDeathCreditAndEliminatesOnce(bool genericDeathFirst, bool relayInput)
    {
        var rig = new Rig(configure: options => options.NativeOneKill = true);
        var eliminated = new Sink<RoundFinalZonePlayerEliminated>();
        rig.Round.FinalZoneEliminated = eliminated;
        try
        {
            var third = CaptainPlayer(33, "third");
            var room = await StartFinalZoneModeAsync(rig, GameModeId.Area32, third);
            var links = BindOneKillRelays(rig, room);
            var host = room.FindByGame(rig.Host.Id)!;
            var guest = room.FindByGame(rig.Guest.Id)!;
            var attacker = RoomRegistry.SceneEntity(host.Slot);
            var victim = RoomRegistry.SceneEntity(guest.Slot);
            var request = OneKillRouteTests.ReadFeed(RoundOneKillKind.Request, attacker, victim);
            var confirmation = OneKillRouteTests.ReadFeed(RoundOneKillKind.Confirmation, attacker, victim);
            var requester = relayInput ? links[rig.Host.Id] : rig.Host;
            var confirmer = relayInput ? links[rig.Guest.Id] : rig.Guest;
            await rig.Round.HandleFeedAsync(requester, request, default);
            Assert.Equal(SeatLife.Alive, guest.Life.State);
            Assert.Equal(0, room.Scoring.Stats(host.Slot).Kills);
            Assert.Empty(eliminated.To(rig.Host));
            Assert.Empty(rig.Feed.To(links[rig.Host.Id]));
            Assert.Equal(request.Payload.ToArray(), Assert.Single(rig.Feed.To(links[rig.Guest.Id])).Payload.ToArray());
            var death = new RoundLifeReported(RoundLifeEvent.Died, victim) { Initiator = attacker };
            if (genericDeathFirst) await rig.Round.HandleLifeReportedAsync(rig.Guest, death, default);
            await rig.Round.HandleFeedAsync(confirmer, confirmation, default);
            await rig.Round.HandleFeedAsync(confirmer, confirmation, default);
            await rig.Round.HandleLifeReportedAsync(rig.Guest, death, default);
            Assert.Equal(SeatLife.Dead, guest.Life.State);
            Assert.Equal(1, room.Scoring.Stats(host.Slot).Kills);
            Assert.Equal(1, room.Scoring.Stats(guest.Slot).Deaths);
            Assert.Equal(new(victim, 2), Assert.Single(eliminated.To(rig.Host)));
            Assert.Single(rig.Feed.To(links[rig.Guest.Id]));
            Assert.Equal(genericDeathFirst ? 0 : 1, rig.Feed.To(links[rig.Host.Id]).Length);
            if (!genericDeathFirst)
                Assert.Equal(confirmation.Payload.ToArray(), Assert.Single(rig.Feed.To(links[rig.Host.Id])).Payload.ToArray());
            Assert.Equal(RoomPhase.Playing, room.Phase);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>A victim can confirm an earlier attack after its attacker dies while the same round remains active.</summary>
    [Fact]
    public async Task OneKillConfirmationRemainsValidAfterTheAttackerDies()
    {
        var rig = new Rig(configure: options => options.NativeOneKill = true);
        var eliminated = new Sink<RoundFinalZonePlayerEliminated>();
        rig.Round.FinalZoneEliminated = eliminated;
        try
        {
            var room = await StartFinalZoneModeAsync(rig, GameModeId.Area32,
                CaptainPlayer(33, "third"), CaptainPlayer(44, "fourth"));
            var links = BindOneKillRelays(rig, room);
            var host = room.FindByGame(rig.Host.Id)!;
            var guest = room.FindByGame(rig.Guest.Id)!;
            var attacker = RoomRegistry.SceneEntity(host.Slot);
            var victim = RoomRegistry.SceneEntity(guest.Slot);
            await rig.Round.HandleFeedAsync(rig.Host,
                OneKillRouteTests.ReadFeed(RoundOneKillKind.Request, attacker, victim), default);
            await rig.Round.HandleLifeReportedAsync(rig.Host, new(RoundLifeEvent.Died, attacker), default);
            Assert.Equal(SeatLife.Dead, host.Life.State);
            Assert.Equal(SeatLife.Alive, guest.Life.State);
            var confirmation = OneKillRouteTests.ReadFeed(RoundOneKillKind.Confirmation, attacker, victim);
            await rig.Round.HandleFeedAsync(rig.Guest, confirmation, default);
            await rig.Round.HandleFeedAsync(rig.Guest, confirmation, default);
            Assert.Equal(SeatLife.Dead, guest.Life.State);
            Assert.Equal(1, room.Scoring.Stats(host.Slot).Kills);
            Assert.Equal(1, room.Scoring.Stats(guest.Slot).Deaths);
            Assert.Equal(new[] { new RoundFinalZonePlayerEliminated(attacker, 3), new(victim, 2) }, eliminated.To(rig.Host));
            Assert.Equal(confirmation.Payload.ToArray(), Assert.Single(rig.Feed.To(links[rig.Host.Id])).Payload.ToArray());
            Assert.Single(rig.Feed.To(links[rig.Guest.Id]));
            Assert.Empty(rig.Ended.To(rig.Host));
            Assert.Equal(RoomPhase.Playing, room.Phase);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Confirming the last opponent's death enters FinalZone results once and rejects repeated native or generic deaths.</summary>
    [Fact]
    public async Task OneKillFinalOpponentEndsTheRoundAndResultsOnce()
    {
        var rig = new Rig(configure: options => options.NativeOneKill = true);
        var eliminated = new Sink<RoundFinalZonePlayerEliminated>();
        rig.Round.FinalZoneEliminated = eliminated;
        try
        {
            var room = await StartFinalZoneModeAsync(rig, GameModeId.Area32);
            var links = BindOneKillRelays(rig, room);
            var host = room.FindByGame(rig.Host.Id)!;
            var guest = room.FindByGame(rig.Guest.Id)!;
            var attacker = RoomRegistry.SceneEntity(host.Slot);
            var victim = RoomRegistry.SceneEntity(guest.Slot);
            var request = OneKillRouteTests.ReadFeed(RoundOneKillKind.Request, attacker, victim);
            var confirmation = OneKillRouteTests.ReadFeed(RoundOneKillKind.Confirmation, attacker, victim);
            await rig.Round.HandleFeedAsync(rig.Host, request, default);
            await rig.Round.HandleFeedAsync(rig.Guest, confirmation, default);
            await rig.Round.HandleFeedAsync(rig.Guest, confirmation, default);
            await rig.Round.HandleFeedAsync(rig.Host, request, default);
            await rig.Round.HandleLifeReportedAsync(rig.Guest,
                new(RoundLifeEvent.Died, victim) { Initiator = attacker }, default);
            Assert.Equal(SeatLife.Dead, guest.Life.State);
            Assert.True(room.FinalZone!.Finished);
            Assert.Equal(host.Slot, Assert.Single(room.DecidedWinnerSlots!));
            Assert.Equal(1, room.Scoring.Stats(host.Slot).Kills);
            Assert.Equal(1, room.Scoring.Stats(guest.Slot).Deaths);
            foreach (var member in new[] { rig.Host, rig.Guest })
            {
                Assert.Equal(new(victim, 1), Assert.Single(eliminated.To(member)));
                var result = Assert.Single(rig.Ended.To(member));
                Assert.Equal(RoundEndReason.WinCondition, result.Reason);
                Assert.Equal(ReferenceEquals(member, rig.Host) ? RoundOutcome.Won : RoundOutcome.Lost, result.Outcome);
                Assert.Single(rig.Feed.To(links[member.Id]));
            }
            Assert.Equal(confirmation.Payload.ToArray(), Assert.Single(rig.Feed.To(links[rig.Host.Id])).Payload.ToArray());
            Assert.Equal(request.Payload.ToArray(), Assert.Single(rig.Feed.To(links[rig.Guest.Id])).Payload.ToArray());
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Partial feed, score or elimination publication cancels the round and closes every game and relay link.</summary>
    [Theory]
    [InlineData("feed-failure")]
    [InlineData("feed-cancel")]
    [InlineData("score-failure")]
    [InlineData("elimination-failure")]
    public async Task FailedOneKillPublicationCannotLeaveAnActiveRound(string fault)
    {
        var services = new ServiceRegistry();
        var context = new PluginContext(new PluginRow { Id = "one-kill-result", Plugin = "test", Source = "test" },
            services, new EventBus(NullLogger.Instance), NullLogger.Instance);
        var scores = new Sink<RoundScoreUpdated>();
        var boards = new Sink<RoundScoreboard>();
        var results = new Sink<RoundEnded>();
        var stages = new Sink<ResultStageAdvanced>();
        context.Effect(services.RegisterMany<IMessageSender<RoundScoreUpdated>>(scores));
        context.Effect(services.RegisterMany<IMessageSender<RoundScoreboard>>(boards));
        context.Effect(services.RegisterMany<IMessageSender<RoundEnded>>(results));
        context.Effect(services.RegisterMany<IMessageSender<ResultStageAdvanced>>(stages));
        var rig = new Rig(resultContext: context, configure: options => options.NativeOneKill = true);
        var eliminated = new Sink<RoundFinalZonePlayerEliminated>();
        rig.Round.FinalZoneEliminated = eliminated;
        using var cancellation = new CancellationTokenSource();
        var failure = new IOException("OneKill publication failed on its second recipient.");
        try
        {
            var third = CaptainPlayer(33, "third");
            var feedFailure = fault.StartsWith("feed-", StringComparison.Ordinal);
            var room = await StartFinalZoneModeAsync(rig, GameModeId.Area32, feedFailure ? [third] : []);
            var host = room.FindByGame(rig.Host.Id)!;
            var guest = room.FindByGame(rig.Guest.Id)!;
            var members = room.Seats.Select(seat => seat.Game).ToArray();
            var relays = room.Seats.Select(seat => new RecordingSession(100 + seat.Slot)).ToArray();
            for (var index = 0; index < relays.Length; index++)
                rig.Registry.BindRelay(relays[index], room.Seats[index].Key);
            var sends = 0;
            if (feedFailure) rig.Feed.OnSending = (_, _, token) => FailSecondSend(token);
            else if (fault == "score-failure") scores.OnSending = (_, _, token) => FailSecondSend(token);
            else eliminated.OnSending = (_, _, token) => FailSecondSend(token);
            var attacker = RoomRegistry.SceneEntity(host.Slot);
            var victim = RoomRegistry.SceneEntity(guest.Slot);
            var confirmation = OneKillRouteTests.ReadFeed(RoundOneKillKind.Confirmation, attacker, victim);
            var publication = rig.Round.HandleFeedAsync(rig.Guest, confirmation, cancellation.Token).AsTask();
            if (fault == "feed-cancel")
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => publication);
            else
                Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => publication));
            Assert.Equal(2, sends);
            Assert.True(room.RoundCancelled);
            Assert.Equal(SeatLife.Dead, guest.Life.State);
            Assert.Equal(1, room.Scoring.Stats(host.Slot).Kills);
            Assert.Equal(1, room.Scoring.Stats(guest.Slot).Deaths);
            Assert.All(members.Concat(relays), member => Assert.True(member.Connection.IsClosed));
            var feedCount = members.Concat(relays).Sum(member => rig.Feed.To(member).Length);
            var scoreCount = members.Sum(member => scores.To(member).Length);
            var eliminationCount = members.Sum(member => eliminated.To(member).Length);
            await rig.Round.HandleFeedAsync(rig.Guest, confirmation, default);
            await rig.Round.HandleLifeReportedAsync(rig.Guest,
                new(RoundLifeEvent.Died, victim) { Initiator = attacker }, default);
            await rig.Round.SynchronizeFinalZoneAsync(room, default);
            Assert.Equal(1, room.Scoring.Stats(host.Slot).Kills);
            Assert.Equal(1, room.Scoring.Stats(guest.Slot).Deaths);
            Assert.Equal(feedCount, members.Concat(relays).Sum(member => rig.Feed.To(member).Length));
            Assert.Equal(scoreCount, members.Sum(member => scores.To(member).Length));
            Assert.Equal(eliminationCount, members.Sum(member => eliminated.To(member).Length));
            Assert.All(members, member =>
            {
                Assert.Empty(results.To(member));
                Assert.Empty(boards.To(member));
                Assert.Empty(stages.To(member));
            });
            Assert.False(room.HasResultSequence);

            void FailSecondSend(CancellationToken token)
            {
                if (++sends != 2) return;
                if (fault == "feed-cancel")
                {
                    cancellation.Cancel();
                    token.ThrowIfCancellationRequested();
                }
                throw failure;
            }
        }
        finally
        {
            await rig.Registry.StopAllAsync();
            await context.UnwindAsync();
        }
    }

    /// <summary>A failed send retained from an old round cannot cancel or close its replacement round.</summary>
    [Fact]
    public async Task FailedOneKillPublicationDoesNotCloseAReplacementRound()
    {
        var rig = new Rig(configure: options => options.NativeOneKill = true);
        rig.Round.FinalZoneEliminated = new Sink<RoundFinalZonePlayerEliminated>();
        var failure = new IOException("The old round's feed failed after replacement.");
        try
        {
            var room = await StartFinalZoneModeAsync(rig, GameModeId.Area32, CaptainPlayer(33, "third"));
            var links = BindOneKillRelays(rig, room);
            var host = room.FindByGame(rig.Host.Id)!;
            var guest = room.FindByGame(rig.Guest.Id)!;
            var generation = room.StartGeneration;
            var sends = 0;
            rig.Feed.OnSending = (_, _, _) =>
            {
                if (++sends != 2) return;
                lock (room.Gate)
                {
                    room.ResetToWaiting();
                    room.MarkStarting();
                    room.MarkPlaying();
                    rig.Round.InitializeFinalZone(room);
                    foreach (var seat in room.Seats)
                        rig.Registry.BindRelay(links[seat.Game.Id], seat.Key);
                }
                throw failure;
            };
            var confirmation = OneKillRouteTests.ReadFeed(RoundOneKillKind.Confirmation,
                RoomRegistry.SceneEntity(host.Slot), RoomRegistry.SceneEntity(guest.Slot));
            Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() =>
                rig.Round.HandleFeedAsync(rig.Guest, confirmation, default).AsTask()));
            Assert.True(room.StartGeneration > generation);
            Assert.False(room.RoundCancelled);
            Assert.Equal(RoomPhase.Playing, room.Phase);
            Assert.All(room.Seats, seat =>
            {
                Assert.False(seat.Game.Connection.IsClosed);
                Assert.False(links[seat.Game.Id].Connection.IsClosed);
                Assert.Equal(room.StartGeneration, seat.RelayGeneration);
                Assert.Equal(SeatLife.Alive, seat.Life.State);
                Assert.Equal(0, room.Scoring.Stats(seat.Slot).Kills);
            });
            Assert.Empty(rig.Ended.To(rig.Host));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Rejected native OneKill packets cannot change lives, award points, eliminate a player or reach a peer.</summary>
    [Theory]
    [InlineData("disabled")]
    [InlineData("mode")]
    [InlineData("no-final-zone")]
    [InlineData("foreign-request")]
    [InlineData("foreign-confirmation")]
    [InlineData("unknown-attacker")]
    [InlineData("unknown-victim")]
    [InlineData("self")]
    [InlineData("near-handle")]
    [InlineData("replaced-game")]
    [InlineData("replaced-relay")]
    [InlineData("old-relay")]
    [InlineData("closed")]
    [InlineData("old-round")]
    [InlineData("cancelled")]
    [InlineData("waiting")]
    [InlineData("echo")]
    [InlineData("dead-attacker-request")]
    public async Task OneKillAdmissionRejectsForeignOrInactiveActions(string fault)
    {
        var rig = new Rig(configure: options => options.NativeOneKill = fault != "disabled");
        var eliminated = new Sink<RoundFinalZonePlayerEliminated>();
        rig.Round.FinalZoneEliminated = eliminated;
        try
        {
            var third = CaptainPlayer(33, "third");
            var room = await StartFinalZoneModeAsync(rig, fault == "mode" ? GameModeId.FinalZone : GameModeId.Area32, third);
            var links = BindOneKillRelays(rig, room);
            var host = room.FindByGame(rig.Host.Id)!;
            var guest = room.FindByGame(rig.Guest.Id)!;
            var attacker = RoomRegistry.SceneEntity(host.Slot);
            var victim = RoomRegistry.SceneEntity(guest.Slot);
            var probe = new RoundFeedSent(0x8000, new byte[] { 1 }) { OnRelay = true, ReportedActors = [attacker] };
            await rig.Round.HandleFeedAsync(rig.Host, probe, default);
            Assert.Empty(rig.Feed.To(links[rig.Host.Id]));
            Assert.Equal(probe.Payload.ToArray(), Assert.Single(rig.Feed.To(links[rig.Guest.Id])).Payload.ToArray());
            Assert.Equal(probe.Payload.ToArray(), Assert.Single(rig.Feed.To(links[third.Id])).Payload.ToArray());
            var deliveries = links.ToDictionary(pair => pair.Key, pair => rig.Feed.To(pair.Value).Length);
            var kind = fault is "foreign-request" or "dead-attacker-request" ? RoundOneKillKind.Request : RoundOneKillKind.Confirmation;
            ISession sender = kind == RoundOneKillKind.Request ? rig.Host : rig.Guest;
            RecordingSession? relay = null;
            switch (fault)
            {
                case "no-final-zone": rig.Round.FinalZoneEliminated = null; break;
                case "foreign-request": sender = rig.Guest; break;
                case "foreign-confirmation": sender = rig.Host; break;
                case "unknown-attacker": attacker = 5000123; break;
                case "unknown-victim": victim = 5000123; break;
                case "self": attacker = victim; break;
                case "near-handle": victim++; break;
                case "replaced-game": sender = CaptainPlayer(rig.Guest.Id, "replacement"); break;
                case "replaced-relay":
                case "old-relay":
                    relay = new RecordingSession(92);
                    rig.Registry.BindRelay(relay, guest.Key);
                    sender = fault == "replaced-relay" ? new RecordingSession(relay.Id) : relay;
                    if (fault == "old-relay") guest.RelayGeneration = room.StartGeneration - 1;
                    break;
                case "closed": await sender.Connection.CloseAsync("test"); break;
                case "old-round": room.FinalZone = new(room.StartGeneration - 1, room.Seats); break;
                case "cancelled": room.RoundCancelled = true; break;
                case "waiting": room.ResetToWaiting(); break;
                case "dead-attacker-request": host.Life.Die(); break;
            }
            var before = room.Seats.Select(seat => seat.Life.State).ToArray();
            var feed = OneKillRouteTests.ReadFeed(kind, attacker, victim);
            if (fault == "echo") feed = feed with { IncludeSender = true };
            await rig.Round.HandleFeedAsync(sender, feed, default);
            Assert.Equal(before, room.Seats.Select(seat => seat.Life.State));
            Assert.All(room.Seats, seat => Assert.Equal(0, room.Scoring.Stats(seat.Slot).Kills));
            Assert.Empty(eliminated.To(rig.Host));
            Assert.Empty(rig.Feed.To(rig.Host));
            Assert.Empty(rig.Feed.To(rig.Guest));
            Assert.Empty(rig.Feed.To(third));
            Assert.All(links, pair => Assert.Equal(deliveries[pair.Key], rig.Feed.To(pair.Value).Length));
            if (relay is not null) Assert.Empty(rig.Feed.To(relay));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>A request or confirmation queued before a round or life transition cannot act on its replacement.</summary>
    [Theory]
    [InlineData(RoundOneKillKind.Request, false)]
    [InlineData(RoundOneKillKind.Request, true)]
    [InlineData(RoundOneKillKind.Confirmation, false)]
    [InlineData(RoundOneKillKind.Confirmation, true)]
    public async Task QueuedOneKillCannotCrossRoundOrLifeGeneration(RoundOneKillKind kind, bool replaceRound)
    {
        var rig = new Rig(configure: options => options.NativeOneKill = true);
        var eliminated = new Sink<RoundFinalZonePlayerEliminated>();
        rig.Round.FinalZoneEliminated = eliminated;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var room = await StartFinalZoneModeAsync(rig, GameModeId.Area32, CaptainPlayer(33, "third"));
            var links = BindOneKillRelays(rig, room);
            var host = room.FindByGame(rig.Host.Id)!;
            var guest = room.FindByGame(rig.Guest.Id)!;
            var boundary = room.Executor.ExecuteAsync(async ct =>
            {
                entered.SetResult();
                await release.Task.WaitAsync(ct);
                lock (room.Gate)
                {
                    if (replaceRound)
                    {
                        room.ResetToWaiting();
                        room.MarkStarting();
                        room.MarkPlaying();
                        rig.Round.InitializeFinalZone(room);
                        foreach (var seat in room.Seats)
                            rig.Registry.BindRelay(links[seat.Game.Id], seat.Key);
                    }
                    else guest.Life.Reset();
                }
            });
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var feed = OneKillRouteTests.ReadFeed(kind, RoomRegistry.SceneEntity(host.Slot), RoomRegistry.SceneEntity(guest.Slot));
            var queued = rig.Round.HandleFeedAsync(kind == RoundOneKillKind.Request ? rig.Host : rig.Guest, feed, default).AsTask();
            release.TrySetResult();
            await Task.WhenAll(boundary, queued).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(SeatLife.Alive, guest.Life.State);
            Assert.Equal(0, room.Scoring.Stats(host.Slot).Kills);
            Assert.Empty(eliminated.To(rig.Host));
            Assert.Empty(rig.Feed.To(rig.Host));
            Assert.Empty(rig.Feed.To(rig.Guest));
            Assert.All(links.Values, link => Assert.Empty(rig.Feed.To(link)));
            Assert.All(room.Seats, seat => Assert.Equal(room.StartGeneration, seat.RelayGeneration));
        }
        finally
        {
            release.TrySetResult();
            await rig.Registry.StopAllAsync();
        }
    }

    private static IReadOnlyDictionary<long, RecordingSession> BindOneKillRelays(Rig rig, PlayRoom room)
    {
        var links = new Dictionary<long, RecordingSession>();
        foreach (var seat in room.Seats)
        {
            var relay = new RecordingSession(100 + seat.Slot);
            Assert.Same(seat, rig.Registry.BindRelay(relay, seat.Key));
            Assert.True(rig.Registry.IsMatchBound(relay));
            Assert.Equal(room.StartGeneration, seat.RelayGeneration);
            links.Add(seat.Game.Id, relay);
        }
        return links;
    }
}
