using System.Text.Json;
using System.Text.Json.Nodes;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Shop;
using MySqlConnector;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.MySql.Tests;

/// <summary>Checks database entity encoding and pre-connection pet command validation for MySQL.</summary>
public sealed class PetStorageTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly PetProgression Progression = new(2, 0, Now.AddHours(1), Now,
        new(1, 4, 0, 0, 0), new(46, 1, 2, 3, 4));
    private static readonly InventorySlotRecord Pet = new(1, InventoryBag.CharacterBag, 10, 301001, 1, 0x81)
        { Lifetime = InventoryLifetime.Permanent, Pet = PetProgressions.Empty.With("kr-812", Progression) };

    /// <summary>The original snapshot member binds by its CLR signature and still validates before opening a connection.</summary>
    [Fact]
    public async Task OriginalSnapshotSignatureBindsWithoutOpeningAConnection()
    {
        var method = typeof(MySqlInventoryRepository).GetMethod(nameof(IInventoryLoadoutStore.UpdateLoadoutSelectionSnapshotAsync),
            [typeof(long), typeof(InventoryLoadoutScope), typeof(IReadOnlyList<InventoryWearGroup>), typeof(int?),
                typeof(bool), typeof(CancellationToken), typeof(PetInitializationCommand)]);
        Assert.NotNull(method);
        Assert.True(method.GetParameters()[6].IsOptional);
        Assert.Null(method.GetParameters()[6].DefaultValue);
        var store = new MySqlInventoryRepository(new UnusedFactory());
        var select = method.CreateDelegate<Func<long, InventoryLoadoutScope, IReadOnlyList<InventoryWearGroup>, int?,
            bool, CancellationToken, PetInitializationCommand?, ValueTask<PetInventorySnapshot?>>>(store);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => select(0, new("kr-812", [301001]), [],
            301001, true, default, null).AsTask());
    }

    /// <summary>Database entity conversion preserves progression structure and historical rows with no pet state.</summary>
    [Fact]
    public void EntityRoundTripPreservesStructuralEqualityAndNullableHistoricalState()
    {
        var row = MySqlInventoryRepository.ToRow(Pet, 4);
        Assert.NotNull(row.PetJson);
        var restored = MySqlInventoryRepository.ToRecord(row);
        Assert.Equal(Pet, restored);
        Assert.NotSame(Pet.Pet, restored.Pet);
        Assert.NotSame(Progression.FirstSkill, restored.Pet!.Find("kr-812")!.FirstSkill);
        var legacy = Pet with { Pet = null };
        Assert.Null(MySqlInventoryRepository.ToRow(legacy, 4).PetJson);
        Assert.Equal(legacy, MySqlInventoryRepository.ToRecord(MySqlInventoryRepository.ToRow(legacy, 4)));
    }

    /// <summary>Missing persisted pet fields are rejected instead of becoming valid default values.</summary>
    [Theory]
    [InlineData("Grade")]
    [InlineData("Experience")]
    [InlineData("LastFedAt")]
    [InlineData("SecondSkill")]
    public void MissingPersistedFieldsCannotDeserializeAsValidDefaults(string missing)
    {
        var row = MySqlInventoryRepository.ToRow(Pet, 0);
        var json = JsonNode.Parse(row.PetJson!)!.AsObject();
        json["kr-812"]!.AsObject().Remove(missing);
        row.PetJson = json.ToJsonString();
        Assert.Throws<JsonException>(() => MySqlInventoryRepository.ToRecord(row));
    }

    /// <summary>Malformed progression JSON fails on unknown fields, incomplete skills and invalid stages.</summary>
    [Fact]
    public void UnknownFieldsTruncatedSkillAndCorruptStagesFailOnRead()
    {
        var row = MySqlInventoryRepository.ToRow(Pet, 0);
        var json = JsonNode.Parse(row.PetJson!)!;
        json["kr-812"]!["FirstSkill"]!.AsObject().Remove("Value3");
        row.PetJson = json.ToJsonString();
        Assert.Throws<JsonException>(() => MySqlInventoryRepository.ToRecord(row));
        row = MySqlInventoryRepository.ToRow(Pet, 0);
        json = JsonNode.Parse(row.PetJson!)!;
        json["kr-812"]!["Surprise"] = 1;
        row.PetJson = json.ToJsonString();
        Assert.Throws<JsonException>(() => MySqlInventoryRepository.ToRecord(row));
        json = JsonNode.Parse(MySqlInventoryRepository.ToRow(Pet, 0).PetJson!)!;
        json["kr-812"]!["Grade"] = 3;
        row.PetJson = json.ToJsonString();
        Assert.Throws<InvalidDataException>(() => MySqlInventoryRepository.ToRecord(row));
        row.PetJson = "null";
        Assert.Throws<InvalidDataException>(() => MySqlInventoryRepository.ToRecord(row));
        row.PetJson = new string(' ', 256 * 2048 + 1);
        Assert.Throws<InvalidDataException>(() => MySqlInventoryRepository.ToRecord(row));
    }

    /// <summary>Insert commands bind progression JSON while ordinary renewal preserves the existing pet column.</summary>
    [Fact]
    public void InsertBindsPetJsonAndRenewalLeavesTheStoredPetColumnUntouched()
    {
        using var db = SugarSession.Create(new MySqlConnection("Server=unused;Database=bf_game_db"));
        var row = MySqlInventoryRepository.ToRow(Pet, 0);
        var insert = db.Insertable(row).ToSql();
        Assert.Contains("`pet_json`", insert.Key, StringComparison.Ordinal);
        Assert.Contains(insert.Value, parameter => Equals(parameter.Value, row.PetJson));
        var renewal = MySqlShopPurchaseStore.SlotUpdate(db, Pet with { Quantity = 2 }).ToSql();
        Assert.DoesNotContain("pet_json", renewal.Key, StringComparison.Ordinal);
    }

    /// <summary>Invalid or canceled growth plans fail before a database connection is opened.</summary>
    [Fact]
    public async Task InvalidAndCancelledPlansFailBeforeOpeningTheConnection()
    {
        var store = new MySqlInventoryRepository(new UnusedFactory());
        var command = new PetMutationCommand(Pet, new("kr-812", [301001, 100643]), Progression, 100643, true, Now);
        await Assert.ThrowsAsync<ArgumentException>(() => store.MutatePetAsync(command with { ConsumableItemId = 301001 }, default).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.MutatePetAsync(command, new CancellationToken(true)).AsTask());
        var bounded = new MySqlInventoryRepository(new UnusedFactory(), maxLoadoutsPerPlayer: 1);
        var twoBuilds = Pet with { Pet = Pet.Pet!.With("kr-1256", Progression) };
        await Assert.ThrowsAsync<InvalidDataException>(() => bounded.ReplaceSlotsAsync(1, InventoryBag.CharacterBag, [twoBuilds], default).AsTask());
    }

    /// <summary>Unsigned Article columns and bound decimal parameters retain exact 64-bit values.</summary>
    [Fact]
    public void UnsignedIdentityColumnsRoundTripAndBindExactDecimalText()
    {
        var pet = Pet with { PetIdentity = new(ulong.MaxValue, ulong.MaxValue) };
        var row = MySqlInventoryRepository.ToRow(pet, 0);
        Assert.Equal("18446744073709551615", row.PetEntitlementId);
        Assert.Equal("18446744073709551615", row.PetArticleKey);
        Assert.Equal(pet, MySqlInventoryRepository.ToRecord(row));
        using var db = SugarSession.Create(new MySqlConnection("Server=unused;Database=bf_game_db"));
        var insert = db.Insertable(row).ToSql();
        Assert.Contains("`pet_entitlement_id`", insert.Key, StringComparison.Ordinal);
        Assert.Contains("`pet_article_key`", insert.Key, StringComparison.Ordinal);
        Assert.Equal(2, insert.Value.Count(parameter => Equals(parameter.Value, "18446744073709551615")));
        var renewal = MySqlShopPurchaseStore.SlotUpdate(db, pet with { Quantity = 2 }).ToSql();
        Assert.DoesNotContain("pet_entitlement_id", renewal.Key, StringComparison.Ordinal);
        Assert.DoesNotContain("pet_article_key", renewal.Key, StringComparison.Ordinal);
    }

    /// <summary>Incomplete, invalid and overflowing identity columns fail during entity conversion.</summary>
    [Fact]
    public void PartialInvalidAndOverflowingIdentityColumnsFailOnRead()
    {
        var row = MySqlInventoryRepository.ToRow(Pet, 0);
        row.PetEntitlementId = "1";
        Assert.Throws<InvalidDataException>(() => MySqlInventoryRepository.ToRecord(row));
        row.PetArticleKey = "99999999";
        Assert.Throws<InvalidDataException>(() => MySqlInventoryRepository.ToRecord(row));
        row.PetArticleKey = "18446744073709551616";
        Assert.Throws<OverflowException>(() => MySqlInventoryRepository.ToRecord(row));
        row.PetArticleKey = "100000000";
        row.PetEntitlementId = "0";
        Assert.Throws<InvalidDataException>(() => MySqlInventoryRepository.ToRecord(row));
    }

    /// <summary>Article command validation rejects invalid ownership inputs before database access.</summary>
    [Fact]
    public async Task ArticleCommandsValidateBeforeOpeningAConnection()
    {
        var store = new MySqlInventoryRepository(new UnusedFactory());
        var command = new PetInitializationCommand(1, new("kr-1256", [301001]), [301001], Progression, Now);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.InitializePetsAsync(command with { UserNum = 0 }, default).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.InitializePetsAsync(command, new(true)).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ReadPetInventoryAsync(1, command.Scope, new(true)).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SelectPetAsync(new(1, command.Scope, [],
            new(301001, new(1, PetArticleKeys.First)), true, Now), new(true)).AsTask());
    }

    /// <summary>Enclosing mutations reject inconsistent initialization owner, build or capacity before database access.</summary>
    [Theory]
    [InlineData("owner")]
    [InlineData("build")]
    [InlineData("catalog")]
    public async Task EnclosingMutationsRejectMismatchedInitializationBeforeOpeningConnection(string mismatch)
    {
        var store = new MySqlInventoryRepository(new UnusedFactory());
        InventoryLoadoutScope scope = new("kr-812", [301001, 100643]);
        var initialization = new PetInitializationCommand(1, scope, [301001], Progression, Now);
        initialization = mismatch switch
        {
            "owner" => initialization with { UserNum = 2 },
            "build" => initialization with { Scope = new("kr-1256", scope.SupportedItemIds) },
            _ => initialization with { Scope = new("kr-812", [301001]) },
        };
        await Assert.ThrowsAsync<ArgumentException>(() => store.UpdateLoadoutSelectionSnapshotAsync(1, scope, [],
            100643, true, default, initialization).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => store.SelectPetAsync(new(1, scope, [],
            new(301001, new(1, PetArticleKeys.First)), true, Now) { Initialization = initialization }, default).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => store.MutatePetSnapshotAsync(new(Pet, scope, Progression, 0, false, Now)
            { Initialization = initialization }, default).AsTask());
    }

    /// <summary>Unsupported mileage reward plans fail before opening a database connection.</summary>
    [Fact]
    public async Task MileageValidationRejectsUnsupportedGrantsBeforeOpeningConnection()
    {
        var store = new MySqlInventoryRepository(new UnusedFactory());
        InventoryLoadoutScope scope = new("kr-812", [301001, 301002, 100643, 103385, 200777]);
        PetMutationCommand command = new(Pet, scope, Progression, 100643, true, Now)
        {
            Initialization = new(1, scope, [301001, 301002], Progression, Now),
            Mileage = new(103385, null, 1, 100, 1000, new(1, 1, 1, false, 0,
                [new(301002, 1) { Mode = ShopGrantMode.MatchItem, Lifetime = InventoryLifetime.Permanent }])),
        };
        await Assert.ThrowsAsync<ArgumentException>(() => store.MutatePetWithResultAsync(command, default).AsTask());
        var valid = command with { Mileage = new(103385, null, 1, 100, 1000, null) };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.MutatePetWithResultAsync(valid, new(true)).AsTask());
    }

    private sealed class UnusedFactory : BubbleFighter.Fantasy.Abstractions.Persistence.IDbConnectionFactory
    {
        public ValueTask<System.Data.Common.DbConnection> OpenAsync(CancellationToken ct) =>
            throw new InvalidOperationException("Validation must complete before a database connection is opened.");
    }
}
