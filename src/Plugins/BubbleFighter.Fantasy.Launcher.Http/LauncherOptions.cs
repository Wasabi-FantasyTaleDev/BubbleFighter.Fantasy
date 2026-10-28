using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Net;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Launcher;

namespace BubbleFighter.Fantasy.Launcher.Http;

/// <summary>Deployment settings for the public launcher API listener and bounded authentication state.</summary>
public sealed class LauncherOptions : IValidatableOptions
{
    /// <summary>Gets or sets the HTTP listener IP and port; use HTTPS termination for remote clients.</summary>
    public string Listen { get; set; } = "127.0.0.1:8090";
    /// <summary>Gets or sets the public server display name.</summary>
    [Required] public string Name { get; set; } = "Bubble Fighter Fantasy";
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
    /// <summary>Gets or sets the public multiline server description, rendered as plain text.</summary>
    public string Motd { get; set; } = "";
    /// <summary>Gets or sets a public HTTP(S) icon URL, or empty for the launcher's default icon.</summary>
    public string IconUrl { get; set; } = "";
    /// <summary>Gets or sets public HTTP(S) screenshot URLs.</summary>
    public string[] Images { get; set; } = [];
    /// <summary>Gets or sets whether the public roster discloses authenticated display names and levels.</summary>
    public bool RosterEnabled { get; set; } = true;
    /// <summary>Gets or sets the maximum number of names returned by one public roster request.</summary>
    [Range(1, int.MaxValue)] public int MaxRosterPlayers { get; set; } = 1000;
    /// <summary>Gets or sets whether the catalog exposes the current authenticated-player count.</summary>
    public bool ShowOnlinePlayers { get; set; } = true;
    /// <summary>Gets or sets the launcher admission limit; null allows any number of players.</summary>
    [Range(1, int.MaxValue)] public int? PlayerLimit { get; set; }
    /// <summary>Gets or sets whether launcher sign-ins and new launch tickets are refused for maintenance.</summary>
    public bool Maintenance { get; set; }
    /// <summary>Gets or sets the announced maintenance start; the maintenance switch controls admission.</summary>
    public string? MaintenanceStart { get; set; }
    /// <summary>Gets or sets the announced maintenance end; null leaves the end unspecified.</summary>
    public string? MaintenanceEnd { get; set; }
    /// <summary>Gets or sets the public plain-text explanation attached to maintenance refusals.</summary>
    public string MaintenanceDescription { get; set; } = "";
    /// <summary>Gets or sets whether only listed accounts may sign in or obtain new launch tickets.</summary>
    public bool WhitelistEnabled { get; set; }
    /// <summary>Gets or sets the account login names allowed by launcher admission.</summary>
    public string[] Whitelist { get; set; } = [];
    /// <summary>Gets or sets the minimum Launcher SemVer for registration, login, refresh and tickets; empty disables the gate.</summary>
    public string MinimumLauncherVersion { get; set; } = "";
    /// <summary>Gets or sets the client policy sent to the Launcher: store sources and typed rules per plugin.</summary>
    public LauncherClientPolicyOptions ClientPolicy { get; set; } = new();
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
    [Range(6, int.MaxValue)] public int MinimumPasswordLength { get; set; } = 6;
    /// <summary>Gets or sets the maximum simultaneously retained sessions and launch tickets, each.</summary>
    [Range(1, int.MaxValue)] public int MaxSessions { get; set; } = 1024;
    /// <summary>Gets or sets live durable sign-ins per account; a new sign-in revokes the oldest. Defaults to 8.</summary>
    [Range(1, int.MaxValue)] public int MaxSessionsPerAccount { get; set; } = 8;
    /// <summary>Gets or sets the access bearer lifetime in seconds; the launcher renews it from its refresh credential.</summary>
    [Range(60, int.MaxValue)] public int SessionSeconds { get; set; } = 1800;
    /// <summary>Gets or sets how long a sign-in stays renewable without a password, in seconds; every renewal restarts it.</summary>
    [Range(120, int.MaxValue)] public int RefreshSessionSeconds { get; set; } = 2592000;
    /// <summary>Gets or sets the single-use launch ticket lifetime in seconds; defaults to ten minutes to allow client startup.</summary>
    [Range(10, int.MaxValue)] public int TicketSeconds { get; set; } = 600;
    /// <summary>Gets or sets the request budget per transport peer IP in a one-minute window.</summary>
    [Range(1, int.MaxValue)] public int RequestsPerMinute { get; set; } = 120;
    /// <summary>Gets or sets retained IP request windows; the oldest window is evicted at capacity. Defaults to 4096.</summary>
    [Range(1, int.MaxValue)] public int MaxRateLimitAddresses { get; set; } = 4096;
    /// <summary>Gets or sets concurrent API work, including password hashing.</summary>
    [Range(1, int.MaxValue)] public int MaxConcurrentRequests { get; set; } = 8;
    /// <summary>Gets or sets maximum incoming JSON body bytes.</summary>
    [Range(1024, int.MaxValue)] public int MaxBodyBytes { get; set; } = 16384;
    /// <summary>Gets or sets the bound on accepted HTTP connections.</summary>
    [Range(1, int.MaxValue)] public int MaxConnections { get; set; } = 128;
    /// <summary>Gets or sets the HTTP header and keep-alive timeout in seconds.</summary>
    [Range(1, int.MaxValue)] public int HttpTimeoutSeconds { get; set; } = 15;
    /// <summary>Gets or sets graceful unload timeout in seconds.</summary>
    [Range(1, int.MaxValue)] public int ShutdownSeconds { get; set; } = 5;
    /// <summary>Gets or sets a trusted HTTPS rank-icon template containing {level}, or empty for numeric levels.</summary>
    public string LevelIconTemplate { get; set; } = "";
    /// <summary>
    /// Gets or sets this API's public HTTPS base URL, or empty. When set it overrides the address launchers and game
    /// clients were given, so their sign-ins travel encrypted; when empty they use the address as entered (HTTP works).
    /// </summary>
    public string SecureUrl { get; set; } = "";
    /// <summary>Gets or sets server-provided client presets, independent of users' installed instances.</summary>
    public LauncherInstanceOptions[] Instances { get; set; } = [];

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate()
    {
        if (SecureUrl.Length > 0 && (!Uri.TryCreate(SecureUrl, UriKind.Absolute, out var secure) || secure.Scheme != Uri.UriSchemeHttps
            || secure.Query.Length > 0 || secure.Fragment.Length > 0))
            yield return new("secure_url must be an absolute https URL without query or fragment.", [nameof(SecureUrl)]);
        if (Motd is null || Motd.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')) ||
            MaintenanceDescription is null || MaintenanceDescription.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')))
            yield return new("Server descriptions must be plain text without control characters.", [nameof(Motd), nameof(MaintenanceDescription)]);
        if (IconUrl is null || IconUrl.Length > 0 && !PublicImage(IconUrl) || Images is null || Images.Any(i => !PublicImage(i)))
            yield return new("icon_url and images must be public HTTP(S) URLs without credentials.", [nameof(IconUrl), nameof(Images)]);
        var validStart = MaintenanceTime(MaintenanceStart, out var start);
        var validEnd = MaintenanceTime(MaintenanceEnd, out var end);
        if (!validStart || !validEnd)
            yield return new("Maintenance times must be ISO 8601 timestamps with seconds and an explicit UTC offset, or null.", [nameof(MaintenanceStart), nameof(MaintenanceEnd)]);
        if (validStart && validEnd && MaintenanceStart is not null && MaintenanceEnd is not null && end <= start)
            yield return new("maintenance_end must follow maintenance_start.", [nameof(MaintenanceEnd)]);
        if (Whitelist is null || Whitelist.Any(n => n is null || n.Length is < 3 or > 64 || !n.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.')) ||
            Whitelist.Distinct(StringComparer.OrdinalIgnoreCase).Count() != Whitelist.Length)
            yield return new("whitelist must contain distinct valid account names.", [nameof(Whitelist)]);
        if (MinimumLauncherVersion is null || MinimumLauncherVersion.Length != 0 && !LauncherVersion.TryParse(MinimumLauncherVersion, out _))
            yield return new("minimum_launcher_version must be empty or a strict SemVer.", [nameof(MinimumLauncherVersion)]);
        ValidationResult? policyError = null;
        try { ClientPolicyRules.Validate((ClientPolicy ?? throw new ClientPolicyException("client_policy_invalid")).ToContract()); }
        catch (ClientPolicyException error)
        {
            policyError = new($"client_policy is invalid ({error.Code}{(error.Plugin is null ? "" : $", plugin '{error.Plugin}'")}" +
                $"{(error.Rule is null ? "" : $", rule '{error.Rule}'")}{(error.Other is null ? "" : $" and rule '{error.Other}'")}).", [nameof(ClientPolicy)]);
        }
        if (policyError is not null) yield return policyError;
        if (HostPolicy is not ("auto" or "ip" or "domain")) yield return new("host_policy must be auto, ip or domain.", [nameof(HostPolicy)]);
        if (Region is null || Region.Length != 0 && (Region.Length != 2 || !Region.All(char.IsAsciiLetterUpper))) yield return new("region must be an uppercase two-letter region code or empty.", [nameof(Region)]);
        if (!IPEndPoint.TryParse(Listen, out _)) yield return new("listen must be an IP address and port.", [nameof(Listen)]);
        if (RefreshSessionSeconds <= SessionSeconds) yield return new("refresh_session_seconds must be longer than session_seconds.", [nameof(RefreshSessionSeconds)]);
        if (Instances is null) { yield return new("instances must be a list.", [nameof(Instances)]); yield break; }
        if (Instances.Any(i => i is null)) { yield return new("instances must not contain null entries.", [nameof(Instances)]); yield break; }
        if (Instances.Select(i => i.Id).Distinct(StringComparer.Ordinal).Count() != Instances.Length) yield return new("instance ids must be unique.", [nameof(Instances)]);
        foreach (var instance in Instances)
        {
            if (!ValidId(instance.Id) || !ValidId(instance.Build) || instance.Name is null || instance.Name.Length == 0 || !instance.Name.Any(n => n is not null && n.Language == "en") || instance.Name.Any(n => n is null || string.IsNullOrWhiteSpace(n.Text) || string.IsNullOrWhiteSpace(n.Language)) || instance.Name.Select(n => n.Language).Distinct(StringComparer.Ordinal).Count() != instance.Name.Length ||
                instance.Host is null || instance.Host.Length > 0 && Uri.CheckHostName(instance.Host) == UriHostNameType.Unknown || instance.Port is < 1 or > 65535 || instance.LauncherLogin is not ("required" or "optional" or "none" or "game") || instance.ReleaseVersion is null || instance.ReleaseVersion.Any(char.IsControl) || instance.PatchVersion is null || instance.PatchVersion.Any(char.IsControl) || instance.ProtocolVersion is < 1 or > 65535 || instance.ClientLanguages is not null && (instance.ClientLanguages.Any(l => l is not ("zh-CN" or "zh-TW" or "ko" or "id" or "vi" or "en")) || instance.ClientLanguages.Distinct(StringComparer.Ordinal).Count() != instance.ClientLanguages.Length))
                yield return new("Each instance needs valid id, build, localized name, endpoint and login policy.", [nameof(Instances)]);
            if (instance.DownloadSource is { } source)
                foreach (var error in source.Validate()) yield return error;
        }
        if (LevelIconTemplate is null || LevelIconTemplate.Length > 0 && (!Uri.TryCreate(LevelIconTemplate.Replace("{level}", "1", StringComparison.Ordinal), UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length != 0))
            yield return new("level_icon_template must be HTTPS without credentials.", [nameof(LevelIconTemplate)]);
    }

    private static bool ValidId(string? value) => value is { Length: > 0 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    private static bool PublicImage(string? value) => value is { Length: > 0 } && !value.Any(char.IsControl) &&
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.Host.Length > 0 && uri.UserInfo.Length == 0;

    private static bool MaintenanceTime(string? value, out DateTimeOffset time)
    {
        time = default;
        return value is null || DateTimeOffset.TryParseExact(value,
            ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", "yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz"],
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out time);
    }
}

/// <summary>The configured client policy; <see cref="ToContract"/> gives the shared record the validator and the wire use.</summary>
public sealed class LauncherClientPolicyOptions
{
    /// <summary>Gets or sets the store sources rules may name.</summary>
    public LauncherPolicySourceOptions[] Sources { get; set; } = [];
    /// <summary>Gets or sets one entry per plugin identity with its rules.</summary>
    public LauncherPolicyPluginOptions[] Plugins { get; set; } = [];

    /// <summary>Converts the bound configuration to the shared record; null entries stay null so validation rejects them.</summary>
    public ClientPolicy ToContract() => new(
        (Sources ?? throw new ClientPolicyException("client_policy_invalid")).Select(source => source is null ? null! :
            new ClientPolicySource(source.Name, source.Kind, source.BaseUrl, source.CatalogPath)).ToArray(),
        (Plugins ?? throw new ClientPolicyException("client_policy_invalid")).Select(plugin => plugin is null ? null! :
            new ClientPluginRules(plugin.Id, plugin.Name, plugin.Description, (plugin.Rules ?? throw new ClientPolicyException("client_policy_invalid", plugin.Id))
                .Select(rule => rule is null ? null! : new ClientPolicyRule(rule.Id, rule.Type, rule.Builds, rule.Version, rule.Sources)).ToArray())).ToArray());
}

/// <summary>A configured store source, referred to by name from recommendation rules.</summary>
public sealed class LauncherPolicySourceOptions
{
    /// <summary>Gets or sets the lowercase name rules use.</summary>
    public string Name { get; set; } = "";
    /// <summary>Gets or sets http or alist.</summary>
    public string Kind { get; set; } = "http";
    /// <summary>Gets or sets the HTTP(S) source root without credentials, query or fragment.</summary>
    public string BaseUrl { get; set; } = "";
    /// <summary>Gets or sets the unescaped catalog path relative to the source root.</summary>
    public string CatalogPath { get; set; } = "FantasyTale/source.toml";
}

/// <summary>The rules for one plugin identity.</summary>
public sealed class LauncherPolicyPluginOptions
{
    /// <summary>Gets or sets the store identity resolved through the Launcher's stores.</summary>
    public string Id { get; set; } = "";
    /// <summary>Gets or sets an optional management label; it is not sent to the Launcher.</summary>
    public string? Name { get; set; }
    /// <summary>Gets or sets an optional management note; it is not sent to the Launcher.</summary>
    public string? Description { get; set; }
    /// <summary>Gets or sets the plugin's typed rules.</summary>
    public LauncherPolicyRuleOptions[] Rules { get; set; } = [];
}

/// <summary>One typed rule; see the shared rule types for which fields each type takes.</summary>
public sealed class LauncherPolicyRuleOptions
{
    /// <summary>Gets or sets the rule's stable identity within its plugin.</summary>
    public string Id { get; set; } = "";
    /// <summary>Gets or sets require, disable, recommend, min_version, max_version or latest.</summary>
    public string Type { get; set; } = "";
    /// <summary>Gets or sets the applicable build IDs; an empty list applies to every build.</summary>
    public string[] Builds { get; set; } = [];
    /// <summary>Gets or sets the version bound of a version rule.</summary>
    public string? Version { get; set; }
    /// <summary>Gets or sets the source names a recommendation installs from.</summary>
    public string[]? Sources { get; set; }
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
    /// <summary>Gets or sets the optional numeric wire version advertised for this preset.</summary>
    public int? ProtocolVersion { get; set; }
    /// <summary>Gets or sets an optional distribution patch label, independent of the wire version.</summary>
    public string PatchVersion { get; set; } = "";
    /// <summary>Gets or sets an optional authoritative list of client language choices.</summary>
    public string[]? ClientLanguages { get; set; }
    /// <summary>Gets or sets the public game hostname, independently of the API bind address; empty advertises the host the launcher used to reach the API.</summary>
    public string Host { get; set; } = "127.0.0.1";
    /// <summary>Gets or sets the game connection port.</summary>
    public int Port { get; set; }
    /// <summary>
    /// Gets or sets required, optional or none for launcher authentication, or game for a client whose own login
    /// screen signs in: the launcher launches without a sign-in and the game binds the launch with the player's.
    /// </summary>
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
        if (string.IsNullOrWhiteSpace(BaseUrl) || BaseUrl.Any(char.IsControl) ||
            !Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            uri.Host.Length == 0 || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            yield return new("download_source.base_url must be HTTP(S) without credentials, query or fragment.", [nameof(BaseUrl)]);
        if (string.IsNullOrWhiteSpace(CatalogPath) || CatalogPath.Any(char.IsControl) ||
            CatalogPath.IndexOfAny(['\\', ':', '?', '#', '%']) >= 0 ||
            CatalogPath.Split('/').Any(part => part.Length == 0 || part is "." or ".." || part != part.Trim()))
            yield return new("download_source.catalog_path must be a relative, unescaped path without traversal or empty segments.", [nameof(CatalogPath)]);
        if (PackageId is null || PackageId.Length == 0 ||
            !PackageId.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-'))
            yield return new("download_source.package_id must contain only a-z, 0-9, period or hyphen.", [nameof(PackageId)]);
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
