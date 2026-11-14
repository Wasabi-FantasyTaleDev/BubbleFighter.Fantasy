using System.ComponentModel.DataAnnotations;
using System.Threading.Channels;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Companion;
using BubbleFighter.Fantasy.Contracts.Sessions;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Companion;

/// <summary>
/// Keeps every bound launcher that negotiated one event type in step with one piece of a game session's state. Game code
/// only marks a session as changed; one worker reads the state afterwards, outside every game lock, folds it into the
/// session's last state and sends it when it differs from what the launcher last received. Reading after the mark means
/// a late or repeated mark can never publish an older scene. A send refused by a full queue stays marked and is retried,
/// so the latest state (an inactive one included) is never dropped; only the latest state is kept, never a backlog.
/// State lives only for connections whose launcher negotiated the type and is removed when the game connection closes.
/// </summary>
/// <typeparam name="TObservation">What is read from the game session.</typeparam>
/// <typeparam name="TState">What the launcher receives; compared by value.</typeparam>
internal sealed class CompanionStateFeed<TObservation, TState>(ICompanionHub hub, string type, int version,
    Func<ISession, TObservation> observe, Func<TState?, TObservation, TState> fold, TimeSpan retry, ILogger logger)
    where TState : class
{
    private readonly Lock _gate = new();
    private readonly Dictionary<IConnection, Entry> _entries = [];
    private readonly Dictionary<IConnection, ISession> _marked = [];
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly Channel<byte> _wake = Channel.CreateBounded<byte>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private volatile bool _retrying;

    /// <summary>Subscribes the feed to the scene, connection and close events and runs its worker for the row's lifetime.</summary>
    internal async ValueTask BindAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        context.Effect(context.Events.On<GameSessionStateChanged>(e => Mark(e.Session, resend: false)));
        context.Effect(context.Events.On<CompanionSessionConnected>(e => Mark(e.Session, resend: true)));
        context.Effect(context.Events.On<ConnectionClosedEvent>(e => Forget(e.Connection)));
        context.Effect(_serial);
        await context.RunTaskAsync("companion-feed-" + type, RunAsync, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Marks a session for the worker. <paramref name="resend"/> sends the current state even when it equals the last one
    /// sent, for a launcher connection that has just become bound and has received nothing yet.
    /// </summary>
    internal void Mark(ISession session, bool resend)
    {
        if (session.Connection.IsClosed || !hub.Supports(session, type, version)) return;
        lock (_gate)
        {
            if (resend && _entries.TryGetValue(session.Connection, out var entry) && ReferenceEquals(entry.Session, session))
                entry.Delivered = null;
            _marked[session.Connection] = session;
        }
        _wake.Writer.TryWrite(0);
    }

    /// <summary>Reads, folds and records the session's current state without sending it, for a launcher's snapshot request.</summary>
    internal async ValueTask<TState> SnapshotAsync(ISession session, CancellationToken ct)
    {
        await _serial.WaitAsync(ct).ConfigureAwait(false);
        try { return Advance(session).State; }
        finally { _serial.Release(); }
    }

    /// <summary>Drops the state of a closed game connection.</summary>
    internal void Forget(IConnection connection)
    {
        lock (_gate)
        {
            _entries.Remove(connection);
            _marked.Remove(connection);
        }
    }

    /// <summary>The number of connections with retained state, for tests of the bound.</summary>
    internal int Retained { get { lock (_gate) return _entries.Count; } }

    internal async ValueTask RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (_retrying)
            {
                using var delay = CancellationTokenSource.CreateLinkedTokenSource(ct);
                delay.CancelAfter(retry);
                try { await _wake.Reader.ReadAsync(delay.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            }
            else
            {
                try { await _wake.Reader.ReadAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
            try { await FlushAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        }
    }

    /// <summary>Publishes every marked session once; a send refused by a full queue is marked again for the retry.</summary>
    internal async ValueTask FlushAsync(CancellationToken ct)
    {
        KeyValuePair<IConnection, ISession>[] work;
        lock (_gate)
        {
            work = [.. _marked];
            _marked.Clear();
        }
        var busy = false;
        foreach (var (connection, session) in work)
        {
            await _serial.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (connection.IsClosed) { Forget(connection); continue; }
                Entry entry;
                try { entry = Advance(session); }
                catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException)
                {
                    logger.LogWarning(error, "Companion {Type} could not read session {Session}; nothing was sent.", type, session.Id);
                    continue;
                }
                TState state;
                lock (_gate)
                {
                    if (Equals(entry.State, entry.Delivered)) continue;
                    state = entry.State;
                }
                if (hub.Send(session, type, version, state))
                {
                    lock (_gate) entry.Delivered = state;
                }
                else if (hub.Supports(session, type, version))
                {
                    // The connection's queue is full: keep the session marked so the latest state is sent on the retry.
                    lock (_gate) _marked[connection] = session;
                    busy = true;
                }
            }
            finally { _serial.Release(); }
        }
        _retrying = busy;
    }

    // Called under _serial, so reads and folds happen in one order; the read itself holds no lock of this feed.
    private Entry Advance(ISession session)
    {
        var observation = observe(session);
        lock (_gate)
        {
            if (!_entries.TryGetValue(session.Connection, out var entry) || !ReferenceEquals(entry.Session, session))
                _entries[session.Connection] = entry = new Entry(session);
            entry.State = fold(entry.State, observation);
            return entry;
        }
    }

    private sealed class Entry(ISession session)
    {
        public ISession Session { get; } = session;
        public TState State { get; set; } = null!;
        public TState? Delivered { get; set; }
    }
}

/// <summary>Delivery settings shared by the Nirinsou Link state feeds.</summary>
public sealed class CompanionFeedOptions
{
    /// <summary>Gets or sets how long, in milliseconds, a state refused by a full launcher queue waits before it is sent again.</summary>
    [Range(10, 60000)] public int RetryMilliseconds { get; set; } = 250;
}
