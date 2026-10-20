using System.Globalization;
using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.Packets;

/// <summary>Applies a structure's menu annotations to named source values without duplicating its layout.</summary>
public static class PacketMenuProjection
{
    /// <summary>Projects one structure scope, preserving supplied nested maps and lists for its encoder.</summary>
    /// <param name="tables">The audited packet tables.</param>
    /// <param name="build">The native build whose fields and menu annotations are resolved.</param>
    /// <param name="structure">The structure to project.</param>
    /// <param name="sources">Named values before menu overrides, including any separately projected nested scopes.</param>
    /// <returns>Values by field name; absent values retain their packet-table defaults.</returns>
    /// <exception cref="ConfigException">A menu reference is absent or an empty annotation targets an incompatible type.</exception>
    public static Dictionary<string, object?> Project(PacketTables tables, string build, string structure,
        IReadOnlyDictionary<string, object?> sources)
    {
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentNullException.ThrowIfNull(sources);
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var field in tables.Resolve(build, structure))
        {
            if (field.Menu is null)
            {
                if (sources.TryGetValue(field.Name, out var value))
                    result.Add(field.Name, value);
            }
            else if (long.TryParse(field.Menu, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                result.Add(field.Name, number);
            else if (field.Menu == "empty")
            {
                if (field.Type == "wstr")
                    result.Add(field.Name, "");
                else if (field.Type == "empty_list" || field.Type.StartsWith("list<", StringComparison.Ordinal))
                    result.Add(field.Name, Array.Empty<object>());
                else
                    throw new ConfigException($"{structure}.{field.Name} cannot carry an empty menu value as {field.Type}.");
            }
            else if (sources.TryGetValue(field.Menu, out var reference) && reference is not null)
                result.Add(field.Name, reference);
            else
                throw new ConfigException($"{structure}.{field.Name} requires menu source '{field.Menu}'.");
        }
        return result;
    }
}
