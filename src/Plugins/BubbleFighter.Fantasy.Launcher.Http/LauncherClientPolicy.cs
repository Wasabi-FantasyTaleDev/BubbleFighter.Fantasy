using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace BubbleFighter.Fantasy.Launcher.Http;

internal sealed class LauncherClientPolicy
{
    private readonly LauncherVersion? _minimum;
    internal string? MinimumLauncherVersion { get; }
    internal object ClientPolicy { get; }

    internal LauncherClientPolicy(LauncherOptions options)
    {
        MinimumLauncherVersion = options.MinimumLauncherVersion.Length == 0 ? null : options.MinimumLauncherVersion;
        LauncherVersion.TryParse(MinimumLauncherVersion, out _minimum);
        ClientPolicy = Wire(options.ClientPolicy.ToContract());
    }

    // Management labels stay on the server; a rule carries only the fields its type takes.
    internal static object Wire(Contracts.Launcher.ClientPolicy policy) => new
    {
        version = Contracts.Launcher.ClientPolicy.FormatVersion,
        sources = policy.Sources.Select(source => new { name = source.Name, kind = source.Kind, baseUrl = source.BaseUrl, catalogPath = source.CatalogPath }).ToArray(),
        plugins = policy.Plugins.Select(plugin => new
        {
            id = plugin.Id,
            rules = plugin.Rules.Select(rule =>
            {
                var item = new Dictionary<string, object> { ["id"] = rule.Id, ["type"] = rule.Type, ["builds"] = rule.Builds.ToArray() };
                if (rule.Version is not null) item["version"] = rule.Version;
                if (rule.Sources is not null) item["sources"] = rule.Sources.ToArray();
                return item;
            }).ToArray(),
        }).ToArray(),
    };

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
