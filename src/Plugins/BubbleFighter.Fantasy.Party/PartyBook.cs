using BubbleFighter.Fantasy.Contracts.Party;

namespace BubbleFighter.Fantasy.Party;

/// <summary>A message for one player, decided under the book's lock and sent after it is released.</summary>
/// <param name="UserNum">The recipient.</param>
/// <param name="Message">The message.</param>
internal readonly record struct PartyNotice(int UserNum, object Message);

/// <summary>
/// The parties of one build, in memory. A party lives while its members are logged in: the client rebuilds its party
/// list from S2C gspUpdateParty, so nothing is stored. Every change returns the notices it causes, in the order the
/// client expects them; the caller sends them outside the lock.
///
/// </summary>
internal sealed class PartyBook(int maxMembers, int maxPendingInvites, TimeSpan inviteTimeout, TimeProvider clock) : IPartyDirectory
{
    private readonly Lock _gate = new();
    private readonly Dictionary<int, List<PartyMember>> _partyOf = [];
    private readonly Dictionary<(int Inviter, int Target), PendingInvite> _pending = [];
    private readonly Dictionary<uint, (int Inviter, int Target)> _byToken = [];
    private uint _lastToken;

    // One invitation waiting for an answer: when it runs out, the word its prompt carries and the invited player's
    // connection (null when the caller does not bind it to one).
    private readonly record struct PendingInvite(long Expires, uint Token, long? Connection);

    /// <inheritdoc />
    public PartySnapshot? PartyOf(int userNum)
    {
        lock (_gate)
            return _partyOf.TryGetValue(userNum, out var members) ? new PartySnapshot([.. members]) : null;
    }

    /// <summary>Checks an invitation and records it as pending.</summary>
    /// <param name="inviter">The inviting player.</param>
    /// <param name="target">The invited player.</param>
    /// <param name="targetConnection">
    /// The invited player's connection; a later answer counts only from this connection. Null leaves the invitation
    /// unbound to a connection.
    /// </param>
    /// <returns>The result the inviter is told.</returns>
    public PartyInviteResult Invite(PartyMember inviter, PartyMember target, long? targetConnection = null) =>
        Invite(inviter, target, targetConnection, out _);

    /// <summary>Checks an invitation and records it as pending, issuing the word its prompt carries.</summary>
    /// <param name="inviter">The inviting player.</param>
    /// <param name="target">The invited player.</param>
    /// <param name="targetConnection">The invited player's connection, or null to leave the invitation unbound.</param>
    /// <param name="token">Receives the nonzero word issued for the pending invitation; zero when none is pending.</param>
    /// <returns>The result the inviter is told.</returns>
    public PartyInviteResult Invite(PartyMember inviter, PartyMember target, long? targetConnection, out uint token)
    {
        token = 0;
        if (inviter.UserNum == target.UserNum) return PartyInviteResult.Self;
        lock (_gate)
        {
            var now = clock.GetTimestamp();
            foreach (var key in _pending.Where(pair => pair.Value.Expires <= now).Select(pair => pair.Key).ToArray())
                Forget(key);
            if (_partyOf.TryGetValue(inviter.UserNum, out var party))
            {
                if (party.Any(member => member.UserNum == target.UserNum)) return PartyInviteResult.SameParty;
                if (party.Count >= maxMembers) return PartyInviteResult.PartyFull;
            }
            if (_partyOf.ContainsKey(target.UserNum)) return PartyInviteResult.Refused;
            if (!_pending.ContainsKey((inviter.UserNum, target.UserNum)) && _pending.Count >= maxPendingInvites)
                return PartyInviteResult.Refused;
            Forget((inviter.UserNum, target.UserNum));
            token = NextToken();
            _pending[(inviter.UserNum, target.UserNum)] = new(now + (long)(inviteTimeout.TotalSeconds * clock.TimestampFrequency), token, targetConnection);
            _byToken[token] = (inviter.UserNum, target.UserNum);
            return PartyInviteResult.Sent;
        }
    }

