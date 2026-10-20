using BubbleFighter.Fantasy.Tests.Shared;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Equipment.Tests;

/// <summary>
/// Minimal <see cref="ISession"/> that records every outbound frame so behaviour tests
/// can assert on the reply opcode and body. An optional <paramref name="userNum"/>
/// plants the lobby player record the equipment row resolves the seat's identity
/// through; a session without one stands for a connection that never logged in.
/// </summary>
internal sealed class RecordingSession(long id, long userNum = 0) : ISessionWithFeatures
{
    /// <inheritdoc />
    public ISessionFeatures Features { get; } = TestSessionFeatures.WithPlayer(userNum);

    /// <inheritdoc />
    public long Id { get; } = id;

    /// <inheritdoc />
    public IConnection Connection { get; } = new StubConnection(id);

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

    private sealed record FakePlayer(long UserNum);
}

/// <summary>A connection stub for tests that never touch the transport.</summary>
internal sealed class StubConnection(long id) : IConnection
{
    /// <inheritdoc />
    public long Id { get; } = id;

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
