using System.Text.Json;
using BubbleFighter.Fantasy.Contracts.Inventory;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.MySql.Tests;

/// <summary>Checks modification metadata mapping and validation without opening a database connection.</summary>
public sealed class ItemModificationStorageTests
{
    private static readonly InventoryLoadoutScope Scope = new("kr-1256", [300100, 190200]);
    private static readonly InventorySlotRecord Target = new(1, InventoryBag.CharacterBag, 1, 300100, 1, 0)
    { Lifetime = InventoryLifetime.Permanent, ArticleIdentity = new(9, 100000008) };

    /// <summary>Signed effect pairs, group identities and empty historical revisions survive ORM mapping alongside enchantment.</summary>
    [Fact]
    public void EntityRoundTripPreservesIndependentModificationScopesAndEnchantments()
    {
        var slot = Target with
        {
            Enhancements = ItemEnhancements.Empty.With(Scope.Key, new(ItemEnhancementKind.Weapon, 2)),
            Modifications = ItemModifications.Empty.With(Scope.Key, new(7,
                [new(101, 160, -40, 0, 0), new(102, int.MaxValue, int.MinValue, 2, 1)])).With("cn-774", new(5, [])),
        };
        var row = MySqlInventoryRepository.ToRow(slot, 0);
        Assert.NotNull(row.ModificationsJson);
        Assert.Equal(slot, MySqlInventoryRepository.ToRecord(row));
        Assert.Null(MySqlInventoryRepository.ToRow(Target, 0).ModificationsJson);
    }

    /// <summary>Invalid persisted fields and non-addressable groups are refused before inventory publication.</summary>
    [Fact]
    public void StoredMalformedModificationMetadataIsRejected()
    {
        var row = MySqlInventoryRepository.ToRow(Target, 0);
        row.ModificationsJson = "null";
        Assert.Throws<InvalidDataException>(() => MySqlInventoryRepository.ToRecord(row));
        row.ModificationsJson = "{\"kr-1256\":{\"Revision\":0,\"Attributes\":[]}}";
        Assert.Throws<InvalidDataException>(() => MySqlInventoryRepository.ToRecord(row));
        row.ModificationsJson = "{\"kr-1256\":{\"Revision\":1,\"Attributes\":[{\"Id\":101,\"Value0\":1,\"Value1\":-1,\"Group\":3,\"NumericTag\":0}]}}";
        Assert.Throws<InvalidDataException>(() => MySqlInventoryRepository.ToRecord(row));
        row.ModificationsJson = "{\"kr-1256\":{\"Revision\":1,\"Attributes\":[],\"Unexpected\":1}}";
        Assert.Throws<JsonException>(() => MySqlInventoryRepository.ToRecord(row));
    }

    /// <summary>Invalid operations, material overlap and cancellation are rejected before obtaining a database connection.</summary>
    [Fact]
    public async Task InvalidOrCancelledPlansNeverOpenADatabaseConnection()
    {
        var store = new MySqlInventoryRepository(new UnusedFactory());
        var material = new InventorySlotRecord(1, InventoryBag.CharacterBag, 2, 190200, 3, 0);
        var command = new ItemModificationCommand(Target, Scope, [new(material, 3)], new(101, 160, -40, 0, 0), null, 0, DateTimeOffset.UtcNow);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.TryModifyAsync(command, new(true)).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => store.TryModifyAsync(command with { DeletedGroup = 0 }, default).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => store.TryModifyAsync(command with { LucciCost = -1 }, default).AsTask());
        var repeated = new ItemModificationCommand(Target, Scope, [new(material, 1), new(material, 2)], new(101, 160, -40, 0, 0), null, 0, DateTimeOffset.UtcNow);
        await Assert.ThrowsAsync<ArgumentException>(() => store.TryModifyAsync(repeated, default).AsTask());
        var overlap = new ItemModificationCommand(Target, Scope, [new(Target, 1)], new(101, 160, -40, 0, 0), null, 0, DateTimeOffset.UtcNow);
        await Assert.ThrowsAsync<ArgumentException>(() => store.TryModifyAsync(overlap, default).AsTask());
    }

    private sealed class UnusedFactory : BubbleFighter.Fantasy.Abstractions.Persistence.IDbConnectionFactory
    {
        public ValueTask<System.Data.Common.DbConnection> OpenAsync(CancellationToken ct) =>
            throw new InvalidOperationException("Validation must finish before opening the database.");
    }
}
