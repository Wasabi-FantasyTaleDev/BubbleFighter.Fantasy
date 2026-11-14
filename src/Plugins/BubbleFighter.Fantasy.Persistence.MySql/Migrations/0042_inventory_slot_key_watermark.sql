-- Ordinary ownership identities remain retired after their rows are consumed or removed.
ALTER TABLE players ADD COLUMN last_inventory_slot_key BIGINT NOT NULL DEFAULT 0;
CREATE INDEX ix_purchase_log_user_last_slot_key ON purchase_log (user_num, last_slot_key);

UPDATE players AS player
LEFT JOIN (
    SELECT user_num, MAX(last_slot_key) AS last_slot_key
    FROM purchase_log GROUP BY user_num
) AS purchased ON purchased.user_num = player.user_num
LEFT JOIN (
    SELECT user_num, MAX(slot_key) AS last_slot_key
    FROM inventory_slots WHERE bag IN (0, 1) GROUP BY user_num
) AS owned ON owned.user_num = player.user_num
SET player.last_inventory_slot_key = GREATEST(player.last_inventory_slot_key,
    COALESCE(purchased.last_slot_key, 0), COALESCE(owned.last_slot_key, 0));
