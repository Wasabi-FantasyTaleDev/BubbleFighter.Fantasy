namespace BubbleFighter.Fantasy.Abstractions.Protocol;

/// <summary>Allows an existing codec's missing named opcode to use an explicitly logged research candidate without changing the runtime table.</summary>
public interface IOpcodeNameFallback
{
    /// <summary>Resolves a missing name in this table's direction and connection scope, or returns null when no unique candidate exists.</summary>
    /// <param name="direction">Direction requested by an existing codec.</param>
    /// <param name="name">Exact packet name requested by that codec.</param>
    /// <returns>The candidate value, or null if the catalog has no candidate.</returns>
    ushort? ResolveMissingName(PacketDirection direction, string name);
}
