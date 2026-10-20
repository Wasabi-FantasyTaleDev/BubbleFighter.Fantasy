namespace BubbleFighter.Fantasy.Abstractions;

/// <summary>Creates one activation's backend resource after the host has reserved ownership.</summary>
public interface IPluginExecutionFactory
{
    /// <summary>Gets an immutable shared coordination key when backend acquisition or reload
    /// must not overlap. Null permits independent entry executions.</summary>
    string? CoordinationKey { get; }

    /// <summary>Acquires an execution for this definition. The host owns the returned resource
    /// through activation rollback and cleanup; a late return is still released.</summary>
    /// <remarks>Cancellation is cooperative. A host observation timeout does not terminate
    /// backend code or relinquish ownership while acquisition is still running.</remarks>
    /// <param name="reload">Whether the backend should refresh its executable definition.</param>
    /// <param name="cancellationToken">Signals cancellation of acquisition.</param>
    /// <returns>The owned execution and its resolved metadata.</returns>
    ValueTask<IPluginExecution> CreateAsync(bool reload, CancellationToken cancellationToken);
}

/// <summary>Owns an activation backend until its plugin effects and dependent consumers drain.</summary>
public interface IPluginExecution : IAsyncDisposable
{
    /// <summary>Gets the resolved declaration. It must preserve the configured definition's
    /// service and override contracts until explicit definition reconciliation changes them.</summary>
    PluginAttribute Declaration { get; }

    /// <summary>Gets the backend's resolved release version for diagnostics.</summary>
    string Version { get; }

    /// <summary>Gets referenced shared contract versions for diagnostics and validation.</summary>
    IReadOnlyDictionary<string, string> ContractVersions { get; }

    /// <summary>Gets the native implementing type when one exists. Backends need not expose a CLR type.</summary>
    Type? ImplementationType { get; }

    /// <summary>Invokes plugin initialization through the supplied owned context exactly once.
    /// Register services and effects through that context; the host validates declarations,
    /// commits publication after success and unwinds owned contributions after failure.</summary>
    /// <param name="context">The declaration-bound activation context.</param>
    /// <param name="cancellationToken">Signals supersession or cancellation of activation.</param>
    ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken);
}
