using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.Json.Serialization;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Clans;
using BubbleFighter.Fantasy.Contracts.Fighters;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Progress;
using BubbleFighter.Fantasy.Contracts.Shop;
using BubbleFighter.Fantasy.Contracts.Settings;
using BubbleFighter.Fantasy.Contracts.Wallet;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SqlSugar;

namespace BubbleFighter.Fantasy.Persistence.Sqlite;

// One owned connection serializes all mutations, including identity, purchases and inventory.
/// <summary>
/// One owned SQLite connection serializing every mutation the save store makes — identities, wallets,
/// purchases, inventory, fighters, progress — behind a single gate. The connection is private and
/// unpooled, claimed through a lease file whose inode outlives disposal (deleting it would let a new
/// owner appear while the old file handle still exists), opened only after a quick integrity check,
/// an application-id and schema-version check and a page-quota fit against
/// <see cref="SqliteOptions.MaxDatabaseBytes"/>, and configured with WAL, <c>synchronous=FULL</c> and
/// foreign keys. Writes run in one transaction that the ORM joins and that ends with a truncating WAL
/// checkpoint; if that checkpoint fails after a commit, the store refuses all further operations
/// rather than reporting a committed transaction as rolled back. Schema versions 1 through 6 are
/// migrated in order, each in its own transaction; fresh files are created at version 1 and stamped
/// with the application id.
/// </summary>
internal sealed class SqliteDatabase : IDisposable
{
    // Schema v6 fixes the identity, wallet, inventory and settlement row shapes; raising it means writing a migration step below.
    private const int SchemaVersion = 7;
    private const int ApplicationId = 0x42464653;
    private readonly object _gate = new();
    private readonly FileStream _lease;
    private readonly SqliteConnection _connection;
    private readonly SqlSugarClient _orm;
    private bool _disposed;
    private Exception? _checkpointFailure;
    private readonly ILogger _logger;

    internal SqliteOptions Options { get; }

    /// <summary>
    /// The ORM client bound to the owned connection. Inside <see cref="Write{T}"/> its statements join that write's
    /// transaction; outside a write they run without one, as reads.
    /// </summary>
    internal SqlSugarClient Orm => _orm;

    // SqlSugarCore 5.1 leaves the SQLite statement open when a trigger aborts an Updateable command. The owned
    // Microsoft.Data.Sqlite command is used for the coupled snapshot write so its using scope finalizes the statement
    // on both the commit and exception paths. The transaction is the one installed by Write above.
    private SqliteTransaction? CurrentTransaction => _orm.Ado.Transaction as SqliteTransaction;

