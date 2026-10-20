using BubbleFighter.Fantasy.Abstractions;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.TestFixtures.ModuleOwnership;

/// <summary>Reports its actual loaded assembly to the host logger without owning external resources.</summary>
[Plugin("test.module-ownership-native")]
public sealed class NativeOwnershipPlugin : IPlugin
{
    /// <summary>Reports the actual assembly and optionally installs a deterministic cleanup failure for ownership tests.</summary>
    /// <param name="context">Context whose row identity accompanies the assembly observation.</param>
    /// <param name="cancellationToken">Cancellation observed before reporting activation.</param>
    /// <returns>A completed activation after the assembly observation is emitted.</returns>
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (context.Row.Config is { } config && config.TryGetValue("fail_cleanup", out var raw))
        {
            if (raw is not bool failCleanup)
                throw new ConfigException(context.Row.Id, ["config.fail_cleanup must be a boolean."]);
            if (failCleanup)
                context.Effect(() => throw new InvalidOperationException("native ownership fixture cleanup failure"));
        }
        context.Logger.LogInformation("Module ownership fixture {FixtureRow} uses {FixtureAssembly}",
            context.Row.Id, typeof(NativeOwnershipPlugin).Assembly);
        return ValueTask.CompletedTask;
    }
}
