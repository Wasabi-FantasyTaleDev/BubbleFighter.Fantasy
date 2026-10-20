-- kr-812 sub_81A820 retains the avatar material color in user state +100,
-- independently of fighter slot and team. Existing accounts keep the native default.
ALTER TABLE players ADD COLUMN color_index SMALLINT UNSIGNED NOT NULL DEFAULT 0;
