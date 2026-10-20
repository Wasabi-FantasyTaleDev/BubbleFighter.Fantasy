using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Dialect.Messages;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Checks bounded native seed state and the build-specific critical vector shape.</summary>
public sealed class SceneSeedStoreTests
{
    /// <summary>Each table family replaces only its own prior value and session cleanup is complete.</summary>
    [Fact]
    public void StoreReplacesByKindAndRemovesBySession()
    {
        var store = new SceneSeedStore(1);
        store.Set(7, 4, NativeSceneSeedTable.Words(NativeSceneSeedKind.Critical, 1000000, [1u, 2u]));
        store.Set(7, 4, NativeSceneSeedTable.Bytes(NativeSceneSeedKind.Defence, 1000000, [3, 4]));
        store.Set(7, 4, NativeSceneSeedTable.Words(NativeSceneSeedKind.Critical, 1000000, [9u]));

        Assert.True(store.TryGet(7, 4, NativeSceneSeedKind.Critical, out var critical));
        Assert.Equal([9u], critical.WordValues);
        Assert.Single(critical.WordVectors);
        Assert.True(store.TryGet(7, 4, NativeSceneSeedKind.Defence, out var defence));
        Assert.Equal([3, 4], defence.ByteValues);
        store.Remove(7);
        Assert.False(store.TryGet(7, 4, NativeSceneSeedKind.Critical, out _));
    }

    /// <summary>New sessions cannot exceed the bounded store while an existing session may refresh its state.</summary>
    [Fact]
    public void StoreRejectsAnAdditionalSessionAtTheLimit()
    {
        var store = new SceneSeedStore(1);
        store.Set(1, 4, NativeSceneSeedTable.Words(NativeSceneSeedKind.Critical, 1, [1u]));
        Assert.Throws<InvalidOperationException>(() =>
            store.Set(2, 4, NativeSceneSeedTable.Words(NativeSceneSeedKind.Critical, 2, [2u])));
    }

    /// <summary>A new room generation replaces every seed family so an old round cannot be reused.</summary>
    [Fact]
    public void NewGenerationDropsPreviousTables()
    {
        var store = new SceneSeedStore(1);
        store.Set(7, 4, NativeSceneSeedTable.Words(NativeSceneSeedKind.Critical, 1000000, [1u]));
        store.Set(7, 5, NativeSceneSeedTable.Bytes(NativeSceneSeedKind.Defence, 1000000, [2]));

        Assert.False(store.TryGet(7, 4, NativeSceneSeedKind.Critical, out _));
        Assert.False(store.TryGet(7, 5, NativeSceneSeedKind.Critical, out _));
        Assert.True(store.TryGet(7, 5, NativeSceneSeedKind.Defence, out _));
    }

    /// <summary>CN2012's second critical vector remains a separate table field but decodes to one word stream.</summary>
    [Fact]
    public void Cn324CriticalVectorsAreReadInWireOrder()
    {
        var values = new Dictionary<string, object?>
        {
            ["entity_id"] = 1000000,
            ["unknown_8"] = new object?[] { 1, 2 },
            ["unknown_20"] = new object?[] { 3, 4 },
        };
        var bytes = PacketCodec.Write(TestBuilds.Packets, "cn-324", "RelayCriticalSeedTable", values);
        var table = SceneSeedWire.Read(TestBuilds.Packets, "cn-324", NativeSceneSeedKind.Critical,
            bytes, new PacketReadLimits(MaxCollectionElements: 1000, MaxValues: 2048));
        Assert.Equal(1000000, table.EntityId);
        Assert.Equal([1u, 2, 3, 4], table.WordValues);
        Assert.Equal([[1u, 2u], [3u, 4u]], table.WordVectors);
        Assert.Empty(table.ByteValues);
    }

    /// <summary>Each audited seed encoder pins its counted-vector body length and preserves vector boundaries.</summary>
    [Theory]
    [InlineData("kr-812", 24)]
    [InlineData("cn-324", 24)]
    [InlineData("cn-406", 24)]
    [InlineData("cn-717", 24)]
    [InlineData("id-400", 24)]
    [InlineData("id-1809", 24)]
    public void CriticalEncoderPinsLengthForEverySeedTableBuild(string build, int expectedLength)
    {
        var source = NativeSceneSeedTable.WithWordVectors(NativeSceneSeedKind.Critical, 1000000,
            [[1u, 2u], [3u]]);
        var bytes = SceneSeedSender.Encode(TestBuilds.Packets, build, source, "RelayCriticalSeedTable");

        Assert.Equal(expectedLength, bytes.Length);
        var decoded = SceneSeedWire.Read(TestBuilds.Packets, build, NativeSceneSeedKind.Critical,
            bytes, new PacketReadLimits(MaxCollectionElements: 1000, MaxValues: 2048));
        Assert.Equal(source.EntityId, decoded.EntityId);
        Assert.Equal(source.WordVectors, decoded.WordVectors);
    }

    /// <summary>Defence and cancellation encoders use their one-vector native wire shapes.</summary>
    [Fact]
    public void DefenceAndCancellationEncodersPinTheirBodyLengths()
    {
        var defence = SceneSeedSender.Encode(TestBuilds.Packets, "kr-812",
            NativeSceneSeedTable.Bytes(NativeSceneSeedKind.Defence, 1000000, [4, 5, 6]),
            "RelayDefenceSeedTable");
        var cancellation = SceneSeedSender.Encode(TestBuilds.Packets, "kr-812",
            NativeSceneSeedTable.Words(NativeSceneSeedKind.CriticalCancellation, 1000000, [7u, 8u]),
            "RelayCriticalSeedCancelTable");

        Assert.Equal(11, defence.Length);
        Assert.Equal(16, cancellation.Length);
        Assert.Equal([4, 5, 6], SceneSeedWire.Read(TestBuilds.Packets, "kr-812",
            NativeSceneSeedKind.Defence, defence, new PacketReadLimits(1000, 2048)).ByteValues);
        Assert.Equal([7u, 8u], SceneSeedWire.Read(TestBuilds.Packets, "kr-812",
            NativeSceneSeedKind.CriticalCancellation, cancellation, new PacketReadLimits(1000, 2048)).WordValues);
    }
}
