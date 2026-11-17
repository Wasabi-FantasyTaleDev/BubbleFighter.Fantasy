-- Clans as the clan-server connection serves them: the clan's own row, a member or applicant row per player
-- (role 1 is an application, 100 a member, 101 the master, 102 the deputy) and the clan board. A clan belongs to
-- no client build, so no column names one. Columns used by MySqlClanDirectory.
-- Ids start at 1000: the client reserves clan id 99 for its training clan.
ALTER TABLE clans
    ADD COLUMN name VARCHAR(64) NULL,
    ADD COLUMN master_user_num BIGINT NOT NULL DEFAULT 0,
    ADD COLUMN introduction VARCHAR(1024) NOT NULL DEFAULT '',
    ADD COLUMN notice VARCHAR(1024) NOT NULL DEFAULT '',
    ADD COLUMN level INT NOT NULL DEFAULT 1,
    ADD COLUMN points INT NOT NULL DEFAULT 0,
    ADD COLUMN mark INT NOT NULL DEFAULT 0,
    ADD COLUMN capacity INT NOT NULL DEFAULT 30,
    ADD COLUMN recruitment INT NOT NULL DEFAULT 1,
    ADD COLUMN disband_at DATETIME NULL,
    ADD UNIQUE KEY uq_clans_name (name),
    AUTO_INCREMENT = 1000;

ALTER TABLE clan_members
    ADD COLUMN nickname VARCHAR(64) NOT NULL DEFAULT '',
    ADD COLUMN level INT NOT NULL DEFAULT 1,
    ADD COLUMN role INT NOT NULL DEFAULT 100,
    ADD COLUMN seen_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP;

CREATE TABLE IF NOT EXISTS clan_posts (
    post_id BIGINT NOT NULL AUTO_INCREMENT,
    clan_id BIGINT NOT NULL,
    user_num BIGINT NOT NULL,
    nickname VARCHAR(64) NOT NULL DEFAULT '',
    level INT NOT NULL DEFAULT 1,
    body VARCHAR(2048) NOT NULL,
    created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (post_id),
    KEY ix_clan_posts_clan (clan_id, post_id),
    CONSTRAINT fk_clan_posts_clan FOREIGN KEY (clan_id) REFERENCES clans (clan_id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
