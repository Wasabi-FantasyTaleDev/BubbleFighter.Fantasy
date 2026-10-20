using System.ComponentModel.DataAnnotations;
using System.Net;
using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.Launcher.Http;

/// <summary>Deployment settings for the public launcher API listener and bounded authentication state.</summary>
public sealed class LauncherOptions : IValidatableOptions
{
    /// <summary>Gets or sets the HTTP listener IP and port; use HTTPS termination for remote clients.</summary>
    public string Listen { get; set; } = "127.0.0.1:8090";
    /// <summary>Gets or sets the public server display name.</summary>
    [Required, MaxLength(128)] public string Name { get; set; } = "Bubble Fighter Fantasy";
    /// <summary>Gets or sets auto, ip or domain; explicit routing requirements override all launcher preferences.</summary>
    public string HostPolicy { get; set; } = "auto";
    /// <summary>Gets or sets the deployment region, or empty when unspecified.</summary>
    public string Region { get; set; } = "";
    /// <summary>Gets or sets whether users may choose between installed instances.</summary>
    public bool SupportsInstances { get; set; } = true;
    /// <summary>Gets or sets whether client plugins are allowed by this service.</summary>
    public bool SupportsPlugins { get; set; } = true;
    /// <summary>Gets or sets whether account rank icons may be displayed.</summary>
    public bool LevelIcon { get; set; } = true;
    /// <summary>Gets or sets the minimum Launcher SemVer for registration, login, refresh and tickets; empty disables the gate.</summary>
    public string MinimumLauncherVersion { get; set; } = "";
    /// <summary>Gets or sets at most 64 plugin identity recommendations, resolved by the launcher's configured stores.</summary>
    public LauncherPluginRecommendation[] PluginRecommendations { get; set; } = [];
    /// <summary>Gets or sets whether account registration is open.</summary>
    public bool RegistrationEnabled { get; set; } = true;
    /// <summary>
    /// Gets or sets whether signing in with an unknown account name creates that account with the given password, so
    /// players never register first. Name typos create new accounts; leave it off where accounts are managed.
    /// </summary>
    public bool CreateAccountOnLogin { get; set; }
    /// <summary>Gets or sets whether existing identities without a password may enroll one on their first launcher login.</summary>
    public bool AllowLegacyPasswordEnrollment { get; set; }
    /// <summary>Gets or sets PBKDF2 SHA-256 work per password verification.</summary>
    [Range(100000, 2000000)] public int PasswordIterations { get; set; } = 600000;
    /// <summary>Gets or sets the minimum password length for registration and initial legacy enrollment.</summary>
    [Range(6, 128)] public int MinimumPasswordLength { get; set; } = 8;
    /// <summary>Gets or sets the maximum simultaneously retained sessions and launch tickets, each.</summary>
    [Range(1, 100000)] public int MaxSessions { get; set; } = 1024;
    /// <summary>Gets or sets live durable sign-ins per account; a new sign-in revokes the oldest. Defaults to 8.</summary>
    [Range(1, 1024)] public int MaxSessionsPerAccount { get; set; } = 8;
    /// <summary>Gets or sets the access bearer lifetime in seconds; the launcher renews it from its refresh credential.</summary>
    [Range(60, 86400)] public int SessionSeconds { get; set; } = 1800;
    /// <summary>Gets or sets how long a sign-in stays renewable without a password, in seconds; every renewal restarts it.</summary>
    [Range(120, 31536000)] public int RefreshSessionSeconds { get; set; } = 2592000;
    /// <summary>Gets or sets single-use launch ticket lifetime in seconds.</summary>
    [Range(10, 600)] public int TicketSeconds { get; set; } = 60;
    /// <summary>Gets or sets the request budget per transport peer IP in a one-minute window.</summary>
    [Range(1, 100000)] public int RequestsPerMinute { get; set; } = 120;
    /// <summary>Gets or sets retained IP request windows; the oldest window is evicted at capacity. Defaults to 4096.</summary>
    [Range(1, 100000)] public int MaxRateLimitAddresses { get; set; } = 4096;
    /// <summary>Gets or sets concurrent API work, including password hashing.</summary>
    [Range(1, 128)] public int MaxConcurrentRequests { get; set; } = 8;
    /// <summary>Gets or sets maximum incoming JSON body bytes.</summary>
    [Range(1024, 65536)] public int MaxBodyBytes { get; set; } = 16384;
    /// <summary>Gets or sets the bound on accepted HTTP connections.</summary>
    [Range(1, 10000)] public int MaxConnections { get; set; } = 128;
    /// <summary>Gets or sets the HTTP header and keep-alive timeout in seconds.</summary>
    [Range(1, 120)] public int HttpTimeoutSeconds { get; set; } = 15;
    /// <summary>Gets or sets graceful unload timeout in seconds.</summary>
    [Range(1, 60)] public int ShutdownSeconds { get; set; } = 5;
    /// <summary>Gets or sets a trusted HTTPS rank-icon template containing {level}, or empty for numeric levels.</summary>
    public string LevelIconTemplate { get; set; } = "";
    /// <summary>Gets or sets server-provided client presets, independent of users' installed instances.</summary>
    public LauncherInstanceOptions[] Instances { get; set; } = [];

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate()
    {
        if (MinimumLauncherVersion is null || MinimumLauncherVersion.Length != 0 && !LauncherVersion.TryParse(MinimumLauncherVersion, out _))
            yield return new("minimum_launcher_version must be empty or a strict SemVer of at most 128 characters.", [nameof(MinimumLauncherVersion)]);
        if (PluginRecommendations is null || PluginRecommendations.Length > 64)
            yield return new("plugin_recommendations must contain at most 64 entries.", [nameof(PluginRecommendations)]);
        else
        {
            foreach (var recommendation in PluginRecommendations)
                if (recommendation is null || recommendation.Id is null || recommendation.Id.Length is < 1 or > 128 ||
                    !recommendation.Id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_') ||
                    recommendation.Builds is null || recommendation.Builds.Length > 64 || recommendation.Builds.Any(b => b is null || !ValidId(b)))
                    yield return new("Each plugin recommendation needs an identity and at most 64 valid build ids.", [nameof(PluginRecommendations)]);
            foreach (var group in PluginRecommendations.Where(p => p is not null && p.Builds is not null).GroupBy(p => p.Id, StringComparer.Ordinal))
                if (!group.Any(p => p.Builds.Length == 0) && group.SelectMany(p => p.Builds).Distinct(StringComparer.Ordinal).Take(65).Count() > 64)
                    yield return new("Merged plugin recommendations must contain at most 64 build ids per identity.", [nameof(PluginRecommendations)]);
        }
        if (HostPolicy is not ("auto" or "ip" or "domain")) yield return new("host_policy must be auto, ip or domain.", [nameof(HostPolicy)]);
        if (Region is null || Region.Length != 0 && (Region.Length != 2 || !Region.All(char.IsAsciiLetterUpper))) yield return new("region must be an uppercase two-letter region code or empty.", [nameof(Region)]);
        if (!IPEndPoint.TryParse(Listen, out _)) yield return new("listen must be an IP address and port.", [nameof(Listen)]);
        if (RefreshSessionSeconds <= SessionSeconds) yield return new("refresh_session_seconds must be longer than session_seconds.", [nameof(RefreshSessionSeconds)]);
        if (Instances is null || Instances.Length > 64) { yield return new("instances must contain at most 64 entries.", [nameof(Instances)]); yield break; }
        if (Instances.Any(i => i is null)) { yield return new("instances must not contain null entries.", [nameof(Instances)]); yield break; }
        if (Instances.Select(i => i.Id).Distinct(StringComparer.Ordinal).Count() != Instances.Length) yield return new("instance ids must be unique.", [nameof(Instances)]);
        foreach (var instance in Instances)
        {
            if (!ValidId(instance.Id) || !ValidId(instance.Build) || instance.Name is null || instance.Name.Length == 0 || !instance.Name.Any(n => n is not null && n.Language == "en") || instance.Name.Length > 16 || instance.Name.Any(n => n is null || string.IsNullOrWhiteSpace(n.Text) || n.Text.Length > 128 || string.IsNullOrWhiteSpace(n.Language) || n.Language.Length > 16) || instance.Name.Select(n => n.Language).Distinct(StringComparer.Ordinal).Count() != instance.Name.Length ||
                instance.Host is null || instance.Host.Length > 0 && Uri.CheckHostName(instance.Host) == UriHostNameType.Unknown || instance.Port is < 1 or > 65535 || instance.LauncherLogin is not ("required" or "optional" or "none") || instance.ReleaseVersion is null || instance.ReleaseVersion.Length > 64 || instance.ClientLanguages is not null && (instance.ClientLanguages.Length > 6 || instance.ClientLanguages.Any(l => l is not ("zh-CN" or "zh-TW" or "ko" or "id" or "vi" or "en")) || instance.ClientLanguages.Distinct(StringComparer.Ordinal).Count() != instance.ClientLanguages.Length))
                yield return new("Each instance needs valid id, build, localized name, endpoint and login policy.", [nameof(Instances)]);
            if (instance.DownloadSource is { } source)
                foreach (var error in source.Validate()) yield return error;
        }
        if (LevelIconTemplate is null || LevelIconTemplate.Length > 0 && (!Uri.TryCreate(LevelIconTemplate.Replace("{level}", "1", StringComparison.Ordinal), UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length != 0 || LevelIconTemplate.Length > 2048))
            yield return new("level_icon_template must be HTTPS without credentials.", [nameof(LevelIconTemplate)]);
    }

    private static bool ValidId(string? value) => value is { Length: > 0 and <= 64 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
}

/// <summary>A plugin store identity with an optional exact-build scope; no download address or executable path is accepted.</summary>
public sealed class LauncherPluginRecommendation
{
    /// <summary>Gets or sets the plugin identity resolved through the launcher's configured stores.</summary>
    public string Id { get; set; } = "";
    /// <summary>Gets or sets the applicable catalog build ids; an empty list applies to every build.</summary>
    public string[] Builds { get; set; } = [];
    /// <summary>Gets or sets whether the launcher must enable this plugin before launching an applicable build.</summary>
    public bool Required { get; set; }
}

/// <summary>A deployer's advertised client preset and its connection destination.</summary>
public sealed class LauncherInstanceOptions
{
    /// <summary>Gets or sets the stable preset identifier.</summary>
    public string Id { get; set; } = "";
    /// <summary>Gets or sets the wire-version client build id, such as kr-1256.</summary>
    public string Build { get; set; } = "";
    /// <summary>Gets or sets localized region-and-year labels.</summary>
    public LauncherLocalizedName[] Name { get; set; } = [];
    /// <summary>Gets or sets the optional release subversion displayed below the title.</summary>
    public string ReleaseVersion { get; set; } = "";
    /// <summary>Gets or sets an optional authoritative list of client language choices.</summary>
    public string[]? ClientLanguages { get; set; }
    /// <summary>Gets or sets the public game hostname, independently of the API bind address; empty advertises the host the launcher used to reach the API.</summary>
    public string Host { get; set; } = "127.0.0.1";
    /// <summary>Gets or sets the game connection port.</summary>
    public int Port { get; set; }
    /// <summary>Gets or sets required, optional or none for launcher authentication.</summary>
    public string LauncherLogin { get; set; } = "required";
    /// <summary>Gets or sets an optional public store recommendation for this build's client package.</summary>
    public LauncherDownloadSourceOptions? DownloadSource { get; set; }
}

/// <summary>A public store location advertised for explicit client installation by the launcher.</summary>
public sealed class LauncherDownloadSourceOptions
{
    /// <summary>Gets or sets http or alist for resolving the common store catalog.</summary>
    public string Kind { get; set; } = "http";
    /// <summary>Gets or sets the HTTP(S) source root without credentials, query or fragment.</summary>
    public string BaseUrl { get; set; } = "";
    /// <summary>Gets or sets the unescaped catalog path relative to the source root.</summary>
    public string CatalogPath { get; set; } = "FantasyTale/source.toml";
    /// <summary>Gets or sets the catalog's client package identifier, independent of the game build identifier.</summary>
    public string PackageId { get; set; } = "";

    internal IEnumerable<ValidationResult> Validate()
    {
        if (Kind is not ("http" or "alist"))
            yield return new("download_source.kind must be http or alist.", [nameof(Kind)]);
        if (string.IsNullOrWhiteSpace(BaseUrl) || BaseUrl.Length > 2048 || BaseUrl.Any(char.IsControl) ||
            !Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            uri.Host.Length == 0 || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            yield return new("download_source.base_url must be HTTP(S) without credentials, query or fragment.", [nameof(BaseUrl)]);
        if (string.IsNullOrWhiteSpace(CatalogPath) || CatalogPath.Length > 1024 || CatalogPath.Any(char.IsControl) ||
            CatalogPath.IndexOfAny(['\\', ':', '?', '#', '%']) >= 0 ||
            CatalogPath.Split('/').Any(part => part.Length == 0 || part is "." or ".." || part != part.Trim()))
            yield return new("download_source.catalog_path must be a relative, unescaped path without traversal or empty segments.", [nameof(CatalogPath)]);
        if (PackageId is null || PackageId.Length is < 1 or > 96 ||
            !PackageId.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-'))
            yield return new("download_source.package_id must contain 1..96 characters of a-z, 0-9, period or hyphen.", [nameof(PackageId)]);
    }
}

/// <summary>A language-tagged display name represented as data supported by the row binder.</summary>
public sealed class LauncherLocalizedName
{
    /// <summary>Gets or sets the BCP 47 language tag, such as en or zh-CN.</summary>
    public string Language { get; set; } = "";
    /// <summary>Gets or sets the region-and-year title rendered by the launcher.</summary>
    public string Text { get; set; } = "";
}
