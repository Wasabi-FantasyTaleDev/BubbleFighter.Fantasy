namespace BubbleFighter.Fantasy.Abstractions;

/// <summary>Owns a backend's complete candidate definition batch until admission rejects it or all admitted uses drain.</summary>
/// <remarks>A producer that throws before returning a batch remains responsible for its partial acquisition.
/// After return, the host owns disposal, including validation failure and cancellation with a late result.</remarks>
public interface IPluginDefinitionBatch : IAsyncDisposable
{
    /// <summary>Gets the complete candidate batch; the host snapshots this collection before atomic admission.</summary>
    IReadOnlyList<PluginDefinition> Definitions { get; }
}

/// <summary>Reports whether a definition source is accepting new uses or retaining resources during cleanup.</summary>
public enum PluginDefinitionSourceState
{
    /// <summary>Capacity and ownership are reserved while the producer is acquiring a candidate batch.</summary>
    Preparing,

    /// <summary>The entire accepted definition batch is available for new acquisitions.</summary>
    Published,

    /// <summary>New acquisitions are revoked while previously owned consumers and backend resources drain.</summary>
    Retiring,

    /// <summary>Cleanup failed and the host retains the source's outstanding resources and reserved capacity.</summary>
    CleanupFailed,

    /// <summary>No owned uses or backend resources remain and the source's capacity has been released.</summary>
    Retired,
}

/// <summary>Contains copied diagnostic values without exposing a source's ownership token, factories, or code references.</summary>
public sealed class PluginDefinitionSourceInfo
{
    /// <summary>Copies the source identity, revision, owner, state, definition identities, and optional failure text.</summary>
    /// <param name="sourceId">The diagnostic source identity scoped to its owning activation.</param>
    /// <param name="revision">The host-assigned source revision, independent of module refresh counters.</param>
    /// <param name="ownerId">The owning configured row identity.</param>
    /// <param name="state">The source's observed admission or cleanup state.</param>
    /// <param name="definitionIds">The definition identities to copy into a read-only collection.</param>
    /// <param name="error">A primitive diagnostic message when acquisition or cleanup failed.</param>
    public PluginDefinitionSourceInfo(string sourceId, long revision, string ownerId, PluginDefinitionSourceState state,
        IReadOnlyList<string> definitionIds, string? error = null)
    {
        ArgumentNullException.ThrowIfNull(sourceId);
        ArgumentNullException.ThrowIfNull(ownerId);
        ArgumentNullException.ThrowIfNull(definitionIds);
        SourceId = sourceId;
        Revision = revision;
        OwnerId = ownerId;
        State = state;
        DefinitionIds = Array.AsReadOnly(definitionIds.ToArray());
        Error = error;
    }

    /// <summary>Gets the diagnostic source identity; this string does not authorize revocation.</summary>
    public string SourceId { get; }

    /// <summary>Gets the host-assigned revision of this source registration.</summary>
    public long Revision { get; }

    /// <summary>Gets the configured row identity whose activation owns the registration.</summary>
    public string OwnerId { get; }

    /// <summary>Gets the observed source admission or cleanup state.</summary>
    public PluginDefinitionSourceState State { get; }

    /// <summary>Gets a copied read-only list of the batch's definition identities.</summary>
    public IReadOnlyList<string> DefinitionIds { get; }

    /// <summary>Gets the diagnostic acquisition or cleanup error text, when present.</summary>
    public string? Error { get; }
}

/// <summary>Identifies one owned source registration and supports revocation followed by asynchronous cleanup.</summary>
/// <remarks>Disposal revokes new selections before draining owned uses. Repeated disposal observes the same cleanup;
/// failed cleanup remains visible and does not imply that resources or capacity were released.</remarks>
public interface IPluginDefinitionRegistration : IAsyncDisposable
{
    /// <summary>Gets the current primitive diagnostic snapshot without granting access to the underlying source.</summary>
    PluginDefinitionSourceInfo Info { get; }

    /// <summary>Stages an owned replacement and atomically publishes its complete definitions before retiring this revision.</summary>
    /// <param name="definitionCapacity">Additional candidate capacity reserved while this revision remains owned.</param>
    /// <param name="coordinationKeys">The distinct module keys reserved before candidate resources are acquired.</param>
    /// <param name="acquire">Acquires a separate candidate batch without altering this revision's resources.</param>
    /// <param name="cancellationToken">Cancels only this caller's observation, not ownership of an accepted operation.</param>
    /// <returns>The accepted replacement registration; previous consumers and resources may still be draining.</returns>
    /// <remarks>Candidate failure leaves the current revision published. Startup capacity must cover both revisions.
    /// This handle cannot replace a newer revision or revoke its replacement. Publication does not promise
    /// successful consumer activation or transactional rollback of business effects.</remarks>
    ValueTask<IPluginDefinitionRegistration> ReplaceAsync(int definitionCapacity, IReadOnlyList<string> coordinationKeys,
        Func<CancellationToken, ValueTask<IPluginDefinitionBatch>> acquire, CancellationToken cancellationToken = default) =>
        ValueTask.FromException<IPluginDefinitionRegistration>(new NotSupportedException("This registration does not support atomic source replacement."));
}
