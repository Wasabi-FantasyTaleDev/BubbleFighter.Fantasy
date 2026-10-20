namespace BubbleFighter.Fantasy.Contracts.Inventory;

/// <summary>
/// One build's initial inventory: the validated starter item ids in bag order and the
/// subset that starts worn. The default is the kr-1256 set; client builds whose
/// item tables lack some of those ids carry a smaller set as profile configuration so a
/// seeded bag never references an item the client does not know.
/// </summary>
public sealed class StarterKit
{
    /// <summary>The default (Dao) character item id.</summary>
    public const int DaoCharacterItemId = 300001;

    // Bag key order of a kr-1256 starter bag: each weapon family
    // contributes its 001/003/004/005 variants, two weapons appear as singletons, and the Dao
    // character leads. Unlocked extra characters in that session are account state, not initial data.
    private static readonly int[] DefaultKeys =
    [
        DaoCharacterItemId,
        201001, 201003, 201004, 201005,
        202001, 202003, 202004, 202006,
        203001, 203003, 203004, 203005,
        204001, 204003, 204004, 204005,
        205001, 207001,
    ];

    private static readonly int[] DefaultWorn = [DaoCharacterItemId, 202001, 201001, 205001, 207001];

    // The i64 markers and second i32 of the 802 and 803 records.
    private const long Marker802 = unchecked((long)0x40F869F000000000);
    private const long Marker803 = unchecked((long)0x40F86A0000000000);
    private const int Field2 = 99999;

    private readonly int[] _itemIds;
    private readonly int[] _wornItemIds;
    private readonly Dictionary<int, double>? _expiringItemDays;

    /// <summary>Creates a validated starter set.</summary>
    /// <param name="itemIds">The starter item ids in bag order; every id must be positive and unique.</param>
    /// <param name="wornItemIds">The ids that start worn; each must also appear in <paramref name="itemIds"/>.</param>
    /// <param name="expiringItemDays">
    /// Item ids that start with a limited lifetime, mapped to their grant length in days; the
    /// client accepts both permanent (the 2958101-day sentinel) and limited
    /// slots, and a limited seed exercises the duration
    /// label a build's bag cells render. Items absent from the map stay permanent.
    /// </param>
    /// <exception cref="ArgumentException">Thrown when the set is empty, too large, or contains
    /// a non-positive or duplicated id, or when a worn id is not a starter item.</exception>
    public StarterKit(IReadOnlyList<int> itemIds, IReadOnlyList<int> wornItemIds,
        IReadOnlyDictionary<int, double>? expiringItemDays = null)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        ArgumentNullException.ThrowIfNull(wornItemIds);
        if (itemIds.Count == 0 || itemIds.Count > InventoryLimits.MaxInventorySlotsPerBag)
            throw new ArgumentException(
                $"starter_item_ids must list 1..{InventoryLimits.MaxInventorySlotsPerBag} items; got {itemIds.Count}.",
                nameof(itemIds));
        if (wornItemIds.Count > itemIds.Count)
            throw new ArgumentException("starter_worn_ids cannot outnumber starter_item_ids.", nameof(wornItemIds));
        var seen = new HashSet<int>();
        foreach (var id in itemIds)
        {
            if (id <= 0)
                throw new ArgumentException($"starter_item_ids entry {id} is not a positive item id.", nameof(itemIds));
            if (!seen.Add(id))
                throw new ArgumentException($"starter_item_ids lists item {id} twice.", nameof(itemIds));
        }
        var wornSeen = new HashSet<int>();
        foreach (var id in wornItemIds)
        {
            if (!seen.Contains(id))
                throw new ArgumentException($"starter_worn_ids entry {id} is not in starter_item_ids.", nameof(wornItemIds));
            if (!wornSeen.Add(id))
                throw new ArgumentException($"starter_worn_ids lists item {id} twice.", nameof(wornItemIds));
        }
        if (expiringItemDays is not null)
        {
            foreach (var (id, days) in expiringItemDays)
            {
                if (!seen.Contains(id))
                    throw new ArgumentException(
                        $"starter_expiry_days names item {id}, which is not in starter_item_ids.", nameof(expiringItemDays));
                if (days is not > 0.0)
                    throw new ArgumentException(
                        $"starter_expiry_days entry {id} must be a positive day count; got {days}.", nameof(expiringItemDays));
            }
        }
        _itemIds = [.. itemIds];
        _wornItemIds = [.. wornItemIds];
        _expiringItemDays = expiringItemDays is null ? null : new Dictionary<int, double>(expiringItemDays);
    }

    /// <summary>Gets the kr-1256 starter set: 19 items, five worn.</summary>
    public static StarterKit Default { get; } = new(DefaultKeys, DefaultWorn);

    /// <summary>Gets the starter item ids in the recorded bag order.</summary>
    public IReadOnlyList<int> ItemIds => _itemIds;

    /// <summary>Gets the ids worn by default, the character first.</summary>
    public IReadOnlyList<int> WornItemIds => _wornItemIds;

    /// <summary>Returns the same starter grants with the chosen initial character replacing the default character.</summary>
    /// <param name="characterItemId">The chosen character item id, in the native 300001..300031 family.</param>
    /// <returns>A new kit preserving weapon grants, worn positions and their expiry periods.</returns>
    public StarterKit WithInitialCharacter(int characterItemId)
    {
        if (characterItemId is < 300001 or > 300031)
            throw new ArgumentOutOfRangeException(nameof(characterItemId));
        int Replace(int id) => id is >= 300001 and <= 300031 ? characterItemId : id;
        return new(_itemIds.Select(Replace).Distinct().ToArray(), _wornItemIds.Select(Replace).Distinct().ToArray(),
            _expiringItemDays?.ToDictionary(pair => Replace(pair.Key), pair => pair.Value));
    }

    /// <summary>Builds the starter rows of one bag; only worn items have flag bit 0 set.</summary>
    /// <param name="userNum">The player the rows belong to.</param>
    /// <param name="bag">The character bag or the equipped bag.</param>
    /// <param name="now">The instant the limited grants start counting from.</param>
    /// <returns>The rows, in the configured order; grants without a configured expiry are permanent.</returns>
    public IReadOnlyList<InventorySlotRecord> Slots(long userNum, InventoryBag bag, DateTimeOffset now)
    {
        var marker = bag == InventoryBag.EquippedBag ? Marker803 : Marker802;
        var slots = new InventorySlotRecord[_itemIds.Length];
        for (var i = 0; i < _itemIds.Length; i++)
        {
            var id = _itemIds[i];
            var lifetime = _expiringItemDays is { } expiries && expiries.TryGetValue(id, out var days)
                ? InventoryLifetime.Until(now.AddDays(days))
                : InventoryLifetime.Permanent;
            slots[i] = new InventorySlotRecord(
                userNum, bag, marker, id, Field2, _wornItemIds.Contains(id) ? (byte)1 : (byte)0)
            { Lifetime = lifetime };
        }
        return slots;
    }
}
