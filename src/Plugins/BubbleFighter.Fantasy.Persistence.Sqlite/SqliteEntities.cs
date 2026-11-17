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

/// <summary>A <c>plaza_game_records</c> row (schema version 14): the day as a day number and the paper's 64 bits.</summary>
[SugarTable("plaza_game_records")]
internal sealed class PlazaGameRecordRow
{
    [SugarColumn(ColumnName = "user_num", IsPrimaryKey = true)]
    public long UserNum { get; set; }

    [SugarColumn(ColumnName = "record_day")]
    public int RecordDay { get; set; }

    [SugarColumn(ColumnName = "fishing_count")]
    public int FishingCount { get; set; }

    [SugarColumn(ColumnName = "fortune_paper")]
    public long FortunePaper { get; set; }
}

/// <summary>A <c>cash_inbox_entries</c> row (schema version 16).</summary>
[SugarTable("cash_inbox_entries")]
internal sealed class CashInboxEntryRow
{
    [SugarColumn(ColumnName = "cash_slot_id", IsPrimaryKey = true, IsIdentity = true)]
    public long CashSlotId { get; set; }

    [SugarColumn(ColumnName = "profile")]
    public string Profile { get; set; } = "";

    [SugarColumn(ColumnName = "user_num")]
    public long UserNum { get; set; }

    [SugarColumn(ColumnName = "stock_key")]
    public int StockKey { get; set; }

    [SugarColumn(ColumnName = "item_key")]
    public int ItemKey { get; set; }

    [SugarColumn(ColumnName = "quantity")]
    public int Quantity { get; set; }

    [SugarColumn(ColumnName = "days")]
    public int Days { get; set; }

    [SugarColumn(ColumnName = "secondary_id")]
    public int SecondaryId { get; set; }

    [SugarColumn(ColumnName = "note")]
    public string Note { get; set; } = "";

    [SugarColumn(ColumnName = "created_at_ms")]
    public long CreatedAtMs { get; set; }

    [SugarColumn(ColumnName = "expires_at_ms")]
    public long ExpiresAtMs { get; set; }

    [SugarColumn(ColumnName = "state")]
    public int State { get; set; }
}

/// <summary>A <c>cash_inbox_claims</c> idempotency receipt row (schema version 16).</summary>
[SugarTable("cash_inbox_claims")]
internal sealed class CashInboxClaimRow
{
    [SugarColumn(ColumnName = "profile", IsPrimaryKey = true)]
    public string Profile { get; set; } = "";

    [SugarColumn(ColumnName = "user_num", IsPrimaryKey = true)]
    public long UserNum { get; set; }

    [SugarColumn(ColumnName = "claim_key", IsPrimaryKey = true)]
    public string ClaimKey { get; set; } = "";

    [SugarColumn(ColumnName = "cash_slot_id")]
    public long CashSlotId { get; set; }
}

/// <summary>A <c>user_host_escrows</c> row (schema version 17).</summary>
[SugarTable("user_host_escrows")]
internal sealed class UserHostEscrowRow
{
    [SugarColumn(ColumnName = "profile", IsPrimaryKey = true)]
    public string Profile { get; set; } = "";

    [SugarColumn(ColumnName = "match_id", IsPrimaryKey = true)]
    public string MatchId { get; set; } = "";

    [SugarColumn(ColumnName = "node_id")]
    public string NodeId { get; set; } = "";

    [SugarColumn(ColumnName = "room_id")]
    public int RoomId { get; set; }

    [SugarColumn(ColumnName = "host_user_num")]
    public long HostUserNum { get; set; }

    [SugarColumn(ColumnName = "prize_lucci")]
    public int PrizeLucci { get; set; }

    [SugarColumn(ColumnName = "fee_percent")]
    public int FeePercent { get; set; }

    [SugarColumn(ColumnName = "state")]
    public int State { get; set; }

    [SugarColumn(ColumnName = "created_at_ms")]
    public long CreatedAtMs { get; set; }

    [SugarColumn(ColumnName = "settled_at_ms", IsNullable = true)]
    public long? SettledAtMs { get; set; }
}

