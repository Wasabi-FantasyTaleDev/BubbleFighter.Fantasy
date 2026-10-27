using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins the level-icon change request and the seat record its S2C notification carries.</summary>
public sealed class LevelIconTableTests
{
    /// <summary>The kr-812 request carries the chosen icon, the user number and the current local icon word.</summary>
    [Fact]
    public void Kr812RequestPinsIconUserNumberAndLocalIcon()
    {
        var body = PacketCodec.Write(TestBuilds.Packets, "kr-812", "LevelIconChangeRequest",
            new Dictionary<string, object?> { ["level_icon"] = 3, ["user_number"] = 282, ["unknown_12"] = 0 });

        Assert.Equal(12, body.Length);
        Assert.Equal(Convert.FromHexString("030000001A01000000000000"), body);
        var fields = PacketCodec.ReadScalars(TestBuilds.Packets, "kr-812", "LevelIconChangeRequest", body);
        Assert.Equal(3, Convert.ToInt32(fields["level_icon"], System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(282, Convert.ToInt32(fields["user_number"], System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(0, Convert.ToInt32(fields["unknown_12"], System.Globalization.CultureInfo.InvariantCulture));
        AssertRejectsIncompleteOrTrailing("kr-812", "LevelIconChangeRequest", body);
    }

    /// <summary>The kr-1256 request is one u16; its two callers pass 1 and 0.</summary>
    [Fact]
    public void Kr1256RequestPinsOneWord()
    {
        var body = PacketCodec.Write(TestBuilds.Packets, "kr-1256", "Unknown2007Request",
            new Dictionary<string, object?> { ["unknown_0"] = 1 });

        Assert.Equal(2, body.Length);
        Assert.Equal(Convert.FromHexString("0100"), body);
        var fields = PacketCodec.ReadScalars(TestBuilds.Packets, "kr-1256", "Unknown2007Request", body);
        Assert.Equal(1, Convert.ToInt32(fields["unknown_0"], System.Globalization.CultureInfo.InvariantCulture));
        AssertRejectsIncompleteOrTrailing("kr-1256", "Unknown2007Request", body);
    }

    /// <summary>
    /// The S2C notification body is one seat record: LevelIconUpdate writes the same bytes as UserSlot, and
    /// the icon word sits at the wire offset the default record gives it (kr-812 188 of 192, kr-1256 131 of
    /// 188). The baseline table field order puts it last for kr-812 and before the optional tag for kr-1256.
    /// </summary>
    [Theory]
    [InlineData("kr-812", 192, 188)]
    [InlineData("kr-1256", 188, 131)]
    public void TheCarryingRecordIsTheSeatRecordWithTheIconAtItsWireOffset(string build, int length, int offset)
    {
        var values = new Dictionary<string, object?> { ["level_icon"] = 0x11223344 };
        var update = PacketCodec.Write(TestBuilds.Packets, build, "LevelIconUpdate", values);
        var direct = PacketCodec.Write(TestBuilds.Packets, build, "UserSlot", values);

        Assert.Equal(length, update.Length);
        Assert.Equal(direct, update);
        Assert.Equal(Convert.FromHexString("44332211"), update[offset..(offset + 4)]);

        var defaults = PacketCodec.Write(TestBuilds.Packets, build, "LevelIconUpdate",
            new Dictionary<string, object?>());
        Assert.Equal(length, defaults.Length);
        Assert.Equal(new byte[4], defaults[offset..(offset + 4)]);
    }

    /// <summary>The record wrapper resolves to the seat record the notification body is.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("kr-1256")]
    public void TheUpdateNamesTheSeatRecord(string build)
    {
        var fields = TestBuilds.Packets.Resolve(build, "LevelIconUpdate");
        var record = Assert.Single(fields);
        Assert.Equal("record", record.Name);
        Assert.Equal("UserSlot", record.Type);
        Assert.Contains(TestBuilds.Packets.Resolve(build, "UserSlot"), field => field.Name == "level_icon");
    }

    private static void AssertRejectsIncompleteOrTrailing(string build, string structure, byte[] body)
    {
        for (var length = 0; length < body.Length; length++)
        {
            var prefix = body[..length];
            Assert.Throws<PacketFormatException>(() => PacketCodec.Read(TestBuilds.Packets, build, structure, prefix));
        }
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(TestBuilds.Packets, build, structure, [.. body, 0]));
    }
}
