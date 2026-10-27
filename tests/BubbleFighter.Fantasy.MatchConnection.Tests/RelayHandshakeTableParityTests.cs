using System.Buffers;
using System.Globalization;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Dialect.Messages;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using Xunit;

namespace BubbleFighter.Fantasy.MatchConnection.Tests;

/// <summary>
/// Pins the kr-1256 relay handshake frames stated in packets/relay-*.yaml as literal hex: the same account,
/// entity, map and nickname values through the table readers and the moved wire readers, the same reply bytes,
/// the same byte lengths, and the same truncation and trailing-byte rejections. Client evidence: kr-1256
/// sub_95FD30 (C2S relay 1), kr-1256 sub_9F02F0 (C2S relay 3), kr-1256 sub_10F32C0 (C2S relay 12),
/// kr-1256 sub_9601E0 cases 1, 4, 5, 6, 13 and 22 (S2C relay) and kr-1256 sub_1155B00 (game S2C 521).
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

        var codec = RelayHandshakeWire.ReadAccountJoin(Tables, Build, body);
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
            Assert.Throws<PacketFormatException>(() => RelayHandshakeWire.ReadAccountJoin(Tables, Build, body.AsSpan(0, length)));
            Assert.Throws<PacketFormatException>(() => PacketCodec.Read(Tables, Build, "RelayJoinRequest", body.AsSpan(0, length)));
        }
        Assert.Throws<PacketFormatException>(() => RelayHandshakeWire.ReadAccountJoin(Tables, Build, [.. body, 0]));
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

        var codec = RelayHandshakeWire.ReadMap(Tables, Build, 1256, body);
        Assert.Equal(1000000, codec.EntityId);
        Assert.Equal("host", codec.Nickname);
        Assert.Equal("map", codec.Map);

        var table = PacketCodec.ReadScalars(Tables, Build, "RelayReconnectMapRequest", body);
        Assert.Equal(1256, Convert.ToInt32(table["client_version"], CultureInfo.InvariantCulture));
        Assert.Equal(codec.Map, table["unknown_n2"] as string);
        Assert.Equal(codec.EntityId, Convert.ToInt32(table["unknown_n3"], CultureInfo.InvariantCulture));
        Assert.Equal(codec.Nickname, table["unknown_n4"] as string);

        // The version word must be the client's own; another word is not the frame the reader knows.
        var wrongVersion = (byte[])body.Clone();
        BitConverter.GetBytes(812).CopyTo(wrongVersion, 0);
        Assert.Throws<PacketFormatException>(() => RelayHandshakeWire.ReadMap(Tables, Build, 1256, wrongVersion));

        for (var length = 0; length < body.Length; length++)
        {
            Assert.Throws<PacketFormatException>(() => RelayHandshakeWire.ReadMap(Tables, Build, 1256, body.AsSpan(0, length)));
            Assert.Throws<PacketFormatException>(() =>
                PacketCodec.ReadScalars(Tables, Build, "RelayReconnectMapRequest", body.AsSpan(0, length)));
        }
        Assert.Throws<PacketFormatException>(() => RelayHandshakeWire.ReadMap(Tables, Build, 1256, [.. body, 0]));
        Assert.Throws<PacketFormatException>(() =>
            PacketCodec.ReadScalars(Tables, Build, "RelayReconnectMapRequest", [.. body, 0]));
    }

    // The three pins below state the accept path's contract as literal hex written ONLY through the tables,
    // the frames the deleted modern codec used to send; the mounted dialect.match-replies sender reproduces
    // them in the same order (MatchRepliesMountTests).

    [Fact]
    public void TheAuthenticationAcceptPairPinsItsExactBytesThroughTheTables()
    {
        var settings = PacketCodec.Write(Tables, Build, "RelayHandshakeSettings", new Dictionary<string, object?>());
        var accepted = PacketCodec.Write(Tables, Build, "RelayJoinAcceptedSignal", new Dictionary<string, object?>());
        Assert.Equal(10, settings.Length);
        Assert.Equal(4, accepted.Length);
        // kr-1256 sub_9601E0 case 22 (block 0x96113C) reads one zero bool32 word and six zero bytes;
        // case 1 (block 0x960278) reads no field, so the accepted word is four zero bytes.
        Assert.Equal("00000000000000000000", Convert.ToHexString(settings));
        Assert.Equal("00000000", Convert.ToHexString(accepted));
    }

    [Fact]
    public void TheSceneAcceptFramesPinTheirExactBytesThroughTheTables()
    {
        const int entity = 1000000;
        var critical = PacketCodec.Write(Tables, Build, "RelayCriticalSeedTable", new Dictionary<string, object?>
        {
            ["entity_id"] = (uint)entity,
            ["unknown_8"] = Zeros(32),
            ["unknown_24"] = Zeros(32),
        });
        var defence = PacketCodec.Write(Tables, Build, "RelayDefenceSeedTable", new Dictionary<string, object?>
        {
            ["entity_id"] = (uint)entity,
            ["unknown_8"] = Zeros(1000),
        });
        var cancelled = PacketCodec.Write(Tables, Build, "RelayCriticalSeedCancelTable", new Dictionary<string, object?>
        {
            ["entity_id"] = (uint)entity,
            ["unknown_8"] = Zeros(32),
        });
        // Consumers kr-1256 sub_4832D0/sub_483420 reset two 32-word bitsets, kr-1256 sub_483540 a
        // 1000-byte per-entity state: entity word, then the counted zero vectors of exactly those sizes.
        Assert.Equal(268, critical.Length);
        Assert.Equal(1008, defence.Length);
        Assert.Equal(136, cancelled.Length);
        Assert.Equal("40420F00" + "20000000" + new string('0', 256) + "20000000" + new string('0', 256),
            Convert.ToHexString(critical));
        Assert.Equal("40420F00" + "E8030000" + new string('0', 2000), Convert.ToHexString(defence));
        Assert.Equal("40420F00" + "20000000" + new string('0', 256), Convert.ToHexString(cancelled));
    }

    [Fact]
    public void TheSessionSignaturePinsItsExactBytesThroughTheTables()
    {
        var table = LoginBurstWire.WriteSessionSignature(Tables, Build, 7);
        Assert.Equal(7, table.Length);
        // kr-1256 sub_1155B00 stores the mode word, then one byte and the empty byte vector.
        Assert.Equal("0700" + "00" + "00000000", Convert.ToHexString(table));
    }

    [Theory]
    [InlineData(PacketDirection.ClientToServer, "rcpReqJoinRelaySession", 1)]
    [InlineData(PacketDirection.ClientToServer, "rcpReqRelayReconnectMap_", 3)]
    [InlineData(PacketDirection.ClientToServer, "rcpReqReportSceneData_", 12)]
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
    internal static string RepositoryDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "packets", "builds.yaml"))
                && File.Exists(Path.Combine(directory.FullName, "profiles", Build, "opcodes.yaml")))
                return directory.FullName;
        }
        throw new InvalidOperationException("The repository root was not found above the test output folder.");
    }
}
