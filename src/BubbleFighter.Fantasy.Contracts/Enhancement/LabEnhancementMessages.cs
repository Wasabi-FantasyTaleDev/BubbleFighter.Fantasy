using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Party;

namespace BubbleFighter.Fantasy.Contracts.Enhancement;

/// <summary>Refuses a laboratory operation using the recipient's audited unavailable result.</summary>
public sealed record LabOperationRefused(LabOperation Operation) : IMessage;

/// <summary>Presents a committed enchant attempt after the client has received the resulting Article state.</summary>
/// <param name="Succeeded">Whether the transaction added the enhancement.</param>
/// <param name="Targets">Native Article references in the order selected by the client.</param>
public sealed record LabEnchantCompleted(bool Succeeded, IReadOnlyList<ulong> Targets) : IMessage;
