using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.Loader.Remote;

/// <summary>One configured SDK plugin (a process or a WebAssembly module) and its definition source.</summary>
/// <param name="SourceId">The definition source identity within the loader's activation.</param>
/// <param name="Launch">The resolved command line, for <c>loader.process</c>; null for a module.</param>
/// <param name="MaxDefinitions">The definition capacity reserved for the source before the plugin is asked to describe.</param>
/// <param name="Wasm">The resolved module, for <c>loader.wasm</c>; null for a process.</param>
internal sealed record RemoteEntry(string SourceId, ProcessLaunch? Launch, int MaxDefinitions, WasmLaunch? Wasm = null)
{
    /// <summary>Gets the host contract names this entry may declare, or null when every contract is allowed.</summary>
    public IReadOnlySet<string>? AllowedServices { get; init; }

    /// <summary>Gets the event type names this entry may declare, or null when every event is allowed.</summary>
    public IReadOnlySet<string>? AllowedEvents { get; init; }
}

/// <summary>The validated configuration of a <c>loader.process</c> or <c>loader.wasm</c> row (spec sections 2 and 3).</summary>
internal sealed class RemoteLoaderConfig
{
    private static readonly HashSet<string> TopKeys = new(StringComparer.Ordinal)
    {
        "plugins", "base_dir", "max_line_bytes", "max_in_flight", "request_timeout_ms", "shutdown_timeout_ms", "max_sessions", "max_reentry_depth",
    };

    private static readonly HashSet<string> EntryKeys = new(StringComparer.Ordinal)
    {
        "command", "args", "cwd", "env", "source_id", "max_definitions", "allowed_services", "allowed_events",
    };

    private static readonly HashSet<string> WasmEntryKeys = new(StringComparer.Ordinal)
    {
        "module", "source_id", "max_definitions", "max_memory_bytes", "allowed_services", "allowed_events",
    };

    /// <summary>Gets the configured processes, in order.</summary>
    public required IReadOnlyList<RemoteEntry> Entries { get; init; }

    /// <summary>Gets the transport bounds shared by every process.</summary>
    public required TransportOptions Transport { get; init; }

    /// <summary>Gets the per-activation bound on sessions the host remembers for <c>messages.send</c>.</summary>
    public required int MaxSessions { get; init; }

    /// <summary>Gets the maximum nested host-request frames admitted by a single-threaded SDK, including the outer request.</summary>
    public required int MaxReentryDepth { get; init; }

