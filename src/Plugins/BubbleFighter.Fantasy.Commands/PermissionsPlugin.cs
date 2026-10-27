using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Administration;

namespace BubbleFighter.Fantasy.Commands;

/// <summary>Publishes bounded permission registrations and a resolver with isolated identity namespaces.</summary>
[Plugin("permissions", Provides = [typeof(IPermissionRegistry), typeof(IPermissionService)])]
public sealed class PermissionsPlugin : IPlugin
{
    /// <inheritdoc />
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var service = new PermissionService(context.Config.Bind<PermissionOptions>());
        context.Effect(service);
        context.Effect(service.Register(new("webui.access", "Enter the administration interface.")));
        context.Effect(service.Register(new("webui.admin", "Administer all WebUI operations.")));
        context.Effect(context.Services.Register<IPermissionRegistry>(service));
        context.Effect(context.Services.Register<IPermissionService>(service));
        return ValueTask.CompletedTask;
    }
}

internal sealed class PermissionService : IPermissionRegistry, IPermissionService, IDisposable
{
    private sealed record Rules(bool Operator, HashSet<string> Allow, HashSet<string> Deny);
    private readonly object _gate = new();
    private readonly int _capacity;
    private readonly Dictionary<string, PermissionDefinition> _nodes = new(StringComparer.Ordinal);
    private readonly Dictionary<CommandPrincipal, Rules> _subjects = [];
    private bool _disposed;

    internal PermissionService(PermissionOptions options)
    {
        _capacity = options.MaxNodes;
        if (options.Groups is null || options.Subjects is null || (long)options.Groups.Length + options.Subjects.Length > options.MaxPolicyEntries)
            throw new ConfigException("Permission groups and subjects must fit their configured bounds.");
        long stored = options.Groups.Length + options.Subjects.Length;
        var groups = new Dictionary<string, PermissionGroupOptions>(StringComparer.Ordinal);
        foreach (var group in options.Groups)
        {
            if (group is null || !Names.Identifier(group.Name) || !groups.TryAdd(group.Name, group))
                throw new ConfigException("Permission group names must be valid and unique.");
            ValidateRules(group.Inherits, group.Allow, group.Deny, options.MaxPolicyEntries);
            stored += group.Inherits.Length;
            if (stored > options.MaxPolicyEntries) throw new ConfigException("Permission inheritance exceeds max_policy_entries.");
        }
        var resolved = new Dictionary<string, Rules>(StringComparer.Ordinal);
        var pending = groups.ToDictionary(pair => pair.Key, pair => pair.Value.Inherits.Length, StringComparer.Ordinal);
        var children = groups.Keys.ToDictionary(name => name, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var group in groups.Values)
            foreach (var parent in group.Inherits)
            {
                if (!children.TryGetValue(parent, out var dependents)) throw new ConfigException("Permission inheritance names an unknown group.");
                dependents.Add(group.Name);
            }
        // Topological expansion avoids imposing an arbitrary inheritance depth or consuming the call stack.
        var ready = new Queue<string>(pending.Where(pair => pair.Value == 0).Select(pair => pair.Key));
        while (ready.TryDequeue(out var name))
        {
            var group = groups[name];
            var rule = new Rules(false, new(group.Allow, StringComparer.Ordinal), new(group.Deny, StringComparer.Ordinal));
            CheckExpanded(rule);
            foreach (var parent in group.Inherits)
            {
                var inherited = resolved[parent];
                rule.Allow.UnionWith(inherited.Allow);
                rule.Deny.UnionWith(inherited.Deny);
                CheckExpanded(rule);
            }
            stored += rule.Allow.Count + rule.Deny.Count;
            resolved.Add(name, rule);
            foreach (var child in children[name])
                if (--pending[child] == 0) ready.Enqueue(child);
        }
        if (resolved.Count != groups.Count) throw new ConfigException("Permission group inheritance contains a cycle.");
        foreach (var subject in options.Subjects)
        {
            if (subject is null || !Names.Identity(subject.Id) || subject.Kind is not ("game" or "webui") || (subject.Operator && subject.Kind != "game"))
                throw new ConfigException("Permission subjects require a stable game or WebUI account; only game accounts may be OP.");
            ValidateRules(subject.Groups, subject.Allow, subject.Deny, options.MaxPolicyEntries);
            var rule = new Rules(subject.Operator, new(subject.Allow, StringComparer.Ordinal), new(subject.Deny, StringComparer.Ordinal));
            CheckExpanded(rule);
            foreach (var name in subject.Groups)
            {
                if (!resolved.TryGetValue(name, out var inherited)) throw new ConfigException("Permission subject names an unknown group.");
                rule.Allow.UnionWith(inherited.Allow);
                rule.Deny.UnionWith(inherited.Deny);
                CheckExpanded(rule);
            }
            var key = new CommandPrincipal(subject.Kind == "game" ? PrincipalKind.Game : PrincipalKind.WebUi, subject.Id);
            stored += rule.Allow.Count + rule.Deny.Count;
            if (!_subjects.TryAdd(key, rule)) throw new ConfigException("Permission subjects must be unique within their authentication namespace.");
        }
        void CheckExpanded(Rules rule)
        {
            if (stored + rule.Allow.Count + rule.Deny.Count > options.MaxPolicyEntries)
                throw new ConfigException("Expanded permission storage exceeds max_policy_entries.");
        }
    }

