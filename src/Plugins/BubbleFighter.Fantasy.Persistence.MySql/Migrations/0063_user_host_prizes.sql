-- 0063: Durable sponsor (user_host) prize escrows and offline settlement/refund claims.
CREATE TABLE IF NOT EXISTS user_host_escrows (
    profile VARCHAR(32) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    match_id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    -- The cluster node running the match ('' on a single server); startup recovery refunds only its own node's.
    node_id VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL DEFAULT '',
    room_id INT NOT NULL,
    host_user_num BIGINT NOT NULL,
    prize_lucci INT NOT NULL,
    fee_percent INT NOT NULL,
    state INT NOT NULL DEFAULT 0,
    created_at_ms BIGINT NOT NULL,
    settled_at_ms BIGINT NULL,
    PRIMARY KEY (profile, match_id),
    KEY ix_user_host_escrows_state (profile, node_id, state, created_at_ms),
    CONSTRAINT fk_user_host_escrows_host FOREIGN KEY (host_user_num) REFERENCES players (user_num) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS user_host_claims (
    claim_id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
    profile VARCHAR(32) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    user_num BIGINT NOT NULL,
    match_id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    amount_lucci INT NOT NULL,
    reason VARCHAR(32) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    state INT NOT NULL DEFAULT 0,
    created_at_ms BIGINT NOT NULL,
    claimed_at_ms BIGINT NULL,
    UNIQUE KEY ux_user_host_claims_dedup (profile, match_id, user_num, reason),
    KEY ix_user_host_claims_owner (profile, user_num, state, claim_id),
    CONSTRAINT fk_user_host_claims_user FOREIGN KEY (user_num) REFERENCES players (user_num) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
