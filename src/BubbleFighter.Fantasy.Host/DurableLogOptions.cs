using System.Globalization;
using BubbleFighter.Fantasy.Abstractions;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Host;

internal sealed record DurableLogOptions
{
    internal const int MaximumFileCount = 32;
    internal required string File { get; init; }
    internal LogLevel Level { get; init; } = LogLevel.Warning;
    internal long MaxFileBytes { get; init; } = 16 * 1024 * 1024;
    internal int FileCount { get; init; } = 5;
    internal int QueueCapacity { get; init; } = 256;
    internal long MaxQueuedBytes { get; init; } = 16 * 1024 * 1024;
    internal int ShutdownTimeoutMs { get; init; } = 5000;
    internal int RetryInitialMs { get; init; } = 1000;
    internal int RetryMaxMs { get; init; } = 60000;

    internal static DurableLogOptions? Parse(string[] args, Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        string? file = null;
        string? level = null;
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index] is not ("--log-file" or "--log-file-level")) continue;
            var option = args[index];
            if (++index >= args.Length || args[index].StartsWith("--", StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(args[index]))
                throw new ConfigException($"Option '{option}' requires a value.");
            if (option == "--log-file") file = args[index];
            else level = args[index];
        }
        file ??= environment("BFF_LOG_FILE");
        if (string.IsNullOrWhiteSpace(file)) return null;
        level ??= environment("BFF_LOG_FILE_LEVEL") ?? "Warning";
        if (!Enum.TryParse<LogLevel>(level, true, out var parsedLevel) || !Enum.IsDefined(parsedLevel))
            throw new ConfigException("BFF_LOG_FILE_LEVEL / --log-file-level must be a named logging level.");
        // Enum.TryParse accepts integers; deployment options use the documented level names.
        if (!string.Equals(parsedLevel.ToString(), level, StringComparison.OrdinalIgnoreCase))
            throw new ConfigException("BFF_LOG_FILE_LEVEL / --log-file-level must be a named logging level.");
        var options = new DurableLogOptions
        {
            File = file,
            Level = parsedLevel,
            MaxFileBytes = Number(environment, "BFF_LOG_FILE_MAX_BYTES", 16 * 1024 * 1024, 1024, 1024L * 1024 * 1024),
            FileCount = (int)Number(environment, "BFF_LOG_FILE_COUNT", 5, 1, MaximumFileCount),
            QueueCapacity = (int)Number(environment, "BFF_LOG_FILE_QUEUE_CAPACITY", 256, 1, 65536),
            MaxQueuedBytes = Number(environment, "BFF_LOG_FILE_MAX_QUEUED_BYTES", 16 * 1024 * 1024, 1024, 256L * 1024 * 1024),
            ShutdownTimeoutMs = (int)Number(environment, "BFF_LOG_FILE_SHUTDOWN_TIMEOUT_MS", 5000, 100, 30000),
            RetryInitialMs = (int)Number(environment, "BFF_LOG_FILE_RETRY_INITIAL_MS", 1000, 100, 60000),
            RetryMaxMs = (int)Number(environment, "BFF_LOG_FILE_RETRY_MAX_MS", 60000, 100, 60000),
        };
        options.Validate();
        return options;
    }

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(File) || string.IsNullOrEmpty(Path.GetFileName(File)))
            throw new ConfigException("The durable log path must name a file.");
        try { _ = Path.GetFullPath(File); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException)
        {
            throw new ConfigException($"The durable log path is invalid: {error.Message}");
        }
        if (!Enum.IsDefined(Level) || MaxFileBytes is < 1024 or > 1024L * 1024 * 1024
            || FileCount is < 1 or > MaximumFileCount || QueueCapacity is < 1 or > 65536
            || MaxQueuedBytes is < 1024 or > 256L * 1024 * 1024 || ShutdownTimeoutMs is < 100 or > 30000
            || RetryInitialMs is < 100 or > 60000 || RetryMaxMs is < 100 or > 60000 || RetryMaxMs < RetryInitialMs)
            throw new ConfigException("The durable log resource bounds are invalid.");
    }

    private static long Number(Func<string, string?> environment, string name, long fallback, long minimum, long maximum)
    {
        var value = environment(name);
        if (value is null) return fallback;
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result)
            || result < minimum || result > maximum)
            throw new ConfigException($"{name} must be an integer between {minimum} and {maximum}.");
        return result;
    }
}
