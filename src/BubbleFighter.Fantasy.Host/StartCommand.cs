using System.Runtime.InteropServices;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Loader.Native;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Host;

public static partial class Program
{
    // The plugin dependency resolver cannot open files whose full path reaches the Windows MAX_PATH limit, even
    // with long paths enabled: a 259-character dependency failed to load where a 257-character one loaded.
    private const int WindowsPackagePathLimit = 258;

    private static void RequireShortPackagePaths(string packageDirectory)
    {
        if (!OperatingSystem.IsWindows())
            return;
        var plugins = Path.Combine(packageDirectory, "plugins");
        if (!Directory.Exists(plugins))
            return;
        var longest = Directory.EnumerateFiles(plugins, "*", SearchOption.AllDirectories).MaxBy(path => path.Length);
        if (longest is not null && longest.Length > WindowsPackagePathLimit)
            throw new ConfigException(
                $"The server folder is too deep for Windows: '{longest}' is {longest.Length} characters, more than "
                + $"{WindowsPackagePathLimit}. Move the folder to a shorter location such as C:\\BubbleFighterFantasy and start it again.");
    }

    private static async Task<int> StartAsync(string[] args, bool openBrowser, ILoggerFactory loggerFactory, ILogger logger)
    {
        string? stateDirectory = Environment.GetEnvironmentVariable("BFF_STATE_DIR");
        var patches = new List<string>();
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--state-dir": stateDirectory = RequireValue(args, ref index, "--state-dir"); break;
                case "--patch": patches.Add(Path.GetFullPath(RequireValue(args, ref index, "--patch"))); break;
                case "--log-level": _ = RequireValue(args, ref index, "--log-level"); break;
                default: throw new ConfigException($"Unknown start option '{args[index]}'.");
            }
        }
        // Packaged data and native plugins use one stable root even when Explorer or a service chose another CWD.
        var packageDirectory = AppContext.BaseDirectory;
        RequireShortPackagePaths(packageDirectory);
        stateDirectory = Path.GetFullPath(stateDirectory ?? Path.Combine(packageDirectory, "state"));
        Directory.SetCurrentDirectory(packageDirectory);
        var profilesDirectory = Path.Combine(packageDirectory, "profiles");
        var session = new SetupProfileSession(profilesDirectory, stateDirectory, patches);
        var seeds = new[] { typeof(NativeLoaderPlugin).Assembly, typeof(PluginHost).Assembly };
        var catalog = PluginCatalog.FromDefinitions(PluginCatalog.ScanAssemblies(seeds).Plugins.Append(session.Definition));
        var inputs = new Dictionary<string, object?>
        {
            ["plugin_roots"] = new List<object?> { Path.Combine(packageDirectory, "plugins") },
            ["seed_modules"] = seeds.Select(a => (object?)a.GetName().Name!).ToList(),
        };
        var initial = BootstrapConfiguration.Compose(Path.Combine(packageDirectory, "bootstrap"), "start", inputs);
        var composer = new ProfileComposer(profilesDirectory,
            name => name switch
            {
                "BFF_STATE_DIR" => stateDirectory,
                "BFF_OPEN_BROWSER" when openBrowser => "true",
                _ => Environment.GetEnvironmentVariable(name),
            });
        var deferred = composer.Compose("setup", []);
        var startup = new PluginStartupPlan(initial, deferred);
        var candidate = startup.InitialRows.Concat(startup.DeferredRows).ToArray();
        session.CaptureShell(candidate);
        var host = new PluginHost(startup, catalog, loggerFactory);
        var shutdown = new OwnedShutdownSignal();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; shutdown.RequestCancellation(); };
        Console.CancelKeyPress += cancel;
        try
        {
            using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, c => { c.Cancel = true; shutdown.RequestCancellation(); });
            using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, c => { c.Cancel = true; shutdown.RequestCancellation(); });
            return await RunServerAsync(host, candidate, logger, shutdown.Token, null, session.MarkReady).ConfigureAwait(false);
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
            await shutdown.CompleteAsync().ConfigureAwait(false);
        }
    }
}
