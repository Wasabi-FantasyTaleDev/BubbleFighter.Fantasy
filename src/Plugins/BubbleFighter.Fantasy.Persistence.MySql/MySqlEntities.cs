using SqlSugar;

namespace BubbleFighter.Fantasy.Persistence.MySql;

// Entity maps over the schema created by Migrations/*.sql. The migrations stay the source of truth for
// types, keys and defaults; these classes only name the tables and columns the repositories touch.

/// <summary>The <c>players</c> columns this plugin reads or writes (migrations 0003, 0018, 0027).</summary>
[SugarTable("players")]
internal sealed class PlayerRow
{
    [SugarColumn(ColumnName = "user_num", IsPrimaryKey = true)]
    public long UserNum { get; set; }

    [SugarColumn(ColumnName = "level")]
    public int Level { get; set; }

    [SugarColumn(ColumnName = "exp")]
    public long Exp { get; set; }

    [SugarColumn(ColumnName = "active_weapon_preset_slot")]
    public byte ActiveWeaponPresetSlot { get; set; }

    [SugarColumn(ColumnName = "last_login_day", IsNullable = true)]
    public DateTime? LastLoginDay { get; set; }
}

/// <summary>A <c>fighters</c> row (migration 0004).</summary>
[SugarTable("fighters")]
internal sealed class FighterRow
{
    [SugarColumn(ColumnName = "user_num", IsPrimaryKey = true)]
    public long UserNum { get; set; }

    [SugarColumn(ColumnName = "slot", IsPrimaryKey = true)]
    public byte Slot { get; set; }

    [SugarColumn(ColumnName = "character_type")]
    public int CharacterType { get; set; }

    [SugarColumn(ColumnName = "level")]
    public int Level { get; set; }

    [SugarColumn(ColumnName = "equipped_item_id")]
    public int EquippedItemId { get; set; }

    [SugarColumn(ColumnName = "nickname")]
    public string Nickname { get; set; } = "";

    [SugarColumn(ColumnName = "updated_at")]
    public DateTime UpdatedAt { get; set; }
}

/// <summary>A <c>player_wallets</c> row (migration 0020).</summary>
[SugarTable("player_wallets")]
internal sealed class WalletRow
{
    [SugarColumn(ColumnName = "user_num", IsPrimaryKey = true)]
    public long UserNum { get; set; }

    [SugarColumn(ColumnName = "lucci")]
    public int Lucci { get; set; }

    [SugarColumn(ColumnName = "cash")]
    public int Cash { get; set; }

    [SugarColumn(ColumnName = "updated_at")]
    public DateTime UpdatedAt { get; set; }
}

/// <summary>An <c>inventory_slots</c> row (migrations 0005, 0016, 0021, 0022, 0027).</summary>
[SugarTable("inventory_slots")]
internal sealed class InventorySlotRow
{
    [SugarColumn(ColumnName = "user_num", IsPrimaryKey = true)]
    public long UserNum { get; set; }

    [SugarColumn(ColumnName = "bag", IsPrimaryKey = true)]
    public int Bag { get; set; }

    [SugarColumn(ColumnName = "ordinal", IsPrimaryKey = true)]
    public int Ordinal { get; set; }

    [SugarColumn(ColumnName = "slot_key")]
    public long SlotKey { get; set; }

    [SugarColumn(ColumnName = "item_id")]
    public int ItemId { get; set; }

    [SugarColumn(ColumnName = "quantity")]
    public int Quantity { get; set; }

    [SugarColumn(ColumnName = "flags")]
    public byte Flags { get; set; }

    [SugarColumn(ColumnName = "updated_at")]
    public DateTime UpdatedAt { get; set; }

    [SugarColumn(ColumnName = "lifetime_kind")]
    public int LifetimeKind { get; set; }

    [SugarColumn(ColumnName = "expires_at_unix_ms", IsNullable = true)]
    public long? ExpiresAtUnixMilliseconds { get; set; }
}

