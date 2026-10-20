using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Admin;
using BubbleFighter.Fantasy.Contracts.Installation;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Installation;

internal sealed class InstallationService : IInstallationService, IAdminAccessControl, IDisposable
{
    // Schema v1 fixes the hash algorithm and work factor; changing them requires a state migration.
    private const int HashIterations = 600000;
    private readonly IPluginContext _context;
    private readonly InstallationOptions _options;
    private readonly IInstallationRuntime _runtime;
    private readonly IReadOnlyList<IInstallationDatabase> _databases;
    private readonly InstallationStore _store;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _mutation = new(1, 1);
    private readonly SemaphoreSlim _database = new(1, 1);
    private readonly SemaphoreSlim _login = new(1, 1);
    private readonly Dictionary<string, DateTimeOffset> _sessions = new(StringComparer.Ordinal);
    private InstallationDocument? _document;
    private string _phase = "language";
    private string? _error;
    private bool _running;
    private readonly Dictionary<IPAddress, LoginWindow> _loginWindows = new();
    private sealed record LoginWindow(long Started, int Failures);
    private int _importActive;

    public InstallationService(IPluginContext context, InstallationOptions options, IInstallationRuntime runtime,
        IReadOnlyList<IInstallationDatabase> databases)
    {
        _context = context; _options = options; _runtime = runtime; _databases = databases;
        _store = new InstallationStore(options.StateDirectory);
        try
        {
            _document = _store.Load();
            if (_document is not null) Validate(_document.Settings);
        }
        catch { _store.Dispose(); throw; }
    }

    public bool HasAdministrator { get { lock (_sync) return _document is not null; } }
    public bool AllowsRemoteBootstrap => _options.BootstrapToken.Length >= 32;

    public InstallationStatus Status
    {
        get
        {
            lock (_sync) return new(_phase, _document?.Installed ?? false, _document is not null,
                _document?.Settings.Language ?? "en", _document?.Settings.ServerName ?? "Bubble Fighter Fantasy",
                _document?.Settings.Profile, _document?.OperationId, _error, _runtime.Profiles, DefaultSqlitePath,
                _document is { } document && HasDeferredClients(document.Settings))
            { ImportLimits = new(_options.ClientDataUploadBytes, _options.ClientDataFileLimit) };
        }
    }


    private string DefaultSqlitePath => Path.Combine(_options.StateDirectory, "saves", "fantasy.db");

    private static bool HasDeferredClients(InstallationSettings settings) =>
        settings.Clients?.Any(client => client.DeferClientDataImport) ?? settings.DeferClientDataImport;

    public InstallationDirectoryPage BrowseDirectories(string? path, CancellationToken cancellationToken) =>
        InstallationDirectoryBrowser.Browse(path, _options.DirectoryEntryLimit, cancellationToken);

    public long ClientDataUploadLimit => _options.ClientDataUploadBytes;

