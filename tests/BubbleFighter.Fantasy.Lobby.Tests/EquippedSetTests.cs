using BubbleFighter.Fantasy.Contracts.Inventory;
using Xunit;

namespace BubbleFighter.Fantasy.Lobby.Tests;

/// <summary>
/// Pins the equipped-set builder the login burst publishes through
/// <see cref="IEquippedViewPush"/>. That triple is sent inside
/// the login burst as well as on room entry, so the character leads the vector
/// and the starter weapons follow.
/// </summary>
public sealed class EquippedSetTests
{
    [Fact]
    public void EquippedSetLeadsWithTheCharacterAndContainsOneWeaponPerPosition()
    {
        var set = StarterInventory.WornItemIds;

        Assert.Equal(new[] { 300001, 202001, 201001, 205001, 207001 }, set);
    }

    [Fact]
    public void EquippedSetIsNeverEmptySoTheRecordAlwaysCarriesAnItem()
    {
        var set = StarterInventory.WornItemIds;

        Assert.NotEmpty(set);
    }
}
