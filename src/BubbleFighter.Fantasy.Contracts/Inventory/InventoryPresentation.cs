using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Rooms;

namespace BubbleFighter.Fantasy.Contracts.Inventory;

/// <summary>Requests feature-owned attributes and a display timer for one committed inventory item.</summary>
/// <param name="Protocol">The client dialect whose native attribute catalog applies.</param>
/// <param name="Slot">The immutable inventory snapshot being published.</param>
public sealed record InventoryPresentationRequested(string Protocol, InventorySlotRecord Slot) : IEvent;

/// <summary>Native item modifiers and an optional display deadline independent of ownership expiration.</summary>
/// <param name="Attributes">ItemAttr records supplied by the feature that owns their semantics.</param>
/// <param name="RemainingUntil">A feature display deadline, or null to display the ownership lifetime.</param>
public sealed record InventoryItemPresentation(IReadOnlyList<RoomItemAttribute> Attributes, DateTimeOffset? RemainingUntil)
{
    /// <summary>Gets the presentation of an item without feature-specific attributes or a display timer.</summary>
    public static InventoryItemPresentation Empty { get; } = new([], null);
}

/// <summary>Collects feature-owned modifiers from an already resolved inventory snapshot.</summary>
public static class InventoryPresentations
{
    /// <summary>Copies modifiers for the supplied worn identities without rereading or changing equipment selections.</summary>
    public static async ValueTask<IReadOnlyList<RoomItemAttribute>> EquippedAsync(IEventBus events, string protocol,
        IReadOnlyList<InventorySlotRecord> rows, IReadOnlyList<int> equippedIds, CancellationToken ct)
    {
        if (rows.Count > InventoryLimits.MaxInventorySlotsPerBag || equippedIds.Count > InventoryLimits.MaxInventorySlotsPerBag)
            throw new InvalidDataException("Equipment presentation exceeds the inventory bound.");
        var selected = equippedIds.ToHashSet();
        var result = new List<RoomItemAttribute>();
        foreach (var row in rows.Where(row => selected.Contains(row.ItemId)))
        {
            var presentation = await events.WaterfallAsync<InventoryPresentationRequested, InventoryItemPresentation>(
                new(protocol, row), _ => ValueTask.FromResult(InventoryItemPresentation.Empty), ct).ConfigureAwait(false);
            foreach (var attribute in presentation.Attributes)
            {
                if (attribute.ItemId != row.ItemId || attribute.Values.Count > InventoryLimits.MaxInventorySlotsPerBag
                    || result.Count >= InventoryLimits.MaxInventorySlotsPerBag)
                    throw new InvalidDataException("An item presentation contains unrelated or excessive modifiers.");
                result.Add(attribute with { Values = Array.AsReadOnly(attribute.Values.ToArray()) });
            }
        }
        return result.AsReadOnly();
    }
}
