using BubbleFighter.Fantasy.Contracts.Clans;
using BubbleFighter.Fantasy.Contracts.Fighters;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Shop;
using BubbleFighter.Fantasy.Contracts.Wallet;
using System.Collections.Concurrent;

namespace BubbleFighter.Fantasy.Persistence.Memory;

/// <summary>
/// The bounded player table every in-memory repository of one row shares. Players
/// are keyed by their login <c>user_num</c>. Adding a player beyond <see cref="MaxPlayers"/> throws instead of
/// evicting or growing, so a full table is a loud error at the call site rather
/// than silent data loss. Per-player mutations lock on the player's own gate, so
/// sessions for different players never contend and one player's snapshot reads
/// and writes stay serialized.
///
/// </summary>
public sealed class MemoryPlayerTable
{
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<long, PlayerEntry> _players = [];

    /// <summary>Initializes the table with its player bound.</summary>
    /// <param name="maxPlayers">Maximum distinct players the table tracks before it refuses more.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="maxPlayers"/> is below 1.</exception>
    public MemoryPlayerTable(int maxPlayers)
    {
        if (maxPlayers < 1)
            throw new ArgumentOutOfRangeException(nameof(maxPlayers), maxPlayers, "max_players must be at least 1.");
        MaxPlayers = maxPlayers;
    }

    /// <summary>Gets the configured bound on distinct tracked players.</summary>
    public int MaxPlayers { get; }

    /// <summary>
    /// Returns the entry of an already tracked player without creating one, so
    /// read paths never grow the table.
    ///
    /// </summary>
    /// <param name="userNum">Login <c>players.user_num</c>.</param>
    /// <returns>The player's entry, or <see langword="null"/> when untracked.</returns>
    internal PlayerEntry? Find(long userNum) => _players.TryGetValue(userNum, out var entry) ? entry : null;

    /// <summary>
    /// Returns the entry of a player, tracking a new one when the table still has
    /// room. Write paths use this member; a full table fails the write.
    ///
    /// </summary>
    /// <param name="userNum">Login <c>players.user_num</c>.</param>
    /// <returns>The player's entry.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the table already tracks
    /// <see cref="MaxPlayers"/> players and <paramref name="userNum"/> is not one of them.</exception>
    internal PlayerEntry GetOrAdd(long userNum)
    {
        if (_players.TryGetValue(userNum, out var entry))
            return entry;
        lock (_gate)
        {
            if (_players.TryGetValue(userNum, out entry))
                return entry;
            if (_players.Count >= MaxPlayers)
                throw new InvalidOperationException(
                    $"persistence.memory reached its max_players cap ({MaxPlayers}); refusing to track user_num {userNum}.");
            entry = new PlayerEntry();
            _players[userNum] = entry;
            return entry;
        }
    }
}

/// <summary>
/// One tracked player's mutable state. Fields are guarded by <see cref="Gate"/>
/// so a snapshot read never observes a half-replaced list; the repositories
/// copy on read and store defensive copies on write, because the stored record
/// types are immutable but their lists are not.
///
/// </summary>
internal sealed class PlayerEntry
{
    /// <summary>Gets saved control maps by explicit scope; guarded by <see cref="Gate"/>.</summary>
    public Dictionary<string, BubbleFighter.Fantasy.Contracts.Settings.CustomizedKey[]> CustomizedKeys { get; } = new(StringComparer.Ordinal);

    /// <summary>Gets or sets the numeric identity of this entry.</summary>
    public long UserNum { get; set; }
    /// <summary>Gets or sets the login name that owns this entry.</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Gets or sets the current display name.</summary>
    public string Nickname { get; set; } = string.Empty;

    /// <summary>Gets or sets the stored progression level.</summary>
    public int Level { get; set; } = 1;

    /// <summary>Gets or sets the player-wide avatar material color, guarded by Gate.</summary>
    public ushort ColorIndex { get; set; }

    /// <summary>Gets or sets whether native character creation has completed.</summary>
    public bool CharacterCreated { get; set; }

    /// <summary>Gets or sets whether the initial tutorial has been completed.</summary>
    public bool TutorialCompleted { get; set; }

    /// <summary>Gets or sets the latest shop open; updates are serialized by Gate.</summary>
    public ShopOpenRecord? ShopOpen { get; set; }

    /// <summary>Gets or sets the last clan open; updates are serialized by Gate.</summary>
    public ClanOpenRecord? ClanOpen { get; set; }

    /// <summary>Gets or sets the bounded purchase audit, guarded by Gate.</summary>
    public List<ShopPurchaseRecord> Purchases { get; set; } = [];

    /// <summary>Gets or sets the last allocated purchase identity, including removed items.</summary>
    public long LastPurchaseSlotKey { get; set; }

    /// <summary>Gets the lock serializing this player's mutations and snapshot reads.</summary>
    public object Gate { get; } = new();

    /// <summary>Gets or sets the stored money snapshot, or <see langword="null"/> before the first write.</summary>
    public PlayerWallet? Money { get; set; }

    /// <summary>Gets or sets the level and experience, or <see langword="null"/> before the first round. Guards: <see cref="Gate"/>.</summary>
    public BubbleFighter.Fantasy.Contracts.Progress.PlayerProgress? Progress { get; set; }

    /// <summary>
    /// Gets the receipts of the player's latest round settlements, oldest first, at most
    /// <see cref="MemoryPlayerProgressRepository.KeptSettlements"/>. Guards: <see cref="Gate"/>.
    ///
    /// </summary>
    public List<BubbleFighter.Fantasy.Contracts.Progress.RoundSettlementResult> Settlements { get; } = [];

    /// <summary>Gets the match ids of <see cref="Settlements"/>, in the same order. Guards: <see cref="Gate"/>.</summary>
    public List<string> SettlementMatches { get; } = [];

    /// <summary>
    /// Gets or sets the active weapon-preset slot the server granted last; 0 until the first grant.
    /// The value decides the S2C 837 leading byte and the S2C 836 entry index of the next login burst. Guards:
    /// <see cref="Gate"/>.
    /// </summary>
    public byte ActiveWeaponPresetSlot { get; set; }

    /// <summary>
    /// Gets or sets the UTC day of the player's last login as the life rule saw it, or null before the first
    /// login (<see cref="InventoryLife"/>). Guards: <see cref="Gate"/>.
    ///
    /// </summary>
    public DateOnly? LastLoginDay { get; set; }

    /// <summary>Gets the per-bag slot lists, keyed by the <see cref="InventoryBag"/> ordinal. Guards: <see cref="Gate"/>.</summary>
    public Dictionary<int, List<InventorySlotRecord>> Bags { get; } = [];

    /// <summary>Gets the fighter slots keyed by slot ordinal. Guards: <see cref="Gate"/>.</summary>
    public Dictionary<byte, FighterRecord> Fighters { get; } = [];
}