    /// <summary>Validates a row's configuration mapping, reporting every problem in one <see cref="ConfigException"/>.</summary>
    /// <param name="row">The loader row.</param>
    /// <param name="wasm">True for a <c>loader.wasm</c> row, whose entries name modules instead of commands.</param>
    /// <returns>The validated configuration.</returns>
    /// <exception cref="ConfigException">Thrown for unknown keys, wrong types, out-of-range values or missing directories.</exception>
    public static RemoteLoaderConfig Parse(PluginRow row, bool wasm = false)
    {
        var errors = new List<string>();
        var map = row.Config is null ? [] : new Dictionary<string, object?>(row.Config, StringComparer.Ordinal);
        foreach (var key in map.Keys.Where(key => !TopKeys.Contains(key)))
            errors.Add($"unknown key '{key}'");

        var baseDir = Directory.GetCurrentDirectory();
        if (map.TryGetValue("base_dir", out var baseValue))
        {
            if (baseValue is string text && text.Length > 0)
                baseDir = Path.GetFullPath(text, baseDir);
            else
                errors.Add("key 'base_dir': expected a nonempty string");
        }

        var maxLine = Integer(map, "max_line_bytes", 1_048_576, 1024, 64 * 1024 * 1024, errors);
        var maxInFlight = Integer(map, "max_in_flight", 64, 1, 4096, errors);
        var maxReentryDepth = Integer(map, "max_reentry_depth", 8, 1, 64, errors);
        var requestMs = Integer(map, "request_timeout_ms", 10_000, 1, 600_000, errors);
        var shutdownMs = Integer(map, "shutdown_timeout_ms", 3_000, 1, 600_000, errors);
        var maxSessions = Integer(map, "max_sessions", 65_536, 1, 10_000_000, errors);

        var entries = new List<RemoteEntry>();
        if (!map.TryGetValue("plugins", out var pluginsValue) || !ConfigValues.TryList(pluginsValue, out var items) || items.Count == 0)
            errors.Add("key 'plugins': expected a nonempty list of process entries");
        else
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < items.Count; index++)
            {
                var entry = wasm ? ParseWasmEntry(items[index], index, baseDir, errors) : ParseEntry(items[index], index, baseDir, errors);
                if (entry is null)
                    continue;
                if (!seen.Add(entry.SourceId))
                    errors.Add($"plugins[{index}]: duplicate source_id '{entry.SourceId}'");
                entries.Add(entry);
            }
        }

        if (errors.Count > 0)
            throw new ConfigException(row.Id, errors);
        return new RemoteLoaderConfig
        {
            Entries = entries,
            Transport = new TransportOptions(maxLine, maxInFlight, TimeSpan.FromMilliseconds(requestMs), TimeSpan.FromMilliseconds(shutdownMs)),
            MaxSessions = maxSessions,
            MaxReentryDepth = maxReentryDepth,
        };
    }

    private static RemoteEntry? ParseEntry(object? value, int index, string baseDir, List<string> errors)
    {
        var at = $"plugins[{index}]";
        if (!ConfigValues.TryMap(value, out var map))
        {
            errors.Add($"{at}: expected a mapping");
            return null;
        }
        var before = errors.Count;
        foreach (var key in map.Keys.Where(key => !EntryKeys.Contains(key)))
            errors.Add($"{at}: unknown key '{key}'");

        var command = map.GetValueOrDefault("command") as string;
        if (string.IsNullOrWhiteSpace(command))
            errors.Add($"{at}.command: required nonempty string");

        var args = new List<string>();
        if (map.TryGetValue("args", out var argsValue))
        {
            if (!ConfigValues.TryList(argsValue, out var list))
                errors.Add($"{at}.args: expected a list of strings");
            else
                foreach (var item in list)
                {
                    if (item is string text)
                        args.Add(text);
                    else
                        errors.Add($"{at}.args: every argument must be a string");
                }
        }

        var cwd = baseDir;
        if (map.TryGetValue("cwd", out var cwdValue))
        {
            if (cwdValue is string text && text.Length > 0)
                cwd = Path.GetFullPath(text, baseDir);
            else
                errors.Add($"{at}.cwd: expected a nonempty string");
        }
        if (!Directory.Exists(cwd))
            errors.Add($"{at}.cwd: directory '{cwd}' does not exist");

        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        if (map.TryGetValue("env", out var envValue))
        {
            if (!ConfigValues.TryMap(envValue, out var envMap))
                errors.Add($"{at}.env: expected a mapping of names to strings");
            else
                foreach (var (name, item) in envMap)
                {
                    if (item is string text)
                        env[name] = text;
                    else
                        errors.Add($"{at}.env.{name}: expected a string");
                }
        }

        var sourceId = $"process-{index}";
        if (map.TryGetValue("source_id", out var idValue))
        {
            if (idValue is string text && !string.IsNullOrWhiteSpace(text))
                sourceId = text;
            else
                errors.Add($"{at}.source_id: expected a nonempty string");
        }

        var capacity = 8;
        if (map.TryGetValue("max_definitions", out var capacityValue))
        {
            if (ConfigValues.TryInteger(capacityValue, out var number) && number is >= 1 and <= 1024)
                capacity = (int)number;
            else
                errors.Add($"{at}.max_definitions: expected an integer in 1..1024");
        }

        var allowedServices = Names(map, "allowed_services", at, errors);
        var allowedEvents = Names(map, "allowed_events", at, errors);

        if (errors.Count > before)
            return null;
        // A command with a directory part is a path: relative ones resolve against base_dir. A bare name is left to PATH.
        var path = command!;
        if ((path.Contains('/') || path.Contains('\\')) && !Path.IsPathRooted(path))
            path = Path.GetFullPath(path, baseDir);
        return new RemoteEntry(sourceId, new ProcessLaunch(path, args, cwd, env), capacity)
        {
            AllowedServices = allowedServices,
            AllowedEvents = allowedEvents,
        };
    }

    private static RemoteEntry? ParseWasmEntry(object? value, int index, string baseDir, List<string> errors)
    {
        var at = $"plugins[{index}]";
        if (!ConfigValues.TryMap(value, out var map))
        {
            errors.Add($"{at}: expected a mapping");
            return null;
        }
        var before = errors.Count;
        foreach (var key in map.Keys.Where(key => !WasmEntryKeys.Contains(key)))
            errors.Add($"{at}: unknown key '{key}'");
        string? path = null;
        if (map.GetValueOrDefault("module") is not string module || string.IsNullOrWhiteSpace(module))
            errors.Add($"{at}.module: required nonempty string (the path of a .wasm file)");
        else
        {
            path = Path.GetFullPath(module, baseDir);
            if (!File.Exists(path))
                errors.Add($"{at}.module: file '{path}' does not exist");
        }
        var sourceId = $"wasm-{index}";
        if (map.TryGetValue("source_id", out var idValue))
        {
            if (idValue is string text && !string.IsNullOrWhiteSpace(text))
                sourceId = text;
            else
                errors.Add($"{at}.source_id: expected a nonempty string");
        }
        var capacity = 8;
        if (map.TryGetValue("max_definitions", out var capacityValue))
        {
            if (ConfigValues.TryInteger(capacityValue, out var number) && number is >= 1 and <= 1024)
                capacity = (int)number;
            else
                errors.Add($"{at}.max_definitions: expected an integer in 1..1024");
        }
        // 64 MiB covers an SDK plugin's working set; a module that needs more says so in its entry.
        long memory = 64L * 1024 * 1024;
        if (map.TryGetValue("max_memory_bytes", out var memoryValue))
        {
            if (ConfigValues.TryInteger(memoryValue, out var number) && number is >= 65_536 and <= 4L * 1024 * 1024 * 1024)
                memory = number;
            else
                errors.Add($"{at}.max_memory_bytes: expected an integer in 65536..4294967296");
        }
        var allowedServices = Names(map, "allowed_services", at, errors);
        var allowedEvents = Names(map, "allowed_events", at, errors);
        if (errors.Count > before)
            return null;
        return new RemoteEntry(sourceId, null, capacity, new WasmLaunch(path!, memory))
        {
            AllowedServices = allowedServices,
            AllowedEvents = allowedEvents,
        };
    }

    /// <summary>Reads an optional bounded list of names; absent means every name is allowed.</summary>
    /// <param name="map">The entry mapping.</param>
    /// <param name="key">The configuration key.</param>
    /// <param name="at">The entry path for error messages.</param>
    /// <param name="errors">Collects every problem.</param>
    /// <returns>The names, or null when the key is absent.</returns>
    private static IReadOnlySet<string>? Names(Dictionary<string, object?> map, string key, string at, List<string> errors)
    {
        if (!map.TryGetValue(key, out var value))
            return null;
        if (!ConfigValues.TryList(value, out var list))
        {
            errors.Add($"{at}.{key}: expected a list of nonempty names");
            return null;
        }
        if (list.Count > 256)
        {
            errors.Add($"{at}.{key}: has {list.Count} entries, over the 256-entry limit");
            return null;
        }
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in list)
        {
            if (item is not string name || string.IsNullOrWhiteSpace(name))
            {
                errors.Add($"{at}.{key}: every entry must be a nonempty string");
                continue;
            }
            if (!names.Add(name))
                errors.Add($"{at}.{key}: lists '{name}' twice");
        }
        return names;
    }

    private static int Integer(Dictionary<string, object?> map, string key, int fallback, int min, int max, List<string> errors)
    {
        if (!map.TryGetValue(key, out var value))
            return fallback;
        if (ConfigValues.TryInteger(value, out var number) && number >= min && number <= max)
            return (int)number;
        errors.Add($"key '{key}': expected an integer in {min}..{max}");
        return fallback;
    }
}
