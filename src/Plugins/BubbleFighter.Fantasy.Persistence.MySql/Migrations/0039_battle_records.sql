CREATE TABLE IF NOT EXISTS player_battle_records (
    user_num BIGINT NOT NULL,
    mode_id INT NOT NULL,
    wins BIGINT NOT NULL DEFAULT 0,
    draws BIGINT NOT NULL DEFAULT 0,
    losses BIGINT NOT NULL DEFAULT 0,
    escapes BIGINT NOT NULL DEFAULT 0,
    knockdowns BIGINT NOT NULL DEFAULT 0,
    kills BIGINT NOT NULL DEFAULT 0,
    rescues BIGINT NOT NULL DEFAULT 0,
    deaths BIGINT NOT NULL DEFAULT 0,
    score BIGINT NOT NULL DEFAULT 0,
    PRIMARY KEY (user_num, mode_id),
    CHECK (mode_id > 0),
    CHECK (wins >= 0 AND draws >= 0 AND losses >= 0 AND escapes >= 0 AND knockdowns >= 0 AND kills >= 0 AND rescues >= 0 AND deaths >= 0 AND score >= 0)
) ENGINE=InnoDB;
