-- Character / bag slots. S2C 802 gspInventoryUpdateAllInventorySlot and
-- S2C 803 container 1 share sub_10D8550 (64-byte stride). Nested extras from
-- sub_10D9410 are not flattened: inner names are unproven.
-- bag 0 = 802 character bag; bag 1 = 803 container 1.
-- slot_key is 802 unknown3 (i64), the same width family as C2S 804 / 805.
CREATE TABLE IF NOT EXISTS inventory_slots (
    user_num BIGINT NOT NULL,
    bag TINYINT NOT NULL,
    slot_key BIGINT NOT NULL,
    unknown1 INT NOT NULL,
    unknown2 INT NOT NULL,
    unknown4 VARCHAR(255) NOT NULL,
    flags TINYINT NOT NULL,
    updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (user_num, bag, slot_key)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- S2C 803 gspInventoryUpdateInventorySlots container 2 (sub_10D8370,
-- 32-byte stride). Nested extras are not stored.
CREATE TABLE IF NOT EXISTS equipped_slots (
    user_num BIGINT NOT NULL,
    unknown10 BIGINT NOT NULL,
    unknown11 INT NOT NULL,
    unknown12 INT NOT NULL,
    unknown13 TINYINT NOT NULL,
    updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (user_num, unknown10)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- S2C 803 container 3 (sub_10D7E90): counted i32 list. The client passes each
-- value to sub_1135250; class 5 subtracts 300000 (character-item range).
CREATE TABLE IF NOT EXISTS character_item_ids (
    user_num BIGINT NOT NULL,
    ordinal INT NOT NULL,
    item_id INT NOT NULL,
    PRIMARY KEY (user_num, ordinal)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- S2C 810 gspCashInvenUpdateAllInventorySlot CashInvenSlot (sub_1142F20).
-- ordinal is the store order in the counted vector; no wire instance key is proven.
CREATE TABLE IF NOT EXISTS cash_inventory_slots (
    user_num BIGINT NOT NULL,
    ordinal INT NOT NULL,
    unknown1 INT NOT NULL,
    unknown2 INT NOT NULL,
    unknown3 SMALLINT NOT NULL,
    unknown4 SMALLINT NOT NULL,
    unknown5 INT NOT NULL,
    unknown6 INT NOT NULL,
    unknown7 VARCHAR(255) NOT NULL,
    unknown8 TINYINT NOT NULL,
    updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (user_num, ordinal)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
