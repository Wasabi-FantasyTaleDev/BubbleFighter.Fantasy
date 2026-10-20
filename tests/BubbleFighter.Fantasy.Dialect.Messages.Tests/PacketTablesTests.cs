using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Tests for reading the packet tables and for turning them into the room writer's field tables.</summary>
public sealed class PacketTablesTests : IDisposable
{
    private const string Builds = """
        version: 1
        builds:
          base: {}
          older: {base: base}
          later: {base: older}
          other: {base: base}
        """;

    private const string Structs = """
        version: 1
        structs:
          Seat:
            - occupied: bool
              menu: 1
            - user_number: u32
            - open_seats: u16
              except: [older]
              also: [later]
            - clan: wstr
              older:
                - unknown_180: u32
                  offset: 180
                  default: 1000
            - unknown_212: u32
              default: 7
              vacant: 0
              only: [later]
            - level: u32
              vacant: 1
              menu: 8
          Seat@other:
            - user_number: u32
            - unknown_9: u8
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "bff-packets-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private PacketTables Load(string structs = Structs, string builds = Builds)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "builds.yaml"), builds);
        File.WriteAllText(Path.Combine(_root, "seat.yaml"), structs);
        return PacketTables.Load(_root);
    }

    private static string[] Names(PacketTables tables, string build) => [.. tables.Resolve(build, "Seat").Select(field => field.Name)];

    [Fact]
    public void ABuildFollowsItsBaseExceptWhereARowNamesItOrABuildItIsBasedOn()
    {
        var tables = Load();

        Assert.Equal(["base", "older", "other", "later"], tables.Builds);
        Assert.Equal(["occupied", "user_number", "open_seats", "clan", "level"], Names(tables, "base"));
        Assert.Equal(["occupied", "user_number", "unknown_180", "level"], Names(tables, "older"));
        // The nearest build of the lineage a row mentions decides: `later` has the seats again and its
        // own field, and inherits what the row says about `older`.
        Assert.Equal(["occupied", "user_number", "open_seats", "unknown_180", "unknown_212", "level"], Names(tables, "later"));
        // A restated structure stands alone.
        Assert.Equal(["user_number", "unknown_9"], Names(tables, "other"));

        var later = tables.Resolve("later", "Seat");
        Assert.Equal(new PacketField("unknown_180", "u32", 1000, null, null, "older", 180), later[3]);
        Assert.Equal(new PacketField("unknown_212", "u32", 7, 0, null, "later"), later[4]);
        Assert.Equal(new PacketField("level", "u32", 0, 1, "8", "base"), later[5]);
        Assert.Equal(["later", "older", "base"], tables.LineageOf("later"));
        Assert.Contains("unknown_180", tables.Show("older", "Seat"));
        Assert.Contains("only older", tables.Diff("base", "older", "Seat"));
        Assert.Contains("only base", tables.Diff("base", "older", "Seat"));
    }

    [Fact]
    public void FixedStructureRunsHaveNoCountPrefixAndRequireTheExactNumberOfRows()
    {
        var tables = Load("""
            version: 1
            structs:
              Envelope:
                - rows: "Row[2]"
              Row:
                - slot: u32
                  default: -1
                - active: bool
            """);
        Assert.Equal("FFFFFFFF00000000FFFFFFFF00000000", Convert.ToHexString(
            PacketCodec.Write(tables, "base", "Envelope", new Dictionary<string, object?>())));
        var rows = new[]
        {
            new Dictionary<string, object?> { ["slot"] = 7, ["active"] = true },
            new Dictionary<string, object?> { ["slot"] = 3 },
        };
        Assert.Equal("07000000010000000300000000000000", Convert.ToHexString(
            PacketCodec.Write(tables, "base", "Envelope", new Dictionary<string, object?> { ["rows"] = rows })));
        Assert.Throws<ArgumentException>(() => PacketCodec.Write(tables, "base", "Envelope",
            new Dictionary<string, object?> { ["rows"] = rows[..1] }));
        Assert.Throws<ArgumentException>(() => PacketCodec.Write(tables, "base", "Envelope",
            new Dictionary<string, object?> { ["rows"] = new[] { 1, 2 } }));
        Assert.False(PacketTables.TryParseRun("Row[65536]", out _, out _));
    }

    [Fact]
    public void ListValuesReplaceTheBasesWholeList()
    {
        var tables = Load(builds: """
            version: 1
            builds:
              base: {lobby: {types: [1, 2], items: [7, 8, 9]}}
              older: {base: base, lobby: {items: [7]}}
            """, structs: "version: 1\nstructs: {Seat: [{user_number: u32}]}\n");

        var lobby = (Dictionary<string, object?>)tables.ValuesOf("older")["lobby"]!;
        Assert.Equal(["1", "2"], (string[])lobby["types"]!);
        Assert.Equal(["7"], (string[])lobby["items"]!);
    }

    [Fact]
    public void LobbyValuesFollowEachBuildsBase()
    {
        string[] Lobby(string build, string key) =>
            (string[])((Dictionary<string, object?>)TestBuilds.Packets.ValuesOf(build)["lobby"]!)[key]!;

        // cn-406 and cn-404 take id-400's Indonesian pair; cn-279 takes cn-324's 2012 starter bag.
        Assert.Equal(["11", "12"], Lobby("cn-406", "creation_character_types"));
        Assert.Equal(["11", "12"], Lobby("cn-404", "creation_character_types"));
        Assert.Equal(Lobby("cn-324", "starter_item_ids"), Lobby("cn-279", "starter_item_ids"));
        Assert.Equal(["1", "2"], Lobby("cn-717", "creation_character_types"));
        Assert.False(TestBuilds.Packets.CapabilityOf("cn-404", "character_creation"));
    }

    [Fact]
    public void BuildValuesLieOverTheBases()
    {
        var tables = Load(builds: """
            version: 1
            builds:
              base: {play: {wire: {a: 1, b: 2}, host_start: true}}
              older: {base: base, play: {wire: {b: 3}}}
            """, structs: "version: 1\nstructs: {Seat: [{user_number: u32}]}\n");

        var play = (Dictionary<string, object?>)tables.ValuesOf("older")["play"]!;
        Assert.Equal("true", play["host_start"]);
        Assert.Equal(new Dictionary<string, object?> { ["a"] = "1", ["b"] = "3" }, (Dictionary<string, object?>)play["wire"]!);
        Assert.Equal(1, tables.StatedValues("older"));
    }

    [Theory]
    [InlineData("structs: {Seat: [{user_number: u32}]}\n")]
    [InlineData("version: 2\nstructs: {Seat: [{user_number: u32}]}\n")]
    [InlineData("version: 1\ncolour: red\n")]
    [InlineData("version: 1\nstructs: {Seat: [{user_number: u33}]}\n")]
    [InlineData("version: 1\nstructs: {Seat: [{user_number: u32, except: [nobody]}]}\n")]
    [InlineData("version: 1\nstructs: {Seat: [{user_number: u32, nobody: [{level: u32}]}]}\n")]
    [InlineData("version: 1\nstructs: {Seat: [{user_number: u32}, {user_number: u16}]}\n")]
    [InlineData("version: 1\nstructs: {Seat: [{UserNumber: u32}]}\n")]
    [InlineData("version: 1\nstructs: {Seat: [{user_number: u32, default: many}]}\n")]
    [InlineData("version: 1\nstructs: {Seat: [{user_number: \"u32 = 1\"}]}\n")]
    [InlineData("version: 1\nstructs: {Seat: [{user number: u32}]}\n")]
    [InlineData("version: 1\nstructs: {Seat@nobody: [{user_number: u32}]}\n")]
    [InlineData("version: 1\nstructs: {Seat@older: [{user_number: u32}]}\n")]
    public void AMalformedStructureFileFailsLoudly(string structs) =>
        Assert.Throws<ConfigException>(() => Load(structs));

    [Theory]
    [InlineData("version: 1\nbuilds: {base: {}, older: {base: nowhere}}\n")]
    [InlineData("version: 1\nbuilds: {base: {}, other: {}}\n")]
    [InlineData("version: 1\nbuilds: {a: {base: b}, b: {base: a}}\n")]
    [InlineData("builds: {base: {}}\n")]
    public void AMalformedBuildsFileFailsLoudly(string builds) =>
        Assert.Throws<ConfigException>(() => Load("version: 1\nstructs: {Seat: [{user_number: u32}]}\n", builds));

    [Fact]
    public void EveryBuildOfTheRepositoryResolvesAndTheBaselineStatesEverything()
    {
        var builds = TestBuilds.All;

        Assert.Equal("kr-812: the baseline", Assert.Single(builds.Summary, line => line.StartsWith("kr-812", StringComparison.Ordinal)));
        foreach (var build in new[] { "kr-812", "id-400", "id-1809", "cn-717", "cn-735", "cn-324", "cn-279", "cn-706" })
        {
            var schema = builds.Of(build).Schema;
            // One layout per structure: the menu and play tables of a build differ in values, never in shape.
            Assert.Equal(schema.UserSlot.Length, schema.PlayUserSlot!.Length);
            Assert.Equal(schema.SessionSetting.Length, schema.PlaySessionSetting!.Length);
            Assert.All(schema.PlayUserSlot, field => Assert.NotEqual("", field.Name));
        }
    }

    [Fact]
    public void AKnownNameIsFilledFromTheRoomAndTheMenuCarriesItsOwnValues()
    {
        var (schema, _) = TestBuilds.Of("kr-812");

        var play = schema.PlayUserSlot!;
        Assert.Equal(new RoomField(RoomFieldType.Bool, RoomFieldValue.Occupied, Name: "occupied"), play[0]);
        Assert.Equal(new RoomField(RoomFieldType.Bool, RoomFieldValue.Occupied, Name: "seated"), play[2]);
        Assert.Equal(new RoomField(RoomFieldType.U32, RoomFieldValue.Level, Vacant: 1, Name: "level"), play[9]);
        // The main-menu seat writes the play-only values as the row's menu constants.
        Assert.Equal(new RoomField(RoomFieldType.Bool, Constant: 1, Name: "occupied"), schema.UserSlot[0]);
        Assert.Equal(new RoomField(RoomFieldType.U32, Constant: 8, Name: "level"), schema.UserSlot[9]);
        Assert.Equal(new RoomField(RoomFieldType.U32, RoomFieldValue.UserNumber, Name: "user_number"), schema.UserSlot[4]);
        // cn-717's menu seat carries the character type in the team byte.
        Assert.Equal(new RoomField(RoomFieldType.U8, RoomFieldValue.CharacterType, Name: "team"), TestBuilds.Of("cn-717").Schema.UserSlot[8]);
    }
}
