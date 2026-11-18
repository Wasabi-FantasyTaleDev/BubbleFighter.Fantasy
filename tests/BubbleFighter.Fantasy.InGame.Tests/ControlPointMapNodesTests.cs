using System.Numerics;
using System.Text;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.InGame.Data;
using BubbleFighter.Fantasy.Tests;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>Checks which world-block material tags can authorize a room objective.</summary>
public sealed class ControlPointMapNodesTests
{
    private const string Decoy = "<property><nodePoint02/></property>";

    /// <summary>The native map menu offers these stages; the selected stage determines which snapshot rows may earn points.</summary>
    [Theory]
    [Trait("Category", "LocalClientData")]
    [InlineData("kr-1256", 5, true, false)]
    [InlineData("cn-774", 5, true, true)]
    [InlineData("tw-454", 3, false, true)]
    public void ReleasedMapsContainOnlyTheirPlayableObjectives(string build, int capacity, bool single, bool triple)
    {
        HashSet<string> verified = new(StringComparer.OrdinalIgnoreCase);
        if (single)
            foreach (var stage in new[] { "rel_cp03_hotSprings", "rel_cp04_train" }) Check(stage, 1);
        if (triple)
            foreach (var stage in new[] { "rel_Desert02_Castle", "rel_fairytale04_Pig", "rel_Ice07_Santavillage",
                "rel_pirate07_Treasure", "rel_Space03_Garage", "rel_Stadium04_Extreme", "rel_Stadium07_Seven",
                "rel_CP01_SquareStadium", "rel_CP02_Ruins" }) Check(stage, 7);
        var meta = MapMetaTable.Parse(LocalClientData.Bytes(build, MapMetaTable.Path));
        var released = meta.Maps.Where(key => meta.IsSet(key, "isRelease") && meta.IsSet(key, "isControlPointModeMap"))
            .Select(key => meta.Get(key, "Directory") is { Length: > 0 } directory ? directory : key);
        Assert.True(verified.SetEquals(released), $"The verified Control Point stages differ from {build}'s released map menu.");

        void Check(string stage, int mask)
        {
            verified.Add(stage);
            var file = LocalClientData.Bytes(build, $"Maps/{stage}/track.wb");
            Assert.Equal(mask, ControlPointMapNodes.Load(new StageStore(file), stage, capacity));
        }
    }

    /// <summary>Replaying a round does not reopen the world block; a different or broken arena cannot grant usable nodes.</summary>
    [Fact]
    public void OnlyReleasedStagesReadWorldBlocksAndSuccessAndFailureAreSharedAcrossRooms()
    {
        List<string> reads = [], unavailable = [];
        var catalog = new ControlPointStageCatalog(["Arena", "Missing"], stage =>
        {
            reads.Add(stage);
            return stage == "Arena" ? 5 : throw new FileNotFoundException();
        }, (stage, _) => unavailable.Add(stage));
        Assert.Equal(0, catalog.Nodes("other"));
        Assert.Equal(5, catalog.Nodes("arena"));
        Assert.Equal(5, catalog.Nodes("Arena"));
        Assert.Equal(0, catalog.Nodes("Missing"));
        Assert.Equal(0, catalog.Nodes("missing"));
        Assert.Equal(new[] { "Arena", "Missing" }, reads);
        Assert.Equal("Missing", Assert.Single(unavailable));
    }

    /// <summary>Only direct property children of a mesh material identify nodes, including duplicated and noncontiguous IDs.</summary>
    [Fact]
    public void UsesOnlyMeshMaterialChildrenAndKeepsTheActualSparseIds()
    {
        var bytes = Track("<property><nodePoint03/><ignoreBullet/></property>",
            "<property><!-- <nodePoint02/> --><other><nodePoint02/></other><nodePoint01/><nodePoint03/></property>");
        var store = new StageStore(bytes);
        Assert.Equal(0b101, ControlPointMapNodes.Load(store, "arena/round_1", 5, bytes.Length));
        Assert.Equal(("Maps/arena/round_1/track.wb", bytes.Length), Assert.Single(store.Reads));
    }

