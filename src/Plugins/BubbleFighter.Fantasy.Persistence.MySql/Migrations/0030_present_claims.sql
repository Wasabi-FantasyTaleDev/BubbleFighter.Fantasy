-- Once-only claims of system rewards: gifts the server gives through the gift box, such as the gifts of a reached
-- level (packets/present.yaml). A reward is stored together with its claim row in one transaction, so a
-- repeated or concurrent grant for the same player, build and claim key finds the row and stores nothing. The claim
-- outlives the gifts: removing a gift from the box never makes the reward come back. claim_key is binary-collated
-- so keys differing only in case stay distinct (for example level:12).
CREATE TABLE IF NOT EXISTS present_claims (
    profile VARCHAR(32) NOT NULL,
    user_num BIGINT NOT NULL,
    claim_key VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    claimed_at_unix_ms BIGINT NOT NULL,
    PRIMARY KEY (profile, user_num, claim_key)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
