using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Inventory;

/// <summary>Reports a completed local equipment-view publication using the exact snapshot passed to its dialect.</summary>
/// <param name="Protocol">The game protocol that published the view and its associated preset frames.</param>
/// <param name="Session">The recipient owning the published equipment.</param>
/// <param name="View">The immutable equipment and preset snapshot supplied to the dialect sender.</param>
/// <param name="SelectionChange">Whether the caller published an accepted selection or preset change.</param>
/// <remarks>Completion means the sender accepted every frame; it does not establish client receipt or application.</remarks>
public sealed record EquipmentViewPublished(string Protocol, ISession Session, LocalEquipmentViewUpdated View,
    bool SelectionChange) : IEvent
{
    /// <summary>
    /// Gets whether this publication replays a supplied room, plaza or login snapshot already applied by its owner,
    /// so completion does not request another equipment-state reconciliation.
    /// </summary>
    public bool IsSceneReplay { get; init; }
}
