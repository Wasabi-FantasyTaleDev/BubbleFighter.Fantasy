using BubbleFighter.Fantasy.Contracts.Fighters;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Launcher;
using BubbleFighter.Fantasy.Contracts.Players;

namespace BubbleFighter.Fantasy.Persistence.Sqlite;

/// <summary>
/// The SQLite save's repository fragments, split by domain but sharing one model: a player's whole
/// coupled state lives in one schema-versioned <see cref="SaveState"/> JSON row, every operation goes
/// through the owned database gate, and cross-domain changes (a purchase touching wallet, inventory
/// and history at once) are atomic because they mutate and save that single row inside one
/// <see cref="SqliteDatabase.Write{T}"/> transaction. This fragment owns identity: durable player and
/// launcher-account records, and character creation that commits the chosen fighter and starter bags
/// in the same identity transaction. A launcher account and its player share the username.
/// </summary>
internal sealed partial class SqliteRepositories(SqliteDatabase database) : IPlayerRepository, ILauncherAccountRepository
{
    /// <summary>This provider writes through to disk; identity and onboarding state survive restarts.</summary>
    public bool PersistsState => true;
    /// <summary>Character creation commits the chosen fighter and the starter bags with the identity row.</summary>
    public bool CreatesStarterInventoryAtomically => true;

    // A launcher account and its player share the username; a player without an account reads as an empty-hash account.
    public ValueTask<LauncherAccount?> FindAsync(string username, CancellationToken ct) => new(database.Read(db =>
    {
        var account = db.Orm.Queryable<AccountRow>().Where(row => row.Username == username).ToList().FirstOrDefault();
        var player = db.Orm.Queryable<PlayerRow>().Where(row => row.Username == username).ToList().FirstOrDefault();
        if (account is null)
            return player is null ? null : new LauncherAccount(username, "", player.UserNum, player.Nickname, null);
        return new LauncherAccount(username, account.PasswordHash, player?.UserNum, player?.Nickname,
            account.BannedUntil is { } banned ? DateTimeOffset.FromUnixTimeMilliseconds(banned).UtcDateTime : null);
    }, ct));

    public ValueTask<bool> RegisterAsync(string username, string passwordHash, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(username);
        ArgumentException.ThrowIfNullOrEmpty(passwordHash);
        return new(database.Write((db, _) =>
        {
            if (db.Orm.Queryable<PlayerRow>().Any(row => row.Username == username)
                || db.Orm.Queryable<AccountRow>().Any(row => row.Username == username))
                return false;
            db.RequireCapacity("accounts", db.Options.MaxAccounts);
            db.Orm.Insertable(new AccountRow { Username = username, PasswordHash = passwordHash }).ExecuteCommand();
            return true;
        }, ct));
    }

    public ValueTask<bool> TrySetInitialPasswordAsync(string username, string passwordHash, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(username);
        ArgumentException.ThrowIfNullOrEmpty(passwordHash);
        return new(database.Write((db, _) =>
        {
            var account = db.Orm.Queryable<AccountRow>().Where(row => row.Username == username).ToList().FirstOrDefault();
            if (account is not null && (account.PasswordHash.Length != 0
                    || account.BannedUntil is { } banned && banned > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))
                return false;
            if (account is null)
            {
                if (!db.Orm.Queryable<PlayerRow>().Any(row => row.Username == username)) return false;
                db.RequireCapacity("accounts", db.Options.MaxAccounts);
                db.Orm.Insertable(new AccountRow { Username = username, PasswordHash = passwordHash }).ExecuteCommand();
            }
            else
            {
                // Only an account with no password yet takes the first one; the empty-hash guard is part of the WHERE.
                db.Orm.Updateable<AccountRow>().SetColumns(row => new AccountRow { PasswordHash = passwordHash })
                    .Where(row => row.Username == username && row.PasswordHash == "").ExecuteCommand();
            }
            return true;
        }, ct));
    }

    public ValueTask<PlayerRecord?> FindByUsernameAsync(string username, CancellationToken ct) => new(database.Read(db =>
        ReadPlayer(db, username), ct));

    private static PlayerRecord? ReadPlayer(SqliteDatabase db, string username)
    {
        var row = db.Orm.Queryable<PlayerRow>().Where(player => player.Username == username).ToList().FirstOrDefault();
        if (row is null) return null;
        var state = SaveJson.Decode(row.State);
        return new(row.UserNum, username, row.Nickname, state.Level) { ColorIndex = state.ColorIndex };
    }

    public ValueTask<PlayerRecord> CreateAsync(string username, string nickname, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(username);
        ArgumentException.ThrowIfNullOrEmpty(nickname);
        return new(database.Write<PlayerRecord>((db, _) =>
        {
            var existing = ReadPlayer(db, username);
            if (existing is not null) return existing;
            db.RequireCapacity("players", db.Options.MaxPlayers);
            var user = db.Orm.Insertable(new PlayerRow { Username = username, Nickname = nickname, State = SaveJson.Encode(new SaveState()) })
                .ExecuteReturnBigIdentity();
            return new PlayerRecord(user, username, nickname, 1);
        }, ct));
    }

