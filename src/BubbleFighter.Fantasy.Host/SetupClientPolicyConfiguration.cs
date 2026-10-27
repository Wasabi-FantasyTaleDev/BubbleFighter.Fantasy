using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Launcher;

namespace BubbleFighter.Fantasy.Host;

/// <summary>Applies the saved client policy to the composed Launcher rows while preserving all other desired configuration.</summary>
internal static class SetupClientPolicyConfiguration
{
    internal static IReadOnlyList<PluginRow> Apply(IReadOnlyList<PluginRow> rows, ClientPolicy? policy)
    {
        if (policy is null) return rows;
        ClientPolicyRules.Validate(policy);
        if (!rows.Any(row => row.Plugin == "launcher.http"))
            throw new ConfigException("A client policy requires a launcher.http row in the selected profile.");
        return rows.Select(row =>
        {
            if (row.Plugin != "launcher.http") return row;
            Dictionary<string, object?> config = row.Config is { } original ? new(original) : [];
            config["client_policy"] = ToConfig(policy);
            return row with { Config = config };
        }).ToArray();
    }

    // The row binder reads snake_case keys; absent optional fields are left out rather than written as null.
    internal static Dictionary<string, object?> ToConfig(ClientPolicy policy) => new()
    {
        ["sources"] = policy.Sources.Select(source => (object?)new Dictionary<string, object?>
        {
            ["name"] = source.Name, ["kind"] = source.Kind, ["base_url"] = source.BaseUrl, ["catalog_path"] = source.CatalogPath,
        }).ToList(),
        ["plugins"] = policy.Plugins.Select(plugin =>
        {
            var entry = new Dictionary<string, object?> { ["id"] = plugin.Id };
            if (plugin.Name is not null) entry["name"] = plugin.Name;
            if (plugin.Description is not null) entry["description"] = plugin.Description;
            entry["rules"] = plugin.Rules.Select(rule =>
            {
                var item = new Dictionary<string, object?> { ["id"] = rule.Id, ["type"] = rule.Type, ["builds"] = rule.Builds.ToArray() };
                if (rule.Version is not null) item["version"] = rule.Version;
                if (rule.Sources is not null) item["sources"] = rule.Sources.ToArray();
                return (object?)item;
            }).ToList();
            return (object?)entry;
        }).ToList(),
    };
}
