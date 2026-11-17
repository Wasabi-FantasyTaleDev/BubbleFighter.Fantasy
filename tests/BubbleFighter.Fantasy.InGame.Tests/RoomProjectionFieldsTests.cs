using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>Checks scoped native room-word allowances without broadening the existing projection gates.</summary>
public sealed class RoomProjectionFieldsTests
{
    /// <summary>The original six-key projection still clears every declared word.</summary>
    [Fact]
    public void SixKeyProjectionKeepsItsExistingClearingBehavior()
    {
        using var fixture = new ProjectionFixture();
        var values = Setting(1);
        RoomProjectionFields.Load(fixture.Tables, "kr-1256").Apply(values, 2);
        AssertProjection(values, 0);
    }

    /// <summary>Unsigned metadata compares against the signed bit patterns produced by PacketCodec.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(int.MinValue)]
    [InlineData(-1)]
    public void AllowedWordsRetainTheirExactUnsignedBits(int word)
    {
        using var fixture = new ProjectionFixture("{ builds: [kr-1256], fields: { word: [0, 1, 2147483648, 4294967295] } }");
        var values = Setting(word);
        RoomProjectionFields.Load(fixture.Tables, "kr-1256").Apply(values, 2);
        AssertProjection(values, word);
    }

    /// <summary>An explicit allowance does not activate in children that inherit its metadata.</summary>
    [Theory]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void InheritedAllowanceRequiresExplicitBuildMembership(string build)
    {
        using var fixture = new ProjectionFixture("{ builds: [kr-1256], fields: { word: [0, 1] } }");
        var values = Setting(1);
        RoomProjectionFields.Load(fixture.Tables, build).Apply(values, 2);
        AssertProjection(values, 0);
    }

    /// <summary>Children may replace their cleared words while the inherited exception validates its own named build.</summary>
    [Theory]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void InheritedAllowanceDoesNotRequireTheChildToClearTheSameWords(string build)
    {
        using var fixture = new ProjectionFixture("{ builds: [kr-1256], fields: { word: [0, 1] } }", replaceChildWords: true);
        var values = Setting(7);
        RoomProjectionFields.Load(fixture.Tables, build).Apply(values, 2);
        AssertProjection(values, 7, 23);
    }

    /// <summary>Malformed allowlists fail at load instead of silently preserving or dropping values.</summary>
    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{ fields: { word: [1] } }")]
    [InlineData("{ builds: [kr-1256] }")]
    [InlineData("{ builds: [kr-1256], fields: { word: [1] }, extra: true }")]
    [InlineData("{ builds: [], fields: { word: [1] } }")]
    [InlineData("{ builds: [missing], fields: { word: [1] } }")]
    [InlineData("{ builds: [kr-1256, kr-1256], fields: { word: [1] } }")]
    [InlineData("{ builds: [kr-1256], fields: {} }")]
    [InlineData("{ builds: [kr-1256], fields: [] }")]
    [InlineData("{ builds: [kr-1256], fields: { word: 1 } }")]
    [InlineData("{ builds: [kr-1256], fields: { word: [] } }")]
    [InlineData("{ builds: [kr-1256], fields: { word: [-1] } }")]
    [InlineData("{ builds: [kr-1256], fields: { word: ['+1'] } }")]
    [InlineData("{ builds: [kr-1256], fields: { word: [1.5] } }")]
    [InlineData("{ builds: [kr-1256], fields: { word: [4294967296] } }")]
    [InlineData("{ builds: [kr-1256], fields: { word: [true] } }")]
    [InlineData("{ builds: [kr-1256], fields: { word: [1, 1] } }")]
    [InlineData("{ builds: [kr-1256], fields: { missing: [1] } }")]
    [InlineData("{ builds: [kr-1256], fields: { mode: [1] } }")]
    [InlineData("{ builds: [kr-1256], fields: { referee: [1] } }")]
    [InlineData("{ builds: [kr-1256], fields: { map: [1] } }")]
    [InlineData("{ builds: [kr-1256], fields: { untouched: [1] } }")]
    public void InvalidWordAllowanceFailsAtLoad(string allowance)
    {
        using var fixture = new ProjectionFixture(allowance);
        var error = Assert.Throws<ConfigException>(() => RoomProjectionFields.Load(fixture.Tables, "kr-1256"));
        Assert.Contains("preserve_word_values", error.Message);
    }

    /// <summary>Being excluded does not conceal an invalid field in a named target build.</summary>
    [Fact]
    public void ExcludedBuildStillValidatesTheNamedTarget()
    {
        using var fixture = new ProjectionFixture("{ builds: [kr-1256], fields: { untouched: [1] } }");
        var error = Assert.Throws<ConfigException>(() => RoomProjectionFields.Load(fixture.Tables, "cn-774"));
        Assert.Contains("already-cleared u32", error.Message);
    }

    /// <summary>Unexpected projection keys remain errors when the optional allowance is present.</summary>
    [Fact]
    public void UnknownProjectionKeyFailsAtLoad()
    {
        using var fixture = new ProjectionFixture("{ builds: [kr-1256], fields: { word: [1] } }", extraKey: true);
        Assert.Throws<ConfigException>(() => RoomProjectionFields.Load(fixture.Tables, "kr-1256"));
    }

    private static Dictionary<string, object?> Setting(int word) => new()
    {
        ["mode"] = 1, ["map"] = "retained", ["flags"] = -1, ["word"] = word,
        ["other"] = 23, ["referee"] = true, ["untouched"] = 17,
    };

    private static void AssertProjection(Dictionary<string, object?> values, int word, int other = 0)
    {
        Assert.Equal(word, values["word"]);
        Assert.Equal(other, values["other"]);
        Assert.Equal(0, values["flags"]);
        Assert.Equal(false, values["referee"]);
        Assert.Equal(2u, values["mode"]);
        Assert.Equal("retained", values["map"]);
        Assert.Equal(17, values["untouched"]);
    }

    private sealed class ProjectionFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "bff-room-projection-" + Guid.NewGuid().ToString("N"));
        internal PacketTables Tables { get; }

        internal ProjectionFixture(string? allowance = null, bool replaceChildWords = false, bool extraKey = false)
        {
            Directory.CreateDirectory(_directory);
            try
            {
                var optional = allowance is null ? "" : $"      preserve_word_values: {allowance}\n";
                if (extraKey) optional += "      unsupported: true\n";
                var child = replaceChildWords ? "    room_projection_fields: { clear_words: [] }\n" : "";
                File.WriteAllText(Path.Combine(_directory, "builds.yaml"), """
                    version: 1
                    builds:
                      kr-1256:
                        room_projection_fields:
                          mode: mode
                          maps: [map]
                          flags: flags
                          clear_mask: 4294967295
                          clear_words: [word, other]
                          clear_booleans: [referee]
                    """ + "\n" + optional + "  cn-774:\n    base: kr-1256\n" + child + "  tw-454:\n    base: cn-774\n");
                File.WriteAllText(Path.Combine(_directory, "room.yaml"), """
                    version: 1
                    structs:
                      GameSetting:
                        - mode: u32
                        - map: wstr
                        - flags: u32
                        - word: u32
                        - other: u32
                        - referee: bool
                        - untouched: u32
                    """);
                Tables = PacketTables.Load(_directory);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}
