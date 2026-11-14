using System.Threading.Channels;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Rooms;

namespace BubbleFighter.Fantasy.Pets;

/// <summary>Holds room admission until each accepted pet mutation has finished its inventory and appearance publications.</summary>
internal sealed class PetRoomMutationPolicy : IRoomPolicy, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly IRoomPolicyHost _rooms;
    private readonly int _maxInFlightSessions;
    private readonly int _maxHeldRooms;
    private readonly Func<RoomPolicyRoom, long, CancellationToken, ValueTask> _notice;
    private readonly HashSet<long> _inFlight = [];
    private readonly Dictionary<Guid, HeldStart> _held = [];
    private readonly Channel<byte> _wake = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
    {
        SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.DropWrite,
        AllowSynchronousContinuations = false,
    });
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _worker;
    private HeldStart? _resuming;
    private Exception? _failure;
    private bool _closed;
    private Task? _shutdown;

    /// <summary>Creates one bounded admission policy and an owned resume worker that never inherits a room executor's context.</summary>
    internal PetRoomMutationPolicy(IRoomPolicyHost rooms, int maxInFlightSessions, int maxHeldRooms,
        Func<RoomPolicyRoom, long, CancellationToken, ValueTask> notice)
    {
        ArgumentNullException.ThrowIfNull(rooms);
        ArgumentNullException.ThrowIfNull(notice);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxInFlightSessions);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxHeldRooms);
        _rooms = rooms;
        _maxInFlightSessions = maxInFlightSessions;
        _maxHeldRooms = maxHeldRooms;
        _notice = notice;
        _worker = Detached(ResumeAsync);
    }

    /// <summary>Exposes the owned worker's completion so the row may also observe a failed resume before unloading.</summary>
    internal Task Completion => _worker;

    /// <summary>Marks a session before inspecting its room; duplicate, capacity-limited and already-started mutations are refused.</summary>
    internal bool TryBegin(long sessionId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sessionId);
        lock (_gate)
        {
            RequireOpen();
            if (_inFlight.Contains(sessionId) || _inFlight.Count >= _maxInFlightSessions) return false;
            _inFlight.Add(sessionId);
        }
        try
        {
            // Publish the mark before Find; never hold our gate while Find takes the room gate used by EvaluateStart.
            var room = _rooms.Find(sessionId);
            if (room is { Waiting: false })
            {
                End(sessionId);
                return false;
            }
            lock (_gate) RequireOpen();
            return true;
        }
        catch
        {
            End(sessionId);
            throw;
        }
    }

    /// <summary>Releases a mutation only after its caller's complete publication or failure cleanup, then wakes held-start processing.</summary>
    internal void End(long sessionId)
    {
        lock (_gate) _inFlight.Remove(sessionId);
        Signal();
    }

    /// <summary>Holds every kind of start while a seated session has a mutation in flight, without entering the room host.</summary>
    public RoomStartDecision EvaluateStart(RoomPolicyRoom room, long requesterSessionId)
    {
        lock (_gate)
        {
            RequireOpen();
            return room.Seats.Any(seat => _inFlight.Contains(seat.SessionId))
                ? RoomStartDecision.Hold : RoomStartDecision.Proceed;
        }
    }

    /// <summary>Remembers the exact held start and announces it once, including when its last mutation already finished.</summary>
    public async ValueTask OnStartHeldAsync(RoomPolicyRoom room, long requesterSessionId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        HeldStart held;
        lock (_gate)
        {
            RequireOpen();
            var host = room.Seats.Single(seat => seat.Host).SessionId;
            if (_held.TryGetValue(room.Instance, out var existing)
                && existing.Requester == requesterSessionId && existing.Host == host) return;
            if (!_held.ContainsKey(room.Instance) && _held.Count >= _maxHeldRooms)
                throw new InvalidOperationException("The pet mutation policy's held-room capacity is exhausted.");
            held = new(room.Instance, requesterSessionId, host, room.Seats.Select(seat => seat.SessionId).ToArray());
            _held[room.Instance] = held;
        }
        try { await _notice(room, requesterSessionId, ct).ConfigureAwait(false); }
        finally
        {
            lock (_gate) held.Announcing = false;
            // End may precede this callback or its notice; both orders must leave a wake for the recorded hold.
            Signal();
        }
    }

    /// <summary>Leaves mutation ownership with the request even when its session enters a room.</summary>
    public ValueTask OnMemberEnteredAsync(RoomPolicyRoom room, long sessionId, CancellationToken ct) => ValueTask.CompletedTask;

    /// <summary>Keeps the admission hold across the appearance callback until all later publications have finished.</summary>
    public ValueTask OnAppearanceChangedAsync(RoomPolicyRoom room, long sessionId, CancellationToken ct) => ValueTask.CompletedTask;

    /// <summary>Drops obsolete room holds after a departure without releasing the departing request's mutation mark.</summary>
    public ValueTask OnMemberLeftAsync(RoomPolicyRoom? room, ISession session, CancellationToken ct)
    {
        Drop(held => held.Instance == room?.Instance || held.Seats.Contains(session.Id));
        return ValueTask.CompletedTask;
    }

    /// <summary>Invalidates starts captured for a previous host without transferring their requester authority.</summary>
    public ValueTask OnHostChangedAsync(RoomPolicyRoom room, CancellationToken ct)
    {
        OnRoomClosed(room.Instance);
        return ValueTask.CompletedTask;
    }

    /// <summary>Releases held-start bookkeeping for a closed room instance.</summary>
    public void OnRoomClosed(Guid instance) => Drop(held => held.Instance == instance);

    /// <summary>Stops accepting mutations, cancels an outstanding resume and awaits the sole worker before releasing its resources.</summary>
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_shutdown is not null) return new(_shutdown);
            _closed = true;
            _held.Clear();
            _inFlight.Clear();
            _wake.Writer.TryComplete();
            _shutdown = Detached(StopAsync);
            return new(_shutdown);
        }
    }

    private async Task StopAsync()
    {
        try
        {
            try { await _stopping.CancelAsync().ConfigureAwait(false); }
            finally { await _worker.ConfigureAwait(false); }
        }
        finally { _stopping.Dispose(); }
    }

    private async Task ResumeAsync()
    {
        try
        {
            while (await _wake.Reader.WaitToReadAsync(_stopping.Token).ConfigureAwait(false))
            {
                while (_wake.Reader.TryRead(out _)) { }
                while (TakeReady() is { } held)
                {
                    try
                    {
                        _stopping.Token.ThrowIfCancellationRequested();
                        var room = _rooms.Find(held.Requester);
                        if (room is null || room.Instance != held.Instance || !room.Waiting
                            || !room.Seats.Any(seat => seat.SessionId == held.Host && seat.Host)) continue;
                        lock (_gate)
                            if (_closed || held.Invalidated) continue;
                        // Consume before replay: a different policy may still hold without notifying this policy again.
                        await _rooms.ResumeHeldStartAsync(held.Instance, held.Requester, _stopping.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        lock (_gate) _resuming = null;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
        catch (Exception exception)
        {
            lock (_gate) _failure = exception;
            throw;
        }
    }

    private HeldStart? TakeReady()
    {
        lock (_gate)
        {
            if (_closed) return null;
            var held = _held.Values.FirstOrDefault(candidate => !candidate.Announcing
                && !candidate.Seats.Any(_inFlight.Contains));
            if (held is null) return null;
            _held.Remove(held.Instance);
            _resuming = held;
            return held;
        }
    }

    private void Drop(Func<HeldStart, bool> matches)
    {
        lock (_gate)
        {
            foreach (var held in _held.Values.Where(matches).ToArray()) _held.Remove(held.Instance);
            if (_resuming is { } current && matches(current)) current.Invalidated = true;
        }
    }

    private void Signal() => _wake.Writer.TryWrite(0);

    private void RequireOpen()
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        if (_failure is { } failure)
            throw new InvalidOperationException("The pet mutation policy's resume worker failed.", failure);
    }

    private static Task Detached(Func<Task> action)
    {
        if (ExecutionContext.IsFlowSuppressed()) return Task.Run(action);
        using (ExecutionContext.SuppressFlow()) return Task.Run(action);
    }

    private sealed class HeldStart(Guid instance, long requester, long host, long[] seats)
    {
        internal Guid Instance { get; } = instance;
        internal long Requester { get; } = requester;
        internal long Host { get; } = host;
        internal long[] Seats { get; } = seats;
        internal bool Announcing { get; set; } = true;
        internal bool Invalidated { get; set; }
    }
}
