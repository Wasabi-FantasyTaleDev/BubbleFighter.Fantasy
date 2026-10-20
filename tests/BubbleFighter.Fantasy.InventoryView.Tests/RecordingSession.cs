using BubbleFighter.Fantasy.Tests.Shared;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.InventoryView.Tests;

/// <summary>
/// Minimal <see cref="ISession"/> that records every outbound frame so behaviour
/// tests can assert on the reply opcode and body.
/// </summary>
internal sealed class RecordingSession(long id) : ISessionWithFeatures
{
    /// <inheritdoc />
    public ISessionFeatures Features { get; } = new TestSessionFeatures();

    /// <inheritdoc />
    public long Id { get; } = id;

    /// <inheritdoc />
    public IConnection Connection { get; } = new StubConnection();

    /// <inheritdoc />
    public ClientProfile Profile { get; } = new("test", "kr", 1256, 0, 0, "test");

    /// <inheritdoc />
    public ConcurrentDictionary<string, object> Items { get; } = new();

    /// <summary>Gets the frames sent by the plugin, in order.</summary>
    public List<(ushort Opcode, byte[] Payload)> Sent { get; } = [];

    /// <inheritdoc />
    public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        Sent.Add((opcode, payload.ToArray()));
        return ValueTask.CompletedTask;
    }
}

/// <summary>A connection stub for tests that never touch the transport.</summary>
internal sealed class StubConnection : IConnection
{
    /// <inheritdoc />
    public long Id => 0;

    /// <inheritdoc />
    public EndPoint RemoteEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 1);

    /// <inheritdoc />
    public PipeReader Input => throw new NotSupportedException();

    /// <inheritdoc />
    public PipeWriter Output => throw new NotSupportedException();

    /// <inheritdoc />
    public ConcurrentDictionary<string, object> Items { get; } = new();

    /// <inheritdoc />
    public bool IsClosed => false;

    /// <inheritdoc />
    public ValueTask CloseAsync(string reason) => ValueTask.CompletedTask;
}
