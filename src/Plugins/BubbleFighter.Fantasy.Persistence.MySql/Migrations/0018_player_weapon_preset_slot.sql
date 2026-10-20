-- Per-player active weapon-preset slot (preset-persistence.md). The client
-- rebuilds its local active-slot store at profile record +28 from the S2C 837
-- leading u8 on every login (sub_81B820) and from every status-0 S2C 836
-- (sub_9D2FF0:9D5094-9D5097), and the swap chain has no save opcode, so the
-- server must persist the slot it granted for the next login to echo it back.
-- 0..4 = the client's preset hotkey slots (sub_808F10).
ALTER TABLE players
    ADD COLUMN active_weapon_preset_slot TINYINT UNSIGNED NOT NULL DEFAULT 0;
