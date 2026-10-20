using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.InGame.Wire;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>
/// Pins the independent profile vectors, including the worn items replayed after equipment reset.
/// </summary>
public sealed class ProfileMapEntryVecBTests
{
    [Fact]
    public void WornVectorPinsAllWeaponPositionsWithoutDuplicatingCharacter()
    {
        var entry = new ProfileMapEntry(11, 300010, EquippedItemIds: [300010, 203073, 201119, 205001, 207001]);
        var body = Written(entry);
        Assert.Equal(60, body.Length); // 44-byte empty entry plus four i32 worn items.
        var (_, character, vecA, vecB, vecC, _, items) = Read(body);
        Assert.Equal(300010, character);
        Assert.Equal(new[] { 203073, 201119, 205001, 207001 }, vecA);
        Assert.Empty(vecB);
        Assert.Empty(vecC);
        Assert.Empty(items);
        var party = RoomSessionBlob.UpdatePartySolo(11, "host", 300010, entry.EquippedItemIds);
        Assert.Equal(RoomSessionBlob.UpdatePartySolo(11, "host", 300010).Length + 16, party.Length);
    }
    /// <summary>Reads back an entry written by <see cref="ProfileMapEntry.Write"/>.</summary>
    private static (int Identity, int CharacterId, int[] VecA, int[] VecB, int[] VecC, int[] Blob, int[] Items)
        Read(byte[] payload)
    {
        var r = new PacketReader(payload);
        var identity = r.ReadInt32();
        var characterId = r.ReadInt32();
        var vecA = ReadVector(ref r);
        var vecB = ReadVector(ref r);
        var vecC = ReadVector(ref r);
        var blobLength = r.ReadInt32();
        var blob = new int[blobLength / 4];
        for (var i = 0; i < blob.Length; i++)
            blob[i] = r.ReadInt32();
        var items = ReadVector(ref r);
        return (identity, characterId, vecA, vecB, vecC, blob, items);
    }

    private static int[] ReadVector(ref PacketReader r)
    {
        var count = r.ReadUInt32();
        var values = new int[count];
        for (var i = 0; i < values.Length; i++)
            values[i] = r.ReadInt32();
        return values;
    }

    /// <summary>The legacy shape without a stored selection keeps every vector empty.</summary>
    [Fact]
    public void DefaultEntryWritesEmptyVectors()
    {
        var entry = new ProfileMapEntry(700001, 300001, 0);
        var (identity, characterId, vecA, vecB, vecC, blob, items) = Read(Written(entry));

        Assert.Equal(700001, identity);
        Assert.Equal(300001, characterId);
        Assert.Empty(vecA);
        Assert.Empty(vecB);
        Assert.Empty(vecC);
        Assert.Equal(4, blob.Length);
        Assert.Empty(items);
    }

    /// <summary>The legacy experimental input stays separate from worn vecA.</summary>
    [Fact]
    public void ModelItemIdsLandInVecBOnly()
    {
        var ids = new[] { 301000, 302000 };
        var entry = new ProfileMapEntry(700002, 300001, 1, ids);
        var (_, _, vecA, vecB, vecC, _, _) = Read(Written(entry));

        Assert.Equal(ids, vecB);
        Assert.Empty(vecA);
        Assert.Empty(vecC);
    }

    /// <summary>An empty list is the same as no list, so the default is not accidentally widened.</summary>
    [Fact]
    public void EmptyListIsSameAsNull()
    {
        var withNull = Written(new ProfileMapEntry(700003, 300001, 2, null));
        var withEmpty = Written(new ProfileMapEntry(700003, 300001, 2, []));

        Assert.Equal(withNull, withEmpty);
    }

    private static byte[] Written(ProfileMapEntry entry)
    {
        var w = PacketWriter.Create();
        entry.Write(w);
        return ((System.Buffers.ArrayBufferWriter<byte>)w.Buffer).WrittenSpan.ToArray();
    }
}
