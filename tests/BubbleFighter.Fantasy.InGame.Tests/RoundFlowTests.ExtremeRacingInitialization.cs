using System.Collections.Concurrent;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.InGame.Sessions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class RoundFlowTests
{
    /// <summary>The real loading barrier awaits both initialization sends between countdown and started, once per recipient.</summary>
    [Fact]
    public async Task RacingInitializationFollowsCountdownAndCompletesBeforeStarted()
    {
        var (rig, initialization) = RacingInitializationRig();
        var events = new ConcurrentQueue<(long Session, string Kind)>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? loading = null;
        rig.Loaded.OnSending = (session, _, _) => events.Enqueue((session.Id, "loaded"));
        rig.Countdown.OnSending = (session, _, _) => events.Enqueue((session.Id, "countdown"));
        rig.Started.OnSending = (session, _, _) =>
        {
            Assert.Single(initialization.To(session));
            events.Enqueue((session.Id, "started"));
        };
        initialization.OnSending = async (session, _) =>
        {
            if (ReferenceEquals(session, rig.Guest))
            {
                entered.TrySetResult();
                await release.Task;
            }
            events.Enqueue((session.Id, "initialization"));
        };
        try
        {
            var room = await PrepareRacingInitializationRoundAsync(rig);
            await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
            Assert.Empty(initialization.To(rig.Host));
            Assert.Empty(rig.Countdown.To(rig.Host));
            loading = rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default).AsTask();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(loading.IsCompleted);
            var duplicateHost = rig.Round.HandleLoadReportedAsync(rig.Host, new(), default).AsTask();
            var duplicateGuest = rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default).AsTask();
            Assert.Single(rig.Countdown.To(rig.Host));
            Assert.Single(rig.Countdown.To(rig.Guest));
            Assert.Empty(rig.Started.To(rig.Host));
            Assert.Empty(rig.Started.To(rig.Guest));
            release.TrySetResult();
            await Task.WhenAll(loading, duplicateHost, duplicateGuest).WaitAsync(TimeSpan.FromSeconds(5));
            await rig.WaitStartedAsync(rig.Host);
            await rig.WaitStartedAsync(rig.Guest);
            await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
            await rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default);
            foreach (var member in new[] { rig.Host, rig.Guest })
            {
                Assert.Single(initialization.To(member));
                Assert.Equal(new[] { "countdown", "initialization", "started" },
                    events.Where(entry => entry.Session == member.Id && entry.Kind != "loaded").Select(entry => entry.Kind));
            }
            lock (room.Gate)
            {
                Assert.Equal(room.StartGeneration, room.RacingInitialization!.Generation);
                Assert.Equal(2, room.RacingInitialization.Recipients.Count);
            }
        }
        finally
        {
            release.TrySetResult();
            if (loading is not null) await loading.WaitAsync(TimeSpan.FromSeconds(5));
            await rig.Registry.StopAllAsync();
        }
    }

    /// <summary>The pre-start hook refuses an unconfigured stage while the room is still waiting and before any map-entry send.</summary>
    [Fact]
    public async Task RacingInitializationRejectsUnknownStageBeforeMapEntry()
    {
        var (rig, initialization) = RacingInitializationRig();
        try
        {
            await rig.Rooms.HandleCreateAsync(rig.Host, new RoomCreateRequested("race room", "", (int)GameModeId.ExtremeRacing, "map"), default);
            var roomId = Assert.Single(rig.Entered.To(rig.Host)).Room.RoomId;
            await rig.Rooms.HandleJoinAsync(rig.Guest, new RoomJoinRequested(roomId, ""), default);
            await rig.Rooms.HandleReadyAsync(rig.Guest, new RoomReadyRequested(true), default);
            await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Rooms.HandleStartAsync(rig.Host,
                new RoomStartRequested("unconfigured_stage"), default).AsTask());
            Assert.Equal(RoomPhase.Waiting, rig.Registry.Find(roomId)!.Phase);
            Assert.Empty(rig.Starting.To(rig.Host));
            Assert.Empty(rig.Starting.To(rig.Guest));
            Assert.Empty(rig.Countdown.To(rig.Host));
            Assert.Empty(initialization.To(rig.Host));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>An unsupported stage caught at the loaded barrier sends neither countdown nor initialization.</summary>
    [Fact]
    public async Task RacingInitializationRejectsUnknownStageBeforeCountdown()
    {
        var (rig, initialization) = RacingInitializationRig();
        try
        {
            var room = await PrepareRacingInitializationRoundAsync(rig);
            lock (room.Gate) room.RoundStageKey = "unconfigured_stage";
            await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
            await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default).AsTask());
            Assert.Empty(rig.Countdown.To(rig.Host));
            Assert.Empty(rig.Countdown.To(rig.Guest));
            Assert.Empty(initialization.To(rig.Host));
            Assert.Empty(rig.Started.To(rig.Host));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Malformed helper data and a native/configured round-count mismatch are rejected during initialization configuration.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RacingInitializationConfigurationRequiresUsableMapData(bool malformed)
    {
        var (rig, initialization) = RacingInitializationRig(mount: false,
            configure: malformed ? null : options => options.ExtremeRacingRounds = 2);
        try
        {
            if (malformed)
                Assert.Throws<InvalidDataException>(() => rig.Round.ConfigureExtremeRacingInitialization(
                    ExtremeRacingMapDataTests.ForStage("map", malformed: true), initialization));
            else
                Assert.Throws<ConfigException>(() => rig.Round.ConfigureExtremeRacingInitialization(
                    ExtremeRacingMapDataTests.ForStage("map"), initialization));
            Assert.Empty(rig.Countdown.To(rig.Host));
            Assert.Empty(initialization.To(rig.Host));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>A partial initialization failure cancels the current generation, closes its connections and never replays a surviving stage.</summary>
    [Fact]
    public async Task RacingInitializationPartialSendFailureCancelsAndClosesCurrentGeneration()
    {
        var (rig, initialization) = RacingInitializationRig();
        initialization.OnSending = (session, _) => ReferenceEquals(session, rig.Guest)
            ? ValueTask.FromException(new IOException("Synthetic initialization send failure.")) : ValueTask.CompletedTask;
        try
        {
            var room = await PrepareRacingInitializationRoundAsync(rig);
            await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
            await Assert.ThrowsAsync<IOException>(() => rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default).AsTask());
            Assert.Single(initialization.To(rig.Host));
            Assert.Empty(initialization.To(rig.Guest));
            Assert.True(rig.Host.Connection.IsClosed);
            Assert.True(rig.Guest.Connection.IsClosed);
            lock (room.Gate) Assert.True(room.RoundCancelled);
            Assert.Empty(rig.Started.To(rig.Host));
            await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
            await rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default);
            Assert.Single(initialization.To(rig.Host));
            Assert.Empty(initialization.To(rig.Guest));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>A stale send's failure cannot cancel or close a replacement generation, which initializes its new stage separately.</summary>
    [Fact]
    public async Task RacingInitializationFailureDoesNotCancelReplacementGeneration()
    {
        var (rig, initialization) = RacingInitializationRig();
        try
        {
            var room = await PrepareRacingInitializationRoundAsync(rig);
            var originalGeneration = room.StartGeneration;
            initialization.OnSending = (session, _) =>
            {
                if (!ReferenceEquals(session, rig.Guest)) return ValueTask.CompletedTask;
                lock (room.Gate)
                {
                    var map = room.RacingInitialization!.Map;
                    room.ResetToWaiting();
                    room.MarkStarting();
                    room.RoundMapKey = room.RoundStageKey = "map";
                    room.RacingInitialization = new(room.StartGeneration, map);
                }
                return ValueTask.FromException(new IOException("Synthetic previous-generation failure."));
            };
            await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
            await Assert.ThrowsAsync<IOException>(() => rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default).AsTask());
            lock (room.Gate)
            {
                Assert.Equal(originalGeneration + 1, room.StartGeneration);
                Assert.False(room.RoundCancelled);
                Assert.Equal(room.StartGeneration, room.RacingInitialization!.Generation);
                Assert.Empty(room.RacingInitialization.Recipients);
            }
            Assert.False(rig.Host.Connection.IsClosed);
            Assert.False(rig.Guest.Connection.IsClosed);
            initialization.OnSending = null;
            await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
            await rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default);
            await rig.WaitStartedAsync(rig.Host);
            await rig.WaitStartedAsync(rig.Guest);
            Assert.Equal(2, initialization.To(rig.Host).Length);
            Assert.Single(initialization.To(rig.Guest));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    private static (Rig Rig, RacingInitializationSink Initialization) RacingInitializationRig(bool mount = true,
        Action<InGameOptions>? configure = null)
    {
        var rig = new Rig(configure: options =>
        {
            options.ExtremeRacingRounds = 3;
            options.ExtremeRacingRoundMs = 60_000;
            options.ExtremeRacingFinishCountdownMs = 60_000;
            options.ExtremeRacingIntermissionMs = ExtremeRacingPause;
            options.ExtremeRacingPlacePoints = [10, 8];
            options.ExtremeRacingInitialization = true;
            options.ExtremeRacingMaximumFileBytes = 4096;
            options.ExtremeRacingMaximumHelpers = 100;
            options.ExtremeRacingMaximumObjects = 100;
            options.ExtremeRacingStages = [new()
            {
                StageDirectory = "map", FirstCannonIdentity = 100, FirstCoinIdentity = 200,
                FirstStoneDisposerIdentity = 300, CoinVariant = 0, CoinRounds = [3, 1],
            }];
            configure?.Invoke(options);
        });
        rig.Round.ExtremeRacing = new ExtremeRacingSinks().Senders;
        rig.Rooms.ValidateRoundMap = rig.Round.ValidateModeMap;
        var initialization = new RacingInitializationSink();
        if (mount) rig.Round.ConfigureExtremeRacingInitialization(ExtremeRacingMapDataTests.ForStage("map"), initialization);
        return (rig, initialization);
    }

    private static async Task<PlayRoom> PrepareRacingInitializationRoundAsync(Rig rig)
    {
        await rig.Rooms.HandleCreateAsync(rig.Host, new RoomCreateRequested("race room", "", (int)GameModeId.ExtremeRacing, "map"), default);
        var roomId = Assert.Single(rig.Entered.To(rig.Host)).Room.RoomId;
        await rig.Rooms.HandleJoinAsync(rig.Guest, new RoomJoinRequested(roomId, ""), default);
        await rig.Rooms.HandleReadyAsync(rig.Guest, new RoomReadyRequested(true), default);
        await rig.Rooms.HandleStartAsync(rig.Host, new RoomStartRequested("map"), default);
        var room = rig.Registry.Find(roomId)!;
        Assert.Equal(RoomPhase.Starting, room.Phase);
        return room;
    }

    private sealed class RacingInitializationSink : IMessageSender<RoundExtremeRacingInitialized>
    {
        private readonly Sink<RoundExtremeRacingInitialized> _sent = new();
        internal Func<ISession, CancellationToken, ValueTask>? OnSending { get; set; }
        public string Protocol => "test/game";
        public async ValueTask SendAsync(ISession session, RoundExtremeRacingInitialized message, CancellationToken ct)
        {
            if (OnSending is { } sending) await sending(session, ct);
            await _sent.SendAsync(session, message, ct);
        }
        internal RoundExtremeRacingInitialized[] To(ISession session) => _sent.To(session);
    }
}
