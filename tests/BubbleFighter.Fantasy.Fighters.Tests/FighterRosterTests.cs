using BubbleFighter.Fantasy.Fighters.Data;
using Xunit;

namespace BubbleFighter.Fantasy.Fighters.Tests;

public sealed class FighterRosterTests
{
    [Fact]
    public async Task UnlockEquipsFirstItemAndListsInOrder()
    {
        var roster = new MemoryFighterRoster();
        await roster.UnlockAsync(9, 300001, CancellationToken.None);
        await roster.UnlockAsync(9, 300002, CancellationToken.None);
        Assert.Equal(300001, await roster.GetEquippedAsync(9, CancellationToken.None));
        Assert.Equal(new[] { 300001, 300002 }, await roster.ListUnlockedAsync(9, CancellationToken.None));
        await roster.EquipAsync(9, 300002, CancellationToken.None);
        Assert.Equal(300002, await roster.GetEquippedAsync(9, CancellationToken.None));
    }

    [Fact]
    public async Task EquipWithoutUnlockFailsLoud()
    {
        var roster = new MemoryFighterRoster();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            roster.EquipAsync(1, 300001, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task UnknownUserListsEmpty()
    {
        var roster = new MemoryFighterRoster();
        Assert.Empty(await roster.ListUnlockedAsync(4, CancellationToken.None));
        Assert.Equal(0, await roster.GetEquippedAsync(4, CancellationToken.None));
    }
}
