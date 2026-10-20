-- Life-counted items: lifetime_kind 3 means no deadline, and quantity is the remaining life, which the daily
-- login rule lowers by the whole UTC days since players.last_login_day (InventoryLife in the contracts).
-- A player with no stored day (every existing player) is on a first login and loses nothing.
ALTER TABLE players
    ADD COLUMN last_login_day DATE NULL;

ALTER TABLE inventory_slots
    DROP CONSTRAINT ck_inventory_lifetime,
    ADD CONSTRAINT ck_inventory_lifetime_v2 CHECK (
        (lifetime_kind IN (0, 1, 3) AND expires_at_unix_ms IS NULL)
        OR (lifetime_kind = 2 AND expires_at_unix_ms IS NOT NULL
            AND expires_at_unix_ms BETWEEN -62135596800000 AND 253402300799999)
    );
