using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Installation;
using BubbleFighter.Fantasy.Kernel;
using System.Threading.Channels;

namespace BubbleFighter.Fantasy.Host;

/// <summary>Ordinary profile-adapter definition scoped to one host invocation; it shares no process-global registry.</summary>
internal sealed class SetupProfileSession : IInstallationRuntime, IPluginExecutionFactory
{
    private readonly string _profilesDirectory;
    private readonly string _stateDirectory;
    private readonly IReadOnlyList<string> _patches;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IReadOnlyList<PluginRow> _shell = [];
    private IPluginHost? _host;
    private Dispatcher? _dispatcher;
    private readonly PluginAttribute _declaration = new("host.profiles")
    { Requires = [typeof(IPluginHost)], Provides = [typeof(IInstallationRuntime)] };

    public SetupProfileSession(string profilesDirectory, string stateDirectory, IReadOnlyList<string> patches)
    {
        _profilesDirectory = profilesDirectory; _stateDirectory = stateDirectory; _patches = patches;
        Profiles = new ProfileComposer(profilesDirectory).ListProfiles()
            .Where(p => p.IsClientBuild || p.Id == "management")
            .Select(p => new InstallationProfile(p.Id, p.Description) { DisplayNames = p.DisplayNames, SetupEnabled = p.SetupEnabled, ActivationEnabled = p.ActivationEnabled, Experimental = p.Experimental })
            .OrderBy(p => p.DisplayNames.GetValueOrDefault("en") ?? p.Id, StringComparer.Ordinal).ToArray();
    }

    public PluginDescriptor Definition => new(_declaration, this, "host-profile-adapter", "1.0.0",
        new Dictionary<string, string>
        {
            [typeof(IPlugin).Assembly.GetName().Name!] = typeof(IPlugin).Assembly.GetName().Version!.ToString(),
            [typeof(IInstallationRuntime).Assembly.GetName().Name!] = typeof(IInstallationRuntime).Assembly.GetName().Version!.ToString(),
        });
    public string? CoordinationKey => null;
    public IReadOnlyList<InstallationProfile> Profiles { get; }
    public void CaptureShell(IReadOnlyList<PluginRow> rows) => _shell = rows.ToArray();
    public void MarkReady() => _ready.TrySetResult();
    public Task WaitForBootstrapAsync(CancellationToken cancellationToken) => _ready.Task.WaitAsync(cancellationToken);

    public ValueTask ApplyAsync(InstallationSettings settings, IReadOnlyDictionary<string, object?> databaseConfig, string administrator,
        CancellationToken cancellationToken) =>
        (_dispatcher ?? throw new InvalidOperationException("Profile adapter is not active.")).ApplyAsync(settings, databaseConfig, administrator, cancellationToken);

