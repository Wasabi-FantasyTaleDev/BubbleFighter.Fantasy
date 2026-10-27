using BubbleFighter.Fantasy.Tests;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using Xunit;

namespace BubbleFighter.Fantasy.Wire.Tests;

/// <summary>
/// cn-324 runs the legacy stream over its own cipher tables (cn-324 <c>sub_785850</c> reads
/// <c>byte_B1FA08</c> modulo 49 and <c>byte_B1FA40</c> modulo 24).
/// </summary>
public sealed class Cn324CipherTests
{
    [Fact]
    public void Cn324FramesRoundTripAndDifferFromTheKoreanTables()
    {
        var frame = PacketFrame_5Bytes.Encode(1, [0x44, 0x01, 0x00, 0x00]);
        var cn = (byte[])frame.Clone();
        TestWireDialects.Get("cn-324").CreateEncryptor(PacketDirection.ClientToServer).Apply(cn);
        var kr = (byte[])frame.Clone();
        TestWireDialects.Get("kr-812").CreateEncryptor(PacketDirection.ClientToServer).Apply(kr);
        Assert.NotEqual(kr, cn);
        TestWireDialects.Get("cn-324").CreateDecryptor(PacketDirection.ClientToServer).Apply(cn);
        Assert.Equal(frame, cn);
    }

    [Fact]
    public void SeedsOutsideTheTablesAreRejected()
    {
        Assert.Throws<ArgumentException>(() => new SimpleCipher(new byte[49], new byte[24], 49, 0));
        Assert.Throws<ArgumentException>(() => new SimpleCipher([], new byte[24], 0, 0));
    }
}
