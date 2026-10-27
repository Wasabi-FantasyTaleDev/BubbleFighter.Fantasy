using System.Globalization;

namespace BubbleFighter.Fantasy.Contracts.Launcher;

/// <summary>
/// The launch policy a server sends to the Launcher. Plugins are its first section; later client policies are added as
/// further sections of this record so the document and its management API keep one shape.
/// </summary>
/// <param name="Sources">Store sources that rules name; a rule never carries its own download address.</param>
/// <param name="Plugins">One entry per plugin identity, each holding that plugin's typed rules.</param>
public sealed record ClientPolicy(IReadOnlyList<ClientPolicySource> Sources, IReadOnlyList<ClientPluginRules> Plugins)
{
    /// <summary>The format number sent to the Launcher; it changes only when a Launcher must reject the older reading.</summary>
    public const int FormatVersion = 1;

    /// <summary>A policy without sources or rules.</summary>
    public static ClientPolicy Empty { get; } = new([], []);
}

/// <summary>A named store catalog from which the Launcher may install a plugin a rule points at.</summary>
/// <param name="Name">The name rules use to refer to this source.</param>
/// <param name="Kind"><c>http</c> or <c>alist</c>, as the Launcher resolves store catalogs.</param>
/// <param name="BaseUrl">The HTTP(S) source root without credentials, query or fragment.</param>
/// <param name="CatalogPath">The unescaped catalog path relative to the source root.</param>
public sealed record ClientPolicySource(string Name, string Kind, string BaseUrl, string CatalogPath);

/// <summary>The rules that apply to one plugin identity.</summary>
/// <param name="Id">The store identity, without a download address or local path.</param>
/// <param name="Name">An optional label shown only in the management interface.</param>
/// <param name="Description">An optional note shown only in the management interface.</param>
/// <param name="Rules">The plugin's rules; each one is checked against the others for conflicts.</param>
public sealed record ClientPluginRules(string Id, string? Name, string? Description, IReadOnlyList<ClientPolicyRule> Rules);

/// <summary>One typed rule. Which optional fields it takes is decided by its type, see <see cref="ClientPolicyRuleTypes"/>.</summary>
/// <param name="Id">A stable identity within its plugin, used for editing and for the Launcher's "already shown" record.</param>
/// <param name="Type">The rule type name, such as <c>require</c> or <c>min_version</c>.</param>
/// <param name="Builds">Exact build IDs; an empty list applies to every build.</param>
/// <param name="Version">The version bound, for the version rule types only.</param>
/// <param name="Sources">Source names to install from, for the recommendation type only; empty allows any declared source.</param>
public sealed record ClientPolicyRule(string Id, string Type, IReadOnlyList<string> Builds, string? Version = null,
    IReadOnlyList<string>? Sources = null);

/// <summary>Which optional field a rule type takes; the management interface builds its form from this.</summary>
public enum ClientPolicyRuleField
{
    /// <summary>The rule takes no field beyond its builds.</summary>
    None,
    /// <summary>The rule takes a strict SemVer <see cref="ClientPolicyRule.Version"/>.</summary>
    Version,
    /// <summary>The rule takes a list of <see cref="ClientPolicyRule.Sources"/>.</summary>
    Sources,
}

/// <summary>
/// One rule type. Each type states its field and which other types it cannot share a build with, so a new policy is
/// one subclass here and one handler in the Launcher, with no shared switch to extend.
/// </summary>
public abstract class ClientPolicyRuleType
{
    /// <summary>Gets the name used in configuration and on the wire.</summary>
    public abstract string Name { get; }

    /// <summary>Gets the optional field this type takes.</summary>
    public abstract ClientPolicyRuleField Field { get; }

    /// <summary>Gets the types that may conflict with this one on an overlapping build.</summary>
    public virtual IReadOnlyList<string> Conflicts => [];

