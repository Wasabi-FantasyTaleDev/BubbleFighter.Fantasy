-- Login player identity. Columns used by IPlayerRepository:
--   FindByUsernameAsync selects user_num, username, nickname, level
--   CreateAsync inserts username, nickname, level, created_at and returns user_num
-- exp and last_login_at are reserved; the current repository does not read or write them.
CREATE TABLE IF NOT EXISTS players (
    user_num BIGINT NOT NULL AUTO_INCREMENT,
    username VARCHAR(64) NOT NULL,
    nickname VARCHAR(64) NOT NULL,
    level INT NOT NULL DEFAULT 1,
    exp BIGINT NOT NULL DEFAULT 0,
    created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    last_login_at DATETIME NULL,
    PRIMARY KEY (user_num),
    UNIQUE KEY uq_players_username (username),
    UNIQUE KEY uq_players_nickname (nickname)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
