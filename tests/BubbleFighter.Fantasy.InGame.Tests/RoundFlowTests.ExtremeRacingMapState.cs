using System.Collections.Concurrent;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.InGame.Sessions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class RoundFlowTests
{
    /// <summary>Every loaded racer receives the same frozen map state once, before countdown and before stage two.</summary>
    [Fact]
    public async Task RacingMapStateIsSharedFrozenAndSentAfterLoadingBeforeCountdown()
    {
        var (rig, sender, store, racing) = RacingMapStateRig();
        var events = new ConcurrentQueue<(long Session, string Kind)>();
        sender.OnSending = (session, _, _) => events.Enqueue((session.Id, "map"));
        rig.Countdown.OnSending = (session, _, _) => events.Enqueue((session.Id, "countdown"));
        rig.Started.OnSending = (session, _, _) => events.Enqueue((session.Id, "started"));
        try
        {
            var room = await PrepareRacingInitializationRoundAsync(rig);
            await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
            Assert.Empty(sender.To(rig.Host));
            await rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default);
            await rig.WaitStartedAsync(rig.Host);
            await rig.WaitStartedAsync(rig.Guest);
            var frozen = Assert.Single(sender.To(rig.Host));
            Assert.Same(frozen, Assert.Single(sender.To(rig.Guest)));
            foreach (var member in new[] { rig.Host, rig.Guest })
                Assert.Equal(new[] { "map", "countdown", "started" }, events.Where(value => value.Session == member.Id).Select(value => value.Kind));
            var reads = store.Reads;
            store.Options = "malformed replacement";
            await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
            await WaitForAsync(() => room.ExtremeRacing.IsActive);
            await rig.Round.HandleExtremeRacingGoalAsync(rig.Host, new(1), default);
            await rig.Round.HandleExtremeRacingGoalAsync(rig.Guest, new(1), default);
            await WaitForAsync(() => racing.Rounds.To(rig.Host).Length == 1);
            Assert.Equal(2, Assert.Single(racing.Rounds.To(rig.Host)).Round);
            Assert.Same(frozen, Assert.Single(sender.To(rig.Host)));
            Assert.Equal(reads, store.Reads);
            Assert.Equal(2, room.ExtremeRacing.MapStateRecipients.Count);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>A partial map-state send fails the match and closes its current connections before countdown can begin.</summary>
    [Fact]
    public async Task RacingMapStateFailureCancelsCurrentGeneration()
    {
        var (rig, sender, _, _) = RacingMapStateRig();
        sender.OnSending = (session, _, _) =>
        {
            if (ReferenceEquals(session, rig.Guest)) throw new IOException("Synthetic map-state failure.");
        };
        try
        {
            var room = await PrepareRacingInitializationRoundAsync(rig);
            await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
            await Assert.ThrowsAsync<IOException>(() => rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default).AsTask());
            Assert.Single(sender.To(rig.Host));
            Assert.Empty(sender.To(rig.Guest));
            Assert.True(room.RoundCancelled);
            Assert.True(rig.Host.Connection.IsClosed);
            Assert.True(rig.Guest.Connection.IsClosed);
            Assert.Empty(rig.Countdown.To(rig.Host));
            Assert.Empty(rig.Started.To(rig.Host));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>An obsolete map-state send fails without closing connections now owned by a replacement match.</summary>
    [Fact]
    public async Task RacingMapStateCompletionCannotCancelReplacementGeneration()
    {
        var (rig, sender, _, _) = RacingMapStateRig();
        try
        {
            var room = await PrepareRacingInitializationRoundAsync(rig);
            ExtremeRacingRoundState? replacement = null;
            sender.OnSending = (session, _, _) =>
            {
                if (!ReferenceEquals(session, rig.Host)) return;
                lock (room.Gate)
                {
                    room.ResetToWaiting();
                    room.MarkStarting();
                    replacement = room.ExtremeRacing;
                }
            };
            await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default).AsTask());
            Assert.NotNull(replacement);
            Assert.Same(replacement, room.ExtremeRacing);
            Assert.Equal(RoomPhase.Starting, room.Phase);
            Assert.False(room.RoundCancelled);
            Assert.False(rig.Host.Connection.IsClosed);
            Assert.False(rig.Guest.Connection.IsClosed);
            Assert.Empty(room.ExtremeRacing.MapStateRecipients);
            Assert.Empty(rig.Countdown.To(rig.Host));
            Assert.Empty(rig.Started.To(rig.Host));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>The next match replaces the entire map-state incarnation and its recipient set.</summary>
    [Fact]
    public async Task RacingMapStateDoesNotSurviveRoomRestart()
    {
        var (rig, sender, _, _) = RacingMapStateRig();
        try
        {
            var room = await StartExtremeRacingRoundAsync(rig);
            var previous = room.ExtremeRacing;
            Assert.NotNull(previous.MapState);
            Assert.Single(sender.To(rig.Host));
            lock (room.Gate)
            {
                room.ResetToWaiting();
                room.MarkStarting();
                Assert.NotSame(previous, room.ExtremeRacing);
                Assert.Null(room.ExtremeRacing.MapState);
                Assert.Empty(room.ExtremeRacing.MapStateRecipients);
            }
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    private static (Rig Rig, Sink<RoundExtremeRacingMapState> Sender, RacingMapStateStore Store, ExtremeRacingSinks Racing) RacingMapStateRig()
    {
        var rig = new Rig(configure: options =>
        {
            options.ExtremeRacingMapState = true;
            options.ExtremeRacingMapStateSelection = "seeded";
            options.ExtremeRacingMapStateSeed = 43;
            options.ExtremeRacingRounds = 3;
            options.ExtremeRacingRoundMs = 60_000;
            options.ExtremeRacingFinishCountdownMs = 60_000;
            options.ExtremeRacingIntermissionMs = ExtremeRacingPause;
        });
        var racing = new ExtremeRacingSinks();
        rig.Round.ExtremeRacing = racing.Senders;
        var store = new RacingMapStateStore();
        var sender = new Sink<RoundExtremeRacingMapState>();
        rig.Round.ConfigureExtremeRacingMapState(store, sender);
        rig.Rooms.ValidateRoundMap = rig.Round.ValidateModeMap;
        return (rig, sender, store, racing);
    }
}
