using BubbleFighter.Fantasy.Abstractions;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.TestFixtures.MultiDefinition;

/// <summary>First independent plugin definition exported by the multi-definition native fixture.</summary>
[Plugin("test.multi.alpha")]
public sealed class AlphaPlugin : IPlugin
{
    /// <summary>Reports this definition and the actual assembly selected by the production native backend.</summary>
    /// <param name="context">Context identifying the activated fixture row.</param>
    /// <param name="cancellationToken">Cancellation checked before recording activation.</param>
    /// <returns>A completed activation without external resources.</returns>
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        context.Logger.LogInformation("Multi-definition fixture {FixtureRow} definition {FixtureDefinition} uses {FixtureAssembly}",
            context.Row.Id, "test.multi.alpha", typeof(AlphaPlugin).Assembly);
        return ValueTask.CompletedTask;
    }
}

/// <summary>Second independent plugin definition whose refresh must not select the first exported type.</summary>
[Plugin("test.multi.beta")]
public sealed class BetaPlugin : IPlugin
{
    /// <summary>Reports the beta definition and its actual loaded assembly.</summary>
    /// <param name="context">Context identifying the activated fixture row.</param>
    /// <param name="cancellationToken">Cancellation checked before recording activation.</param>
    /// <returns>A completed activation without external resources.</returns>
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        context.Logger.LogInformation("Multi-definition fixture {FixtureRow} definition {FixtureDefinition} uses {FixtureAssembly}",
            context.Row.Id, "test.multi.beta", typeof(BetaPlugin).Assembly);
        return ValueTask.CompletedTask;
    }
}
