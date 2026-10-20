using System.Buffers;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins map supply layouts and rejects report counts that exceed the configured bound.</summary>
public sealed class SupplyWireTests
{
    /// <summary>The shared writer preserves the existing scalar replies and native creation defaults.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-324")]
    [InlineData("cn-717")]
    [InlineData("id-400")]
    [InlineData("id-1809")]
    [InlineData("cn-706")]
    public void SharedWriterPreservesSupplyReplies(string build)
    {
        var tables = TestBuilds.All.Tables;
        var words = new Dictionary<string, int>
        {
            ["object_id"] = 0x62000001, ["seat"] = 2, ["kind"] = 109,
            ["item_slot"] = 1, ["result"] = 0,
        };
        var values = words.ToDictionary(pair => pair.Key, pair => (object?)pair.Value);
        foreach (var name in new[] { "SupplyPicked", "SupplyItemGranted", "SupplyItemUsed", "SupplyItemSlotChanged" })
            Assert.Equal(ScalarGolden.Write(tables.Resolve(build, name), words),
                PacketCodec.Write(tables, build, name, values));
        values["x"] = 1f; values["y"] = -2f; values["z"] = 3f;
        Assert.Equal(ScalarGolden.Write(tables.Resolve(build, "SupplyCreate"), words, new(1, -2, 3, 109, 60, 60)),
            PacketCodec.Write(tables, build, "SupplyCreate", values));
    }

    /// <summary>Drop coordinates remain finite and bodies must consume the complete native envelope.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-324")]
    [InlineData("cn-717")]
    [InlineData("id-400")]
    [InlineData("id-1809")]
    [InlineData("cn-706")]
    public void SharedDropReaderRetainsCoordinatesAndBounds(string build)
    {
        var tables = TestBuilds.All.Tables;
        var values = new Dictionary<string, object?> { ["x"] = 1f, ["y"] = -2f, ["z"] = 3f,
            ["kind"] = 109, ["unknown_36"] = 10000 };
        var body = PacketCodec.Write(tables, build, "SupplyDropRequest", values);
        Assert.Equal(new RoundSupplyPoint(1, -2, 3, 109, 0, 0), SupplyBinding.ReadDrop(tables, build, body).Point);
        Assert.Throws<PacketFormatException>(() => SupplyBinding.ReadDrop(tables, build, body[..^1]));
        Assert.Throws<PacketFormatException>(() => SupplyBinding.ReadDrop(tables, build, body.Concat(new byte[1]).ToArray()));
        values["x"] = float.NaN;
        var invalid = PacketCodec.Write(tables, build, "SupplyDropRequest", values);
        Assert.Throws<PacketFormatException>(() => SupplyBinding.ReadDrop(tables, build, invalid));
    }

    /// <summary>Pins the native four-word switch reply and the slot-one grant/use bodies.</summary>
    [Fact]
    public void ItemSwitchReplyPinsItsSelectedSlotAndResult()
    {
        var tables = TestBuilds.All.Tables;
        var reply = ScalarGolden.Write(tables.Resolve("cn-324", "SupplyItemSlotChanged"),
            new() { ["seat"] = 2, ["item_slot"] = 1, ["kind"] = 109, ["result"] = 0 });
        Assert.Equal(16, reply.Length);
        Assert.Equal(Convert.FromHexString("02000000010000006D00000000000000"), reply);
        var rejected = ScalarGolden.Write(tables.Resolve("cn-324", "SupplyItemSlotChanged"),
            new() { ["seat"] = 2, ["item_slot"] = 0, ["kind"] = -1, ["result"] = 1 });
        Assert.Equal(Convert.FromHexString("0200000000000000FFFFFFFF01000000"), rejected);
        foreach (var name in new[] { "SupplyItemGranted", "SupplyItemUsed" })
        {
            var body = ScalarGolden.Write(tables.Resolve("cn-324", name),
                new() { ["seat"] = 2, ["kind"] = 109, ["item_slot"] = 1 });
            Assert.Equal(12, body.Length);
            Assert.Equal(Convert.FromHexString("020000006D00000001000000"), body);
        }
    }
    [Fact]
    public void OldTutorialLoadingReportContainsNoMultiplayerSupplyPoints()
    {
        // Map report immediately after tutorial entry.
        var body = Convert.FromHexString("000000000800000015000000FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000");
        Assert.Equal(96, body.Length);
        Assert.Empty(SupplyBinding.ReadReport(TestBuilds.All.Tables, "cn-324", body, 128).Points);
        Assert.Throws<PacketFormatException>(() => SupplyBinding.ReadReport(TestBuilds.All.Tables, "cn-324", body[..95], 128));
    }
    [Fact]
    public void ReferenceDropRequestPreservesZeroKindAndCreationClassification()
    {
        var body = Convert.FromHexString("84E3EC44C4FE2E438776E144541C7BBFF0043240A0670C3F000000000100000010270000");
        var drop = SupplyBinding.ReadDrop(TestBuilds.All.Tables, "cn-324", body);
        Assert.Equal(36, body.Length);
        Assert.Equal(0, drop.Point.Kind);
        Assert.Equal(10000, drop.CreationClass);
        Assert.Equal(BitConverter.ToSingle(body, 0), drop.Point.X);
        Assert.Throws<PacketFormatException>(() => SupplyBinding.ReadDrop(TestBuilds.All.Tables, "cn-324", body[..35]));
    }
    [Fact]
    public void CreationIsFortyBytesAndPickupIsEightBytes()
    {
        var tables = TestBuilds.All.Tables;
        var created = ScalarGolden.Write(tables.Resolve("cn-324", "SupplyCreate"),
            new() { ["object_id"] = 0x62000001, ["kind"] = 10 }, new(1, -2, 3, 10, 60, 60));
        Assert.Equal("01000062102700000000803F000000C0000040400A00000000000000000000000000000000000000",
            Convert.ToHexString(created));
        var picked = ScalarGolden.Write(tables.Resolve("cn-324", "SupplyPicked"),
            new() { ["seat"] = 2, ["object_id"] = 0x62000001 });
        Assert.Equal("0200000001000062", Convert.ToHexString(picked));
    }

