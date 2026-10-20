using System.Text.RegularExpressions;
using BubbleFighter.Fantasy.Abstractions;
using YamlDotNet.RepresentationModel;

namespace BubbleFighter.Fantasy.Kernel;

/// <summary>Summary information about a discoverable profile.</summary>
/// <param name="Id">The profile id (its directory name).</param>
/// <param name="Description">The profile's description, when declared.</param>
public sealed record ProfileInfo(string Id, string? Description)
{
    /// <summary>Gets locale-keyed names from profile data for version selectors, independent of the configuration id.</summary>
    public IReadOnlyDictionary<string, string> DisplayNames { get; init; } = new Dictionary<string, string>();

    /// <summary>Gets whether the profile declares a client build and can be selected as an individual game version.</summary>
    public bool IsClientBuild { get; init; }

    /// <summary>Gets whether the setup assistant allows this packaged build to be selected.</summary>
    public bool SetupEnabled { get; init; } = true;

    /// <summary>Gets whether this profile may be activated by a composed runtime.</summary>
    public bool ActivationEnabled { get; init; } = true;

    /// <summary>Gets whether the profile is an experimental client integration.</summary>
    public bool Experimental { get; init; }
}

/// <summary>
/// Composes a profile's plugin rows from configuration layers, in order:
/// <c>profiles/&lt;id&gt;/profile.yaml</c> lists bundle names and patch files; each bundle
/// <c>profiles/bundles/&lt;name&gt;.yaml</c> contributes rows; each named group
/// <c>profiles/groups/&lt;name&gt;.yaml</c> then composes further bundles, nested groups and
/// inline rows depth-first; each patch then applies replace/insert/remove operations; extra
/// patches from the command line apply last; string scalars are env-interpolated
/// (<c>${VAR}</c>, <c>${VAR:-default}</c>) at the end.
/// </summary>
public sealed partial class ProfileComposer
{
    /// <summary>
    /// Patch files whose names end with this suffix are optional: they are silently skipped
    /// when absent, so developers can keep git-ignored local overrides.
    /// </summary>
    public const string OptionalPatchSuffix = "local.patch.yaml";

    // Input-validation bound for recursive group composition (cycles are rejected by name before
    // this fires); sized like the 256-entry Overrides bound rather than as a deployable tunable.
    private const int MaxGroupDepth = 16;

    // Acyclic group graphs may still re-expand shared groups (a diamond is legal); this caps the
    // total number of group file loads per composition so fan-out cannot explode a local profile.
    private const int MaxGroupExpansions = 256;

    private static readonly Regex EnvPattern = EnvRegex();

    // The variables of the profile being composed: its id, its client build and its own `vars`.
    // Composition is synchronous and re-entered once per listed build, so the scope is saved and
    // restored around each profile.
    [ThreadStatic]
    private static IReadOnlyDictionary<string, string>? t_variables;

    // The deployment variable source of the composition in progress, scoped like t_variables.
    [ThreadStatic]
    private static Func<string, string?>? t_environment;

    [ThreadStatic]
    private static bool t_unsetReadsEmpty;

    private readonly string _profilesDir;
    private readonly Func<string, string?> _environment;
    private readonly bool _unsetReadsEmpty;

    /// <summary>Creates a composer that resolves deployment variables from the process environment.</summary>
    /// <param name="profilesDir">Absolute path of the profiles directory.</param>
    public ProfileComposer(string profilesDir) : this(profilesDir, Environment.GetEnvironmentVariable) { }

    /// <summary>
    /// Creates a composer that resolves deployment variables through <paramref name="environment"/>. Offline
    /// tools that read only a build's data rows use it to compose a profile without the server's secrets.
    /// </summary>
    /// <param name="profilesDir">Absolute path of the profiles directory.</param>
    /// <param name="environment">Returns a deployment variable's value, or <see langword="null"/> when it is unset.</param>
    /// <param name="unsetReadsEmpty">
    /// When <see langword="true"/>, an unset variable without a default reads as empty instead of failing; a
    /// default still applies to an unset variable. Only offline tools that ignore the affected rows set it.
    /// </param>
    public ProfileComposer(string profilesDir, Func<string, string?> environment, bool unsetReadsEmpty = false)
    {
        _profilesDir = profilesDir;
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _unsetReadsEmpty = unsetReadsEmpty;
    }

