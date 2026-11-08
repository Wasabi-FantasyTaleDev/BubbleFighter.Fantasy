CREATE TABLE IF NOT EXISTS player_loadouts (
    user_num BIGINT NOT NULL,
    build_key VARCHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    state_json MEDIUMTEXT NOT NULL,
    PRIMARY KEY (user_num, build_key)
) ENGINE=InnoDB;
