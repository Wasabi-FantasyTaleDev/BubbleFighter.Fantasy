-- A persistent identity lets directory nodes detect when matching deployment labels point to different databases.
CREATE TABLE IF NOT EXISTS cluster_data_domain (
    singleton_id TINYINT UNSIGNED NOT NULL,
    domain_id CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    PRIMARY KEY (singleton_id),
    CONSTRAINT ck_cluster_data_domain_singleton CHECK (singleton_id = 1)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

INSERT IGNORE INTO cluster_data_domain (singleton_id, domain_id) VALUES (1, UUID());
