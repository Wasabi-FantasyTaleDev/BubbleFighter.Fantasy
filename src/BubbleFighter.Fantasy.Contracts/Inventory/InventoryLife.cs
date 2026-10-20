namespace BubbleFighter.Fantasy.Contracts.Inventory;

/// <summary>
/// The daily life rule of life-counted items (<see cref="InventoryLifetimeKind.Life"/>), shared by every store:
/// each whole UTC day the player did not log in costs one life, and life never goes below zero.
/// </summary>
public static class InventoryLife
{
    /// <summary>Returns the whole days that passed between the stored last-login day and <paramref name="today"/>.</summary>
    /// <param name="lastLogin">The stored last-login day, or null on the first login ever (nothing is charged).</param>
    /// <param name="today">The current UTC day.</param>
    /// <returns>Zero for a first login, the same day or a clock that went back; otherwise the day difference.</returns>
    public static int DaysMissed(DateOnly? lastLogin, DateOnly today) =>
        lastLogin is { } last && today > last ? today.DayNumber - last.DayNumber : 0;

    /// <summary>Returns the life left after charging <paramref name="days"/> days.</summary>
    /// <param name="life">The stored life (a row's quantity).</param>
    /// <param name="days">The whole days to charge; not negative.</param>
    /// <returns>The life minus the days, never below zero.</returns>
    public static int Reduce(int life, int days)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(days);
        return (int)Math.Max(0L, (long)life - days);
    }

    /// <summary>Returns the day to store after a login: <paramref name="today"/>, but never earlier than the stored day.</summary>
    /// <param name="lastLogin">The stored last-login day, or null on the first login ever.</param>
    /// <param name="today">The current UTC day.</param>
    /// <returns>The later of the two days, so a clock that went back cannot charge the same day twice.</returns>
    public static DateOnly NextLoginDay(DateOnly? lastLogin, DateOnly today) =>
        lastLogin is { } last && last > today ? last : today;
}
