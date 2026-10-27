namespace BubbleFighter.Fantasy.Contracts.Launcher;

/// <summary>A store-resolved client plugin policy scoped to exact builds, independent of server-installed modules.</summary>
/// <param name="Id">The store identity, without a download address or local path.</param>
/// <param name="Builds">Exact build IDs; an empty list applies to every build.</param>
/// <param name="Required">Whether applicable launches must enable the plugin.</param>
/// <param name="Disabled">Whether applicable launches must disable the plugin.</param>
public sealed record ClientPluginPolicy(string Id, IReadOnlyList<string> Builds, bool Required = false, bool Disabled = false);

/// <summary>Validates the shared Launcher policy format before persistence or listener activation.</summary>
public static class ClientPluginPolicyRules
{
    /// <summary>Rejects invalid identities, existing metadata bounds and overlapping required/disabled scopes.</summary>
    /// <param name="policies">The complete candidate, rather than only its changed rows.</param>
    public static void Validate(IReadOnlyList<ClientPluginPolicy> policies)
    {
        ArgumentNullException.ThrowIfNull(policies);
        // These are the existing Launcher metadata bounds, shared by configuration and graphical editing.
        if (policies.Count > 64) throw new ArgumentException("plugin_policy_invalid", nameof(policies));
        foreach (var policy in policies)
        {
            if (policy is null || policy.Id is not { Length: > 0 and <= 128 } ||
                !policy.Id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_') ||
                policy.Builds is null || policy.Builds.Count > 64 ||
                policy.Builds.Any(build => build is not { Length: > 0 and <= 64 } ||
                    !build.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')) || policy.Required && policy.Disabled)
                throw new ArgumentException("plugin_policy_invalid", nameof(policies));
        }
        foreach (var group in policies.GroupBy(policy => policy.Id, StringComparer.Ordinal))
        {
            if (!group.Any(policy => policy.Builds.Count == 0) &&
                group.SelectMany(policy => policy.Builds).Distinct(StringComparer.Ordinal).Take(65).Count() > 64)
                throw new ArgumentException("plugin_policy_invalid", nameof(policies));
            foreach (var required in group.Where(policy => policy.Required))
                if (group.Any(disabled => disabled.Disabled &&
                    (required.Builds.Count == 0 || disabled.Builds.Count == 0 || required.Builds.Intersect(disabled.Builds, StringComparer.Ordinal).Any())))
                    throw new ArgumentException("plugin_policy_conflict", nameof(policies));
        }
    }
}
