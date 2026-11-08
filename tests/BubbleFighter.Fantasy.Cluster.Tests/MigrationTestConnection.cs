using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using BubbleFighter.Fantasy.Abstractions.Net;

namespace BubbleFighter.Fantasy.Cluster.Tests;

internal sealed class MigrationTestConnection(EndPoint remote) : IConnection
{
    public long Id => 1;
    public EndPoint RemoteEndPoint => remote;
    public PipeReader Input => throw new NotSupportedException();
    public PipeWriter Output => throw new NotSupportedException();
    public ConcurrentDictionary<string, object> Items { get; } = new();
    public bool IsClosed { get; private set; }
    public ValueTask CloseAsync(string reason) { IsClosed = true; return ValueTask.CompletedTask; }

    internal static MigrationTestConnection Accept(MigrationArrivals arrivals, IPAddress address, string listenerId = "game")
    {
        var connection = new MigrationTestConnection(new IPEndPoint(address, 40000));
        arrivals.Observe(new(connection, listenerId));
        return connection;
    }
}
