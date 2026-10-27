using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Tests.Shared;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>
/// In-memory game session that records every send, for tests that assert whole reply sets in order
/// without a socket. Shared by the room, plaza and round suites.
/// </summary>
/// <param name="id">The session id.</param>
internal sealed class RecordingSession(long id, ClientProfile? profile = null) : ISessionWithFeatures
{
    /// <inheritdoc />
    public ISessionFeatures Features { get; } = new TestSessionFeatures();

    private readonly List<(ushort Opcode, byte[] Body)> _sent = [];

    public long Id { get; } = id;
    public IConnection Connection { get; } = new FakeConnection(id);
    public ClientProfile Profile { get; } = profile ?? new("test", "kr", 1256, 0, 0, "test");
    public ConcurrentDictionary<string, object> Items { get; } = new();

    public IEnumerable<ushort> Opcodes
    {
        get
        {
            lock (_sent)
                return _sent.Select(s => s.Opcode).ToArray();
        }
    }

    public byte[] Body(ushort opcode)
    {
        lock (_sent)
            return _sent.Last(s => s.Opcode == opcode).Body;
    }

    /// <summary>Gets every body recorded for an opcode, in send order.</summary>
    public byte[][] Bodies(ushort opcode)
    {
        lock (_sent)
            return [.. _sent.Where(s => s.Opcode == opcode).Select(s => s.Body)];
    }

    /// <summary>Gets the first body recorded for an opcode.</summary>
    /// <param name="opcode">The opcode to look up.</param>
    public byte[] First(ushort opcode)
    {
        lock (_sent)
            return _sent.First(s => s.Opcode == opcode).Body;
    }

    /// <summary>Gets the last body recorded for an opcode.</summary>
    /// <param name="opcode">The opcode to look up.</param>
    public byte[] Last(ushort opcode)
    {
        lock (_sent)
            return _sent.Last(s => s.Opcode == opcode).Body;
    }

    /// <summary>Gets or sets an optional callback run after each send is recorded, for tests that widen interleaving windows.</summary>
    public Func<ushort, byte[], ValueTask>? OnSend { get; set; }

    public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        var body = payload.ToArray();
        lock (_sent)
            _sent.Add((opcode, body));
        return OnSend?.Invoke(opcode, body) ?? ValueTask.CompletedTask;
    }
}

/// <summary>In-memory connection stub; the pipe members are never touched by the room and round flows.</summary>
/// <param name="id">The connection id; unique per session so seat lookups can tell sessions apart.</param>
internal sealed class FakeConnection(long id) : IConnection
{
    public long Id { get; } = id;
    public EndPoint RemoteEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 1);
    public PipeReader Input => throw new NotSupportedException();
    public PipeWriter Output => throw new NotSupportedException();
    public ConcurrentDictionary<string, object> Items { get; } = new();
    public bool IsClosed { get; private set; }
    public ValueTask CloseAsync(string reason)
    {
        IsClosed = true;
        return ValueTask.CompletedTask;
    }
}

/// <summary>Records typed sends so handler tests assert messages, not one build's bytes.</summary>
internal sealed class CapturingSender<T> : IMessageSender<T> where T : IMessage
{
    public List<T> Sent { get; } = [];

    public string Protocol => "kr-1256/game";

    public ValueTask SendAsync(ISession session, T message, CancellationToken ct)
    {
        Sent.Add(message);
        return ValueTask.CompletedTask;
    }
}
