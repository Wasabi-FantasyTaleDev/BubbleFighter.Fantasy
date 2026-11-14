using System.Collections.Concurrent;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Rooms;
using Xunit;

namespace BubbleFighter.Fantasy.Pets.Tests;

/// <summary>Exercises bounded pet mutation marks and held-start replay without sharing a room executor's execution context.</summary>
public sealed class PetRoomMutationPolicyTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);
    private static readonly AsyncLocal<string?> ExecutorContext = new();

    /// <summary>Room lookup observes the mark, and duplicate admission never clears the first mutation.</summary>
    [Fact]
    public async Task MarksBeforeRoomLookupAndRefusesDuplicateMutation()
    {
        await using var rig = new Rig();
        rig.Host.FindOverride = _ =>
        {
            Assert.Equal(RoomStartDecision.Hold, rig.Policy.EvaluateStart(rig.Room, 1));
            return rig.Room;
        };
        Assert.True(rig.Policy.TryBegin(2));
        Assert.False(rig.Policy.TryBegin(2));
        Assert.Equal(RoomStartDecision.Hold, rig.Policy.EvaluateStart(rig.Room, 1));
        rig.Policy.End(2);
        Assert.Equal(RoomStartDecision.Proceed, rig.Policy.EvaluateStart(rig.Room, 1));
    }

    /// <summary>A started room or failed host lookup releases the provisional mark so later admission can be retried.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnavailableRoomAndLookupFailureDoNotLeakMarks(bool lookupFails)
    {
        await using var rig = new Rig();
        rig.Host.FindOverride = _ => lookupFails ? throw new IOException("lookup failed") : rig.Room with { Waiting = false };
        if (lookupFails) Assert.Throws<IOException>(() => rig.Policy.TryBegin(2));
        else Assert.False(rig.Policy.TryBegin(2));
        Assert.Equal(RoomStartDecision.Proceed, rig.Policy.EvaluateStart(rig.Room, 1));
        rig.Host.FindOverride = null;
        Assert.True(rig.Policy.TryBegin(2));
        rig.Policy.End(2);
    }

    /// <summary>Persistence and appearance completion do not release admission before the final publication barrier.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HoldsManualAndAutomaticStartsThroughCompletePublication(bool automatic)
    {
        await using var rig = new Rig();
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(rig.Policy.TryBegin(2));
        async Task MutationAsync()
        {
            try { await published.Task.WaitAsync(Limit); }
            finally { rig.Policy.End(2); }
        }
        var mutation = MutationAsync();
        try
        {
            Assert.True(await rig.Host.StartAsync(rig.Room, 1, automatic));
            await rig.Policy.OnAppearanceChangedAsync(rig.Room, 2, default);
            Assert.Equal(RoomStartDecision.Hold, rig.Policy.EvaluateStart(rig.Room with { AutomaticStart = automatic }, 1));
            Assert.Equal(0, rig.Host.ResumeCount);
            Assert.Equal(0, rig.Host.StartedCount);
            published.TrySetResult();
            await mutation.WaitAsync(Limit);
            await rig.Host.ResumeFinished.Task.WaitAsync(Limit);
            Assert.Equal(1, rig.Host.ResumeCount);
            Assert.Equal(1, rig.Host.StartedCount);
            Assert.Equal(1, rig.Notices);
        }
        finally
        {
            published.TrySetResult();
            await mutation.WaitAsync(Limit);
        }
    }

    /// <summary>A mutation that finishes between evaluation and the held callback cannot lose its resumption wake.</summary>
    [Fact]
    public async Task ReleaseBeforeHeldCallbackStillResumesOnce()
    {
        await using var rig = new Rig();
        Assert.True(rig.Policy.TryBegin(2));
        Assert.True(await rig.Host.StartAsync(rig.Room, 1, beforeNotice: () => rig.Policy.End(2)));
        await rig.Host.ResumeFinished.Task.WaitAsync(Limit);
        Assert.Equal(1, rig.Host.ResumeCount);
        Assert.Equal(1, rig.Host.StartedCount);
    }

    /// <summary>Repeated held callbacks announce once, and release during the notice waits for that callback to finish.</summary>
    [Fact]
    public async Task HeldNoticeIsCoalescedAndCompletesBeforeReplay()
    {
        var noticeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseNotice = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var rig = new Rig(notice: async (_, _, ct) =>
        {
            noticeEntered.TrySetResult();
            await releaseNotice.Task.WaitAsync(ct);
        });
        Assert.True(rig.Policy.TryBegin(2));
        var start = rig.Host.StartAsync(rig.Room, 1).AsTask();
        try
        {
            await noticeEntered.Task.WaitAsync(Limit);
            await rig.Policy.OnStartHeldAsync(rig.Room, 1, default);
            rig.Policy.End(2);
            Assert.Equal(0, rig.Host.ResumeCount);
            releaseNotice.TrySetResult();
            await start.WaitAsync(Limit);
            await rig.Host.ResumeFinished.Task.WaitAsync(Limit);
            Assert.Equal(1, rig.Notices);
            Assert.Equal(1, rig.Host.ResumeCount);
        }
        finally { releaseNotice.TrySetResult(); await start.WaitAsync(Limit); }
    }

    /// <summary>A failing or cancelled mutation releases admission from its caller's finally block.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedMutationCleanupResumesHeldStart(bool cancelled)
    {
        await using var rig = new Rig();
        Assert.True(rig.Policy.TryBegin(2));
        await rig.Host.StartAsync(rig.Room, 1);
        Action failMutation = () =>
        {
            try
            {
                if (cancelled) throw new OperationCanceledException();
                throw new IOException("publication failed");
            }
            finally { rig.Policy.End(2); }
        };
        var error = Record.Exception(failMutation);
        Assert.NotNull(error);
        await rig.Host.ResumeFinished.Task.WaitAsync(Limit);
        Assert.Equal(1, rig.Host.StartedCount);
    }

    /// <summary>Departure, host transfer and closure discard the old request while its mutation remains owned by the caller.</summary>
    [Theory]
    [InlineData("departure")]
    [InlineData("host-departure")]
    [InlineData("host")]
    [InlineData("closed")]
    public async Task RoomLifecycleDropsHeldRequestWithoutEndingMutation(string change)
    {
        var rig = new Rig();
        Assert.True(rig.Policy.TryBegin(2));
        await rig.Host.StartAsync(rig.Room, 1);
        if (change == "departure")
            await rig.Policy.OnMemberLeftAsync(rig.Room with { Seats = [rig.Room.Seats[0]] }, new Session(2), default);
        else if (change == "host-departure")
            await rig.Policy.OnMemberLeftAsync(rig.Room with { Seats = [rig.Room.Seats[1] with { Host = true }] }, new Session(1), default);
        else if (change == "host")
            await rig.Policy.OnHostChangedAsync(rig.Room with
            {
                Seats = rig.Room.Seats.Select(seat => seat with { Host = seat.SessionId == 2 }).ToArray(),
            }, default);
        else rig.Policy.OnRoomClosed(rig.Room.Instance);
        Assert.False(rig.Policy.TryBegin(2));
        Assert.Equal(RoomStartDecision.Hold, rig.Policy.EvaluateStart(rig.Room, 1));
        rig.Policy.End(2);
        await rig.DisposeAsync();
        Assert.Equal(0, rig.Host.ResumeCount);
    }

    /// <summary>A second policy may retain the start without causing this policy to replay it in a loop.</summary>
    [Fact]
    public async Task AnotherPolicyHoldingAfterPetReleaseDoesNotLoop()
    {
        var rig = new Rig();
        using var registration = rig.Host.Register(new OtherHoldPolicy());
        Assert.True(rig.Policy.TryBegin(2));
        await rig.Host.StartAsync(rig.Room, 1);
        rig.Policy.End(2);
        await rig.Host.ResumeFinished.Task.WaitAsync(Limit);
        for (var index = 0; index < 20; index++) rig.Policy.End(99);
        await rig.DisposeAsync();
        Assert.Equal(1, rig.Host.ResumeCount);
        Assert.Equal(0, rig.Host.StartedCount);
        Assert.Equal(1, rig.Notices);
    }

    /// <summary>A stale host replay is consumed once and never retried by repeated wake signals.</summary>
    [Fact]
    public async Task FalseResumeResultDropsExactRecord()
    {
        var rig = new Rig();
        rig.Host.ResumeOverride = (_, _, _) => ValueTask.FromResult(false);
        Assert.True(rig.Policy.TryBegin(2));
        await rig.Host.StartAsync(rig.Room, 1);
        rig.Policy.End(2);
        await rig.Host.ResumeFinished.Task.WaitAsync(Limit);
        rig.Policy.End(2);
        await rig.DisposeAsync();
        Assert.Equal(1, rig.Host.ResumeCount);
    }

    /// <summary>The resume worker does not inherit execution context from construction, callbacks or the releasing request.</summary>
    [Fact]
    public async Task WorkerRunsOutsideInheritedExecutorContext()
    {
        ExecutorContext.Value = "room callback";
        try
        {
            await using var rig = new Rig();
            rig.Host.ResumeOverride = (_, _, _) =>
            {
                Assert.Null(ExecutorContext.Value);
                return ValueTask.FromResult(false);
            };
            Assert.True(rig.Policy.TryBegin(2));
            await rig.Host.StartAsync(rig.Room, 1);
            rig.Policy.End(2);
            await rig.Host.ResumeFinished.Task.WaitAsync(Limit);
        }
        finally { ExecutorContext.Value = null; }
    }

    /// <summary>In-flight capacity refuses excess requests without disturbing the accepted mutation.</summary>
    [Fact]
    public async Task InFlightCapacityIsBoundedAndReusable()
    {
        await using var rig = new Rig(maxInFlight: 1);
        Assert.True(rig.Policy.TryBegin(2));
        Assert.False(rig.Policy.TryBegin(1));
        rig.Policy.End(2);
        Assert.True(rig.Policy.TryBegin(1));
        rig.Policy.End(1);
    }

    /// <summary>One completed publication cannot release a start while a second seated mutation remains in flight.</summary>
    [Fact]
    public async Task EverySeatedMutationMustFinishBeforeReplay()
    {
        await using var rig = new Rig();
        Assert.True(rig.Policy.TryBegin(1));
        Assert.True(rig.Policy.TryBegin(2));
        await rig.Host.StartAsync(rig.Room, 1);
        rig.Policy.End(2);
        Assert.Equal(RoomStartDecision.Hold, rig.Policy.EvaluateStart(rig.Room, 1));
        Assert.Equal(0, rig.Host.ResumeCount);
        rig.Policy.End(1);
        await rig.Host.ResumeFinished.Task.WaitAsync(Limit);
        Assert.Equal(1, rig.Host.StartedCount);
    }

    /// <summary>The worker is never created with an unbounded or disabled tracking capacity.</summary>
    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    public void InvalidBoundsFailBeforeWorkerCreation(int inFlight, int held)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PetRoomMutationPolicy(new Host(), inFlight, held,
            (_, _, _) => ValueTask.CompletedTask));
    }

    /// <summary>A full held-room map fails explicitly and accepts another room after lifecycle cleanup releases its slot.</summary>
    [Fact]
    public async Task HeldRoomCapacityFailsLoudAndCanBeReused()
    {
        await using var rig = new Rig(maxHeld: 1);
        var other = Room(3, 4);
        rig.Host.Add(other);
        Assert.True(rig.Policy.TryBegin(2));
        Assert.True(rig.Policy.TryBegin(4));
        await rig.Host.StartAsync(rig.Room, 1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Host.StartAsync(other, 3).AsTask());
        rig.Policy.OnRoomClosed(rig.Room.Instance);
        await rig.Host.StartAsync(other, 3);
        rig.Policy.End(4);
        await rig.Host.ResumeFinished.Task.WaitAsync(Limit);
        rig.Policy.End(2);
        Assert.Equal(2, rig.Notices);
    }

    /// <summary>Unload cancels an outstanding replay, awaits its completion, and refuses later admission.</summary>
    [Fact]
    public async Task DisposalCancelsAndAwaitsWorker()
    {
        var rig = new Rig();
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Host.ResumeOverride = async (_, _, ct) =>
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            finally { cancelled.TrySetResult(); }
            return true;
        };
        Assert.True(rig.Policy.TryBegin(2));
        await rig.Host.StartAsync(rig.Room, 1);
        rig.Policy.End(2);
        await rig.Host.ResumeEntered.Task.WaitAsync(Limit);
        await rig.DisposeAsync().AsTask().WaitAsync(Limit);
        Assert.True(cancelled.Task.IsCompleted);
        Assert.True(rig.Policy.Completion.IsCompletedSuccessfully);
        Assert.Throws<ObjectDisposedException>(() => rig.Policy.TryBegin(2));
        rig.Policy.End(2);
        await rig.DisposeAsync();
    }

    /// <summary>A worker fault is observable immediately and prevents later admission from depending on a dead worker.</summary>
    [Fact]
    public async Task ResumeFailureIsObservedAndFailsClosed()
    {
        var rig = new Rig();
        rig.Host.ResumeOverride = (_, _, _) => ValueTask.FromException<bool>(new IOException("resume failed"));
        Assert.True(rig.Policy.TryBegin(2));
        await rig.Host.StartAsync(rig.Room, 1);
        rig.Policy.End(2);
        await Assert.ThrowsAsync<IOException>(() => rig.Policy.Completion.WaitAsync(Limit));
        Assert.Throws<InvalidOperationException>(() => rig.Policy.TryBegin(2));
        await Assert.ThrowsAsync<IOException>(() => rig.DisposeAsync().AsTask());
    }

    /// <summary>Cancellation before the held callback does not publish a notice or register abandoned replay work.</summary>
    [Fact]
    public async Task CancelledHeldCallbackDoesNotAllocateRecord()
    {
        var rig = new Rig();
        Assert.True(rig.Policy.TryBegin(2));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            rig.Policy.OnStartHeldAsync(rig.Room, 1, new CancellationToken(true)).AsTask());
        rig.Policy.End(2);
        await rig.DisposeAsync();
        Assert.Equal(0, rig.Notices);
        Assert.Equal(0, rig.Host.ResumeCount);
    }

    private static RoomPolicyRoom Room(long host = 1, long member = 2) => new(Guid.NewGuid(), checked((int)host), 4, false, true,
    [
        new(host, 0, checked((int)host), "host", 300001, [], true),
        new(member, 1, checked((int)member), "member", 300001, [], false),
    ]);

    private sealed class Rig : IAsyncDisposable
    {
        private readonly IDisposable _registration;
        private int _notices;
        internal Rig(int maxInFlight = 4, int maxHeld = 4,
            Func<RoomPolicyRoom, long, CancellationToken, ValueTask>? notice = null)
        {
            Host.Add(Room);
            Policy = new(Host, maxInFlight, maxHeld, async (room, requester, ct) =>
            {
                Interlocked.Increment(ref _notices);
                if (notice is not null) await notice(room, requester, ct);
            });
            _registration = Host.Register(Policy);
        }
        internal RoomPolicyRoom Room { get; } = PetRoomMutationPolicyTests.Room();
        internal Host Host { get; } = new();
        internal PetRoomMutationPolicy Policy { get; }
        internal int Notices => Volatile.Read(ref _notices);
        /// <summary>Unregisters callbacks before awaiting the policy's owned worker.</summary>
        public async ValueTask DisposeAsync() { _registration.Dispose(); await Policy.DisposeAsync(); }
    }

    private sealed class Host : IRoomPolicyHost
    {
        private readonly object _gate = new();
        private readonly Dictionary<Guid, RoomPolicyRoom> _rooms = [];
        private readonly Dictionary<Guid, (long Requester, bool Automatic)> _held = [];
        private readonly List<IRoomPolicy> _policies = [];
        private int _resumes;
        private int _started;
        internal Func<long, RoomPolicyRoom?>? FindOverride { get; set; }
        internal Func<Guid, long, CancellationToken, ValueTask<bool>>? ResumeOverride { get; set; }
        internal TaskCompletionSource ResumeEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ResumeFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int ResumeCount => Volatile.Read(ref _resumes);
        internal int StartedCount => Volatile.Read(ref _started);
        /// <inheritdoc />
        public string Protocol => "kr-1256/game";
        /// <inheritdoc />
        public string ClientBuild => "kr-1256";
        internal void Add(RoomPolicyRoom room) { lock (_gate) _rooms[room.Instance] = room; }

        /// <inheritdoc />
        public IDisposable Register(IRoomPolicy policy)
        {
            lock (_gate) _policies.Add(policy);
            return new Registration(() => { lock (_gate) _policies.Remove(policy); });
        }

        /// <inheritdoc />
        public RoomPolicyRoom? Find(long sessionId)
        {
            if (FindOverride is { } find) return find(sessionId);
            lock (_gate) return _rooms.Values.FirstOrDefault(room => room.Seats.Any(seat => seat.SessionId == sessionId));
        }

        /// <inheritdoc />
        public ISession? Session(long sessionId) => Find(sessionId) is null ? null : new PetRoomMutationPolicyTests.Session(sessionId);

        internal async ValueTask<bool> StartAsync(RoomPolicyRoom room, long requester, bool automatic = false,
            Action? beforeNotice = null, CancellationToken ct = default)
        {
            IRoomPolicy[] policies;
            lock (_gate) policies = _policies.ToArray();
            var request = room with { AutomaticStart = automatic };
            var holders = policies.Where(policy => policy.EvaluateStart(request, requester) == RoomStartDecision.Hold).ToArray();
            if (holders.Length == 0)
            {
                Interlocked.Increment(ref _started);
                return false;
            }
            lock (_gate) _held[room.Instance] = (requester, automatic);
            beforeNotice?.Invoke();
            foreach (var policy in holders) await policy.OnStartHeldAsync(request, requester, ct);
            return true;
        }

        /// <inheritdoc />
        public async ValueTask<bool> ResumeHeldStartAsync(Guid instance, long requesterSessionId, CancellationToken ct)
        {
            Interlocked.Increment(ref _resumes);
            ResumeEntered.TrySetResult();
            try
            {
                if (ResumeCount > 1) throw new InvalidOperationException("A consumed held start was replayed more than once.");
                if (ResumeOverride is { } resume) return await resume(instance, requesterSessionId, ct);
                RoomPolicyRoom room;
                (long Requester, bool Automatic) held;
                lock (_gate)
                {
                    if (!_rooms.TryGetValue(instance, out room!) || !_held.TryGetValue(instance, out held)
                        || held.Requester != requesterSessionId) return false;
                }
                await StartAsync(room, requesterSessionId, held.Automatic, ct: ct);
                return true;
            }
            finally { ResumeFinished.TrySetResult(); }
        }
    }

    private sealed class OtherHoldPolicy : IRoomPolicy
    {
        /// <inheritdoc />
        public RoomStartDecision EvaluateStart(RoomPolicyRoom room, long requesterSessionId) => RoomStartDecision.Hold;
        /// <inheritdoc />
        public ValueTask OnStartHeldAsync(RoomPolicyRoom room, long requesterSessionId, CancellationToken ct) => ValueTask.CompletedTask;
        /// <inheritdoc />
        public ValueTask OnMemberEnteredAsync(RoomPolicyRoom room, long sessionId, CancellationToken ct) => ValueTask.CompletedTask;
        /// <inheritdoc />
        public ValueTask OnAppearanceChangedAsync(RoomPolicyRoom room, long sessionId, CancellationToken ct) => ValueTask.CompletedTask;
        /// <inheritdoc />
        public void OnRoomClosed(Guid instance) { }
    }

    private sealed class Registration(Action remove) : IDisposable
    {
        private Action? _remove = remove;
        /// <summary>Removes the test registration at most once.</summary>
        public void Dispose() => Interlocked.Exchange(ref _remove, null)?.Invoke();
    }

    private sealed class Session(long id) : ISession
    {
        /// <inheritdoc />
        public long Id => id;
        /// <inheritdoc />
        public IConnection Connection => throw new NotSupportedException();
        /// <inheritdoc />
        public ClientProfile Profile { get; } = new("kr-1256", "kr", 1256, 0, 0, "test");
        /// <inheritdoc />
        public ConcurrentDictionary<string, object> Items { get; } = new();
        /// <inheritdoc />
        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct) => throw new NotSupportedException();
    }
}
