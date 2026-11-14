namespace BubbleFighter.Fantasy.Contracts.Inventory;

/// <summary>
/// Admits one timed ordinary ownership for a same-identity permanent conversion. The provider judges it at the later of
/// the command's instant and its own current time while it holds the account's locks, so a timer that runs out during a
/// lock wait is refused and a rebuilt request for an already permanent row never passes. The guard holds only values, so
/// remote plugins can still reach the laboratory store.
/// </summary>
/// <param name="TargetItemId">The native catalog identity converted in place.</param>
/// <param name="MinimumRemainingMilliseconds">The configured minimum remaining time, in whole days as milliseconds.</param>
/// <param name="ForbiddenFlags">Slot flag bits that exclude a target, for example worn or packed states.</param>
public sealed record InventoryConversionGuard(int TargetItemId, long MinimumRemainingMilliseconds, byte ForbiddenFlags)
{
    /// <summary>
    /// The half-second kr-1256 sub_DCB180 adds to the remaining time before truncating it to whole days.
    /// </summary>
    public const long ToleranceMilliseconds = 500;

    /// <summary>
    /// The longest timer still treated as timed: kr-1256 sub_806A20 reads a truncated remainder above 10000 days as permanent.
    /// </summary>
    public const long MaximumTimedMilliseconds = 10_000L * 86_400_000;

    /// <summary>Rejects an unusable target identity or threshold before any store is opened.</summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(TargetItemId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MinimumRemainingMilliseconds);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MinimumRemainingMilliseconds, MaximumTimedMilliseconds);
    }

    /// <summary>Whether the row may be converted at the given instant.</summary>
    /// <param name="row">The projected character-bag row of the target.</param>
    /// <param name="at">The instant sampled under the provider's locks.</param>
    public bool Admits(InventorySlotRecord row, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.ItemId != TargetItemId || row.Bag != InventoryBag.CharacterBag || row.Quantity <= 0
            || row.ArticleIdentity is not null || row.PetIdentity is not null || row.Pet is not null
            || (row.Flags & ForbiddenFlags) != 0
            || row.Lifetime.Kind != InventoryLifetimeKind.ExpiresAt || row.Lifetime.ExpiresAtUnixMilliseconds is not { } deadline)
            return false;
        var remaining = deadline - at.ToUnixTimeMilliseconds();
        return remaining > 0 && remaining + ToleranceMilliseconds >= MinimumRemainingMilliseconds
            && remaining + ToleranceMilliseconds < MaximumTimedMilliseconds + 86_400_000;
    }
}