    /// <summary>Lists every profile under the profiles directory.</summary>
    /// <returns>Profile summaries in directory order.</returns>
    public IReadOnlyList<ProfileInfo> ListProfiles()
    {
        var result = new List<ProfileInfo>();
        if (!Directory.Exists(_profilesDir))
            return result;
        foreach (var dir in Directory.EnumerateDirectories(_profilesDir))
        {
            var file = Path.Combine(dir, "profile.yaml");
            if (!File.Exists(file))
                continue;
            var doc = LoadYaml(file) as IReadOnlyDictionary<string, object?>;
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            if (doc?.TryGetValue("display_names", out var rawNames) == true)
            {
                if (rawNames is not IReadOnlyDictionary<string, object?> labels || labels.Count > 32)
                    throw new ConfigException($"Profile '{Path.GetFileName(dir)}' display_names must be a locale-to-text mapping with at most 32 entries.");
                foreach (var (locale, value) in labels)
                {
                    if (string.IsNullOrWhiteSpace(locale) || locale.Length > 35 || value is not string label
                        || string.IsNullOrWhiteSpace(label) || label.Length > 120)
                        throw new ConfigException($"Profile '{Path.GetFileName(dir)}' display_names requires locale keys and nonempty names of at most 120 characters.");
                    names.Add(locale, label);
                }
            }
            var setupEnabled = true;
            if (doc?.TryGetValue("setup_enabled", out var rawSetupEnabled) == true)
            {
                if (rawSetupEnabled is not bool value && !(rawSetupEnabled is string text && bool.TryParse(text, out value)))
                    throw new ConfigException($"Profile '{Path.GetFileName(dir)}' setup_enabled must be a boolean.");
                setupEnabled = value;
            }
            var activationEnabled = true;
            if (doc?.TryGetValue("activation_enabled", out var rawActivationEnabled) == true)
            {
                if (rawActivationEnabled is not bool activationValue && !(rawActivationEnabled is string activationText && bool.TryParse(activationText, out activationValue)))
                    throw new ConfigException($"Profile '{Path.GetFileName(dir)}' activation_enabled must be a boolean.");
                activationEnabled = activationValue;
            }
            var experimental = false;
            if (doc?.TryGetValue("experimental", out var rawExperimental) == true)
            {
                if (rawExperimental is not bool experimentalValue && !(rawExperimental is string experimentalText && bool.TryParse(experimentalText, out experimentalValue)))
                    throw new ConfigException($"Profile '{Path.GetFileName(dir)}' experimental must be a boolean.");
                experimental = experimentalValue;
            }
            result.Add(new ProfileInfo(Path.GetFileName(dir), doc is not null && doc.TryGetValue("description", out var d) ? d?.ToString() : null)
            {
                IsClientBuild = doc?.ContainsKey("client_build") == true && !doc.ContainsKey("builds"),
                DisplayNames = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(names),
                SetupEnabled = setupEnabled,
                ActivationEnabled = activationEnabled,
                Experimental = experimental,
            });
        }
        return result;
    }

    /// <summary>
    /// Composes the effective row list for a profile.
    /// </summary>
    /// <param name="profileId">The profile id (directory name under <c>profiles/</c>).</param>
    /// <param name="extraPatches">Additional patch files applied after the profile's own
    /// patches; each must exist.</param>
    /// <returns>The composed rows in activation-layer order.</returns>
    /// <exception cref="ConfigException">Thrown on any malformed or missing file, unresolved
    /// env var, unknown patch operation, or duplicate row id.</exception>
    public IReadOnlyList<PluginRow> Compose(string profileId, IReadOnlyList<string> extraPatches)
    {
        var outer = t_variables;
        var outerEnvironment = t_environment;
        var outerUnset = t_unsetReadsEmpty;
        try
        {
            t_environment = _environment;
            t_unsetReadsEmpty = _unsetReadsEmpty;
            return ComposeScoped(profileId, extraPatches);
        }
        finally
        {
            t_variables = outer;
            t_environment = outerEnvironment;
            t_unsetReadsEmpty = outerUnset;
        }
    }

    /// <summary>
    /// Composes shared rows once and appends a selected subset of client builds using the same namespacing
    /// and parent mapping as a profile's builds list. Each build may resolve deployment values independently.
    /// </summary>
    /// <param name="sharedProfileId">The base profile containing shared services and no builds list.</param>
    /// <param name="buildIds">Unique packaged client profile ids to activate.</param>
    /// <param name="extraPatches">Additional patches for the shared profile.</param>
    /// <param name="buildEnvironment">Optional deployment lookup receiving the client profile id and variable name.</param>
    /// <returns>Shared rows followed by each selected build's scoped rows.</returns>
    public IReadOnlyList<PluginRow> ComposeBuilds(string sharedProfileId, IReadOnlyList<string> buildIds,
        IReadOnlyList<string> extraPatches, Func<string, string, string?>? buildEnvironment = null)
    {
        var available = ListProfiles().ToDictionary(p => p.Id, StringComparer.Ordinal);
        if (buildIds.Count > 256 || buildIds.Distinct(StringComparer.Ordinal).Count() != buildIds.Count ||
            buildIds.Any(id => !available.TryGetValue(id, out var profile) || !profile.IsClientBuild))
            throw new ConfigException("Selected builds must be unique packaged client profiles within the composition bound.");
        // An installation may still list a build that a later release marks as preparing; name it instead of failing vaguely.
        if (buildIds.FirstOrDefault(id => !available[id].ActivationEnabled) is { } preparing)
            throw new ConfigException($"Client profile '{preparing}' is not available in this release; remove it from the installation in Settings.");
        var baseFile = Path.Combine(_profilesDir, sharedProfileId, "profile.yaml");
        if (LoadYaml(baseFile) is IReadOnlyDictionary<string, object?> doc && doc.ContainsKey("builds"))
            throw new ConfigException("The shared profile must not declare an additional builds list.");
        var result = Compose(sharedProfileId, extraPatches).ToList();
        var ids = result.Select(row => row.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var buildId in buildIds)
        {
            var composer = buildEnvironment is null ? this : new ProfileComposer(_profilesDir,
                key => buildEnvironment(buildId, key), _unsetReadsEmpty);
            AddBuildRows(sharedProfileId, buildId, ids, result, composer);
        }
        return result;
    }