    /// <summary>Takes a pending invitation for the invited player.</summary>
    /// <param name="inviter">The inviting player.</param>
    /// <param name="target">The invited player.</param>
    /// <param name="connection">The connection the answer came from; an invitation bound to another one is not taken.</param>
    /// <returns>Whether an unexpired invitation was pending.</returns>
    public bool TakeInvite(int inviter, int target, long? connection = null)
    {
        lock (_gate)
        {
            if (!_pending.TryGetValue((inviter, target), out var pending)) return false;
            if (pending.Connection is { } bound && connection != bound) return false;
            Forget((inviter, target));
            return pending.Expires > clock.GetTimestamp();
        }
    }

    /// <summary>Takes the pending invitation a prompt's token names, for the invited player's own connection.</summary>
    /// <param name="token">The word the client echoed.</param>
    /// <param name="target">The invited player.</param>
    /// <param name="connection">The connection the answer came from.</param>
    /// <param name="inviter">Receives the inviting player when an unexpired invitation was taken.</param>
    /// <returns>Whether an unexpired invitation for this player and connection carried the token.</returns>
    public bool TakeInviteByToken(uint token, int target, long connection, out int inviter)
    {
        inviter = 0;
        lock (_gate)
        {
            if (!_byToken.TryGetValue(token, out var key) || key.Target != target) return false;
            var pending = _pending[key];
            if (pending.Connection is { } bound && bound != connection) return false;
            Forget(key);
            inviter = key.Inviter;
            return pending.Expires > clock.GetTimestamp();
        }
    }

    /// <summary>Invalidates both directions of invitation when a connection ends or is replaced.</summary>
    internal void ForgetInvites(int userNum)
    {
        lock (_gate)
            foreach (var key in _pending.Keys.Where(key => key.Inviter == userNum || key.Target == userNum).ToArray())
                Forget(key);
    }

    private void Forget((int Inviter, int Target) key)
    {
        if (_pending.Remove(key, out var pending)) _byToken.Remove(pending.Token);
    }

    // Tokens run 1, 2,... and wrap past zero, which stays reserved for "none"; a value still in use is skipped.
    private uint NextToken()
    {
        do _lastToken = unchecked(_lastToken + 1);
        while (_lastToken == 0 || _byToken.ContainsKey(_lastToken));
        return _lastToken;
    }

    /// <summary>Adds the invited player to the inviter's party, creating it when the inviter is alone.</summary>
    /// <param name="inviter">The inviting player, who leads a new party.</param>
    /// <param name="joiner">The player who accepted.</param>
    /// <param name="notices">Receives the notices: the party list and the join notice for every member.</param>
    /// <returns><see cref="PartyInviteResult.Sent"/> on success, or why the join was refused.</returns>
    public PartyInviteResult Join(PartyMember inviter, PartyMember joiner, List<PartyNotice> notices)
    {
        lock (_gate)
        {
            if (_partyOf.ContainsKey(joiner.UserNum)) return PartyInviteResult.Refused;
            if (!_partyOf.TryGetValue(inviter.UserNum, out var party))
            {
                party = [inviter];
                _partyOf[inviter.UserNum] = party;
            }
            if (party.Count >= maxMembers) return PartyInviteResult.PartyFull;
            party.Add(joiner);
            _partyOf[joiner.UserNum] = party;
            // kr-812 sub_927290 shows "joined %s's party" to the joiner and "%s joined" to the others.
            Broadcast(party, member => new PartyUpdated(new PartySnapshot([.. party])), notices);
            Broadcast(party, member => new PartyMemberJoined(joiner.Nickname), notices);
            return PartyInviteResult.Sent;
        }
    }

    /// <summary>Removes a player from their party, as a leave request or a disconnect.</summary>
    /// <param name="leaver">The player.</param>
    /// <param name="notices">Receives the leaver's confirmation and the remaining members' notices.</param>
    /// <returns>Whether the player was in a party.</returns>
    public bool Leave(PartyMember leaver, List<PartyNotice> notices)
    {
        lock (_gate)
        {
            if (!_partyOf.Remove(leaver.UserNum, out var party)) return false;
            var wasLeader = party[0].UserNum == leaver.UserNum;
            party.RemoveAll(member => member.UserNum == leaver.UserNum);
            notices.Add(new(leaver.UserNum, new PartyLeft()));
            notices.Add(new(leaver.UserNum, new PartyUpdated(Solo(leaver))));
            // kr-812 sub_926B90 announces the new leader (leaver, leader); sub_926D90 a member's leave.
            Broadcast(party, member => wasLeader ? new PartyLeaderReplaced(leaver.Nickname, party[0].Nickname)
                : new PartyMemberLeft(leaver.Nickname), notices);
            Settle(party, notices);
            return true;
        }
    }

