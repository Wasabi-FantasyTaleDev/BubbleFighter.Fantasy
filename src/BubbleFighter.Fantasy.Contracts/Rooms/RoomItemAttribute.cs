namespace BubbleFighter.Fantasy.Contracts.Rooms;

/// <summary>Retains one worn item's native modifier without assigning meanings to its unproven values.</summary>
/// <param name="ItemId">The worn item the modifier belongs to.</param>
/// <param name="Kind">The native modifier selector.</param>
/// <param name="Values">The modifier's ordered words, preserving signed and floating-point bit patterns.</param>
/// <param name="Unknown28">The trailing native word whose interpretation depends on the modifier kind.</param>
/// <param name="Unknown32">The trailing native text, retained verbatim rather than interpreted by room logic.</param>
public sealed record RoomItemAttribute(int ItemId, uint Kind, IReadOnlyList<uint> Values, uint Unknown28, string Unknown32);
