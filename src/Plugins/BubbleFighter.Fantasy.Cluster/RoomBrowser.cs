using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Cluster;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Contracts.Sessions;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Cluster;

/// <summary>Mounts a bounded native room browser only when explicitly included in a cluster profile.</summary>
[Plugin("cluster.room-browser", Description = "Connection-local native room aliases over authenticated discovery.",
    Requires = [typeof(IFederatedRoomDiscovery)], ProvidesMany = [typeof(IRoomBrowser)])]
public sealed class RoomBrowserPlugin : IPlugin
{
    /// <inheritdoc />
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var browser = new RoomBrowser(context.Config.Bind<RoomBrowserOptions>(),
            context.Services.Get<IFederatedRoomDiscovery>(), context.Logger);
        context.Effect(browser);
        context.Effect(context.Events.On<ConnectionClosedEvent>(browser.OnClosed));
        context.Effect(context.Services.RegisterMany<IRoomBrowser>(browser));
        return ValueTask.CompletedTask;
    }
}

internal sealed class RoomBrowser(RoomBrowserOptions options, IFederatedRoomDiscovery discovery, ILogger? logger = null) : IRoomBrowser, IDisposable
{
    private readonly ILogger _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    private readonly Lock _gate = new();
    private readonly Dictionary<IConnection, State> _connections = new(ReferenceEqualityComparer.Instance);
    private readonly CancellationTokenSource _stop = new();
    private bool _disposed;

    public string ClientBuild => options.ClientBuild;

    // State lives exactly as long as its game connection, which the listener already bounds.
    internal int RetainedConnections { get { lock (_gate) return _connections.Count; } }

    public async ValueTask<IReadOnlyList<RoomListEntry>> ListAsync(ISession session,
        IReadOnlyList<PublishedRoomSnapshot> localRooms, CancellationToken cancellationToken)
    {
        var state = Begin(session, create: true, cancellationToken)!;
        try
        {
            IReadOnlyList<FederatedRoom> rooms;
            try { rooms = await discovery.ListAsync(ClientBuild, state.Cancellation!.Token).ConfigureAwait(false); }
            catch (Exception error) when (Unavailable(error, cancellationToken) && !state.Cancellation!.IsCancellationRequested)
            {
                // A lost directory hides remote rooms only; this node's own rooms stay listed and joinable.
                _logger.LogWarning(error, "Session {Session} lists only local rooms: the cluster room directory is unavailable.", session.Id);
                rooms = [];
            }
            state.Cancellation!.Token.ThrowIfCancellationRequested();
            lock (_gate)
            {
                EnsureCurrent(session, state);
                // Local registry snapshots are newer than the last heartbeat and carry no remote lease claim.
                return state.Aliases.Replace(localRooms,
                    rooms.Where(room => room.NodeId != options.NodeId && room.Listing is not null).ToArray(), options.NodeId, ClientBuild);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Invalidate(session);
            throw;
        }
        catch (Exception error) when (Unavailable(error, cancellationToken))
        {
            Invalidate(session);
            throw new RoomBrowserUnavailableException("The room browser could not obtain a current list.", error);
        }
        finally { End(state); }
    }

    public async ValueTask<RoomBrowserSelection?> ResolveAsync(ISession session, int alias, CancellationToken cancellationToken)
    {
        var state = Begin(session, create: false, cancellationToken);
        if (state is null) return null;
        try
        {
            var target = state.Aliases.Resolve(alias);
            if (target is null) return null;
            if (target.Local)
            {
                lock (_gate)
                {
                    EnsureCurrent(session, state);
                    return new(true, target.NodeId, 0, target.RoomId, target.InstanceId, true);
                }
            }
            var rooms = await discovery.ListAsync(ClientBuild, state.Cancellation!.Token).ConfigureAwait(false);
            state.Cancellation.Token.ThrowIfCancellationRequested();
            lock (_gate)
            {
                EnsureCurrent(session, state);
                var room = rooms.FirstOrDefault(room => room.NodeId == target.NodeId && room.Epoch == target.Epoch
                    && room.ClientBuild == target.ClientBuild && room.RoomId == target.RoomId && room.InstanceId == target.InstanceId
                    && room.Listing is not null);
                if (room is null) state.Aliases.Invalidate();
                return room is null ? null : new(false, room.NodeId, room.Epoch, room.RoomId, room.InstanceId, room.JoinAllowed);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Invalidate(session);
            throw;
        }
        catch (Exception error) when (Unavailable(error, cancellationToken))
        {
            Invalidate(session);
            throw new RoomBrowserUnavailableException("The selected room could not be revalidated.", error);
        }
        finally { End(state); }
    }

    public void Invalidate(ISession session)
    {
        lock (_gate)
            if (_connections.TryGetValue(session.Connection, out var state))
            {
                state.Revision++;
                state.Aliases.Invalidate();
            }
    }

    internal void OnClosed(ConnectionClosedEvent change)
    {
        lock (_gate)
            if (_connections.TryGetValue(change.Connection, out var state))
            {
                state.Closed = true;
                state.Aliases.Invalidate();
                if (!state.Busy) _connections.Remove(change.Connection);
            }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var state in _connections.Values)
            {
                state.Closed = true;
                state.Aliases.Invalidate();
            }
            _connections.Clear();
        }
        try { _stop.Cancel(); }
        finally { _stop.Dispose(); }
    }

