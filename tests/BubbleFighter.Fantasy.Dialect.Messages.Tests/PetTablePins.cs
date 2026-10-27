using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>
/// Pins the pet packets before any binding exists: the legacy evolve reply of
/// <c>packets/pet.yaml</c> (kr-812 and cn-717), its different kr-1256 body at the same name, and the
/// kr-1256 follower level-up and ability-change replies. A body that changes on purpose updates its
/// pin together with the client reader that decides it.
/// </summary>
public sealed class PetTablePins
{
    private static readonly PacketTables Tables = PacketTables.Load(TestBuilds.PacketsDirectory());

    /// <summary>The legacy evolve reply is three four-byte words in both builds whose reader was read.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-717")]
    public void LegacyEvolveResultPinsThreeWords(string build)
    {
        var body = PacketCodec.Write(Tables, build, "PetEvolveResult", new Dictionary<string, object?>
        {
            ["unknown_4"] = 1,
            ["unknown_8"] = 2,
            ["unknown_12"] = 3,
        });
        Assert.Equal(12, body.Length);
        Assert.Equal("010000000200000003000000", Convert.ToHexString(body));

        var fields = PacketCodec.Read(Tables, build, "PetEvolveResult", body);
        Assert.Equal(3, fields.Count);
        Assert.Equal(body, PacketCodec.Write(Tables, build, "PetEvolveResult", fields));
        AssertRejectsIncompleteOrTrailing(build, "PetEvolveResult", body);
    }

    /// <summary>The kr-1256 evolve reply is a word, an eight-byte value, an optional word and a counted word list.</summary>
    [Fact]
    public void Kr1256EvolveResultPinsTheTaggedExtraWord()
    {
        var absent = PacketCodec.Write(Tables, "kr-1256", "PetEvolveResultModern", new Dictionary<string, object?>
        {
            ["unknown_0"] = 1,
            ["unknown_8"] = 0x1122334455667788L,
            ["unknown_16"] = null,
        });
        Assert.Equal(17, absent.Length);
        Assert.Equal("0100000088776655443322110000000000", Convert.ToHexString(absent));
        var readAbsent = PacketCodec.Read(Tables, "kr-1256", "PetEvolveResultModern", absent);
        Assert.Equal(absent, PacketCodec.Write(Tables, "kr-1256", "PetEvolveResultModern", readAbsent));

        var present = PacketCodec.Write(Tables, "kr-1256", "PetEvolveResultModern", new Dictionary<string, object?>
        {
            ["unknown_0"] = 1,
            ["unknown_8"] = 0x1122334455667788L,
            ["unknown_16"] = new Dictionary<string, object?> { ["unknown_0"] = 5 },
            ["unknown_24"] = new uint[] { 6, 7 },
        });
        Assert.Equal(29, present.Length);
        Assert.Equal("0100000088776655443322113A05000000020000000600000007000000", Convert.ToHexString(present));
        var readPresent = PacketCodec.Read(Tables, "kr-1256", "PetEvolveResultModern", present);
        Assert.Equal(present, PacketCodec.Write(Tables, "kr-1256", "PetEvolveResultModern", readPresent));
        AssertRejectsIncompleteOrTrailing("kr-1256", "PetEvolveResultModern", absent);
        AssertRejectsIncompleteOrTrailing("kr-1256", "PetEvolveResultModern", present);
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(Tables, "kr-1256", "PetEvolveResultModern", absent[..13]));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(Tables, "kr-1256", "PetEvolveResultModern", present[..17]));
    }

    /// <summary>The level-up reply is one eight-byte word and one four-byte word.</summary>
    [Fact]
    public void Kr1256FollowerLevelUpPinsTwelveBytes()
    {
        var body = PacketCodec.Write(Tables, "kr-1256", "PetFollowerLevelUp", new Dictionary<string, object?>
        {
            ["unknown_0"] = 0x1122334455667788L,
            ["unknown_8"] = 9,
        });
        Assert.Equal(12, body.Length);
        Assert.Equal("887766554433221109000000", Convert.ToHexString(body));
        var fields = PacketCodec.Read(Tables, "kr-1256", "PetFollowerLevelUp", body);
        Assert.Equal(body, PacketCodec.Write(Tables, "kr-1256", "PetFollowerLevelUp", fields));
        AssertRejectsIncompleteOrTrailing("kr-1256", "PetFollowerLevelUp", body);
    }

    /// <summary>The ability-change reply is one eight-byte word and four four-byte words.</summary>
    [Fact]
    public void Kr1256FollowerAbilityChangePinsTwentyFourBytes()
    {
        var body = PacketCodec.Write(Tables, "kr-1256", "PetFollowerAbilityChange", new Dictionary<string, object?>
        {
            ["unknown_0"] = 1L,
            ["result"] = 0,
            ["unknown_12"] = 2,
            ["unknown_16"] = 3,
            ["unknown_20"] = 4,
        });
        Assert.Equal(24, body.Length);
        Assert.Equal("010000000000000000000000020000000300000004000000", Convert.ToHexString(body));
        var fields = PacketCodec.Read(Tables, "kr-1256", "PetFollowerAbilityChange", body);
        Assert.Equal(5, fields.Count);
        Assert.Equal(body, PacketCodec.Write(Tables, "kr-1256", "PetFollowerAbilityChange", fields));
        AssertRejectsIncompleteOrTrailing("kr-1256", "PetFollowerAbilityChange", body);
    }

    private static void AssertRejectsIncompleteOrTrailing(string build, string structure, byte[] body)
    {
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(Tables, build, structure, body[..^1]));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(Tables, build, structure, [.. body, 0]));
    }
}