/// <summary>A <c>user_host_claims</c> row (schema version 17).</summary>
[SugarTable("user_host_claims")]
internal sealed class UserHostClaimRow
{
    [SugarColumn(ColumnName = "claim_id", IsPrimaryKey = true, IsIdentity = true)]
    public long ClaimId { get; set; }

    [SugarColumn(ColumnName = "profile")]
    public string Profile { get; set; } = "";

    [SugarColumn(ColumnName = "user_num")]
    public long UserNum { get; set; }

    [SugarColumn(ColumnName = "match_id")]
    public string MatchId { get; set; } = "";

    [SugarColumn(ColumnName = "amount_lucci")]
    public int AmountLucci { get; set; }

    [SugarColumn(ColumnName = "reason")]
    public string Reason { get; set; } = "";

    [SugarColumn(ColumnName = "state")]
    public int State { get; set; }

    [SugarColumn(ColumnName = "created_at_ms")]
    public long CreatedAtMs { get; set; }

    [SugarColumn(ColumnName = "claimed_at_ms", IsNullable = true)]
    public long? ClaimedAtMs { get; set; }
}

/// <summary>A <c>clans</c> row (schema version 18); <c>disband_at</c> is Unix milliseconds or null.</summary>
[SugarTable("clans")]
internal sealed class ClanRow
{
    [SugarColumn(ColumnName = "clan_id", IsPrimaryKey = true, IsIdentity = true)]
    public long ClanId { get; set; }

    [SugarColumn(ColumnName = "name")]
    public string Name { get; set; } = "";

    [SugarColumn(ColumnName = "master_user_num")]
    public long MasterUserNum { get; set; }

    [SugarColumn(ColumnName = "introduction")]
    public string Introduction { get; set; } = "";

    [SugarColumn(ColumnName = "notice")]
    public string Notice { get; set; } = "";

    [SugarColumn(ColumnName = "level")]
    public int Level { get; set; }

    [SugarColumn(ColumnName = "points")]
    public int Points { get; set; }

    [SugarColumn(ColumnName = "mark")]
    public int Mark { get; set; }

    [SugarColumn(ColumnName = "capacity")]
    public int Capacity { get; set; }

    [SugarColumn(ColumnName = "recruitment")]
    public int Recruitment { get; set; }

    [SugarColumn(ColumnName = "created_at")]
    public long CreatedAt { get; set; }

    [SugarColumn(ColumnName = "disband_at", IsNullable = true)]
    public long? DisbandAt { get; set; }
}

/// <summary>A <c>clan_members</c> row (schema version 18): one per player, applicants included.</summary>
[SugarTable("clan_members")]
internal sealed class ClanMemberRow
{
    [SugarColumn(ColumnName = "user_num", IsPrimaryKey = true)]
    public long UserNum { get; set; }

    [SugarColumn(ColumnName = "clan_id")]
    public long ClanId { get; set; }

    [SugarColumn(ColumnName = "nickname")]
    public string Nickname { get; set; } = "";

    [SugarColumn(ColumnName = "level")]
    public int Level { get; set; }

    [SugarColumn(ColumnName = "role")]
    public int Role { get; set; }

    [SugarColumn(ColumnName = "joined_at")]
    public long JoinedAt { get; set; }

    [SugarColumn(ColumnName = "seen_at")]
    public long SeenAt { get; set; }
}

/// <summary>A <c>clan_posts</c> row (schema version 18): one board post.</summary>
[SugarTable("clan_posts")]
internal sealed class ClanPostRow
{
    [SugarColumn(ColumnName = "post_id", IsPrimaryKey = true, IsIdentity = true)]
    public long PostId { get; set; }

    [SugarColumn(ColumnName = "clan_id")]
    public long ClanId { get; set; }

    [SugarColumn(ColumnName = "user_num")]
    public long UserNum { get; set; }

    [SugarColumn(ColumnName = "nickname")]
    public string Nickname { get; set; } = "";

    [SugarColumn(ColumnName = "level")]
    public int Level { get; set; }

    [SugarColumn(ColumnName = "body")]
    public string Body { get; set; } = "";

    [SugarColumn(ColumnName = "created_at")]
    public long CreatedAt { get; set; }
}
