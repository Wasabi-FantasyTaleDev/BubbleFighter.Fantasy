using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace BubbleFighter.Fantasy.Launcher.Http;

internal sealed class LauncherClientPolicy
{
    private readonly LauncherVersion? _minimum;
    internal string? MinimumLauncherVersion { get; }
    internal object[] PluginRecommendations { get; }

    internal LauncherClientPolicy(LauncherOptions options)
    {
        MinimumLauncherVersion = options.MinimumLauncherVersion.Length == 0 ? null : options.MinimumLauncherVersion;
        LauncherVersion.TryParse(MinimumLauncherVersion, out _minimum);
        PluginRecommendations = options.PluginRecommendations.GroupBy(p => p.Id, StringComparer.Ordinal).Select(group =>
            (object)new
            {
                id = group.Key,
                builds = group.Any(p => p.Builds.Length == 0) ? Array.Empty<string>() : group.SelectMany(p => p.Builds).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                required = group.Any(p => p.Required),
            }).ToArray();
    }

    internal void Check(HttpContext context, JsonElement body)
    {
        var header = context.Request.Headers["X-FantasyTale-Launcher-Version"];
        if (header.Count > 1) throw new InvalidDataException();
        var headerValue = header.Count == 0 ? null : header[0];
        string? bodyValue = null;
        if (body.TryGetProperty("launcherVersion", out var value))
        {
            if (value.ValueKind != JsonValueKind.String) throw new InvalidDataException();
            bodyValue = value.GetString();
        }
        if (headerValue is not null && bodyValue is not null && !string.Equals(headerValue, bodyValue, StringComparison.Ordinal))
            throw new InvalidDataException();
        var supplied = headerValue ?? bodyValue;
        LauncherVersion? parsed = null;
        if (supplied is not null && !LauncherVersion.TryParse(supplied, out parsed)) throw new InvalidDataException();
        if (_minimum is not null && (parsed is null || parsed.CompareTo(_minimum) < 0)) throw new LauncherUpdateRequiredException();
    }
}

internal sealed class LauncherUpdateRequiredException : Exception { }
