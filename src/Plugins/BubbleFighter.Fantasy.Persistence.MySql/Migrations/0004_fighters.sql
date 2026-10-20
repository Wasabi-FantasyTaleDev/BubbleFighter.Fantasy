-- Login fighter slots for S2C 133 gspRepFighterInfo (sub_99A430 / sub_5D19B0)
-- and the nested FighterInfo on S2C 105 gspUserStat (sub_115A6D0).
-- character_type is the FighterConfig Field id (1..31). Type 0 is rejected by
-- sub_4BD3E0 / sub_64C070 on the live slot; it is stored only for an unused
-- partner slot (133 object +584 constructor default).
-- level is FighterInfo +36 (copied to user-info dword [15] in sub_9A5C40).
-- equipped_item_id is the inferred 300000+T character-item class from
-- sub_EF12E0 / sub_1135760 when T is in 1..31; 0 means none stored.
CREATE TABLE IF NOT EXISTS fighters (
    user_num BIGINT NOT NULL,
    slot TINYINT NOT NULL,
    character_type INT NOT NULL,
    level INT NOT NULL,
    equipped_item_id INT NOT NULL,
    nickname VARCHAR(64) NOT NULL,
    updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (user_num, slot)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
