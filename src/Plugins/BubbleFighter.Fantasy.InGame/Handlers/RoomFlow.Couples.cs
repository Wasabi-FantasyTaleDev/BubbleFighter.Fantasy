using System.Runtime.CompilerServices;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Couples;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.InGame.Sessions;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.InGame.Handlers;

internal sealed partial class RoomFlow
{
    private readonly ConditionalWeakTable<ISession, CoupleRoster> _coupleRosters = new();
    private readonly object _coupleGate = new();
    private readonly Dictionary<ISession, CoupleSceneChanged> _couplePending = new(ReferenceEqualityComparer.Instance);
    private Task _coupleWorker = Task.CompletedTask;
    private CancellationTokenSource? _coupleCancellation;
    private Task? _coupleStopTask;
    private bool _coupleRunning;
    private bool _coupleStopping;
    private IMessageSender<RoomCoupleUpdated>? _roomCouple;
    private IMessageSender<PlazaCoupleUpdated>? _plazaCouple;

    internal bool CoupleScenesBound => _roomCouple is not null || _plazaCouple is not null;

    private static CoupleSceneState? SceneCouple(ISession session) =>
        session.TryFeature<ICoupleProfile>(out var profile) ? profile.Scene : null;

    /// <summary>Posts a relationship change without reentering an executor from an event raised inside room work.</summary>
    internal void OnCoupleSceneChanged(CoupleSceneChanged change)
    {
        if (!CoupleScenesBound || CurrentCoupleScene(change.Session) is null) return;
        lock (_coupleGate)
        {
            if (_coupleStopping || _lifetime.IsCancellationRequested) return;
            // One pending state per currently seated connection. Churn cannot retain departed sessions.
            foreach (var session in _couplePending.Keys.Where(session => CurrentCoupleScene(session) is null).ToArray())
                _couplePending.Remove(session);
            _couplePending[change.Session] = change;
            if (_coupleRunning) return;
            _coupleCancellation ??= CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
            _coupleRunning = true;
            if (ExecutionContext.IsFlowSuppressed()) _coupleWorker = Task.Run(PublishPendingCouplesAsync);
            else
            {
                using (ExecutionContext.SuppressFlow())
                    _coupleWorker = Task.Run(PublishPendingCouplesAsync);
            }
        }
    }

