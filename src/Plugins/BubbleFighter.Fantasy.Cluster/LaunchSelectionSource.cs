using System.ComponentModel.DataAnnotations;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Cluster;
using BubbleFighter.Fantasy.Contracts.Launcher;

namespace BubbleFighter.Fantasy.Cluster;

/// <summary>Configures the ticket authority for manual targets and the shared persistent scope for automatic selection.</summary>
public sealed class LaunchSelectionOptions : IValidatableOptions
{
    /// <summary>Gets or sets the issuer's physical game node identity for local ticket redemption.</summary>
    public string LocalNodeId { get; set; } = "";

    /// <summary>Gets or sets the directory's explicit authentication authority identity.</summary>
    public string AuthorityId { get; set; } = "";

    /// <summary>Gets or sets that authority's exact public HTTP or HTTPS API origin.</summary>
    public string AuthorityUrl { get; set; } = "";

    /// <summary>Gets or sets the shared persistent scope required for automatic targets; manual targets may retain independent data.</summary>
    public string DataScope { get; set; } = "";

    /// <summary>Gets or sets the native automatic selector whose configured members establish the admitted pool.</summary>
    [Range(1, int.MaxValue)] public int AutomaticSelectorId { get; set; } = 1;

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate()
    {
        if (!TicketValidation.Id(LocalNodeId) || !TicketValidation.Id(AuthorityId) || !TicketValidation.Id(DataScope))
            yield return new("Explicit bounded local node, authentication authority and persistent scope identities are required.");
        if (!Uri.TryCreate(AuthorityUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.AbsolutePath != "/")
            yield return new("authority_url requires an HTTP or HTTPS origin.", [nameof(AuthorityUrl)]);
    }
}

internal sealed class LaunchSelectionSource(LaunchSelectionOptions options, IServerSelectionDirectory directory, TimeProvider? clock = null)
    : ILauncherServerSelection
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    public string LocalNodeId => options.LocalNodeId;

    internal void ValidateConfiguration()
    {
        foreach (var build in directory.ClientBuilds) Capture(build);
    }

    public LauncherServerSelectionSnapshot Capture(string build)
    {
        var now = _clock.GetUtcNow().ToUniversalTime();
        var entries = directory.ListSelectionServers(build);
        var pool = entries.SingleOrDefault(entry => entry.Id == options.AutomaticSelectorId && entry.Automatic)
            ?? throw new InvalidOperationException("The configured automatic selector does not exist.");
        var automaticMembers = pool.Targets.Where(AuthorizedAutomatic).Select(target => target.NodeId).ToHashSet(StringComparer.Ordinal);
        if (pool.Targets.Count > 0 && automaticMembers.Count == 0)
            throw new InvalidOperationException("The automatic selector has no members under the configured issuer authority and persistent scope.");
        var manualMembers = entries.Where(entry => !entry.Automatic).SelectMany(entry => entry.Targets)
            .Where(AuthorizedManual).Select(target => target.NodeId).ToHashSet(StringComparer.Ordinal);
        var members = automaticMembers.Union(manualMembers).ToHashSet(StringComparer.Ordinal);
        foreach (var target in pool.Targets.Where(target => automaticMembers.Contains(target.NodeId)))
            if (entries.Count(entry => !entry.Automatic && entry.Targets.Count == 1 && SameBinding(entry.Targets[0], target)) != 1)
                throw new InvalidOperationException("Each admitted worker requires exactly one canonical manual selector with the same endpoint binding.");
        return new(options.AuthorityId, options.DataScope, now, Array.AsReadOnly(entries.Select(entry => entry with
        {
            Targets = Array.AsReadOnly(entry.Targets.Where(target => members.Contains(target.NodeId) && Authorized(entry, target))
                .Select(target => target with { }).ToArray()),
        }).ToArray()));
    }

    public LauncherServerSelectionSnapshot Refresh(string build, LauncherServerSelectionSnapshot original)
    {
        if (original.AuthorityId != options.AuthorityId || original.DataScope != options.DataScope)
            throw new InvalidOperationException("The launch admission belongs to another authority or persistent scope.");
        var current = Capture(build);
        return current with
        {
            Servers = Array.AsReadOnly(original.Servers.Select(entry => entry with
            {
                Targets = Array.AsReadOnly(entry.Targets.Select(binding => current.Servers
                    .SingleOrDefault(candidate => candidate.Id == entry.Id && candidate.Automatic == entry.Automatic)?.Targets
                    .SingleOrDefault(target => SameBinding(binding, target)
                        && (!entry.Automatic || SameNativeMapping(original, current, binding))))
                    .OfType<ServerNodeTarget>().ToArray()),
            }).ToArray()),
        };
    }

    private bool Authorized(ServerSelectorEntry entry, ServerNodeTarget target) => entry.Automatic
        ? AuthorizedAutomatic(target)
        : AuthorizedManual(target);

    private bool AuthorizedAutomatic(ServerNodeTarget target) => target.DataScope == options.DataScope
        && MasterIssuedByConfiguredAuthority(target);

    private bool AuthorizedManual(ServerNodeTarget target) => MasterIssuedByConfiguredAuthority(target);

    private bool MasterIssuedByConfiguredAuthority(ServerNodeTarget target) =>
        target.Authentication is { Mode: "master-issued", PolicyMode: "authority" } auth
        && auth.AuthorityId == options.AuthorityId && auth.ApiUrl == options.AuthorityUrl;

    private static bool SameBinding(ServerNodeTarget left, ServerNodeTarget right) => left.NodeId == right.NodeId
        && left.Host == right.Host && left.Port == right.Port && left.DataScope == right.DataScope
        && left.JoinScope == right.JoinScope && left.Authentication == right.Authentication;

    private static bool SameNativeMapping(LauncherServerSelectionSnapshot original, LauncherServerSelectionSnapshot current, ServerNodeTarget target)
    {
        var binding = original.Servers.Single(entry => !entry.Automatic && entry.Targets.Any(candidate => SameBinding(candidate, target)));
        return current.Servers.Any(entry => !entry.Automatic && entry.Id == binding.Id
            && entry.Targets.Count == 1 && SameBinding(entry.Targets[0], target));
    }
}

/// <summary>Publishes opt-in, authority-bound launch selection from the configured directory without changing standalone launches.</summary>
[Plugin("cluster.launch-selection", Description = "Authority-bound launch admission and bounded node-health refresh.",
    Requires = [typeof(IServerSelectionDirectory)], Provides = [typeof(ILauncherServerSelection)])]
public sealed class LaunchSelectionPlugin : IPlugin
{
    /// <inheritdoc />
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<LaunchSelectionOptions>();
        var source = new LaunchSelectionSource(options, context.Services.Get<IServerSelectionDirectory>());
        source.ValidateConfiguration();
        context.Effect(context.Services.Register<ILauncherServerSelection>(source));
        return ValueTask.CompletedTask;
    }
}
