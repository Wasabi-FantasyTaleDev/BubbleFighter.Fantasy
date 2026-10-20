-- Tutorial-room stages each player has cleared, keyed by the stage's MapMeta stageNum, with the best count
-- (higher is better) and the best time in seconds (lower is better, 0 for none) the room shows beside it.
CREATE TABLE IF NOT EXISTS tutorial_stage_clears (
    user_num BIGINT NOT NULL,
    stage INT NOT NULL,
    best_count INT NOT NULL DEFAULT 0,
    best_seconds INT NOT NULL DEFAULT 0,
    first_cleared_at DATETIME(6) NOT NULL,
    PRIMARY KEY (user_num, stage)
) ENGINE=InnoDB DEFAULT CHARSET=ascii COLLATE=ascii_bin;
