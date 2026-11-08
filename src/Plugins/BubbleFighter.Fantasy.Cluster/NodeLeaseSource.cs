using BubbleFighter.Fantasy.Contracts.Cluster;

namespace BubbleFighter.Fantasy.Cluster;

internal sealed class NodeLeaseSource(string nodeId, string directoryUrl) : INodeLeaseSource
{
    private NodeLeaseCredentials? _current;
    public string NodeId => nodeId;
    public string DirectoryUrl => directoryUrl;
    public NodeLeaseCredentials? Current => Volatile.Read(ref _current);
    internal void Publish(LeaseGrant? lease) => Volatile.Write(ref _current, lease is null ? null : new(lease.Token, lease.Epoch));
}
