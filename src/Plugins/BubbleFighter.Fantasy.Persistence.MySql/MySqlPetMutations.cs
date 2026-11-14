using System.Text.Json;
using System.Text.Json.Serialization;
using BubbleFighter.Fantasy.Contracts.Inventory;
using SqlSugar;

namespace BubbleFighter.Fantasy.Persistence.MySql;

public sealed partial class MySqlInventoryRepository : IPetMutationStore
{
    private static readonly JsonSerializerOptions PetJson = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 8,
    };

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<InventorySlotRecord>?> MutatePetAsync(PetMutationCommand command, CancellationToken ct) =>
        (await MutatePetSnapshotAsync(command, ct).ConfigureAwait(false))?.CharacterBag;

    /// <inheritdoc />
    public async ValueTask<PetInventorySnapshot?> MutatePetSnapshotAsync(PetMutationCommand command, CancellationToken ct) =>
        (await MutatePetWithResultAsync(command, ct).ConfigureAwait(false))?.Snapshot;

    /// <inheritdoc />
    public async ValueTask<PetMutationResult?> MutatePetWithResultAsync(PetMutationCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Validate();
        ct.ThrowIfCancellationRequested();
        var user = command.ExpectedSlot.UserNum;
        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        var db = session.Db;
        await using var transaction = await session.BeginWriteAsync(user, authority, ct).ConfigureAwait(false);
        await MySqlPlayerLock.AcquireAsync(db, user, ct).ConfigureAwait(false);
        var (loadout, exists) = await ReadLoadoutAsync(db, user, command.Scope, ct).ConfigureAwait(false);
        var stored = await db.Queryable<InventorySlotRow>()
            .Where(row => row.UserNum == user && (row.Bag == 0 || row.Bag == 1))
            .OrderBy(row => row.Bag).OrderBy(row => row.Ordinal)
            .Take(2 * InventoryLimits.MaxInventorySlotsPerBag + 1).TranLock(DbLockType.Wait)
            .ToListAsync(ct).ConfigureAwait(false);
        if (stored.Count > 2 * InventoryLimits.MaxInventorySlotsPerBag
            || stored.GroupBy(row => row.Bag).Any(bag => bag.Count() > InventoryLimits.MaxInventorySlotsPerBag))
            throw new InvalidDataException("Stored pet inventory exceeds its bag capacity.");
        var plan = await PreparePetInventoryAsync(db, stored.Select(ReadRecord).ToArray(), loadout, command.Scope, command.Initialization, ct,
            reserveIdentityAllocation: command.ArticleInitialization is not null).ConfigureAwait(false);
        (long Stored, long Highest) keys = command.Mileage is null ? (0, 0)
            : await MySqlInventorySlotKeys.ReadAsync(db, user, ct).ConfigureAwait(false);
        var changed = command.Plan(plan.Rows, plan.Loadout, keys.Highest);
        if (changed is null) return null;
        var finalRows = changed.Rows;
        if (command.ArticleInitialization is not null)
        {
            var lastId = plan.LastId;
            var lastArticle = plan.LastArticle;
            // Reward grants are resolved before Article preparation so their expected rows are not changed by our own staging.
            var prepared = InventoryMetadataPreparation.Prepare(command.ExpectedSlot.UserNum, command.Scope,
                changed.Rows, plan.Loadout, command.ArticleInitialization, null, () =>
                {
                    if (!plan.Allocate || lastArticle < PetArticleKeys.First - 1 || lastArticle >= PetArticleKeys.Last)
                        throw new InvalidOperationException("Native Article allocation is exhausted or corrupt.");
                    return new(checked(++lastId), checked(++lastArticle));
                });
            finalRows = prepared.Rows;
            plan = plan with { Rows = prepared.Rows, Loadout = prepared.Loadout, LastId = lastId, LastArticle = lastArticle };
        }
        var result = new PetMutationResult(PetInventorySnapshot.Project(finalRows, plan.Loadout, command.Scope), changed.RewardItemIds);
        await CommitPetPreparationAsync(db, user, command.Scope, plan, finalRows,
            command.Initialization is null && command.ArticleInitialization is null ? null : plan.Loadout, exists, ct).ConfigureAwait(false);
        if (command.Mileage is not null)
            await MySqlInventorySlotKeys.AdvanceAsync(db, user, keys.Stored, changed.LastSlotKey, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        await transaction.CommitAsync().ConfigureAwait(false);
        return result;
    }

    private static PetProgressions? DecodePet(string? json)
    {
        if (json is null) return null;
        // Each bounded build state has six scalars and two fixed-size skills.
        if (json.Length > 256 * 2048) throw new InvalidDataException("Stored pet progression exceeds its serialized bound.");
        var pet = JsonSerializer.Deserialize<PetProgressions>(json, PetJson)
            ?? throw new InvalidDataException("Stored pet progression is JSON null instead of a nullable column.");
        pet.Validate();
        return pet;
    }

    private static string? EncodePet(PetProgressions? pet)
    {
        if (pet is null) return null;
        pet.Validate();
        return JsonSerializer.Serialize(pet, PetJson);
    }
}