    private IReadOnlyList<PluginRow> ComposeScoped(string profileId, IReadOnlyList<string> extraPatches)
    {
        t_variables = null;
        var profileDir = Path.Combine(_profilesDir, profileId);
        var profileFile = Path.Combine(profileDir, "profile.yaml");
        if (!File.Exists(profileFile))
            throw new ConfigException($"Profile '{profileId}' not found: expected '{profileFile}'.");

        var doc = LoadYaml(profileFile) as IReadOnlyDictionary<string, object?>
            ?? throw new ConfigException($"Profile file '{profileFile}' must be a YAML mapping.");

        Abstractions.Protocol.ClientBuild? clientBuild = null;
        if (doc.TryGetValue("client_build", out var rawBuild))
        {
            if (rawBuild is not string buildText)
                throw new ConfigException($"Profile '{profileId}' client_build must be a string.");
            try { clientBuild = Abstractions.Protocol.ClientBuild.Parse((string)InterpolateScalar(buildText, profileFile, profileId)!); }
            catch (Exception ex) when (ex is FormatException or ArgumentException)
            {
                throw new ConfigException($"Profile '{profileId}' has invalid client_build: {ex.Message}");
            }
        }

        var lineage = ResolveLineage(profileId, doc, profileFile);
        var family = clientBuild is null ? null : ResolveFamily(lineage);
        t_variables = ReadVariables(profileId, doc, clientBuild, profileFile);
        var rows = new List<Row>();

        foreach (var bundleName in AsStringList(doc, "bundles", profileFile))
        {
            var bundleFile = Path.Combine(_profilesDir, "bundles", bundleName + ".yaml");
            if (!File.Exists(bundleFile))
                throw new ConfigException($"Profile '{profileId}' references bundle '{bundleName}' but '{bundleFile}' does not exist.");
            var bundleDoc = LoadYaml(bundleFile);
            if (bundleDoc is not IList<object?> list)
                throw new ConfigException($"Bundle file '{bundleFile}' must be a YAML list of rows.");
            foreach (var item in list)
                rows.Add(ParseRow(item, $"bundle:{bundleName}", bundleFile));
        }

        var expansions = 0;
        foreach (var groupName in AsStringList(doc, "groups", profileFile))
            ExpandGroup(groupName, rows, profileId, [], ref expansions);

        foreach (var patchName in AsStringList(doc, "patches", profileFile))
        {
            var patchFile = Path.Combine(profileDir, patchName);
            var optional = patchName.EndsWith(OptionalPatchSuffix, StringComparison.OrdinalIgnoreCase);
            if (!File.Exists(patchFile))
            {
                if (optional)
                    continue;
                throw new ConfigException($"Profile '{profileId}' lists patch '{patchName}' but '{patchFile}' does not exist.");
            }
            ApplyPatch(patchFile, rows, $"patch:{patchName}");
        }

        foreach (var patchFile in extraPatches)
        {
            if (!File.Exists(patchFile))
                throw new ConfigException($"Patch file '{patchFile}' does not exist.");
            ApplyPatch(patchFile, rows, $"cli:{Path.GetFileName(patchFile)}");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<PluginRow>(rows.Count);
        foreach (var row in rows)
        {
            if (!ids.Add(row.Id))
                throw new ConfigException($"Duplicate row id '{row.Id}' in profile '{profileId}'.");
            result.Add(row.ToPluginRow() with { ClientBuild = clientBuild, Lineage = lineage, Family = family });
        }
        foreach (var buildId in AsStringList(doc, "builds", profileFile))
            AddBuildRows(profileId, buildId, ids, result);
        return result;
    }

    /// <summary>
    /// Adds one client build's rows to a server profile that serves several builds in one process.
    /// Rows whose id the server profile already composed (its shared bundles) are kept once; the
    /// build's own rows keep its client build and lineage and are renamed <c>&lt;build&gt;.&lt;id&gt;</c>.
    /// </summary>
    /// <summary>
    /// The variables a profile's rows may reference as <c>${NAME}</c> ahead of the environment:
    /// <c>BFF_PROFILE_ID</c>, <c>BFF_BUILD_REGION</c> and <c>BFF_BUILD_MAJOR</c> when the profile has
    /// a client build, and the scalars of its own <c>vars</c> mapping. They let one bundle serve
    /// many builds, each profile stating only its own values.
    /// </summary>
    private static Dictionary<string, string> ReadVariables(string profileId, IReadOnlyDictionary<string, object?> doc,
        Abstractions.Protocol.ClientBuild? clientBuild, string file)
    {
        var variables = new Dictionary<string, string>(StringComparer.Ordinal) { ["BFF_PROFILE_ID"] = profileId };
        if (clientBuild is { } build)
        {
            variables["BFF_BUILD_REGION"] = build.Region;
            variables["BFF_BUILD_MAJOR"] = build.Major.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        if (!doc.TryGetValue("vars", out var raw) || raw is null)
            return variables;
        if (raw is not IReadOnlyDictionary<string, object?> map)
            throw new ConfigException($"Profile '{profileId}' vars must be a mapping of names to scalar values ('{file}').");
        foreach (var (name, value) in map)
        {
            if (!VariableName().IsMatch(name) || name.StartsWith("BFF_PROFILE_", StringComparison.Ordinal)
                || name.StartsWith("BFF_BUILD_", StringComparison.Ordinal))
                throw new ConfigException($"Profile '{profileId}' vars name '{name}' is not allowed: use letters, digits and '_', and not the reserved BFF_PROFILE_ / BFF_BUILD_ prefixes.");
            if (value is null or IReadOnlyDictionary<string, object?> or IList<object?>)
                throw new ConfigException($"Profile '{profileId}' vars '{name}' must be a scalar value.");
            // Resolve deployment environment values before these variables enter the bundle scope.
            variables[name] = InterpolateScalar(Scalar(value), file, profileId)!;
        }
        return variables;
    }

    private void AddBuildRows(string profileId, string buildId, HashSet<string> ids, List<PluginRow> result, ProfileComposer? composer = null)
    {
        var buildFile = Path.Combine(_profilesDir, buildId, "profile.yaml");
        if (!File.Exists(buildFile))
            throw new ConfigException($"Profile '{profileId}' lists build '{buildId}' but '{buildFile}' does not exist.");
        if (LoadYaml(buildFile) is IReadOnlyDictionary<string, object?> buildDoc && buildDoc.ContainsKey("builds"))
            throw new ConfigException($"Profile '{profileId}' lists build '{buildId}', which itself lists builds.");
        var shared = new HashSet<string>(ids, StringComparer.Ordinal);
        var own = (composer ?? this).Compose(buildId, []).Where(row => !shared.Contains(row.Id)).ToArray();
        var renamed = own.Select(row => row.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var row in own)
        {
            var id = buildId + "." + row.Id;
            if (!ids.Add(id))
                throw new ConfigException($"Duplicate row id '{id}' in profile '{profileId}'.");
            var parent = row.ParentId is { } p && renamed.Contains(p) ? buildId + "." + p : row.ParentId;
            result.Add(row with { Id = id, ParentId = parent });
        }
    }

    /// <summary>Follows <c>parent</c> links from a profile to its root.</summary>
    /// <param name="profileId">The profile id.</param>
    /// <returns>The profile id and its ancestors, nearest first.</returns>
    /// <exception cref="ConfigException">Thrown when a parent is missing, not a string, or the links form a cycle.</exception>
    public IReadOnlyList<string> Lineage(string profileId)
    {
        var file = Path.Combine(_profilesDir, profileId, "profile.yaml");
        if (!File.Exists(file))
            throw new ConfigException($"Profile '{profileId}' not found: expected '{file}'.");
        return ResolveLineage(profileId, LoadYaml(file) as IReadOnlyDictionary<string, object?>
            ?? throw new ConfigException($"Profile file '{file}' must be a YAML mapping."), file);
    }

    /// <summary>The client build families a profile may declare; a build without a declaration in its lineage is legacy.</summary>
    private static readonly string[] Families = ["legacy", "modern"];

    /// <summary>
    /// Returns the <c>family</c> of the nearest profile in <paramref name="lineage"/> that declares one. A family
    /// head (kr-1256 for modern) declares it, and its descendants inherit it through their <c>parent</c> links.
    /// </summary>
    private string ResolveFamily(IReadOnlyList<string> lineage)
    {
        foreach (var id in lineage)
        {
            var file = Path.Combine(_profilesDir, id, "profile.yaml");
            if (LoadYaml(file) is not IReadOnlyDictionary<string, object?> doc || !doc.TryGetValue("family", out var raw) || raw is null)
                continue;
            if (raw is not string family || !Families.Contains(family, StringComparer.Ordinal))
                throw new ConfigException($"Profile file '{file}': family must be one of {string.Join(", ", Families)}.");
            return family;
        }
        return "legacy";
    }

    private List<string> ResolveLineage(string profileId, IReadOnlyDictionary<string, object?> doc, string file)
    {
        var lineage = new List<string> { profileId };
        while (doc.TryGetValue("parent", out var raw) && raw is not null)
        {
            if (raw is not string parent || parent.Length == 0)
                throw new ConfigException($"Profile file '{file}': parent must be a profile id.");
            if (lineage.Contains(parent, StringComparer.Ordinal))
                throw new ConfigException($"Profile '{profileId}' has a parent cycle: {string.Join(" -> ", lineage)} -> {parent}.");
            file = Path.Combine(_profilesDir, parent, "profile.yaml");
            if (!File.Exists(file))
                throw new ConfigException($"Profile '{lineage[^1]}' names parent '{parent}' but '{file}' does not exist.");
            lineage.Add(parent);
            doc = LoadYaml(file) as IReadOnlyDictionary<string, object?>
                ?? throw new ConfigException($"Profile file '{file}' must be a YAML mapping.");
        }
        return lineage;
    }

    /// <summary>
    /// Expands a named group file depth-first: its bundles, then nested groups, then its inline
    /// rows. Group sourcing stamps every contributed row with <c>group:&lt;name&gt;</c> so the
    /// dump shows which composition unit provided it.
    /// </summary>
    private void ExpandGroup(string name, List<Row> rows, string profileId, List<string> active, ref int expansions)
    {
        if (active.Contains(name, StringComparer.Ordinal))
            throw new ConfigException($"Profile '{profileId}' has a group cycle: {string.Join(" -> ", active)} -> {name}.");
        if (active.Count >= MaxGroupDepth)
            throw new ConfigException($"Profile '{profileId}' exceeds group nesting depth {MaxGroupDepth} at group '{name}'.");
        if (++expansions > MaxGroupExpansions)
            throw new ConfigException($"Profile '{profileId}' exceeds the {MaxGroupExpansions}-expansion group budget at group '{name}'.");
        var file = Path.Combine(_profilesDir, "groups", name + ".yaml");
        if (!File.Exists(file))
            throw new ConfigException($"Profile '{profileId}' references group '{name}' but '{file}' does not exist.");
        var doc = LoadYaml(file) as IReadOnlyDictionary<string, object?>
            ?? throw new ConfigException($"Group file '{file}' must be a YAML mapping with bundles, groups and/or rows.");
        foreach (var key in doc.Keys)
            if (key is not ("bundles" or "groups" or "rows"))
                throw new ConfigException($"Group file '{file}': unknown key '{key}'; expected only bundles, groups and rows.");
        active.Add(name);
        foreach (var bundleName in AsStringList(doc, "bundles", file))
        {
            var bundleFile = Path.Combine(_profilesDir, "bundles", bundleName + ".yaml");
            if (!File.Exists(bundleFile))
                throw new ConfigException($"Group '{name}' references bundle '{bundleName}' but '{bundleFile}' does not exist.");
            var bundleDoc = LoadYaml(bundleFile);
            if (bundleDoc is not IList<object?> list)
                throw new ConfigException($"Bundle file '{bundleFile}' must be a YAML list of rows.");
            foreach (var item in list)
                rows.Add(ParseRow(item, $"group:{name}", bundleFile));
        }
        foreach (var nested in AsStringList(doc, "groups", file))
            ExpandGroup(nested, rows, profileId, active, ref expansions);
        if (doc.TryGetValue("rows", out var inline))
        {
            if (inline is not IList<object?> list)
                throw new ConfigException($"Group file '{file}': 'rows' must be a list of row mappings.");
            foreach (var item in list)
                rows.Add(ParseRow(item, $"group:{name}", file));
        }
        active.RemoveAt(active.Count - 1);
    }

    /// <summary>Serializes a row list to the YAML form used by <c>--dump-config</c>.</summary>
    /// <param name="rows">The rows to dump.</param>
    /// <returns>A YAML document listing each row.</returns>
    public static string Dump(IEnumerable<PluginRow> rows)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var row in rows)
        {
            builder.Append("- id: ").Append(row.Id).Append('\n');
            builder.Append("  plugin: ").Append(row.Plugin).Append('\n');
            builder.Append("  source: ").Append(row.Source).Append('\n');
            if (row.ParentId is { } parent)
                builder.Append("  parent_id: ").Append(System.Text.Json.JsonSerializer.Serialize(parent)).Append((char)10);
            if (row.IsolatedServices.Count > 0)
                builder.Append("  isolated_services: ").Append(System.Text.Json.JsonSerializer.Serialize(row.IsolatedServices)).Append((char)10);
            if (row.Version is { } version)
                builder.Append("  version: ").Append(System.Text.Json.JsonSerializer.Serialize(version)).Append('\n');
            if (row.ClientBuild is { } build)
                builder.Append("  client_build: ").Append(build).Append('\n');
            if (row.Lineage.Count > 1)
                builder.Append("  lineage: [").Append(string.Join(", ", row.Lineage)).Append("]\n");
            if (row.Family is { } family)
                builder.Append("  family: ").Append(family).Append('\n');
            if (row.Disabled)
                builder.Append("  disabled: true\n");
            builder.Append("  lifecycle:\n    effect_capacity: ").Append(row.Lifecycle.EffectCapacity).Append('\n');
            builder.Append("    code_refresh_policy: ").Append(row.Lifecycle.CodeRefreshPolicy switch
            {
                PluginCodeRefreshPolicy.Allow => "allow",
                PluginCodeRefreshPolicy.RestartRequired => "restart_required",
                _ => throw new ConfigException(row.Id, ["lifecycle.code_refresh_policy is not a defined code refresh policy."]),
            }).Append('\n');
            builder.Append("    teardown_timeout_ms: ").Append(row.Lifecycle.TeardownTimeoutMs).Append('\n');
            builder.Append("    reconcile_attempt_limit: ").Append(row.Lifecycle.ReconcileAttemptLimit).Append((char)10);
            builder.Append("    entry_reserve: ").Append(row.Lifecycle.EntryReserve).Append((char)10);
            builder.Append("    service_metadata_reserve: ").Append(row.Lifecycle.ServiceMetadataReserve).Append((char)10);
            builder.Append("    definition_source_reserve: ").Append(row.Lifecycle.DefinitionSourceReserve).Append((char)10);
            builder.Append("    definition_reserve: ").Append(row.Lifecycle.DefinitionReserve).Append((char)10);
            builder.Append("    configuration_depth_limit: ").Append(row.Lifecycle.ConfigurationDepthLimit).Append((char)10);
            builder.Append("    configuration_node_limit: ").Append(row.Lifecycle.ConfigurationNodeLimit).Append((char)10);
            builder.Append("    configuration_collection_limit: ").Append(row.Lifecycle.ConfigurationCollectionLimit).Append((char)10);
            builder.Append("    configuration_text_limit: ").Append(row.Lifecycle.ConfigurationTextLimit).Append((char)10);
            if (row.Config is { } config)
            {
                builder.Append("  config:\n");
                WriteNode(builder, config, "    ");
            }
        }
        return builder.ToString();
    }

