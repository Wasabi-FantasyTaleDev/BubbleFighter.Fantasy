-- Keep the request identity and the entire allocated range after inventory removal.
ALTER TABLE purchase_log
    ADD COLUMN IF NOT EXISTS purchase_key INT NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS last_slot_key BIGINT NOT NULL DEFAULT 0;
UPDATE purchase_log SET last_slot_key = slot_key WHERE last_slot_key = 0;