    private State? Begin(ISession session, bool create, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (session.Profile.Id != ClientBuild || !session.TryFeature<IPlayerSession>(out var player)
            || player.LoginConnectionId != session.Connection.Id)
            throw new InvalidOperationException("Room browsing requires the authenticated owning game connection for this build.");
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (session.Connection.IsClosed) throw new IOException("The browser connection has closed.");
            if (!_connections.TryGetValue(session.Connection, out var state))
            {
                if (!create) return null;
                state = new(session, options.MaxAliasesPerConnection);
                _connections.Add(session.Connection, state);
            }
            if (state.Closed || !ReferenceEquals(session, state.Session))
                throw new IOException("The room browser state belongs to another session.");
            if (state.Busy)
            {
                state.Aliases.Invalidate();
                state.Revision++;
                throw new RoomBrowserUnavailableException("The connection already has a room browser operation in progress.",
                    new InvalidOperationException("Concurrent room browser requests are not queued."));
            }
            state.Busy = true;
            state.ActiveRevision = state.Revision;
            state.Player = player;
            state.Generation = player.Generation;
            state.Cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
            return state;
        }
    }

    private void EnsureCurrent(ISession session, State state)
    {
        if (_disposed || state.Closed || session.Connection.IsClosed || !ReferenceEquals(session, state.Session)
            || state.ActiveRevision != state.Revision || !session.TryFeature<IPlayerSession>(out var player)
            || !ReferenceEquals(player, state.Player) || player.Generation != state.Generation
            || player.LoginConnectionId != session.Connection.Id)
            throw new IOException("The room browser operation no longer belongs to a current connection view.");
    }

    private void End(State state)
    {
        lock (_gate)
        {
            state.Cancellation?.Dispose();
            state.Cancellation = null;
            state.Busy = false;
            if (state.Closed) _connections.Remove(state.Session.Connection);
        }
    }

    private static bool Unavailable(Exception error, CancellationToken ct) =>
        error is IOException or HttpRequestException or InvalidOperationException or System.Text.Json.JsonException
        || error is OperationCanceledException && !ct.IsCancellationRequested;

    private sealed class State(ISession session, int aliases)
    {
        internal ISession Session { get; } = session;
        internal RoomBrowserAliases Aliases { get; } = new(aliases);
        internal bool Closed { get; set; }
        internal bool Busy { get; set; }
        internal long Revision { get; set; }
        internal long ActiveRevision { get; set; }
        internal IPlayerSession? Player { get; set; }
        internal long Generation { get; set; }
        internal CancellationTokenSource? Cancellation { get; set; }
    }
}
