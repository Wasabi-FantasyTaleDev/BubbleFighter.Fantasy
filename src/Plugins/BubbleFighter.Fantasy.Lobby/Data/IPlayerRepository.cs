using BubbleFighter.Fantasy.Contracts.Players;
using System.Collections.Concurrent;
using BubbleFighter.Fantasy.Abstractions.Persistence;
using Dapper;
using BubbleFighter.Fantasy.Contracts.Inventory;
using System.Data.Common;

namespace BubbleFighter.Fantasy.Lobby.Data;

/// <summary>
/// MySQL-backed repository over the <c>players</c> table created by the persistence
/// plugin's migrations.
/// </summary>
public sealed partial class MySqlPlayerRepository(IDbConnectionFactory connections) : IPlayerRepository
{
    /// <inheritdoc />
    public bool PersistsState => true;

    /// <inheritdoc />
    public bool CreatesStarterInventoryAtomically => true;

    /// <inheritdoc />
    public async ValueTask<int> RenameWithItemAsync(long userNum, string nickname, int itemId, CancellationToken ct)
    {
        await using var db = await connections.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await db.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            var present = await db.QuerySingleOrDefaultAsync<long?>(new CommandDefinition(
                "SELECT user_num FROM players WHERE user_num=@userNum FOR UPDATE", new { userNum }, tx, cancellationToken: ct)).ConfigureAwait(false);
            if (present is null) throw new InvalidOperationException("Rename requires an existing player.");
            if (await db.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT EXISTS(SELECT 1 FROM players WHERE nickname=@nickname AND user_num<>@userNum)",
                new { userNum, nickname }, tx, cancellationToken: ct)).ConfigureAwait(false) != 0)
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                return 9;
            }
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var quantity = await db.QuerySingleOrDefaultAsync<int?>(new CommandDefinition(
                "SELECT quantity FROM inventory_slots WHERE user_num=@userNum AND bag=0 AND item_id=@itemId "
                + "AND quantity>0 AND (lifetime_kind<>2 OR expires_at_unix_ms>@now) FOR UPDATE",
                new { userNum, itemId, now }, tx, cancellationToken: ct)).ConfigureAwait(false);
            if (quantity is null)
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                return 1;
            }
            await db.ExecuteAsync(new CommandDefinition(
                "UPDATE players SET nickname=@nickname WHERE user_num=@userNum; "
                + "UPDATE fighters SET nickname=@nickname WHERE user_num=@userNum; "
                + "UPDATE inventory_slots SET quantity=quantity-1,flags=IF(quantity=0,flags & 254,flags),updated_at=UTC_TIMESTAMP() "
                + "WHERE user_num=@userNum AND bag IN (0,1) AND item_id=@itemId AND quantity>0; "
                + "DELETE FROM inventory_slots WHERE user_num=@userNum AND bag IN (0,1) AND item_id=@itemId AND quantity=0",
                new { userNum, nickname, itemId }, tx, cancellationToken: ct)).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return 0;
        }
        catch (DbException ex) when (ex.SqlState == "23000")
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            return 9;
        }
    }
    /// <inheritdoc />
    public async ValueTask<bool> HasCreatedCharacterAsync(long userNum, CancellationToken ct)
    {
        await using var db = await connections.OpenAsync(ct).ConfigureAwait(false);
        return await db.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM player_onboarding WHERE user_num=@userNum)",
            new { userNum }, cancellationToken: ct)).ConfigureAwait(false) != 0;
    }

    /// <inheritdoc />
    public async ValueTask<bool> NicknameExistsAsync(string nickname, CancellationToken ct, string? exceptUsername = null)
    {
        await using var db = await connections.OpenAsync(ct).ConfigureAwait(false);
        return await db.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM players WHERE nickname=@nickname AND (@exceptUsername IS NULL OR username<>@exceptUsername))",
            new { nickname, exceptUsername }, cancellationToken: ct)).ConfigureAwait(false) != 0;
    }

    /// <inheritdoc />
    public async ValueTask<bool> NeedsTutorialAsync(long userNum, CancellationToken ct)
    {
        await using var db = await connections.OpenAsync(ct).ConfigureAwait(false);
        return await db.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM player_onboarding WHERE user_num=@userNum AND tutorial_completed=0)",
            new { userNum }, cancellationToken: ct)).ConfigureAwait(false) == 1;
    }

    /// <inheritdoc />
    public async ValueTask CompleteTutorialAsync(long userNum, CancellationToken ct)
    {
        await using var db = await connections.OpenAsync(ct).ConfigureAwait(false);
        await db.ExecuteAsync(new CommandDefinition(
            "UPDATE player_onboarding SET tutorial_completed=1 WHERE user_num=@userNum",
            new { userNum }, cancellationToken: ct)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<PlayerRecord?> CreateCharacterAsync(string username, string nickname, StarterKit kit, CancellationToken ct)
    {
        await using var db = await connections.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await db.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            var existing = await db.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
                "SELECT user_num AS UserNum,level AS Level,color_index AS ColorIndex FROM players WHERE username=@username FOR UPDATE",
                new { username }, tx, cancellationToken: ct)).ConfigureAwait(false);
            var userNum = existing?.UserNum ?? await db.ExecuteScalarAsync<long>(new CommandDefinition(
                "INSERT INTO players(username,nickname,level,created_at) VALUES(@username,@nickname,1,UTC_TIMESTAMP()); SELECT LAST_INSERT_ID();",
                new { username, nickname }, tx, cancellationToken: ct)).ConfigureAwait(false);
            if (existing is not null)
            {
                if (await db.ExecuteScalarAsync<int>(new CommandDefinition(
                    "SELECT EXISTS(SELECT 1 FROM player_onboarding WHERE user_num=@userNum)",
                    new { userNum }, tx, cancellationToken: ct)).ConfigureAwait(false) != 0)
                {
                    await tx.RollbackAsync(ct).ConfigureAwait(false);
                    return null;
                }
                await db.ExecuteAsync(new CommandDefinition("UPDATE players SET nickname=@nickname WHERE user_num=@userNum",
                    new { userNum, nickname }, tx, cancellationToken: ct)).ConfigureAwait(false);
            }
            var character = kit.WornItemIds.Single(id => id is >= 300001 and <= 300031);
            await db.ExecuteAsync(new CommandDefinition(
                "INSERT INTO player_onboarding(user_num,initial_character_item_id) VALUES(@userNum,@character); "
                + "INSERT INTO fighters(user_num,slot,character_type,level,equipped_item_id,nickname) VALUES(@userNum,0,@type,1,@character,@nickname) "
                + "ON DUPLICATE KEY UPDATE character_type=VALUES(character_type),equipped_item_id=VALUES(equipped_item_id),nickname=VALUES(nickname)",
                new { userNum, character, type = character - 300000, nickname }, tx, cancellationToken: ct)).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            foreach (var bagKind in new[] { InventoryBag.CharacterBag, InventoryBag.EquippedBag })
            {
                var bag = (int)bagKind;
                var stored = (await db.QueryAsync<StoredSlot>(new CommandDefinition(
                    "SELECT item_id AS ItemId,ordinal AS Ordinal FROM inventory_slots WHERE user_num=@userNum AND bag=@bag FOR UPDATE",
                    new { userNum, bag }, tx, cancellationToken: ct)).ConfigureAwait(false)).ToArray();
                var ordinal = stored.Length == 0 ? 0 : checked(stored.Max(row => row.Ordinal) + 1);
                await db.ExecuteAsync(new CommandDefinition(
                    "UPDATE inventory_slots SET flags=flags & 254 WHERE user_num=@userNum AND bag=@bag AND item_id BETWEEN 300001 AND 300031",
                    new { userNum, bag }, tx, cancellationToken: ct)).ConfigureAwait(false);
                foreach (var row in kit.Slots(userNum, bagKind, now))
                {
                    if (stored.Any(item => item.ItemId == row.ItemId))
                    {
                        if ((row.Flags & InventorySlotRecord.WornFlagBit) != 0)
                            await db.ExecuteAsync(new CommandDefinition(
                                "UPDATE inventory_slots SET flags=flags | 1 WHERE user_num=@userNum AND bag=@bag AND item_id=@itemId",
                                new { userNum, bag, itemId = row.ItemId }, tx, cancellationToken: ct)).ConfigureAwait(false);
                        continue;
                    }
                    await db.ExecuteAsync(new CommandDefinition(
                        "INSERT INTO inventory_slots(user_num,bag,ordinal,slot_key,item_id,quantity,flags,lifetime_kind,expires_at_unix_ms,updated_at) "
                        + "VALUES(@userNum,@bag,@ordinal,@slotKey,@itemId,@quantity,@flags,@lifetimeKind,@expires,UTC_TIMESTAMP())",
                        new { userNum, bag, ordinal = ordinal++, slotKey = row.SlotKey, itemId = row.ItemId,
                            quantity = row.Quantity, flags = row.Flags, lifetimeKind = (int)row.Lifetime.Kind,
                            expires = row.Lifetime.ExpiresAtUnixMilliseconds }, tx, cancellationToken: ct)).ConfigureAwait(false);
                }
            }
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return new(userNum, username, nickname, existing?.Level ?? 1) { ColorIndex = existing?.ColorIndex ?? 0 };
        }
        catch (DbException ex) when (ex.SqlState == "23000")
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            return null;
        }
    }
    private sealed class StoredSlot
    {
        public int ItemId { get; init; }
        public int Ordinal { get; init; }
    }
    private sealed class Row
    {
        public long UserNum { get; init; }
        public string Username { get; init; } = string.Empty;
        public string Nickname { get; init; } = string.Empty;
        public int Level { get; init; }
        public ushort ColorIndex { get; init; }
    }

    /// <inheritdoc />
    public async ValueTask<PlayerRecord?> FindByUsernameAsync(string username, CancellationToken ct)
    {
        await using var db = await connections.OpenAsync(ct).ConfigureAwait(false);
        var row = await db.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            "SELECT user_num AS UserNum, username AS Username, nickname AS Nickname, level AS Level, color_index AS ColorIndex FROM players WHERE username = @username",
            new { username }, cancellationToken: ct)).ConfigureAwait(false);
        return row is null ? null : new PlayerRecord(row.UserNum, row.Username, row.Nickname, row.Level) { ColorIndex = row.ColorIndex };
    }

    /// <inheritdoc />
    public async ValueTask<PlayerRecord> CreateAsync(string username, string nickname, CancellationToken ct)
    {
        await using var db = await connections.OpenAsync(ct).ConfigureAwait(false);
        var userNum = await db.ExecuteScalarAsync<long>(new CommandDefinition(
            "INSERT INTO players (username, nickname, level, created_at) VALUES (@username, @nickname, 1, UTC_TIMESTAMP()); SELECT LAST_INSERT_ID();",
            new { username, nickname }, cancellationToken: ct)).ConfigureAwait(false);
        return new PlayerRecord(userNum, username, nickname, 1);
    }
}

