using BubbleFighter.Fantasy.InGame.Data;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>Checks native RNG rounding and the supply generator's distinct-row sampling rules.</summary>
public sealed class FinalZoneSupplyTests
{
    /// <summary>Native subtractive draws retain their single-precision scaling, including signed seed boundaries.</summary>
    [Theory]
    [InlineData(0, "72624,81732,76802,55816,20603,55888,90602,44217,97754,27370")]
    [InlineData(1, "24866,11074,46701,77160,65751,43278,35408,94386,10126,64245")]
    [InlineData(12345, "6674,7015,77476,51113,79749,82730,16595,73613,26021,50600")]
    [InlineData(-12345, "6674,7015,77476,51113,79749,82730,16595,73613,26021,50600")]
    [InlineData(int.MinValue, "24866,11074,46701,77160,65751,43278,35408,94386,10126,64245")]
    [InlineData(int.MaxValue, "72624,81732,76802,55816,20603,55888,90602,44217,97754,27370")]
    public void NativeSeededDrawsPinFloatRoundedSequence(int seed, string expected)
    {
        // kr-1256 sub_F99280 and kr-1256 sub_F99330 produce these ten sequential [0,100000) draws.
        var random = new Area32Random(seed);
        Assert.Equal(expected, string.Join(',', Enumerable.Range(0, 10).Select(_ => random.Next(100000))));
    }

    /// <summary>Equal-valued rows remain separate draws, while an exhausted group does not repeat a row.</summary>
    [Fact]
    public void SupplyLotteryRemovesRowIdentityAndKeepsEachGroupIndependent()
    {
        var item = new FinalZoneItemSpec("Fixture", 9001, "GunAmmo", "catFixture", -1, true, true, true);
        var row = new FinalZoneSpawnRule(100, [new(item, new(3, 3))], [], new(0, 0));
        var catalog = new FinalZoneSupplyCatalog(new(3, 3), new(1, 1), [row, row with { }], [row]);
        var rewards = catalog.Generate(12345);
        Assert.Equal(3, rewards.Count);
        Assert.All(rewards, entry =>
        {
            Assert.Equal((9001, -1, 3), (entry.ItemSpecId, entry.WeaponId, entry.Quantity));
            Assert.Empty(entry.Reinforcement.Unknown4);
            Assert.Empty(entry.Reinforcement.Unknown20);
        });
    }
}
