using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Launcher;

namespace BubbleFighter.Fantasy.Contracts.Installation;

/// <summary>Coordinates durable first-run configuration without coupling installation to an HTTP transport.</summary>
public interface IInstallationService
{
    /// <summary>Returns public progress and choices; credentials and connection secrets are never included.</summary>
    InstallationStatus Status { get; }
    /// <summary>Lists a bounded page of local server directories for the authenticated or bootstrap-authorized folder picker.</summary>
    InstallationDirectoryPage BrowseDirectories(string? path, CancellationToken cancellationToken);
    /// <summary>Imports a local browser-selected folder or ZIP package into the server's private client-data area.</summary>
    ValueTask<string> ImportClientDataAsync(string profile, bool archive, IAsyncEnumerable<InstallationUpload> files, CancellationToken cancellationToken);
    /// <summary>Gets the maximum bytes accepted from one client-data request, including multipart framing.</summary>
    long ClientDataUploadLimit { get; }
    /// <summary>Checks the supplied database through its provider, with bounded time and cancellation.</summary>
    ValueTask ProbeAsync(InstallationSettings settings, CancellationToken cancellationToken);
    /// <summary>Checks an authenticated correction, optionally retaining the saved password for the same database endpoint.</summary>
    ValueTask ProbeUpdateAsync(InstallationUpdate update, CancellationToken cancellationToken);
    /// <summary>Durably claims the first administrator and starts one supervised apply operation; matching retries are idempotent.</summary>
    ValueTask BeginAsync(InstallationRequest request, CancellationToken cancellationToken);
    /// <summary>Retries the retained configuration without replacing its administrator or database selection.</summary>
    ValueTask RetryAsync(CancellationToken cancellationToken);
    /// <summary>Returns the retained candidate for an authenticated operator, with its database secret removed.</summary>
    InstallationConfiguration GetConfiguration();
    /// <summary>Corrects a failed installation, or activates deferred client data without changing durable storage or the administrator.</summary>
    ValueTask UpdateAsync(InstallationUpdate update, CancellationToken cancellationToken);
    /// <summary>Persists client-plugin launch rules without changing server modules, storage, accounts or reward settings.</summary>
    ValueTask UpdateClientPluginsAsync(InstallationClientPluginSettings settings, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This installation provider cannot edit client-plugin policy.");
}

/// <summary>A complete client-plugin override; null restores the profile policy and an empty list explicitly clears it.</summary>
/// <param name="Policies">The store identities and their exact build scopes, or null to inherit the profile.</param>
public sealed record InstallationClientPluginSettings(IReadOnlyList<ClientPluginPolicy>? Policies);

/// <summary>An authenticated configuration response; the password field is always empty.</summary>
/// <param name="Settings">The retained settings with secrets removed.</param>
/// <param name="HasDatabasePassword">Whether a saved MySQL password can be retained on correction.</param>
public sealed record InstallationConfiguration(InstallationSettings Settings, bool HasDatabasePassword);

/// <summary>An authenticated correction of a failed operation.</summary>
/// <param name="Settings">The replacement candidate.</param>
/// <param name="RetainDatabasePassword">Whether to reuse the saved password for the same MySQL account and endpoint.</param>
public sealed record InstallationUpdate(InstallationSettings Settings, bool RetainDatabasePassword);

/// <summary>Configuration edited by the installation assistant; this record must never be exposed as public status.</summary>
public sealed record InstallationSettings
{
    /// <summary>Gets the client-plugin launch rules; null preserves the selected profile's original policy.</summary>
    public IReadOnlyList<ClientPluginPolicy>? ClientPlugins { get; init; }
    /// <summary>Gets the selected client builds and their independent data roots; null reads the older single-profile fields.</summary>
    public IReadOnlyList<InstallationClient>? Clients { get; init; }
    /// <summary>Gets the administration UI language, independently of the selected game's locale.</summary>
    public string Language { get; init; } = "en";
    /// <summary>Gets the operator's display name for this server.</summary>
    public string ServerName { get; init; } = "Bubble Fighter Fantasy";
    /// <summary>Gets the packaged profile to activate, or the explicit management-only profile.</summary>
    public string Profile { get; init; } = "management";
    /// <summary>Gets the absolute root of operator-supplied client data for game profiles.</summary>
    public string ClientDataPath { get; init; } = "";
    /// <summary>Gets whether game activation is postponed while the selected profile is retained for later import.</summary>
    public bool DeferClientDataImport { get; init; }
    /// <summary>Gets the selected durable provider identifier: sqlite or mysql.</summary>
    public string DatabaseKind { get; init; } = "sqlite";
    /// <summary>Gets the absolute SQLite save path; empty selects the installation state's default save.</summary>
    public string SqlitePath { get; init; } = "";
    /// <summary>Gets the MySQL host, without a URI scheme or connection-string syntax.</summary>
    public string MySqlHost { get; init; } = "127.0.0.1";
    /// <summary>Gets the MySQL TCP port.</summary>
    public int MySqlPort { get; init; } = 3306;
    /// <summary>Gets the existing database name supported by the persistence provider.</summary>
    public string MySqlDatabase { get; init; } = "bf_game_db";
    /// <summary>Gets the database account, distinct from the administrator and game accounts.</summary>
    public string MySqlUser { get; init; } = "bff";
    /// <summary>Gets the database secret; only the restricted local installation document may persist it.</summary>
    public string MySqlPassword { get; init; } = "";
    /// <summary>Gets the connector's explicit TLS policy, defaulting to the compatibility mode used by the setup assistant.</summary>
    public string MySqlSslMode { get; init; } = "None";
    /// <summary>
    /// Gets how players sign in: <c>launcher</c> publishes the launcher service and admits only game logins that redeem
    /// its tickets; <c>open</c> admits any account name the client sends and suits only a trusted network.
    /// </summary>
    public string PlayerLogin { get; init; } = "launcher";
    /// <summary>
    /// Gets the host name or IP address game clients connect to. Empty uses the address each player's launcher used to
    /// reach this server, which is right whenever the launcher service and the game run on the same machine.
    /// </summary>
    public string PublicHost { get; init; } = "";
    /// <summary>
    /// Gets whether a launcher sign-in with an unknown account name creates the account, so players need not register.
    /// Used only with <see cref="PlayerLogin"/> <c>launcher</c>; open sign-in always creates unknown accounts.
    /// </summary>
    public bool CreateAccountOnLogin { get; init; }
}

/// <summary>A client build selected for this server, with activation deferred until its prepared data is supplied.</summary>
/// <param name="Profile">The packaged single-build profile id.</param>
/// <param name="ClientDataPath">The absolute parent root containing the profile's data and dataraw directories under its id.</param>
/// <param name="DeferClientDataImport">Whether to retain this choice without activating its game listeners yet.</param>
public sealed record InstallationClient(string Profile, string ClientDataPath, bool DeferClientDataImport);

/// <summary>A bounded directory listing containing names and paths only, never file contents.</summary>
/// <param name="Path">The current absolute server directory, or null for the available local roots.</param>
/// <param name="Parent">The parent directory, or null when at a local root.</param>
/// <param name="Directories">Accessible child directory candidates; symbolic links are excluded.</param>
/// <param name="Truncated">Whether more entries exist; the operator may type a more specific path.</param>
public sealed record InstallationDirectoryPage(string? Path, string? Parent,
    IReadOnlyList<InstallationDirectory> Directories, bool Truncated);

/// <summary>One server-side folder offered in the installation path picker.</summary>
/// <param name="Name">The folder's display name.</param>
/// <param name="Path">The absolute path to submit when opening or selecting the folder.</param>
public sealed record InstallationDirectory(string Name, string Path);

/// <summary>A bounded file stream supplied by the local folder or package picker.</summary>
/// <param name="Name">The browser-relative file name.</param>
/// <param name="Content">The readable stream, valid until the caller advances the enumeration; the caller retains ownership.</param>
public sealed record InstallationUpload(string Name, Stream Content);

/// <summary>One idempotent installation claim; plaintext administrator passwords are only used to derive a hash.</summary>
/// <param name="OperationId">A browser-generated UUID retained for retries.</param>
/// <param name="Settings">The candidate durable runtime configuration.</param>
/// <param name="Username">The separate administration identity.</param>
/// <param name="Password">The administrator password, never written to disk.</param>
public sealed record InstallationRequest(Guid OperationId, InstallationSettings Settings, string Username, string Password);

/// <summary>Public setup progress with no account hashes, database passwords or private exception messages.</summary>
/// <param name="Phase">One of language, applying, ready or failed.</param>
/// <param name="Installed">Whether a runtime activation was durably committed.</param>
/// <param name="HasAdministrator">Whether a candidate has already claimed the first administrator.</param>
/// <param name="Language">The saved UI preference, if a candidate exists.</param>
/// <param name="ServerName">The saved public server name.</param>
/// <param name="Profile">The selected profile, if present.</param>
/// <param name="OperationId">The current claim's id, allowing safe polling after a disconnected response.</param>
/// <param name="ErrorCode">A stable redacted error code, or null.</param>
/// <param name="Profiles">The packaged profile choices.</param>
/// <param name="DefaultSqlitePath">The durable default save path shown before submission.</param>
/// <param name="ClientDataDeferred">Whether administration is ready but game data still needs to be supplied.</param>
public sealed record InstallationStatus(string Phase, bool Installed, bool HasAdministrator, string Language,
    string ServerName, string? Profile, Guid? OperationId, string? ErrorCode,
    IReadOnlyList<InstallationProfile> Profiles, string DefaultSqlitePath, bool ClientDataDeferred = false)
{
    /// <summary>Gets the configured upload limits so the picker can reject an oversized selection before transmission.</summary>
    public InstallationDataLimits? ImportLimits { get; init; }
}

/// <summary>Public import limits for preflight checks in the local file picker; the server enforces them independently.</summary>
/// <param name="UploadBytes">Maximum bytes received in one request, including multipart framing.</param>
/// <param name="FileCount">Maximum folder files or ZIP entries in one import.</param>
public sealed record InstallationDataLimits(long UploadBytes, int FileCount);

/// <summary>One discoverable packaged profile offered by the assistant.</summary>
/// <param name="Id">The profile directory name, validated by the runtime adapter.</param>
/// <param name="Description">A human-readable explanation from packaged data.</param>
public sealed record InstallationProfile(string Id, string? Description)
{
    /// <summary>Gets localized client names for display; selection and persistence continue to use <see cref="Id"/>.</summary>
    public IReadOnlyDictionary<string, string> DisplayNames { get; init; } = new Dictionary<string, string>();

