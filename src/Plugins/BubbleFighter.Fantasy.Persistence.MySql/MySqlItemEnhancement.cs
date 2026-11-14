using System.Globalization;
using System.Text.Json;
using BubbleFighter.Fantasy.Contracts.Inventory;
using SqlSugar;

namespace BubbleFighter.Fantasy.Persistence.MySql;

public sealed partial class MySqlInventoryRepository : IItemEnhancementStore
{
    /// <inheritdoc />
    public async ValueTask<InventorySnapshot> InitializeArticlesAsync(ArticleInitializationCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Validate();
        ct.ThrowIfCancellationRequested();
        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        var db = session.Db;
        await using var transaction = await session.BeginWriteAsync(command.UserNum, authority, ct).ConfigureAwait(false);
        await MySqlPlayerLock.AcquireAsync(db, command.UserNum, ct).ConfigureAwait(false);
        var (loadout, exists) = await ReadLoadoutAsync(db, command.UserNum, command.Scope, ct).ConfigureAwait(false);
        var stored = await ReadOwnedInventoryRowsAsync(db, command.UserNum, ct).ConfigureAwait(false);
        var allocate = InventoryArticles.NeedsIdentity(command, stored);
        ulong lastId = 0, lastArticle = 0;
        if (allocate)
        {
            var counters = Convert.ToString(await db.Ado.GetScalarAsync(
                "SELECT CONCAT(CAST(entitlement_id AS CHAR),',',CAST(article_key AS CHAR)) FROM pet_identity_allocator WHERE singleton=1 FOR UPDATE",
                new List<SugarParameter>(), ct).ConfigureAwait(false), CultureInfo.InvariantCulture)?.Split(',');
            if (counters is not { Length: 2 }) throw new InvalidDataException("Shared Article allocator is missing or malformed.");
            lastId = ulong.Parse(counters[0], NumberStyles.None, CultureInfo.InvariantCulture);
            lastArticle = ulong.Parse(counters[1], NumberStyles.None, CultureInfo.InvariantCulture);
        }
        var rows = InventoryArticles.Initialize(command, stored, () =>
        {
            if (!allocate || lastArticle < PetArticleKeys.First - 1 || lastArticle >= PetArticleKeys.Last)
                throw new InvalidOperationException("Native Article allocation is exhausted or corrupt.");
            return new(checked(++lastId), checked(++lastArticle));
        });
        loadout = InventoryLoadouts.MaterializeArticles(loadout, rows, command.Scope);
        var result = InventorySnapshot.Project(rows, loadout, command.Scope);
        if (!stored.SequenceEqual(rows)) await ReplaceOwnedInventoryRowsAsync(db, command.UserNum, stored.Count, rows, ct).ConfigureAwait(false);
        await SaveLoadoutAsync(db, command.UserNum, command.Scope.Key, loadout, exists, ct).ConfigureAwait(false);
        if (allocate && await db.Ado.ExecuteCommandAsync(
            "UPDATE pet_identity_allocator SET entitlement_id=CAST(@id AS UNSIGNED),article_key=CAST(@article AS UNSIGNED) WHERE singleton=1",
            new List<SugarParameter> { new("@id", lastId.ToString(CultureInfo.InvariantCulture)),
                new("@article", lastArticle.ToString(CultureInfo.InvariantCulture)) }, ct).ConfigureAwait(false) != 1)
            throw new InvalidDataException("Shared Article allocator update affected an invalid row count.");
        ct.ThrowIfCancellationRequested();
        await transaction.CommitAsync().ConfigureAwait(false);
        return result;
    }

    /// <inheritdoc />
    public async ValueTask<InventorySnapshot> ReadEnhancementInventoryAsync(long userNum, InventoryLoadoutScope scope, CancellationToken ct)
    {
        RequireUserNum(userNum);
        ArgumentNullException.ThrowIfNull(scope);
        ct.ThrowIfCancellationRequested();
        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        await using var transaction = await session.BeginAsync().ConfigureAwait(false);
        await MySqlPlayerLock.AcquireAsync(session.Db, userNum, ct).ConfigureAwait(false);
        var (state, _) = await ReadLoadoutAsync(session.Db, userNum, scope, ct).ConfigureAwait(false);
        var result = InventorySnapshot.Project(await ReadOwnedInventoryRowsAsync(session.Db, userNum, ct).ConfigureAwait(false), state, scope);
        ct.ThrowIfCancellationRequested();
        await transaction.CommitAsync().ConfigureAwait(false);
        return result;
    }

    /// <inheritdoc />
    public async ValueTask<InventorySnapshot?> TryEnhanceAsync(ItemEnhancementCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Validate();
        ct.ThrowIfCancellationRequested();
        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        var db = session.Db;
        await using var transaction = await session.BeginWriteAsync(command.UserNum, authority, ct).ConfigureAwait(false);
        await MySqlPlayerLock.AcquireAsync(db, command.UserNum, ct).ConfigureAwait(false);
        var (loadout, exists) = await ReadLoadoutAsync(db, command.UserNum, command.Scope, ct).ConfigureAwait(false);
        var stored = await ReadOwnedInventoryRowsAsync(db, command.UserNum, ct).ConfigureAwait(false);
        var result = await MutateArticlesAsync(db, command.UserNum, command.Scope, stored, loadout, exists,
            command.ArticleInitialization, command.PetInitialization, prepared =>
            {
                var rows = command.Apply(prepared.Rows, prepared.Loadout);
                return rows is null ? null : new ArticleMutation(rows, prepared.Loadout);
            }, ct).ConfigureAwait(false);
        if (result is null) return null;
        ct.ThrowIfCancellationRequested();
        await transaction.CommitAsync().ConfigureAwait(false);
        return result;
    }