    /// <summary>
    /// Validates the options and customized-key rules, claims the lease, opens and checks the save
    /// file, applies pending schema migrations, validates every stored row, and runs a first
    /// checkpoint. Failure at any point releases the lease and propagates: the store never half-opens.
    /// </summary>
    /// <exception cref="ConfigException">Thrown when the options or customized keys are invalid.</exception>
    /// <exception cref="InvalidDataException">Thrown when the file fails its integrity or identity checks, carries
    /// another application's id, an unsupported schema version, an oversized page count, or stored rows that
    /// violate their domain rules or capacity.</exception>
    /// <exception cref="IOException">Thrown when the page quota or WAL mode cannot be applied, or the first checkpoint fails.</exception>
    internal SqliteDatabase(SqliteOptions options, ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
        var validation = new List<ValidationResult>();
        Validator.TryValidateObject(options, new(options), validation, true);
        validation.AddRange(options.Validate());
        if (validation.Count != 0)
            throw new ConfigException(string.Join(" ", validation.Select(result => result.ErrorMessage)));
        ArgumentNullException.ThrowIfNull(options.CustomizedKeys);
        options.CustomizedKeys.Validate();
        Options = new()
        {
            DatabasePath = Path.GetFullPath(options.DatabasePath), MaxDatabaseBytes = options.MaxDatabaseBytes,
            MaxPlayers = options.MaxPlayers, MaxAccounts = options.MaxAccounts,
            MaxMovementActors = options.MaxMovementActors, BusyTimeoutMs = options.BusyTimeoutMs,
            MaxPurchaseHistoryPerPlayer = options.MaxPurchaseHistoryPerPlayer,
            CustomizedKeys = options.CustomizedKeys,
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Options.DatabasePath)!);
        // Keep the lease file after release: deleting it would permit two owners of different inodes.
        _lease = new(Options.DatabasePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        _connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = Options.DatabasePath, Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private, Pooling = false,
            DefaultTimeout = (Options.BusyTimeoutMs + 999) / 1000,
        }.ToString());
        try
        {
            _connection.Open();
            _orm = Bind(_connection, Options.BusyTimeoutMs);
            RawScalar("PRAGMA busy_timeout=" + Options.BusyTimeoutMs.ToString(CultureInfo.InvariantCulture));
            var integrity = Convert.ToString(RawScalar("PRAGMA quick_check"), CultureInfo.InvariantCulture);
            if (integrity != "ok") throw new InvalidDataException("SQLite save failed its integrity check.");
            var version = Convert.ToInt32(RawScalar("PRAGMA user_version"), CultureInfo.InvariantCulture);
            var application = Convert.ToInt32(RawScalar("PRAGMA application_id"), CultureInfo.InvariantCulture);
            var tables = UserTableCount();
            if (version != SchemaVersion && version is not (1 or 2 or 3 or 4 or 5 or 6) && !(version == 0 && application == 0 && tables == 0))
                throw new InvalidDataException($"Unsupported SQLite save schema {version}.");
            if (version != 0 && application != ApplicationId)
                throw new InvalidDataException("SQLite save belongs to another application.");
            var pageSize = Convert.ToInt64(RawScalar("PRAGMA page_size"), CultureInfo.InvariantCulture);
            var pages = Options.MaxDatabaseBytes / pageSize;
            if (Convert.ToInt64(RawScalar("PRAGMA page_count"), CultureInfo.InvariantCulture) > pages)
                throw new InvalidDataException("Existing SQLite save exceeds max_database_bytes.");
            if (Convert.ToInt64(RawScalar("PRAGMA max_page_count=" + pages.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture) != pages)
                throw new InvalidDataException("SQLite could not apply its page quota.");
            if (Convert.ToString(RawScalar("PRAGMA journal_mode=WAL"), CultureInfo.InvariantCulture) != "wal")
                throw new InvalidDataException("SQLite could not enable WAL.");
            RawExecute("PRAGMA synchronous=FULL");
            RawExecute("PRAGMA foreign_keys=ON");
            RawScalar("PRAGMA journal_size_limit=" + Options.MaxDatabaseBytes.ToString(CultureInfo.InvariantCulture));
            if (version == 0)
            {
                using var transaction = _connection.BeginTransaction();
                RawExecute("""
                    CREATE TABLE accounts(username TEXT PRIMARY KEY COLLATE BINARY, password_hash TEXT NOT NULL,
                        banned_until INTEGER);
                    CREATE TABLE players(user_num INTEGER PRIMARY KEY AUTOINCREMENT,
                        username TEXT NOT NULL UNIQUE COLLATE BINARY, nickname TEXT NOT NULL UNIQUE COLLATE NOCASE,
                        character_created INTEGER NOT NULL DEFAULT 0, tutorial_completed INTEGER NOT NULL DEFAULT 0,
                        state TEXT NOT NULL);
                    """, transaction);
                RawExecute("PRAGMA application_id=" + ApplicationId.ToString(CultureInfo.InvariantCulture), transaction);
                RawExecute("PRAGMA user_version=1", transaction);
                transaction.Commit();
                version = 1;
            }
            if (version == 1)
            {
                // Version 2 adds durable launcher sign-ins; only a hash of the current refresh credential is kept.
                using var transaction = _connection.BeginTransaction();
                RawExecute("""
                    CREATE TABLE launcher_sessions(session_id TEXT PRIMARY KEY COLLATE BINARY, username TEXT NOT NULL,
                        token_hash TEXT NOT NULL UNIQUE COLLATE BINARY, created_at INTEGER NOT NULL, rotated_at INTEGER,
                        expires_at INTEGER NOT NULL, revoked_at INTEGER);
                    """, transaction);
                RawExecute("PRAGMA user_version=2", transaction);
                transaction.Commit();
                version = 2;
            }
            if (version == 2)
            {
                // Version 3 adds tutorial-room stage clears, keyed by player and MapMeta stageNum.
                using var transaction = _connection.BeginTransaction();
                RawExecute("""
                    CREATE TABLE tutorial_stage_clears(user_num INTEGER NOT NULL, stage INTEGER NOT NULL,
                        best_count INTEGER NOT NULL DEFAULT 0, best_seconds INTEGER NOT NULL DEFAULT 0,
                        first_cleared_at INTEGER NOT NULL, PRIMARY KEY(user_num, stage));
                    """, transaction);
                RawExecute("PRAGMA user_version=3", transaction);
                transaction.Commit();
                version = 3;
            }
            if (version == 3)
            {
                // Version 4 adds the gift box: gifts between players, hidden separately by sender and receiver. The
                // profile column names the client build whose shop workbook the stock key belongs to.
                using var transaction = _connection.BeginTransaction();
                RawExecute("""
                    CREATE TABLE presents(id INTEGER PRIMARY KEY AUTOINCREMENT, profile TEXT NOT NULL,
                        sender_user_num INTEGER NOT NULL, sender_nickname TEXT NOT NULL,
                        receiver_user_num INTEGER NOT NULL, receiver_nickname TEXT NOT NULL,
                        stock_key INTEGER NOT NULL, message TEXT NOT NULL, sent_at INTEGER NOT NULL,
                        state INTEGER NOT NULL DEFAULT 0 CHECK(state IN (0,1,2)),
                        sender_hidden INTEGER NOT NULL DEFAULT 0 CHECK(sender_hidden IN (0,1)),
                        receiver_hidden INTEGER NOT NULL DEFAULT 0 CHECK(receiver_hidden IN (0,1)));
                    CREATE INDEX ix_presents_receiver ON presents(receiver_user_num, profile, id);
                    CREATE INDEX ix_presents_sender ON presents(sender_user_num, profile, id);
                    """, transaction);
                RawExecute("PRAGMA user_version=4", transaction);
                transaction.Commit();
                version = 4;
            }
            if (version == 4)
            {
                // Version 5 adds the once-only claims of system rewards (gifts the server gives, such as level-up
                // gifts): one row per player, build and claim key, written in the same transaction as the gifts.
                using var transaction = _connection.BeginTransaction();
                RawExecute("""
                    CREATE TABLE present_claims(profile TEXT NOT NULL, user_num INTEGER NOT NULL,
                        claim_key TEXT NOT NULL COLLATE BINARY, claimed_at INTEGER NOT NULL,
                        PRIMARY KEY(profile, user_num, claim_key));
                    """, transaction);
                RawExecute("PRAGMA user_version=5", transaction);
                transaction.Commit();
                version = 5;
            }
            if (version == 5)
            {
                // Version 6 adds round settlement receipts: one row per round start and player, written in the same
                // transaction as the credit, so a repeated settlement finds it and changes nothing.
                using var transaction = _connection.BeginTransaction();
                RawExecute("""
                    CREATE TABLE round_settlements(match_id TEXT NOT NULL COLLATE BINARY, user_num INTEGER NOT NULL,
                        before_level INTEGER NOT NULL, before_exp INTEGER NOT NULL, after_level INTEGER NOT NULL,
                        after_exp INTEGER NOT NULL, lucci_earned INTEGER NOT NULL, settled_at INTEGER NOT NULL,
                        PRIMARY KEY(match_id, user_num));
                    """, transaction);
                RawExecute("PRAGMA user_version=6", transaction);
                transaction.Commit();
                version = 6;
            }
            if (version == 6)
            {
                // Version 7 adds the messenger: accounts keyed by tenant and subject, friendships stored once per
                // direction, per-owner categories and pending requests. nickname_key is the invariant upper-case
                // nickname, so a lookup ignores case for every script the way the other stores do.
                using var transaction = _connection.BeginTransaction();
                RawExecute("""
                    CREATE TABLE messenger_users(id INTEGER PRIMARY KEY AUTOINCREMENT, tenant TEXT NOT NULL COLLATE BINARY,
                        subject TEXT NOT NULL COLLATE BINARY, nickname TEXT NOT NULL, nickname_key TEXT NOT NULL COLLATE BINARY,
                        game_code INTEGER NOT NULL, UNIQUE(tenant, subject));
                    CREATE INDEX ix_messenger_users_nickname ON messenger_users(tenant, nickname_key);
                    CREATE TABLE messenger_relations(owner INTEGER NOT NULL, friend INTEGER NOT NULL,
                        category INTEGER NOT NULL DEFAULT 0, blocked INTEGER NOT NULL DEFAULT 0 CHECK(blocked IN (0, 1)),
                        PRIMARY KEY(owner, friend));
                    CREATE TABLE messenger_categories(owner INTEGER NOT NULL, id INTEGER NOT NULL CHECK(id > 0),
                        name TEXT NOT NULL, game_code INTEGER NOT NULL, PRIMARY KEY(owner, id));
                    CREATE TABLE messenger_requests(handle INTEGER PRIMARY KEY AUTOINCREMENT, sender INTEGER NOT NULL,
                        recipient INTEGER NOT NULL, message TEXT NOT NULL, created_at INTEGER NOT NULL);
                    CREATE INDEX ix_messenger_requests_recipient ON messenger_requests(recipient, handle);
                    CREATE INDEX ix_messenger_requests_sender ON messenger_requests(sender);
                    """, transaction);
                RawExecute("PRAGMA user_version=7", transaction);
                transaction.Commit();
            }
            ValidateExistingState();
            Checkpoint();
        }
        catch
        {
            ReleaseNative();
            throw;
        }
    }

    /// <summary>Runs a read under the store gate. Reads see committed state only; a poisoned checkpoint or disposal throws before the delegate runs.</summary>
    internal T Read<T>(Func<SqliteDatabase, T> read, CancellationToken ct)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            RequireHealthy();
            ct.ThrowIfCancellationRequested();
            return read(this);
        }
    }

    /// <summary>
    /// Runs one unit of work in a single transaction under the store gate: the ORM joins the
    /// transaction for the delegate's duration, the transaction commits only after the delegate
    /// returns without cancellation, and the commit is followed by a truncating WAL checkpoint. A
    /// checkpoint failure after a successful commit poisons the store — later operations throw until
    /// the host reopens the save — because the committed data is durable and pretending otherwise
    /// would invite a destructive reopen.
    /// </summary>
    internal T Write<T>(Func<SqliteDatabase, SqliteTransaction, T> write, CancellationToken ct)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            RequireHealthy();
            ct.ThrowIfCancellationRequested();
            T result;
            using (var transaction = _connection.BeginTransaction())
            {
                // The ORM client joins this transaction only while the write runs; reads never see it.
                _orm.Ado.Transaction = transaction;
                try { result = write(this, transaction); }
                finally { _orm.Ado.Transaction = null; }
                ct.ThrowIfCancellationRequested();
                transaction.Commit();
            }
            // The owned connection has no concurrent readers; truncate the WAL after each commit.
            // A durable commit must not be reported as rolled back when only its checkpoint failed.
            try { Checkpoint(); }
            catch (Exception exception) when (exception is SqliteException or IOException)
            {
                _checkpointFailure = exception;
                _logger.LogError(exception, "SQLite transaction committed but WAL truncation failed; further operations are refused.");
            }
            return result;
        }
    }

    // Raw SQL is kept only for PRAGMA statements, the schema DDL and INSERT OR IGNORE; every other statement goes through Orm.
    internal SqliteCommand RawCommand(string sql, SqliteTransaction? transaction = null, params (string Name, object? Value)[] values)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    internal object? RawScalar(string sql, SqliteTransaction? transaction = null, params (string Name, object? Value)[] values)
    {
        using var command = RawCommand(sql, transaction, values);
        return command.ExecuteScalar();
    }

    internal int RawExecute(string sql, SqliteTransaction? transaction = null, params (string Name, object? Value)[] values)
    {
        using var command = RawCommand(sql, transaction, values);
        return command.ExecuteNonQuery();
    }

    /// <summary>Decodes and validates the player's whole-domain snapshot, or <see langword="null"/> when the player has no row.</summary>
    /// <exception cref="InvalidDataException">Thrown when the stored snapshot violates its domain rules.</exception>
    internal SaveState? State(long user)
    {
        if (user <= 0) throw new ArgumentOutOfRangeException(nameof(user));
        var json = _orm.Queryable<PlayerRow>().Where(player => player.UserNum == user).ToList().FirstOrDefault()?.State;
        if (json is null) return null;
        var state = SaveJson.Decode(json);
        ValidateState(user, state);
        return state;
    }

    /// <summary>Resolves the player's snapshot or throws; the missing-player case is a caller bug, not an empty state.</summary>
    internal SaveState RequireState(long user) =>
        State(user) ?? throw new InvalidOperationException($"SQLite player {user} does not exist.");

    /// <summary>
    /// Replaces the player's whole-domain snapshot in one statement. Must run inside a write
    /// transaction; the affected-row check turns a concurrent identity deletion into an error instead
    /// of a silent lost write.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the player row vanished mid-transaction.</exception>
    internal void Save(long user, SaveState state)
    {
        ValidateState(user, state);
        var json = SaveJson.Encode(state);
        using var command = RawCommand("UPDATE players SET state=$state WHERE user_num=$user", CurrentTransaction,
            ("$state", json), ("$user", user));
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException($"SQLite player {user} disappeared during its transaction.");
    }

    /// <summary>Refuses further identity creation once the table holds its configured maximum of rows.</summary>
    /// <exception cref="InvalidOperationException">Thrown when the table is at capacity.</exception>
    /// <exception cref="ArgumentException">Thrown for an unknown capacity table.</exception>
    internal void RequireCapacity(string table, int maximum)
    {
        var count = table switch
        {
            "accounts" => _orm.Queryable<AccountRow>().Count(),
            "players" => _orm.Queryable<PlayerRow>().Count(),
            "launcher_sessions" => _orm.Queryable<LauncherSessionRow>().Count(),
            _ => throw new ArgumentException("Unknown capacity table.", nameof(table)),
        };
        if (count >= maximum)
            throw new InvalidOperationException($"SQLite {table} capacity {maximum} has been reached.");
    }

    private void Checkpoint()
    {
        using var command = RawCommand("PRAGMA wal_checkpoint(TRUNCATE)");
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.GetInt32(0) != 0)
            throw new IOException("SQLite WAL checkpoint could not complete.");
    }

    // Closes the connection and releases the lease. Save() uses a raw command so its statement is finalized by the
    // using scope; other ORM statements (Queryable, Updateable, Deleteable, Insertable) finalize through their own
    // readers and commands, but SqlSugar does not guarantee finalization on every exception path. The collection
    // here drains any remaining native finalizers before the caller tries to delete or reopen the file.
    private void ReleaseNative()
    {
        try { _orm?.Dispose(); }
        finally
        {
            try { _connection.Dispose(); }
            finally
            {
                _lease.Dispose();
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }
    }

    private void RequireHealthy()
    {
        if (_checkpointFailure is not null)
            throw new IOException("SQLite save needs reopening after a committed transaction's WAL checkpoint failed.", _checkpointFailure);
    }

    private void ValidateExistingState()
    {
        var accounts = _orm.Queryable<AccountRow>().Count();
        var players = _orm.Queryable<PlayerRow>().Count();
        if (accounts > Options.MaxAccounts || players > Options.MaxPlayers)
            throw new InvalidDataException("Existing SQLite save exceeds configured identity capacity.");
        foreach (var account in _orm.Queryable<AccountRow>().ToList())
        {
            if (account.Username.Length == 0) throw new InvalidDataException("SQLite account has an empty name.");
            if (account.BannedUntil is { } banned) _ = DateTimeOffset.FromUnixTimeMilliseconds(banned);
        }
        foreach (var player in _orm.Queryable<PlayerRow>().ToList())
        {
            if (player.Username.Length == 0 || player.Nickname.Length == 0
                || player.CharacterCreated is not (0 or 1) || player.TutorialCompleted is not (0 or 1))
                throw new InvalidDataException("SQLite player identity or onboarding state is invalid.");
            ValidateState(player.UserNum, SaveJson.Decode(player.State));
        }
    }

    // A fresh file has no tables of its own; sqlite_schema lists the tables that do exist, catalog tables excluded.
    private long UserTableCount() => _orm.Queryable<SqliteSchemaRow>().Where(entry => entry.Type == "table").ToList()
        .LongCount(entry => !IsSqliteCatalogName(entry.Name));

    // Matches the former "name NOT LIKE 'sqlite_%'" filter: LIKE folds ASCII letters only and "_" matches any one character.
    private static bool IsSqliteCatalogName(string name)
    {
        if (name.Length < 7) return false;
        for (var i = 0; i < 6; i++)
        {
            var character = name[i] is >= 'A' and <= 'Z' ? (char)(name[i] + 32) : name[i];
            if (character != "sqlite"[i]) return false;
        }
        return true;
    }

    // Statements run on this client use the connection's default command timeout, which the busy timeout already implies.
    private static SqlSugarClient Bind(SqliteConnection connection, int busyTimeoutMs)
    {
        var orm = new SqlSugarClient(new ConnectionConfig
        {
            DbType = SqlSugar.DbType.Sqlite,
            ConnectionString = connection.ConnectionString,
            IsAutoCloseConnection = false,
            InitKeyType = InitKeyType.Attribute,
        });
        orm.Ado.Connection = connection;
        orm.Ado.CommandTimeOut = (busyTimeoutMs + 999) / 1000;
        return orm;
    }

    private void ValidateState(long user, SaveState state)
    {
        if (state.CustomizedKeys is null || state.CustomizedKeys.Count > Options.CustomizedKeys.MaxScopes)
            throw new InvalidDataException("SQLite customized-key scopes are null or exceed their capacity.");
        try
        {
            foreach (var (scope, keys) in state.CustomizedKeys)
            {
                CustomizedKeyLimits.ValidateIdentity(user, scope);
                Options.CustomizedKeys.Copy(keys);
            }
        }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException("SQLite customized-key settings are invalid.", ex);
        }
        if (user <= 0 || state.Level < 1 || state.ActivePreset > InventoryLimits.MaxWeaponPresetSlot
            || state.Slots is null || state.Fighters is null || state.Purchases is null
            || state.Slots.Count > 2 * InventoryLimits.MaxInventorySlotsPerBag
            || state.Fighters.Count > FighterLimits.MaxFighterSlots
            || state.Purchases.Count > Options.MaxPurchaseHistoryPerPlayer
            || state.LastPurchaseSlotKey < 0
            || state.Wallet is { } wallet && (wallet.UserNum != user || wallet.Lucci < 0 || wallet.Cash < 0)
            || state.Progress is { } progress && (progress.UserNum != user || progress.Level != state.Level || progress.Exp < 0)
            || state.ShopOpen is { } shop && (shop.UserNum != user || !Enum.IsDefined(shop.Kind))
            || state.ClanOpen is { } clan && (clan.UserNum != user || !Enum.IsDefined(clan.Kind)))
            throw new InvalidDataException("SQLite player snapshot has invalid identity, values or capacity.");
        foreach (var group in state.Slots.GroupBy(slot => slot.Bag))
            if (group.Key is not (InventoryBag.CharacterBag or InventoryBag.EquippedBag)
                || group.Count() > InventoryLimits.MaxInventorySlotsPerBag)
                throw new InvalidDataException("SQLite inventory bag exceeds its bound or has an unknown bag kind.");
        if (state.Slots.Any(slot => slot.UserNum != user || slot.ItemId <= 0 || slot.SlotKey <= 0 || slot.Quantity < 0)
            || state.Fighters.Select(row => row.Slot).Distinct().Count() != state.Fighters.Count
            || state.Fighters.Any(row => row.UserNum != user || row.Slot >= FighterLimits.MaxFighterSlots || row.Level < 1
                || row.Nickname is null || row.CharacterType < (row.Slot == 0 ? 1 : 0) || row.CharacterType > 31
                || row.EquippedItemId != 0 && row.EquippedItemId - 300000 is not (>= 1 and <= 31))
            || state.Purchases.Any(row => row.UserNum != user || row.Price < 0 || row.MoneyType is not ("cash" or "lucci")))
            throw new InvalidDataException("SQLite player snapshot contains invalid domain rows.");
        // Pages are positional: anything but a full table would shift later pages onto the wrong hotkey.
        if (state.WeaponPresetPages is { } pages
            && (pages.Length != InventoryLimits.MaxWeaponPresetSlot + 1 || pages.Any(page => page is null)))
            throw new InvalidDataException("SQLite weapon-preset pages are not a full five-page table.");
    }

    /// <summary>Checkpoints and closes the save once, releasing the lease while keeping the lease file on disk.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            try { Checkpoint(); }
            finally { ReleaseNative(); }
        }
    }
}

