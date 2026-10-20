using System.Buffers.Binary;
using System.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.InGame.Wire;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>
/// The kr-1256 round-start layouts in <c>packets/</c> write the bytes of the hand-written kr-1256 start codecs for
/// the same values; those codecs were checked against the client readers and stay the oracle until they are deleted.
/// </summary>
public sealed class Kr1256RoundStartTableParityTests
{
    private const string Build = "kr-1256";
    private static readonly Lazy<PacketTables> Tables = new(() => PacketTables.Load(PacketsDirectory()));

    private static string PacketsDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "packets");
            if (File.Exists(Path.Combine(candidate, "builds.yaml")))
                return candidate;
        }
        throw new InvalidOperationException("The repository's packets directory was not found above the test output folder.");
    }

    private static Dictionary<string, object?> Values(params (string Name, object? Value)[] fields) =>
        fields.ToDictionary(field => field.Name, field => field.Value);

    private static Dictionary<string, object?> StageValues(EnterGameStageRecord record) => new()
    {
        ["map_key"] = record.Unknown0, ["stage_key"] = record.Unknown1, ["map_selection"] = record.Unknown2,
        ["room_state"] = record.Unknown3, ["unknown_76"] = record.Unknown4, ["unknown_80"] = record.Unknown5,
        ["unknown_84"] = record.Unknown6, ["unknown_88"] = record.Unknown7, ["unknown_92"] = record.Unknown8,
        ["unknown_96"] = record.Unknown9.Select(Entry).ToArray(),
        ["unknown_104"] = record.Unknown10.Select(triple => new Dictionary<string, object?>
        {
            ["unknown_n0"] = triple.Unknown0, ["unknown_n1"] = triple.Unknown1, ["unknown_n2"] = triple.Unknown2,
        }).ToArray(),
        ["unknown_112"] = record.Unknown11.Select(Entry).ToArray(),
    };

    private static Dictionary<string, object?> Entry(StartStageEntry entry) => new()
    {
        ["unknown_n0"] = entry.Unknown0, ["unknown_n1"] = entry.Unknown1, ["unknown_n2"] = entry.Unknown2,
    };

    [Fact]
    public void TheStageRecordTableWritesTheCodecsBytes()
    {
        foreach (var record in new[]
        {
            EnterGameStageRecord.ForMap("rel_Village01", 3),
            EnterGameStageRecord.ForMission("npc_Village07_Easy01", "rel_Village07_Easy02", 106),
            new EnterGameStageRecord("a", "bc", "", 1, -2, 3, 4, 5, 6,
                [new StartStageEntry(7, 8, 9), new StartStageEntry(0xFF, -1, 0)], [new StartStageTriple(1, 2, 3)],
                [new StartStageEntry(1, 0, -5)]),
        })
        {
            var expected = GameToClient.EnterGameStage(record);
            var written = PacketCodec.Write(Tables.Value, Build, "RoomEnterGame", StageValues(record));
            Assert.Equal(Convert.ToHexString(expected), Convert.ToHexString(written));
        }
        // Three empty strings, six scalars (three words and three bytes) and three empty lists.
        Assert.Equal(3 * 4 + 3 * 4 + 3 + 3 * 4,
            PacketCodec.Write(Tables.Value, Build, "RoomEnterGame", StageValues(EnterGameStageRecord.Default)).Length);
    }

    [Fact]
    public void TheRelayOfferTableWritesTheCodecsBytes()
    {
        var keyBytes = Enumerable.Range(1, RelaySessionKey.ByteLength).Select(value => (byte)value).ToArray();
        var address = IPAddress.Parse("192.0.2.17");
        var expected = GameToClient.ConnectRelay(address, 7101, RelaySessionKey.FromBytes(keyBytes));
        var written = PacketCodec.Write(Tables.Value, Build, "RelayConnectionOffer", new Dictionary<string, object?>
        {
            ["endpoint"] = new Dictionary<string, object?>
            {
                ["ipv4_word"] = BinaryPrimitives.ReadUInt32LittleEndian(address.GetAddressBytes()),
                ["port"] = (ushort)7101,
            },
            ["key"] = PacketCodec.ReadScalars(Tables.Value, Build, "RelaySessionKey", keyBytes),
        });
        Assert.Equal(26, written.Length);
        Assert.Equal(Convert.ToHexString(expected), Convert.ToHexString(written));
    }

    [Fact]
    public void TheCountdownAndStartTablesWriteTheCodecsBytes()
    {
        Assert.Equal(Convert.ToHexString(GameToClient.StartFlags(1, 0, 1)), Convert.ToHexString(PacketCodec.Write(Tables.Value,
            Build, "RoundEverybodyLoadingComplete", Values(("unknown_4", true), ("unknown_5", false), ("unknown_6", true)))));
        Assert.Equal(Convert.ToHexString(GameToClient.StartTimer(7000)), Convert.ToHexString(PacketCodec.Write(Tables.Value,
            Build, "RoundCountdown", Values(("milliseconds", 7000)))));
        var start = PacketCodec.Write(Tables.Value, Build, "RoundStartGame", Values(("unknown_4", 240000), ("unknown_8", 0)));
        Assert.Equal(8, start.Length);
        Assert.Equal(Convert.ToHexString(GameToClient.RoundInitialize(240000, 0)), Convert.ToHexString(start));
    }

    [Fact]
    public void TheObjectAddTableIsTheOneWordTheClientWrites()
    {
        // kr-1256 sub_6DDE20 and kr-812 sub_50EE70 write one word, the character's scene object.
        foreach (var build in new[] { Build, "kr-812" })
        {
            var body = PacketCodec.Write(Tables.Value, build, "RoundObjectAddRequest", Values(("object_id", 1100000)));
            Assert.Equal("E0C81000", Convert.ToHexString(body));
            Assert.Equal(1100000, Convert.ToInt32(PacketCodec.ReadScalars(Tables.Value, build, "RoundObjectAddRequest", body)["object_id"]));
            Assert.Throws<PacketFormatException>(() => PacketCodec.ReadScalars(Tables.Value, build, "RoundObjectAddRequest", body.AsSpan(0, 3)));
        }
    }

    [Fact]
    public void TheRelayJoinTableReadsTheKeyOfTheModernJoin()
    {
        // kr-1256 sub_95FD30: the 20-byte key, a byte, a word, a word and a counted UTF-16 string.
        var keyBytes = Enumerable.Range(40, RelaySessionKey.ByteLength).Select(value => (byte)value).ToArray();
        var w = PacketWriter.Create();
        w.WriteBytes(keyBytes);
        w.WriteByte(0);
        w.WriteInt32(5);
        w.WriteInt32(12345);
        w.WriteWideStringPrefixed("player");
        var body = ((System.Buffers.ArrayBufferWriter<byte>)w.Buffer).WrittenSpan.ToArray();
        Assert.Equal(20 + 1 + 4 + 4 + 4 + 12, body.Length);
        var join = PacketCodec.Read(Tables.Value, Build, "RelayJoinRequest", body);
        var key = PacketCodec.Write(Tables.Value, Build, "RelaySessionKey", (IReadOnlyDictionary<string, object?>)join["key"]!);
        Assert.Equal(keyBytes, key);
        Assert.Equal(12345, Convert.ToInt32(join["unknown_n3"]));
        Assert.Equal("player", join["unknown_n4"]);
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(Tables.Value, Build, "RelayJoinRequest", body.AsSpan(0, body.Length - 1)));
    }
}
