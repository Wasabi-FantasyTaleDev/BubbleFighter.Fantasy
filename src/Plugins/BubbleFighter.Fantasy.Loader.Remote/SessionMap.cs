using System.Globalization;
using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Loader.Remote;

/// <summary>
/// Remembers the sessions an activation delivered messages for, so the plugin can answer them by opaque id.
/// Entries are dropped when their connection closes and the map never grows past <c>max_sessions</c>.
/// </summary>
internal sealed class SessionMap(int capacity, bool opaqueHandles = false)
{
    private readonly Dictionary<string, (ISession Session, long? ConnectionId)> _sessions = [];
    private readonly Lock _gate = new();
    private long _sequence;
    private readonly string _prefix = Guid.NewGuid().ToString("N") + ":";

    /// <summary>Gets the number of remembered sessions.</summary>
    public int Count { get { lock (_gate) return _sessions.Count; } }

    /// <summary>Remembers a session and returns its opaque id.</summary>
    /// <param name="session">The session a message arrived on.</param>
    /// <returns>The id the plugin uses in <c>messages.send</c>.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the session is new and the map is full.</exception>
    public string Add(ISession session)
    {
        lock (_gate)
        {
            if (opaqueHandles && Closed(session)) throw new InvalidOperationException("A closed session cannot be exported.");
            if (opaqueHandles && TryGetId(session, out var known)) return known;
            var id = opaqueHandles ? _prefix + checked(++_sequence).ToString(CultureInfo.InvariantCulture)
                : session.Id.ToString(CultureInfo.InvariantCulture);
            if (_sessions.ContainsKey(id))
                return id;
            if (_sessions.Count >= capacity)
                throw new InvalidOperationException($"SDK plugin session map is full ({capacity} sessions, max_sessions); a session must close before another is delivered.");
            _sessions[id] = (session, ConnectionIdOf(session));
            return id;
        }
    }

    /// <summary>Looks a session up by its opaque id.</summary>
    /// <param name="id">The id from <see cref="Add"/>.</param>
    /// <param name="session">Receives the session.</param>
    /// <returns>Whether the session is remembered.</returns>
    public bool TryGet(string id, out ISession session)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue(id, out var entry))
            {
                if (opaqueHandles && Closed(entry.Session))
                {
                    _sessions.Remove(id);
                    session = null!;
                    return false;
                }
                session = entry.Session;
                return true;
            }
        }
        session = null!;
        return false;
    }

    /// <summary>Returns the opaque id for a session already remembered by this activation.</summary>
    /// <param name="session">The session to find.</param>
    /// <param name="id">Receives the opaque id.</param>
    /// <returns>Whether the session is present in the map.</returns>
    public bool TryGetId(ISession session, out string id)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_gate)
        {
            foreach (var entry in _sessions)
            {
                if (ReferenceEquals(entry.Value.Session, session))
                {
                    id = entry.Key;
                    return true;
                }
            }
        }
        id = string.Empty;
        return false;
    }

    /// <summary>Forgets every session carried by a closed connection.</summary>
    /// <param name="connectionId">The closed connection's id.</param>
    public void RemoveConnection(long connectionId)
    {
        lock (_gate)
            foreach (var id in _sessions.Where(pair => pair.Value.ConnectionId == connectionId).Select(pair => pair.Key).ToArray())
                _sessions.Remove(id);
    }

    /// <summary>Forgets everything; used when the activation ends.</summary>
    public void Clear()
    {
        lock (_gate)
            _sessions.Clear();
    }

    private static long? ConnectionIdOf(ISession session)
    {
        try { return session.Connection.Id; }
        catch (NotSupportedException) { return null; }
    }

    private static bool Closed(ISession session)
    {
        try { return session.Connection.IsClosed; }
        catch (NotSupportedException) { return false; }
    }
}