    /// <summary>Whether this rule cannot hold together with <paramref name="other"/>, which shares a build with it.</summary>
    /// <param name="self">A rule of this type.</param>
    /// <param name="other">A rule of another type on an overlapping build.</param>
    public virtual bool ConflictsWith(ClientPolicyRule self, ClientPolicyRule other) => Conflicts.Contains(other.Type, StringComparer.Ordinal);
}

/// <summary>The rule types of policy format 1. The Launcher refuses a policy that names a type it does not know.</summary>
public static class ClientPolicyRuleTypes
{
    /// <summary>The Launcher refuses to launch until the plugin is installed and enabled.</summary>
    public const string Require = "require";
    /// <summary>The plugin is turned off for launches to this server without changing the saved selection.</summary>
    public const string Disable = "disable";
    /// <summary>The Launcher offers the plugin once per client and rule.</summary>
    public const string Recommend = "recommend";
    /// <summary>An enabled plugin must be at least this version.</summary>
    public const string MinVersion = "min_version";
    /// <summary>An enabled plugin must be at most this version.</summary>
    public const string MaxVersion = "max_version";
    /// <summary>An enabled plugin is updated to its newest version before launch.</summary>
    public const string Latest = "latest";

    /// <summary>Gets every known type, in the order the management interface lists them.</summary>
    public static IReadOnlyList<ClientPolicyRuleType> All { get; } =
        [new Simple(Require, []), new Simple(Disable, [Require, Recommend, MinVersion, MaxVersion, Latest]),
            new SourcesType(), new MinimumType(), new Simple(MaxVersion, [Latest], ClientPolicyRuleField.Version), new Simple(Latest, [MaxVersion])];

    /// <summary>Finds a type by name.</summary>
    /// <param name="name">The configured type name.</param>
    /// <param name="type">The type, when it is known.</param>
    public static bool TryGet(string? name, out ClientPolicyRuleType type)
    {
        type = All.FirstOrDefault(candidate => candidate.Name == name)!;
        return type is not null;
    }

    private sealed class Simple(string name, string[] conflicts, ClientPolicyRuleField takes = ClientPolicyRuleField.None) : ClientPolicyRuleType
    {
        public override string Name => name;
        public override ClientPolicyRuleField Field => takes;
        public override IReadOnlyList<string> Conflicts => conflicts;
    }

    private sealed class SourcesType : ClientPolicyRuleType
    {
        public override string Name => Recommend;
        public override ClientPolicyRuleField Field => ClientPolicyRuleField.Sources;
    }

    // Only a minimum above the maximum conflicts; equal bounds pin one exact version.
    private sealed class MinimumType : ClientPolicyRuleType
    {
        public override string Name => MinVersion;
        public override ClientPolicyRuleField Field => ClientPolicyRuleField.Version;
        public override IReadOnlyList<string> Conflicts => [MaxVersion];
        public override bool ConflictsWith(ClientPolicyRule self, ClientPolicyRule other) =>
            other.Type == MaxVersion && ClientPolicyVersion.Compare(self.Version!, other.Version!) > 0;
    }
}

/// <summary>A rejected policy, with a stable code the management interface maps to a message and the rules involved.</summary>
public sealed class ClientPolicyException : ArgumentException
{
    /// <summary>Creates a rejection.</summary>
    /// <param name="code">The stable error code.</param>
    /// <param name="plugin">The plugin the problem is in, when it is in one.</param>
    /// <param name="rule">The first rule involved.</param>
    /// <param name="other">The second rule, for conflicts.</param>
    public ClientPolicyException(string code, string? plugin = null, string? rule = null, string? other = null) : base(code)
    {
        Code = code; Plugin = plugin; Rule = rule; Other = other;
    }

    /// <summary>Gets the stable code: <c>client_policy_invalid</c>, <c>client_policy_unknown_type</c>,
    /// <c>client_policy_unknown_source</c> or <c>client_policy_conflict</c>.</summary>
    public string Code { get; }
    /// <summary>Gets the plugin identity involved, if any.</summary>
    public string? Plugin { get; }
    /// <summary>Gets the first rule identity involved, if any.</summary>
    public string? Rule { get; }
    /// <summary>Gets the conflicting rule identity, if any.</summary>
    public string? Other { get; }
}

