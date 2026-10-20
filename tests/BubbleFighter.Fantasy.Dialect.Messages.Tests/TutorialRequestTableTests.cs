using System.Buffers;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Dialect.Rooms;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins tutorial request envelopes independently of the table-driven parser.</summary>
public sealed class TutorialRequestTableTests
{
    /// <summary>Independent native envelopes retain their values and reject every incomplete body.</summary>
    [Theory]
    [InlineData("kr-812", 6)]
    [InlineData("cn-717", 6)]
    [InlineData("cn-324", 4)]
    [InlineData("cn-279", 4)]
    [InlineData("id-400", 5)]
    [InlineData("id-1809", 6)]
    [InlineData("cn-706", 6)]
    public void NativeEnvelopePreservesStringsTimeDifficultyAndCompleteLength(string build, int boolCount)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new PacketWriter(buffer);
        for (var index = 0; index < 13; index++)
            writer.WriteInt32(index == 8 ? 99_999_000 : index + 100);
        for (var index = 0; index < boolCount; index++)
            writer.WriteInt32(index % 2 == 0 ? -1 : 0);
        writer.WriteWideStringPrefixed("dir");
        writer.WriteWideStringPrefixed("display");
        writer.WriteWideStringPrefixed("rel_prologue_01");
        writer.WriteInt32(3);
        var body = buffer.WrittenSpan.ToArray();
        Assert.Equal(68 + 4 * boolCount + 2 * (3 + 7 + 15), body.Length);
        Assert.Equal(new PlayTutorialRequest("dir", "display", "rel_prologue_01", 3, 99_999_000),
            TutorialWire.ReadPlayTutorial(TestBuilds.Packets, build, body));
        for (var length = 0; length < body.Length; length++)
        {
            var truncated = body[..length];
            Assert.Throws<PacketFormatException>(() =>
                TutorialWire.ReadPlayTutorial(TestBuilds.Packets, build, truncated));
        }
        Assert.Throws<PacketFormatException>(() =>
            TutorialWire.ReadPlayTutorial(TestBuilds.Packets, build, [.. body, 0]));
        var stringCountOffset = 52 + 4 * boolCount;
        BitConverter.TryWriteBytes(body.AsSpan(stringCountOffset), uint.MaxValue);
        Assert.Throws<PacketFormatException>(() =>
            TutorialWire.ReadPlayTutorial(TestBuilds.Packets, build, body));
    }

    /// <summary>Flattened object offsets preserve the native bool reader's nonnumeric wire order.</summary>
    [Theory]
    [InlineData("kr-812", "60,61,63,64,62,65")]
    [InlineData("cn-717", "60,61,63,64,62,65")]
    [InlineData("cn-324", "60,61,62,63")]
    [InlineData("cn-279", "60,61,62,63")]
    [InlineData("id-400", "60,61,63,64,62")]
    [InlineData("id-1809", "60,61,63,64,62,65")]
    [InlineData("cn-706", "60,61,63,64,62,65")]
    public void NativeBoolStoreOffsetsRetainWireOrder(string build, string offsets) =>
        Assert.Equal(offsets, string.Join(',', TestBuilds.Packets.Resolve(build, "TutorialPlayRequest")
            .Where(field => field.Type == "bool").Select(field => field.Offset)));
}