    private static void WriteNode(System.Text.StringBuilder builder, object? node, string indent)
    {
        switch (node)
        {
            case IReadOnlyDictionary<string, object?> map:
                foreach (var (key, value) in map)
                {
                    if (value is IReadOnlyDictionary<string, object?> or IList<object?>)
                    {
                        builder.Append(indent).Append(key).Append(":\n");
                        WriteNode(builder, value, indent + "  ");
                    }
                    else
                    {
                        builder.Append(indent).Append(key).Append(": ").Append(Scalar(value)).Append('\n');
                    }
                }
                break;
            case IList<object?> list:
                foreach (var item in list)
                {
                    if (item is IReadOnlyDictionary<string, object?> or IList<object?>)
                    {
                        builder.Append(indent).Append("-\n");
                        WriteNode(builder, item, indent + "  ");
                    }
                    else
                    {
                        builder.Append(indent).Append("- ").Append(Scalar(item)).Append('\n');
                    }
                }
                break;
        }
    }

    private static string Scalar(object? value) => value switch
    {
        null => "null",
        bool b => b ? "true" : "false",
        string s => PlainSafe(s) ? s : Quoted(s),
        _ => System.Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "",
    };

    // A string stays plain only when YAML reads it back as the same string; anything else (a ": "
    // or " #" inside, an indicator first, edge spaces, control characters, a null spelling) is quoted.
    private static bool PlainSafe(string s) =>
        s.Length > 0
        && !char.IsWhiteSpace(s[0]) && !char.IsWhiteSpace(s[^1])
        && "-?:,[]{}#&*!|>'\"%@`".IndexOf(s[0], StringComparison.Ordinal) < 0
        && !s.Contains(": ", StringComparison.Ordinal) && !s.EndsWith(':') && !s.Contains(" #", StringComparison.Ordinal)
        && !s.Any(char.IsControl)
        && s is not ("~" or "null" or "Null" or "NULL");

