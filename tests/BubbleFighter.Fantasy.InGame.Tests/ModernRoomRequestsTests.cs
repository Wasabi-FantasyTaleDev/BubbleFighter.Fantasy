using System.Buffers;
using System.Buffers.Binary;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Dialect.Modern.Messages.Play;
using BubbleFighter.Fantasy.InGame.Wire;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>Modern request decoding must preserve the established wire shapes and reject incomplete bodies.</summary>
public sealed class ModernRoomRequestsTests
{
    private static readonly Lazy<PacketTables> Tables = new(() => PacketTables.Load(FindTables()));

    private static string FindTables()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "packets");
            if (File.Exists(Path.Combine(path, "builds.yaml")))
                return path;
        }
        throw new InvalidOperationException("The packet tables were not found above the test output.");
    }

    private static byte[] Raw(Action<PacketWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        write(new PacketWriter(buffer));
        return buffer.WrittenSpan.ToArray();
    }

    private static SessionSettingBlob Setting() =>
        SessionSettingBlob.ForRoomListRow("Room", "Master", 3, 7, 1, 8, 42, hasPassword: true) with
        {
            Map = MapMetaBlob.ForTrainingRoom(3, "rel_Map") with
            {
                Unknown297 = [new MapMetaPair(7, 8)],
                Unknown310 = [new MapMetaKeyedString(4, "key")],
                Unknown328 = [new MapMetaU16Ints(5, [1, 2])],
            },
        };

    private static byte[] Create(byte? tag) => Raw(writer =>
    {
        Setting().Write(writer);
        writer.WriteWideStringPrefixed("pw");
        if (tag is { } value)
        {
            writer.WriteByte(value);
            if (value == 0x3A)
                writer.WriteInt32(77);
        }
    });

    /// <summary>The optional tag has three accepted forms; nested settings survive table decoding unchanged.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData(0xBA)]
    [InlineData(0x3A)]
    public void CreateMatchesTheRawParserAndPreservesNestedSettings(int? marker)
    {
        var body = Create(marker is { } value ? (byte)value : null);
        var raw = Unknown402Request.Parse(body);
        var parsed = ModernRoomRequests.ReadCreate(Tables.Value, "kr-1256", body);
        Assert.Equal(raw.Password, parsed["password"]);
        if (raw.UnknownOpt is { } optional)
            Assert.Equal(optional, Assert.IsType<Dictionary<string, object?>>(parsed["unknown_n2"])["unknown_n0"]);
        else
            Assert.Equal(raw.OptTag, Assert.IsType<byte>(parsed["unknown_n2"]));
        Assert.Equal(Raw(Setting().Write), PacketCodec.Write(Tables.Value, "kr-1256", "SessionSetting",
            Assert.IsType<Dictionary<string, object?>>(parsed["session_setting"])));
        var roundtrip = PacketCodec.Write(Tables.Value, "kr-1256", "RoomCreateRequest", parsed);
        Assert.Equal(marker is null ? [.. body, (byte)0xBA] : body, roundtrip);
        Assert.Equal(345 + (marker is null ? 0 : marker == 0xBA ? 1 : 5), body.Length);
    }

    /// <summary>Appending the optional marker cannot repair any truncated field, including nested collection elements.</summary>
    [Fact]
    public void CreateRejectsEveryTruncatedPrefixExceptTheMissingFinalTag()
    {
        var body = Create(0x3A);
        var omittedLength = Create(null).Length;
        for (var length = 0; length < body.Length; length++)
        {
            if (length == omittedLength)
                continue;
            var prefix = body[..length];
            Assert.Throws<PacketFormatException>(() => ModernRoomRequests.ReadCreate(Tables.Value, "kr-1256", prefix));
        }
    }

    /// <summary>The create compatibility case permits neither another absent tag nor bytes after a complete body.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(0x39)]
    [InlineData(0xFF)]
    public void CreateRejectsUnknownTagsAndTrailingBytes(byte tag)
    {
        Assert.Throws<PacketFormatException>(() => ModernRoomRequests.ReadCreate(Tables.Value, "kr-1256", Create(tag)));
        Assert.Throws<PacketFormatException>(() => ModernRoomRequests.ReadCreate(Tables.Value, "kr-1256", [.. Create(0xBA), (byte)0]));
    }

    /// <summary>The same table-backed requests map the legacy wire fields into shared room messages.</summary>
    [Fact]
    public void JoinChatAndSeatLockMatchTheRawShapes()
    {
        var join = Raw(writer => { writer.WriteInt32(42); writer.WriteWideStringPrefixed("pw"); });
        Assert.Equal(12, join.Length);
        var rawJoin = Unknown403Request.Parse(join);
        var parsedJoin = ModernRoomRequests.ReadJoin(Tables.Value, "kr-1256", join);
        Assert.Equal((rawJoin.Unknown0, rawJoin.Password), (parsedJoin.RoomId, parsedJoin.Password));
        var chat = Raw(writer =>
        {
            writer.WriteInt32(3);
            writer.WriteWideStringPrefixed("hello");
            writer.WriteWideStringPrefixed("extra");
        });
        Assert.Equal(32, chat.Length);
        var rawChat = RoomChatRequest.Parse(chat);
        var parsedChat = ModernRoomRequests.ReadChat(Tables.Value, "kr-1256", chat);
        Assert.Equal((rawChat.Unknown0, rawChat.Unknown1), (parsedChat.Channel, parsedChat.Text));
        var locked = Raw(writer => { writer.WriteInt32(7); writer.WriteInt32(1); });
        Assert.Equal(8, locked.Length);
        var parsedLock = ModernRoomRequests.ReadSeatLock(Tables.Value, "kr-1256", locked);
        Assert.Equal((7, true), (parsedLock.Slot, parsedLock.Closed));
        foreach (var body in new[] { locked[..^1], locked.Concat(new byte[] { 0 }).ToArray(), Raw(writer => { writer.WriteInt32(7); writer.WriteInt32(2); }) })
            Assert.Throws<PacketFormatException>(() => ModernRoomRequests.ReadSeatLock(Tables.Value, "kr-1256", body));
        Assert.Throws<PacketFormatException>(() => ModernRoomRequests.ReadJoin(Tables.Value, "kr-1256", join[..^1]));
        Assert.Throws<PacketFormatException>(() => ModernRoomRequests.ReadChat(Tables.Value, "kr-1256", chat[..^1]));
    }

    /// <summary>Ready, shop, inventory and neutral UI reports preserve their individual states.</summary>
    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(1, 0, 0)]
    [InlineData(0, 1, 0)]
    [InlineData(0, 0, 1)]
    public void ReadyMatchesTheRawParser(int ready, int shop, int inventory)
    {
        var body = Raw(writer => { writer.WriteInt32(ready); writer.WriteInt32(shop); writer.WriteInt32(inventory); });
        Assert.Equal(12, body.Length);
        var raw = RoomStatusRequest.Parse(body);
        var parsed = ModernRoomRequests.ReadReady(Tables.Value, "kr-1256", body);
        Assert.Equal((raw.Ready, raw.InShop, raw.InInventory), (parsed.Ready, parsed.InShop, parsed.InInventory));
    }

    /// <summary>Noncanonical booleans and conflicting UI states fail before a typed request can be published.</summary>
    [Theory]
    [InlineData(2, 0, 0)]
    [InlineData(0, -1, 0)]
    [InlineData(1, 1, 0)]
    [InlineData(0, 1, 1)]
    [InlineData(1, 0, 1)]
    public void ReadyRejectsInvalidFlags(int ready, int shop, int inventory)
    {
        var body = Raw(writer => { writer.WriteInt32(ready); writer.WriteInt32(shop); writer.WriteInt32(inventory); });
        Assert.Throws<PacketFormatException>(() => ModernRoomRequests.ReadReady(Tables.Value, "kr-1256", body));
    }

    /// <summary>The seated start report is fully validated without requesting a match start.</summary>
    [Fact]
    public void StartConsumesTheCompleteNativeRuleRecord()
    {
        var body = Raw(writer =>
        {
            writer.WriteInt32(1);
            writer.WriteWideStringPrefixed("map");
            writer.WriteWideStringPrefixed("two");
            writer.WriteWideStringPrefixed("three");
            for (var i = 1; i <= 14; i++)
                writer.WriteInt32(i);
            writer.WriteByte(0x5A);
            writer.WriteInt32(99);
        });
        Assert.Equal(99, body.Length);
        var raw = CreateRoomRequest.Parse(body);
        var parsed = ModernRoomRequests.ReadStart(Tables.Value, "kr-1256", body);
        Assert.Equal(raw.Unknown1, parsed["map_key"]);
        Assert.Equal(raw.Unknown5, parsed["unknown_n19"]);
        for (var length = 0; length < body.Length; length++)
        {
            var prefix = body[..length];
            Assert.Throws<PacketFormatException>(() => ModernRoomRequests.ReadStart(Tables.Value, "kr-1256", prefix));
        }
        Assert.Throws<PacketFormatException>(() => ModernRoomRequests.ReadStart(Tables.Value, "kr-1256", [.. body, (byte)0]));
    }

    /// <summary>Strings and collection counts are bounded before allocating their decoded contents.</summary>
    [Fact]
    public void OversizedBodiesAndCountsAreRejected()
    {
        var join = Raw(writer => { writer.WriteInt32(42); writer.WriteUInt32(1025); });
        Assert.Throws<PacketFormatException>(() => ModernRoomRequests.ReadJoin(Tables.Value, "kr-1256", join));
        var create = Create(0xBA);
        // The title count follows the room id and session-state word in the audited SessionSetting table.
        BinaryPrimitives.WriteUInt32LittleEndian(create.AsSpan(6), uint.MaxValue);
        Assert.Throws<PacketFormatException>(() => ModernRoomRequests.ReadCreate(Tables.Value, "kr-1256", create));
        var excessivePairs = Raw(writer =>
        {
            (Setting() with { Map = Setting().Map with { Unknown297 = new MapMetaPair[4097] } }).Write(writer);
            writer.WriteWideStringPrefixed("pw");
            writer.WriteByte(0xBA);
        });
        Assert.Throws<PacketFormatException>(() => ModernRoomRequests.ReadCreate(Tables.Value, "kr-1256", excessivePairs));
        Assert.Throws<PacketFormatException>(() => ModernRoomRequests.ReadCreate(Tables.Value, "kr-1256", new byte[ushort.MaxValue + 1]));
    }

    /// <summary>Only the audited ordinary button form becomes a change of the authenticated sender's team.</summary>
    [Fact]
    public void TeamChangeUsesTheNativeAbsentTags()
    {
        var ordinary = PacketCodec.Write(Tables.Value, "kr-1256", "RoomTeamChangeRequest", new Dictionary<string, object?>());
        Assert.Equal(new byte[] { 0xBA, 0xBA }, ordinary);
        Assert.NotNull(ModernRoomRequests.ReadTeamChange(Tables.Value, "kr-1256", ordinary));
        Assert.Empty(PacketCodec.Write(Tables.Value, "kr-812", "RoomTeamChangeRequest", new Dictionary<string, object?>()));
        foreach (var body in new byte[][] { [], [0xBA], [0, 0xBA], [0xBA, 0], [0xBA, 0xBA, 0] })
            Assert.Throws<PacketFormatException>(() => ModernRoomRequests.ReadTeamChange(Tables.Value, "kr-1256", body));
    }

    /// <summary>Present blocks retain their exact shapes without granting authority over another seat.</summary>
    [Theory]
    [InlineData(true, false, 6)]
    [InlineData(false, true, 10)]
    [InlineData(true, true, 14)]
    public void TeamChangeQualifiersAreDecodedButNotActivated(bool first, bool second, int length)
    {
        var body = PacketCodec.Write(Tables.Value, "kr-1256", "RoomTeamChangeRequest", new Dictionary<string, object?>
        {
            ["unknown_n0"] = first ? new Dictionary<string, object?> { ["unknown_n0"] = 7 } : (object)(byte)0xBA,
            ["unknown_n1"] = second ? new Dictionary<string, object?> { ["unknown_n0"] = 8, ["unknown_n1"] = 9 } : (object)(byte)0xBA,
        });
        Assert.Equal(length, body.Length);
        Assert.Equal(body, PacketCodec.Write(Tables.Value, "kr-1256", "RoomTeamChangeRequest",
            PacketCodec.Read(Tables.Value, "kr-1256", "RoomTeamChangeRequest", body)));
        Assert.Throws<NotSupportedException>(() => ModernRoomRequests.ReadTeamChange(Tables.Value, "kr-1256", body));
        Assert.Throws<PacketFormatException>(() => ModernRoomRequests.ReadTeamChange(Tables.Value, "kr-1256", body[..^1]));
    }
}