    /// <summary>The leader removes a member.</summary>
    /// <param name="leader">The requesting player.</param>
    /// <param name="target">The member's nickname.</param>
    /// <param name="notices">Receives the kicked member's, the leader's and the others' notices.</param>
    /// <returns>The result the leader is told.</returns>
    public PartyKickResult Kick(PartyMember leader, string target, List<PartyNotice> notices)
    {
        lock (_gate)
        {
            if (!_partyOf.TryGetValue(leader.UserNum, out var party) || party[0].UserNum != leader.UserNum)
                return PartyKickResult.NotAllowed;
            if (string.Equals(target, leader.Nickname, StringComparison.Ordinal)) return PartyKickResult.Self;
            var kicked = party.FirstOrDefault(member => string.Equals(member.Nickname, target, StringComparison.Ordinal));
            if (kicked is null) return PartyKickResult.AlreadyLeft;
            party.Remove(kicked);
            _partyOf.Remove(kicked.UserNum);
            notices.Add(new(kicked.UserNum, new PartyKicked(leader.Nickname)));
            notices.Add(new(kicked.UserNum, new PartyUpdated(Solo(kicked))));
            // kr-812 sub_929370: "%s kicked %s from the party" for the members who stay; the leader gets its reply.
            foreach (var member in party.Where(member => member.UserNum != leader.UserNum))
                notices.Add(new(member.UserNum, new PartyMemberKicked(leader.Nickname, kicked.Nickname)));
            Settle(party, notices);
            return PartyKickResult.Kicked;
        }
    }

    /// <summary>The leader hands the leadership to a member, who moves to the front of the list.</summary>
    /// <param name="leader">The requesting player.</param>
    /// <param name="target">The member's nickname.</param>
    /// <param name="notices">Receives the handover answer and the reordered list for every member.</param>
    /// <returns>The result; on a refusal only the requester is told.</returns>
    public PartyLeaderChangeResult ChangeLeader(PartyMember leader, string target, List<PartyNotice> notices)
    {
        lock (_gate)
        {
            if (!_partyOf.TryGetValue(leader.UserNum, out var party)) return PartyLeaderChangeResult.NotLeader;
            var next = party.FirstOrDefault(member => string.Equals(member.Nickname, target, StringComparison.Ordinal));
            if (next is null) return PartyLeaderChangeResult.NotMember;
            if (party[0].UserNum == next.UserNum) return PartyLeaderChangeResult.AlreadyLeader;
            if (party[0].UserNum != leader.UserNum) return PartyLeaderChangeResult.NotLeader;
            party.Remove(next);
            party.Insert(0, next);
            // kr-812 sub_928C50 code 0 shows the requester and the others their own texts from the same packet.
            Broadcast(party, member => new PartyLeaderChanged(leader.Nickname, next.Nickname, PartyLeaderChangeResult.Changed), notices);
            Broadcast(party, member => new PartyUpdated(new PartySnapshot([.. party])), notices);
            return PartyLeaderChangeResult.Changed;
        }
    }

    /// <summary>A party of one, the list a player outside every party is sent.</summary>
    /// <param name="player">The player.</param>
    /// <returns>The snapshot.</returns>
    public static PartySnapshot Solo(PartyMember player) => new([player]);

    // A party left with one member ends: the client's end signal is a list that is the player alone.
    private void Settle(List<PartyMember> party, List<PartyNotice> notices)
    {
        if (party.Count == 1)
        {
            _partyOf.Remove(party[0].UserNum);
            notices.Add(new(party[0].UserNum, new PartyUpdated(Solo(party[0]))));
            return;
        }
        Broadcast(party, member => new PartyUpdated(new PartySnapshot([.. party])), notices);
    }

    private static void Broadcast(List<PartyMember> party, Func<PartyMember, object> message, List<PartyNotice> notices)
    {
        foreach (var member in party)
            notices.Add(new(member.UserNum, message(member)));
    }
}
