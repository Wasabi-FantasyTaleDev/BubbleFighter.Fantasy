using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Players;

namespace BubbleFighter.Fantasy.Persistence.Memory;

/// <summary>
/// Bounded process-local player identity repository used by the memory persistence row.
/// It shares the same player entries as inventory and fighter stores, so native character
/// creation and consumable name changes are atomic with the in-memory bags.
/// </summary>
public sealed partial class MemoryPlayerRepository(MemoryPlayerTable table) : IPlayerRepository
{
    private readonly object _identityGate = new();
    private long _nextUserNum;

    /// <inheritdoc />
    public bool PersistsState => false;

    /// <inheritdoc />
    public bool CreatesStarterInventoryAtomically => true;

    /// <inheritdoc />
    public ValueTask<PlayerRecord?> FindByUsernameAsync(string username, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(username);
        ct.ThrowIfCancellationRequested();
        lock (_identityGate)
        {
            var entry = FindByUsername(username);
            return ValueTask.FromResult(entry is null ? null : Record(entry));
        }
    }

    /// <inheritdoc />
    public ValueTask<PlayerRecord> CreateAsync(string username, string nickname, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(username);
        ArgumentException.ThrowIfNullOrEmpty(nickname);
        ct.ThrowIfCancellationRequested();
        lock (_identityGate)
        {
            var existing = FindByUsername(username);
            if (existing is not null)
                return ValueTask.FromResult(Record(existing));

            var userNum = Interlocked.Increment(ref _nextUserNum);
            var entry = table.GetOrAdd(userNum);
            entry.UserNum = userNum;
            entry.Username = username;
            entry.Nickname = nickname;
            entry.Level = 1;
            _entries.Add(username, entry);
            return ValueTask.FromResult(Record(entry));
        }
    }

    /// <inheritdoc />
    public ValueTask<bool> NicknameExistsAsync(string nickname, CancellationToken ct, string? exceptUsername = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(nickname);
        ct.ThrowIfCancellationRequested();
        lock (_identityGate)
            return ValueTask.FromResult(TableEntries().Any(entry =>
                !string.Equals(entry.Username, exceptUsername, StringComparison.Ordinal)
                && string.Equals(entry.Nickname, nickname, StringComparison.OrdinalIgnoreCase)));
    }

    /// <inheritdoc />
    public ValueTask<bool> HasCreatedCharacterAsync(long userNum, CancellationToken ct)
    {
        RequireUserNum(userNum);
        ct.ThrowIfCancellationRequested();
        var entry = table.Find(userNum);
        if (entry is null) return ValueTask.FromResult(false);
        lock (entry.Gate) return ValueTask.FromResult(entry.CharacterCreated);
    }

    /// <inheritdoc />
    public ValueTask<PlayerRecord?> CreateCharacterAsync(string username, string nickname, StarterKit kit, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(username);
        ArgumentException.ThrowIfNullOrEmpty(nickname);
        ArgumentNullException.ThrowIfNull(kit);
        ct.ThrowIfCancellationRequested();
        lock (_identityGate)
        {
            var entry = FindByUsername(username);
            if (entry is not null && entry.CharacterCreated)
                return ValueTask.FromResult<PlayerRecord?>(null);
            if (TableEntries().Any(other => !ReferenceEquals(other, entry)
                && string.Equals(other.Nickname, nickname, StringComparison.OrdinalIgnoreCase)))
                return ValueTask.FromResult<PlayerRecord?>(null);

            if (entry is null)
            {
                var userNum = Interlocked.Increment(ref _nextUserNum);
                entry = table.GetOrAdd(userNum);
                entry.UserNum = userNum;
                entry.Username = username;
                entry.Level = 1;
                _entries.Add(username, entry);
            }

            lock (entry.Gate)
            {
                var character = kit.WornItemIds.Single(id => id is >= 300001 and <= 300031);
                foreach (var bag in new[] { InventoryBag.CharacterBag, InventoryBag.EquippedBag })
                {
                    var current = entry.Bags.GetValueOrDefault((int)bag) ?? [];
                    var rows = current.Select(row => row.ItemId is >= 300001 and <= 300031
                        ? row with { Flags = (byte)(row.Flags & ~InventorySlotRecord.WornFlagBit) } : row).ToArray();
                    var grants = kit.Slots(EntryUserNum(entry), bag, DateTimeOffset.UtcNow);
                    var merged = InventoryAppend.Merge(EntryUserNum(entry), bag, rows, grants).Select(row =>
                        grants.Any(grant => grant.ItemId == row.ItemId && (grant.Flags & InventorySlotRecord.WornFlagBit) != 0)
                            ? row with { Flags = (byte)(row.Flags | InventorySlotRecord.WornFlagBit) } : row);
                    entry.Bags[(int)bag] = [.. merged];
                }
                entry.Fighters[0] = new(EntryUserNum(entry), 0, character - 300000, entry.Level, character, nickname);
                entry.Nickname = nickname;
                entry.CharacterCreated = true;
                entry.TutorialCompleted = false;
                return ValueTask.FromResult<PlayerRecord?>(Record(entry));
            }
        }
    }

