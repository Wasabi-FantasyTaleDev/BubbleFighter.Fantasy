using BubbleFighter.Fantasy.Contracts.Inventory;
using Xunit;

namespace BubbleFighter.Fantasy.InventoryView.Tests;

/// <summary>
/// The inventory catalogs kept in a build's compiled client data read back as what was written; a row may load from
/// the file only because of that.
/// </summary>
public sealed class CompiledCatalogsTests
{
    /// <summary>Every field of every wear group survives, including a group without a member list and an empty one.</summary>
    [Fact]
    public void WearLocationsReadBackEqual()
    {
        InventoryWearGroup[] groups =
        [
            new(100000, 199999, 100001, true),
            new(200000, 200000, 0, false) { RejectWhenOccupied = true, ItemIds = [200000, 5, int.MaxValue] },
            new(-1, int.MinValue, -7, true) { ItemIds = [] },
        ];
        using var section = new MemoryStream();
        WearLocationsCatalog.Instance.Write(groups, section);
        section.Position = 0;
        var read = WearLocationsCatalog.Instance.Read(section);
        Assert.Equal(section.Length, section.Position);

        Assert.Equal(groups.Length, read.Length);
        for (var index = 0; index < groups.Length; index++)
        {
            Assert.Equal(groups[index] with { ItemIds = null }, read[index] with { ItemIds = null });
            Assert.Equal(groups[index].ItemIds, read[index].ItemIds);
        }
        Assert.Null(read[0].ItemIds);
        Assert.Empty(read[2].ItemIds!);

        using var again = new MemoryStream();
        WearLocationsCatalog.Instance.Write(read, again);
        Assert.Equal(section.ToArray(), again.ToArray());
    }

    /// <summary>The displayable item identities survive in order, also when there are none.</summary>
    [Theory]
    [InlineData(new int[0])]
    [InlineData(new[] { 1, 100321, int.MaxValue })]
    public void LoadoutItemsReadBackEqual(int[] ids)
    {
        using var section = new MemoryStream();
        LoadoutItemsCatalog.Instance.Write(ids, section);
        section.Position = 0;
        Assert.Equal(ids, LoadoutItemsCatalog.Instance.Read(section));
        Assert.Equal(section.Length, section.Position);
    }

    /// <summary>A count the section cannot hold is refused before anything is allocated for it.</summary>
    [Fact]
    public void ACountBeyondTheSectionIsRefused()
    {
        Assert.ThrowsAny<Exception>(() => WearLocationsCatalog.Instance.Read(new MemoryStream([0xFF, 0xFF, 0xFF, 0x7F])));
        Assert.ThrowsAny<Exception>(() => LoadoutItemsCatalog.Instance.Read(new MemoryStream([2, 0, 0, 0, 1, 0, 0, 0])));
        using var section = new MemoryStream();
        LoadoutItemsCatalog.Instance.Write([1, 2, 3], section);
        Assert.ThrowsAny<Exception>(() => LoadoutItemsCatalog.Instance.Read(new MemoryStream(section.ToArray()[..^1])));
        // A null in place of the list is not a catalog either.
        Assert.Throws<InvalidDataException>(() => LoadoutItemsCatalog.Instance.Read(
            new MemoryStream(MemoryPack.MemoryPackSerializer.Serialize<int[]?>(null))));
    }
}
