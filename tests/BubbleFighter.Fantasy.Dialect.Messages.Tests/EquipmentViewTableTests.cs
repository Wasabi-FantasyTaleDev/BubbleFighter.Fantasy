using BubbleFighter.Fantasy.Abstractions.Protocol;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>
/// Pins the equipped-view publications the shared binding writes from the tables. kr-1256's bodies are the bytes the
/// former modern writer sent: the view is the profile record kr-1256 sub_9AF2F0 reads (two words, three word vectors,
/// a byte vector, a record vector), the preset list is kr-1256 sub_9C3880's (a byte, a word, three-word records, a
/// byte vector) and the preset reply is a byte and a status word.
/// </summary>
public sealed class EquipmentViewTableTests
{
    private const string Kr1256 = "kr-1256";

    /// <summary>CN and TW retain their own native container counts when publishing a worn weapon.</summary>
    [Theory]
    [InlineData("cn-774", 4)]
    [InlineData("tw-454", 3)]
    public void UnkeyedModernViewsMatchTheirNativeEquipmentReader(string build, int remainingContainers)
    {
        // cn-774 sub_496F40 / tw-454 sub_4A0550: four words, the item vector, then the native trailing vectors.
        var body = EquipmentViewBinding.WriteView(TestBuilds.Packets, build, 42, 300001, [203073]);
        Assert.Equal("2A000000E193040000000000FFFFFFFF0100000041190300" + new string('0', remainingContainers * 8),
            Convert.ToHexString(body));
        Assert.Equal(body, BubbleFighter.Fantasy.Packets.PacketCodec.Write(TestBuilds.Packets, build,
            "EquippedObjectEntry", new Dictionary<string, object?>
            {
                ["user_number"] = 42, ["character_id"] = 300001, ["equipped_items"] = new[] { 203073 },
            }));
    }

    /// <summary>The TW byte vector is immediately after the worn ids; CN carries an extra word vector.</summary>
    [Theory]
    [InlineData("cn-774", "0100000004030201030000000708090000000000000000")]
    [InlineData("tw-454", "030000000708090000000000000000")]
    public void NativeAppearanceContainerWidthsStayDistinct(string build, string expectedTail)
    {
        var values = new Dictionary<string, object?>
        {
            ["user_number"] = 42, ["character_id"] = 300001,
            ["unknown_32"] = build == "cn-774" ? new object[] { 0x01020304 } : new object[] { 7, 8, 9 },
            ["unknown_44"] = build == "cn-774" ? new byte[] { 7, 8, 9 } : Array.Empty<byte>(),
        };
        var expected = "2A000000E193040000000000FFFFFFFF00000000" + expectedTail;
        foreach (var structure in new[] { "SoloPartyEquippedItem", "EquippedObjectEntry" })
            Assert.Equal(expected, Convert.ToHexString(BubbleFighter.Fantasy.Packets.PacketCodec.Write(
                TestBuilds.Packets, build, structure, values)));
    }

    [Fact]
    public void Kr1256ViewIsTheProfileRecordWithTheWornIds()
    {
        var body = EquipmentViewBinding.WriteView(TestBuilds.Packets, Kr1256, 42, 300001, [300001, 203073, 205001]);
        Assert.Equal("2A000000" + "E1930400" + "03000000" + "E1930400" + "41190300" + "C9200300"
            + "00000000" + "00000000" + "00000000" + "00000000", Convert.ToHexString(body));
        var parsed = ReadSub9AF2F0(body);
        Assert.Equal((42, 300001, body.Length), (parsed.Identity, parsed.Character, parsed.Consumed));
        Assert.Equal(new[] { 300001, 203073, 205001 }, parsed.Worn);
    }

    [Fact]
    public void Kr1256EmptyViewIsTwentyEightBytes()
    {
        var body = EquipmentViewBinding.WriteView(TestBuilds.Packets, Kr1256, 1, 300001, []);
        Assert.Equal("01000000" + "E1930400" + new string('0', 40), Convert.ToHexString(body));
        Assert.Equal(body.Length, ReadSub9AF2F0(body).Consumed);
    }

    [Fact]
    public void Kr1256PresetListCarriesOneRecordPerPage()
    {
        var body = PresetWire.WriteUpdate(TestBuilds.Packets, Kr1256, 0, 160, new int[5], 201489, 205001, 3);
        var record = "11130300" + "C9200300" + "00000000";
        Assert.Equal("00" + "A0000000" + "03000000" + record + record + record + "00000000", Convert.ToHexString(body));
        Assert.Equal("00" + "A0000000" + "00000000" + "00000000",
            Convert.ToHexString(PresetWire.WriteUpdate(TestBuilds.Packets, Kr1256, 0, 160, new int[5])));
    }

    [Fact]
    public void Kr1256PresetReplyIsTheSlotThenTheStatus()
    {
        Assert.Equal("0000000000", Convert.ToHexString(PresetWire.WriteChangeReply(TestBuilds.Packets, Kr1256, 0, 0)));
        Assert.Equal("02A0000000", Convert.ToHexString(PresetWire.WriteChangeReply(TestBuilds.Packets, Kr1256, 2, 160)));
    }

    [Theory]
    [InlineData("kr-812", "EquippedItemList", new[] { "view", "preset_update" })]
    [InlineData("kr-1256", "RoomProfileEntry", new[] { "preset_reply", "preset_update", "view" })]
    [InlineData("cn-774", "SoloPartyEquippedItem", new[] { "view" })]
    [InlineData("tw-454", "SoloPartyEquippedItem", new[] { "view" })]
    public void EachBuildStatesItsViewStructureAndOrder(string build, string structure, string[] order)
    {
        var values = EquipmentViewValues.Of(TestBuilds.Packets, build);
        Assert.Equal(structure, values.ViewStructure);
        Assert.Equal(order, values.LocalView);
    }

    /// <summary>Follows kr-1256 sub_9AF2F0's read order and reports how many bytes it consumed.</summary>
    private static (int Identity, int Character, int[] Worn, int Consumed) ReadSub9AF2F0(byte[] body)
    {
        var r = new PacketReader(body);
        var identity = r.ReadInt32();
        var character = r.ReadInt32();
        var worn = Words(ref r);
        Words(ref r);
        Words(ref r);
        r.ReadBytes(checked((int)r.ReadUInt32()));
        Assert.Equal(0u, r.ReadUInt32()); // kr-1256 sub_10D9050: no records are sent
        return (identity, character, worn, r.Offset);

        static int[] Words(ref PacketReader reader)
        {
            var values = new int[reader.ReadUInt32()];
            for (var i = 0; i < values.Length; i++)
                values[i] = reader.ReadInt32();
            return values;
        }
    }
}
