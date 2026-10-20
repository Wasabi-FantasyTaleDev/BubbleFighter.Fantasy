using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.InGame.Sessions;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.InGame.Handlers;

internal sealed class RoomQuickMatcher(RoomRegistry registry, RoomFlow rooms, InGameOptions options,
    IMessageSender<RoomMatchNotified> notifications, ILogger logger, CancellationToken lifetime)
{
    private sealed record Pending(long ConnectionId, CancellationTokenSource Cancellation, TaskCompletionSource Completion);
    private readonly object _gate = new();
    private readonly Dictionary<long, Pending> _pending = [];
    private readonly SemaphoreSlim _matching = new(1, 1);
    private bool _stopping;
    private Task? _stop;

    internal async ValueTask HandleAsync(ISession session, RoomMatchRequested request, CancellationToken ct)
    {
        // kr-812 sub_68E450 and cn-324 sub_58CF60 require queue stages distinct from ordinary room seating.
        if (request.DedicatedQueue || request.RetryQueue || options.AutoStartModeIds.Contains(request.ModeId))
        {
            logger.LogWarning("Session {Session} requested mode {Mode} through a separate queue stage (dedicated={Dedicated}, retry={Retry}); this stage is not mounted yet.",
                session.Id, request.ModeId, request.DedicatedQueue, request.RetryQueue);
            await notifications.SendAsync(session, new RoomMatchNotified(false), ct).ConfigureAwait(false);
            return;
        }
        _ = SessionPlayer.RequireIdentity(session);
        Pending pending;
        lock (_gate)
        {
            if (_stopping || _pending.ContainsKey(session.Id))
                return;
            if (_pending.Count >= options.MaxPendingQuickMatches)
                throw new InvalidOperationException("The configured pending quick-match limit has been reached.");
            pending = new Pending(session.Connection.Id, CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime),
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            _pending.Add(session.Id, pending);
        }
        try
        {
            var cancellation = pending.Cancellation.Token;
            await notifications.SendAsync(session, new RoomMatchNotified(true), cancellation).ConfigureAwait(false);
            var deadline = Environment.TickCount64 + options.QuickMatchWaitMs;
            while (true)
            {
                await _matching.WaitAsync(cancellation).ConfigureAwait(false);
                try
                {
                if (session.Connection.IsClosed || registry.TryGet(session.Id) is not null
                    || !session.TryFeature<IPlayerSession>(out var player)
                    || player.Phase is not (SessionPhase.MainMenu or SessionPhase.Plaza))
                    return;
                foreach (var room in registry.OpenRooms())
                {
                    bool suitable;
                    lock (room.Gate)
                        suitable = room.Phase == RoomPhase.Waiting && room.ModeId == request.ModeId
                            && room.Password.Length == 0 && !room.IsFull;
                    if (suitable && await rooms.TryMatchRoomAsync(session, room, request.ModeId, cancellation).ConfigureAwait(false))
                        return;
                }
                    if (Environment.TickCount64 >= deadline)
                    {
                        await rooms.HandleCreateAsync(session, new RoomCreateRequested("", "", request.ModeId,
                            options.QuickMatchDefaultMap), cancellation).ConfigureAwait(false);
                        return;
                    }
                }
                finally { _matching.Release(); }
                var remaining = deadline - Environment.TickCount64;
                if (remaining > 0)
                    await Task.Delay((int)Math.Min(options.QuickMatchPollMs, remaining), cancellation).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (pending.Cancellation.IsCancellationRequested) { }
        finally
        {
            lock (_gate)
                _pending.Remove(session.Id);
            pending.Cancellation.Dispose();
            pending.Completion.TrySetResult();
        }
    }

    internal void OnConnectionClosed(ConnectionClosedEvent closed)
    {
        lock (_gate)
            foreach (var pending in _pending.Values.Where(pending => pending.ConnectionId == closed.Connection.Id).ToArray())
                pending.Cancellation.Cancel();
    }

    internal Task StopAsync()
    {
        lock (_gate)
        {
            if (_stop is not null)
                return _stop;
            _stopping = true;
            var pendingRequests = _pending.Values.ToArray();
            foreach (var pending in pendingRequests)
                pending.Cancellation.Cancel();
            return _stop = FinishStopAsync(Task.WhenAll(pendingRequests.Select(pending => pending.Completion.Task)));
        }
    }

    private async Task FinishStopAsync(Task pending)
    {
        await pending.ConfigureAwait(false);
        _matching.Dispose();
    }
}
