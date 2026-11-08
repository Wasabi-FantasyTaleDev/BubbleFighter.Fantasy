using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins the Area32 strategy's item and elimination bodies used by FinalZone.</summary>
public sealed class FinalZoneTests
{
    /// <summary>Rod requests and acknowledgements keep native stage order; linking has an unaligned 16-bit weapon and counted words.</summary>
    [Fact]
    public void TrapBodiesPinAllocationAndPeerLink()
    {
        FinalZoneTrapBinding.RequireLayouts(TestBuilds.Packets, "kr-1256");
        var request = Convert.FromHexString("40420F0030020000030000000000803F000000C000004040");
        Assert.Equal(24, request.Length);
        var placement = FinalZoneTrapBinding.ReadRequest(TestBuilds.Packets, "kr-1256", request);
        Assert.Equal(new RoundFinalZoneTrapRequested(1000000, 560, 3, 1, -2, 3), placement);
        var spawned = FinalZoneTrapBinding.WriteSpawn(TestBuilds.Packets, "kr-1256", new(65536, placement));
        Assert.Equal(28, spawned.Length);
        Assert.Equal("40420F000000010030020000030000000000803F000000C000004040", Convert.ToHexString(spawned));
        var link = Convert.FromHexString("40420F00000001000100010021170200000007000000FFFFFFFF");
        Assert.Equal(26, link.Length);
        var decoded = FinalZoneTrapBinding.ReadLink(TestBuilds.Packets, "kr-1256", link);
        Assert.Equal((1000000, 65536, 65537, (ushort)5921),
            (decoded.Actor, decoded.FirstObjectId, decoded.SecondObjectId, decoded.WeaponId));
        Assert.Equal(new[] { 7, -1 }, decoded.Unknown16);
        Assert.Equal(link, FinalZoneTrapBinding.WriteLink(TestBuilds.Packets, "kr-1256", decoded));
        foreach (var malformed in new[] { request[..^1], request.Concat(new byte[] { 0 }).ToArray() })
            Assert.Throws<PacketFormatException>(() => FinalZoneTrapBinding.ReadRequest(TestBuilds.Packets, "kr-1256", malformed));
        foreach (var malformed in new[] { link[..^1], link.Concat(new byte[] { 0 }).ToArray() })
            Assert.Throws<PacketFormatException>(() => FinalZoneTrapBinding.ReadLink(TestBuilds.Packets, "kr-1256", malformed));
        BitConverter.GetBytes(float.NaN).CopyTo(request, 12);
        Assert.Throws<PacketFormatException>(() => FinalZoneTrapBinding.ReadRequest(TestBuilds.Packets, "kr-1256", request));
    }

    /// <summary>Live drops omit only the server handle and reject nonfinite coordinates or truncated nested contents.</summary>
    [Fact]
    public void LiveDropPinsItsIndependentDescriptorWithoutObjectHandle()
    {
        var bytes = Convert.FromHexString("01000000A40100000C0000000000803F000000C000004040040000000500000006000000"
            + "010000000200000007000000080000000100000009000000");
        Assert.Equal(60, bytes.Length);
        var item = Assert.Single(FinalZoneBinding.ReadItemsDrop(TestBuilds.Packets, "kr-1256", bytes).Items);
        Assert.Equal((0, 420, 12, 1f, -2f, 3f), (item.ObjectId, item.ItemSpecId, item.Quantity, item.X, item.Y, item.Z));
        Assert.Equal((4, 5, 6, true), (item.Unknown24, item.Unknown28, item.Unknown32, item.Temporary));
        Assert.Equal(new[] { 7, 8 }, item.Reinforcement.Unknown4);
        Assert.Equal(new[] { 9 }, item.Reinforcement.Unknown20);
        Assert.Throws<PacketFormatException>(() => FinalZoneBinding.ReadItemsDrop(TestBuilds.Packets, "kr-1256", bytes[..^1]));
        var invalid = bytes.ToArray();
        BitConverter.GetBytes(float.PositiveInfinity).CopyTo(invalid, 12);
        Assert.Throws<PacketFormatException>(() => FinalZoneBinding.ReadItemsDrop(TestBuilds.Packets, "kr-1256", invalid));
    }

