-- A settings scope identifies a proven compatible native control-name namespace, not a transport flag.
-- Writers take the existing players row lock before admitting, replacing or deleting scoped maps.
CREATE TABLE IF NOT EXISTS player_customized_keys (
    user_num BIGINT NOT NULL,
    control_scope VARCHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    mappings_json LONGTEXT NOT NULL,
    PRIMARY KEY (user_num, control_scope),
    CONSTRAINT fk_customized_keys_player FOREIGN KEY (user_num) REFERENCES players(user_num) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin;
