using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.InGame.Sessions;

namespace BubbleFighter.Fantasy.InGame.Handlers;

/// <summary>
/// The room row's side of <see cref="IRoomPolicy"/>: it copies room state for the policies under the room's lock and
/// replays a start they held. With no policy registered every call is a no-op, so a profile without one is unchanged.
/// </summary>
internal sealed class RoomPolicyHost(RoomRegistry registry, string protocol, string clientBuild) : IRoomPolicyHost
{
    private readonly Lock _gate = new();
    private IRoomPolicy[] _policies = [];

    /// <summary>Gets or sets the start the room row runs for a held start's replay.</summary>
    internal Func<PlayRoom, ISession, RoomStartRequested, bool, CancellationToken, ValueTask>? Start { get; set; }

    public string Protocol => protocol;

    public string ClientBuild => clientBuild;

    internal bool Active => Volatile.Read(ref _policies).Length > 0;

    public IDisposable Register(IRoomPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        lock (_gate)
        {
            if (_policies.Contains(policy)) throw new InvalidOperationException("The room policy is already registered.");
            Volatile.Write(ref _policies, [.. _policies, policy]);
        }
        return new Registration(() =>
        {
            lock (_gate) Volatile.Write(ref _policies, [.. _policies.Where(current => !ReferenceEquals(current, policy))]);
        });
    }

    public RoomPolicyRoom? Find(long sessionId)
    {
        if (registry.TryGet(sessionId) is not { } room) return null;
        lock (room.Gate)
            return room.FindByGame(sessionId) is null || room.Phase == RoomPhase.Closed ? null : Snapshot(room);
    }

    public ISession? Session(long sessionId)
    {
        if (registry.TryGet(sessionId) is not { } room) return null;
        lock (room.Gate) return room.FindByGame(sessionId)?.Game;
    }

