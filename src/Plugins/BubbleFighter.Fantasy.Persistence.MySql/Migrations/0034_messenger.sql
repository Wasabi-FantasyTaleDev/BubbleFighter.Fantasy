-- The messenger: accounts of the native friend list, keyed by tenant (the game server that signed the ticket) and
-- subject (that server's account), so two servers never share an account by name. nickname_key is the invariant
-- upper-case nickname with a binary collation, so a lookup ignores case for every script the same way the other
-- stores do. A friendship is stored once per direction in messenger_relations and written and removed for both
-- accounts in one transaction; blocked is the owner's own flag. Categories are per owner with ids from 1; category 0
-- in a relation is the root. Requests are pending friend requests; created_at is Unix milliseconds.
CREATE TABLE IF NOT EXISTS messenger_users (
    id BIGINT NOT NULL AUTO_INCREMENT,
    tenant VARCHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    subject VARCHAR(128) COLLATE utf8mb4_bin NOT NULL,
    nickname VARCHAR(256) NOT NULL,
    nickname_key VARCHAR(256) COLLATE utf8mb4_bin NOT NULL,
    game_code INT UNSIGNED NOT NULL,
    PRIMARY KEY (id),
    UNIQUE KEY ux_messenger_users_subject (tenant, subject),
    KEY ix_messenger_users_nickname (tenant, nickname_key)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS messenger_relations (
    owner BIGINT NOT NULL,
    friend BIGINT NOT NULL,
    category INT NOT NULL DEFAULT 0,
    blocked TINYINT UNSIGNED NOT NULL DEFAULT 0,
    PRIMARY KEY (owner, friend),
    KEY ix_messenger_relations_friend (friend),
    CONSTRAINT ck_messenger_relations_blocked CHECK (blocked IN (0, 1)),
    CONSTRAINT ck_messenger_relations_category CHECK (category >= 0)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS messenger_categories (
    owner BIGINT NOT NULL,
    id INT NOT NULL,
    name VARCHAR(256) NOT NULL,
    game_code INT UNSIGNED NOT NULL,
    PRIMARY KEY (owner, id),
    CONSTRAINT ck_messenger_categories_id CHECK (id > 0)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS messenger_requests (
    handle INT NOT NULL AUTO_INCREMENT,
    sender BIGINT NOT NULL,
    recipient BIGINT NOT NULL,
    message VARCHAR(1024) NOT NULL,
    created_at BIGINT NOT NULL,
    PRIMARY KEY (handle),
    KEY ix_messenger_requests_recipient (recipient, handle),
    KEY ix_messenger_requests_sender (sender),
    KEY ix_messenger_requests_created (created_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
