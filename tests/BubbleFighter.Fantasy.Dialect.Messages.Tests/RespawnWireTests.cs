using BubbleFighter.Fantasy.Dialect.Rooms;
using Xunit;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>
/// Pins the kr-812 respawn wire bodies: the index proposal the flow answers a dead client with, and
/// the flagged revive packet shown to the whole round.
/// </summary>
public sealed class RespawnWireTests
{
    [Fact]
    public void RespawnIndexRequestIsTwelveBytesHandleIndexAndASetFlag()
    {
        var body = PlayRoomCodec.WriteRespawnIndexRequest(TestBuilds.Packets, "kr-812", 1100000, 1);

        Assert.Equal(12, body.Length);
        Assert.Equal(1100000, BitConverter.ToInt32(body, 0));
        Assert.Equal(1, BitConverter.ToInt32(body, 4));
        Assert.Equal(1, body[8]);
        Assert.Equal(0, body[9]);
        Assert.Equal(0, body[10]);
        Assert.Equal(0, body[11]);
    }

    [Fact]
    public void RespawnIsSixteenBytesWithTheReviveFlagSet()
    {
        var body = PlayRoomCodec.WriteRespawn(TestBuilds.Packets, "kr-812", 1100000, 1);

        Assert.Equal(16, body.Length);
        Assert.Equal(1100000, BitConverter.ToInt32(body, 0));
        Assert.Equal(1, BitConverter.ToInt32(body, 4));
        Assert.Equal(1, body[8]);
        Assert.Equal(0, body[9]);
        Assert.Equal(0, body[10]);
        Assert.Equal(0, body[11]);
        Assert.Equal(0, BitConverter.ToInt32(body, 12));
    }

    [Fact]
    public void TheCompactRespawnPacketsAreTwelveBytesWithoutAFourthField()
    {
        foreach (var body in new[]
                 {
                     PlayRoomCodec.WriteRespawn(TestBuilds.Packets, "cn-324", 1100000, 1),
                     PlayRoomCodec.WriteRespawnIndexRequest(TestBuilds.Packets, "cn-324", 1100000, 1),
                 })
        {
            Assert.Equal(12, body.Length);
            Assert.Equal(1100000, BitConverter.ToInt32(body, 0));
            Assert.Equal(1, BitConverter.ToInt32(body, 4));
            Assert.Equal(1, body[8]);
        }
    }

    [Fact]
    public void ExtendRespawnIsTheHandleAndASignedNumberOfMilliseconds()
    {
        var body = PlayRoomCodec.WriteExtendRespawn(TestBuilds.Packets, "kr-812", 1100000, -2000);

        Assert.Equal(8, body.Length);
        Assert.Equal(1100000, BitConverter.ToInt32(body, 0));
        Assert.Equal(-2000, BitConverter.ToInt32(body, 4));
    }