    public async ValueTask<bool> ResumeHeldStartAsync(Guid instance, long requesterSessionId, CancellationToken ct)
    {
        if (Start is not { } start || registry.TryGet(requesterSessionId) is not { } room || room.InstanceId != instance)
            return false;
        var resumed = false;
        await room.Executor.ExecuteAsync(async operationCt =>
        {
            PlayRoom.HeldRoomStart held;
            ISession session;
            lock (room.Gate)
            {
                // Only the start that was held, by the same seat of the same room, may be repeated.
                if (room.HeldStart is not { } current || current.SessionId != requesterSessionId || room.Phase != RoomPhase.Waiting
                    || room.FindByGame(requesterSessionId) is not { } seat)
                    return;
                held = current;
                session = seat.Game;
            }
            resumed = true;
            await start(room, session, held.Request, held.AllowMemberStart, operationCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        return resumed;
    }

    // Under the room's gate. Proceeds unless a policy holds the start.
    // Under the room's gate. Every policy is asked, so each one sees every start and only the holders explain it.
    // The caller leaves waiting in the same locked section when this returns null, so the outcome each policy is told is
    // final: one-shot consent is consumed only by a start that really proceeds.
    // roomHeld is the room's own hold (an open equipment write): the policies still answer, so a host's consent is
    // recorded, but they learn the start does not proceed and keep their one-shot state for the resumed start.
    internal HeldStartPolicies? EvaluateStart(PlayRoom room, long requesterSessionId, bool automaticStart, bool resumedStart,
        bool roomHeld = false)
    {
        var policies = Volatile.Read(ref _policies);
        if (policies.Length == 0) return null;
        var snapshot = Snapshot(room) with { AutomaticStart = automaticStart, ResumedStart = resumedStart };
        var holders = policies.Where(policy => policy.EvaluateStart(snapshot, requesterSessionId) == RoomStartDecision.Hold).ToArray();
        foreach (var policy in policies)
            policy.OnStartDecided(snapshot, requesterSessionId, proceeding: holders.Length == 0 && !roomHeld);
        return holders.Length == 0 ? null : new(snapshot, holders);
    }

    internal static async ValueTask StartHeldAsync(HeldStartPolicies held, long requesterSessionId, CancellationToken ct)
    {
        foreach (var policy in held.Holders)
            await policy.OnStartHeldAsync(held.Room, requesterSessionId, ct).ConfigureAwait(false);
    }

    internal async ValueTask MemberEnteredAsync(PlayRoom room, long sessionId, CancellationToken ct)
    {
        var policies = Volatile.Read(ref _policies);
        if (policies.Length == 0) return;
        RoomPolicyRoom snapshot;
        lock (room.Gate)
        {
            if (room.FindByGame(sessionId) is null) return;
            snapshot = Snapshot(room);
        }
        foreach (var policy in policies)
            await policy.OnMemberEnteredAsync(snapshot, sessionId, ct).ConfigureAwait(false);
    }

    internal async ValueTask AppearanceChangedAsync(PlayRoom room, long sessionId, CancellationToken ct)
    {
        var policies = Volatile.Read(ref _policies);
        if (policies.Length == 0) return;
        RoomPolicyRoom snapshot;
        lock (room.Gate)
        {
            if (room.FindByGame(sessionId) is null) return;
            snapshot = Snapshot(room);
        }
        foreach (var policy in policies)
            await policy.OnAppearanceChangedAsync(snapshot, sessionId, ct).ConfigureAwait(false);
    }

    internal async ValueTask MemberLeftAsync(PlayRoom room, ISession session, CancellationToken ct)
    {
        var policies = Volatile.Read(ref _policies);
        if (policies.Length == 0) return;
        RoomPolicyRoom? snapshot;
        lock (room.Gate)
            snapshot = room.Seats.Count == 0 || room.Phase == RoomPhase.Closed ? null : Snapshot(room);
        foreach (var policy in policies)
            await policy.OnMemberLeftAsync(snapshot, session, ct).ConfigureAwait(false);
    }

    internal async ValueTask WaitingAgainAsync(PlayRoom room, CancellationToken ct)
    {
        var policies = Volatile.Read(ref _policies);
        if (policies.Length == 0) return;
        RoomPolicyRoom snapshot;
        lock (room.Gate)
        {
            if (room.Seats.Count == 0 || room.Phase != RoomPhase.Waiting) return;
            snapshot = Snapshot(room);
        }
        foreach (var policy in policies)
            await policy.OnWaitingAgainAsync(snapshot, ct).ConfigureAwait(false);
    }

    internal async ValueTask HostChangedAsync(PlayRoom room, CancellationToken ct)
    {
        var policies = Volatile.Read(ref _policies);
        if (policies.Length == 0) return;
        RoomPolicyRoom snapshot;
        lock (room.Gate)
        {
            if (room.Seats.Count == 0 || room.Phase == RoomPhase.Closed) return;
            snapshot = Snapshot(room);
        }
        foreach (var policy in policies)
            await policy.OnHostChangedAsync(snapshot, ct).ConfigureAwait(false);
    }

    internal void Closed(PlayRoom room)
    {
        foreach (var policy in Volatile.Read(ref _policies))
            policy.OnRoomClosed(room.InstanceId);
    }

    // Under the room's gate.
    private static RoomPolicyRoom Snapshot(PlayRoom room) => new(room.InstanceId, room.Id, room.ModeId, room.Password.Length > 0,
        room.Phase == RoomPhase.Waiting,
        [.. room.Seats.Select(seat => new RoomPolicySeat(seat.Game.Id, seat.Slot, seat.UserNum, seat.Nickname, seat.CharacterId,
            seat.EquippedItemIds?.ToArray() ?? [], seat.Slot == room.MasterSlot))]);

    /// <summary>A held start: the room as the policies saw it and the policies that held it.</summary>
    internal sealed record HeldStartPolicies(RoomPolicyRoom Room, IReadOnlyList<IRoomPolicy> Holders);

    private sealed class Registration(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
