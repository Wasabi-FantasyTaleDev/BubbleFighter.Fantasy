-- 0062: Durable cash item inbox entries and idempotent claim keys.
CREATE TABLE IF NOT EXISTS cash_inbox_entries (
    cash_slot_id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
    profile VARCHAR(32) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    user_num BIGINT NOT NULL,
    stock_key INT NOT NULL,
    item_key INT NOT NULL,
    quantity INT NOT NULL,
    days INT NOT NULL,
    secondary_id INT NOT NULL DEFAULT 0,
    note VARCHAR(256) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NOT NULL,
    created_at_ms BIGINT NOT NULL,
    expires_at_ms BIGINT NOT NULL,
    state INT NOT NULL DEFAULT 0,
    KEY ix_cash_inbox_owner (profile, user_num, state, cash_slot_id),
    CONSTRAINT fk_cash_inbox_entries_user FOREIGN KEY (user_num) REFERENCES players (user_num) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS cash_inbox_claims (
    profile VARCHAR(32) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    user_num BIGINT NOT NULL,
    claim_key VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    cash_slot_id BIGINT NOT NULL,
    PRIMARY KEY (profile, user_num, claim_key),
    CONSTRAINT fk_cash_inbox_claims_user FOREIGN KEY (user_num) REFERENCES players (user_num) ON DELETE CASCADE,
    CONSTRAINT fk_cash_inbox_claims_slot FOREIGN KEY (cash_slot_id) REFERENCES cash_inbox_entries (cash_slot_id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