    /// <summary>A map manifest spanning native frame limits keeps every object in order across complete counted packets.</summary>
    [Fact]
    public void ExplicitMapManifestSplitsAtNativeFrameBoundaryWithoutDroppingItems()
    {
        var items = Enumerable.Range(65536, 2000).Select(id => new FinalZoneMapItem(
            id, 420, 1, 0, 0, 0, 0, 0, 0, false, new([], []))).ToArray();
        var bodies = FinalZoneBinding.WriteItemBatches(TestBuilds.Packets, "kr-1256", new(items)).ToArray();
        Assert.Equal(2, bodies.Length);
        Assert.All(bodies, body => Assert.InRange(body.Length, 1, ushort.MaxValue));
        var decoded = bodies.SelectMany(body => (object?[])PacketCodec.Read(
            TestBuilds.Packets, "kr-1256", "Area32ItemsSpawn", body,
            limits: new PacketReadLimits(MaxValues: 40000))["items"]!).Cast<IReadOnlyDictionary<string, object?>>();
        Assert.Equal(items.Select(item => item.ObjectId), decoded.Select(item => (int)item["object_id"]!));
    }

    /// <summary>Explicit item batches retain every descriptor word and both reinforcement vectors.</summary>
    [Fact]
    public void ExplicitItemsPinCountedDescriptorAndReinforcement()
    {
        FinalZoneBinding.RequireItemLayouts(TestBuilds.Packets, "kr-1256");
        var message = new RoundFinalZoneItemsSpawned(
            [new(65536, 420, 12, 1, -2, 3, 4, 5, 6, true, new([7, 8], [9]))]);
        var bytes = FinalZoneBinding.WriteItems(TestBuilds.Packets, "kr-1256", message);
        Assert.Equal(64, bytes.Length);
        Assert.Equal("0100000000000100A40100000C0000000000803F000000C000004040040000000500000006000000"
            + "010000000200000007000000080000000100000009000000", Convert.ToHexString(bytes));
        Assert.Equal(new byte[4], FinalZoneBinding.WriteItems(TestBuilds.Packets, "kr-1256", new([])));
    }

    /// <summary>The seed is three words; pickup and elimination are two words with no optional tail.</summary>
    [Fact]
    public void ItemAndEliminationBodiesMatchTheNativeReaders()
    {
        FinalZoneBinding.RequireLayouts(TestBuilds.Packets, "kr-1256");
        Assert.Equal("785634120002000000000100", Convert.ToHexString(FinalZoneBinding.WriteSeed(TestBuilds.Packets,
            "kr-1256", new(0x12345678, 65536, 512))));
        var pickup = Convert.FromHexString("0000010040420F00");
        Assert.Equal(new RoundFinalZoneItemPickupRequested(65536, 1000000),
            FinalZoneBinding.ReadPickup(TestBuilds.Packets, "kr-1256", pickup));
        Assert.Equal(pickup, FinalZoneBinding.WritePickup(TestBuilds.Packets, "kr-1256", new(65536, 1000000)));
        Assert.Equal("40420F0001000000", Convert.ToHexString(FinalZoneBinding.WriteElimination(TestBuilds.Packets,
            "kr-1256", new(1000000, 1))));
        foreach (var malformed in new[] { pickup[..^1], pickup.Concat(new byte[] { 0 }).ToArray() })
            Assert.Throws<PacketFormatException>(() => FinalZoneBinding.ReadPickup(TestBuilds.Packets, "kr-1256", malformed));
    }

