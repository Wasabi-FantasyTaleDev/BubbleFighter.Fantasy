using System.ComponentModel.DataAnnotations;
using System.Text;
using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.TestFixtures.RunBootstrap;

/// <summary>Configures an isolated output directory and a finite byte limit for each lifecycle evidence marker.</summary>
public sealed class EvidenceOptions
{
    /// <summary>Gets or sets the private directory used by this fixture process for lifecycle markers.</summary>
    [Required]
    public string EvidenceDirectory { get; set; } = "";

    /// <summary>Gets or sets the largest UTF-8 row identity written into one marker.</summary>
    [Range(16, 65536)]
    public int MaximumMarkerBytes { get; set; } = 1024;
}

internal static class Evidence
{
    internal static void Write(EvidenceOptions options, string marker, string rowId)
    {
        var bytes = Encoding.UTF8.GetBytes(rowId + Environment.NewLine);
        if (bytes.Length > options.MaximumMarkerBytes) throw new ConfigException("Fixture marker exceeds maximum_marker_bytes.");
        Directory.CreateDirectory(options.EvidenceDirectory);
        using var stream = new FileStream(Path.Combine(options.EvidenceDirectory, marker), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }
}

/// <summary>Proves that a discovered game entry activates and its owned effect drains on server shutdown.</summary>
[Plugin("test.run.success")]
public sealed class SuccessPlugin : IPlugin
{
    /// <summary>Writes an activation marker and registers its matching release marker as an owned effect.</summary>
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var options = context.Config.Bind<EvidenceOptions>();
        context.Effect(() => Evidence.Write(options, "success.released", context.Row.Id));
        Evidence.Write(options, "success.active", context.Row.Id);
        return ValueTask.CompletedTask;
    }
}

/// <summary>Fails after acquiring an effect so the real CLI startup must preserve the error and release resources.</summary>
[Plugin("test.run.failure")]
public sealed class FailurePlugin : IPlugin
{
    /// <summary>Records acquired ownership, then reports a deterministic startup failure.</summary>
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var options = context.Config.Bind<EvidenceOptions>();
        context.Effect(() => Evidence.Write(options, "failure.released", context.Row.Id));
        Evidence.Write(options, "failure.entered", context.Row.Id);
        throw new ConfigException("RUN_FIXTURE_ACTIVATION_FAILURE");
    }
}

/// <summary>Represents an intentionally absent service used to reject false-positive startup convergence.</summary>
public interface IMissingStartupService { }

/// <summary>Remains pending because no fixture provides its declared required service.</summary>
[Plugin("test.run.pending", Requires = [typeof(IMissingStartupService)])]
public sealed class PendingPlugin : IPlugin
{
    /// <summary>Fails if dependency admission incorrectly invokes an entry without its required provider.</summary>
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("RUN_FIXTURE_PENDING_ACTIVATED_UNEXPECTEDLY");
}

/// <summary>Holds an in-progress activation until the real server's owned cancellation reaches it.</summary>
[Plugin("test.run.cancellable")]
public sealed class CancellablePlugin : IPlugin
{
    /// <summary>Registers cleanup before signaling that cancellation can be injected into server startup.</summary>
    public async ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<EvidenceOptions>();
        context.Effect(() => Evidence.Write(options, "cancellable.released", context.Row.Id));
        Evidence.Write(options, "cancellable.entered", context.Row.Id);
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
    }
}
