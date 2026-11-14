using BubbleFighter.Fantasy.Contracts.Inventory;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.MySql.Tests;

/// <summary>Checks that a stored grade-up maps through the existing enhancement column without a schema change.</summary>
public sealed class ItemAdvancementStorageTests
{
    private static readonly InventorySlotRecord Target = new(1, InventoryBag.CharacterBag, 1, 300301, 1, 0)
    { Lifetime = InventoryLifetime.Permanent, ArticleIdentity = new(9, 100000008) };

    /// <summary>The selector rides in the enhancement JSON beside kind and revision; an earlier row without it reads as none.</summary>
    [Fact]
    public void EntityRoundTripPreservesTheGradeUp()
    {
        var slot = Target with { Enhancements = ItemEnhancements.Empty.With("kr-1256", new(ItemEnhancementKind.Clothing, 2, 158)) };
        var row = MySqlInventoryRepository.ToRow(slot, 0);
        Assert.Equal("{\"kr-1256\":{\"Kind\":2,\"Revision\":2,\"Advancement\":158}}", row.EnhancementsJson);
        Assert.Equal(slot, MySqlInventoryRepository.ToRecord(row));
        row.EnhancementsJson = "{\"kr-1256\":{\"Kind\":2,\"Revision\":2}}";
        Assert.Equal(0, MySqlInventoryRepository.ToRecord(row).Enhancements!.Find("kr-1256")!.Advancement);
    }

    /// <summary>A negative stored selector fails loudly instead of becoming no grade-up.</summary>
    [Fact]
    public void AMalformedStoredSelectorIsRejected()
    {
        var row = MySqlInventoryRepository.ToRow(Target, 0);
        foreach (var json in new[] { "{\"kr-1256\":{\"Kind\":0,\"Revision\":1,\"Advancement\":-1}}",
            "{\"kr-1256\":{\"Kind\":0,\"Revision\":1,\"Advancement\":1,\"Advancement\":1}}" })
        {
            row.EnhancementsJson = json;
            Assert.ThrowsAny<Exception>(() => MySqlInventoryRepository.ToRecord(row));
        }
    }
}