    /// <summary>The effect's three strings precede three native words, and oversized or partial bodies fail decoding.</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void MapEffectPinsStringsVisibilityUnknownWordAndScriptSequence(string build)
    {
        FinalZoneMapEffectBinding.RequireLayout(TestBuilds.Packets, build);
        var message = new RoundFinalZoneMapEffectChanged("#", "fx", "", true, -1, 1234);
        var bytes = FinalZoneMapEffectBinding.Write(TestBuilds.Packets, build, message);
        Assert.Equal(30, bytes.Length);
        Assert.Equal("01000000230002000000660078000000000001000000FFFFFFFFD2040000", Convert.ToHexString(bytes));
        Assert.Equal(message, FinalZoneMapEffectBinding.Read(TestBuilds.Packets, build, bytes));
        foreach (var malformed in new[] { bytes[..^1], bytes.Concat(new byte[] { 0 }).ToArray(), BitConverter.GetBytes(1025) })
            Assert.Throws<PacketFormatException>(() => FinalZoneMapEffectBinding.Read(TestBuilds.Packets, build, malformed));
        var noncanonicalTrue = bytes.ToArray();
        noncanonicalTrue[18] = 2;
        Assert.True(FinalZoneMapEffectBinding.Read(TestBuilds.Packets, build, noncanonicalTrue).Visible);
    }

    /// <summary>The death-drop descriptor retains every word, its text and both counted reinforcement vectors.</summary>
    [Fact]
    public void BackpackDropAndSpawnPinNestedItemContents()
    {
        FinalZoneBinding.RequireBackpackLayouts(TestBuilds.Packets, "kr-1256");
        var drop = new RoundFinalZoneBackpackDropped(1000000, 1, -2, 3, 4, 5, 6, "A",
            [new(420, 7, 12, new([8, 9], [10]))]);
        var bytes = Convert.FromHexString(
            "40420F000000803F000000C00000404004000000050000000600000001000000410001000000"
            + "A4010000070000000C000000020000000800000009000000010000000A000000");
        Assert.Equal(70, bytes.Length);
        var decoded = FinalZoneBinding.ReadBackpackDrop(TestBuilds.Packets, "kr-1256", bytes);
        Assert.Equal(drop with { Items = decoded.Items }, decoded);
        var item = Assert.Single(decoded.Items);
        Assert.Equal((420, 7, 12), (item.ItemSpecId, item.WeaponId, item.Quantity));
        Assert.Equal(new[] { 8, 9 }, item.Reinforcement.Unknown4);
        Assert.Equal(new[] { 10 }, item.Reinforcement.Unknown20);
        var spawned = FinalZoneBinding.WriteBackpackSpawn(TestBuilds.Packets, "kr-1256", new(65536, drop));
        Assert.Equal(74, spawned.Length);
        Assert.Equal(Convert.FromHexString("00000100").Concat(bytes), spawned);
        foreach (var malformed in new[] { bytes[..^1], bytes.Concat(new byte[] { 0 }).ToArray() })
            Assert.Throws<PacketFormatException>(() => FinalZoneBinding.ReadBackpackDrop(TestBuilds.Packets, "kr-1256", malformed));
        var invalidPosition = bytes.ToArray();
        BitConverter.GetBytes(float.NaN).CopyTo(invalidPosition, 4);
        Assert.Throws<PacketFormatException>(() => FinalZoneBinding.ReadBackpackDrop(TestBuilds.Packets, "kr-1256", invalidPosition));
        var unboundedItems = bytes.ToArray();
        BitConverter.GetBytes(int.MaxValue).CopyTo(unboundedItems, 34);
        Assert.Throws<PacketFormatException>(() => FinalZoneBinding.ReadBackpackDrop(TestBuilds.Packets, "kr-1256", unboundedItems));
    }

    /// <summary>The pickup selects a container and specification; the reply prepends the native recipient identity.</summary>
    [Fact]
    public void BackpackPickupAndAcceptedRemovalPinNativeWordOrder()
    {
        var request = Convert.FromHexString("00000100A4010000");
        Assert.Equal(new RoundFinalZoneBackpackPickupRequested(65536, 420),
            FinalZoneBinding.ReadBackpackPickup(TestBuilds.Packets, "kr-1256", request));
        Assert.Equal("0300000000000100A4010000", Convert.ToHexString(FinalZoneBinding.WriteBackpackPicked(
            TestBuilds.Packets, "kr-1256", new(3, 65536, 420))));
        foreach (var malformed in new[] { request[..^1], request.Concat(new byte[] { 0 }).ToArray() })
            Assert.Throws<PacketFormatException>(() => FinalZoneBinding.ReadBackpackPickup(TestBuilds.Packets, "kr-1256", malformed));
    }