    [Theory]
    [InlineData("kr-812", 16, 12)]
    [InlineData("cn-717", 16, 12)]
    [InlineData("cn-324", 12, 12)]
    [InlineData("cn-279", 8, 8)]
    [InlineData("id-400", 16, 12)]
    [InlineData("id-1809", 16, 12)]
    [InlineData("cn-706", 16, 12)]
    [InlineData("cn-406", 12, 12)]
    public void NativeSpawnBodiesMatchEachBuildsOwnReader(string build, int confirmationBytes, int acceptanceBytes)
    {
        // CN2011 sends only actor/index (cn-279 sub_4040E0); its offer still has the request flag.
        const int actor = unchecked((int)0x80000001);
        const int index = -1;
        var prefix = new byte[] { 1, 0, 0, 128, 255, 255, 255, 255 };
        var confirmation = PlayRoomCodec.WriteRespawn(TestBuilds.Packets, build, actor, index);
        var expected = new byte[confirmationBytes];
        prefix.CopyTo(expected, 0);
        if (confirmationBytes > 8)
            expected[8] = 1;
        Assert.Equal(expected, confirmation);
        Assert.Equal(prefix.Concat(new byte[] { 1, 0, 0, 0 }).ToArray(),
            PlayRoomCodec.WriteRespawnIndexRequest(TestBuilds.Packets, build, actor, index));

        var acceptance = new byte[acceptanceBytes];
        prefix.CopyTo(acceptance, 0);
        if (acceptanceBytes > 8)
            acceptance[8] = 1;
        var parsed = PlayRoomCodec.ReadSpawnAcceptance(TestBuilds.Packets, build, acceptance);
        Assert.Equal(actor, parsed.Actor);
        Assert.Equal(index, parsed.SpawnIndex);
        for (var length = 0; length < acceptance.Length; length++)
        {
            var truncated = acceptance[..length];
            Assert.Throws<PacketFormatException>(() =>
                PlayRoomCodec.ReadSpawnAcceptance(TestBuilds.Packets, build, truncated));
        }
        Assert.Throws<PacketFormatException>(() => PlayRoomCodec.ReadSpawnAcceptance(
            TestBuilds.Packets, build, acceptance.Concat(new byte[] { 0 }).ToArray()));
    }

    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-717")]
    [InlineData("cn-324")]
    [InlineData("cn-279")]
    [InlineData("id-400")]
    [InlineData("id-1809")]
    [InlineData("cn-706")]
    public void NativeRespawnPromptAndWaitChangePreserveSignedBits(string build)
    {
        Assert.Equal(new byte[] { 255, 255, 255, 255 },
            PlayRoomCodec.WriteRespawnPrompt(TestBuilds.Packets, build, -1));
        Assert.Equal(new byte[] { 255, 255, 255, 255, 48, 248, 255, 255 },
            PlayRoomCodec.WriteExtendRespawn(TestBuilds.Packets, build, -1, -2000));
    }

    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-717")]
    [InlineData("cn-324")]
    [InlineData("cn-279")]
    [InlineData("id-400")]
    [InlineData("id-1809")]
    [InlineData("cn-706")]
    [InlineData("cn-406")]
    public void DeathAndIndexRequestsRequireExactlyTwoWords(string build)
    {
        var body = new byte[] { 255, 255, 255, 255, 128, 12, 0, 0 };
        foreach (var (structure, secondField) in new[]
                 {
                     ("RoundDeathReport", "delay_ms"),
                     ("RoundSpawnIndexRequest", "unknown_8"),
                 })
        {
            var fields = PacketCodec.ReadScalars(TestBuilds.Packets, build, structure, body);
            Assert.Equal(-1, (int)fields["actor"]);
            Assert.Equal(3200, (int)fields[secondField]);
            for (var length = 0; length < body.Length; length++)
            {
                var truncated = body[..length];
                Assert.Throws<PacketFormatException>(() =>
                    PacketCodec.ReadScalars(TestBuilds.Packets, build, structure, truncated));
            }
            Assert.Throws<PacketFormatException>(() => PacketCodec.ReadScalars(
                TestBuilds.Packets, build, structure, body.Concat(new byte[] { 0 }).ToArray()));
        }
    }

