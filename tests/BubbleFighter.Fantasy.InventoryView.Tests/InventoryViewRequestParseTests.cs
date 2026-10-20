using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.InventoryView.Wire;
using Xunit;

namespace BubbleFighter.Fantasy.InventoryView.Tests;

/// <summary>
/// Pins <see cref="InventoryViewRequest.Parse"/> (C2S 806, policy name
/// <c>Unknown806</c>, builder <c>sub_80DA80</c> with string list
/// <c>sub_429D10</c>): two leading <c>i32</c>, a <c>u32</c> record count and one
/// prefixed wstring per record. Truncated, trailing, oversized and implausible
/// bodies fail loud.
/// </summary>
public sealed class InventoryViewRequestParseTests
{
    [Fact]
    public void EmptyListPinsTwelveBytesAndDocumentedHex()
    {
        var payload = WireLengthSupport.Written(w =>
        {
            w.WriteInt32(5);
            w.WriteInt32(6);
            w.WriteUInt32(0);
        });

        Assert.Equal(12, payload.Length);
        Assert.Equal("05000000" + "06000000" + "00000000", Convert.ToHexString(payload));

        var parsed = InventoryViewRequest.Parse(payload);
        Assert.Equal(5, parsed.Unknown0);
        Assert.Equal(6, parsed.Unknown1);
        Assert.Empty(parsed.Records);
    }

    [Fact]
    public void PopulatedListPinsCountTimesRecords()
    {
        var payload = WireLengthSupport.Written(w =>
        {
            w.WriteInt32(1);
            w.WriteInt32(2);
            w.WriteUInt32(2);
            w.WriteWideStringPrefixed("AB");
            w.WriteWideStringPrefixed(string.Empty);
        });

        Assert.Equal(24, payload.Length);
        Assert.Equal(
            "01000000" + "02000000" + "02000000" +
            "02000000" + "41004200" +
            "00000000",
            Convert.ToHexString(payload));

        var parsed = InventoryViewRequest.Parse(payload);
        Assert.Equal(1, parsed.Unknown0);
        Assert.Equal(2, parsed.Unknown1);
        Assert.Equal(new[] { "AB", string.Empty }, parsed.Records);
    }

    [Fact]
    public void KoreanTextSurvivesTheRoundTrip()
    {
        var payload = WireLengthSupport.Written(w =>
        {
            w.WriteInt32(-1);
            w.WriteInt32(0);
            w.WriteUInt32(1);
            w.WriteWideStringPrefixed("다오");
        });

        var parsed = InventoryViewRequest.Parse(payload);
        Assert.Equal(-1, parsed.Unknown0);
        Assert.Equal(new[] { "다오" }, parsed.Records);
    }

    [Fact]
    public void TrailingBytesFailLoudAndNameTheOpcode()
    {
        var payload = WireLengthSupport.Written(w =>
        {
            w.WriteInt32(5);
            w.WriteInt32(6);
            w.WriteUInt32(0);
            w.WriteByte(0);
        });

        var ex = Assert.Throws<PacketFormatException>(() => InventoryViewRequest.Parse(payload));
        Assert.Contains("806", ex.Message, StringComparison.Ordinal);
        Assert.Equal(12, ex.Offset);
    }

    [Fact]
    public void TruncatedStringFailsLoud()
    {
        var payload = WireLengthSupport.Written(w =>
        {
            w.WriteInt32(5);
            w.WriteInt32(6);
            w.WriteUInt32(1);
            w.WriteUInt32(3);
            w.WriteWideStringPrefixed("Da");
        });

        Assert.Throws<PacketFormatException>(() => InventoryViewRequest.Parse(payload));
    }

    [Fact]
    public void StringAboveTheClientWriterCapFailsLoud()
    {
        var payload = WireLengthSupport.Written(w =>
        {
            w.WriteInt32(5);
            w.WriteInt32(6);
            w.WriteUInt32(1);
            w.WriteUInt32(InventoryViewRequest.WideStringCharCap + 1);
        });

        var ex = Assert.Throws<PacketFormatException>(() => InventoryViewRequest.Parse(payload));
        Assert.Contains("806", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ImplausibleCountFailsLoud()
    {
        var payload = WireLengthSupport.Written(w =>
        {
            w.WriteInt32(5);
            w.WriteInt32(6);
            w.WriteUInt32(100);
        });

        var ex = Assert.Throws<PacketFormatException>(() => InventoryViewRequest.Parse(payload));
        Assert.Contains("806", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyPayloadFailsLoud()
    {
        Assert.Throws<PacketFormatException>(() => InventoryViewRequest.Parse([]));
    }
}
