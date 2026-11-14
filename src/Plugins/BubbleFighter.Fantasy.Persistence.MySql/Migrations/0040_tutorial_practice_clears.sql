-- Successful supplementary practice is distinct from dismissed onboarding and numbered MapMeta stages.
CREATE TABLE IF NOT EXISTS tutorial_practice_clears (
    profile VARCHAR(32) NOT NULL,
    user_num BIGINT NOT NULL,
    map_key VARCHAR(128) NOT NULL,
    PRIMARY KEY (profile, user_num, map_key)
) ENGINE=InnoDB DEFAULT CHARSET=ascii COLLATE=ascii_bin;