/// <summary>Validates a client policy before it is saved or a Launcher listener starts with it.</summary>
public static class ClientPolicyRules
{
    /// <summary>Rejects malformed entries, unknown types or sources, and rules that conflict on an overlapping build.</summary>
    /// <param name="policy">The complete candidate, rather than only its changed plugins.</param>
    /// <exception cref="ClientPolicyException">The policy is rejected; the code says why.</exception>
    public static void Validate(ClientPolicy policy)
    {
        if (policy?.Sources is null || policy.Plugins is null)
            throw new ClientPolicyException("client_policy_invalid");
        var sources = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in policy.Sources)
            if (source is null || !Name(source.Name) || !sources.Add(source.Name) || source.Kind is not ("http" or "alist") ||
                !SourceUrl(source.BaseUrl) || !CatalogPath(source.CatalogPath))
                throw new ClientPolicyException("client_policy_invalid");
        var plugins = new HashSet<string>(StringComparer.Ordinal);
        foreach (var plugin in policy.Plugins)
        {
            if (plugin is null || !PluginId(plugin.Id) || !plugins.Add(plugin.Id) || plugin.Rules is null ||
                !Text(plugin.Name, false) || !Text(plugin.Description, true))
                throw new ClientPolicyException("client_policy_invalid", plugin?.Id);
            var rules = new HashSet<string>(StringComparer.Ordinal);
            foreach (var rule in plugin.Rules)
                ValidateRule(plugin.Id, rule, rules, sources);
            for (var i = 0; i < plugin.Rules.Count; i++)
            for (var j = i + 1; j < plugin.Rules.Count; j++)
            {
                var a = plugin.Rules[i]; var b = plugin.Rules[j];
                if (!Overlap(a.Builds, b.Builds)) continue;
                ClientPolicyRuleTypes.TryGet(a.Type, out var first); ClientPolicyRuleTypes.TryGet(b.Type, out var second);
                if (a.Type == b.Type || first.ConflictsWith(a, b) || second.ConflictsWith(b, a))
                    throw new ClientPolicyException("client_policy_conflict", plugin.Id, a.Id, b.Id);
            }
        }
    }

    /// <summary>Whether two build scopes share a build; an empty scope means every build.</summary>
    /// <param name="left">The first scope.</param>
    /// <param name="right">The second scope.</param>
    public static bool Overlap(IReadOnlyList<string> left, IReadOnlyList<string> right) =>
        left.Count == 0 || right.Count == 0 || left.Intersect(right, StringComparer.Ordinal).Any();

    private static void ValidateRule(string plugin, ClientPolicyRule? rule, HashSet<string> seen, HashSet<string> sources)
    {
        if (rule is null || !RuleId(rule.Id) || !seen.Add(rule.Id) || rule.Builds is null ||
            rule.Builds.Any(build => !BuildId(build)) || rule.Builds.Distinct(StringComparer.Ordinal).Count() != rule.Builds.Count)
            throw new ClientPolicyException("client_policy_invalid", plugin, rule?.Id);
        if (!ClientPolicyRuleTypes.TryGet(rule.Type, out var type))
            throw new ClientPolicyException("client_policy_unknown_type", plugin, rule.Id);
        var versionValid = type.Field == ClientPolicyRuleField.Version
            ? rule.Version is not null && ClientPolicyVersion.IsValid(rule.Version) : rule.Version is null;
        var sourcesValid = type.Field == ClientPolicyRuleField.Sources
            ? rule.Sources is { } list && list.All(Name) && list.Distinct(StringComparer.Ordinal).Count() == list.Count
            : rule.Sources is null;
        if (!versionValid || !sourcesValid) throw new ClientPolicyException("client_policy_invalid", plugin, rule.Id);
        if (rule.Sources?.FirstOrDefault(name => !sources.Contains(name)) is not null)
            throw new ClientPolicyException("client_policy_unknown_source", plugin, rule.Id);
    }

    private static bool PluginId(string? id) => id is { Length: > 0 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');
    private static bool RuleId(string? id) => id is { Length: > 0 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    private static bool BuildId(string? id) => id is { Length: > 0 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
    private static bool Name(string? name) => name is { Length: > 0 } && (char.IsAsciiLetterLower(name[0]) || char.IsAsciiDigit(name[0])) &&
        name.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '.' or '-' or '_');

    private static bool Text(string? value, bool multiline) => value is null ||
        !value.Any(c => char.IsControl(c) && !(multiline && c is '\n' or '\r' or '\t'));

    private static bool SourceUrl(string? value) => value is { Length: > 0 } && !value.Any(char.IsControl) &&
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.Host.Length > 0 &&
        uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0;

    private static bool CatalogPath(string? value) => value is { Length: > 0 } && !value.Any(char.IsControl) &&
        value.IndexOfAny(['\\', ':', '?', '#', '%']) < 0 && value.Split('/').All(part => part.Length > 0 && part is not ("." or "..") && part == part.Trim());
}

/// <summary>Strict SemVer 2.0 comparison, the same grammar and order as the Launcher's plugin versions.</summary>
public static class ClientPolicyVersion
{
    /// <summary>Whether <paramref name="text"/> is a strict SemVer.</summary>
    /// <param name="text">The candidate version.</param>
    public static bool IsValid(string? text) => text is not null && TryParse(text, out _, out _);

    /// <summary>Compares two valid versions by SemVer precedence; build metadata is ignored.</summary>
    /// <param name="left">The first version.</param>
    /// <param name="right">The second version.</param>
    /// <exception cref="FormatException">Either version is not strict SemVer.</exception>
    public static int Compare(string left, string right)
    {
        if (!TryParse(left, out var a, out var preA) || !TryParse(right, out var b, out var preB)) throw new FormatException("invalid semantic version");
        for (var i = 0; i < 3; i++) if (a[i] != b[i]) return a[i].CompareTo(b[i]);
        if (preA.Length == 0 || preB.Length == 0) return (preA.Length == 0).CompareTo(preB.Length == 0);
        for (var i = 0; i < Math.Min(preA.Length, preB.Length); i++)
        {
            var x = preA[i]; var y = preB[i];
            var nx = x.All(char.IsAsciiDigit); var ny = y.All(char.IsAsciiDigit);
            var result = nx != ny ? (nx ? -1 : 1) : nx && x.Length != y.Length ? x.Length.CompareTo(y.Length) : string.CompareOrdinal(x, y);
            if (result != 0) return result;
        }
        return preA.Length.CompareTo(preB.Length);
    }

    private static bool TryParse(string text, out uint[] release, out string[] prerelease)
    {
        release = []; prerelease = [];
        var metadata = text.Split('+');
        if (metadata.Length > 2 || metadata.Length == 2 && !Identifiers(metadata[1], false)) return false;
        var parts = metadata[0].Split('-', 2);
        var numbers = parts[0].Split('.');
        if (numbers.Length != 3 || numbers.Any(n => n.Length == 0 || n.Length > 1 && n[0] == '0' || !n.All(char.IsAsciiDigit))) return false;
        if (parts.Length == 2 && !Identifiers(parts[1], true)) return false;
        var parsed = new uint[3];
        for (var i = 0; i < 3; i++)
            if (!uint.TryParse(numbers[i], NumberStyles.None, CultureInfo.InvariantCulture, out parsed[i])) return false;
        release = parsed; prerelease = parts.Length == 2 ? parts[1].Split('.') : [];
        return true;
    }

    private static bool Identifiers(string text, bool prerelease) => text.Split('.').All(p => p.Length > 0 &&
        p.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') && (!prerelease || !p.All(char.IsAsciiDigit) || p.Length == 1 || p[0] != '0'));
}