    private async ValueTask ApplyCoreAsync(InstallationSettings settings, IReadOnlyDictionary<string, object?> databaseConfig, string administrator,
        CancellationToken cancellationToken)
    {
        var host = _host ?? throw new InvalidOperationException("Profile adapter is not active.");
        if (settings.Clients is null && !Profiles.Any(p => p.Id == settings.Profile)) throw new ConfigException("The selected profile is not packaged.");
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["BFF_MYSQL_PASSWORD"] = settings.DatabaseKind == "mysql" ? settings.MySqlPassword : "unused-by-selected-provider",
            ["BFF_MYSQL_HOST"] = settings.MySqlHost, ["BFF_MYSQL_PORT"] = settings.MySqlPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["BFF_MYSQL_USER"] = settings.MySqlUser, ["BFF_MYSQL_DATABASE"] = settings.MySqlDatabase,
            ["BFF_CLIENT_DATA"] = settings.ClientDataPath,
            // Every lobby row reads its authentication from this variable; the installation decides it, not the shell.
            ["BFF_LOGIN_AUTHENTICATION"] = settings.PlayerLogin == "launcher" ? "ticket" : "open",
        };
        var operations = new List<object>();
        foreach (var kind in new[] { "memory", "mysql", "sqlite" })
        {
            var replacement = new Dictionary<string, object?> { ["id"] = "persistence." + kind, ["disabled"] = kind != settings.DatabaseKind };
            if (kind == settings.DatabaseKind)
            {
                var config = new Dictionary<string, object?>();
                foreach (var (key, value) in databaseConfig)
                {
                    // Interpolation happens once. Secrets containing ${...}, quotes or newlines remain literal values.
                    if (value is string text)
                    {
                        var variable = "BFF_INSTALL_" + key.ToUpperInvariant();
                        environment.Add(variable, text); config.Add(key, "${" + variable + "}");
                    }
                    else config.Add(key, value);
                }
                replacement.Add("config", config);
            }
            operations.Add(new Dictionary<string, object?> { ["replace"] = replacement });
        }
        var overlay = Path.Combine(_stateDirectory, "runtime.generated.patch.yaml");
        var composer = new ProfileComposer(_profilesDirectory,
            key => environment.TryGetValue(key, out var value) ? value : Environment.GetEnvironmentVariable(key));
        var runtime = await ComposeAsync().ConfigureAwait(false);
        if (settings.PlayerLogin == "launcher")
        {
            // A disabled row is never interpolated, so the launcher is enabled through the overlay, not in memory.
            operations.Add(LauncherOperation(runtime, settings.PublicHost, settings.CreateAccountOnLogin));
            runtime = await ComposeAsync().ConfigureAwait(false);
        }
        // A last-applied CLI override must not silently replace the durable provider the operator selected.
        var providers = runtime.Where(r => !r.Disabled && (r.Id is "persistence.mysql" or "persistence.sqlite" or "persistence.memory")).ToArray();
        if (providers.Length != 1 || providers[0].Id != "persistence." + settings.DatabaseKind)
            throw new ConfigException("Runtime patches conflict with the installation's durable database selection.");
        runtime = SetupClientPolicyConfiguration.Apply(runtime, settings.ClientPolicy);
        runtime = SetupAdministratorPermissions.Apply(runtime, administrator);
        var shellIds = _shell.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        var candidate = _shell.Concat(runtime.Where(r => !shellIds.Contains(r.Id))).ToArray();
        var result = await host.ReconcileConfigurationAsync(candidate, cancellationToken).ConfigureAwait(false);
        var instances = host.Instances.ToDictionary(i => i.RowId, StringComparer.Ordinal);
        if (result.Outcome != PluginTransitionOutcome.Converged || candidate.Where(r => !r.Disabled)
            .Any(r => !instances.TryGetValue(r.Id, out var instance) || instance.State != PluginState.Active))
            throw new InvalidOperationException("The selected runtime did not converge. Review local server diagnostics.");