/// <summary>
/// Process-local repository used when no database plugin is mounted. Players vanish on
/// restart; intended for protocol work without MySQL.
/// </summary>
public sealed class InMemoryPlayerRepository : IPlayerRepository
{
    private readonly Lock _creationGate = new();
    private readonly HashSet<long> _tutorialCompleted = [];
    private readonly HashSet<long> _createdCharacters = [];

    /// <inheritdoc />
    public ValueTask<bool> NicknameExistsAsync(string nickname, CancellationToken ct, string? exceptUsername = null) =>
        ValueTask.FromResult(_players.Values.Any(p => p.Username != exceptUsername && string.Equals(p.Nickname, nickname, StringComparison.OrdinalIgnoreCase)));

    /// <inheritdoc />
    public ValueTask<bool> HasCreatedCharacterAsync(long userNum, CancellationToken ct)
    {
        lock (_creationGate) return ValueTask.FromResult(_createdCharacters.Contains(userNum));
    }

    /// <inheritdoc />
    public ValueTask<PlayerRecord?> CreateCharacterAsync(string username, string nickname, StarterKit kit, CancellationToken ct)
    {
        lock (_creationGate)
        {
            _players.TryGetValue(username, out var existing);
            if ((existing is not null && _createdCharacters.Contains(existing.UserNum))
                || _players.Values.Any(p => p.Username != username && string.Equals(p.Nickname, nickname, StringComparison.OrdinalIgnoreCase)))
                return ValueTask.FromResult<PlayerRecord?>(null);
            var record = new PlayerRecord(existing?.UserNum ?? Interlocked.Increment(ref _nextUserNum), username, nickname, existing?.Level ?? 1);
            _players[username] = record;
            _createdCharacters.Add(record.UserNum);
            return ValueTask.FromResult<PlayerRecord?>(record);
        }
    }

    /// <inheritdoc />
    public ValueTask<bool> NeedsTutorialAsync(long userNum, CancellationToken ct)
    {
        lock (_creationGate) return ValueTask.FromResult(_createdCharacters.Contains(userNum) && !_tutorialCompleted.Contains(userNum));
    }

    /// <inheritdoc />
    public ValueTask CompleteTutorialAsync(long userNum, CancellationToken ct)
    {
        lock (_creationGate) _tutorialCompleted.Add(userNum);
        return ValueTask.CompletedTask;
    }
    private readonly ConcurrentDictionary<string, PlayerRecord> _players = new(StringComparer.Ordinal);
    private long _nextUserNum;

    /// <inheritdoc />
    public ValueTask<PlayerRecord?> FindByUsernameAsync(string username, CancellationToken ct) =>
        ValueTask.FromResult(_players.TryGetValue(username, out var p) ? p : null);

    /// <inheritdoc />
    public ValueTask<PlayerRecord> CreateAsync(string username, string nickname, CancellationToken ct)
    {
        var record = new PlayerRecord(Interlocked.Increment(ref _nextUserNum), username, nickname, 1);
        return ValueTask.FromResult(_players.GetOrAdd(username, record));
    }
}
