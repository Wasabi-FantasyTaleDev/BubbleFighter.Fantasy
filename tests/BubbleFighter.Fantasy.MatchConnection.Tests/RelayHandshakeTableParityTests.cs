using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Pipelines;
using System.Net;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Dialect.Modern.Messages;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using Xunit;

namespace BubbleFighter.Fantasy.MatchConnection.Tests;

/// <summary>
/// Proves that the relay handshake frames stated in packets/relay-*.yaml reproduce what ModernMatchCodec
/// reads and writes: the same account, entity, map and nickname values, the same reply bytes, the same byte
/// lengths, and the same truncation and trailing-byte rejections. Client evidence: kr-1256 sub_95FD30
/// (C2S relay 1), kr-1256 sub_9F02F0 (C2S relay 3), kr-1256 sub_9601E0 cases 1, 4, 5, 6, 13 and 22
/// (S2C relay) and kr-1256 sub_1155B00 (game S2C 521).
/// </summary>
public sealed class RelayHandshakeTableParityTests
{
    private const string Build = "kr-1256";

    private static readonly PacketTables Tables = PacketTables.Load(Path.Combine(RepositoryDirectory(), "packets"));

    // The relay connection is its own numeric space; resolve names through it, never through the game table.
    private static readonly IOpcodeTable Relay = OpcodeTableLoader.Load(
        Path.Combine(RepositoryDirectory(), "profiles", Build, "opcodes.yaml"),
        new ClientProfile(Build, "kr", 1256, 0, 0, Build)).ForConnection(OpcodeConnection.Relay);