    /// <inheritdoc />
    public ValueTask<bool> NeedsTutorialAsync(long userNum, CancellationToken ct)
    {
        RequireUserNum(userNum);
        ct.ThrowIfCancellationRequested();
        var entry = table.Find(userNum);
        if (entry is null) return ValueTask.FromResult(false);
        lock (entry.Gate) return ValueTask.FromResult(entry.CharacterCreated && !entry.TutorialCompleted);
    }

    /// <inheritdoc />
    public ValueTask CompleteTutorialAsync(long userNum, CancellationToken ct)
    {
        RequireUserNum(userNum);
        ct.ThrowIfCancellationRequested();
        var entry = table.Find(userNum) ?? throw new InvalidOperationException("Tutorial completion requires an existing player.");
        lock (entry.Gate) entry.TutorialCompleted = true;
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<int> RenameWithItemAsync(long userNum, string nickname, int itemId, CancellationToken ct)
    {
        RequireUserNum(userNum);
        ArgumentException.ThrowIfNullOrEmpty(nickname);
        if (itemId <= 0) throw new ArgumentOutOfRangeException(nameof(itemId));
        ct.ThrowIfCancellationRequested();
        lock (_identityGate)
        {
            var entry = table.Find(userNum);
            if (entry is null) throw new InvalidOperationException("Rename requires an existing player.");
            if (TableEntries().Any(other => !ReferenceEquals(other, entry)
                && string.Equals(other.Nickname, nickname, StringComparison.OrdinalIgnoreCase)))
                return ValueTask.FromResult(9);
            lock (entry.Gate)
            {
                var bag = entry.Bags.GetValueOrDefault((int)InventoryBag.CharacterBag);
                var item = bag?.LastOrDefault(row => row.ItemId == itemId && row.Quantity > 0
                    && (row.Lifetime.ExpiresAtUnixMilliseconds is not { } end || end > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
                if (item is null) return ValueTask.FromResult(1);
                foreach (var bagKind in new[] { InventoryBag.CharacterBag, InventoryBag.EquippedBag })
                    if (entry.Bags.TryGetValue((int)bagKind, out var rows))
                        entry.Bags[(int)bagKind] = rows.Select(row => row.ItemId == itemId
                                ? row with { Quantity = row.Quantity - 1 }
                                : row)
                            .Where(row => row.ItemId != itemId || row.Quantity > 0).ToList();
                entry.Nickname = nickname;
                foreach (var slot in entry.Fighters.Keys.ToArray())
                    entry.Fighters[slot] = entry.Fighters[slot] with { Nickname = nickname };
                return ValueTask.FromResult(0);
            }
        }
    }

    /// <summary>Finds a player by nickname, ignoring case, for the gift store.</summary>
    /// <param name="nickname">The nickname a sender typed.</param>
    /// <returns>The player, or null.</returns>
    internal BubbleFighter.Fantasy.Contracts.Presents.PresentPlayer? FindPresentPlayer(string nickname)
    {
        lock (_identityGate)
        {
            var entry = TableEntries().FirstOrDefault(other => string.Equals(other.Nickname, nickname, StringComparison.OrdinalIgnoreCase));
            return entry is null ? null : new(entry.UserNum, entry.Nickname);
        }
    }

    private PlayerEntry? FindByUsername(string username) => _entries.GetValueOrDefault(username);

    private IEnumerable<PlayerEntry> TableEntries()
    {
        return _entries.Values;
    }

    private readonly Dictionary<string, PlayerEntry> _entries = new(StringComparer.Ordinal);

    private static long EntryUserNum(PlayerEntry entry) => entry.UserNum;

    private static PlayerRecord Record(PlayerEntry entry)
    {
        lock (entry.Gate)
            return new(entry.UserNum, entry.Username, entry.Nickname, entry.Level) { ColorIndex = entry.ColorIndex };
    }

    private static void RequireUserNum(long userNum)
    {
        if (userNum <= 0) throw new ArgumentOutOfRangeException(nameof(userNum), userNum, "user_num must be positive.");
    }
}
