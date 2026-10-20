-- Append-only shop purchase audit log. The buy request (C2S 809,
-- sub_E1D720) carries only an item id and a zero-extended u8, so price,
-- money type and granted slot key are recorded here server-side. Rows are
-- never rewritten; the bag itself lives in inventory_slots (migration 0005).
CREATE TABLE IF NOT EXISTS purchase_log (
    id BIGINT NOT NULL AUTO_INCREMENT,
    user_num BIGINT NOT NULL,
    stock_set_id INT NOT NULL,
    item_id INT NOT NULL,
    money_type VARCHAR(16) NOT NULL,
    price INT NOT NULL,
    slot_key BIGINT NOT NULL,
    created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (id),
    KEY ix_purchase_log_user (user_num, id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
