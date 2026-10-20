-- S2C 106 gspUserMoneyStat (inline parser at 0x9D3336). Six i32 fields.
-- unknown16 / unknown20 are stored at user-info +76 / +80. Currency meaning
-- of those two stores is unverified, so columns keep the packet names.
CREATE TABLE IF NOT EXISTS player_money (
    user_num BIGINT NOT NULL,
    unknown0 INT NOT NULL,
    unknown4 INT NOT NULL,
    unknown8 INT NOT NULL,
    unknown12 INT NOT NULL,
    unknown16 INT NOT NULL,
    unknown20 INT NOT NULL,
    updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (user_num)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
