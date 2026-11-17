-- Accepted relationships survive restart; pending consent and bounded duplicate-answer receipts do not.
CREATE TABLE IF NOT EXISTS couples (
    id CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    authority VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    first_user BIGINT NOT NULL,
    second_user BIGINT NOT NULL,
    created_at BIGINT NOT NULL,
    ring_item INT NOT NULL DEFAULT 0,
    PRIMARY KEY (id),
    CONSTRAINT ck_couples_distinct CHECK (first_user <> second_user),
    CONSTRAINT fk_couples_first FOREIGN KEY (first_user) REFERENCES players(user_num) ON DELETE CASCADE,
    CONSTRAINT fk_couples_second FOREIGN KEY (second_user) REFERENCES players(user_num) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin;

-- Uniqueness belongs to the account, not to its first/second position in a relationship.
CREATE TABLE IF NOT EXISTS couple_members (
    authority VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    user_num BIGINT NOT NULL,
    couple_id CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    PRIMARY KEY (authority, user_num),
    KEY ix_couple_members_pair (couple_id),
    CONSTRAINT fk_couple_members_pair FOREIGN KEY (couple_id) REFERENCES couples(id) ON DELETE CASCADE,
    CONSTRAINT fk_couple_members_player FOREIGN KEY (user_num) REFERENCES players(user_num) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin;
