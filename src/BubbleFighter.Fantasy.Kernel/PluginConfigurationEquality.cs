using System.Collections;

namespace BubbleFighter.Fantasy.Kernel;

/// <summary>Compares detached configuration snapshots without coercing scalar representations.</summary>
internal static class PluginConfigurationEquality
{
    internal static bool Equivalent(IReadOnlyDictionary<string, object?>? left, IReadOnlyDictionary<string, object?>? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null || left.Count != right.Count) return false;
        // Snapshot capture fixes every mapping's comparer to ordinal before comparison.
        foreach (var (key, value) in left)
            if (!right.TryGetValue(key, out var other) || !EquivalentValue(value, other)) return false;
        return true;
    }

    private static bool EquivalentValue(object? left, object? right)
    {
        if (left is null || right is null) return left is null && right is null;
        if (left is IReadOnlyDictionary<string, object?> map)
            return right is IReadOnlyDictionary<string, object?> otherMap && Equivalent(map, otherMap);
        if (left is IList sequence)
        {
            if (right is not IList otherSequence || sequence.Count != otherSequence.Count) return false;
            for (var index = 0; index < sequence.Count; index++)
                if (!EquivalentValue(sequence[index], otherSequence[index])) return false;
            return true;
        }
        if (left.GetType() != right.GetType()) return false;
        return left switch
        {
            DateTime value => right is DateTime other && value.Ticks == other.Ticks && value.Kind == other.Kind,
            DateTimeOffset value => right is DateTimeOffset other && value.Ticks == other.Ticks && value.Offset == other.Offset,
            string or bool or char or sbyte or byte or short or ushort or int or uint or long or ulong or nint or nuint
                or Int128 or UInt128 or float or double or decimal or TimeSpan or Guid => left.Equals(right),
            _ => throw new InvalidOperationException("Only captured configuration values may be compared."),
        };
    }
}
