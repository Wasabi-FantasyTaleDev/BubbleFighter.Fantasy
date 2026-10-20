using BubbleFighter.Fantasy.Contracts.Inventory;

namespace BubbleFighter.Fantasy.Dialect.Messages;

// 0x977960 establishes the full-inventory time origin; 0x978920 subtracts elapsed OLE days.
// Other full/update paths: CN717 0xA20E30/0xA209D0, ID400 0x8E9F50/0x8EA090,
// CN324 0x7FD780/0x7FD880, CN279 0x664D20/0x664320. Updates preserve the origin.
internal static class InventoryTime
{
    internal static double ExpiringDaysAtBaseline(InventoryLifetime lifetime, DateTimeOffset baseline)
    {
        if (lifetime.Kind != InventoryLifetimeKind.ExpiresAt)
            throw new ArgumentException("A relative expiry requires an established UTC deadline.", nameof(lifetime));
        var expiry = DateTimeOffset.FromUnixTimeMilliseconds(lifetime.ExpiresAtUnixMilliseconds!.Value);
        var days = (expiry - baseline).TotalDays;
        // KR812 0x4C7710, CN717 0xA221E0, ID400 0x45F690, CN324 0x452E20, CN279 0x4624D0.
        // Clamping the common signed day range would change the entitlement.
        if (days is < -3615897 or > 3615897)
            throw new ArgumentOutOfRangeException(nameof(lifetime), "The deadline exceeds the client's day range.");
        return days;
    }
}