    /// <summary>Maps that supply fewer objectives than the dialect's capacity retain only their actual marker bits.</summary>
    [Theory]
    [InlineData("<property><nodePoint01/></property>", 5, 1)]
    [InlineData("<property><nodePoint03/></property>", 3, 4)]
    public void SnapshotCapacityDoesNotCreateMissingObjectives(string material, int capacity, int expected) =>
        Assert.Equal(expected, ControlPointMapNodes.Load(new StageStore(Track(material)), "stage", capacity));

    /// <summary>Invalid marker numbers, malformed property XML and absent objectives prevent admission to the stage.</summary>
    [Theory]
    [InlineData("<property><nodePoint/></property>")]
    [InlineData("<property><nodePoint00/></property>")]
    [InlineData("<property><nodePoint04/></property>")]
    [InlineData("<property><nodePoint1x/></property>")]
    [InlineData("<property><nodePoint01/>")]
    [InlineData("<property><NodePoint01/></property>")]
    [InlineData("<property><ignoreBullet/></property>")]
    [InlineData("<!DOCTYPE property [<!ENTITY n '1'>]><property><nodePoint01/></property>")]
    public void RejectsMalformedUnknownOrEmptyObjectiveSets(string material) =>
        Assert.Throws<InvalidDataException>(() => ControlPointMapNodes.Load(new StageStore(Track(material)), "stage", 3));

    /// <summary>A valid tag cannot conceal a truncated animation trailer or bypass the size limit.</summary>
    [Fact]
    public void RejectsOversizedAndTruncatedResourcesEvenAfterFindingANode()
    {
        var bytes = Track("<property><nodePoint01/></property>");
        Assert.Throws<InvalidDataException>(() => ControlPointMapNodes.Load(new StageStore(bytes), "stage", 5, bytes.Length - 1));
        Assert.Throws<InvalidDataException>(() => ControlPointMapNodes.Load(new StageStore(bytes[..^1]), "stage", 5));
    }

    /// <summary>Absolute, malformed and traversing stage directories never reach the data store.</summary>
    [Theory]
    [InlineData("../stage")]
    [InlineData("stage/../other")]
    [InlineData("/stage")]
    [InlineData("stage\\other")]
    [InlineData("stage//other")]
    [InlineData("C:stage")]
    public void RejectsUnsafeDirectoriesBeforeCallingTheStore(string directory)
    {
        var store = new StageStore([]);
        Assert.Throws<InvalidDataException>(() => ControlPointMapNodes.Load(store, directory, 5));
        Assert.Empty(store.Reads);
    }

    private static byte[] Track(params string[] properties)
    {
        using var buffer = new MemoryStream();
        using var w = new BinaryWriter(buffer, Encoding.Latin1, leaveOpen: true);
        w.Write(WorldBlockFixture.HelperWithProperties("helper", Vector3.One, Decoy));
        foreach (var property in properties)
        {
            WriteString(w, "mesh"); w.Write(0); WriteString(w, Decoy);
            w.Write(new byte[12]); w.Write(2); w.Write(0); w.Write(1);
            WriteString(w, "No Texture");
            WriteString(w, "surface.tga"); w.Write(0); WriteString(w, Decoy);
            w.Write(new byte[36]); WriteString(w, property);
            w.Write(1); WriteString(w, Decoy); w.Write(new byte[36]);
            w.Write(2); w.Write(0); w.Write(new byte[32]);
            w.Write(new byte[20]);
        }
        return buffer.ToArray();
    }

    private static void WriteString(BinaryWriter writer, string text)
    {
        var bytes = Encoding.Latin1.GetBytes(text);
        writer.Write(checked((byte)bytes.Length));
        writer.Write(bytes);
    }

    private sealed class StageStore(byte[] data) : IClientDataStore
    {
        internal List<(string Path, int Limit)> Reads { get; } = [];
        /// <inheritdoc/>
        public bool Exists(string path) => true;
        /// <inheritdoc/>
        public byte[] Read(string path) => throw new InvalidOperationException("A stage read must specify its byte limit.");
        /// <inheritdoc/>
        public byte[] Read(string path, int maximumBytes)
        {
            Reads.Add((path, maximumBytes));
            return data.Length <= maximumBytes ? data : throw new InvalidDataException("Resource exceeds the requested limit.");
        }
    }
}
