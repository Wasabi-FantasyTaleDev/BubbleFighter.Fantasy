using SqlSugar;

namespace BubbleFighter.Fantasy.Persistence.Sqlite;

// Entity maps over the tables created by the schema chain in SqliteDatabase. The DDL stays the source of truth for types,
// keys and defaults; these classes only name the tables and columns the repositories touch. Timestamps are Unix
// milliseconds stored as INTEGER, which is why no entity has a DateTime property.

/// <summary>The <c>accounts</c> table (schema version 1); <c>banned_until</c> is Unix milliseconds or null.</summary>
[SugarTable("accounts")]
internal sealed class AccountRow
{
    [SugarColumn(ColumnName = "username", IsPrimaryKey = true)]
    public string Username { get; set; } = "";

    [SugarColumn(ColumnName = "password_hash")]
    public string PasswordHash { get; set; } = "";

    [SugarColumn(ColumnName = "banned_until", IsNullable = true)]
    public long? BannedUntil { get; set; }
}

/// <summary>The <c>players</c> table (schema version 1); <c>state</c> is the JSON <see cref="SaveState"/> snapshot.</summary>
[SugarTable("players")]
internal sealed class PlayerRow
{
    [SugarColumn(ColumnName = "user_num", IsPrimaryKey = true, IsIdentity = true)]
    public long UserNum { get; set; }

    [SugarColumn(ColumnName = "username")]
    public string Username { get; set; } = "";

    [SugarColumn(ColumnName = "nickname")]
    public string Nickname { get; set; } = "";

    [SugarColumn(ColumnName = "character_created")]
    public int CharacterCreated { get; set; }

    [SugarColumn(ColumnName = "tutorial_completed")]
    public int TutorialCompleted { get; set; }

    [SugarColumn(ColumnName = "state")]
    public string State { get; set; } = "";
}

/// <summary>The <c>launcher_sessions</c> table (schema version 2); only a hash of the refresh credential is stored.</summary>
[SugarTable("launcher_sessions")]
internal sealed class LauncherSessionRow
{
    [SugarColumn(ColumnName = "session_id", IsPrimaryKey = true)]
    public string SessionId { get; set; } = "";

    [SugarColumn(ColumnName = "username")]
    public string Username { get; set; } = "";

    [SugarColumn(ColumnName = "token_hash")]
    public string TokenHash { get; set; } = "";

    [SugarColumn(ColumnName = "created_at")]
    public long CreatedAt { get; set; }

    [SugarColumn(ColumnName = "rotated_at", IsNullable = true)]
    public long? RotatedAt { get; set; }

    [SugarColumn(ColumnName = "expires_at")]
    public long ExpiresAt { get; set; }

    [SugarColumn(ColumnName = "revoked_at", IsNullable = true)]
    public long? RevokedAt { get; set; }
}

/// <summary>A <c>tutorial_stage_clears</c> row (schema version 3), keyed by player and MapMeta stage.</summary>
[SugarTable("tutorial_stage_clears")]
internal sealed class TutorialStageClearRow
{
    [SugarColumn(ColumnName = "user_num", IsPrimaryKey = true)]
    public long UserNum { get; set; }

    [SugarColumn(ColumnName = "stage", IsPrimaryKey = true)]
    public int Stage { get; set; }

    [SugarColumn(ColumnName = "best_count")]
    public int BestCount { get; set; }

    [SugarColumn(ColumnName = "best_seconds")]
    public int BestSeconds { get; set; }

    [SugarColumn(ColumnName = "first_cleared_at")]
    public long FirstClearedAt { get; set; }
}

/// <summary>A <c>round_settlements</c> receipt (schema version 6): one per round start and player.</summary>
[SugarTable("round_settlements")]
internal sealed class RoundSettlementRow
{
    [SugarColumn(ColumnName = "match_id", IsPrimaryKey = true)]
    public string MatchId { get; set; } = "";

    [SugarColumn(ColumnName = "user_num", IsPrimaryKey = true)]
    public long UserNum { get; set; }

    [SugarColumn(ColumnName = "before_level")]
    public int BeforeLevel { get; set; }

    [SugarColumn(ColumnName = "before_exp")]
    public long BeforeExp { get; set; }

    [SugarColumn(ColumnName = "after_level")]
    public int AfterLevel { get; set; }

    [SugarColumn(ColumnName = "after_exp")]
    public long AfterExp { get; set; }

    [SugarColumn(ColumnName = "lucci_earned")]
    public int LucciEarned { get; set; }

    [SugarColumn(ColumnName = "settled_at")]
    public long SettledAt { get; set; }
}

/// <summary>A <c>presents</c> row (schema version 4): one gift, hidden separately by sender and receiver.</summary>
[SugarTable("presents")]
internal sealed class PresentRow
{
    [SugarColumn(ColumnName = "id", IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }

    [SugarColumn(ColumnName = "profile")]
    public string ProfileId { get; set; } = "";

    [SugarColumn(ColumnName = "sender_user_num")]
    public long SenderUserNum { get; set; }

    [SugarColumn(ColumnName = "sender_nickname")]
    public string SenderNickname { get; set; } = "";

    [SugarColumn(ColumnName = "receiver_user_num")]
    public long ReceiverUserNum { get; set; }

    [SugarColumn(ColumnName = "receiver_nickname")]
    public string ReceiverNickname { get; set; } = "";

    [SugarColumn(ColumnName = "stock_key")]
    public int StockKey { get; set; }

    [SugarColumn(ColumnName = "message")]
    public string Message { get; set; } = "";

    [SugarColumn(ColumnName = "sent_at")]
    public long SentAt { get; set; }

    // A PresentState ordinal: 0 pending, 1 accepted, 2 discarded.
    [SugarColumn(ColumnName = "state")]
    public int State { get; set; }

    [SugarColumn(ColumnName = "sender_hidden")]
    public int SenderHidden { get; set; }

    [SugarColumn(ColumnName = "receiver_hidden")]
    public int ReceiverHidden { get; set; }
}

/// <summary>One row of SQLite's own <c>sqlite_schema</c> catalog, read to tell a fresh file from one with tables.</summary>
[SugarTable("sqlite_schema")]
internal sealed class SqliteSchemaRow
{
    [SugarColumn(ColumnName = "type")]
    public string Type { get; set; } = "";

    [SugarColumn(ColumnName = "name")]
    public string Name { get; set; } = "";
}
