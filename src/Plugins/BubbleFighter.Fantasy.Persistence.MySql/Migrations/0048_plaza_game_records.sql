-- One row per player with the current day's plaza mini-game record: the fish landed that day and the fortune
-- paper drawn that day (0 for none). A row of an earlier day reads as that day's empty record and is replaced by the
-- next write, so the table never grows with history.
CREATE TABLE IF NOT EXISTS plaza_game_records (
    user_num BIGINT NOT NULL,
    record_day DATE NOT NULL,
    fishing_count INT NOT NULL DEFAULT 0,
    fortune_paper BIGINT UNSIGNED NOT NULL DEFAULT 0,
    PRIMARY KEY (user_num),
    CONSTRAINT fk_plaza_game_records_player FOREIGN KEY (user_num) REFERENCES players(user_num) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=ascii COLLATE=ascii_bin;
