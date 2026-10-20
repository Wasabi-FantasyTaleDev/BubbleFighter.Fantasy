using BubbleFighter.Fantasy.Wire;
using System.Buffers.Binary;
using System.Text;
using BubbleFighter.Fantasy.Protocol;
using Xunit;

namespace BubbleFighter.Fantasy.Protocol.Tests;

/// <summary>
/// Pins <see cref="BootstrapGreeting"/>: 258-byte plaintext consumed by
/// <c>ServerConnection::OnFirstConnected</c> (<c>sub_10BB110 -&gt; sub_99EAD0</c>).
/// </summary>
public sealed class BootstrapGreetingTests
{
    [Fact]
    public void GreetingIsExactly258Bytes()
    {
        var greeting = BootstrapGreeting.Build(4, "");
        Assert.Equal(WirePins.GreetingSize, greeting.Length);
        Assert.Equal(BootstrapGreeting.Size, greeting.Length);
        Assert.Equal(4, BinaryPrimitives.ReadUInt16LittleEndian(greeting));
        Assert.StartsWith(WirePins.EmptyGreetingPrefixHex, Convert.ToHexString(greeting));
        Assert.All(greeting[2..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void GreetingCarriesCommandLineUtf16()
    {
        var greeting = BootstrapGreeting.Build(4, "run");
        Assert.Equal(WirePins.GreetingSize, greeting.Length);
        Assert.Equal(WirePins.RunGreetingPrefixHex, Convert.ToHexString(greeting[..8]));
        Assert.Equal('r', (char)BinaryPrimitives.ReadUInt16LittleEndian(greeting.AsSpan(2)));
        Assert.Equal('u', (char)BinaryPrimitives.ReadUInt16LittleEndian(greeting.AsSpan(4)));
        Assert.Equal('n', (char)BinaryPrimitives.ReadUInt16LittleEndian(greeting.AsSpan(6)));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(greeting.AsSpan(8)));
        Assert.All(greeting[8..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void GreetingRejectsMoreThan127Chars()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BootstrapGreeting.Build(4, new string('x', 128)));
        var max = BootstrapGreeting.Build(4, new string('x', 127));
        Assert.Equal(WirePins.GreetingSize, max.Length);
        // 127 UTF-16LE chars occupy bytes 2..255; the 128th slot (256..257) stays zero.
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(max.AsSpan(256)));
        Assert.Equal((ushort)'x', BinaryPrimitives.ReadUInt16LittleEndian(max.AsSpan(254)));
        Assert.Equal(2 + (127 * 2) + 2, max.Length);
    }

    [Fact]
    public void GreetingNullCommandLineThrows()
    {
        Assert.Throws<ArgumentNullException>(() => BootstrapGreeting.Build(4, null!));
    }

    [Fact]
    public void GreetingPinsStatusWordAtOffsetZero()
    {
        var greeting = BootstrapGreeting.Build(ushort.MaxValue, "");
        Assert.Equal(WirePins.GreetingSize, greeting.Length);
        Assert.Equal(ushort.MaxValue, BinaryPrimitives.ReadUInt16LittleEndian(greeting));
        Assert.All(greeting[2..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void GreetingUtf16MatchesEncodingUnicode()
    {
        const string commandLine = "patch";
        var greeting = BootstrapGreeting.Build(4, commandLine);
        Assert.Equal(WirePins.GreetingSize, greeting.Length);
        Assert.Equal(Encoding.Unicode.GetBytes(commandLine), greeting.AsSpan(2, commandLine.Length * 2).ToArray());
    }
}
