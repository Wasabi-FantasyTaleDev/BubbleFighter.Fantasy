using BubbleFighter.Fantasy.Contracts.Clans;

namespace BubbleFighter.Fantasy.Persistence.Memory;

/// <summary>
/// Clans, member rows and board posts held in memory for the life of the persistence row. One gate serializes
/// every operation, so the name and one-row-per-player rules hold without further locking.
/// </summary>
public sealed class MemoryClanDirectory : IClanDirectory
{
    // Ids start above the client's reserved training clan id 99 (kr-1256 sub_51A3A0 treats clan 99 specially).
    private const long FirstClanId = 1000;

    private readonly object _gate = new();
    private readonly Dictionary<long, ClanRecord> _clans = [];
    private readonly Dictionary<long, ClanMemberRecord> _members = [];
    private readonly Dictionary<long, ClanPostRecord> _posts = [];
    private readonly TimeProvider _clock;
    private long _nextClanId = FirstClanId;
    private long _nextPostId = 1;

    /// <summary>Creates an empty directory.</summary>
    /// <param name="clock">The clock that stamps rows; the system clock when null.</param>
    public MemoryClanDirectory(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    /// <inheritdoc />
    public ValueTask<ClanRecord?> CreateAsync(string name, string introduction, int capacity, long founderUserNum,
        string founderNickname, int founderLevel, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(introduction);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(founderUserNum);
        lock (_gate)
        {
            if (_members.ContainsKey(founderUserNum) || _clans.Values.Any(clan => SameName(clan.Name, name)))
                return new((ClanRecord?)null);
            var now = Now;
            var clan = new ClanRecord(_nextClanId++, name, founderUserNum, introduction, "", 1, 0, 0, capacity, 1, now, null, 1);
            _clans[clan.ClanId] = clan;
            _members[founderUserNum] = new ClanMemberRecord(clan.ClanId, founderUserNum, founderNickname, founderLevel, ClanRole.Master, now, now);
            return new(clan);
        }
    }

    /// <inheritdoc />
    public ValueTask<ClanRecord?> FindAsync(long clanId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
            return new(_clans.TryGetValue(clanId, out var clan) ? Counted(clan) : null);
    }

    /// <inheritdoc />
    public ValueTask<ClanRecord?> FindByNameAsync(string name, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(name);
        lock (_gate)
        {
            var clan = _clans.Values.FirstOrDefault(stored => SameName(stored.Name, name));
            return new(clan is null ? null : Counted(clan));
        }
    }

    /// <inheritdoc />
    public ValueTask<ClanSearchPage> SearchAsync(string text, ClanSearchOrder order, bool recruitingOnly, int skip, int take, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(text);
        if (!Enum.IsDefined(order))
            throw new ArgumentOutOfRangeException(nameof(order), order, "Unknown clan search order.");
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(take);
        lock (_gate)
        {
            var found = _clans.Values.Where(clan => clan.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
                && (!recruitingOnly || clan.Recruitment != 0));
            var matches = (order == ClanSearchOrder.Points
                ? found.OrderByDescending(clan => clan.Points).ThenBy(clan => clan.ClanId)
                : found.OrderBy(clan => clan.ClanId)).ToArray();
            return new(new ClanSearchPage(matches.Length, matches.Skip(skip).Take(take).Select(Counted).ToArray()));
        }
    }

    /// <inheritdoc />
    public ValueTask<bool> UpdateAsync(ClanRecord clan, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(clan);
        lock (_gate)
        {
            if (!_clans.TryGetValue(clan.ClanId, out var stored))
                return new(false);
            _clans[clan.ClanId] = clan with { Name = stored.Name, CreatedAt = stored.CreatedAt };
            return new(true);
        }
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<ClanMemberRecord>> DeleteDueAsync(DateTime now, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var due = _clans.Values.Where(clan => clan.DisbandAt is { } at && at <= now).Select(clan => clan.ClanId).ToHashSet();
            var rows = _members.Values.Where(member => due.Contains(member.ClanId)).OrderBy(member => member.ClanId).ThenBy(member => member.JoinedAt).ToArray();
            foreach (var clanId in due)
            {
                _clans.Remove(clanId);
                foreach (var post in _posts.Values.Where(post => post.ClanId == clanId).Select(post => post.PostId).ToArray())
                    _posts.Remove(post);
            }
            foreach (var row in rows)
                _members.Remove(row.UserNum);
            return new(rows);
        }
    }

    /// <inheritdoc />
    public ValueTask<bool> DeleteAsync(long clanId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_clans.Remove(clanId))
                return new(false);
            foreach (var user in _members.Values.Where(member => member.ClanId == clanId).Select(member => member.UserNum).ToArray())
                _members.Remove(user);
            foreach (var post in _posts.Values.Where(post => post.ClanId == clanId).Select(post => post.PostId).ToArray())
                _posts.Remove(post);
            return new(true);
        }
    }

    /// <inheritdoc />
    public ValueTask<ClanMemberRecord?> FindMemberAsync(long userNum, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
            return new(_members.GetValueOrDefault(userNum));
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<ClanMemberRecord>> ListMembersAsync(long clanId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
            return new(_members.Values.Where(member => member.ClanId == clanId)
                .OrderBy(member => member.JoinedAt).ThenBy(member => member.UserNum).ToArray());
    }

    /// <inheritdoc />
    public ValueTask<bool> AddMemberAsync(long clanId, long userNum, string nickname, int level, int role, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(nickname);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userNum);
        lock (_gate)
        {
            if (!_clans.ContainsKey(clanId) || _members.ContainsKey(userNum))
                return new(false);
            var now = Now;
            _members[userNum] = new ClanMemberRecord(clanId, userNum, nickname, level, role, now, now);
            return new(true);
        }
    }

    /// <inheritdoc />
    public ValueTask<bool> SeatAsync(long clanId, long userNum, int role, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (role is not (ClanRole.Member or ClanRole.Deputy))
            throw new ArgumentOutOfRangeException(nameof(role), role, "A seat is given as member or deputy.");
        lock (_gate)
        {
            if (!_clans.TryGetValue(clanId, out var clan) || !_members.TryGetValue(userNum, out var member)
                || member.ClanId != clanId || member.Role != ClanRole.Applicant || Counted(clan).MemberCount >= clan.Capacity)
                return new(false);
            _members[userNum] = member with { Role = role };
            return new(true);
        }
    }

    /// <inheritdoc />
    public ValueTask<bool> TransferMasterAsync(long clanId, long fromUserNum, long toUserNum, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (fromUserNum == toUserNum || !_clans.TryGetValue(clanId, out var clan)
                || !_members.TryGetValue(fromUserNum, out var source) || source.ClanId != clanId || source.Role != ClanRole.Master
                || !_members.TryGetValue(toUserNum, out var to) || to.ClanId != clanId || !ClanRole.IsMember(to.Role))
                return new(false);
            _members[fromUserNum] = source with { Role = ClanRole.Member };
            _members[toUserNum] = to with { Role = ClanRole.Master };
            _clans[clanId] = clan with { MasterUserNum = toUserNum };
            return new(true);
        }
    }

    /// <inheritdoc />
    public ValueTask<bool> SetRoleAsync(long clanId, long userNum, int role, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_members.TryGetValue(userNum, out var member) || member.ClanId != clanId)
                return new(false);
            _members[userNum] = member with { Role = role };
            return new(true);
        }
    }

    /// <inheritdoc />
    public ValueTask<bool> RemoveMemberAsync(long clanId, long userNum, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
            return new(_members.TryGetValue(userNum, out var member) && member.ClanId == clanId && _members.Remove(userNum));
    }

    /// <inheritdoc />
    public ValueTask RefreshMemberAsync(long userNum, string nickname, int level, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(nickname);
        lock (_gate)
        {
            if (_members.TryGetValue(userNum, out var member))
                _members[userNum] = member with { Nickname = nickname, Level = level, SeenAt = Now };
        }
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<ClanPostRecord> AddPostAsync(long clanId, long userNum, string nickname, int level, string body, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(nickname);
        ArgumentNullException.ThrowIfNull(body);
        lock (_gate)
        {
            if (!_clans.ContainsKey(clanId))
                throw new InvalidOperationException($"Clan {clanId} does not exist.");
            var post = new ClanPostRecord(_nextPostId++, clanId, userNum, nickname, level, body, Now);
            _posts[post.PostId] = post;
            return new(post);
        }
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<ClanPostRecord>> ListPostsAsync(long clanId, long beforePostId, int take, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegative(beforePostId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(take);
        lock (_gate)
            return new(_posts.Values.Where(post => post.ClanId == clanId && (beforePostId == 0 || post.PostId < beforePostId))
                .OrderByDescending(post => post.PostId).Take(take).ToArray());
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<ClanPostRecord>> ListPostsAfterAsync(long clanId, long afterPostId, int take, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegative(afterPostId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(take);
        lock (_gate)
            return new(_posts.Values.Where(post => post.ClanId == clanId && post.PostId > afterPostId)
                .OrderBy(post => post.PostId).Take(take).ToArray());
    }

    /// <inheritdoc />
    public ValueTask<ClanPostRecord?> FindPostAsync(long clanId, long postId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
            return new(_posts.TryGetValue(postId, out var post) && post.ClanId == clanId ? post : null);
    }

    /// <inheritdoc />
    public ValueTask<bool> DeletePostAsync(long clanId, long postId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
            return new(_posts.TryGetValue(postId, out var post) && post.ClanId == clanId && _posts.Remove(postId));
    }

    private static bool SameName(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private ClanRecord Counted(ClanRecord clan) =>
        clan with { MemberCount = _members.Values.Count(member => member.ClanId == clan.ClanId && ClanRole.IsMember(member.Role)) };
}
