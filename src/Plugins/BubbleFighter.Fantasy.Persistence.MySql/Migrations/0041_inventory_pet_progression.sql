-- Growth and feeding deadlines are independent of an inventory item's ownership lifetime.
ALTER TABLE inventory_slots ADD COLUMN pet_json JSON NULL;
ALTER TABLE inventory_slots ADD COLUMN pet_entitlement_id BIGINT UNSIGNED NULL;
ALTER TABLE inventory_slots ADD COLUMN pet_article_key BIGINT UNSIGNED NULL;
CREATE UNIQUE INDEX uq_inventory_pet_entitlement ON inventory_slots (bag, pet_entitlement_id);
CREATE UNIQUE INDEX uq_inventory_pet_article ON inventory_slots (bag, pet_article_key);
CREATE TABLE pet_identity_allocator (
    singleton TINYINT NOT NULL PRIMARY KEY,
    entitlement_id BIGINT UNSIGNED NOT NULL,
    article_key BIGINT UNSIGNED NOT NULL,
    CONSTRAINT ck_pet_allocator_singleton CHECK (singleton = 1)
);
INSERT INTO pet_identity_allocator(singleton, entitlement_id, article_key) VALUES (1, 0, 99999999);
