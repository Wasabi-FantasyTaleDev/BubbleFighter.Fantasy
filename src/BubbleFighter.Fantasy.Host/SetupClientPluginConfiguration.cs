using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Launcher;

namespace BubbleFighter.Fantasy.Host;

/// <summary>Applies saved client-plugin rules to the composed Launcher rows while preserving all other desired configuration.</summary>
internal static class SetupClientPluginConfiguration
{
    internal static IReadOnlyList<PluginRow> Apply(IReadOnlyList<PluginRow> rows, IReadOnlyList<ClientPluginPolicy>? policies)
    {
        if (policies is null) return rows;
        ClientPluginPolicyRules.Validate(policies);
        if (!rows.Any(row => row.Plugin == "launcher.http"))
            throw new ConfigException("Client-plugin policies require a launcher.http row in the selected profile.");
        return rows.Select(row =>
        {
            if (row.Plugin != "launcher.http") return row;
            Dictionary<string, object?> config = row.Config is { } original ? new(original) : [];
            config["plugin_recommendations"] = policies.Select(policy => (object?)new Dictionary<string, object?>
            {
                ["id"] = policy.Id, ["builds"] = policy.Builds.ToArray(),
                ["required"] = policy.Required, ["disabled"] = policy.Disabled,
            }).ToList();
            return row with { Config = config };
        }).ToArray();
    }
}
