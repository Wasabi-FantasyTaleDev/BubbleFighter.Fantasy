namespace BubbleFighter.Fantasy.Contracts.Inventory;

/// <summary>
/// One S2C 803 container-2 article slot (<c>sub_10D8370</c>, 32-byte stride).
/// Nested extras are not stored.
/// </summary>
/// <param name="UserNum">Login <c>players.user_num</c>.</param>
/// <param name="Unknown10">First element field (<c>i64</c>).</param>
/// <param name="Unknown11">Second element field (<c>i32</c>).</param>
/// <param name="Unknown12">Third element field (<c>i32</c>).</param>
/// <param name="Unknown13">Fourth element field stored as bool.</param>
public sealed record EquippedSlotRecord(
    long UserNum,
    long Unknown10,
    int Unknown11,
    int Unknown12,
    bool Unknown13);
