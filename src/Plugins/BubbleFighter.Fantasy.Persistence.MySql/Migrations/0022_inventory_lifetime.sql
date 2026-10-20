-- Existing rows have no established lifetime; never infer permanence from a missing deadline.
-- Unix milliseconds retain the same instant across database and process time zones.
ALTER TABLE inventory_slots
    ADD COLUMN lifetime_kind TINYINT NOT NULL DEFAULT 0,
    ADD COLUMN expires_at_unix_ms BIGINT NULL,
    ADD CONSTRAINT ck_inventory_lifetime CHECK (
        (lifetime_kind IN (0, 1) AND expires_at_unix_ms IS NULL)
        OR (lifetime_kind = 2 AND expires_at_unix_ms IS NOT NULL
            AND expires_at_unix_ms BETWEEN -62135596800000 AND 253402300799999)
    );