        async ValueTask<IReadOnlyList<PluginRow>> ComposeAsync()
        {
            // JSON is a YAML subset; this derived file contains placeholders, never database credentials.
            var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(operations);
            var temporary = overlay + ".next";
            foreach (var path in new[] { overlay, temporary })
                if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("The generated runtime overlay cannot be a symbolic link.");
            try
            {
                await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
                File.Move(temporary, overlay, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            // Retain the chosen build in durable settings while deliberately withholding game listeners until import.
            return settings.Clients is { } clients
                ? composer.ComposeBuilds("management", clients.Where(c => !c.DeferClientDataImport).Select(c => c.Profile).ToArray(),
                    [overlay, .. _patches], (profile, key) => key == "BFF_CLIENT_DATA"
                        ? clients.Single(c => c.Profile == profile).ClientDataPath
                        : environment.TryGetValue(key, out var value) ? value : Environment.GetEnvironmentVariable(key))
                : composer.Compose(settings.DeferClientDataImport ? "management" : settings.Profile, [overlay, .. _patches]);
        }
    }

    /// <summary>
    /// Builds the overlay operation that enables the launcher row and advertises one instance per active game listener.
    /// The list follows the composed listeners, so a deferred build appears once its data is imported. An empty
    /// <paramref name="publicHost"/> lets the launcher advertise the address each player's launcher used to reach it.
    /// </summary>
    internal Dictionary<string, object?> LauncherOperation(IReadOnlyList<PluginRow> runtime, string publicHost, bool createAccountOnLogin = false)
    {
        if (!runtime.Any(r => r.Id == "launcher.http"))
            throw new ConfigException("Launcher sign-in needs the launcher.http row, which this profile does not include.");
        var instances = new List<object?>();
        foreach (var row in runtime.Where(r => !r.Disabled && r.Plugin == "net.tcp"))
        {
            if (row.Config?.GetValueOrDefault("protocol") is not string protocol || !protocol.EndsWith("/game", StringComparison.Ordinal) ||
                row.Config.GetValueOrDefault("listen") is not string listen ||
                !int.TryParse(listen[(listen.LastIndexOf(':') + 1)..], System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var port))
                continue;
            var build = protocol[..^"/game".Length];
            var names = Profiles.SingleOrDefault(p => p.Id == build)?.DisplayNames ?? new Dictionary<string, string>();
            // The launcher requires an English label; profiles without one fall back to the build id.
            var labels = names.Where(n => n.Key != "en").Select(n => (object?)new Dictionary<string, object?> { ["language"] = n.Key, ["text"] = n.Value })
                .Prepend(new Dictionary<string, object?> { ["language"] = "en", ["text"] = names.GetValueOrDefault("en") ?? build }).ToList();
            instances.Add(new Dictionary<string, object?>
            {
                ["id"] = build, ["build"] = build, ["name"] = labels, ["host"] = publicHost, ["port"] = port,
                ["launcher_login"] = "required",
            });
        }
        return new Dictionary<string, object?>
        {
            ["replace"] = new Dictionary<string, object?>
            {
                ["id"] = "launcher.http", ["disabled"] = false,
                ["config"] = new Dictionary<string, object?>
                {
                    // Players' launchers connect from other machines, so the default bind is every interface.
                    ["listen"] = "${BFF_LAUNCHER_LISTEN:-0.0.0.0:8090}",
                    ["host_policy"] = "${BFF_LAUNCHER_HOST_POLICY:-auto}",
                    ["region"] = "${BFF_LAUNCHER_REGION:-}",
                    ["instances"] = instances,
                    ["create_account_on_login"] = createAccountOnLogin,
                },
            },
        };
    }

    public ValueTask<IPluginExecution> CreateAsync(bool reload, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IPluginExecution>(new Execution(this));

    private sealed class Execution(SetupProfileSession session) : IPluginExecution
    {
        public PluginAttribute Declaration => session._declaration;
        public string Version => "1.0.0";
        public IReadOnlyDictionary<string, string> ContractVersions => session.Definition.ContractVersions;
        public Type? ImplementationType => null;
        public async ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
        {
            session._host = context.Services.Get<IPluginHost>();
            context.Effect(() => session._host = null);
            await context.AcquireEffectAsync(_ =>
            {
                var dispatcher = new Dispatcher(session, context.Lifetime);
                session._dispatcher = dispatcher;
                return ValueTask.FromResult<IAsyncDisposable>(dispatcher);
            }).ConfigureAwait(false);
            context.Effect(context.Services.Register<IInstallationRuntime>(session));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // Lifecycle requests cross a bounded transport boundary, like HTTP administration requests. The worker
    // has independent execution context and always retains the shell rows, so it cannot unload its caller.
    private sealed class Dispatcher : IAsyncDisposable
    {
        private sealed record Request(InstallationSettings Settings, IReadOnlyDictionary<string, object?> Database, string Administrator,
            CancellationToken Cancellation, TaskCompletionSource Completion);
        private readonly SetupProfileSession _session;
        private readonly CancellationTokenSource _stopping;
        private readonly Channel<Request> _requests = Channel.CreateBounded<Request>(new BoundedChannelOptions(1)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        private readonly Task _worker;

        public Dispatcher(SetupProfileSession session, CancellationToken lifetime)
        {
            _session = session;
            _stopping = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            if (ExecutionContext.IsFlowSuppressed()) _worker = Task.Run(WorkAsync);
            else { using (ExecutionContext.SuppressFlow()) _worker = Task.Run(WorkAsync); }
        }

        public async ValueTask ApplyAsync(InstallationSettings settings, IReadOnlyDictionary<string, object?> database, string administrator,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_requests.Writer.TryWrite(new(settings, database, administrator, cancellationToken, completion)))
                throw new InvalidOperationException("The profile adapter is busy or closing.");
            await completion.Task.ConfigureAwait(false);
        }

        private async Task WorkAsync()
        {
            try
            {
                await foreach (var request in _requests.Reader.ReadAllAsync(_stopping.Token).ConfigureAwait(false))
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token, request.Cancellation);
                    try
                    {
                        await _session.ApplyCoreAsync(request.Settings, request.Database, request.Administrator, linked.Token).ConfigureAwait(false);
                        request.Completion.TrySetResult();
                    }
                    catch (OperationCanceledException) when (linked.IsCancellationRequested) { request.Completion.TrySetCanceled(linked.Token); }
                    catch (Exception error) { request.Completion.TrySetException(error); }
                }
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
            finally
            {
                _requests.Writer.TryComplete();
                while (_requests.Reader.TryRead(out var request)) request.Completion.TrySetCanceled(_stopping.Token);
            }
        }

        public async ValueTask DisposeAsync()
        {
            _requests.Writer.TryComplete();
            await _stopping.CancelAsync().ConfigureAwait(false);
            try { await _worker.ConfigureAwait(false); }
            finally { _session._dispatcher = null; _stopping.Dispose(); }
        }
    }
}
