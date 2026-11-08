-- A node hands an account to another node of the same data domain by writing this row while it still holds the
-- account lock and then releasing the lock. Until the deadline only the named migration may claim the account;
-- the source may withdraw it. Deadlines use the database clock so node clock skew cannot extend a handoff.
CREATE TABLE IF NOT EXISTS account_handoffs (
    user_num BIGINT NOT NULL,
    migration_id CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    source_node VARCHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    destination_node VARCHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    state ENUM('pending', 'claimed', 'withdrawn', 'expired') NOT NULL,
    created_at DATETIME(3) NOT NULL,
    deadline_at DATETIME(3) NOT NULL,
    settled_at DATETIME(3) NULL,
    PRIMARY KEY (user_num)
) ENGINE=InnoDB;
