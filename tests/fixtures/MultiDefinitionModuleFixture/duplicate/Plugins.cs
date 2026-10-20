using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.TestFixtures.DuplicateDefinition;

/// <summary>First conflicting export used to reject duplicate identities inside one actual native DLL.</summary>
[Plugin("test.multi.duplicate")]
public sealed class DuplicateAlphaPlugin : IPlugin
{
    /// <summary>Provides an inert activation that catalog validation must never need to call.</summary>
    /// <param name="context">Unused fixture context.</param>
    /// <param name="cancellationToken">Cancellation checked if an invalid catalog is ever activated.</param>
    /// <returns>A completed fixture activation.</returns>
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Second conflicting export with the same declared id as another type in this native module.</summary>
[Plugin("test.multi.duplicate")]
public sealed class DuplicateBetaPlugin : IPlugin
{
    /// <summary>Provides an inert activation that cannot resolve the fixture's ambiguous identity.</summary>
    /// <param name="context">Unused fixture context.</param>
    /// <param name="cancellationToken">Cancellation checked if an invalid catalog is ever activated.</param>
    /// <returns>A completed fixture activation.</returns>
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }
}