    /// <summary>Gets whether the setup assistant permits selecting this profile for activation.</summary>
    public bool SetupEnabled { get; init; } = true;

    /// <summary>Gets whether the profile is currently allowed in a runtime composition.</summary>
    public bool ActivationEnabled { get; init; } = true;

    /// <summary>Gets whether the profile is an experimental client integration.</summary>
    public bool Experimental { get; init; }
}

/// <summary>Provider-owned database validation and initialization, shared with the native persistence implementation.</summary>
public interface IInstallationDatabase
{
    /// <summary>Gets the stable provider kind accepted by this implementation.</summary>
    string Kind { get; }
    /// <summary>Validates and opens the real save, optionally applying the provider's existing migrations.</summary>
    /// <returns>The validated native provider configuration for profile composition.</returns>
    ValueTask<IReadOnlyDictionary<string, object?>> PrepareAsync(InstallationSettings settings, bool initialize, CancellationToken cancellationToken);
}

/// <summary>Adapts packaged profile composition and normal lifecycle admission for the installation plugin.</summary>
public interface IInstallationRuntime
{
    /// <summary>Gets the explicitly offered packaged profiles.</summary>
    IReadOnlyList<InstallationProfile> Profiles { get; }
    /// <summary>Waits for the bootstrap configuration to converge before requesting another configuration.</summary>
    Task WaitForBootstrapAsync(CancellationToken cancellationToken);
    /// <summary>Composes and admits the desired profile while retaining the installation services, then verifies every enabled row.</summary>
    ValueTask ApplyAsync(InstallationSettings settings, IReadOnlyDictionary<string, object?> databaseConfig, CancellationToken cancellationToken);
}
