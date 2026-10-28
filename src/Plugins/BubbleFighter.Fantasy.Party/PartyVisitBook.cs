namespace BubbleFighter.Fantasy.Party;

/// <summary>
/// Visits waiting for the visited player's answer, each bound to the visited player's connection and carrying the
/// nonzero word the modern prompt echoes. Bounded and expiring like the invitations; a disconnect forgets both sides.
/// </summary>
internal sealed class PartyVisitBook(int maxPending, TimeSpan timeout, TimeProvider clock)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(int Visitor, int Host), Pending> _pending = [];
    private readonly Dictionary<uint, (int Visitor, int Host)> _byToken = [];
    private uint _lastToken;

    private sealed record Pending(long Expires, uint Token, long HostConnection);

    /// <summary>Records a visit and issues its token; false when the bound is reached.</summary>
    public bool Ask(int visitor, int host, long hostConnection, out uint token)
    {
        token = 0;
        lock (_gate)
        {
            var now = clock.GetTimestamp();
            foreach (var key in _pending.Where(pair => pair.Value.Expires <= now).Select(pair => pair.Key).ToArray())
                Forget(key);
            if (!_pending.ContainsKey((visitor, host)) && _pending.Count >= maxPending) return false;
            Forget((visitor, host));
            token = NextToken();
            _pending[(visitor, host)] = new(now + (long)(timeout.TotalSeconds * clock.TimestampFrequency), token, hostConnection);
            _byToken[token] = (visitor, host);
            return true;
        }
    }

    /// <summary>Takes the visit a named visitor asked of this host, answered from the host's own connection.</summary>
    public bool Take(int visitor, int host, long hostConnection)
    {
        lock (_gate)
        {
            if (!_pending.TryGetValue((visitor, host), out var pending) || pending.HostConnection != hostConnection) return false;
            Forget((visitor, host));
            return pending.Expires > clock.GetTimestamp();
        }
    }

    /// <summary>Takes the visit a prompt's token names, answered from the host's own connection.</summary>
    public bool TakeByToken(uint token, int host, long hostConnection, out int visitor)
    {
        visitor = 0;
        lock (_gate)
        {
            if (!_byToken.TryGetValue(token, out var key) || key.Host != host) return false;
            var pending = _pending[key];
            if (pending.HostConnection != hostConnection) return false;
            Forget(key);
            visitor = key.Visitor;
            return pending.Expires > clock.GetTimestamp();
        }
    }

    /// <summary>Forgets every visit a player asked or was asked, when the player's connection ends.</summary>
    public void ForgetPlayer(int userNum)
    {
        lock (_gate)
            foreach (var key in _pending.Keys.Where(key => key.Visitor == userNum || key.Host == userNum).ToArray())
                Forget(key);
    }

    private void Forget((int Visitor, int Host) key)
    {
        if (_pending.Remove(key, out var pending)) _byToken.Remove(pending.Token);
    }

    // Tokens run 1, 2,... and wrap past zero, which the prompt never carries; a value still in use is skipped.
    private uint NextToken()
    {
        do _lastToken = unchecked(_lastToken + 1);
        while (_lastToken == 0 || _byToken.ContainsKey(_lastToken));
        return _lastToken;
    }
}
