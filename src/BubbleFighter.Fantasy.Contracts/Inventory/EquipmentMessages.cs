using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.Contracts.Inventory;

/// <summary>Requests the protocol's normalized equipped inventory before installing a player scene.</summary>
/// <param name="Protocol">The game dialect selecting the equipment provider.</param>
/// <param name="UserNum">The authenticated player owning the inventory.</param>
public sealed record EquipmentSelectionReadRequested(string Protocol, long UserNum) : IEvent;

/// <summary>Publishes committed appearance during a scene transition without coupling the scene codec to the view provider's activation.</summary>
/// <param name="Protocol">The game dialect selecting the equipment provider.</param>
/// <param name="Session">The recipient of the appearance update.</param>
/// <param name="UserNum">The player whose appearance is published.</param>
/// <param name="CharacterItemId">The committed character item.</param>
/// <param name="EquippedItemIds">The committed worn item identities.</param>
/// <param name="Remote">Whether the appearance belongs to another player rather than the recipient.</param>
public sealed record EquipmentViewRequested(string Protocol, ISession Session, int UserNum, int CharacterItemId,
    IReadOnlyList<int> EquippedItemIds, bool Remote) : IEvent;

/// <summary>Requests a worn-state change with an opaque client reference and a codec-resolved inventory target.</summary>
/// <param name="Reference">The original client reference retained for correlation; it is not a persistence slot key.</param>
/// <param name="ItemId">The supported positive bag item id, or null when a valid request names an unsupported target.</param>
/// <param name="Worn">True to equip the target, false to remove it.</param>
public sealed record EquipmentChangeRequested(long Reference, int? ItemId, bool Worn) : IMessage;

/// <summary>Reports the result of one equipment request without prescribing a build-specific result or echo layout.</summary>
/// <param name="Request">The exact request being answered, including its original client reference.</param>
/// <param name="Succeeded">Whether the configured equipment operation accepted the request.</param>
public sealed record EquipmentChangeReplied(EquipmentChangeRequested Request, bool Succeeded) : IMessage;

/// <summary>Runs before a persisted equipment selection changes storage and allows an owner to deny or retarget it.</summary>
/// <param name="Protocol">The equipment provider's game protocol.</param>
/// <param name="UserNum">The player whose inventory will be changed.</param>
/// <param name="ItemId">The inventory item selected by the client after dialect validation.</param>
/// <param name="Worn">Whether the item is being equipped or removed.</param>
public sealed record EquipmentSelectionApplying(string Protocol, long UserNum, int ItemId, bool Worn) : IEvent;

/// <summary>Decision returned by the precommit equipment selection pipeline.</summary>
/// <param name="Allowed">Whether the selection may proceed to persistence.</param>
/// <param name="ItemId">The item id to persist when allowed; middleware may replace the requested item.</param>
/// <param name="Worn">The worn flag to persist when allowed.</param>
/// <param name="Reason">An optional diagnostic reason recorded by the caller when the operation is denied.</param>
public sealed record EquipmentSelectionDecision(bool Allowed, int ItemId, bool Worn, string? Reason = null);

/// <summary>Declares when a dialect's equipment acknowledgement may repaint the stored inventory.</summary>
public interface IEquipmentReplyPolicy
{
    /// <summary>Gets whether a successful acknowledgement must follow the inventory and equipped-view updates.</summary>
    bool ReplyAfterInventory { get; }
}
