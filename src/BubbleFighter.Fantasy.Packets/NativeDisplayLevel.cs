using System.Globalization;
using System.Runtime.CompilerServices;
using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.Packets;

/// <summary>Bounds native rank projections by the build's own LevelMark sprite count without changing saved progress.</summary>
public static class NativeDisplayLevel
{
    private static readonly ConditionalWeakTable<PacketTables, Dictionary<string, int>> Limits = new();

    /// <summary>Returns a positive rank that the selected build's native sprite resource can display.</summary>
    /// <param name="tables">The immutable packet and build metadata snapshot.</param>
    /// <param name="build">The build whose declared native display limit applies.</param>
    /// <param name="level">The saved player level, which is not modified.</param>
    /// <returns>The saved level bounded to one through the build's display limit.</returns>
    /// <exception cref="ConfigException">A build does not declare a valid positive display limit.</exception>
    public static int Project(PacketTables tables, string build, int level)
    {
        var limits = Limits.GetValue(tables, static snapshot => snapshot.Builds.ToDictionary(name => name, name =>
        {
            var values = snapshot.ValuesOf(name);
            if (!values.TryGetValue("display_level_max", out var raw)
                || raw is not string text || !int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var limit)
                || limit < 1)
                throw new ConfigException($"Build '{name}' requires a positive display_level_max from its native rank resources.");
            return limit;
        }, StringComparer.Ordinal));
        // cn-279 sub_779CC0 uses level - 1 directly as a frame index; out-of-range ranks draw no icon.
        return Math.Clamp(level, 1, limits[build]);
    }
}
