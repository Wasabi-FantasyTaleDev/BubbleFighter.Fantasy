-- Last shop-open token per player. No catalog table: ShopItemCardInfo and
-- ShopMainCategory field meanings past the empty/default wire form are unproven.
-- Columns used by MySqlShopSessionStore.RememberOpenAsync.
CREATE TABLE IF NOT EXISTS shop_sessions (
    user_num BIGINT NOT NULL,
    token BIGINT NOT NULL,
    shop_type INT NOT NULL,
    kind TINYINT NOT NULL,
    opened_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (user_num)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
