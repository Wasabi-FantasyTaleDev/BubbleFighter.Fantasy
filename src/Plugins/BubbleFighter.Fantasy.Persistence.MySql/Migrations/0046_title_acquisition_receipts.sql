-- A title's one-time acquisition bonuses remain consumed after its inventory item expires or is removed.
CREATE TABLE IF NOT EXISTS player_title_acquisition_receipts (
    user_num BIGINT NOT NULL,
    catalog_scope VARCHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    title_id INT NOT NULL,
    stock_id INT NOT NULL,
    fingerprint CHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    PRIMARY KEY (user_num, catalog_scope, title_id),
    CONSTRAINT fk_title_acquisition_receipts_player FOREIGN KEY (user_num) REFERENCES players(user_num) ON DELETE CASCADE
) ENGINE=InnoDB;
