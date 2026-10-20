using System.Runtime.CompilerServices;
using System.Text.Json;
using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.Native;

/// <summary>Owns newly created contexts until one loading operation accepts all of its definitions.</summary>
public sealed class NativeModuleLoadBatch : IPluginDefinitionBatch
{
    private static readonly ConditionalWeakTable<Exception, FailureSnapshot> FailureSnapshots = new();
    private readonly string _operation;
    private readonly Func<string, string, NativePluginLoadContext> _createContext;
    private readonly List<Attempt> _attempts = [];
    private bool _settled;
    private bool _sealed;

    private NativeModuleLoadBatch(string operation, Func<string, string, NativePluginLoadContext> createContext)
    {
        ArgumentNullException.ThrowIfNull(createContext);
        _operation = operation;
        _createContext = createContext;
    }

    /// <summary>Gets or sets the diagnostic phase recorded if this attempt fails.</summary>
    public string FailurePhase { get; set; } = "preparation";

    /// <summary>Reads immutable failure facts without retaining contexts or plugin definitions.</summary>
    public static string? GetFailureDiagnostics(Exception error) =>
        FailureSnapshots.TryGetValue(error, out var snapshot) ? Volatile.Read(ref snapshot.Json) : null;

    /// <summary>Owns context acquisitions until a complete loading operation commits successfully.</summary>
    public static T Execute<T>(string operation, Func<string, string, NativePluginLoadContext> createContext,
        Func<NativeModuleLoadBatch, T> load)
    {
        var batch = new NativeModuleLoadBatch(operation, createContext);
        try
        {
            var result = load(batch);
            batch.Commit();
            return result;
        }
        catch (Exception error)
        {
            batch.Rollback(error);
            throw;
        }
    }

    /// <summary>Loads and validates a module while retaining its context in this acquisition batch.</summary>
    public IReadOnlyList<PluginDefinition> Load(string path, string directory, int maximumDefinitions = int.MaxValue)
    {
        if (_settled || _sealed) throw new InvalidOperationException("A completed module load batch cannot acquire code.");
        FailurePhase = "module-load";
        var attempt = new Attempt(path);
        _attempts.Add(attempt);
        attempt.Context = _createContext(path, Path.GetFileName(directory))
            ?? throw new InvalidOperationException("A native context factory returned no context.");
        attempt.Stage = "assembly";
        var assembly = attempt.Context.LoadFromAssemblyPath(path);
        attempt.Stage = "scan";
        var found = new List<PluginDefinition>();
        found.AddRange(NativePluginModules.ScanAssembly(assembly, directory, path, maximumDefinitions));
        if (found.Count == 0)
            throw new ConfigException($"Plugin assembly '{path}' defines no [Plugin] type.");
        attempt.Stage = "validation";
        var definitions = NativePluginModules.ValidateDefinitions(found);
        attempt.Stage = "validated";
        return definitions;
    }

    private IReadOnlyList<PluginDefinition> _definitions = Array.Empty<PluginDefinition>();

    /// <summary>Gets the immutable candidate definitions retained until this batch is released.</summary>
    public IReadOnlyList<PluginDefinition> Definitions => _definitions;

