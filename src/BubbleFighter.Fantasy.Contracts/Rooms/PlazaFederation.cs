namespace BubbleFighter.Fantasy.Contracts.Rooms;

/// <summary>What happened to one plaza member on the node that hosts the player.</summary>
public enum PlazaFederationEventKind
{
    /// <summary>The player entered the plaza or changed appearance; <see cref="PlazaFederationEvent.Member"/> is set.</summary>
    Entered,

    /// <summary>The player reported a position; <see cref="PlazaFederationEvent.Position"/> is the build's upload body.</summary>
    Moved,

    /// <summary>The player left the plaza, by any route.</summary>
    Left,
}

/// <summary>
/// One plaza change shared between nodes of the same account domain and client build. The member's seat is the
/// hosting node's; each receiving node seats the player in its own plaza.
/// </summary>
/// <param name="Kind">What happened.</param>
/// <param name="UserNumber">The player's account number, unique across the shared domain.</param>
/// <param name="Member">The appearance, for <see cref="PlazaFederationEventKind.Entered"/>; otherwise null.</param>
/// <param name="Position">The position upload body for <see cref="PlazaFederationEventKind.Moved"/>; otherwise empty.</param>
public sealed record PlazaFederationEvent(PlazaFederationEventKind Kind, int UserNumber, PlazaMember? Member,
    ReadOnlyMemory<byte> Position);

/// <summary>Remote plaza changes for one build, in order.</summary>
/// <param name="Snapshot">
/// True when <paramref name="Events"/> describe every remote member now present (entries, then last positions):
/// the receiver replaces all remote members it holds, after a reconnect or a directory restart.
/// </param>
/// <param name="Events">The changes.</param>
public sealed record PlazaFederationBatch(bool Snapshot, IReadOnlyList<PlazaFederationEvent> Events);

/// <summary>The channel a plaza publishes its local members' changes through to the other nodes.</summary>
public interface IPlazaFederationLink
{
    /// <summary>Queues a local change without waiting; a full queue drops position updates before entries and leaves.</summary>
    /// <param name="change">The change.</param>
    void Publish(PlazaFederationEvent change);
}

/// <summary>
/// A build's plaza, offered to a cluster row that shares it with the same build on other nodes of one account domain.
/// Rooms publish it at activation; the cluster row starts later and binds it.
/// </summary>
public interface IPlazaFederationEndpoint
{
    /// <summary>Gets the client build whose plaza this is.</summary>
    string ClientBuild { get; }

    /// <summary>
    /// Attaches the link and publishes every local member present now. Disposing detaches it and removes every remote
    /// member from the plaza. One link at a time.
    /// </summary>
    /// <param name="link">The link to the other nodes.</param>
    /// <returns>The binding.</returns>
    IDisposable Bind(IPlazaFederationLink link);

    /// <summary>Applies remote changes on the plaza's executor and shows them to the local players present.</summary>
    /// <param name="batch">The changes.</param>
    /// <param name="cancellationToken">Cancels the wait for the plaza executor.</param>
    ValueTask ApplyAsync(PlazaFederationBatch batch, CancellationToken cancellationToken);
}