    private static string Quoted(string s)
    {
        var builder = new System.Text.StringBuilder(s.Length + 2).Append('"');
        foreach (var c in s)
        {
            builder.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ when char.IsControl(c) => $"\\x{(int)c:X2}",
                _ => c.ToString(),
            });
        }
        return builder.Append('"').ToString();
    }

    private sealed record Row(string Id, string Plugin, IReadOnlyDictionary<string, object?>? Config, bool Disabled, string Source, string? Version, PluginLifecycleOptions Lifecycle, string? ParentId, IReadOnlyList<string> IsolatedServices)
    {
        public PluginRow ToPluginRow() => new() { Id = Id, Plugin = Plugin, Config = Config, Disabled = Disabled, Source = Source, Version = Version, Lifecycle = Lifecycle, ParentId = ParentId, IsolatedServices = IsolatedServices };
    }

    private void ApplyPatch(string patchFile, List<Row> rows, string source)
    {
        var doc = LoadYaml(patchFile);
        if (doc is null)
            return;
        if (doc is not IList<object?> ops)
            throw new ConfigException($"Patch file '{patchFile}' must be a YAML list of operations.");
        foreach (var op in ops)
        {
            if (op is not IReadOnlyDictionary<string, object?> map || map.Count != 1)
                throw new ConfigException($"Patch file '{patchFile}': every operation must be a single-key mapping (replace, set, insert, or remove).");
            var (verb, arg) = map.First();
            switch (verb)
            {
                case "replace":
                {
                    if (arg is not IReadOnlyDictionary<string, object?> argMap)
                        throw new ConfigException($"Patch '{patchFile}': replace expects a row mapping.");
                    var id = RequiredString(argMap, "id", patchFile);
                    var index = rows.FindIndex(r => r.Id == id);
                    if (index < 0)
                        throw new ConfigException($"Patch '{patchFile}': replace targets unknown row '{id}'.");
                    var old = rows[index];
                    var plugin = argMap.ContainsKey("plugin") ? RequiredString(argMap, "plugin", patchFile) : old.Plugin;
                    var disabled = argMap.ContainsKey("disabled") ? ParseBool(argMap["disabled"], patchFile, id, "disabled") : old.Disabled;
                    var config = old.Config;
                    if (argMap.ContainsKey("config"))
                    {
                        if (argMap["config"] is not null and not IReadOnlyDictionary<string, object?>)
                            throw new ConfigException($"Patch '{patchFile}': row '{id}' config must be a mapping.");
                        var replaced = argMap["config"] as IReadOnlyDictionary<string, object?>;
                        config = replaced is null || disabled ? replaced : (IReadOnlyDictionary<string, object?>)Interpolate(replaced, patchFile, id);
                    }
                    var lifecycle = argMap.ContainsKey("lifecycle") ? ReadLifecycle(argMap, patchFile, id) : old.Lifecycle;
                    rows[index] = new Row(
                        (string)InterpolateScalar(id, patchFile, id)!,
                        (string)InterpolateScalar(plugin, patchFile, id)!,
                        config,
                        disabled,
                        source,
                        argMap.ContainsKey("version") ? ReadVersion(argMap, patchFile, id) : old.Version,
                        lifecycle,
                        argMap.ContainsKey("parent_id") ? ReadParentId(argMap, patchFile, id) : old.ParentId,
                        argMap.ContainsKey("isolated_services") ? ReadIsolatedServices(argMap, patchFile, id, lifecycle) : old.IsolatedServices);
                    break;
                }
                case "set":
                {
                    // Changes single config keys of a row and keeps the rest, so a profile states
                    // only the values it differs in; a null value removes the key.
                    if (arg is not IReadOnlyDictionary<string, object?> setMap)
                        throw new ConfigException($"Patch '{patchFile}': set expects a row mapping.");
                    var id = RequiredString(setMap, "id", patchFile);
                    var index = rows.FindIndex(r => r.Id == id);
                    if (index < 0)
                        throw new ConfigException($"Patch '{patchFile}': set targets unknown row '{id}'.");
                    if (setMap.Keys.FirstOrDefault(key => key is not ("id" or "config")) is { } unknown)
                        throw new ConfigException($"Patch '{patchFile}': set on row '{id}' has unknown key '{unknown}'; it takes id and config.");
                    if (!setMap.TryGetValue("config", out var rawKeys) || rawKeys is not IReadOnlyDictionary<string, object?> keys)
                        throw new ConfigException($"Patch '{patchFile}': set on row '{id}' needs a config mapping.");
                    var old = rows[index];
                    var merged = old.Config is null
                        ? new Dictionary<string, object?>(StringComparer.Ordinal)
                        : new Dictionary<string, object?>(old.Config, StringComparer.Ordinal);
                    foreach (var (key, value) in keys)
                    {
                        if (value is null)
                            merged.Remove(key);
                        else
                            merged[key] = old.Disabled ? value : Interpolate(value, patchFile, id);
                    }
                    rows[index] = old with { Config = merged, Source = source };
                    break;
                }
                case "insert":
                {
                    if (arg is not IList<object?> list)
                        throw new ConfigException($"Patch '{patchFile}': insert expects a list of rows.");
                    foreach (var item in list)
                        rows.Add(ParseRow(item, source, patchFile));
                    break;
                }
                case "remove":
                {
                    var id = arg?.ToString() ?? throw new ConfigException($"Patch '{patchFile}': remove expects a row id.");
                    var index = rows.FindIndex(r => r.Id == id);
                    if (index < 0)
                        throw new ConfigException($"Patch '{patchFile}': remove targets unknown row '{id}'.");
                    rows.RemoveAt(index);
                    break;
                }
                default:
                    throw new ConfigException($"Patch '{patchFile}': unknown operation '{verb}'.");
            }
        }
    }

    private Row ParseRow(object? node, string source, string file)
    {
        if (node is not IReadOnlyDictionary<string, object?> map)
            throw new ConfigException($"File '{file}': every row must be a mapping.");
        var id = RequiredString(map, "id", file);
        var plugin = RequiredString(map, "plugin", file);
        var disabled = map.TryGetValue("disabled", out var d) ? ParseBool(d, file, id, "disabled") : false;
        IReadOnlyDictionary<string, object?>? config = null;
        if (map.TryGetValue("config", out var c) && c is not null)
        {
            if (c is not IReadOnlyDictionary<string, object?> cMap)
                throw new ConfigException($"File '{file}': row '{id}' config must be a mapping.");
            config = cMap;
        }
        // Disabled rows never activate, so they do not need their environment variables.
        var interpolated = config is null || disabled ? config : (IReadOnlyDictionary<string, object?>)Interpolate(config, file, id);
        var lifecycle = ReadLifecycle(map, file, id);
        return new Row((string)InterpolateScalar(id, file, id)!, (string)InterpolateScalar(plugin, file, id)!, interpolated, disabled, source, ReadVersion(map, file, id), lifecycle,
            ReadParentId(map, file, id), ReadIsolatedServices(map, file, id, lifecycle));
    }

    private string? ReadParentId(IReadOnlyDictionary<string, object?> map, string file, string rowId)
    {
        if (!map.TryGetValue("parent_id", out var value) || value is null) return null;
        if (value is not string text || InterpolateScalar(text, file, rowId) is not string parent || string.IsNullOrWhiteSpace(parent))
            throw new ConfigException($"File '{file}': row '{rowId}' parent_id must be a nonblank string or null.");
        return parent;
    }

    private IReadOnlyList<string> ReadIsolatedServices(IReadOnlyDictionary<string, object?> map, string file, string rowId, PluginLifecycleOptions lifecycle)
    {
        if (!map.TryGetValue("isolated_services", out var value)) return [];
        if (value is not IList<object?> values || values.Count > lifecycle.ConfigurationCollectionLimit)
            throw new ConfigException($"File '{file}': row '{rowId}' isolated_services must be a list within its collection budget.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in values)
            if (item is not string text || InterpolateScalar(text, file, rowId) is not string name || string.IsNullOrWhiteSpace(name) || !names.Add(name))
                throw new ConfigException($"File '{file}': row '{rowId}' isolated_services requires unique nonblank strings.");
        return Array.AsReadOnly(names.Order(StringComparer.Ordinal).ToArray());
    }

    private static PluginLifecycleOptions ReadLifecycle(IReadOnlyDictionary<string, object?> map, string file, string rowId)
    {
        if (!map.TryGetValue("lifecycle", out var value) || value is null) return new();
        if (value is not IReadOnlyDictionary<string, object?> settings)
            throw new ConfigException($"File '{file}': row '{rowId}' lifecycle must be a mapping.");
        var interpolated = ((IReadOnlyDictionary<string, object?>)Interpolate(settings, file, rowId))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        if (interpolated.TryGetValue("code_refresh_policy", out var policy))
            interpolated["code_refresh_policy"] = policy switch
            {
                "allow" => nameof(PluginCodeRefreshPolicy.Allow),
                "restart_required" => nameof(PluginCodeRefreshPolicy.RestartRequired),
                _ => throw new ConfigException($"File '{file}': row '{rowId}' lifecycle.code_refresh_policy must be 'allow' or 'restart_required'."),
            };
        return new PluginConfig(new PluginRow
        {
            Id = rowId, Plugin = "lifecycle", Source = file,
            Config = interpolated,
        }).Bind<PluginLifecycleOptions>();
    }

    private static string? ReadVersion(IReadOnlyDictionary<string, object?> map, string file, string rowId)
    {
        if (!map.TryGetValue("version", out var value) || value is null)
            return null;
        if (value is not string text)
            throw new ConfigException($"File '{file}': row '{rowId}' version must be a string.");
        text = (string)InterpolateScalar(text, file, rowId)!;
        try { _ = PluginVersion.Parse("0.0.0").Satisfies(text); }
        catch (ConfigException ex)
        {
            throw new ConfigException($"File '{file}': row '{rowId}' has invalid version constraint: {ex.Message}");
        }
        return text;
    }

    private static bool ParseBool(object? value, string file, string rowId, string key) => value switch
    {
        bool b => b,
        string s when bool.TryParse(s, out var parsed) => parsed,
        _ => throw new ConfigException($"File '{file}': row '{rowId}' key '{key}' must be a boolean."),
    };

    private static string RequiredString(IReadOnlyDictionary<string, object?> map, string key, string file)
    {
        if (!map.TryGetValue(key, out var value) || value?.ToString() is not { Length: > 0 } s)
            throw new ConfigException($"File '{file}': row is missing required key '{key}'.");
        return s;
    }

    private static object Interpolate(object node, string file, string rowId) => node switch
    {
        string s => InterpolateScalar(s, file, rowId)!,
        Dictionary<string, object?> map => map.ToDictionary(kv => kv.Key, kv => kv.Value is null ? null : Interpolate(kv.Value, file, rowId)),
        List<object?> list => list.Select(v => v is null ? null : Interpolate(v, file, rowId)).ToList(),
        _ => node,
    };

    private static string? InterpolateScalar(string value, string file, string rowId) =>
        EnvPattern.Replace(value, match =>
        {
            var name = match.Groups[1].Value;
            var fallback = match.Groups[2].Success ? match.Groups[2].Value : null;
            if (t_variables is { } variables && variables.TryGetValue(name, out var own))
                return own;
            var env = (t_environment ?? Environment.GetEnvironmentVariable)(name);
            if (env is not null)
                return env;
            if (fallback is not null)
                return fallback;
            if (t_unsetReadsEmpty)
                return "";
            throw new ConfigException($"File '{file}', row '{rowId}': environment variable '{name}' is not set and has no default.");
        });

    private static IReadOnlyList<string> AsStringList(IReadOnlyDictionary<string, object?> doc, string key, string file)
    {
        if (!doc.TryGetValue(key, out var value) || value is null)
            return [];
        if (value is not IList<object?> list)
            throw new ConfigException($"File '{file}': '{key}' must be a list.");
        return list.Select(v => v?.ToString() ?? throw new ConfigException($"File '{file}': '{key}' entries must be strings.")).ToList();
    }

    private static object? LoadYaml(string file)
    {
        using var reader = new StreamReader(file);
        var stream = new YamlStream();
        try
        {
            stream.Load(reader);
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            throw new ConfigException($"File '{file}': {ex.Message}");
        }
        if (stream.Documents.Count == 0)
            return null;
        return ToGraph(stream.Documents[0].RootNode);
    }

    private static object? ToGraph(YamlNode node) => node switch
    {
        YamlScalarNode scalar => ScalarValue(scalar),
        YamlMappingNode mapping => mapping.Children.ToDictionary(
            kv => ((YamlScalarNode)kv.Key).Value ?? "",
            kv => ToGraph(kv.Value)),
        YamlSequenceNode sequence => sequence.Children.Select(ToGraph).Cast<object?>().ToList(),
        _ => null,
    };

    private static string? ScalarValue(YamlScalarNode scalar)
    {
        if (scalar.Tag.Equals(new YamlDotNet.Core.TagName("tag:yaml.org,2002:str"))) return scalar.Value;
        if (scalar.Tag.Equals(new YamlDotNet.Core.TagName("tag:yaml.org,2002:null"))) return null;
        if (scalar.Style == YamlDotNet.Core.ScalarStyle.Plain && scalar.Value is null or "" or "~" or "null" or "Null" or "NULL") return null;
        return scalar.Value;
    }

    [GeneratedRegex(@"\$\{([A-Za-z_][A-Za-z0-9_]*)(?::-([^}]*))?\}")]
    private static partial Regex EnvRegex();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex VariableName();
}
