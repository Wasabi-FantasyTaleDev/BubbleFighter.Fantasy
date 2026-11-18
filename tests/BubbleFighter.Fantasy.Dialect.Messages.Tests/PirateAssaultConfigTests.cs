using System.Security.Cryptography;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins the pirate configuration's regional key widths and counted map records without enabling a new category.</summary>
public sealed class PirateAssaultConfigTests
{
    /// <summary>Rewriting the map record as a collection preserves every byte of the published KR generation.</summary>
    [Fact]
    public void KoreanGenerationKeepsItsOriginalInflatedBytes()
    {
        var tables = TestBuilds.Packets;
        var values = DynamicConfigContent.PublishedSlot(tables, "kr-1256", 8, "PirateAssaultContext");
        Assert.NotNull(values);
        var data = PacketCodec.Write(tables, "kr-1256", "PirateAssaultContext", values);
        Assert.Equal(620, data.Length);
        Assert.Equal("1A7D8C0595E9B04828B9A66126F5718BBFB56182695AB18519CB12586FE667B9", Convert.ToHexString(SHA256.HashData(data)));
    }

    /// <summary>The KR key takes two bytes; CN and TW take four, and all three decoders must consume both map rows.</summary>
    [Theory]
    [InlineData("kr-1256", "3412")]
    [InlineData("cn-774", "34120000")]
    [InlineData("tw-454", "34120000")]
    public void KeyWidthAndSecondMapRecordRoundTrip(string build, string key)
    {
        var bytes = Convert.FromHexString("01000000" + key + "00000000" + "02000000"
            + "0100000001000000410003000000" + "02000000020000004200430004000000");
        var tables = TestBuilds.Packets;
        var decoded = PacketCodec.Read(tables, build, "PirateAssaultContext", bytes);
        var maps = Assert.IsType<object?[]>(decoded["bindings"]);
        Assert.Equal(2, maps.Length);
        Assert.Equal("BC", Assert.IsType<Dictionary<string, object?>>(maps[1])["map_name"]);
        Assert.Equal(bytes, PacketCodec.Write(tables, build, "PirateAssaultContext", decoded));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(tables, build, "PirateAssaultContext", bytes[..^1]));
        if (build != "kr-1256")
            Assert.Null(DynamicConfigContent.PublishedSlot(tables, build, 8, "PirateAssaultContext"));
    }

    /// <summary>The character ability notification always has 16-bit keys, even where the dynamic curves use 32-bit keys.</summary>
    [Theory]
    [InlineData("kr-1256", 751)]
    [InlineData("cn-774", 750)]
    [InlineData("tw-454", 751)]
    public void AccountAbilityLevelsUseTheirOwnKeyWidth(string build, int opcode)
    {
        var tables = TestBuilds.Packets;
        Assert.Equal((ushort)opcode, TestBuilds.Opcodes(build).S2C("gspChangePirateAssaultAbilityLevel"));
        var bytes = Convert.FromHexString("02000000020003000000050004000000");
        var decoded = PacketCodec.Read(tables, build, "PirateAssaultAbilityLevels", bytes);
        var rows = Assert.IsType<object?[]>(decoded["abilities"]);
        Assert.Equal(2, rows.Length);
        var last = Assert.IsType<Dictionary<string, object?>>(rows[1]);
        Assert.Equal((ushort)5, last["unknown_key"]);
        Assert.Equal(4, last["unknown_value"]);
        Assert.Equal(bytes, PacketCodec.Write(tables, build, "PirateAssaultAbilityLevels", decoded));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(tables, build, "PirateAssaultAbilityLevels", bytes[..^1]));
    }
}
