-- Round settlement receipts: one row per round start (match_id) and player, inserted in the same transaction that
-- stores the player's new level and experience and credits the round's Lucci. A repeated settlement for the same
-- match and player finds the row and changes nothing, so retrying after an unacknowledged commit cannot credit
-- twice. The row keeps the progress before and after so the repeat returns the first result.
CREATE TABLE IF NOT EXISTS round_settlements (
    match_id VARCHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    user_num BIGINT NOT NULL,
    before_level INT NOT NULL,
    before_exp BIGINT NOT NULL,
    after_level INT NOT NULL,
    after_exp BIGINT NOT NULL,
    lucci_earned INT NOT NULL,
    settled_at_unix_ms BIGINT NOT NULL,
    PRIMARY KEY (match_id, user_num)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
