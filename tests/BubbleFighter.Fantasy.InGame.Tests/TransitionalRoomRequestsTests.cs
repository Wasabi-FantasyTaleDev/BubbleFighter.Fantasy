using System.Buffers.Binary;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using BubbleFighter.Fantasy.Tests;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>Pins room admission and start requests of the four-byte-cookie Chinese clients.</summary>
public sealed class TransitionalRoomRequestsTests
{
    private static readonly string Root = Directory.GetParent(SharedDialectRows.PacketsDirectory())!.FullName;
    private static readonly PacketTables Tables = PacketTables.Load(Path.Combine(Root, "packets"));

    /// <summary>The native join contains one room word, one string and one trailing word.</summary>
    [Theory]
    [InlineData("cn-735")]
    [InlineData("cn-738")]
    [InlineData("cn-742")]
    public void JoinConsumesTheCompleteNativeRequest(string build)
    {
        byte[] body = Convert.FromHexString("4433221101000000410088776655");
        Assert.Equal(14, body.Length);
        var request = RoomRequests.ReadJoin(Tables, build, body);
        Assert.Equal(0x11223344, request.RoomId);
        Assert.Equal("A", request.Password);
        var fields = PacketCodec.ReadScalars(Tables, build, "RoomJoinRequest", body);
        Assert.Equal(0x55667788, fields["unknown_36"]);
        Assert.Equal(body, PacketCodec.Write(Tables, build, "RoomJoinRequest",
            fields.ToDictionary(pair => pair.Key, pair => (object?)pair.Value)));
        for (var length = 0; length < body.Length; length++)
            Assert.Throws<PacketFormatException>(() => RoomRequests.ReadJoin(Tables, build, body[..length]));
        Assert.Throws<PacketFormatException>(() => RoomRequests.ReadJoin(Tables, build, [.. body, 0]));
    }

    /// <summary>The start rule block ends with one byte and has no extra flags or word.</summary>
    [Theory]
    [InlineData("cn-735")]
    [InlineData("cn-738")]
    [InlineData("cn-742")]
    public void StartUsesTheNativeRuleByte(string build)
    {
        byte[] body = [
            0, 0, 0, 0,
            1, 0, 0, 0, 109, 0,
            1, 0, 0, 0, 110, 0,
            1, 0, 0, 0, 115, 0,
            0, 0, 0, 0,
            1, 0, 0, 0, 2, 0, 0, 0, 3, 0, 0, 0, 4, 0, 0, 0,
            5, 0, 0, 0, 6, 0, 0, 0, 7, 0, 0, 0,
            64, 13, 3, 0, 128, 26, 6, 0,
            10, 0, 0, 0, 11, 0, 0, 0, 12, 0, 0, 0, 13, 0, 0, 0, 0xA5,
        ];
        Assert.Equal(79, body.Length);
        var fields = RoomRequests.ReadStart(Tables, build, body);
        Assert.Equal("m", fields["map_key"]);
        Assert.Equal("n", fields["unknown_36"]);
        Assert.Equal("s", fields["unknown_64"]);
        Assert.Equal(200000, fields["death_match_time_limit_ms"]);
        Assert.Equal(400000, fields["time_attack_time_limit_ms"]);
        Assert.Equal((byte)0xA5, fields["unknown_152"]);
        Assert.Equal(body, PacketCodec.Write(Tables, build, "RoomEnterGameRequest",
            fields.ToDictionary(pair => pair.Key, pair => (object?)pair.Value)));
        for (var length = 0; length < body.Length; length++)
            Assert.Throws<PacketFormatException>(() => RoomRequests.ReadStart(Tables, build, body[..length]));
        Assert.Throws<PacketFormatException>(() => RoomRequests.ReadStart(Tables, build, [.. body, 0]));
    }

    /// <summary>Creation ends after the password, and each build retains its own session suffix types.</summary>
    [Theory]
    [InlineData("cn-735", "u32", "bool")]
    [InlineData("cn-738", "bool", "u32")]
    [InlineData("cn-742", "bool", "u32")]
    public void CreateHasNoLegacyBooleanSuffix(string build, string firstType, string lastType)
    {
        var body = new byte[243];
        BinaryPrimitives.WriteUInt16LittleEndian(body, 7);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(2), 2);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(24), 2);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(237), 1);
        body[241] = (byte)'A';
        var fields = RoomRequests.ReadCreate(Tables, build, body);
        Assert.Equal("A", fields["password"]);
        Assert.False(fields.ContainsKey("unknown_n2"));
        var setting = Assert.IsType<Dictionary<string, object?>>(fields["session_setting"]);
        Assert.Equal((ushort)7, setting["room_id"]);
        Assert.Equal(2, setting["session_state"]);
        Assert.Equal(2, setting["kind"]);
        Assert.Equal(firstType, Tables.Resolve(build, "SessionSetting").Single(field => field.Name == "unknown_116").Type);
        Assert.Equal(lastType, Tables.Resolve(build, "SessionSetting").Single(field => field.Name == "unknown_132").Type);
        Assert.Equal(body, PacketCodec.Write(Tables, build, "RoomCreateRequest", fields));
        for (var length = 0; length < body.Length; length++)
            Assert.Throws<PacketFormatException>(() => RoomRequests.ReadCreate(Tables, build, body[..length]));
        Assert.Throws<PacketFormatException>(() => RoomRequests.ReadCreate(Tables, build, [.. body, 0, 0, 0, 0]));
    }

    /// <summary>Game requests retain their proven identities without occupying the relay opcode space.</summary>
    [Theory]
    [InlineData("cn-735")]
    [InlineData("cn-738")]
    [InlineData("cn-742")]
    public void CoreRoomRequestsResolveOnlyOnTheGameConnection(string build)
    {
        var profile = new ClientProfile(build, "cn", int.Parse(build[3..]), 0, 0, build);
        var table = OpcodeTableLoader.Load(Path.Combine(Root, "profiles", build, "opcodes.yaml"), profile);
        foreach (var (name, opcode) in new (string, ushort)[] {
            ("gcpReqListSession", 401), ("gcpReqCreateSession", 402), ("gcpReqJoinSession", 403),
            ("gcpReqLeaveSession", 404), ("gcpRoomReqEnterGame", 503), ("gcpRoomReqReadyStatus", 505),
            ("gcpGameReportMapLoadingComplete", 701), ("gcpReportRelayConnected", 601),
        })
        {
            Assert.Equal(opcode, table.C2S(name));
            Assert.Null(table.ForConnection(OpcodeConnection.Relay).Find(PacketDirection.ClientToServer, name));
        }
    }
}
