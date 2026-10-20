using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Inventory;

/// <summary>Requests removal of the selected expired inventory item rather than use of its contents.</summary>
/// <param name="ItemId">The native item identity echoed by the action result.</param>
public sealed record InventoryThrowRequested(int ItemId) : IMessage;

/// <summary>Returns the selected identity and the native expired-item removal result.</summary>
/// <param name="ItemId">The unchanged identity from the request.</param>
/// <param name="Result">Zero removes the identity locally; one refuses the expired-item action.</param>
public sealed record InventoryThrowReplied(int ItemId, int Result) : IMessage;
