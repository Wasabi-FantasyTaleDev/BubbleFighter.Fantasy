using System.Runtime.InteropServices;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Loader.Native;
using BubbleFighter.Fantasy.Packets;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Host;

/// <summary>Console entry point for the Bubble Fighter Fantasy server host.</summary>
public static partial class Program
{
    /// <summary>Parses the command line and runs the requested command.</summary>
    /// <param name="args">Command-line arguments.</param>
    /// <returns>Process exit code.</returns>
    public static async Task<int> Main(string[] args)
    {
        // Double-clicking the Windows executable passes no arguments and owns a fresh console window.
        var doubleClicked = args.Length == 0 && OperatingSystem.IsWindows()
            && !Console.IsInputRedirected && !Console.IsOutputRedirected;
        var exitCode = await DispatchAsync(args.Length == 0 ? ["start"] : args, doubleClicked).ConfigureAwait(false);
        if (doubleClicked && exitCode != 0)
        {
            // The window closes with the process; keep the error readable.
            Console.Error.WriteLine("Press any key to close this window.");
            Console.ReadKey(intercept: true);
        }
        return exitCode;
    }

    private static async Task<int> DispatchAsync(string[] args, bool openBrowser)
    {
        if (args[0] is "-h" or "--help" or "help")
        {
            PrintUsage();
            return 0;
        }

        try
        {
            // Populate unset process variables from the repo-root.env before log-level resolution and profile
            // composition so ${VAR} / ${VAR:-default} can resolve.
            var envFile = args[0] == "start" ? null : RepoRootEnvFile.Apply();
            var logLevel = ParseLogLevel(args);
            using var loggerFactory = LoggerFactory.Create(builder => builder
                .SetMinimumLevel(logLevel)
                .AddConsole(options =>
                {
                    if (args[0] == "contracts") options.LogToStandardErrorThreshold = LogLevel.Trace;
                })
                .AddSimpleConsole(options =>
                {
                    options.SingleLine = true;
                    options.TimestampFormat = "yyyy-MM-ddTHH:mm:ssZ ";
                    options.UseUtcTimestamp = true;
                }));
            var logger = loggerFactory.CreateLogger("bff");
            if (envFile is not null)
                logger.LogInformation("Loaded unset variables from {EnvFile}.", envFile);

            return args[0] switch
            {
                "start" => await StartAsync(args[1..], openBrowser, loggerFactory, logger).ConfigureAwait(false),
                "run" => await RunAsync(args[1..], loggerFactory, logger).ConfigureAwait(false),
                "profiles" when args.Length > 1 && args[1] == "list" => ListProfiles(args[2..]),
                "packets" when args.Length > 1 && args[1] is "show" or "diff" or "check" or "json" => Packets(args[1], args[2..]),
                "plugins" when args.Length > 1 && args[1] == "list" => await ListPluginsAsync(args[2..], loggerFactory).ConfigureAwait(false),
                "contracts" when args.Length > 1 && args[1] == "schema" => await ExportContractsAsync(args[2..], loggerFactory).ConfigureAwait(false),
                _ => Fail($"Unknown command '{args[0]}'."),
            };
        }
        catch (Exception ex) when (ex is ConfigException or InvalidOperationException or IOException ||
            PluginCatalog.GetLoadFailureDiagnostics(ex) is not null)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            if (PluginCatalog.GetLoadFailureDiagnostics(ex) is { } diagnostics)
                Console.Error.WriteLine($"load diagnostics: {diagnostics}");
            return 1;
        }
    }

    private static async Task<int> RunAsync(string[] args, ILoggerFactory loggerFactory, ILogger logger)
    {
        var options = ParseRunOptions(args);
        if (options.Profile is null)
            return Fail("run requires --profile <id>.");
        if (options.ManagedStdio && (options.DumpConfig || !Console.IsInputRedirected || !Console.IsOutputRedirected))
            throw new ConfigException("--managed-stdio requires redirected stdin/stdout and cannot be combined with --dump-config.");

        var profilesDir = Path.GetFullPath(options.ProfilesDir ?? "profiles");
        var composer = new ProfileComposer(profilesDir);
        var rows = composer.Compose(options.Profile, options.Patches);

        if (options.DumpConfig)
        {
            Console.Out.Write(ProfileComposer.Dump(rows));
            return 0;
        }

        var shutdown = new OwnedShutdownSignal();
        ManagedStdioSession? managed = null;
        Exception? startupFailure = null;
        ConsoleCancelEventHandler cancelHandler = (_, e) =>
        {
            e.Cancel = true;
            shutdown.RequestCancellation();
        };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; shutdown.RequestCancellation(); });
            using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx => { ctx.Cancel = true; shutdown.RequestCancellation(); });
            if (options.ManagedStdio)
            {
                var input = Console.OpenStandardInput();
                var reader = new RedirectedStdin(input);
                managed = new ManagedStdioSession(input, Console.Out, shutdown, reader.ReadByteAsync);
            }
            var roots = options.PluginRoots.Count > 0
                ? options.PluginRoots.Select(Path.GetFullPath).ToList()
                : [Path.Combine(AppContext.BaseDirectory, "plugins")];
            // The kernel assembly carries the core.* built-in rows every server profile composes.
            var seedAssemblies = new[] { typeof(NativeLoaderPlugin).Assembly, typeof(PluginHost).Assembly };
            var catalog = PluginCatalog.ScanAssemblies(seedAssemblies);
            var inputs = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["plugin_roots"] = roots.Cast<object?>().ToList(),
                ["seed_modules"] = seedAssemblies.Select(assembly => (object?)assembly.GetName().Name!).ToList(),
            };
            var bootstrapDirectory = Path.GetFullPath(options.BootstrapDir ?? Path.Combine(AppContext.BaseDirectory, "bootstrap"));
            var initial = BootstrapConfiguration.Compose(bootstrapDirectory, "run", inputs);
            var startup = new PluginStartupPlan(initial, rows);
            var host = new PluginHost(startup, catalog, loggerFactory);
            var candidate = startup.InitialRows.Concat(startup.DeferredRows).ToArray();
            return await RunServerAsync(host, candidate, logger, shutdown.Token, managed).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            startupFailure = error;
            throw;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            Exception? managedFailure = null;
            if (managed is not null)
            {
                try { await managed.DisposeAsync().ConfigureAwait(false); }
                catch (Exception controlError)
                {
                    managedFailure = controlError;
                    if (startupFailure is not null) ReportSecondaryFailure("managed standard input", controlError);
                }
            }
            try { await shutdown.CompleteAsync().ConfigureAwait(false); }
            catch (Exception cancellationError)
            {
                if (startupFailure is null && managedFailure is null)
                    throw new InvalidOperationException("Server cancellation callbacks did not complete cleanly.", cancellationError);
                ReportSecondaryFailure("server cancellation", cancellationError);
            }
            if (startupFailure is null && managedFailure is not null) throw managedFailure;
        }
    }

    private static async Task<int> RunServerAsync(PluginHost host, IReadOnlyList<PluginRow> candidate, ILogger logger,
        CancellationToken shutdownToken, ManagedStdioSession? managed, Action? ready = null)
    {
        Exception? failure = null;
        try
        {
            await host.StartAsync(shutdownToken).ConfigureAwait(false);
            var result = await host.ReconcileConfigurationAsync(candidate, shutdownToken).ConfigureAwait(false);
            shutdownToken.ThrowIfCancellationRequested();
            var instances = host.Instances.ToDictionary(instance => instance.RowId, StringComparer.Ordinal);
            var incomplete = candidate.Where(row => !row.Disabled)
                .Where(row => !instances.TryGetValue(row.Id, out var info) || info.State != PluginState.Active)
                .Select(row => instances.TryGetValue(row.Id, out var info)
                    ? $"{row.Id}: {info.State} ({info.Error ?? "no diagnostic"})" : $"{row.Id}: missing runtime instance")
                .ToArray();
            if (result.Outcome != PluginTransitionOutcome.Converged || incomplete.Length > 0)
                throw new InvalidOperationException($"Server startup did not converge ({result.Outcome}): {string.Join("; ", incomplete)}.");
            ready?.Invoke();
            managed?.PublishReady();
            logger.LogInformation("Server started with {Count} active row(s).", instances.Values.Count(instance => instance.State == PluginState.Active));
            await Task.Delay(Timeout.InfiniteTimeSpan, shutdownToken).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception error)
        {
            failure = error;
            throw;
        }
        finally
        {
            try
            {
                await host.ShutdownAsync(CancellationToken.None).ConfigureAwait(false);
                logger.LogInformation("Shutdown complete.");
            }
            catch (Exception cleanupError) when (failure is not null)
            {
                ReportSecondaryFailure("server cleanup", cleanupError);
            }
        }
    }

    private static void ReportSecondaryFailure(string phase, Exception error)
    {
        // Reporting cannot replace the primary failure after owned cleanup has been attempted.
        try { Console.Error.WriteLine($"{phase} error: {error.Message}"); }
        catch (Exception) { }
    }

    // Prints packet structures as one build has them, so nobody has to lay a build's differences over
    // its base by hand.
    private static int Packets(string verb, string[] args)
    {
        var tables = PacketTables.Load(Path.GetFullPath(GetOption(args, "--packets-dir") ?? "packets"));
        var names = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--packets-dir")
                i++;
            else
                names.Add(args[i]);
        }
        switch (verb)
        {
            case "check":
                foreach (var build in tables.Builds)
                {
                    var (rows, own) = tables.StatedRows(build);
                    Console.Out.WriteLine(tables.BaseOf(build) is { } parent
                        ? $"{build}	based on {parent}; {rows} field row(s) stated{(own.Count > 0 ? "; restates " + string.Join(", ", own) : "")}"
                        : $"{build}	the baseline");
                }
                Console.Out.WriteLine($"{tables.Structs.Count} structure(s): {string.Join(", ", tables.Structs)}");
                return 0;
            case "json" when names.Count == 1:
                Console.Out.WriteLine(tables.ToJson(names[0]));
                return 0;
            case "show" when names.Count >= 1:
                foreach (var structure in names.Count > 1 ? names.Skip(1) : tables.Structs)
                    Console.Out.WriteLine(tables.Show(names[0], structure));
                return 0;
            case "diff" when names.Count >= 2:
                foreach (var structure in names.Count > 2 ? names.Skip(2) : tables.Structs)
                    Console.Out.WriteLine(tables.Diff(names[0], names[1], structure));
                return 0;
            default:
                return Fail("usage: bff packets show <build> [structure]... | bff packets diff <build> <build> [structure]... | bff packets json <build> | bff packets check");
        }
    }

    private static int ListProfiles(string[] args)
    {
        var profilesDir = Path.GetFullPath(GetOption(args, "--profiles-dir") ?? "profiles");
        var composer = new ProfileComposer(profilesDir);
        var profiles = composer.ListProfiles();
        if (profiles.Count == 0)
        {
            Console.Out.WriteLine("no profiles found");
            return 0;
        }
        foreach (var profile in profiles)
            Console.Out.WriteLine(profile.Description is { } d ? $"{profile.Id}\t{d}" : profile.Id);
        return 0;
    }

    private static async Task<int> ListPluginsAsync(string[] args, ILoggerFactory loggerFactory)
    {
        var roots = new List<string>();
        var bootstrapDirectory = Path.Combine(AppContext.BaseDirectory, "bootstrap");
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--plugin-root":
                    roots.Add(Path.GetFullPath(RequireValue(args, ref index, "--plugin-root")));
                    break;
                case "--bootstrap-dir":
                    bootstrapDirectory = Path.GetFullPath(RequireValue(args, ref index, "--bootstrap-dir"));
                    break;
                case "--log-level":
                    _ = RequireValue(args, ref index, "--log-level");
                    break;
                default:
                    throw new ConfigException($"Unknown plugin-list option '{args[index]}'.");
            }
        }
        if (roots.Count == 0)
            roots.Add(Path.Combine(AppContext.BaseDirectory, "plugins"));

        // Seeds are application composition; their ordinary rows perform discovery through the host contract.
        var seedAssemblies = new[] { typeof(NativeLoaderPlugin).Assembly, typeof(PluginHost).Assembly };
        var catalog = PluginCatalog.ScanAssemblies(seedAssemblies);
        var inputs = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["plugin_roots"] = roots.Cast<object?>().ToList(),
            ["seed_modules"] = seedAssemblies.Select(assembly => (object?)assembly.GetName().Name!).ToList(),
        };
        var rows = BootstrapConfiguration.Compose(bootstrapDirectory, "list", inputs);
        var host = new PluginHost(rows, catalog, loggerFactory);
        Exception? failure = null;
        try
        {
            await host.StartAsync(CancellationToken.None).ConfigureAwait(false);
            foreach (var plugin in ((IPluginHost)host).Definitions)
                Console.Out.WriteLine($"{plugin.Id}\t{plugin.Version}\t{string.Join(",", plugin.SupportedBuilds)}\t{plugin.Origin}");
            return 0;
        }
        catch (Exception error)
        {
            failure = error;
            throw;
        }
        finally
        {
            try { await host.ShutdownAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception cleanupError) when (failure is not null)
            {
                Console.Error.WriteLine($"bootstrap cleanup error: {cleanupError.Message}");
            }
        }
    }

    private static async Task<int> ExportContractsAsync(string[] args, ILoggerFactory loggerFactory)
    {
        var roots = new List<string>();
        var bootstrapDirectory = Path.Combine(AppContext.BaseDirectory, "bootstrap");
        var outputPath = "";
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--out": outputPath = RequireValue(args, ref index, "--out"); break;
                case "--plugin-root": roots.Add(Path.GetFullPath(RequireValue(args, ref index, "--plugin-root"))); break;
                case "--bootstrap-dir": bootstrapDirectory = Path.GetFullPath(RequireValue(args, ref index, "--bootstrap-dir")); break;
                case "--log-level": _ = RequireValue(args, ref index, "--log-level"); break;
                default: throw new ConfigException($"Unknown contracts-schema option '{args[index]}'.");
            }
        }
        if (roots.Count == 0) roots.Add(Path.Combine(AppContext.BaseDirectory, "plugins"));
        var seeds = new[] { typeof(NativeLoaderPlugin).Assembly, typeof(PluginHost).Assembly };
        var inputs = new Dictionary<string, object?>
        {
            ["plugin_roots"] = roots.Cast<object?>().ToList(),
            ["seed_modules"] = seeds.Select(a => (object?)a.GetName().Name!).ToList(),
            ["output_path"] = outputPath,
        };
        var initial = BootstrapConfiguration.Compose(bootstrapDirectory, "list", inputs);
        var deferred = BootstrapConfiguration.Compose(bootstrapDirectory, "contracts", inputs);
        var startup = new PluginStartupPlan(initial, deferred);
        var rows = initial.Concat(deferred).ToArray();
        var host = new PluginHost(startup, PluginCatalog.ScanAssemblies(seeds), loggerFactory);
        Exception? failure = null;
        try
        {
            await host.StartAsync(CancellationToken.None).ConfigureAwait(false);
            var result = await host.ReconcileConfigurationAsync(rows, CancellationToken.None).ConfigureAwait(false);
            var incomplete = host.Instances.Where(i => i.State != PluginState.Active && !rows.Single(r => r.Id == i.RowId).Disabled).ToArray();
            if (result.Outcome != PluginTransitionOutcome.Converged || incomplete.Length > 0)
                throw new InvalidOperationException("Contract export did not converge: " +
                    string.Join("; ", incomplete.Select(i => $"{i.RowId}: {i.State} ({i.Error})")));
            return 0;
        }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            try { await host.ShutdownAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception cleanupError) when (failure is not null) { ReportSecondaryFailure("contract export cleanup", cleanupError); }
        }
    }

    private sealed class RunOptions
    {
        public string? Profile;
        public string? ProfilesDir;
        public string? BootstrapDir;
        public List<string> Patches = [];
        public List<string> PluginRoots = [];
        public bool DumpConfig;
        public bool ManagedStdio;
    }

    private static RunOptions ParseRunOptions(string[] args)
    {
        var options = new RunOptions();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--profile":
                    options.Profile = RequireValue(args, ref i, "--profile");
                    break;
                case "--profiles-dir":
                    options.ProfilesDir = RequireValue(args, ref i, "--profiles-dir");
                    break;
                case "--bootstrap-dir":
                    options.BootstrapDir = RequireValue(args, ref i, "--bootstrap-dir");
                    break;
                case "--patch":
                    options.Patches.Add(RequireValue(args, ref i, "--patch"));
                    break;
                case "--plugin-root":
                    options.PluginRoots.Add(RequireValue(args, ref i, "--plugin-root"));
                    break;
                case "--dump-config":
                    options.DumpConfig = true;
                    break;
                case "--managed-stdio":
                    options.ManagedStdio = true;
                    break;
                case "--log-level":
                    _ = RequireValue(args, ref i, "--log-level");
                    break;
                default:
                    throw new ConfigException($"Unknown option '{args[i]}'.");
            }
        }
        return options;
    }

    private static LogLevel ParseLogLevel(string[] args)
    {
        var value = GetOption(args, "--log-level") ?? Environment.GetEnvironmentVariable("BFF_LOG_LEVEL");
        return value is not null && Enum.TryParse<LogLevel>(value, ignoreCase: true, out var level)
            ? level
            : LogLevel.Information;
    }

    private static string? GetOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == name)
                return args[i + 1];
        return null;
    }

    private static string[] GetOptions(string[] args, string name)
    {
        var values = new List<string>();
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == name)
                values.Add(args[i + 1]);
        return [.. values];
    }

    private static string RequireValue(string[] args, ref int i, string name) =>
        i + 1 < args.Length ? args[++i] : throw new ConfigException($"Option '{name}' requires a value.");

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"error: {message}");
        return 1;
    }

    private static void PrintUsage()
    {
        Console.Out.WriteLine("""
            bff - Bubble Fighter Fantasy server host

            usage:
              bff [start] [--state-dir dir] [--patch file]...
              bff run --profile <id> [--profiles-dir dir] [--patch file]... [--plugin-root dir]... [--bootstrap-dir dir] [--dump-config | --managed-stdio]
              bff profiles list [--profiles-dir dir]
              bff packets show <build> [structure]... [--packets-dir dir]
              bff packets diff <build> <build> [structure]... [--packets-dir dir]
              bff packets json <build> [--packets-dir dir]
              bff packets check [--packets-dir dir]
              bff plugins list [--plugin-root dir]... [--bootstrap-dir dir]
              bff contracts schema [--out file] [--plugin-root dir]... [--bootstrap-dir dir]

            options:
              --profile <id>        profile directory under profiles/ to run
              --profiles-dir <dir>  profiles root (default: ./profiles)
              --patch <file>        extra patch file applied after profile patches (repeatable)
              --plugin-root <dir>   plugin root directory (repeatable; default: plugins next to the host)
              --bootstrap-dir <dir> bootstrap profiles root for run/list (default: bootstrap next to the host)
              --dump-config         print the effective row list as YAML and exit
              --managed-stdio       redirected control pipe: FT_HOST_READY 1 on startup; stop or EOF shuts down
              --log-level <level>   console log level (or BFF_LOG_LEVEL)

            With no command the packaged installation assistant starts; its state defaults
            to state/ next to the executable (BFF_STATE_DIR or --state-dir overrides it).
            The start command never reads a repository .env. Other commands apply a repository-root .env to unset process variables before
            composing the profile. Existing environment variables win. Values are never logged.
            """);
    }
}
