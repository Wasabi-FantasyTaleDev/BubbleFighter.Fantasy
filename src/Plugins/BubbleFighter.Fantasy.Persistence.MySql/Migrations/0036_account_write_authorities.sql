CREATE TABLE IF NOT EXISTS account_write_authorities (
    user_num BIGINT NOT NULL,
    token CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    lock_name CHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    owner_connection_id BIGINT UNSIGNED NOT NULL,
    PRIMARY KEY (user_num)
) ENGINE=InnoDB;
