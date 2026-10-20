using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.InGame.Wire;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>
/// Byte-length pins for the C2S 513 map-list report, whose body is three
/// prefixed wstrings and thirteen <c>i32</c>s per the builder <c>sub_10D3F80</c>.
///
/// </summary>
public sealed class MapListReportTests
{
    /// <summary>Builds a body the way <c>sub_10D3F80</c> writes it.</summary>
    private static byte[] Body(string name, string mapKey, string sceneKey, int[] tail)
    {
        var w = PacketWriter.Create();
        w.WriteWideStringPrefixed(name);
        w.WriteWideStringPrefixed(mapKey);
        w.WriteWideStringPrefixed(sceneKey);
        foreach (var t in tail)
            w.WriteInt32(t);
        return ((System.Buffers.ArrayBufferWriter<byte>)w.Buffer).WrittenSpan.ToArray();
    }

    /// <summary>A reference Random frame, byte for byte.</summary>
    [Fact]
    public void RandomSelectionParses()
    {
        // 06 00 00 00 "Random" 00 00 00 00 0a 00 00 00 "Random_Hot" then thirteen i32s.
        var payload = new byte[]
        {
            0x06, 0x00, 0x00, 0x00,
            0x52, 0x00, 0x61, 0x00, 0x6E, 0x00, 0x64, 0x00, 0x6F, 0x00, 0x6D, 0x00,
            0x00, 0x00, 0x00, 0x00,
            0x0A, 0x00, 0x00, 0x00,
            0x52, 0x00, 0x61, 0x00, 0x6E, 0x00, 0x64, 0x00, 0x6F, 0x00, 0x6D, 0x00,
            0x5F, 0x00, 0x48, 0x00, 0x6F, 0x00, 0x74, 0x00,
            0x01, 0x00, 0x00, 0x00,
            0x01, 0x00, 0x00, 0x00,
            0x80, 0xA9, 0x03, 0x00,
            0x03, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
            0x01, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
        };

        var report = MapListReport.Parse(payload);

        Assert.Equal("Random", report.Name);
        Assert.Equal(string.Empty, report.MapKey);
        Assert.Equal("Random_Hot", report.SceneKey);
        Assert.Equal(1, report.Unknown4);
        Assert.Equal(1, report.Unknown5);
        Assert.Equal(0x3A980, report.Unknown6);
        Assert.Equal(3, report.Unknown7);
    }

    /// <summary>A named map carries its display name and both keys.</summary>
    [Fact]
    public void NamedMapParsesAllThreeStrings()
    {
        var report = MapListReport.Parse(Body("우리학교", "rel_S315", "rel_S315", new int[13]));

        Assert.Equal("우리학교", report.Name);
        Assert.Equal("rel_S315", report.MapKey);
        Assert.Equal("rel_S315", report.SceneKey);
    }

    /// <summary>A round trip through the writer and the parser is stable.</summary>
    [Fact]
    public void RoundTripIsStable()
    {
        var tail = new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13 };
        var body = Body("빌리지 강변 쉼터", "rel_Village07_Easy02", "rel_Village07_Easy02", tail);
        var report = MapListReport.Parse(body);

        Assert.Equal("빌리지 강변 쉼터", report.Name);
        Assert.Equal("rel_Village07_Easy02", report.MapKey);
        Assert.Equal("rel_Village07_Easy02", report.SceneKey);
        Assert.Equal(tail[0], report.Unknown4);
        Assert.Equal(tail[12], report.Unknown16);
    }
}