    [Fact]
    public void TheAuthenticationBodyReadsTheSameAccountAndNicknameFromTheTable()
    {
        // kr-1256 sub_95FD30 stamps 1 and writes the 20-byte session key, one byte, two words and the
        // nickname as a counted string (layout RelayJoinRequest).
        var body = Bytes(w =>
        {
            w.WriteBytes(new byte[20]);
            w.WriteByte(0);
            w.WriteInt32(0);
            w.WriteInt32(7);
            w.WriteWideStringPrefixed("host");
        });
        Assert.Equal(41, body.Length);

        var codec = ModernMatchCodec.ReadAuthentication(body);
        Assert.Equal(20, codec.Ticket.Length);
        Assert.Equal(7, codec.UserNumber);
        Assert.Equal("host", codec.Nickname);

        // RelayJoinRequest nests the session key, so the table reader is PacketCodec.Read:
        // ReadScalars refuses nested structures, which is a reader choice, not a missing layout.
        var table = PacketCodec.Read(Tables, Build, "RelayJoinRequest", body);
        Assert.Equal(codec.UserNumber, Convert.ToInt32(table["unknown_n3"], CultureInfo.InvariantCulture));
        Assert.Equal(codec.Nickname, table["unknown_n4"] as string);
        var key = Assert.IsType<Dictionary<string, object?>>(table["key"]);
        // The relay session key states six fields for every audited build (two fewer for cn-279, one for cn-324).
        Assert.Equal(6, key.Count);

        for (var length = 0; length < body.Length; length++)
        {
            Assert.Throws<PacketFormatException>(() => ModernMatchCodec.ReadAuthentication(body.AsSpan(0, length)));
            Assert.Throws<PacketFormatException>(() => PacketCodec.Read(Tables, Build, "RelayJoinRequest", body.AsSpan(0, length)));
        }
        Assert.Throws<PacketFormatException>(() => ModernMatchCodec.ReadAuthentication([.. body, 0]));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(Tables, Build, "RelayJoinRequest", [.. body, 0]));
    }

    [Fact]
    public void TheMapBodyReadsTheSameVersionMapEntityAndNicknameFromTheTable()
    {
        // kr-1256 sub_9F02F0 stamps 3 and writes its version word, a widened byte, the map string, a word,
        // the nickname string and two words (layout RelayReconnectMapRequest).
        var body = Bytes(w =>
        {
            w.WriteInt32(1256);
            w.WriteInt32(0);
            w.WriteWideStringPrefixed("map");
            w.WriteInt32(1000000);
            w.WriteWideStringPrefixed("host");
            w.WriteInt32(0);
            w.WriteInt32(0);
        });
        Assert.Equal(42, body.Length);

        var codec = ModernMatchCodec.ReadMap(body);
        Assert.Equal(1000000, codec.EntityId);
        Assert.Equal("host", codec.Nickname);
        Assert.Equal("map", codec.Map);

        var table = PacketCodec.ReadScalars(Tables, Build, "RelayReconnectMapRequest", body);
        Assert.Equal(1256, Convert.ToInt32(table["client_version"], CultureInfo.InvariantCulture));
        Assert.Equal(codec.Map, table["unknown_n2"] as string);
        Assert.Equal(codec.EntityId, Convert.ToInt32(table["unknown_n3"], CultureInfo.InvariantCulture));
        Assert.Equal(codec.Nickname, table["unknown_n4"] as string);

        for (var length = 0; length < body.Length; length++)
        {
            Assert.Throws<PacketFormatException>(() => ModernMatchCodec.ReadMap(body.AsSpan(0, length)));
            Assert.Throws<PacketFormatException>(() =>
                PacketCodec.ReadScalars(Tables, Build, "RelayReconnectMapRequest", body.AsSpan(0, length)));
        }
        Assert.Throws<PacketFormatException>(() => ModernMatchCodec.ReadMap([.. body, 0]));
        Assert.Throws<PacketFormatException>(() =>
            PacketCodec.ReadScalars(Tables, Build, "RelayReconnectMapRequest", [.. body, 0]));
    }

    [Fact]
    public async Task TheAuthenticationRepliesMatchTheTableAndPinTenAndFourBytes()
    {
        var session = new RecordingSession(1);
        await ModernMatchCodec.SendAuthenticationAsync(session, CancellationToken.None);

        Assert.Equal(new ushort[] { 22, 1 }, session.Sent.Select(frame => frame.Opcode));
        var settings = PacketCodec.Write(Tables, Build, "RelayHandshakeSettings", new Dictionary<string, object?>());
        var accepted = PacketCodec.Write(Tables, Build, "RelayJoinAcceptedSignal", new Dictionary<string, object?>());
        Assert.Equal(10, settings.Length);
        Assert.Equal(4, accepted.Length);
        Assert.Equal(settings, session.Sent[0].Body);
        Assert.Equal(accepted, session.Sent[1].Body);
    }

    [Fact]
    public async Task TheSceneRepliesMatchTheSeedTablesAndPinTheirLengths()
    {
        var session = new RecordingSession(1);
        await ModernMatchCodec.SendSceneAsync(session, 1000000, CancellationToken.None);

        Assert.Equal(new ushort[] { 4, 5, 6, 13 }, session.Sent.Select(frame => frame.Opcode));
        var critical = PacketCodec.Write(Tables, Build, "RelayCriticalSeedTable", new Dictionary<string, object?>
        {
            ["entity_id"] = 1000000u,
            ["unknown_8"] = Zeros(32),
            ["unknown_24"] = Zeros(32),
        });
        var defence = PacketCodec.Write(Tables, Build, "RelayDefenceSeedTable", new Dictionary<string, object?>
        {
            ["entity_id"] = 1000000u,
            ["unknown_8"] = Zeros(1000),
        });
        var cancelled = PacketCodec.Write(Tables, Build, "RelayCriticalSeedCancelTable", new Dictionary<string, object?>
        {
            ["entity_id"] = 1000000u,
            ["unknown_8"] = Zeros(32),
        });
        var accepted = PacketCodec.Write(Tables, Build, "RelaySceneAcceptedSignal", new Dictionary<string, object?>());
        Assert.Equal(268, critical.Length);
        Assert.Equal(1008, defence.Length);
        Assert.Equal(136, cancelled.Length);
        Assert.Empty(accepted);
        Assert.Equal(critical, session.Sent[0].Body);
        Assert.Equal(defence, session.Sent[1].Body);
        Assert.Equal(cancelled, session.Sent[2].Body);
        Assert.Equal(accepted, session.Sent[3].Body);
    }

    [Fact]
    public void TheSessionSignatureMatchesTheTableAndPinsSevenBytes()
    {
        var bytes = ModernMatchCodec.WriteSessionSignature(7);
        var table = PacketCodec.Write(Tables, Build, "SessionSignatureData", new Dictionary<string, object?>
        {
            ["unknown_4"] = (ushort)7,
        });
        Assert.Equal(7, bytes.Length);
        Assert.Equal(table, bytes);
    }

    [Theory]
    [InlineData(PacketDirection.ClientToServer, "rcpReqJoinRelaySession", 1)]
    [InlineData(PacketDirection.ClientToServer, "rcpReqRelayReconnectMap_", 3)]
    [InlineData(PacketDirection.ServerToClient, "rspRelayJoinAccepted_", 1)]
    [InlineData(PacketDirection.ServerToClient, "rspSendCrtSeedTable", 4)]
    [InlineData(PacketDirection.ServerToClient, "rspSendDefSeedTable", 5)]
    [InlineData(PacketDirection.ServerToClient, "rspSendCrtCancelSeedTable", 6)]
    [InlineData(PacketDirection.ServerToClient, "rspRelaySceneAccepted_", 13)]
    [InlineData(PacketDirection.ServerToClient, "rspRelayHandshakeSettings_", 22)]
    public void EachHandshakeFrameResolvesOnTheRelayConnection(PacketDirection direction, string name, int value)
    {
        var resolved = direction == PacketDirection.ClientToServer ? Relay.C2S(name) : Relay.S2C(name);
        Assert.Equal((ushort)value, resolved);
    }

    private static object?[] Zeros(int count) => Enumerable.Repeat<object?>(0u, count).ToArray();

    private static byte[] Bytes(Action<PacketWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        write(new PacketWriter(buffer));
        return buffer.WrittenSpan.ToArray();
    }

    // The repository root is above the test output folder; the packet tables and profiles live there.
    private static string RepositoryDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "packets", "builds.yaml"))
                && File.Exists(Path.Combine(directory.FullName, "profiles", Build, "opcodes.yaml")))
                return directory.FullName;
        }
        throw new InvalidOperationException("The repository root was not found above the test output folder.");
    }

    private sealed class RecordingSession(long id) : ISession
    {
        public long Id => id;
        public IConnection Connection { get; } = new RecordingConnection(id);
        public ClientProfile Profile { get; } = new(Build, "kr", 1256, 0, 0, Build);
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public List<(ushort Opcode, byte[] Body)> Sent { get; } = [];
        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Sent.Add((opcode, payload.ToArray()));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingConnection(long id) : IConnection
    {
        public long Id => id;
        public EndPoint RemoteEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 1);
        public PipeReader Input => throw new NotSupportedException();
        public PipeWriter Output => throw new NotSupportedException();
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public bool IsClosed => false;
        public ValueTask CloseAsync(string reason) => ValueTask.CompletedTask;
    }
}
