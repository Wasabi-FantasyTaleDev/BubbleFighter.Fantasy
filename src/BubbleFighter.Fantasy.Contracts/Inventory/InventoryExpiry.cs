namespace BubbleFighter.Fantasy.Contracts.Inventory;

/// <summary>
/// The one rule for when a bag row has ended, shared by every store and sweep: a time-limited row whose
/// deadline passed, or a life-counted row whose life ran out.
/// </summary>
public static class InventoryExpiry
{
    /// <summary>Returns whether the row has ended: its deadline is at or before <paramref name="now"/>, or its life is used up.</summary>
    /// <param name="slot">The bag row.</param>
    /// <param name="now">The instant to compare against.</param>
    /// <returns>
    /// True for an <see cref="InventoryLifetimeKind.ExpiresAt"/> row whose deadline has passed and for an
    /// <see cref="InventoryLifetimeKind.Life"/> row whose quantity (its remaining life) is zero or less.
    /// </returns>
    public static bool IsExpired(InventorySlotRecord slot, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(slot);
        return slot.Lifetime.Kind switch
        {
            InventoryLifetimeKind.ExpiresAt => slot.Lifetime.ExpiresAtUnixMilliseconds is { } deadline
                && deadline <= now.ToUnixTimeMilliseconds(),
            InventoryLifetimeKind.Life => slot.Quantity <= 0,
            _ => false,
        };
    }
}