/// <summary>A <c>purchase_log</c> row (migrations 0017, 0019); <c>id</c> and <c>created_at</c> come from the database.</summary>
[SugarTable("purchase_log")]
internal sealed class PurchaseLogRow
{
    [SugarColumn(ColumnName = "id", IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }

    [SugarColumn(ColumnName = "user_num")]
    public long UserNum { get; set; }

    [SugarColumn(ColumnName = "stock_set_id")]
    public int StockSetId { get; set; }

    [SugarColumn(ColumnName = "item_id")]
    public int ItemId { get; set; }

    [SugarColumn(ColumnName = "money_type")]
    public string MoneyType { get; set; } = "";

    [SugarColumn(ColumnName = "price")]
    public int Price { get; set; }

    [SugarColumn(ColumnName = "slot_key")]
    public long SlotKey { get; set; }

    [SugarColumn(ColumnName = "purchase_key")]
    public int PurchaseKey { get; set; }

    [SugarColumn(ColumnName = "last_slot_key")]
    public long LastSlotKey { get; set; }
}

/// <summary>
/// A <c>presents</c> row (migration 0029): one gift between two players, hidden separately by each. <c>state</c> is a
/// <c>PresentState</c> ordinal; the two hidden columns are 0 or 1.
/// </summary>
[SugarTable("presents")]
internal sealed class PresentRow
{
    [SugarColumn(ColumnName = "id", IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }

    [SugarColumn(ColumnName = "profile")]
    public string Profile { get; set; } = "";

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

    [SugarColumn(ColumnName = "sent_at_unix_ms")]
    public long SentAtUnixMilliseconds { get; set; }

    [SugarColumn(ColumnName = "state")]
    public byte State { get; set; }

    [SugarColumn(ColumnName = "sender_hidden")]
    public byte SenderHidden { get; set; }

    [SugarColumn(ColumnName = "receiver_hidden")]
    public byte ReceiverHidden { get; set; }
}

/// <summary>A <c>present_claims</c> row (migration 0030): the once-only marker of one system reward.</summary>
[SugarTable("present_claims")]
internal sealed class PresentClaimRow
{
    [SugarColumn(ColumnName = "profile", IsPrimaryKey = true)]
    public string Profile { get; set; } = "";

    [SugarColumn(ColumnName = "user_num", IsPrimaryKey = true)]
    public long UserNum { get; set; }

    [SugarColumn(ColumnName = "claim_key", IsPrimaryKey = true)]
    public string ClaimKey { get; set; } = "";

    [SugarColumn(ColumnName = "claimed_at_unix_ms")]
    public long ClaimedAtUnixMilliseconds { get; set; }
}

/// <summary>A <c>round_settlements</c> row (migration 0031): the receipt of one round credited to one player.</summary>
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

    [SugarColumn(ColumnName = "settled_at_unix_ms")]
    public long SettledAtUnixMilliseconds { get; set; }
}

/// <summary>The <c>players</c> columns a gift sender's nickname lookup reads (migration 0003).</summary>
[SugarTable("players")]
internal sealed class PresentPlayerRow
{
    [SugarColumn(ColumnName = "user_num", IsPrimaryKey = true)]
    public long UserNum { get; set; }

    [SugarColumn(ColumnName = "nickname")]
    public string Nickname { get; set; } = "";
}

/// <summary>A <c>shop_sessions</c> row (migration 0011).</summary>
[SugarTable("shop_sessions")]
internal sealed class ShopSessionRow
{
    [SugarColumn(ColumnName = "user_num", IsPrimaryKey = true)]
    public long UserNum { get; set; }

    [SugarColumn(ColumnName = "token")]
    public long Token { get; set; }

    [SugarColumn(ColumnName = "shop_type")]
    public int ShopType { get; set; }

    [SugarColumn(ColumnName = "kind")]
    public int Kind { get; set; }

    [SugarColumn(ColumnName = "opened_at")]
    public DateTime OpenedAt { get; set; }
}

/// <summary>A <c>clans</c> row (migration 0015).</summary>
[SugarTable("clans")]
internal sealed class ClanRow
{
    [SugarColumn(ColumnName = "clan_id", IsPrimaryKey = true, IsIdentity = true)]
    public long ClanId { get; set; }
}

/// <summary>A <c>clan_members</c> row (migration 0015).</summary>
[SugarTable("clan_members")]
internal sealed class ClanMemberRow
{
    [SugarColumn(ColumnName = "clan_id", IsPrimaryKey = true)]
    public long ClanId { get; set; }

    [SugarColumn(ColumnName = "user_num", IsPrimaryKey = true)]
    public long UserNum { get; set; }
}

/// <summary>A <c>clan_opens</c> row (migration 0015).</summary>
[SugarTable("clan_opens")]
internal sealed class ClanOpenRow
{
    [SugarColumn(ColumnName = "user_num", IsPrimaryKey = true)]
    public long UserNum { get; set; }

    [SugarColumn(ColumnName = "kind")]
    public int Kind { get; set; }