    /// <summary>Acquires a complete source batch without transferring context ownership prematurely.</summary>
    public static NativeModuleLoadBatch Acquire(IReadOnlyList<string> modulePaths, int maximumDefinitions, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(modulePaths);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumDefinitions);
        if (modulePaths.Count > maximumDefinitions) throw new ConfigException("The native module count exceeds definition capacity.");
        var batch = new NativeModuleLoadBatch("definition-source", static (path, name) => new NativePluginLoadContext(path, name));
        try
        {
            var definitions = new List<PluginDefinition>();
            foreach (var path in modulePaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (definitions.Count == maximumDefinitions)
                    throw new ConfigException($"Native source exceeds its {maximumDefinitions} definition capacity.");
                var loaded = batch.Load(path, Path.GetDirectoryName(path)!, maximumDefinitions - definitions.Count);
                if (loaded.Count > maximumDefinitions - definitions.Count)
                    throw new ConfigException($"Native source exceeds its {maximumDefinitions} definition capacity.");
                definitions.AddRange(loaded);
            }
            batch.FailurePhase = "catalog-validation";
            batch._definitions = NativePluginModules.ValidateDefinitions(definitions);
            batch._sealed = true;
            cancellationToken.ThrowIfCancellationRequested();
            return batch;
        }
        catch (Exception error)
        {
            batch.Rollback(error);
            throw;
        }
    }

    /// <summary>Releases a drained source batch, requesting unload only for collectible contexts.</summary>
    public ValueTask DisposeAsync()
    {
        _definitions = Array.Empty<PluginDefinition>();
        Rollback(new InvalidOperationException("The definition source batch was released."));
        return ValueTask.CompletedTask;
    }

    private void Commit()
    {
        // Validated descriptors retain their code anchors; this temporary owner must not unload them.
        foreach (var attempt in _attempts) attempt.Context = null;
        _attempts.Clear();
        _settled = true;
    }

    private void Rollback(Exception error)
    {
        if (_settled) return;
        for (var index = _attempts.Count - 1; index >= 0; index--)
        {
            var attempt = _attempts[index];
            if (attempt.Context is { } context)
            {
                attempt.Lifetime = context.IsCollectible ? PluginCodeLifetime.Collectible : PluginCodeLifetime.ProcessLifetime;
                attempt.RestartRequired = !context.IsCollectible;
                attempt.Cleanup = "ProcessLifetimeRetained";
                if (context.IsCollectible)
                {
                    try
                    {
                        context.Unload();
                        attempt.Cleanup = "UnloadRequested";
                    }
                    catch (Exception cleanupError)
                    {
                        attempt.Cleanup = "CleanupFailed";
                        attempt.RestartRequired = true;
                        try { attempt.CleanupErrorType = cleanupError.GetType().FullName; }
                        catch (Exception) { attempt.CleanupErrorType = "Unavailable"; }
                    }
                }
                attempt.Context = null;
            }
        }
        _settled = true;
        FailureSnapshot? snapshot = null;
        try
        {
            // Reporting starts only after every cleanup attempt; weak values contain no code roots.
            snapshot = FailureSnapshots.GetValue(error, static _ => new FailureSnapshot());
            var outcomes = _attempts.AsEnumerable().Reverse().Select(attempt => new Outcome(
                attempt.Path, attempt.Stage, attempt.Lifetime.ToString(), attempt.Cleanup,
                attempt.CleanupErrorType, attempt.RestartRequired));
            Volatile.Write(ref snapshot.Json,
                JsonSerializer.Serialize(new { Operation = _operation, FailurePhase, Modules = outcomes }));
        }
        catch (Exception)
        {
            // Allocation/serialization failure cannot replace the original load error or skip cleanup.
            if (snapshot is not null)
                Volatile.Write(ref snapshot.Json, """{"DiagnosticsUnavailable":true}""");
        }
        finally
        {
            _attempts.Clear();
        }
    }

    private sealed class FailureSnapshot
    {
        internal string? Json;
    }

    private sealed class Attempt(string path)
    {
        internal string Path { get; } = path;
        internal string Stage { get; set; } = "context";
        internal NativePluginLoadContext? Context { get; set; }
        internal PluginCodeLifetime Lifetime { get; set; } = PluginCodeLifetime.Unknown;
        internal string Cleanup { get; set; } = "ContextNotReturned";
        internal string? CleanupErrorType { get; set; }
        internal bool? RestartRequired { get; set; }
    }

    private sealed record Outcome(string Path, string Stage, string CodeLifetime, string Cleanup,
        string? CleanupErrorType, bool? RestartRequiredForReclamation);
}
