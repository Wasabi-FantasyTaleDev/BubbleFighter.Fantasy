using BubbleFighter.Fantasy.Contracts.Inventory;

namespace BubbleFighter.Fantasy.Dialect.Messages;

// kr-812 sub_977960 establishes the full-inventory time origin; kr-812 sub_978920 subtracts elapsed OLE days
// (packets/inventory.yaml InventorySlot.remaining_days). Other full/update paths: cn-717 sub_A20E30/sub_A209D0,
// id-400 sub_8E9F50/sub_8EA090, cn-324 sub_7FD780/sub_7FD880, cn-279 sub_664D20/sub_664320. Updates preserve the origin.
internal static class InventoryTime
{
    internal static double ExpiringDaysAtBaseline(InventoryLifetime lifetime, DateTimeOffset baseline)
    {
        if (lifetime.Kind != InventoryLifetimeKind.ExpiresAt)
            throw new ArgumentException("A relative expiry requires an established UTC deadline.", nameof(lifetime));
        var expiry = DateTimeOffset.FromUnixTimeMilliseconds(lifetime.ExpiresAtUnixMilliseconds!.Value);
        var days = (expiry - baseline).TotalDays;
        // Day computation: kr-812 sub_4C7710, cn-717 sub_A221E0, id-400 sub_45F690, cn-324 sub_452E20, cn-279 sub_4624D0.
        // The bound is the common signed day range the audited timer paths accept (kr-812-binary audit;
        // not re-derived from the tables or the kr-1256 export). Clamping it would change the entitlement.
        if (days is < -3615897 or > 3615897)
            throw new ArgumentOutOfRangeException(nameof(lifetime), "The deadline exceeds the client's day range.");
        return days;
    }
}
