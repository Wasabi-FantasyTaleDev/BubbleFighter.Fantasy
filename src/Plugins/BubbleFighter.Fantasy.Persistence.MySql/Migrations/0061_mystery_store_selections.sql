-- One row per player and build scope with the current Mysterystore selection: the token the client echoes, the UTC
-- deadline, the bought-card bits and the frozen board (cards, coin quotes and grant terms) as JSON. A refresh or a
-- rotation replaces the row, so the table never grows with history; the token only grows.
CREATE TABLE IF NOT EXISTS mystery_store_selections (
    user_num BIGINT NOT NULL,
    build_key VARCHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    token INT UNSIGNED NOT NULL,
    expires_at BIGINT NOT NULL,
    purchased_mask INT UNSIGNED NOT NULL,
    board_json MEDIUMTEXT NOT NULL,
    PRIMARY KEY (user_num, build_key),
    CONSTRAINT fk_mystery_store_selections_player FOREIGN KEY (user_num) REFERENCES players(user_num) ON DELETE CASCADE
) ENGINE=InnoDB;
