-- Bag entries by meaning. unknown1 is the item id (the client's bag map is keyed by it,
-- kr-1256 sub_8046E0) and unknown2 the item count (bags count stacks 1, 2, 3 ...,
-- consumables down from 999, permanent gear at 2147483647). unknown4 was a wire string every
-- bag sends empty, so it is no longer stored.
ALTER TABLE inventory_slots
    CHANGE COLUMN unknown1 item_id INT NOT NULL,
    CHANGE COLUMN unknown2 quantity INT NOT NULL,
    DROP COLUMN unknown4;

-- S2C 803 containers 2 and 3 and S2C 810 are always sent empty (container 3 is
-- a removal list, sub_933190), and the create-user submit table was never read: nothing that
-- belongs to a player is lost with them.
DROP TABLE IF EXISTS equipped_slots;
DROP TABLE IF EXISTS character_item_ids;
DROP TABLE IF EXISTS cash_inventory_slots;
DROP TABLE IF EXISTS create_user_submits;
