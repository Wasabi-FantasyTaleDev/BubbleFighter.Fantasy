using BubbleFighter.Fantasy.Contracts.Inventory;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.MySql.Tests;

/// <summary>Checks exact metadata encoding and invalid enhancement refusal before database access.</summary>
public sealed class ItemEnhancementStorageTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly InventoryLoadoutScope Scope = new("kr-1256", [300100, 190106]);
    private static readonly InventorySlotRecord Target = new(1, InventoryBag.CharacterBag, 1, 300100, 1, 0)
    { Lifetime = InventoryLifetime.Permanent, ArticleIdentity = new(9, 100000008) };

    /// <summary>Unsigned ownership and scoped revisions survive the ORM entity mapping without changing pet columns.</summary>
    [Fact]
    public void EntityRoundTripPreservesArticleOwnershipAndAllEnhancementScopes()
    {
        var expected = Target with
        {
            ArticleIdentity = new(ulong.MaxValue, long.MaxValue),
            Enhancements = ItemEnhancements.Empty.With("kr-1256", new(ItemEnhancementKind.Weapon, 5))
                .With("other-client", new(ItemEnhancementKind.None, 2)),
        };
        var row = MySqlInventoryRepository.ToRow(expected, 3);
        Assert.Equal("18446744073709551615", row.ArticleEntitlementId);
        Assert.Equal("9223372036854775807", row.ArticleKey);
        Assert.Null(row.PetEntitlementId);
        Assert.Null(row.PetArticleKey);
        Assert.Equal(expected, MySqlInventoryRepository.ToRecord(row));
        Assert.Null(MySqlInventoryRepository.ToRecord(MySqlInventoryRepository.ToRow(Target with { ArticleIdentity = null }, 0)).Enhancements);
    }

    /// <summary>Malformed identity pairs and duplicate or invalid revision JSON never reach native publication.</summary>
    [Fact]
    public void StoredMalformedMetadataIsRejected()
    {
        var row = MySqlInventoryRepository.ToRow(Target, 0);
        row.ArticleKey = null;
        Assert.Throws<InvalidDataException>(() => MySqlInventoryRepository.ToRecord(row));
        row.ArticleKey = "99999999";
        Assert.Throws<InvalidDataException>(() => MySqlInventoryRepository.ToRecord(row));
        row.ArticleKey = "100000008";
        row.EnhancementsJson = "{\"kr-1256\":{\"Kind\":1,\"Revision\":0}}";
        Assert.Throws<InvalidDataException>(() => MySqlInventoryRepository.ToRecord(row));
        row.EnhancementsJson = "{\"kr-1256\":{\"Kind\":1,\"Revision\":1},\"kr-1256\":{\"Kind\":0,\"Revision\":2}}";
        Assert.Throws<System.Text.Json.JsonException>(() => MySqlInventoryRepository.ToRecord(row));
    }

    /// <summary>Command ownership errors, duplicate materials and cancellation fail without opening a connection.</summary>
    [Fact]
    public async Task InvalidOrCancelledCommandsNeverOpenDatabaseConnection()
    {
        var store = new MySqlInventoryRepository(new UnusedFactory());
        var material = new InventorySlotRecord(1, InventoryBag.CharacterBag, 2, 190106, 4, 0);
        var command = new ItemEnhancementCommand([Target], Scope, [new(material, 2)], ItemEnhancementKind.Weapon, true, Now);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.TryEnhanceAsync(command, new(true)).AsTask());
        var invalid = new ItemEnhancementCommand([Target], Scope, [new(material, 2), new(material, 1)], ItemEnhancementKind.Weapon, true, Now);
        await Assert.ThrowsAsync<ArgumentException>(() => store.TryEnhanceAsync(invalid, default).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.InitializeArticlesAsync(new(0, Scope, [300100], Now), default).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.InitializeArticlesAsync(new(1, Scope, [300100], Now), new(true)).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SelectArticleAsync(new(1, Scope, Target, [], true, Now), new(true)).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ReadEnhancementInventoryAsync(1, Scope, new(true)).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.PrepareInventorySnapshotAsync(new(1, Scope), new(true)).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => store.PrepareInventorySnapshotAsync(new(1, Scope)
            { ArticleInitialization = new(2, Scope, [300100], Now) }, default).AsTask());
    }

    private sealed class UnusedFactory : BubbleFighter.Fantasy.Abstractions.Persistence.IDbConnectionFactory
    {
        public ValueTask<System.Data.Common.DbConnection> OpenAsync(CancellationToken ct) =>
            throw new InvalidOperationException("Validation must finish before opening the database.");
    }
}
