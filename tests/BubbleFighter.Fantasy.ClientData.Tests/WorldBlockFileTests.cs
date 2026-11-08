using System.Numerics;
using System.Security.Cryptography;
using BubbleFighter.Fantasy.Archives;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.Tests;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.ClientData.Tests;

/// <summary>Exercises shared helper extraction and the generated client-data lookup path.</summary>
public sealed class WorldBlockFileTests
{
    [Fact]
    public void HelpersRetainNamesTransformsAndOffsetsWithoutModeFiltering()
    {
        var bytes = WorldBlockFixture.Create(("#sp_star_1_00", new(1, 2, 3)), ("zombie001", new(4, 5, 6)),
            ("#ic_waterBombZombie_00", new(-7, 8, -9)));
        var helpers = WorldBlockFile.ReadHelpers(bytes, bytes.Length, 3);
        Assert.Equal(new[] { "#sp_star_1_00", "zombie001", "#ic_waterBombZombie_00" }, helpers.Select(helper => helper.Name));
        Assert.Equal(new Vector3(4, 5, 6), helpers[1].Position);
        Assert.All(helpers, helper => Assert.Equal(new Vector3(0.25f, -0.5f, 1.5f), helper.Rotation));
        Assert.Equal(8, helpers[0].Offset);
        Assert.True(helpers[2].Offset > helpers[1].Offset);
    }

    [Fact]
    public void MeshPropertiesAndAnimationPayloadsDoNotBecomeHelpers()
    {
        var bytes = WorldBlockFixture.MeshWithEmbeddedHelper();
        Assert.Equal("#spawn_real", Assert.Single(WorldBlockFile.ReadHelpers(bytes, 1024, 2)).Name);
        Assert.Throws<InvalidDataException>(() => WorldBlockFile.ReadHelpers(bytes[..150], 1024, 2));
    }

    [Fact]
    public void HelpersMayCarryNonemptyPropertiesAndInvalidRecordCountsFail()
    {
        var bytes = WorldBlockFixture.HelperWithProperties("#sp_star_1_00", new(1, 2, 3), "<property>active</property>");
        var helper = Assert.Single(WorldBlockFile.ReadHelpers(bytes, 1024, 2));
        Assert.Equal("<property>active</property>", helper.Properties);
        Assert.Equal(new Vector3(1, 2, 3), helper.Position);
        var mesh = WorldBlockFixture.MeshWithEmbeddedHelper();
        // Header, name/type, properties and xyz precede the material count.
        var countOffset = 8 + 5 + 4 + 16 + 12;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(mesh.AsSpan(countOffset), -1);
        Assert.Throws<InvalidDataException>(() => WorldBlockFile.ReadHelpers(mesh, 1024, 2));
    }

    [Fact]
    public void UnsupportedTruncatedAndOversizedResourcesFailBeforeReturningHelpers()
    {
        var bytes = WorldBlockFixture.Create(("helper", new(1, 2, 3)), ("other", new(4, 5, 6)));
        Assert.Throws<InvalidDataException>(() => WorldBlockFile.ReadHelpers(bytes, bytes.Length - 1, 2));
        Assert.Throws<InvalidDataException>(() => WorldBlockFile.ReadHelpers(bytes, 1024, 1));
        Assert.Throws<InvalidDataException>(() => WorldBlockFile.ReadHelpers(bytes[..^1], 1024, 2));
        bytes[0] = 3;
        Assert.Throws<InvalidDataException>(() => WorldBlockFile.ReadHelpers(bytes, 1024, 2));
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void NonFiniteHelperCoordinatesAreRejected(float axis)
    {
        var bytes = WorldBlockFixture.Create(("#sp_star_1_00", new(axis, 2, 3)));
        Assert.Throws<InvalidDataException>(() => WorldBlockFile.ReadHelpers(bytes, 1024, 2));
    }

    [Fact]
    public void GeneratedNestedTrackIsIndexedAndReadThroughTheRuntimeStore()
    {
        var root = Path.Combine(Path.GetTempPath(), "bff-world-block-" + Guid.NewGuid().ToString("N"));
        try
        {
            var client = Path.Combine(root, "client");
            var generated = Path.Combine(root, "generated");
            Directory.CreateDirectory(Path.Combine(client, "Pack"));
            var bytes = WorldBlockFixture.Create(("#sp_star_1_00", new(10, 20, 30)));
            using (var output = File.Create(Path.Combine(client, "Pack", "Maps0000.pkn")))
                PknWriter.Write(output, "pack/maps0000.pkn",
                    [("Config/ShopItem2.xml", "shop"u8.ToArray()), ("Maps/Area/Nested/track.wb", bytes),
                     ("Maps/Area/Nested/texture.dds", "texture"u8.ToArray())]);
            new ClientDataGenerator().Generate("cn-279", client, generated, 100, 1048576, default);
            using var store = ClientDataStore.Open(Path.Combine(generated, "data"), Path.Combine(generated, "dataraw"), NullLogger.Instance);
            Assert.True(store.Exists("maps/area/nested/TRACK.WB"));
            Assert.Equal(new Vector3(10, 20, 30), Assert.Single(WorldBlockFile.LoadHelpers(store,
                "maps/area/nested/TRACK.WB", 1024, 2)).Position);
            Assert.False(store.Exists("Maps/Area/Nested/texture.dds"));
            var provenance = File.ReadAllText(Path.Combine(generated, "sources.tsv"));
            Assert.Contains("dataraw/Maps/Area/Nested/track.wb\tPack/Maps0000.pkn\tMaps/Area/Nested/track.wb\t", provenance);
            Assert.Contains(Convert.ToHexStringLower(SHA256.HashData(bytes)), provenance);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