    /// <inheritdoc />
    public async ValueTask<InventorySnapshot?> SelectArticleAsync(ArticleSelectionCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Validate();
        ct.ThrowIfCancellationRequested();
        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        var db = session.Db;
        await using var transaction = await session.BeginWriteAsync(command.UserNum, authority, ct).ConfigureAwait(false);
        await MySqlPlayerLock.AcquireAsync(db, command.UserNum, ct).ConfigureAwait(false);
        var (loadout, exists) = await ReadLoadoutAsync(db, command.UserNum, command.Scope, ct).ConfigureAwait(false);
        var stored = await ReadOwnedInventoryRowsAsync(db, command.UserNum, ct).ConfigureAwait(false);
        var result = await MutateArticlesAsync(db, command.UserNum, command.Scope, stored, loadout, exists,
            command.ArticleInitialization, command.PetInitialization, prepared =>
            {
                var selected = InventoryArticles.Select(command, prepared.Rows, prepared.Loadout);
                return selected is null ? null : new ArticleMutation(prepared.Rows, InventoryLoadouts.Selection(prepared.Loadout, selected));
            }, ct).ConfigureAwait(false);
        if (result is null) return null;
        ct.ThrowIfCancellationRequested();
        await transaction.CommitAsync().ConfigureAwait(false);
        return result;
    }

    private async ValueTask<InventorySnapshot?> MutateArticlesAsync(SqlSugarClient db, long userNum, InventoryLoadoutScope scope,
        IReadOnlyList<InventorySlotRecord> stored, InventoryLoadoutState loadout, bool exists,
        ArticleInitializationCommand? articles, PetInitializationCommand? pets,
        Func<PreparedInventoryMetadata, ArticleMutation?> operation, CancellationToken ct)
    {
        var allocate = InventoryMetadataPreparation.NeedsIdentity(userNum, scope, stored, articles, pets);
        ulong lastId = 0, lastArticle = 0;
        if (allocate)
        {
            var counters = Convert.ToString(await db.Ado.GetScalarAsync(
                "SELECT CONCAT(CAST(entitlement_id AS CHAR),',',CAST(article_key AS CHAR)) FROM pet_identity_allocator WHERE singleton=1 FOR UPDATE",
                new List<SugarParameter>(), ct).ConfigureAwait(false), CultureInfo.InvariantCulture)?.Split(',');
            if (counters is not { Length: 2 }) throw new InvalidDataException("Shared Article allocator is missing or malformed.");
            lastId = ulong.Parse(counters[0], NumberStyles.None, CultureInfo.InvariantCulture);
            lastArticle = ulong.Parse(counters[1], NumberStyles.None, CultureInfo.InvariantCulture);
        }
        var prepared = InventoryMetadataPreparation.Prepare(userNum, scope, stored, loadout, articles, pets, () =>
        {
            if (!allocate || lastArticle < PetArticleKeys.First - 1 || lastArticle >= PetArticleKeys.Last)
                throw new InvalidOperationException("Native Article allocation is exhausted or corrupt.");
            return new(checked(++lastId), checked(++lastArticle));
        });
        var changed = operation(prepared);
        if (changed is null) return null;
        var result = InventorySnapshot.Project(changed.Rows, changed.Loadout, scope);
        if (!stored.SequenceEqual(changed.Rows)) await ReplaceOwnedInventoryRowsAsync(db, userNum, stored.Count, changed.Rows, ct).ConfigureAwait(false);
        if (!exists || !InventoryLoadouts.SameState(loadout, changed.Loadout))
            await SaveLoadoutAsync(db, userNum, scope.Key, changed.Loadout, exists, ct).ConfigureAwait(false);
        if (allocate && await db.Ado.ExecuteCommandAsync(
            "UPDATE pet_identity_allocator SET entitlement_id=CAST(@id AS UNSIGNED),article_key=CAST(@article AS UNSIGNED) WHERE singleton=1",
            new List<SugarParameter> { new("@id", lastId.ToString(CultureInfo.InvariantCulture)),
                new("@article", lastArticle.ToString(CultureInfo.InvariantCulture)) }, ct).ConfigureAwait(false) != 1)
            throw new InvalidDataException("Shared Article allocator update affected an invalid row count.");
        return result;
    }

    private sealed record ArticleMutation(IReadOnlyList<InventorySlotRecord> Rows, InventoryLoadoutState Loadout);

    private static InventoryArticleIdentity? DecodeArticleIdentity(string? entitlement, string? article)
    {
        if (entitlement is null && article is null) return null;
        if (entitlement is null || article is null) throw new InvalidDataException("Stored equipment identity is incomplete.");
        var identity = new InventoryArticleIdentity(ulong.Parse(entitlement, NumberStyles.None, CultureInfo.InvariantCulture),
            ulong.Parse(article, NumberStyles.None, CultureInfo.InvariantCulture));
        identity.Validate();
        return identity;
    }

    private static ItemEnhancements? DecodeEnhancements(string? json)
    {
        if (json is null) return null;
        if (json.Length > 256 * 512) throw new InvalidDataException("Stored enhancement metadata exceeds its bound.");
        var state = JsonSerializer.Deserialize<ItemEnhancements>(json)
            ?? throw new InvalidDataException("Stored enhancement metadata is JSON null.");
        state.Validate();
        return state;
    }

    private static string? EncodeEnhancements(ItemEnhancements? enhancements)
    {
        if (enhancements is null) return null;
        enhancements.Validate();
        return JsonSerializer.Serialize(enhancements);
    }
}
