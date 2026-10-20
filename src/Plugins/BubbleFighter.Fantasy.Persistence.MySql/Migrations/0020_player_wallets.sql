-- Player balances. S2C 106 carries six i32 values, but only the fifth (Lucci) and sixth (Cash)
-- are player data: the client stores them at user-info +0x4C / +0x50. The other four are UI
-- switches the server builds per send, so player_money's copies of them are dropped.
CREATE TABLE IF NOT EXISTS player_wallets (
    user_num BIGINT NOT NULL,
    lucci INT NOT NULL,
    cash INT NOT NULL,
    updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (user_num),
    CONSTRAINT ck_player_wallets_balances CHECK (lucci >= 0 AND cash >= 0)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

INSERT INTO player_wallets (user_num, lucci, cash, updated_at)
SELECT user_num, GREATEST(unknown16, 0), GREATEST(unknown20, 0), updated_at FROM player_money
ON DUPLICATE KEY UPDATE lucci = VALUES(lucci), cash = VALUES(cash), updated_at = VALUES(updated_at);

DROP TABLE player_money;
