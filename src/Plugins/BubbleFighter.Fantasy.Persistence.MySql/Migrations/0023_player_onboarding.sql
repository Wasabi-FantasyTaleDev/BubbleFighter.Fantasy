-- Initial character choice is independent of the currently worn inventory character.
CREATE TABLE IF NOT EXISTS player_onboarding (
    user_num BIGINT NOT NULL,
    initial_character_item_id INT NOT NULL,
    tutorial_offered TINYINT NOT NULL DEFAULT 0,
    created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (user_num)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