// Schema-versioned domain snapshots keep all of a player's coupled state in one atomic row.
internal sealed class SaveState
{
    // Older saves have no customized overrides; adding this field does not rewrite their other state.
    public Dictionary<string, CustomizedKey[]> CustomizedKeys { get; set; } = new(StringComparer.Ordinal);

    [JsonRequired]
    public int Level { get; set; } = 1;
    // Older snapshots omit this additive field and retain the native color default.
    public ushort ColorIndex { get; set; }
    [JsonRequired]
    public PlayerWallet? Wallet { get; set; }
    [JsonRequired]
    public PlayerProgress? Progress { get; set; }
    [JsonRequired]
    public byte ActivePreset { get; set; }
    // Older snapshots omit this additive field and read as never materialized; null means the derived
    // preset bar, not an empty page table.
    public WeaponPresetPage[]? WeaponPresetPages { get; set; }
    [JsonRequired]
    public List<InventorySlotRecord> Slots { get; set; } = [];
    [JsonRequired]
    public List<FighterRecord> Fighters { get; set; } = [];
    [JsonRequired]
    public List<ShopPurchaseRecord> Purchases { get; set; } = [];
    [JsonRequired]
    public long LastPurchaseSlotKey { get; set; }
    [JsonRequired]
    public ShopOpenRecord? ShopOpen { get; set; }
    [JsonRequired]
    public ClanOpenRecord? ClanOpen { get; set; }
    // The UTC day (yyyy-MM-dd) of the last login the life rule saw; absent in older saves, which read as a first login.
    public string? LastLoginDay { get; set; }
}
