using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Kernel;

namespace BubbleFighter.Fantasy.Host;

/// <summary>Composes ordinary bootstrap rows and binds explicit application inputs without routing by plugin identity.</summary>
internal static class BootstrapConfiguration
{
    private const string InputPrefix = "@bootstrap:";

    /// <summary>Expands top-level configuration input references after the ordinary profile has been validated and composed.</summary>
    /// <param name="profilesDirectory">The bootstrap profile root containing bundles and profile folders.</param>
    /// <param name="profile">The selected bootstrap profile directory name.</param>
    /// <param name="inputs">Application-owned values available to explicitly configured input references.</param>
    /// <returns>Rows with input values substituted; the host snapshots them on admission.</returns>
    public static IReadOnlyList<PluginRow> Compose(string profilesDirectory, string profile,
        IReadOnlyDictionary<string, object?> inputs)
    {
        var rows = new ProfileComposer(profilesDirectory).Compose(profile, []);
        return rows.Select(row => row.Config is null ? row : row with
        {
            Config = row.Config.ToDictionary(pair => pair.Key, pair => Bind(pair.Value, row.Id, inputs), StringComparer.Ordinal),
        }).ToArray();
    }

    private static object? Bind(object? value, string rowId, IReadOnlyDictionary<string, object?> inputs)
    {
        if (value is not string text || !text.StartsWith(InputPrefix, StringComparison.Ordinal))
            return value;
        var name = text[InputPrefix.Length..];
        return inputs.TryGetValue(name, out var input) ? input
            : throw new ConfigException($"Bootstrap row '{rowId}' references unknown application input '{name}'.");
    }
}
