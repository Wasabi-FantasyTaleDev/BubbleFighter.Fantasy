-- The gift box (packets/present.yaml): shop items given to another player. A gift
-- is stored when the sender pays for it and delivered when the receiver takes it, which marks it taken in the
-- same transaction that grants the items (state 1). profile names the client build whose shop workbook stock_key
-- belongs to, because a stock key means nothing in another build's workbook. The sender and the receiver hide the
-- row from their own lists independently (sender_hidden, receiver_hidden); it is deleted when both have. state is
-- 0 pending, 1 taken, 2 thrown away unopened.
CREATE TABLE IF NOT EXISTS presents (
    id BIGINT NOT NULL AUTO_INCREMENT,
    profile VARCHAR(32) NOT NULL,
    sender_user_num BIGINT NOT NULL,
    sender_nickname VARCHAR(64) NOT NULL,
    receiver_user_num BIGINT NOT NULL,
    receiver_nickname VARCHAR(64) NOT NULL,
    stock_key INT NOT NULL,
    message VARCHAR(1024) NOT NULL,
    sent_at_unix_ms BIGINT NOT NULL,
    state TINYINT UNSIGNED NOT NULL DEFAULT 0,
    sender_hidden TINYINT UNSIGNED NOT NULL DEFAULT 0,
    receiver_hidden TINYINT UNSIGNED NOT NULL DEFAULT 0,
    PRIMARY KEY (id),
    KEY ix_presents_receiver (receiver_user_num, profile, id),
    KEY ix_presents_sender (sender_user_num, profile, id),
    CONSTRAINT ck_presents_state CHECK (state IN (0, 1, 2)),
    CONSTRAINT ck_presents_hidden CHECK (sender_hidden IN (0, 1) AND receiver_hidden IN (0, 1))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
