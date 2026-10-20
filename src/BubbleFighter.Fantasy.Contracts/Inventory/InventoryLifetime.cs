namespace BubbleFighter.Fantasy.Contracts.Inventory;

/// <summary>Distinguishes an unclassified historical grant from a permanent or expiring entitlement.</summary>
public enum InventoryLifetimeKind
{
    /// <summary>The grant predates lifetime tracking or its catalog semantics have not been established.</summary>
    Unspecified = 0,

    /// <summary>The catalog grants ownership without an expiry deadline.</summary>
    Permanent = 1,

    /// <summary>The entitlement ends at its stored UTC deadline, including while the player is offline.</summary>
    ExpiresAt = 2,

    /// <summary>
    /// The item has no time limit and a life count instead: the row's quantity is the remaining life, which the
    /// daily login rule lowers (<see cref="InventoryLife"/>). A row at zero life is unusable.
    /// </summary>
    Life = 3,
}

/// <summary>
/// Durable entitlement lifetime, independent of a client's relative timer or inventory identity.
/// An unspecified lifetime must not be converted into a permanent grant by a dialect.
/// </summary>
public readonly record struct InventoryLifetime
{
    /// <summary>Creates a lifetime, rejecting unknown kinds and inconsistent or out-of-range deadlines.</summary>
    /// <param name="kind">The established entitlement lifetime.</param>
    /// <param name="expiresAtUnixMilliseconds">UTC deadline in Unix milliseconds, required only for ExpiresAt.</param>
    public InventoryLifetime(InventoryLifetimeKind kind, long? expiresAtUnixMilliseconds = null)
    {
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));
        if ((kind == InventoryLifetimeKind.ExpiresAt) != expiresAtUnixMilliseconds.HasValue)
            throw new ArgumentException("Only an expiring entitlement must have a deadline.", nameof(expiresAtUnixMilliseconds));
        if (expiresAtUnixMilliseconds is { } deadline)
            _ = DateTimeOffset.FromUnixTimeMilliseconds(deadline);
        Kind = kind;
        ExpiresAtUnixMilliseconds = expiresAtUnixMilliseconds;
    }

    /// <summary>Gets the classification; the default value is explicitly unspecified.</summary>
    public InventoryLifetimeKind Kind { get; }

    /// <summary>Gets the persisted UTC deadline, without depending on database or machine time zones.</summary>
    public long? ExpiresAtUnixMilliseconds { get; }

    /// <summary>Gets an explicitly permanent entitlement.</summary>
    public static InventoryLifetime Permanent => new(InventoryLifetimeKind.Permanent);

    /// <summary>Gets a life-counted entitlement; the remaining life is the row's quantity.</summary>
    public static InventoryLifetime Life => new(InventoryLifetimeKind.Life);

    /// <summary>Creates an expiring entitlement, normalizing the supplied instant to Unix millisecond precision.</summary>
    /// <param name="deadline">The absolute end of ownership, including any offline time.</param>
    public static InventoryLifetime Until(DateTimeOffset deadline) =>
        new(InventoryLifetimeKind.ExpiresAt, deadline.ToUnixTimeMilliseconds());
}
