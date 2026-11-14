using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins the audited pet request, reply and article layouts against their native byte sequences.</summary>
public sealed class PetTablePins
{
    private static PacketTables Tables => TestBuilds.Packets;

    /// <summary>The legacy consumer reads a signed outcome, ordinary item id and unused final word.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-717")]
    [InlineData("cn-404")]
    [InlineData("cn-406")]
    [InlineData("cn-408")]
    [InlineData("cn-706")]
    [InlineData("id-400")]
    [InlineData("id-1809")]
    public void LegacyEvolutionPinsThreeWords(string build) => Pin(build, "PetEvolveResult", new()
    {
        ["outcome"] = -2, ["target"] = 2, ["unknown_12"] = 3,
    }, "FEFFFFFF0200000003000000");

    /// <summary>The Korean and Chinese modern replies always retain the list count after the optional tag.</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    public void ModernEvolutionPinsAbsentAndPresentOptionalWords(string build)
    {
        Pin(build, "PetEvolveResult", new()
        {
            ["outcome"] = 1, ["target"] = 0x1122334455667788L,
        }, "010000008877665544332211BA00000000");
        Pin(build, "PetEvolveResult", new()
        {
            ["outcome"] = 0, ["target"] = 0x1122334455667788L,
            ["unknown_16"] = new Dictionary<string, object?> { ["unknown_0"] = 5 },
            ["rewards"] = new uint[] { 6, 7 },
        }, "0000000088776655443322113A05000000020000000600000007000000");
    }

    /// <summary>Taiwan has a fixed final word after the full-width target and no optional tag or reward vector.</summary>
    [Fact]
    public void TaiwanEvolutionPinsSixteenBytes() => Pin("tw-454", "PetEvolveResult", new()
    {
        ["outcome"] = 1, ["target"] = 0x1122334455667788L, ["unknown_16"] = 3,
    }, "01000000887766554433221103000000");

    /// <summary>Every audited dedicated level-up request carries only the full target identity.</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void LevelUpRequestPinsEightBytes(string build) => Pin(build, "PetLevelUpRequest", new()
    {
        ["target"] = -1L,
    }, "FFFFFFFFFFFFFFFF");

    /// <summary>The ability request carries both selectors and the actual selection consumable item id.</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    public void AbilityChangeRequestPinsTwentyBytes(string build) => Pin(build, "PetAbilityChangeRequest", new()
    {
        ["target"] = 0x1122334455667788L, ["old_selector"] = 1,
        ["new_selector"] = 2, ["consumable_item_id"] = 3,
    }, "8877665544332211010000000200000003000000");

    /// <summary>The level-up status is one eight-byte identity followed by one four-byte result.</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void LevelUpReplyPinsTwelveBytes(string build) => Pin(build, "PetFollowerLevelUp", new()
    {
        ["target"] = 0x1122334455667788L, ["result"] = 19,
    }, "887766554433221113000000");

    /// <summary>The selected ability is the third word after the target, between two uninterpreted words.</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    public void AbilityChangeReplyPinsTwentyFourBytes(string build) => Pin(build, "PetFollowerAbilityChange", new()
    {
        ["target"] = 1L, ["result"] = 0, ["unknown_12"] = 2,
        ["selector"] = 3, ["unknown_20"] = 4,
    }, "010000000000000000000000020000000300000004000000");

    /// <summary>A nonempty article preserves the four-byte equipped flag and all four native attribute words.</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void ArticlePinsIdentityFlagAndAttributeVector(string build)
    {
        var row = Article();
        const string rowHex = "88776655443322110C000000010000000100000001000000"
            + "0C00000007000000040000000200000003000000040000000500000000000000010000004100";
        Pin(build, "ArticleSlot", row, rowHex);
        Pin(build, "ArticleSlots", new() { ["articles"] = new[] { row } }, "01000000" + rowHex);
        if (build == "kr-1256")
            Pin(build, "InventorySlots", new()
            {
                ["articles"] = new[] { row }, ["removed_item_ids"] = new[] { 12 },
            }, "0000000001000000" + rowHex + "010000000C000000");
    }

    private static Dictionary<string, object?> Article() => new()
    {
        ["article_key"] = 0x1122334455667788L, ["item_id"] = 12, ["state"] = 1, ["equipped"] = true,
        ["attributes"] = new Dictionary<string, object?>[]
        {
            new()
            {
                ["unknown_4"] = 12, ["unknown_8"] = 7, ["unknown_12"] = new[] { 2, 3, 4, 5 },
                ["unknown_28"] = 0, ["unknown_32"] = "A",
            },
        },
    };

    private static void Pin(string build, string structure, Dictionary<string, object?> values, string expected)
    {
        var body = PacketCodec.Write(Tables, build, structure, values);
        Assert.Equal(expected.Length / 2, body.Length);
        Assert.Equal(expected, Convert.ToHexString(body));
        var fields = PacketCodec.Read(Tables, build, structure, body);
        Assert.Equal(body, PacketCodec.Write(Tables, build, structure, fields));
        for (var length = 0; length < body.Length; length++)
            Assert.Throws<PacketFormatException>(() => PacketCodec.Read(Tables, build, structure, body[..length]));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(Tables, build, structure, [.. body, 0]));
    }
}