    /// <summary>Drone and golden-supply bodies preserve coordinates, opaque words and the trailing handle/seed pair.</summary>
    [Fact]
    public void SeededSuppliesPinEveryNativeWordAndRejectMalformedRequests()
    {
        FinalZoneBinding.RequireSupplyLayouts(TestBuilds.Packets, "kr-1256");
        var drone = new RoundFinalZoneDroneDestroyed(900, 1, -2, 3, 7);
        var destroyed = Convert.FromHexString("840300000000803F000000C00000404007000000");
        Assert.Equal(20, destroyed.Length);
        Assert.Equal(drone, FinalZoneBinding.ReadDroneDestroyed(TestBuilds.Packets, "kr-1256", destroyed));
        var spawn = FinalZoneBinding.WriteDroneSpawn(TestBuilds.Packets, "kr-1256", new(900, 12345));
        Assert.Equal(8, spawn.Length);
        Assert.Equal("8403000039300000", Convert.ToHexString(spawn));
        var box = FinalZoneBinding.WriteDroneBox(TestBuilds.Packets, "kr-1256", new(drone, 65536, 12345));
        Assert.Equal(28, box.Length);
        Assert.Equal(destroyed.Concat(Convert.FromHexString("0000010039300000")), box);
        var golden = new RoundFinalZoneGoldenSupplyRequested(3, 1, -2, 3);
        var request = Convert.FromHexString("030000000000803F000000C000004040");
        Assert.Equal(16, request.Length);
        Assert.Equal(golden, FinalZoneBinding.ReadGoldenSupply(TestBuilds.Packets, "kr-1256", request));
        var response = FinalZoneBinding.WriteGoldenSupply(TestBuilds.Packets, "kr-1256", new(golden, 65536, 12345));
        Assert.Equal(24, response.Length);
        Assert.Equal(request.Concat(Convert.FromHexString("0000010039300000")), response);
        foreach (var malformed in new[] { destroyed[..^1], destroyed.Concat(new byte[] { 0 }).ToArray() })
            Assert.Throws<PacketFormatException>(() => FinalZoneBinding.ReadDroneDestroyed(TestBuilds.Packets, "kr-1256", malformed));
        foreach (var malformed in new[] { request[..^1], request.Concat(new byte[] { 0 }).ToArray() })
            Assert.Throws<PacketFormatException>(() => FinalZoneBinding.ReadGoldenSupply(TestBuilds.Packets, "kr-1256", malformed));
        BitConverter.GetBytes(float.PositiveInfinity).CopyTo(destroyed, 4);
        Assert.Throws<PacketFormatException>(() => FinalZoneBinding.ReadDroneDestroyed(TestBuilds.Packets, "kr-1256", destroyed));
        BitConverter.GetBytes(float.NaN).CopyTo(request, 8);
        Assert.Throws<PacketFormatException>(() => FinalZoneBinding.ReadGoldenSupply(TestBuilds.Packets, "kr-1256", request));
    }

    /// <summary>The water phase and duration are integer words even though the decompiler inferred float arguments.</summary>
    [Fact]
    public void WaterTimerPinsBooleanPhaseAndMillisecondWords()
    {
        Assert.Equal("010000000200000088130000", Convert.ToHexString(FinalZoneBinding.WriteWaterTimer(
            TestBuilds.Packets, "kr-1256", new(true, 2, 5000))));
        Assert.Equal("000000000000000000000000", Convert.ToHexString(FinalZoneBinding.WriteWaterTimer(
            TestBuilds.Packets, "kr-1256", new(false, 0, 0))));
    }
}