    public ValueTask<bool> NicknameExistsAsync(string nickname, CancellationToken ct, string? exceptUsername = null) =>
        new(database.Read(db =>
        {
            var query = db.Orm.Queryable<PlayerRow>().Where(row => row.Nickname == nickname);
            if (exceptUsername is not null) query = query.Where(row => row.Username != exceptUsername);
            return query.Any();
        }, ct));

    public ValueTask<bool> HasCreatedCharacterAsync(long userNum, CancellationToken ct) =>
        new(database.Read(db => CharacterCreated(db, userNum), ct));

    // A missing player reads as not created, as the former scalar read of a missing row did.
    private static bool CharacterCreated(SqliteDatabase db, long userNum) =>
        (db.Orm.Queryable<PlayerRow>().Where(row => row.UserNum == userNum).ToList().FirstOrDefault()?.CharacterCreated ?? 0) != 0;

    public ValueTask<PlayerRecord?> CreateCharacterAsync(string username, string nickname, StarterKit kit, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(username);
        ArgumentException.ThrowIfNullOrEmpty(nickname);
        ArgumentNullException.ThrowIfNull(kit);
        return new(database.Write<PlayerRecord?>((db, _) =>
        {
            var existing = ReadPlayer(db, username);
            if (existing is not null && CharacterCreated(db, existing.UserNum)) return null;
            if (db.Orm.Queryable<PlayerRow>().Any(row => row.Nickname == nickname && row.Username != username)) return null;
            var character = kit.WornItemIds.Single(id => id is >= 300001 and <= 300031);
            long user;
            SaveState state;
            if (existing is null)
            {
                db.RequireCapacity("players", db.Options.MaxPlayers);
                user = db.Orm.Insertable(new PlayerRow { Username = username, Nickname = nickname, State = SaveJson.Encode(new SaveState()) })
                    .ExecuteReturnBigIdentity();
                state = new();
            }
            else { user = existing.UserNum; state = db.RequireState(user); }
            foreach (var bag in new[] { InventoryBag.CharacterBag, InventoryBag.EquippedBag })
            {
                var rows = state.Slots.Where(slot => slot.Bag == bag).Select(slot => slot.ItemId is >= 300001 and <= 300031
                    ? slot with { Flags = (byte)(slot.Flags & ~InventorySlotRecord.WornFlagBit) } : slot).ToArray();
                var grants = kit.Slots(user, bag, DateTimeOffset.UtcNow);
                var merged = InventoryAppend.Merge(user, bag, rows, grants).Select(slot =>
                    grants.Any(grant => grant.ItemId == slot.ItemId && (grant.Flags & InventorySlotRecord.WornFlagBit) != 0)
                        ? slot with { Flags = (byte)(slot.Flags | InventorySlotRecord.WornFlagBit) } : slot);
                state.Slots.RemoveAll(slot => slot.Bag == bag);
                state.Slots.AddRange(merged);
            }
            state.Fighters.RemoveAll(fighter => fighter.Slot == 0);
            state.Fighters.Add(new(user, 0, character - 300000, state.Level, character, nickname));
            db.Orm.Updateable<PlayerRow>().SetColumns(row => new PlayerRow { Nickname = nickname, CharacterCreated = 1, TutorialCompleted = 0 })
                .Where(row => row.UserNum == user).ExecuteCommand();
            db.Save(user, state);
            return new(user, username, nickname, state.Level) { ColorIndex = state.ColorIndex };
        }, ct));
    }

    public ValueTask<bool> NeedsTutorialAsync(long userNum, CancellationToken ct) =>
        new(database.Read(db => db.Orm.Queryable<PlayerRow>()
            .Any(row => row.UserNum == userNum && row.CharacterCreated == 1 && row.TutorialCompleted == 0), ct));

    public ValueTask CompleteTutorialAsync(long userNum, CancellationToken ct)
    {
        database.Write((db, _) => db.Orm.Updateable<PlayerRow>().SetColumns(row => new PlayerRow { TutorialCompleted = 1 })
            .Where(row => row.UserNum == userNum).ExecuteCommand(), ct);
        return ValueTask.CompletedTask;
    }

    public ValueTask<int> RenameWithItemAsync(long userNum, string nickname, int itemId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(nickname);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(itemId);
        return new(database.Write((db, _) =>
        {
            var state = db.RequireState(userNum);
            if (db.Orm.Queryable<PlayerRow>().Any(row => row.Nickname == nickname && row.UserNum != userNum)) return 9;
            if (Consume(state, itemId) is null) return 1;
            db.Orm.Updateable<PlayerRow>().SetColumns(row => new PlayerRow { Nickname = nickname })
                .Where(row => row.UserNum == userNum).ExecuteCommand();
            state.Fighters = state.Fighters.Select(fighter => fighter with { Nickname = nickname }).ToList();
            db.Save(userNum, state);
            return 0;
        }, ct));
    }
}
