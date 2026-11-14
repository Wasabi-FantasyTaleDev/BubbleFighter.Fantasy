using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;

namespace BubbleFighter.Fantasy.Contracts.Enhancement;

/// <summary>
/// A committed permanent conversion, sent after the complete inventory that installs it. The native reader consumes only
/// the ordered references of one vector: an individual item is its one Article key, and a set is its native set identity
/// followed by one converted member's Article key.
/// </summary>
/// <param name="Keys">The ordered references the client resolves against the inventory published first.</param>
public sealed record LabConversionCompleted(IReadOnlyList<ulong> Keys) : IMessage
{
    /// <summary>Creates the reply of an individual conversion, which carries the converted item's Article key.</summary>
    /// <param name="target">The converted item's native Article key.</param>
    public LabConversionCompleted(ulong target) : this([target])
    {
    }

    /// <summary>Creates the reply of a set conversion.</summary>
    /// <param name="setId">The native set identity the client requested.</param>
    /// <param name="member">The Article key of one member the commit converted; the client checks membership in the owned set.</param>
    public static LabConversionCompleted ForSet(int setId, ulong member)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(setId);
        return new([(ulong)setId, member]);
    }
}
