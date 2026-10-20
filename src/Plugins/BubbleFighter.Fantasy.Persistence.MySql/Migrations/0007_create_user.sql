-- C2S 32 gcpReqCreateUserInfo submit (CreateUserStage + name + two i32).
-- Name-taken refuse codes on S2C 13 / 14 are unproven and are not stored.
-- user_num may be 0 when the creating session has no login identity yet.
CREATE TABLE IF NOT EXISTS create_user_submits (
    name VARCHAR(64) NOT NULL,
    user_num BIGINT NOT NULL,
    unknown1 INT NOT NULL,
    unknown2 INT NOT NULL,
    created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (name)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
