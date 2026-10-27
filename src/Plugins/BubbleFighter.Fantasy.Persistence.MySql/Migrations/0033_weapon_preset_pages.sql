-- Per-player weapon-preset pages, the server-owned bar content behind the default-off
-- persist_preset_pages switch. The swap chain has no client-to-server save packet, so the page contents
-- the S2C 837/833 records carry must live here once a player's pages are materialized. One row per page:
-- five pages, three u32 words each (kr-1256 sub_115C000 reads the twelve-byte record; kr-1256 sub_81B820
-- keys the client's page map by list position, so the page index is the row key beside the player). Only
-- word 0 is consumed by the clients (kr-1256 sub_808F10's primary-family check; kr-812 sub_97A8D0's
-- /1000 == 204 gate, projected by the legacy writer); words 1 and 2 are stored verbatim because their
-- meaning is not traced.
CREATE TABLE IF NOT EXISTS weapon_preset_pages (
    user_num BIGINT NOT NULL,
    page TINYINT UNSIGNED NOT NULL,
    word_0 INT NOT NULL DEFAULT 0,
    word_1 INT NOT NULL DEFAULT 0,
    word_2 INT NOT NULL DEFAULT 0,
    PRIMARY KEY (user_num, page),
    CONSTRAINT fk_weapon_preset_pages_player FOREIGN KEY (user_num) REFERENCES players(user_num) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=ascii COLLATE=ascii_bin;
