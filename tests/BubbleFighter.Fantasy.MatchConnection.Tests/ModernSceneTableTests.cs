using System.Buffers;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Dialect.Messages;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.MatchConnection.Tests;

public sealed class ModernSceneTableTests
{
    private static readonly PacketTables Tables = PacketTables.Load(
        Path.Combine(RelayHandshakeTableParityTests.RepositoryDirectory(), "packets"));

    [Theory]
    [InlineData("kr-1256", 162)]
    [InlineData("cn-774", 180)]
    [InlineData("tw-454", 168)]
    public void PopulatedNativeScenePreservesIdentityAndEveryNestedByte(string build, int length)
    {
        var bytes = Scene(build);
        Assert.Equal(length, bytes.Length);
        Assert.Equal(new MatchSceneIdentityRegistered(1000000, "host"),
            RelayHandshakeWire.ReadSceneIdentity(Tables, build, bytes));
        if (build == "kr-1256")
            Assert.Equal(new MatchSceneRegistered(7, 1000000, "host"), RelayHandshakeWire.ReadScene(Tables, build, bytes));
        else
            Assert.Throws<PacketFormatException>(() => RelayHandshakeWire.ReadScene(Tables, build, bytes));
        var fields = PacketCodec.Read(Tables, build, "RelaySceneReport", bytes);
        Assert.Equal(bytes, PacketCodec.Write(Tables, build, "RelaySceneReport", fields));
        var equipment = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(fields["equipment"]);
        Assert.Equal(300001, equipment["character_id"]);
        var presets = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(fields["presets"]);
        Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<object?>>(presets["entries"]));
        for (var prefix = 0; prefix < bytes.Length; prefix++)
            Assert.Throws<PacketFormatException>(() =>
                RelayHandshakeWire.ReadSceneIdentity(Tables, build, bytes.AsSpan(0, prefix)));
        Assert.Throws<PacketFormatException>(() => RelayHandshakeWire.ReadSceneIdentity(Tables, build, [.. bytes, 0]));
    }

    [Theory]
    [InlineData("kr-1256", 1256)]
    [InlineData("cn-774", 774)]
    [InlineData("tw-454", 454)]
    public void ReconnectRequiresItsOwningBuildVersion(string build, int version)
    {
        var bytes = Bytes(w =>
        {
            w.WriteInt32(version); w.WriteInt32(1); w.WriteWideStringPrefixed("map");
            w.WriteInt32(1000000); w.WriteWideStringPrefixed("host");
            w.WriteInt32(17); w.WriteInt32(1);
        });
        Assert.Equal(42, bytes.Length);
        Assert.Equal(new MatchMapNamed(1000000, "host", "map"),
            RelayHandshakeWire.ReadMap(Tables, build, version, bytes));
        Assert.Throws<PacketFormatException>(() => RelayHandshakeWire.ReadMap(Tables, build, version + 1, bytes));
    }

    [Theory]
    [InlineData("kr-1256", 28)]
    [InlineData("cn-774", 36)]
    [InlineData("tw-454", 36)]
    public void NativeSceneRejectsOversizedEquipmentCountsBeforeAllocating(string build, int countOffset)
    {
        var bytes = Scene(build);
        BitConverter.GetBytes(uint.MaxValue).CopyTo(bytes, countOffset);
        Assert.Throws<PacketFormatException>(() => RelayHandshakeWire.ReadSceneIdentity(Tables, build, bytes));
    }

    // Native writers: kr-1256 sub_10F32C0/sub_10C4510/sub_10D7D70,
    // cn-774 sub_488AD0/sub_496E60/sub_497FC0 and tw-454 sub_475C20/sub_4A0480/sub_4A1620.
    private static byte[] Scene(string build) => Bytes(w =>
    {
        var korean = build == "kr-1256";
        var chinese = build == "cn-774";
        // kr-1256 sub_9601E0: account, actor, nickname; cn-774 sub_B593D0 / tw-454: actor, nickname, session +236.
        w.WriteInt32(korean ? 7 : 1000000);
        if (korean) w.WriteInt32(1000000);
        w.WriteWideStringPrefixed("host");
        if (!korean) w.WriteInt32(7);
        w.WriteInt32(7); w.WriteInt32(300001);
        if (!korean) { w.WriteInt32(3); w.WriteInt32(-1); }
        Words(w, 100001, 100002);
        if (korean || chinese) Words(w, 91);
        if (korean) Words(w, 92);
        w.WriteInt32(2); w.WriteByte(1); w.WriteByte(2);
        // All three native modifier readers consume two words, a word vector, a word and a wide string.
        w.WriteInt32(1); w.WriteInt32(100001); w.WriteInt32(5);
        Words(w, 11, 12); w.WriteInt32(13); w.WriteWideStringPrefixed("attr");
        if (!korean)
        {
            w.WriteInt32(1); w.WriteInt32(100001); w.WriteDouble(1234.5);
        }
        w.WriteInt32(7); w.WriteInt32(1);
        if (korean) { w.WriteInt32(2); w.WriteInt32(100001); w.WriteInt32(100002); }
        else
        {
            w.WriteByte(1); w.WriteByte(2); w.WriteInt32(100001); w.WriteInt32(100002);
            if (chinese) w.WriteInt32(100003);
        }
        w.WriteInt32(2); w.WriteByte(3); w.WriteByte(4);
        w.WriteWideStringPrefixed("scene"); w.WriteByte(7);
        w.WriteUInt16(1); w.WriteUInt16(2); w.WriteUInt16(3);
        w.WriteUInt16(38); w.WriteByte(5); w.WriteInt32(2); w.WriteByte(6); w.WriteByte(7);
        w.WriteInt32(1);
    });

    private static void Words(PacketWriter writer, params int[] words)
    {
        writer.WriteInt32(words.Length);
        foreach (var word in words) writer.WriteInt32(word);
    }

    private static byte[] Bytes(Action<PacketWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        write(new PacketWriter(buffer));
        return buffer.WrittenSpan.ToArray();
    }
}
