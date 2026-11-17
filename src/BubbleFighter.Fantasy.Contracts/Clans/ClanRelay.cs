namespace BubbleFighter.Fantasy.Contracts.Clans;

/// <summary>
/// One clan notice for members whose clan connection is on another node. A node that cannot tell a member a notice
/// itself hands it to the relay; the node holding that member's connection writes it in the member's own build.
/// </summary>
/// <param name="Kind">The typed message the body holds, by its contract type name (for example <c>ClanChatReceived</c>).</param>
/// <param name="Recipients">The members to tell, by user number; members not connected to a receiving node are skipped there.</param>
/// <param name="Body">The message as a JSON object.</param>
public sealed record ClanRelayNotice(string Kind, IReadOnlyList<long> Recipients, string Body);

/// <summary>The channel a node's clan connections hand notices for members elsewhere to.</summary>
public interface IClanRelayLink
{
    /// <summary>Queues a notice without waiting; a full queue drops the oldest notice.</summary>
    /// <param name="notice">The notice.</param>
    void Publish(ClanRelayNotice notice);
}

/// <summary>
/// The node's clan connections, offered to a cluster row that relays clan notices between nodes of one account domain.
/// The clan connections row publishes it at activation; the cluster row starts later and binds it.
/// </summary>
public interface IClanRelayEndpoint
{
    /// <summary>Attaches the relay. Disposing detaches it; notices for members elsewhere are then dropped again. One link at a time.</summary>
    /// <param name="link">The link to the other nodes.</param>
    /// <returns>The binding.</returns>
    IDisposable Bind(IClanRelayLink link);

    /// <summary>Tells the notices to those of their recipients whose clan connection is on this node, each in its own build.</summary>
    /// <param name="notices">Notices published by other nodes, in order.</param>
    /// <param name="cancellationToken">Cancels the delivery.</param>
    ValueTask DeliverAsync(IReadOnlyList<ClanRelayNotice> notices, CancellationToken cancellationToken);
}
