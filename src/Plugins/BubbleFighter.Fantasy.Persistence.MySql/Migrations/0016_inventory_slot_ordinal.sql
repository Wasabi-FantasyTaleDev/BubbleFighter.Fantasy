-- S2C 802 / 803-container-1 bag rows are a LIST, not a keyed map: the
-- client keys all four starter slots with the same i64 marker
-- (0x40F869F000000000 / 0x40F86A0000000000), so slot_key cannot be the row
-- identity. The wire order becomes the primary key; slot_key stays a stored
-- value only. Applied on top of 0005_inventory.
ALTER TABLE inventory_slots DROP PRIMARY KEY;
ALTER TABLE inventory_slots ADD COLUMN IF NOT EXISTS ordinal INT NOT NULL DEFAULT 0 AFTER bag;
ALTER TABLE inventory_slots ADD PRIMARY KEY (user_num, bag, ordinal);
