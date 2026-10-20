using BubbleFighter.Fantasy.Tests;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using Xunit;

namespace BubbleFighter.Fantasy.Wire.Tests;

/// <summary>Pins the cn-279 stream to client bytes and checks per-connection cipher continuity.</summary>
public sealed class Cn279StreamTests
{
    private static readonly WireDialect Dialect = TestWireDialects.Get("cn-279");

    [Fact]
    public void FirstConnectPrefixMatchesTheNativeClient()
    {
        // A cn-279 first C2S frame: header plus the version, without machine details.
        var bytes = Convert.FromHexString("1B71AD84F1D7773BBF6DD1A639EB");
        Dialect.CreateDecryptor(PacketDirection.ClientToServer).Apply(bytes);
        Assert.Equal(Convert.FromHexString("FF001E0500000000010017010000"), bytes);
        Assert.Equal("cn-279", Dialect.Id);
        Assert.Equal(279, Dialect.Profile.ClientVersionMajor);
        Assert.Equal(10, Dialect.MaxFrameOverheadBytes);
    }

    [Fact]
    public void ServerGreetingIsPlaintextAndDoesNotAdvanceTheFrameCipher()
    {
        var greeting = Dialect.BuildServerPreamble(1, "");
        Assert.Equal(258, greeting.Length);
        Assert.Equal(1, greeting[0]);
        Assert.All(greeting.Skip(1), value => Assert.Equal(0, value));
        // Independently evaluated from 0x46A040, seeds at 0x570560, and 0x56E7E0.
        var cipherFrames = Convert.FromHexString("B89440DC4FBD8F57BC8D3D123C899FD57F5FBD873D05B13F124D344E");
        var raw = greeting.Concat(cipherFrames).ToArray();
        foreach (var chunkSize in new[] { 1, 7, 257, 258, 259, raw.Length })
        {
            var decoder = new WireStreamDecoder(Dialect, PacketDirection.ServerToClient, 8);
            var output = new List<WireStreamItem>();
            for (var i = 0; i < raw.Length; i += chunkSize)
                output.AddRange(decoder.Feed(raw.AsSpan(i, Math.Min(chunkSize, raw.Length - i))));
            Assert.Equal(3, output.Count);
            Assert.Equal(greeting, Assert.IsType<WireStreamItem.Preamble>(output[0]).Bytes);
            var first = Assert.IsType<WireStreamItem.Frame>(output[1]).Value;
            var second = Assert.IsType<WireStreamItem.Frame>(output[2]).Value;
            Assert.Equal(2, first.Opcode);
            Assert.Equal(new byte[8], first.Payload);
            Assert.Equal(4, second.Opcode);
            Assert.Empty(second.Payload);
            Assert.Equal(0, decoder.PendingBytes);
        }
    }

    [Fact]
    public void InterleavedConnectionsAndReconnectEachStartWithFreshState()
    {
        var encryptor = Dialect.CreateEncryptor(PacketDirection.ClientToServer);
        var first = Dialect.EncodeFrame(2, []);
        var next = Dialect.EncodeFrame(117, []);
        encryptor.Apply(first);
        encryptor.Apply(next);
        var a = new WireStreamDecoder(Dialect, PacketDirection.ClientToServer, 8);
        var b = new WireStreamDecoder(Dialect, PacketDirection.ClientToServer, 8);
        Assert.Empty(a.Feed(first.AsSpan(0, 5)));
        Assert.Equal(2, Assert.IsType<WireStreamItem.Frame>(Assert.Single(b.Feed(first))).Value.Opcode);
        Assert.Equal(2, Assert.IsType<WireStreamItem.Frame>(Assert.Single(a.Feed(first.AsSpan(5)))).Value.Opcode);
        Assert.Equal(117, Assert.IsType<WireStreamItem.Frame>(Assert.Single(a.Feed(next))).Value.Opcode);
        Assert.Equal(117, Assert.IsType<WireStreamItem.Frame>(Assert.Single(b.Feed(next))).Value.Opcode);
        var reconnect = new WireStreamDecoder(Dialect, PacketDirection.ClientToServer, 8);
        Assert.Equal(2, Assert.IsType<WireStreamItem.Frame>(Assert.Single(reconnect.Feed(first))).Value.Opcode);
    }
}
