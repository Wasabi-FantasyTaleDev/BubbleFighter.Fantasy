using System.Security.Cryptography;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Clans;

namespace BubbleFighter.Fantasy.Clan;

/// <summary>
/// The passports this row has issued and not yet seen back. A passport ties a clan login to the game session
/// that asked for it: the client forwards the passport unchanged as its clan login (kr-1256 sub_9D2FF0 case 21
/// into kr-1256 sub_525DF0), so the account id it states is only as good as the ticket beside it. One passport
/// is kept per account, the newest; it is redeemed once, dropped when its game connection closes, and the table
/// is bounded by the row's <see cref="ClanOptions.MaxTrackedOpens"/>.
/// </summary>
internal sealed class ClanPassports : IDisposable
{
    /// <summary>The ticket length in bytes: 256 random bits, which a client cannot guess within a ticket's life.</summary>
    internal const int TicketBytes = 32;

    private readonly object _gate = new();
    private readonly Dictionary<int, Entry> _byUser = [];
    private readonly bool _authentication;
    private readonly TimeSpan _lifetime;
    private readonly int _capacity;
    private readonly TimeProvider _clock;
    private bool _disposed;

    /// <summary>Creates the table.</summary>
    /// <param name="authentication">Whether a login must present the ticket; off accepts the stated account of a live game session.</param>
    /// <param name="lifetime">How long an issued ticket may be redeemed.</param>
    /// <param name="capacity">The most accounts with an unredeemed passport.</param>
    /// <param name="clock">The clock ticket age is measured on.</param>
    public ClanPassports(bool authentication, TimeSpan lifetime, int capacity, TimeProvider clock)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _authentication = authentication;
        _lifetime = lifetime;
        _capacity = capacity;
        _clock = clock;
    }

    /// <summary>
    /// Issues the passport of a logged-in game session, replacing the account's earlier one.
    /// </summary>
    /// <param name="game">The game session that asked.</param>
    /// <param name="userNumber">Its account id.</param>
    /// <returns>The ticket to put in the passport; empty when authentication is off.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the table is full of other accounts.</exception>
    public byte[] Issue(ISession game, int userNumber)
    {
        var ticket = _authentication ? RandomNumberGenerator.GetBytes(TicketBytes) : [];
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_byUser.ContainsKey(userNumber) && _byUser.Count >= _capacity)
                throw new InvalidOperationException(
                    $"Clan passport table is full ({_capacity} accounts). Raise max_tracked_opens.");
            _byUser[userNumber] = new Entry(game, ticket, _clock.GetTimestamp());
        }
        return ticket;
    }

    /// <summary>
    /// Redeems the passport a clan login presents. A passport is used up by the attempt that names its account,
    /// whether or not the ticket matched, so a wrong guess cannot be repeated against the same ticket.
    /// </summary>
    /// <param name="userNumber">The account id the login states.</param>
    /// <param name="ticket">The ticket the login carries.</param>
    /// <param name="game">The live game session the passport was issued to, when accepted.</param>
    /// <returns>The answer for the client.</returns>
    public ClanServerLoginResult Redeem(int userNumber, ReadOnlySpan<byte> ticket, out ISession? game)
    {
        game = null;
        Entry? entry;
        lock (_gate)
        {
            if (!_byUser.Remove(userNumber, out entry))
                return _authentication ? ClanServerLoginResult.NotAuthenticated : ClanServerLoginResult.Failed;
        }
        if (_authentication
            && (_clock.GetElapsedTime(entry.IssuedAt) > _lifetime
                || !CryptographicOperations.FixedTimeEquals(entry.Ticket, ticket)))
            return ClanServerLoginResult.NotAuthenticated;
        if (entry.Game.Connection.IsClosed)
            return ClanServerLoginResult.Failed;
        game = entry.Game;
        return ClanServerLoginResult.Accepted;
    }

    /// <summary>Drops the passports issued to a closed game connection.</summary>
    /// <param name="connectionId">The closed connection.</param>
    public void RemoveConnection(long connectionId)
    {
        lock (_gate)
        {
            foreach (var user in _byUser.Where(pair => pair.Value.Game.Connection.Id == connectionId).Select(pair => pair.Key).ToArray())
                _byUser.Remove(user);
        }
    }

    /// <summary>Releases every passport this row still holds.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _byUser.Clear();
        }
    }

    private sealed record Entry(ISession Game, byte[] Ticket, long IssuedAt);
}

/// <summary>
/// What a logged-in clan connection is bound to: the account and the game session whose passport it presented.
/// Registered as a session feature of the clan connection for as long as it stays logged in.
/// </summary>
/// <param name="UserNumber">The account id.</param>
/// <param name="Nickname">The nickname the game session had when the passport was issued.</param>
/// <param name="Game">The game session; the clan connection ends when it does.</param>
internal sealed record ClanConnectionIdentity(int UserNumber, string Nickname, ISession Game);
