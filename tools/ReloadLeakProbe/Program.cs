using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace BubbleFighter.Fantasy.Tools.ReloadLeakProbe;

/// <summary>
/// Measures a finite sequence of synthetic fixture loads into non-collectible assembly contexts.
/// Runtime context observations and process memory samples do not measure production host reloads
/// or prove collectible-module reclamation.
/// </summary>
public static class Program
{
    /// <summary>Accepted sample counts; each invocation runs exactly one tier.</summary>
    private static readonly int[] Tiers = [8, 32, 128];

    /// <summary>Fixture loads counted in observations but excluded from slope fitting.</summary>
    private const int WarmupRounds = 2;

    /// <summary>Entry point: variant first (pure|leaky), tier second (8|32|128).</summary>
    /// <returns>0 for a completed experiment, 2 for usage, 3 for missing fixtures, or 4 for a measurement failure.</returns>
    public static int Main(string[] args)
    {
        if (args.Length != 2 || !int.TryParse(args[1], out var tier) || !Tiers.Contains(tier) ||
            args[0] is not ("pure" or "leaky"))
        {
            Console.Error.WriteLine("usage: ReloadLeakProbe <pure|leaky> <8|32|128>");
            return 2;
        }
        try
        {
            return Run(args[0], tier);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"probe failed: {error.GetType().Name}: {error.Message}");
            return 4;
        }
    }

    private static int Run(string variant, int tier)
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "fixtures", variant,
            variant == "pure" ? "PureFixture.dll" : "LeakyFixture.dll");
        if (!File.Exists(fixturePath))
        {
            Console.Error.WriteLine($"fixture assembly missing: {fixturePath}; build ReloadLeakProbe.csproj first");
            return 3;
        }

        var prefix = $"reload-probe-{Environment.ProcessId}-{Guid.NewGuid():N}-";
        Console.WriteLine($"probe scope=synthetic-noncollectible-fixture variant={variant} tier={tier} " +
            $"warmupLoads={WarmupRounds} pid={Environment.ProcessId} runPrefix={prefix} " +
            $"serverGC={System.Runtime.GCSettings.IsServerGC} runtime={Environment.Version}");
        Console.WriteLine("scope: fixture loads only; no Host or PluginCatalog reload, no collectible reclamation proof.");
        var created = 0;
        WriteSample("cold", Observe(0, created, prefix));
        for (var warmup = 0; warmup < WarmupRounds; warmup++)
            LoadOnce(fixturePath, prefix + (++created).ToString(CultureInfo.InvariantCulture));
        WriteSample("post-warmup", Observe(0, created, prefix));

        var samples = new List<Sample>(tier);
        for (var iteration = 1; iteration <= tier; iteration++)
        {
            LoadOnce(fixturePath, prefix + (++created).ToString(CultureInfo.InvariantCulture));
            var sample = Observe(iteration, created, prefix);
            samples.Add(sample);
            WriteSample("measured", sample);
        }

        // Fit only post-warmup observations; creation counts never stand in for runtime observations.
        var iterations = samples.Select(sample => (double)sample.Iteration).ToArray();
        var privateSlope = Slope(iterations, samples.Select(sample => (double)sample.PrivateBytes).ToArray());
        var contextSlope = Slope(iterations, samples.Select(sample => (double)sample.ObservedContexts).ToArray());
        Console.WriteLine($"summary variant={variant} tier={tier} " +
            $"privateBytesPerFixtureLoad={privateSlope.ToString("F1", CultureInfo.InvariantCulture)} " +
            $"observedContextsPerFixtureLoad={contextSlope.ToString("F3", CultureInfo.InvariantCulture)} " +
            $"createdContexts={created} observedContexts={samples[^1].ObservedContexts} " +
            $"firstPrivateBytes={samples[0].PrivateBytes} lastPrivateBytes={samples[^1].PrivateBytes} " +
            $"lastManagedBytes={samples[^1].ManagedBytes}");
        return 0;
    }

    /// <summary>Loads and invokes one fixture without returning its context or instance to the sampler.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void LoadOnce(string fixturePath, string contextName)
    {
        var context = new AssemblyLoadContext(contextName, isCollectible: false);
        using var stream = File.OpenRead(fixturePath);
        var assembly = context.LoadFromStream(stream);
        var plugin = assembly.GetType("ReloadLeakProbe.Fixtures.Pure.FixturePlugin")
            ?? assembly.GetType("ReloadLeakProbe.Fixtures.Leaky.FixturePlugin")
            ?? throw new InvalidOperationException("fixture type not found");
        var instance = Activator.CreateInstance(plugin)
            ?? throw new InvalidOperationException("fixture instance could not be created");
        var activate = plugin.GetMethod("Activate")
            ?? throw new InvalidOperationException("fixture activation method not found");
        _ = activate.Invoke(instance, null);
    }

    /// <summary>Collects temporary allocations, then measures runtime-listed contexts and process counters.</summary>
    private static Sample Observe(int iteration, int created, string prefix)
    {
        for (var round = 0; round < 3; round++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        GC.Collect();
        var observed = AssemblyLoadContext.All.Count(context =>
            context.Name?.StartsWith(prefix, StringComparison.Ordinal) == true);
        var managed = GC.GetTotalMemory(forceFullCollection: false);
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        return new Sample(iteration, process.PrivateMemorySize64, managed, created, observed, process.HandleCount);
    }

    private static void WriteSample(string phase, Sample sample) =>
        Console.WriteLine($"sample phase={phase} iteration={sample.Iteration} " +
            $"createdContexts={sample.CreatedContexts} observedContexts={sample.ObservedContexts} " +
            $"privateBytes={sample.PrivateBytes} managedBytes={sample.ManagedBytes} handles={sample.Handles}");

    private readonly record struct Sample(int Iteration, long PrivateBytes, long ManagedBytes,
        int CreatedContexts, int ObservedContexts, int Handles);

    /// <summary>Ordinary least squares slope of y over x.</summary>
    private static double Slope(double[] x, double[] y)
    {
        var meanX = x.Average();
        var meanY = y.Average();
        var numerator = 0d;
        var denominator = 0d;
        for (var index = 0; index < x.Length; index++)
        {
            numerator += (x[index] - meanX) * (y[index] - meanY);
            denominator += (x[index] - meanX) * (x[index] - meanX);
        }
        return denominator == 0 ? 0 : numerator / denominator;
    }
}
