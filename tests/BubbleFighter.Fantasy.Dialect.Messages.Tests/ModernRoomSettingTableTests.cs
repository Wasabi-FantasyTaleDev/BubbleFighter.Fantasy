using System.Buffers;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins modern settings builders independently of the inherited legacy request layout.</summary>
public sealed class ModernRoomSettingTableTests
{
    /// <summary>Every widened native byte remains a full word, with the CN and TW extra string in its own position.</summary>
    [Theory]
    [InlineData("kr-1256", 3, "72,73,76,80,84,88,92,96,100,104,108,112,113", 70)]
    [InlineData("cn-774", 4, "96,97,100,104,108,112,116,120,124,128,132,136", 72)]
    [InlineData("tw-454", 4, "96,97,100,104,108,112,116,120,124,128,132,136", 72)]
    public void SettingChangePinsAuditedStringsAndWordWidths(string build, int strings, string offsets, int length)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new PacketWriter(buffer);
        for (var index = 0; index < strings; index++)
            writer.WriteWideStringPrefixed(((char)('A' + index)).ToString());
        var wordOffsets = offsets.Split(',');
        for (var index = 0; index < wordOffsets.Length; index++)
            writer.WriteInt32(index + 1);
        var body = buffer.WrittenSpan.ToArray();
        Assert.Equal(length, body.Length);

        var fields = PacketCodec.ReadScalars(TestBuilds.Packets, build, "RoomGameSettingChangeRequest", body);
        Assert.Equal(strings + wordOffsets.Length, fields.Count);
        for (var index = 0; index < strings; index++)
            Assert.Equal(((char)('A' + index)).ToString(), fields[$"unknown_{index * 24}"]);
        for (var index = 0; index < wordOffsets.Length; index++)
            // kr-1256 names its word at +104 the room's mode (kr-1256 sub_96CF30); every other word stays unknown.
            Assert.Equal(index + 1, fields[build == "kr-1256" && wordOffsets[index] == "104" ? "mode" : $"unknown_{wordOffsets[index]}"]);
        Assert.Equal(body, PacketCodec.Write(TestBuilds.Packets, build, "RoomGameSettingChangeRequest",
            fields.ToDictionary(pair => pair.Key, pair => (object?)pair.Value)));

        for (var count = 0; count < body.Length; count++)
        {
            var truncated = body[..count];
            Assert.Throws<PacketFormatException>(() =>
                PacketCodec.ReadScalars(TestBuilds.Packets, build, "RoomGameSettingChangeRequest", truncated));
        }
        Assert.Throws<PacketFormatException>(() =>
            PacketCodec.ReadScalars(TestBuilds.Packets, build, "RoomGameSettingChangeRequest", [.. body, 0]));
    }
}