    private static byte[] MapBlock(int mode, string display, string currentMap, string theme, string mapKey)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>(256);
        var w = new BubbleFighter.Fantasy.Abstractions.Protocol.PacketWriter(buffer);
        w.WriteInt32(mode);
        w.WriteInt32(0);
        w.WriteWideStringPrefixed(display);
        w.WriteWideStringPrefixed(currentMap);
        w.WriteWideStringPrefixed(theme);
        w.WriteWideStringPrefixed(mapKey);
        for (var i = 0; i < 5; i++)
            w.WriteInt32(7);
        w.WriteBool32(false);
        w.WriteBool32(true);
        for (var i = 0; i < 6; i++)
            w.WriteInt32(9);
        return buffer.WrittenSpan.ToArray();
    }

    [Theory]
    [InlineData("cn-324")]
    [InlineData("cn-279")]
    public void ARoomWithoutAMapKeyIsShownAsTheAllRandomEntry(string build)
    {
        // A create request carries the startup configuration's map in the first two strings and nothing in
        // the theme and the key (cn-324 sub_582C00).
        var schema = TestBuilds.Of(build).Schema;
        var shown = PlazaRoomWire.PresentUnchosenMap(schema, MapBlock(1, "Byway", "rel_Village01_Byway", "", ""), "Random", "All");

        Assert.Equal(MapBlock(1, "Random", "Random", "All", "Random"), shown.ToArray());
    }

    [Fact]
    public void AChosenMapAndAnAllRandomEntryAreKept()
    {
        var schema = TestBuilds.Of("cn-324").Schema;
        var chosen = MapBlock(1, "Byway", "rel_Village01_Byway", "Village", "rel_Village01_Byway");
        var allRandom = MapBlock(1, "Random", "Random", "All", "Random");

        Assert.Equal(chosen, PlazaRoomWire.PresentUnchosenMap(schema, chosen, "Random", "All").ToArray());
        Assert.Equal(allRandom, PlazaRoomWire.PresentUnchosenMap(schema, allRandom, "Random", "All").ToArray());
    }

    [Fact]
    public void PirateRoomsWithoutAChosenMapUseTheNativeRandomEntry()
    {
        var schema = TestBuilds.Of("cn-324").Schema;
        var keyed = MapBlock(101, "Byway", "rel_Village01_Byway", "", "");

        Assert.Equal(MapBlock(101, "Random", "Random", "All", "Random"), PlazaRoomWire.PresentUnchosenMap(schema, keyed, "Random", "All").ToArray());
        Assert.Equal(keyed, PlazaRoomWire.PresentUnchosenMap(schema, keyed, "", "").ToArray());
        Assert.Equal("Random", TestBuilds.Of("cn-324").Play.Wire.UnchosenMapKey);
        Assert.Equal("Random", TestBuilds.Of("kr-812").Play.Wire.UnchosenMapKey);
    }

    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-717")]
    [InlineData("id-400")]
    [InlineData("id-1809")]
    [InlineData("cn-706")]
    public void ACreateRequestsStartupMapIsShownAsTheAllRandomEntry(string build)
    {
        // Every create copies Config/Lambient.xml's startup map (kr-812 sub_480700): the current map
        // carries rel_Village01_Byway and the key with an empty theme.
        var (schema, play) = TestBuilds.Of(build);
        var values = new RoomValues(RoomCodec.PlayRoomKind, [])
        {
            Play = true, Mode = 1, MapDirectory = "Byway", CurrentMap = "rel_Village01_Byway",
            MapName = "rel_Village01_Byway", Difficulty = 2,
        };
        var created = RoomCodec.WriteGameSettingChange(schema, values);
        var expected = RoomCodec.WriteGameSettingChange(schema,
            values with { MapDirectory = "Random", CurrentMap = "Random", NpcTableKey = "All", MapName = "Random" });

        Assert.Equal(expected, Present(schema, play, created, createRequest: true));
        Assert.Equal(expected, Present(schema, play, expected, createRequest: true));
        // The same map in a block the master changed is the master's choice and stays.
        Assert.Equal(created, Present(schema, play, created, createRequest: false));
    }

    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-717")]
    [InlineData("id-400")]
    [InlineData("id-1809")]
    [InlineData("cn-706")]
    public void APirateCreateShowsTheFirstDifficultysRandomEntry(string build)
    {
        // A mode-101 create is answered with Random_Hot_Dif1 in the three map strings and All as the theme.
        var (schema, play) = TestBuilds.Of(build);
        var values = new RoomValues(RoomCodec.PlayRoomKind, [])
        {
            Play = true, Mode = 101, MapDirectory = "Byway", CurrentMap = "rel_Village01_Byway",
            MapName = "rel_Village01_Byway",
        };
        var created = RoomCodec.WriteGameSettingChange(schema, values);
        var expected = RoomCodec.WriteGameSettingChange(schema, values with
        {
            MapDirectory = "Random_Hot_Dif1", CurrentMap = "Random_Hot_Dif1", NpcTableKey = "All", MapName = "Random_Hot_Dif1",
        });

        Assert.Equal(101, play.Wire.UnchosenDifficultyMode);
        Assert.Equal(expected, Present(schema, play, created, createRequest: true));
    }

    [Theory]
    [InlineData("cn-324")]
    [InlineData("cn-279")]
    [InlineData("cn-406")]
    public void BuildsWithoutDifficultyMarkersShowThePlainRandomEntryForPirateRooms(string build)
    {
        var (schema, play) = TestBuilds.Of(build);

        Assert.Equal("", play.Wire.UnchosenDifficultyMapKey);
        if (build != "cn-406")
            Assert.Equal(MapBlock(101, "Random", "Random", "All", "Random"),
                Present(schema, play, MapBlock(101, "Byway", "rel_Village01_Byway", "", ""), createRequest: true));
    }

    private static byte[] Present(RoomSchema schema, PlayValues play, byte[] block, bool createRequest) =>
        PlazaRoomWire.PresentUnchosenMap(schema, block, play.Wire.UnchosenMapKey, play.Wire.UnchosenMapTheme,
            createRequest, play.Wire.UnchosenDifficultyMode, play.Wire.UnchosenDifficultyMapKey).ToArray();
}
