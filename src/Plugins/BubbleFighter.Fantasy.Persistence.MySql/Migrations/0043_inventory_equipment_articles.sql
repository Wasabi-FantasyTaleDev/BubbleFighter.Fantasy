-- Ordinary equipment shares the existing Article allocator and retains pet ownership unchanged.
ALTER TABLE inventory_slots ADD COLUMN article_entitlement_id BIGINT UNSIGNED NULL;
ALTER TABLE inventory_slots ADD COLUMN article_key BIGINT UNSIGNED NULL;
ALTER TABLE inventory_slots ADD COLUMN enhancements_json JSON NULL;
CREATE UNIQUE INDEX uq_inventory_article_entitlement ON inventory_slots (bag, article_entitlement_id);
CREATE UNIQUE INDEX uq_inventory_article_key ON inventory_slots (bag, article_key);
