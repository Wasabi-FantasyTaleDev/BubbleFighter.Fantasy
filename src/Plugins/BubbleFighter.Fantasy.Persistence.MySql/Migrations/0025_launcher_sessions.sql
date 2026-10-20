-- Durable launcher sign-ins. Only the SHA-256 of the current refresh credential is kept; rotation replaces
-- token_hash in one conditional UPDATE so exactly one concurrent refresh wins.
CREATE TABLE IF NOT EXISTS launcher_sessions (
    session_id CHAR(64) NOT NULL,
    username VARCHAR(64) NOT NULL,
    token_hash CHAR(64) NOT NULL,
    created_at DATETIME(6) NOT NULL,
    rotated_at DATETIME(6) NULL,
    expires_at DATETIME(6) NOT NULL,
    revoked_at DATETIME(6) NULL,
    PRIMARY KEY (session_id),
    UNIQUE KEY uq_launcher_sessions_token (token_hash),
    KEY ix_launcher_sessions_username (username),
    KEY ix_launcher_sessions_expires (expires_at)
) ENGINE=InnoDB DEFAULT CHARSET=ascii COLLATE=ascii_bin;
