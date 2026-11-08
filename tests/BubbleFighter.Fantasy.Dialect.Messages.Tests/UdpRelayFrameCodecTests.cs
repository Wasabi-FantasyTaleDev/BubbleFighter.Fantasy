using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Dialect.Messages;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins the native datagram header and opcode position used by its native UDP reader.</summary>
public sealed class ProtocolDatagramCodecTests
{
    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-717")]
    [InlineData("id-1809")]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    [InlineData("cn-735")]
    [InlineData("cn-738")]
    [InlineData("cn-742")]
    public void RoundTripsNativeDatagramFrameWithoutChangingBody(string build)
    {
        var tables = PacketTables.Load(Path.Combine(FindRoot(), "packets"));
        byte[] body = [0xEF, 0xCD, 0xAB, 0x89, 0x67, 0x45, 0x23, 0x01, 0xAA];

        var datagram = ProtocolDatagramCodec.Write(tables, build, 20001, body);

        Assert.Equal("214E00000000EFCDAB8967452301AA", Convert.ToHexString(datagram));
        var decoded = ProtocolDatagramCodec.Read(tables, build, datagram);
        Assert.Equal((ushort)20001, decoded.Opcode);
        Assert.Equal(body, decoded.Body.ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(2)]
    [InlineData(4)]
    public void RejectsDatagramsWithoutACompleteOpcode(int length)
    {
        var tables = PacketTables.Load(Path.Combine(FindRoot(), "packets"));
        Assert.Throws<PacketFormatException>(() => ProtocolDatagramCodec.Read(tables, "kr-1256", new byte[length]));
    }

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "BubbleFighter.Fantasy.sln"))) return directory.FullName;
        throw new InvalidOperationException("Repository root not found.");
    }
}
