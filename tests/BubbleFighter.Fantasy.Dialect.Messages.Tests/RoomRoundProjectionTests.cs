using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins the native selectors' string destinations independently of their request layout.</summary>
public sealed class RoomRoundProjectionTests
{
    /// <summary>Unchanged flags and opaque words retain their raw bits when string lengths change.</summary>
    [Fact]
    public void EncodedProjectionPreservesNoncanonicalTrueAndOpaqueWords()
    {
        var tables = TestBuilds.Packets;
        var original = PacketCodec.Write(tables, "kr-812", "GameSetting", new Dictionary<string, object?>
        {
            ["unknown_12"] = unchecked((int)0xFEDCBA98),
        });
        // Four empty strings and five words put the baseline's first bool32 at wire +44.
        BitConverter.GetBytes(0x12345678).CopyTo(original, 44);
        var projection = Assert.IsType<RoomRoundSettingFields>(RoomRoundSettingFields.Load(tables, "kr-812"));
        var changed = projection.Apply(original, 2, "accompanying", "map\u03A9");
        Assert.Equal(0x12345678, BitConverter.ToInt32(changed, 44));
        var decoded = PacketCodec.Read(tables, "kr-812", "GameSetting", changed);
        Assert.Equal("accompanying", decoded["unknown_396"]);
        Assert.Equal("map\u03A9", decoded["unknown_312"]);
        Assert.Equal(original, projection.Apply(changed, 2, "", ""));
        Assert.Throws<PacketFormatException>(() => projection.Apply(original[..^1], 2, "a", "b"));
        Assert.Throws<PacketFormatException>(() => projection.Apply([.. original, 0], 2, "a", "b"));
        Assert.Throws<ArgumentOutOfRangeException>(() => projection.Apply(original, 2, new string('a', ushort.MaxValue), "b"));
        Assert.Throws<BubbleFighter.Fantasy.Abstractions.ConfigException>(() =>
            PacketCodec.ReplaceStrings(tables, "kr-812", "GameSetting", original,
                new Dictionary<string, string> { ["mode"] = "not a string field" }));
    }

    /// <summary>The baseline groups strings while the modern baseline interleaves each pair.</summary>
    [Theory]
    [InlineData("kr-812", 0, "unknown_340", "unknown_256")]
    [InlineData("kr-812", 1, "unknown_368", "unknown_284")]
    [InlineData("kr-812", 2, "unknown_396", "unknown_312")]
    [InlineData("kr-1256", 0, "unknown_168", "unknown_144")]
    [InlineData("kr-1256", 1, "unknown_216", "unknown_192")]
    [InlineData("kr-1256", 2, "unknown_264", "unknown_240")]
    [InlineData("cn-774", 0, "unknown_176", "unknown_200")]
    [InlineData("cn-774", 1, "unknown_224", "unknown_248")]
    [InlineData("cn-774", 2, "unknown_272", "unknown_296")]
    [InlineData("tw-454", 0, "unknown_160", "unknown_184")]
    [InlineData("tw-454", 1, "unknown_208", "unknown_232")]
    [InlineData("tw-454", 2, "unknown_256", "unknown_280")]
    public void EachSelectorChangesOnlyItsAuditedPair(string build, int index, string first, string second)
    {
        var tables = TestBuilds.Packets;
        var projection = Assert.IsType<RoomRoundSettingFields>(RoomRoundSettingFields.Load(tables, build));
        var original = PacketCodec.Read(tables, build, "GameSetting",
            PacketCodec.Write(tables, build, "GameSetting", new Dictionary<string, object?>()));
        var setting = new Dictionary<string, object?>(original);
        projection.Apply(setting, index, "A\u03A9", "map");
        var encoded = PacketCodec.Write(tables, build, "GameSetting", setting);
        var decoded = PacketCodec.Read(tables, build, "GameSetting", encoded);
        Assert.Equal("A\u03A9", decoded[first]);
        Assert.Equal("map", decoded[second]);
        setting[first] = original[first];
        setting[second] = original[second];
        Assert.Equal(PacketCodec.Write(tables, build, "GameSetting", original),
            PacketCodec.Write(tables, build, "GameSetting", setting));
    }

    /// <summary>The complete request rejects bad controls, truncation and extra bytes before a shared mutation.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void RequestAdmissionPinsLengthAndRejectsOutOfRangeIndices(string build)
    {
        var projection = Assert.IsType<RoomRoundSettingFields>(RoomRoundSettingFields.Load(TestBuilds.Packets, build));
        var body = Convert.FromHexString("0200000001000000410001000000A903");
        Assert.Equal(16, body.Length);
        Assert.Equal((2, "A", "\u03A9"), projection.Read(body));
        for (var length = 0; length < body.Length; length++)
        {
            var truncated = body[..length];
            Assert.Throws<PacketFormatException>(() => projection.Read(truncated));
        }
        Assert.Throws<PacketFormatException>(() => projection.Read([.. body, 0]));
        foreach (var invalid in new[] { 3, -1 })
        {
            BitConverter.GetBytes(invalid).CopyTo(body, 0);
            Assert.Throws<PacketFormatException>(() => projection.Read(body));
        }
    }

    /// <summary>Each native selector replaces only its audited pair in an encoded retained setting.</summary>
    [Theory]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void NativeEncodedProjectionPreservesOtherSelectors(string build)
    {
        var tables = TestBuilds.Packets;
        var projection = Assert.IsType<RoomRoundSettingFields>(RoomRoundSettingFields.Load(tables, build));
        var original = PacketCodec.Write(tables, build, "GameSetting", new Dictionary<string, object?>());
        for (var index = 0; index < 3; index++)
        {
            var changed = projection.Apply(original, index, "selected-map", "native-text");
            Assert.NotEqual(original, changed);
            Assert.Equal(original, projection.Apply(changed, index, "", ""));
        }
    }
}
