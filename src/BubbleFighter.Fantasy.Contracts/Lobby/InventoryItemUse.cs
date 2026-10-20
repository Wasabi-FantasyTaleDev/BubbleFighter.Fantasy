using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Lobby;

/// <summary>Requests use of an inventory consumable with the client's action parameters.</summary>
public sealed record InventoryItemUseRequested(int ItemId, int Quantity, string Parameters) : IMessage;

/// <summary>Returns the native use result and action parameters consumed by the inventory dialog.</summary>
public sealed record InventoryItemUseReplied(int Result, string Parameters) : IMessage
{
    /// <summary>Gets the consumed item whose native inventory action determines the result dialog.</summary>
    public int ItemId { get; init; }
}

/// <summary>Displays a server announcement through the client's native notification area.</summary>
public sealed record ServerNotice(string Text) : IMessage;

/// <summary>Reports a committed nickname change to scene membership owners.</summary>
public sealed record PlayerNicknameChanged(int UserNum, string Nickname) : IEvent;
