-- Title ownership remains in inventory_slots; only catalogue medal balances and applied credit events live here.
CREATE TABLE IF NOT EXISTS player_title_medals (
    user_num BIGINT NOT NULL,
    catalog_scope VARCHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    state_json JSON NOT NULL,
    PRIMARY KEY (user_num, catalog_scope),
    CONSTRAINT fk_title_medals_player FOREIGN KEY (user_num) REFERENCES players(user_num) ON DELETE CASCADE
) ENGINE=InnoDB;

-- One row per applied medal event, so a replay is a primary-key lookup and the balance row never grows with history.
CREATE TABLE IF NOT EXISTS player_title_medal_receipts (
    user_num BIGINT NOT NULL,
    catalog_scope VARCHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    grant_key VARCHAR(128) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    source VARCHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    fingerprint CHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    PRIMARY KEY (user_num, catalog_scope, grant_key),
    CONSTRAINT fk_title_medal_receipts_player FOREIGN KEY (user_num) REFERENCES players(user_num) ON DELETE CASCADE
) ENGINE=InnoDB;
