using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.Host;

/// <summary>
/// Makes the account that claimed the installation the WebUI administrator of the profile it starts. Packaged profiles
/// list no permission subjects, so without this the only account a setup creates could sign in but use nothing that
/// checks a permission.
/// </summary>
internal static class SetupAdministratorPermissions
{
    internal static IReadOnlyList<PluginRow> Apply(IReadOnlyList<PluginRow> rows, string administrator)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(administrator);
        return rows.Select(row =>
        {
            if (row.Plugin != "permissions" || row.Disabled) return row;
            Dictionary<string, object?> config = row.Config is { } original ? new(original) : [];
            var subjects = config.TryGetValue("subjects", out var listed) && listed is IEnumerable<object?> existing ? existing.ToList() : [];
            // An operator who configured this account keeps their rules; the grant only fills the gap a fresh setup leaves.
            if (subjects.OfType<IReadOnlyDictionary<string, object?>>().Any(subject =>
                    Equals(subject.GetValueOrDefault("kind"), "webui") && Equals(subject.GetValueOrDefault("id"), administrator)))
                return row;
            subjects.Add(new Dictionary<string, object?> { ["kind"] = "webui", ["id"] = administrator, ["allow"] = new List<object?> { "webui.admin" } });
            config["subjects"] = subjects;
            return row with { Config = config };
        }).ToArray();
    }
}