    [SugarColumn(ColumnName = "opened_at")]
    public DateTime OpenedAt { get; set; }
}

/// <summary>
/// An <c>accounts</c> row (migration 0002): the launcher credential and ban. <c>id</c>, <c>nickname</c> and the
/// timestamps are not mapped; the account insert is written raw so the duplicate-key code reaches its caller.
/// </summary>
[SugarTable("accounts")]
internal sealed class AccountRow
{
    [SugarColumn(ColumnName = "username")]
    public string Username { get; set; } = "";

    [SugarColumn(ColumnName = "password_hash")]
    public string PasswordHash { get; set; } = "";

    [SugarColumn(ColumnName = "banned_until", IsNullable = true)]
    public DateTime? BannedUntil { get; set; }
}

/// <summary>The <c>players</c> columns the launcher account lookup, registration and enrollment read (migrations 0003, 0018).</summary>
[SugarTable("players")]
internal sealed class PlayerIdentityRow
{
    [SugarColumn(ColumnName = "user_num", IsPrimaryKey = true)]
    public long UserNum { get; set; }

    [SugarColumn(ColumnName = "username")]
    public string Username { get; set; } = "";

    [SugarColumn(ColumnName = "nickname")]
    public string Nickname { get; set; } = "";
}

/// <summary>
/// The shared shape of both branches of the launcher account lookup (a projection, not a table): an account with
/// its player, or a player with no account yet. Nullable columns are the ones a left join can leave empty.
/// </summary>
internal sealed class LauncherAccountSourceRow
{
    public string Username { get; set; } = "";

    public string PasswordHash { get; set; } = "";

    public long? UserNum { get; set; }

    public string? Nickname { get; set; }

    public DateTime? BannedUntil { get; set; }
}

/// <summary>A <c>launcher_sessions</c> row (migration 0025); the rotation and revocation times are null until they happen.</summary>
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
    public DateTime CreatedAt { get; set; }

    [SugarColumn(ColumnName = "rotated_at", IsNullable = true)]
    public DateTime? RotatedAt { get; set; }

    [SugarColumn(ColumnName = "expires_at")]
    public DateTime ExpiresAt { get; set; }

    [SugarColumn(ColumnName = "revoked_at", IsNullable = true)]
    public DateTime? RevokedAt { get; set; }
}

/// <summary>
/// A <c>tutorial_stage_clears</c> row (migration 0026). <c>first_cleared_at</c> is written only by the raw
/// <c>INSERT IGNORE</c> in <see cref="MySqlTutorialStages"/>, so it is not mapped.
/// </summary>
[SugarTable("tutorial_stage_clears")]
internal sealed class TutorialStageRow
{
    [SugarColumn(ColumnName = "user_num", IsPrimaryKey = true)]
    public long UserNum { get; set; }

    [SugarColumn(ColumnName = "stage", IsPrimaryKey = true)]
    public int Stage { get; set; }

    [SugarColumn(ColumnName = "best_count")]
    public int BestCount { get; set; }

    [SugarColumn(ColumnName = "best_seconds")]
    public int BestSeconds { get; set; }
}

/// <summary>
/// A <c>weapon_preset_pages</c> row (migration 0033): one persisted preset page keyed by its list position,
/// because <c>kr-1256 sub_81B820</c> keys the client's per-slot map by record position.
/// </summary>
[SugarTable("weapon_preset_pages")]
internal sealed class WeaponPresetPageRow
{
    [SugarColumn(ColumnName = "user_num", IsPrimaryKey = true)]
    public long UserNum { get; set; }

    /// <summary>The page's position in the S2C 837/833 list, 0..4.</summary>
    [SugarColumn(ColumnName = "page", IsPrimaryKey = true)]
    public byte Page { get; set; }

    [SugarColumn(ColumnName = "word_0")]
    public int Word0 { get; set; }

    [SugarColumn(ColumnName = "word_1")]
    public int Word1 { get; set; }

    [SugarColumn(ColumnName = "word_2")]
    public int Word2 { get; set; }
}

/// <summary>A <c>schema_migrations</c> row (migration 0001); <c>applied_at</c> comes from the database.</summary>
[SugarTable("schema_migrations")]
internal sealed class SchemaMigrationRow
{
    [SugarColumn(ColumnName = "version", IsPrimaryKey = true)]
    public int Version { get; set; }

    [SugarColumn(ColumnName = "name")]
    public string Name { get; set; } = "";
}
