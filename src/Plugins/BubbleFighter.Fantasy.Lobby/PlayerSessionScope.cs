using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Players;
using BubbleFighter.Fantasy.Contracts.Sessions;

namespace BubbleFighter.Fantasy.Lobby;

/// <summary>What happened to a login's claim on an account.</summary>
internal enum LoginClaimStatus
{
    /// <summary>The session now holds the account and may be served as that player.</summary>
    Attached,

    /// <summary>The session already held the account on this connection, so the existing state is kept.</summary>
    Existing,

    /// <summary>Another live connection holds the account and the row's policy refuses second logins.</summary>
    RefusedDuplicate,
}

/// <summary>The outcome of claiming an account for a session.</summary>
/// <param name="Status">Whether the session holds the account, already held it, or was refused.</param>
/// <param name="Session">The player state the session holds; null when the claim was refused.</param>
/// <param name="Replaced">The live session the claim took the account from, when it replaced one.</param>
internal readonly record struct LoginClaim(LoginClaimStatus Status, IPlayerSession? Session, ISession? Replaced);

/// <summary>
/// Owns the <see cref="IPlayerSession"/> registrations this row makes and keeps at most one live
/// connection per account. One entry per live game connection: it is removed when the connection
/// closes and every remaining one is released when the row unloads, so the scope never outgrows
/// the listener's connection limit. It is also the build's <see cref="IOnlinePlayers"/>: the only
/// map from an account to its live connection.
/// </summary>
internal sealed class PlayerSessionScope : IDisposable, IOnlinePlayers
{
    private readonly Lock _gate = new();
    private readonly Dictionary<long, Registration> _registrations = [];
    private readonly Dictionary<long, ISession> _holders = [];

    /// <summary>
    /// Registers the player state for an authenticated login. A second login on the same
    /// connection for the same account keeps the existing state; one for a different account
    /// is refused, because every other plugin already reads the first identity.
    /// </summary>
    /// <param name="session">The session the login arrived on.</param>
    /// <param name="identity">The authenticated identity.</param>
    /// <returns>The player state the session holds.</returns>
    /// <exception cref="InvalidOperationException">Thrown for a different account on a session
    /// that is already logged in, or for an account another live connection still holds.</exception>
    public IPlayerSession Attach(ISession session, PlayerIdentity identity)
    {
        var claim = Claim(session, identity, takeOver: false);
        return claim.Session ?? throw new InvalidOperationException(
            $"Account {identity.UserNum} is already live on connection {claim.Replaced!.Connection.Id}; "
            + "the row must settle that login before attaching another.");
    }

    /// <summary>
    /// Claims an account for a session. When another live connection holds it, the claim either
    /// releases that connection's registration and reports it as <see cref="LoginClaim.Replaced"/>
    /// (<paramref name="takeOver"/>), or refuses the new login and leaves the holder untouched.
    /// </summary>
    /// <param name="session">The session the login arrived on.</param>
    /// <param name="identity">The authenticated identity.</param>
    /// <param name="takeOver">Whether the new login replaces the connection that holds the account.</param>
    /// <returns>The claim, including the replaced session when a takeover released one.</returns>
    /// <exception cref="InvalidOperationException">Thrown for a different account on a session
    /// that is already logged in.</exception>
    public LoginClaim Claim(ISession session, PlayerIdentity identity, bool takeOver)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_gate)
        {
            if (session.TryFeature<IPlayerSession>(out var existing))
            {
                if (existing.Identity.UserNum != identity.UserNum)
                    throw new InvalidOperationException(
                        $"Session {session.Id} is already logged in as {existing.Identity.UserNum}; refusing login as {identity.UserNum}.");
                return new LoginClaim(LoginClaimStatus.Existing, existing, null);
            }

            var connectionId = session.Connection.Id;
            if (_holders.TryGetValue(identity.UserNum, out var holder) && holder.Connection.Id == connectionId)
            {
                // The feature bag closed without a close event, so the index outlived its
                // registration: drop the stale entry and register this connection again.
                Release(connectionId);
            }
            else if (holder is not null && !takeOver)
            {
                return new LoginClaim(LoginClaimStatus.RefusedDuplicate, null, holder);
            }

            var state = new PlayerSession(identity);
            var handle = session.RegisterFeature<IPlayerSession>(state);
            ISession? replaced = null;
            if (_holders.TryGetValue(identity.UserNum, out var taken) && taken is not null
                && taken.Connection.Id != connectionId)
            {
                replaced = taken;
                Release(taken.Connection.Id);
            }

            _registrations[connectionId] = new Registration(handle, identity.UserNum);
            _holders[identity.UserNum] = session;
            return new LoginClaim(LoginClaimStatus.Attached, state, replaced);
        }
    }

    /// <inheritdoc />
    public ISession? FindByUserNum(int userNum)
    {
        lock (_gate)
            return _holders.GetValueOrDefault(userNum) is { Connection.IsClosed: false } holder ? holder : null;
    }

    /// <inheritdoc />
    /// <remarks>A scan of the live holders, bounded by the listener's connection limit; nicknames change with
    /// rename items, so the session's current identity is read rather than an index kept beside it.</remarks>
    public ISession? FindByNickname(string nickname)
    {
        ArgumentNullException.ThrowIfNull(nickname);
        lock (_gate)
        {
            foreach (var holder in _holders.Values)
                if (!holder.Connection.IsClosed && holder.TryFeature<IPlayerSession>(out var player)
                    && string.Equals(player.Identity.Nickname, nickname, StringComparison.Ordinal))
                    return holder;
            return null;
        }
    }

    /// <summary>Drops the registration of a closed connection.</summary>
    /// <param name="connectionId">The closed connection's id.</param>
    public void Remove(long connectionId)
    {
        lock (_gate)
            Release(connectionId);
    }

    /// <summary>Releases every registration this row still holds.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var connectionId in _registrations.Keys.ToArray())
                Release(connectionId);
        }
    }

    private void Release(long connectionId)
    {
        if (!_registrations.Remove(connectionId, out var registration))
            return;
        if (_holders.TryGetValue(registration.UserNum, out var holder) && holder.Connection.Id == connectionId)
            _holders.Remove(registration.UserNum);
        registration.Handle.Dispose();
    }

    /// <summary>One live connection's registration and the account it holds.</summary>
    /// <param name="Handle">The feature removal handle.</param>
    /// <param name="UserNum">The account the connection is logged in as.</param>
    private readonly record struct Registration(IDisposable Handle, long UserNum);
}
