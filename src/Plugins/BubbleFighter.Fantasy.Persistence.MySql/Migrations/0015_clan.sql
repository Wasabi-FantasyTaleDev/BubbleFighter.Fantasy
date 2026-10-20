-- Clan identity and last clan-UI open. Search-list and roster payloads are not
-- stored: those counted rows are clan-server NxPacket bodies and must not be
-- invented. Columns used by MySqlClanStore.
CREATE TABLE IF NOT EXISTS clans (
    clan_id BIGINT NOT NULL AUTO_INCREMENT,
    created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (clan_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS clan_members (
    clan_id BIGINT NOT NULL,
    user_num BIGINT NOT NULL,
    joined_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (clan_id, user_num),
    UNIQUE KEY uq_clan_members_user (user_num),
    CONSTRAINT fk_clan_members_clan FOREIGN KEY (clan_id) REFERENCES clans (clan_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS clan_opens (
    user_num BIGINT NOT NULL,
    kind TINYINT NOT NULL,
    opened_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (user_num)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