    public async ValueTask<string> ImportClientDataAsync(string profile, bool archive,
        IAsyncEnumerable<InstallationUpload> files, CancellationToken cancellationToken)
    {
        if (profile == "management" || !_runtime.Profiles.Any(candidate => candidate.Id == profile))
            throw new ArgumentException("client_data_invalid");
        cancellationToken.ThrowIfCancellationRequested();
        _context.Lifetime.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _importActive, 1, 0) != 0)
            throw new InvalidOperationException("client_data_busy");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _context.Lifetime);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.ClientDataImportSeconds));
        try
        {
            return await new InstallationClientDataImporter(_options, _runtime.Profiles.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase))
                .ImportAsync(profile, archive, files, timeout.Token).ConfigureAwait(false);
        }
        finally { Interlocked.Exchange(ref _importActive, 0); }
    }

    public bool AcceptsBootstrapToken(string? token) => AllowsRemoteBootstrap && token is { Length: <= 256 } &&
        CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(token)),
            SHA256.HashData(Encoding.UTF8.GetBytes(_options.BootstrapToken)));

    public async ValueTask ProbeAsync(InstallationSettings settings, CancellationToken cancellationToken)
    {
        settings = Normalize(settings);
        Validate(settings);
        if (!await _database.WaitAsync(0, cancellationToken).ConfigureAwait(false)) throw new InvalidOperationException("operation_busy");
        try { await PrepareDatabaseAsync(settings, false, cancellationToken).ConfigureAwait(false); }
        finally { _database.Release(); }
    }

    public async ValueTask BeginAsync(InstallationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await _mutation.WaitAsync(0, cancellationToken).ConfigureAwait(false)) throw new InvalidOperationException("operation_busy");
        try
        {
            lock (_sync)
            {
                if (_document is not null)
                {
                    if (_document.OperationId == request.OperationId) return;
                    throw new InvalidOperationException("administrator_already_claimed");
                }
            }
            var settings = Normalize(request.Settings);
            Validate(settings);
            var username = request.Username?.Trim().Normalize(NormalizationForm.FormC) ?? "";
            if (request.OperationId == Guid.Empty || username.Length is < 3 or > 64 ||
                username.EnumerateRunes().Any(c => !Rune.IsLetterOrDigit(c) && c.Value is not ('.' or '_' or '-')) ||
                request.Password is not { Length: >= 12 and <= 256 })
                throw new ArgumentException("administrator_invalid");
            var salt = RandomNumberGenerator.GetBytes(16);
            var hash = await Task.Run(() => Hash(request.Password, salt), cancellationToken).ConfigureAwait(false);
            var document = new InstallationDocument(1, request.OperationId, settings, username,
                Convert.ToBase64String(salt), Convert.ToBase64String(hash), false);
            // The claim survives disconnects and crashes before any database or runtime activation begins.
            _store.Save(document);
            lock (_sync) _document = document;
            await StartApplyAsync().ConfigureAwait(false);
        }
        finally { _mutation.Release(); }
    }

    public async ValueTask RetryAsync(CancellationToken cancellationToken)
    {
        if (!await _mutation.WaitAsync(0, cancellationToken).ConfigureAwait(false)) throw new InvalidOperationException("operation_busy");
        try
        {
            lock (_sync)
                if (_document is null || _running || _phase == "ready") throw new InvalidOperationException("retry_unavailable");
            await StartApplyAsync().ConfigureAwait(false);
        }
        finally { _mutation.Release(); }
    }

    public ValueTask ResumeAsync() => _document is null ? ValueTask.CompletedTask : StartApplyAsync();

    public InstallationConfiguration GetConfiguration()
    {
        lock (_sync)
        {
            var settings = (_document ?? throw new InvalidOperationException("installation_missing")).Settings;
            return new(settings with { MySqlPassword = "" }, settings.MySqlPassword.Length > 0);
        }
    }

    public async ValueTask UpdateAsync(InstallationUpdate update, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (!await _mutation.WaitAsync(0, cancellationToken).ConfigureAwait(false)) throw new InvalidOperationException("operation_busy");
        try
        {
            InstallationDocument document;
            lock (_sync)
            {
                if (_document is null || _running || (_phase != "failed" && !(_phase == "ready" && HasDeferredClients(_document.Settings))))
                    throw new InvalidOperationException("retry_unavailable");
                document = _document;
            }
            var settings = ResolveUpdate(update, document.Settings);
            Validate(settings);
            // Finishing deferred import cannot silently move an installed database or replace its credentials.
            if (document.Installed && settings != (document.Settings with
                { Clients = settings.Clients, Profile = settings.Profile, ClientDataPath = settings.ClientDataPath,
                    DeferClientDataImport = settings.DeferClientDataImport }))
                throw new ArgumentException("configuration_invalid");
            var corrected = document with { Settings = settings };
            _store.Save(corrected);
            lock (_sync) _document = corrected;
            await StartApplyAsync().ConfigureAwait(false);
        }
        finally { _mutation.Release(); }
    }

    public ValueTask ProbeUpdateAsync(InstallationUpdate update, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        InstallationSettings saved;
        lock (_sync) saved = (_document ?? throw new InvalidOperationException("installation_missing")).Settings;
        return ProbeAsync(ResolveUpdate(update, saved), cancellationToken);
    }

    private InstallationSettings ResolveUpdate(InstallationUpdate update, InstallationSettings saved)
    {
        var settings = Normalize(update.Settings);
        if (!update.RetainDatabasePassword) return settings;
        // A retained credential can only go back to its original host and account, never to a replacement endpoint.
        if (settings.DatabaseKind != "mysql" || saved.DatabaseKind != "mysql" || settings.MySqlHost != saved.MySqlHost ||
            settings.MySqlPort != saved.MySqlPort || settings.MySqlUser != saved.MySqlUser)
            throw new ArgumentException("configuration_invalid");
        return settings with { MySqlPassword = saved.MySqlPassword };
    }

    private async ValueTask StartApplyAsync()
    {
        lock (_sync) { _running = true; _phase = "applying"; _error = null; }
        try { await _context.RunTaskAsync("installation-apply", ApplyAsync).ConfigureAwait(false); }
        catch
        {
            lock (_sync) { _running = false; _phase = "failed"; _error = "runtime_failed"; }
            throw;
        }
    }

    private async ValueTask ApplyAsync(CancellationToken cancellationToken)
    {
        var errorCode = "database_failed";
        try
        {
            await _runtime.WaitForBootstrapAsync(cancellationToken).ConfigureAwait(false);
            InstallationDocument document;
            lock (_sync) document = _document ?? throw new InvalidOperationException("installation_missing");
            await _database.WaitAsync(cancellationToken).ConfigureAwait(false);
            IReadOnlyDictionary<string, object?> config;
            try { config = await PrepareDatabaseAsync(document.Settings, true, cancellationToken).ConfigureAwait(false); }
            finally { _database.Release(); }
            errorCode = "runtime_failed";
            await _runtime.ApplyAsync(document.Settings, config, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            errorCode = "storage_failed";
            var installed = document with { Installed = true };
            _store.Save(installed);
            lock (_sync) { _document = installed; _phase = "ready"; _error = null; }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error)
        {
            // Database errors and configuration diagnostics can contain secrets. Only a stable code crosses this boundary.
            _context.Logger.LogError("Installation operation failed: {Code} ({FailureType}).", errorCode, error.GetType().Name);
            lock (_sync) { _phase = "failed"; _error = errorCode; }
        }
        finally { lock (_sync) _running = false; }
    }

    private async ValueTask<IReadOnlyDictionary<string, object?>> PrepareDatabaseAsync(InstallationSettings settings, bool initialize,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _context.Lifetime);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.DatabaseTimeoutSeconds));
        try
        {
            var provider = _databases.Single(d => d.Kind == settings.DatabaseKind);
            return await provider.PrepareAsync(settings, initialize, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _context.Lifetime.IsCancellationRequested) { throw; }
        catch (Exception) { throw new InvalidOperationException("database_failed"); }
    }

    private InstallationSettings Normalize(InstallationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings with
        {
            SqlitePath = string.IsNullOrWhiteSpace(settings.SqlitePath) ? DefaultSqlitePath : settings.SqlitePath.Trim(),
            ServerName = settings.ServerName?.Trim() ?? "",
            PublicHost = settings.PublicHost?.Trim() ?? "",
            Clients = settings.Clients is null ? null : Array.AsReadOnly(settings.Clients.ToArray()),
        };
    }

    // A profile marked as preparing (setup_enabled or activation_enabled false) is refused here as well as in the
    // assistant, so a direct API call cannot install a client build that is not ready.
    private bool IsSelectable(string profile) =>
        _runtime.Profiles.Any(p => p.Id == profile && p.SetupEnabled && p.ActivationEnabled);

    // Empty is valid: the launcher then advertises the address each player's launcher used to reach it.
    private static bool ValidPublicHost(InstallationSettings settings) => settings.PublicHost is { Length: <= 253 } host &&
        (host.Length == 0 || Uri.CheckHostName(host) is not UriHostNameType.Unknown);

    private void Validate(InstallationSettings settings)
    {
        if (settings.Language is not ("en" or "zh-Hans" or "zh-Hant" or "ko") ||
            settings.ServerName is not { Length: >= 1 and <= 80 } || settings.ServerName.Any(char.IsControl) ||
            (settings.Clients is null && !IsSelectable(settings.Profile)) ||
            settings.DatabaseKind is not ("sqlite" or "mysql") || _databases.Count(d => d.Kind == settings.DatabaseKind) != 1 ||
            settings.ClientDataPath is not { Length: <= 2048 } ||
            (settings.Clients is null && settings.Profile != "management" && !settings.DeferClientDataImport && !Path.IsPathFullyQualified(settings.ClientDataPath)) ||
            (settings.Clients is null && settings.DeferClientDataImport && settings.ClientDataPath.Length != 0) ||
            settings.SqlitePath is not { Length: <= 2048 } || !Path.IsPathFullyQualified(settings.SqlitePath) ||
            settings.MySqlHost is not { Length: >= 1 and <= 253 } || settings.MySqlHost.Any(char.IsControl) ||
            settings.MySqlPort is < 1 or > 65535 || settings.MySqlDatabase is not { Length: >= 1 and <= 64 } ||
            settings.MySqlUser is not { Length: >= 1 and <= 128 } || settings.MySqlPassword is not { Length: <= 4096 } ||
            settings.MySqlSslMode is not ("None" or "Required" or "VerifyCA" or "VerifyFull") ||
            settings.PlayerLogin is not ("launcher" or "open") || !ValidPublicHost(settings))
            throw new ArgumentException("configuration_invalid");
        if (settings.Clients is { } clients)
        {
            if (clients.Count is < 1 or > 64 || settings.Profile != "management" ||
                settings.ClientDataPath.Length != 0 || settings.DeferClientDataImport)
                throw new ArgumentException("configuration_invalid");
            var selected = new HashSet<string>(StringComparer.Ordinal);
            foreach (var client in clients)
                if (client is null || client.Profile == "management" || !selected.Add(client.Profile) ||
                    !IsSelectable(client.Profile) || client.ClientDataPath is not { Length: <= 2048 } ||
                    (client.DeferClientDataImport ? client.ClientDataPath.Length != 0 : !Path.IsPathFullyQualified(client.ClientDataPath)))
                    throw new ArgumentException("configuration_invalid");
        }
    }

    public bool Authorize(string? session)
    {
        if (session is not { Length: 64 }) return false;
        lock (_sync)
        {
            if (!_sessions.TryGetValue(session, out var expires)) return false;
            if (expires > DateTimeOffset.UtcNow) return true;
            _sessions.Remove(session);
            return false;
        }
    }

    public ValueTask<AdminSession?> SignInAsync(string username, string password, CancellationToken cancellationToken) =>
        SignInAsync(username, password, IPAddress.None.ToString(), cancellationToken);

    public async ValueTask<AdminSession?> SignInAsync(string username, string password, string sourceAddress, CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(sourceAddress, out var peer))
            throw new ArgumentException("The login source must be an IP address.", nameof(sourceAddress));
        if (peer.IsIPv4MappedToIPv6) peer = peer.MapToIPv4();
        if (username is not { Length: <= 64 } || password is not { Length: >= 1 and <= 256 }) return null;
        if (!await _login.WaitAsync(0, cancellationToken).ConfigureAwait(false)) throw new InvalidOperationException("login_busy");
        try
        {
            InstallationDocument? document;
            lock (_sync) document = _document;
            if (document is null) return null;
            if (!_loginWindows.TryGetValue(peer, out var window) ||
                Stopwatch.GetElapsedTime(window.Started).TotalSeconds >= _options.LoginWindowSeconds)
            {
                if (window is null && _loginWindows.Count >= _options.LoginAddressCapacity)
                    _loginWindows.Remove(_loginWindows.MinBy(pair => pair.Value.Started).Key);
                window = new(Stopwatch.GetTimestamp(), 0);
                _loginWindows[peer] = window;
            }
            if (window.Failures >= _options.LoginAttempts) throw new InvalidOperationException("login_rate_limited");
            var hash = await Task.Run(() => Hash(password, Convert.FromBase64String(document.Salt)), cancellationToken).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(hash, Convert.FromBase64String(document.PasswordHash)) ||
                !string.Equals(username.Trim().Normalize(NormalizationForm.FormC), document.Username, StringComparison.Ordinal))
            { _loginWindows[peer] = window with { Failures = window.Failures + 1 }; return null; }
            lock (_sync)
            {
                var now = DateTimeOffset.UtcNow;
                foreach (var key in _sessions.Where(p => p.Value <= now).Select(p => p.Key).ToArray()) _sessions.Remove(key);
                if (_sessions.Count >= _options.SessionCapacity) throw new InvalidOperationException("session_capacity");
                var session = new AdminSession(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), now.AddMinutes(_options.SessionMinutes));
                _sessions.Add(session.Token, session.ExpiresUtc);
                return session;
            }
        }
        finally { _login.Release(); }
    }

    public void SignOut(string? session) { if (session is not null) lock (_sync) _sessions.Remove(session); }
    private static byte[] Hash(string password, byte[] salt) => Rfc2898DeriveBytes.Pbkdf2(password, salt, HashIterations, HashAlgorithmName.SHA256, 32);
    public void Dispose() { lock (_sync) _sessions.Clear(); _store.Dispose(); _mutation.Dispose(); _database.Dispose(); _login.Dispose(); }
}