    [Fact]
    public void ItemSlotGrantAndUsePinTheNativeTwelveBytePayloads()
    {
        var tables = TestBuilds.All.Tables;
        var grant = ScalarGolden.Write(tables.Resolve("cn-324", "SupplyItemGranted"),
            new() { ["seat"] = 2, ["kind"] = 101, ["item_slot"] = 0 });
        Assert.Equal(Convert.FromHexString("020000006500000000000000"), grant);
        Assert.Equal(12, grant.Length);
        var used = ScalarGolden.Write(tables.Resolve("cn-324", "SupplyItemUsed"), new() { ["seat"] = 2, ["kind"] = 101, ["item_slot"] = 0 });
        Assert.Equal(Convert.FromHexString("020000006500000000000000"), used);
        Assert.Equal(12, used.Length);
    }

    [Theory]
    [InlineData("cn-324")]
    [InlineData("kr-812")]
    [InlineData("cn-717")]
    [InlineData("id-400")]
    [InlineData("id-1809")]
    [InlineData("cn-406")]
    public void MapReportsDecodeTheSamePointsAcrossDifferentObjectStrides(string build)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new PacketWriter(buffer);
        writer.WriteInt32(0);
        writer.WriteInt32(0);
        writer.WriteInt32(1);
        writer.WriteSingle(1); writer.WriteSingle(-2); writer.WriteSingle(3);
        foreach (var value in new[] { 10, 0, 60, 60 })
        { writer.WriteInt32(1); writer.WriteInt32(value); }
        for (var i = 0; i < 9; i++) writer.WriteInt32(0);
        var report = SupplyBinding.ReadReport(TestBuilds.All.Tables, build, buffer.WrittenSpan, 128);
        Assert.Equal(new RoundSupplyPoint(1, -2, 3, 10, 60, 60), Assert.Single(report.Points));
    }

    [Theory]
    [InlineData("cn-324")]
    [InlineData("kr-812")]
    [InlineData("cn-717")]
    [InlineData("id-400")]
    [InlineData("id-1809")]
    [InlineData("cn-406")]
    public void ItemModeMapAcceptsRandomKindsAndMaximumBeforeMinimum(string build)
    {
        // cn-324 sub_466160 and kr-812 sub_50C840 write the maximum interval first.
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new PacketWriter(buffer);
        writer.WriteInt32(0);
        writer.WriteInt32(8);
        foreach (var actor in new[] { 11, 21, -1, -1, -1, -1, -1, -1 }) writer.WriteInt32(actor);
        writer.WriteInt32(4);
        for (var i = 0; i < 4; i++)
        { writer.WriteSingle(3200 + i); writer.WriteSingle(120); writer.WriteSingle(1700); }
        foreach (var value in new[] { 0, 0, 30000, 15000 })
        {
            writer.WriteInt32(4);
            for (var i = 0; i < 4; i++) writer.WriteInt32(value);
        }
        for (var i = 0; i < 9; i++) writer.WriteInt32(0);
        Assert.Equal(208, buffer.WrittenCount);
        var report = SupplyBinding.ReadReport(TestBuilds.All.Tables, build, buffer.WrittenSpan, 128);
        Assert.Equal(4, report.Points.Count);
        Assert.All(report.Points, point =>
        {
            Assert.Equal(0, point.Kind);
            Assert.Equal(15000, point.MinimumMs);
            Assert.Equal(30000, point.MaximumMs);
        });
    }

    /// <summary>The report's team vector (kr-812 sub_50C840: 0 any team, 1 for a "red" point, 2 for a "blue" one) reaches the points.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-324")]
    [InlineData("cn-717")]
    [InlineData("id-400")]
    [InlineData("id-1809")]
    [InlineData("cn-406")]
    [InlineData("cn-404")]
    [InlineData("cn-408")]
    public void MapReportTeamsReachTheSupplyPoints(string build)
    {
        var tables = TestBuilds.All.Tables;
        Assert.Contains(tables.Resolve(build, "SupplyMapReport"), field => field.Name == "team");
        var unknownPosition = tables.Resolve(build, "SupplyMapReport").First(field => field.Type == "list<SupplyPosition>" && field.Name != "positions").Name;
        var unknownString = tables.Resolve(build, "SupplyMapReport").Single(field => field.Type == "list<wstr>").Name;
        Dictionary<string, object?> Point(float x) => new() { ["x"] = x, ["y"] = 120f, ["z"] = 1700f };
        var fields = new Dictionary<string, object?>
        {
            ["positions"] = new[] { Point(1), Point(2), Point(3) }, ["kinds"] = new[] { 0, 0, 138 }, ["team"] = new[] { 0, 1, 2 },
            ["regeneration_max_ms"] = new[] { 30000, 30000, 30000 }, ["regeneration_min_ms"] = new[] { 15000, 15000, 15000 },
            [unknownPosition] = Array.Empty<Dictionary<string, object?>>(), [unknownString] = Array.Empty<string>(),
        };
        var report = SupplyBinding.ReadReport(tables, build, PacketCodec.Write(tables, build, "SupplyMapReport", fields), 128);
        Assert.Equal(new[] { 0, 1, 2 }, report.Points.Select(point => point.Team));
        Assert.Equal(new[] { 0, 0, 138 }, report.Points.Select(point => point.Kind));
        fields["team"] = new[] { 0, 1, 3 };
        Assert.Throws<PacketFormatException>(() => SupplyBinding.ReadReport(tables, build, PacketCodec.Write(tables, build, "SupplyMapReport", fields), 128));
        fields["team"] = new[] { 0, 1 };
        Assert.Throws<PacketFormatException>(() => SupplyBinding.ReadReport(tables, build, PacketCodec.Write(tables, build, "SupplyMapReport", fields), 128));
    }

    /// <summary>The early client has no team vector in its report and no team word in its creation envelope.</summary>
    [Fact]
    public void EarlySuppliesCarryNoTeam()
    {
        var tables = TestBuilds.All.Tables;
        Assert.DoesNotContain(tables.Resolve("cn-279", "SupplyMapReport"), field => field.Name == "team");
        Assert.DoesNotContain(tables.Resolve("cn-279", "SupplyCreate"), field => field.Name == "team");
        Assert.Contains(tables.Resolve("kr-812", "SupplyCreate"), field => field is { Name: "team", Offset: 40 });
    }

    [Fact]
    public void OversizedMapVectorsAreRejectedBeforeAllocation()
    {
        var body = Convert.FromHexString("0000000081000000");
        Assert.Throws<PacketFormatException>(() => SupplyBinding.ReadReport(TestBuilds.All.Tables, "cn-324", body, 128));
    }

    /// <summary>The early client has one fewer vector and shorter create and drop envelopes.</summary>
    [Fact]
    public void EarlySuppliesUseTheirOwnNativeEnvelopes()
    {
        var tables = TestBuilds.All.Tables;
        var empty = new byte[60]; // One word and fourteen empty vector counts.
        Assert.Empty(SupplyBinding.ReadReport(tables, "cn-279", empty, 128).Points);
        Assert.Throws<PacketFormatException>(() => SupplyBinding.ReadReport(tables, "cn-279", new byte[64], 128));
        var created = PacketCodec.Write(tables, "cn-279", "SupplyCreate", new Dictionary<string, object?>
            { ["object_id"] = 0x62000001, ["kind"] = 10, ["x"] = 1f, ["y"] = -2f, ["z"] = 3f });
        Assert.Equal("01000062102700000000803F000000C0000040400A000000000000000000000000000000", Convert.ToHexString(created));
        var dropped = Convert.FromHexString("0000803F000000C0000040400000000000000000000000000A000000");
        Assert.Equal(28, dropped.Length);
        Assert.Equal(10, PacketCodec.ReadScalars(tables, "cn-279", "SupplyDropRequest", dropped)["kind"]);
        // The request has no classification word; the ordinary throw's value is assumed (cn-279 sub_404180, sub_81FF70).
        var drop = SupplyBinding.ReadDrop(tables, "cn-279", dropped);
        Assert.Equal(new RoundSupplyPoint(1, -2, 3, 10, 0, 0), drop.Point);
        Assert.Equal(10000, drop.CreationClass);
        Assert.Throws<PacketFormatException>(() => SupplyBinding.ReadDrop(tables, "cn-279", dropped[..27]));
        // Grant and use carry the seat and the kind in two words (cn-279 sub_820120, sub_81BB50).
        foreach (var name in new[] { "SupplyItemGranted", "SupplyItemUsed" })
            Assert.Equal("0200000065000000", Convert.ToHexString(PacketCodec.Write(tables, "cn-279", name,
                new Dictionary<string, object?> { ["seat"] = 2, ["kind"] = 101, ["item_slot"] = 1 })));
    }

    /// <summary>Unknown vectors are fully consumed and position entries retain their validation.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-324")]
    [InlineData("cn-279")]
    [InlineData("cn-717")]
    [InlineData("id-400")]
    [InlineData("id-1809")]
    [InlineData("cn-406")]
    public void NestedMapReaderRejectsMismatchedAndMalformedVectors(string build)
    {
        var tables = TestBuilds.All.Tables;
        var unknownPosition = tables.Resolve(build, "SupplyMapReport").First(field => field.Type == "list<SupplyPosition>" && field.Name != "positions").Name;
        var unknownString = tables.Resolve(build, "SupplyMapReport").Single(field => field.Type == "list<wstr>").Name;
        var point = new Dictionary<string, object?> { ["x"] = 1f, ["y"] = -2f, ["z"] = 3f };
        var fields = new Dictionary<string, object?>
        {
            ["positions"] = new[] { point }, ["kinds"] = new[] { 10 },
            ["regeneration_max_ms"] = new[] { 30000 }, ["regeneration_min_ms"] = new[] { 15000 },
            [unknownPosition] = new[] { point }, [unknownString] = new[] { "map marker" },
        };
        // A build with team boxes reports one team word per point.
        if (tables.Resolve(build, "SupplyMapReport").Any(field => field.Name == "team")) fields["team"] = new[] { 0 };
        byte[] Encode() => PacketCodec.Write(tables, build, "SupplyMapReport", fields);
        var body = Encode();
        // cn-279 states no interval (sub_8205F0); the round applies its default to the zero bounds.
        var expected = build == "cn-279" ? new RoundSupplyPoint(1, -2, 3, 10, 0, 0) : new RoundSupplyPoint(1, -2, 3, 10, 15000, 30000);
        var read = SupplyBinding.ReadReport(tables, build, body, 128);
        Assert.Equal(expected, Assert.Single(read.Points));
        Assert.Equal(build != "cn-279", read.IntervalsStated);
        Assert.Throws<PacketFormatException>(() => SupplyBinding.ReadReport(tables, build, body[..^1], 128));
        Assert.Throws<PacketFormatException>(() => SupplyBinding.ReadReport(tables, build, body.Concat(new byte[1]).ToArray(), 128));
        fields["kinds"] = Array.Empty<int>();
        Assert.Throws<PacketFormatException>(() => SupplyBinding.ReadReport(tables, build, Encode(), 128));
        fields["kinds"] = new[] { 10 };
        fields[unknownPosition] = new[] { new Dictionary<string, object?> { ["x"] = float.PositiveInfinity } };
        Assert.Throws<PacketFormatException>(() => SupplyBinding.ReadReport(tables, build, Encode(), 128));
        fields[unknownPosition] = Array.Empty<Dictionary<string, object?>>();
        fields[unknownString] = new[] { new string('x', 1025) };
        Assert.Throws<PacketFormatException>(() => SupplyBinding.ReadReport(tables, build, Encode(), 128));
    }
}