    public IDisposable Register(PermissionDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!Names.Node(definition.Node) || !Names.Description(definition.Description) || (definition.Node is "webui.admin" or "webui.access" && definition.DefaultAllowed))
            throw new ArgumentException("Permission metadata is invalid or grants reserved WebUI access by default.", nameof(definition));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_nodes.Count >= _capacity) throw new InvalidOperationException("Permission registry capacity is occupied.");
            if (!_nodes.TryAdd(definition.Node, definition)) throw new InvalidOperationException("Permission node is already registered.");
        }
        return new Registration(() => { lock (_gate) _nodes.Remove(definition.Node); });
    }

    public IReadOnlyList<PermissionDefinition> List()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return Array.AsReadOnly(_nodes.Values.OrderBy(n => n.Node, StringComparer.Ordinal).ToArray());
        }
    }

    public bool IsAllowed(CommandPrincipal principal, string node)
    {
        Names.ValidatePrincipal(principal);
        if (!Names.Node(node)) throw new ArgumentException("Permission node is invalid.", nameof(node));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_nodes.TryGetValue(node, out var definition)) return false;
            if (principal.Kind == PrincipalKind.Game && node is "webui.access" or "webui.admin") return false;
            _subjects.TryGetValue(principal, out var rules);
            if (principal.Kind == PrincipalKind.Console || (principal.Kind == PrincipalKind.Game && rules?.Operator == true)) return true;
            if (principal.Kind == PrincipalKind.WebUi && Evaluate(rules, "webui.admin", false)) return true;
            return Evaluate(rules, node, definition.DefaultAllowed);
        }
    }

    public void Dispose()
    {
        lock (_gate) { _disposed = true; _nodes.Clear(); }
    }

    private static bool Evaluate(Rules? rules, string node, bool defaultAllowed) =>
        rules is null ? defaultAllowed : !rules.Deny.Any(pattern => Matches(pattern, node)) &&
            (defaultAllowed || rules.Allow.Any(pattern => Matches(pattern, node)));

    private static bool Matches(string pattern, string node) => pattern == "*" || pattern == node ||
        (pattern.EndsWith(".*", StringComparison.Ordinal) && node.StartsWith(pattern[..^1], StringComparison.Ordinal));

    private static void ValidateRules(string[]? groups, string[]? allow, string[]? deny, int limit)
    {
        if (groups is null || allow is null || deny is null || (long)groups.Length + allow.Length + deny.Length > limit ||
            groups.Any(g => !Names.Identifier(g)) || allow.Concat(deny).Any(p => p != "*" && !Names.Node(p?.EndsWith(".*", StringComparison.Ordinal) == true ? p[..^2] : p)))
            throw new ConfigException("Permission entries require bounded group names and exact or terminal-wildcard nodes.");
    }
}

internal static class Names
{
    internal static bool Identifier(string? value) => value is { Length: > 0 and <= 64 } &&
        value.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-');
    internal static bool Node(string? value) => value is { Length: > 0 and <= 128 } && value.Split('.').All(Identifier);
    internal static bool Identity(string? value) => value is { Length: > 0 and <= 128 } && !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl);
    internal static bool Description(string? value) => value is { Length: > 0 and <= 512 } && !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl);
    internal static void ValidatePrincipal(CommandPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (!Enum.IsDefined(principal.Kind) || !Identity(principal.Id)) throw new ArgumentException("Authenticated principal is invalid.", nameof(principal));
    }
}

internal sealed class Registration(Action release) : IDisposable
{
    private Action? _release = release;
    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}
