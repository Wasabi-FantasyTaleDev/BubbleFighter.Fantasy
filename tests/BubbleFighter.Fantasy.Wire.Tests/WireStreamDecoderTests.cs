using BubbleFighter.Fantasy.Tests;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Wire;
using Xunit;

namespace BubbleFighter.Fantasy.Wire.Tests;

public sealed class WireStreamDecoderTests
{
    private static readonly WireDialect Dialect = TestWireDialects.Get("kr-1256");

    [Fact]
    public void ServerStreamYieldsGreetingThenFramesWhateverTheChunking()
    {
        var greeting = BootstrapGreeting.Build(0, "cmd");
        var stream = Concat(greeting, Encrypt(PacketDirection.ServerToClient,
            Dialect.EncodeFrame(2, [1, 2, 3]), Dialect.EncodeFrame(17, []), Dialect.EncodeFrame(3, new byte[300])));

        foreach (var chunk in new[] { 1, 7, 258, 259, stream.Length })
        {
            var decoder = new WireStreamDecoder(Dialect, PacketDirection.ServerToClient, 4096);
            var items = new List<WireStreamItem>();
            for (var i = 0; i < stream.Length; i += chunk)
                items.AddRange(decoder.Feed(stream.AsSpan(i, Math.Min(chunk, stream.Length - i))));

            Assert.Equal(4, items.Count);
            Assert.Equal(greeting, Assert.IsType<WireStreamItem.Preamble>(items[0]).Bytes);
            var frames = items.Skip(1).Cast<WireStreamItem.Frame>().Select(f => f.Value).ToList();
            Assert.Equal(new ushort[] { 2, 17, 3 }, frames.Select(f => f.Opcode));
            Assert.Equal(new byte[] { 1, 2, 3 }, frames[0].Payload);
            Assert.Empty(frames[1].Payload);
            Assert.Equal(300, frames[2].Payload.Length);
            Assert.Equal(0, decoder.PendingBytes);
        }
    }

    [Fact]
    public void ClientStreamHasNoPreamble()
    {
        var decoder = new WireStreamDecoder(Dialect, PacketDirection.ClientToServer, 4096);
        var items = decoder.Feed(Encrypt(PacketDirection.ClientToServer, Dialect.EncodeFrame(1, new byte[96])));
        var frame = Assert.IsType<WireStreamItem.Frame>(Assert.Single(items)).Value;
        Assert.Equal(1, frame.Opcode);
        Assert.Equal(96, frame.Payload.Length);
    }

    [Fact]
    public void PartialFrameIsHeldUntilComplete()
    {
        var bytes = Encrypt(PacketDirection.ClientToServer, Dialect.EncodeFrame(9, new byte[20]));
        var decoder = new WireStreamDecoder(Dialect, PacketDirection.ClientToServer, 4096);
        Assert.Empty(decoder.Feed(bytes.AsSpan(0, 10)));
        Assert.Equal(10, decoder.PendingBytes);
        Assert.Single(decoder.Feed(bytes.AsSpan(10)));
    }

    [Fact]
    public void CorruptedHeaderFailsLoud()
    {
        var bytes = Encrypt(PacketDirection.ClientToServer, Dialect.EncodeFrame(9, new byte[4]));
        bytes[5] ^= 0xFF;
        var decoder = new WireStreamDecoder(Dialect, PacketDirection.ClientToServer, 4096);
        Assert.Throws<PacketFormatException>(() => decoder.Feed(bytes));
    }

    [Fact]
    public void CoalescedMaximumFramesDoNotExceedPendingBufferBound()
    {
        var bytes = Encrypt(PacketDirection.ClientToServer,
            Dialect.EncodeFrame(9, new byte[100]), Dialect.EncodeFrame(10, new byte[100]), Dialect.EncodeFrame(11, new byte[100]));
        var decoder = new WireStreamDecoder(Dialect, PacketDirection.ClientToServer, 100);
        var items = decoder.Feed(bytes);
        Assert.Equal(new ushort[] { 9, 10, 11 }, items.Cast<WireStreamItem.Frame>().Select(item => item.Value.Opcode));
        Assert.Equal(0, decoder.PendingBytes);
    }

    [Fact]
    public void FrameOverTheLimitFailsLoud()
    {
        var bytes = Encrypt(PacketDirection.ClientToServer, Dialect.EncodeFrame(9, new byte[200]));
        var decoder = new WireStreamDecoder(Dialect, PacketDirection.ClientToServer, 100);
        Assert.Throws<PacketFormatException>(() => decoder.Feed(bytes));
    }

    [Fact]
    public void UnknownFrameNameFails()
    {
        Assert.Throws<ArgumentException>(() => WireStream.Parse("frame9", null, null));
        Assert.Throws<ArgumentException>(() => new WireStream(WireFrameKind.Legacy5, [1], null));
    }

    [Fact]
    public void CreatedDialectCarriesTheProfileIdentity()
    {
        var profile = new ClientProfile("cn-404", "cn", 404, 0, 0, "test build");
        var dialect = WireDialect.Create(profile, new WireStream(WireFrameKind.Legacy5));
        Assert.Equal("cn-404", dialect.Id);
        Assert.Same(profile, dialect.Profile);
        Assert.Equal(LegacyFrame.HeaderSize, dialect.MaxFrameOverheadBytes);
    }

    [Fact]
    public void DerivedBuildOverridesOnlyWhatDiffers()
    {
        var derived = new ClientPreambleBuild();
        var bytes = Concat([0xAA, 0xBB], Encrypt(PacketDirection.ClientToServer, Dialect.EncodeFrame(4, [7])));
        var items = new WireStreamDecoder(derived, PacketDirection.ClientToServer, 4096).Feed(bytes);
        Assert.Equal(new byte[] { 0xAA, 0xBB }, Assert.IsType<WireStreamItem.Preamble>(items[0]).Bytes);
        Assert.Equal(4, Assert.IsType<WireStreamItem.Frame>(items[1]).Value.Opcode);
    }

    private sealed class ClientPreambleBuild : WireDialect
    {
        public override ClientProfile Profile { get; } = new("test-derived", "kr", 1, 0, 0, "test");

        public override int MaxFrameOverheadBytes => Dialect.MaxFrameOverheadBytes;

        public override byte[] BuildServerPreamble(ushort status, string commandLine) => Dialect.BuildServerPreamble(status, commandLine);

        public override int PreambleLength(PacketDirection direction) =>
            direction == PacketDirection.ClientToServer ? 2 : Dialect.PreambleLength(direction);

        public override IStreamTransform CreateDecryptor(PacketDirection direction) => Dialect.CreateDecryptor(direction);

        public override IStreamTransform CreateEncryptor(PacketDirection direction) => Dialect.CreateEncryptor(direction);

        public override bool TryReadFrame(ref System.Buffers.ReadOnlySequence<byte> plaintext, int maxPayloadBytes, out DecodedFrame frame) =>
            Dialect.TryReadFrame(ref plaintext, maxPayloadBytes, out frame);

        public override byte[] EncodeFrame(ushort opcode, ReadOnlySpan<byte> payload) => Dialect.EncodeFrame(opcode, payload);
    }

    private static byte[] Encrypt(PacketDirection direction, params byte[][] frames)
    {
        var plain = Concat(frames);
        Dialect.CreateEncryptor(direction).Apply(plain);
        return plain;
    }

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
}
