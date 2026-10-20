using BubbleFighter.Fantasy.Contracts.Inventory;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>
/// A life-counted bag slot on the wire: no time limit (the permanent day sentinel) and the life in the slot's
/// +8 count (kr-812 sub_6D6480 and sub_978350 read it for item kind 3), plus which builds have the kind.
/// </summary>
public sealed class LifeItemWireTests
{
    // 201350 = 0x00031286, life 15, then 2958101.0 days (0x4146918A80000000) and 28 zero bytes of flags, string, vector.
    private const string OneLifeSlotHex = "01000000" + "86120300" + "0F000000" + "000000808A914641" + "00000000000000000000000000000000000000000000000000000000";

    [Fact]
    public void ALifeSlotIsFortyEightBytesWithTheLifeInTheCountAndThePermanentDaySentinel()
    {
        var row = new InventorySlotRecord(1, InventoryBag.CharacterBag, 10, 201350, 15, 0) { Lifetime = InventoryLifetime.Life };

        var slot = InventoryBinding.Map(row, DateTimeOffset.UtcNow);
        var bytes = new InventoryWire(TestBuilds.Packets, "kr-812").RepInventoryAll([slot]);

        Assert.Equal(48, bytes.Length);
        Assert.Equal(OneLifeSlotHex, Convert.ToHexString(bytes));
    }

    [Fact]
    public void ALifeSlotWritesTheSameBytesAsAPermanentSlotOfTheSameCount()
    {
        var baseline = DateTimeOffset.UtcNow;
        var life = InventoryBinding.Map(new(1, InventoryBag.CharacterBag, 10, 201350, 15, 0) { Lifetime = InventoryLifetime.Life }, baseline);
        var permanent = InventoryBinding.Map(new(1, InventoryBag.CharacterBag, 10, 201350, 15, 0) { Lifetime = InventoryLifetime.Permanent }, baseline);

        Assert.Equal(permanent, life);
    }

    [Fact]
    public void AnUnclassifiedRowIsShownWithoutATimeLimitInsteadOfFailingTheLogin()
    {
        // A kr-1256 shop purchase stores quantity 99999 with no lifetime; the shared account then logs in on a legacy build.
        var baseline = DateTimeOffset.UtcNow;
        var unclassified = InventoryBinding.Map(new(12, InventoryBag.CharacterBag, 75, 207910, 99999, 0), baseline);
        var permanent = InventoryBinding.Map(new(12, InventoryBag.CharacterBag, 75, 207910, 99999, 0) { Lifetime = InventoryLifetime.Permanent }, baseline);

        Assert.Equal(permanent, unclassified);
    }

    [Theory]
    [InlineData("kr-812", true)]
    [InlineData("cn-717", true)]
    [InlineData("cn-324", false)]
    [InlineData("cn-279", false)]
    [InlineData("id-400", false)]
    [InlineData("id-1809", false)]
    [InlineData("cn-706", false)]
    [InlineData("cn-406", false)]
    [InlineData("cn-404", false)]
    [InlineData("cn-408", false)]
    [InlineData("kr-1256", false)]
    [InlineData("cn-735", false)]
    public void OnlyTheBuildsWhoseClientHasTheEnduranceKindCarryTheCapability(string build, bool expected)
    {
        Assert.Equal(expected, TestBuilds.Packets.CapabilityOf(build, "life_items"));
    }
}