    private async Task PublishPendingCouplesAsync()
    {
        var cancellationToken = _coupleCancellation!.Token;
        while (true)
        {
            CoupleSceneChanged change;
            lock (_coupleGate)
            {
                if (_coupleStopping || _couplePending.Count == 0)
                {
                    _coupleRunning = false;
                    return;
                }
                var entry = _couplePending.First();
                change = entry.Value;
                _couplePending.Remove(entry.Key);
            }
            try { await PublishCoupleSceneAsync(change, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (ObjectDisposedException) { }
            catch (Exception error)
            {
                _logger.LogError(error, "Could not publish the couple scene state for session {Session}.", change.Session.Id);
            }
        }
    }

    /// <summary>Waits for already posted scene publications; callers must be outside room and plaza executors.</summary>
    internal Task FlushCoupleChangesAsync()
    {
        lock (_coupleGate) return _coupleWorker;
    }

    internal Task StopCoupleChangesAsync()
    {
        lock (_coupleGate)
        {
            if (_coupleStopTask is not null) return _coupleStopTask;
            _coupleStopping = true;
            _couplePending.Clear();
            return _coupleStopTask = StopCoupleWorkerAsync(_coupleCancellation, _coupleWorker);
        }
    }

    private static async Task StopCoupleWorkerAsync(CancellationTokenSource? cancellation, Task worker)
    {
        try
        {
            if (cancellation is not null) await cancellation.CancelAsync().ConfigureAwait(false);
        }
        finally
        {
            try { await worker.ConfigureAwait(false); }
            finally { cancellation?.Dispose(); }
        }
    }

    private object? CurrentCoupleScene(ISession session)
    {
        if (session.Connection.IsClosed) return null;
        if (_online is not null && !ReferenceEquals(_online.FindByUserNum(SessionPlayer.ResolveUserNum(session)), session))
            return null;
        if (_registry.TryGame(session) is { } current && ReferenceEquals(current.Seat.Game, session))
            return current.Room;
        return _plaza.Find(session.Id) is { Reserved: false } member && ReferenceEquals(member.Game, session)
            ? _plaza : null;
    }

    private async ValueTask PublishCoupleSceneAsync(CoupleSceneChanged change, CancellationToken ct)
    {
        var session = change.Session;
        if (CurrentCoupleScene(session) is PlayRoom room && _roomCouple is { } roomSender)
        {
            // This single publication worker bounds admission waiters and retains the event while the queue is full.
            await room.Executor.ExecuteWhenAvailableAsync(async token =>
            {
                if (!ReferenceEquals(CurrentCoupleScene(session), room) || SceneCouple(session) != change.Couple) return;
                var target = room.FindByGame(session.Id);
                if (target is null || !ReferenceEquals(target.Game, session)) return;
                var update = new RoomCoupleUpdated(View(target));
                RoomSeat[] recipients;
                lock (room.Gate) recipients = [.. room.Seats];
                foreach (var recipient in recipients)
                    if (ReferenceEquals(CurrentCoupleScene(recipient.Game), room)
                        && HasCoupleTarget(recipient.Game, room, target.UserNum))
                        await NotifyScenePeerAsync(recipient.Game, roomSender, update, token).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
            return;
        }
        if (_plazaCouple is null || !ReferenceEquals(CurrentCoupleScene(session), _plaza)) return;
        await _plaza.Executor.ExecuteWhenAvailableAsync(async token =>
        {
            if (!ReferenceEquals(CurrentCoupleScene(session), _plaza) || SceneCouple(session) != change.Couple) return;
            var target = _plaza.Find(session.Id);
            if (target is null || !ReferenceEquals(target.Game, session) || target.Couple == change.Couple) return;
            target.Couple = change.Couple;
            await PublishPlazaCoupleAsync(target, token).ConfigureAwait(false);
            PublishPlaza(new(PlazaFederationEventKind.Entered, target.UserNumber, target.ToMember(), default));
        }, ct).ConfigureAwait(false);
    }

    private async ValueTask PublishPlazaCoupleAsync(PlazaMemberState target, CancellationToken ct)
    {
        if (_plazaCouple is not { } sender) return;
        var update = new PlazaCoupleUpdated(target.ToMember());
        foreach (var recipient in _plaza.Sessions())
            if (recipient.Game is { } game && ReferenceEquals(CurrentCoupleScene(game), _plaza)
                && HasCoupleTarget(game, _plaza, target.UserNumber))
                await NotifyScenePeerAsync(game, sender, update, ct).ConfigureAwait(false);
    }

    private bool HasCoupleTarget(ISession session, object scene, int userNumber)
    {
        if (!_coupleRosters.TryGetValue(session, out var roster)) return false;
        lock (roster) return ReferenceEquals(roster.Scene, scene) && roster.Users.Contains(userNumber);
    }

    private void ForgetCoupleRoster(ISession session, object scene)
    {
        if (!_coupleRosters.TryGetValue(session, out var roster)) return;
        lock (roster)
            if (ReferenceEquals(roster.Scene, scene))
            {
                roster.Scene = null;
                roster.Users.Clear();
            }
    }

    private IMessageSender<T>? TrackCoupleRoster<T>(IMessageSender<T>? sender) where T : IMessage =>
        sender is null || !CoupleScenesBound ? sender : new CoupleRosterSender<T>(sender, this);

    private void InstalledCoupleRoster<T>(ISession session, T message) where T : IMessage
    {
        var scene = CurrentCoupleScene(session);
        if (scene is null) return;
        var roster = _coupleRosters.GetValue(session, static _ => new());
        lock (roster)
        {
            switch (message)
            {
                case RoomEntered roomEntered when scene is PlayRoom enteredRoom && enteredRoom.Id == roomEntered.Room.RoomId:
                    roster.Scene = enteredRoom;
                    roster.Users = roomEntered.Room.Seats.Select(member => member.UserNum).ToHashSet();
                    break;
                case PlazaEntered plazaEntered when ReferenceEquals(scene, _plaza):
                    roster.Scene = _plaza;
                    roster.Users = plazaEntered.Members.Select(member => member.UserNumber).Append(plazaEntered.You.UserNumber).ToHashSet();
                    break;
                case RoomSeatUpdated roomUpdated when scene is PlayRoom updatedRoom && updatedRoom.Id == roomUpdated.RoomId
                    && ReferenceEquals(roster.Scene, updatedRoom):
                    roster.Users.Add(roomUpdated.Seat.UserNum);
                    break;
                case RoomSeatVacated roomLeft when scene is PlayRoom leftRoom && leftRoom.Id == roomLeft.RoomId
                    && ReferenceEquals(roster.Scene, leftRoom):
                    roster.Users.Remove(roomLeft.Seat.UserNum);
                    break;
                case PlazaMemberEntered plazaMember when ReferenceEquals(scene, _plaza) && ReferenceEquals(roster.Scene, _plaza):
                    roster.Users.Add(plazaMember.Member.UserNumber);
                    break;
                case PlazaMemberLeft plazaLeft when ReferenceEquals(scene, _plaza) && ReferenceEquals(roster.Scene, _plaza):
                    roster.Users.Remove(plazaLeft.Member.UserNumber);
                    break;
            }
        }
    }

    private sealed class CoupleRoster
    {
        internal object? Scene;
        internal HashSet<int> Users = [];
    }

    private sealed class CoupleRosterSender<T>(IMessageSender<T> inner, RoomFlow flow) : IMessageSender<T> where T : IMessage
    {
        public string Protocol => inner.Protocol;

        public async ValueTask SendAsync(ISession session, T message, CancellationToken ct)
        {
            try { await inner.SendAsync(session, message, ct).ConfigureAwait(false); }
            catch
            {
                // A partial full install cannot prove which targets the native reader retained.
                if (message is RoomEntered or PlazaEntered) flow._coupleRosters.Remove(session);
                throw;
            }
            flow.InstalledCoupleRoster(session, message);
        }
    }
}
