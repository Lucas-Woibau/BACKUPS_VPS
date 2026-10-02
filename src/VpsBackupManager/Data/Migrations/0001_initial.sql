-- 0001: schema inicial. Todos os timestamps são INTEGER (Unix epoch em milissegundos, UTC).

CREATE TABLE users (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    username        TEXT NOT NULL UNIQUE COLLATE NOCASE,
    password_hash   TEXT NOT NULL,
    created_at      INTEGER NOT NULL,
    last_login_at   INTEGER
);

CREATE TABLE sessions (
    token_hash      TEXT PRIMARY KEY,
    user_id         INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    csrf_token      TEXT NOT NULL,
    created_at      INTEGER NOT NULL,
    last_seen_at    INTEGER NOT NULL,
    expires_at      INTEGER NOT NULL,
    ip              TEXT,
    user_agent      TEXT
);
CREATE INDEX idx_sessions_user ON sessions(user_id);

CREATE TABLE settings (
    key             TEXT PRIMARY KEY,
    value           TEXT NOT NULL,
    updated_at      INTEGER NOT NULL
);

CREATE TABLE connections (
    id                   INTEGER PRIMARY KEY AUTOINCREMENT,
    name                 TEXT NOT NULL UNIQUE COLLATE NOCASE,
    db_type              TEXT NOT NULL,
    host                 TEXT NOT NULL,
    port                 INTEGER NOT NULL,
    username             TEXT NOT NULL,
    password_enc         TEXT NOT NULL,
    ssl_mode             TEXT NOT NULL DEFAULT 'disabled',
    options              TEXT NOT NULL DEFAULT '{}',
    backup_all           INTEGER NOT NULL DEFAULT 1,
    include_system       INTEGER NOT NULL DEFAULT 0,
    selected_databases   TEXT NOT NULL DEFAULT '[]',
    excluded_databases   TEXT NOT NULL DEFAULT '[]',
    discovered_databases TEXT NOT NULL DEFAULT '[]',
    enabled              INTEGER NOT NULL DEFAULT 1,
    last_test_ok         INTEGER,
    last_test_at         INTEGER,
    last_test_message    TEXT,
    created_at           INTEGER NOT NULL,
    updated_at           INTEGER NOT NULL
);

CREATE TABLE schedules (
    id                    INTEGER PRIMARY KEY AUTOINCREMENT,
    name                  TEXT NOT NULL,
    enabled               INTEGER NOT NULL DEFAULT 1,
    kind                  TEXT NOT NULL,               -- 'weekly' | 'interval'
    days_of_week          TEXT NOT NULL DEFAULT '[]',  -- .NET DayOfWeek: 0 = domingo ... 6 = sábado
    times                 TEXT NOT NULL DEFAULT '[]',  -- ["03:00","15:30"]
    interval_minutes      INTEGER,
    target_type           TEXT NOT NULL DEFAULT 'all', -- 'all' | 'connection' | 'database'
    target_connection_id  INTEGER REFERENCES connections(id) ON DELETE CASCADE,
    target_database       TEXT,
    last_triggered_at     INTEGER,
    created_at            INTEGER NOT NULL,
    updated_at            INTEGER NOT NULL
);

CREATE TABLE runs (
    id                    TEXT PRIMARY KEY,
    trigger               TEXT NOT NULL,               -- 'manual' | 'schedule' | 'catchup'
    schedule_id           INTEGER,
    target_type           TEXT NOT NULL,
    target_connection_id  INTEGER,
    target_database       TEXT,
    status                TEXT NOT NULL,               -- pending | running | success | warning | error
    total                 INTEGER NOT NULL DEFAULT 0,
    succeeded             INTEGER NOT NULL DEFAULT 0,
    warnings              INTEGER NOT NULL DEFAULT 0,
    failed                INTEGER NOT NULL DEFAULT 0,
    message               TEXT,
    started_at            INTEGER NOT NULL,
    finished_at           INTEGER
);
CREATE INDEX idx_runs_started ON runs(started_at);

CREATE TABLE backups (
    id                TEXT PRIMARY KEY,
    run_id            TEXT NOT NULL REFERENCES runs(id) ON DELETE CASCADE,
    connection_id     INTEGER,
    connection_name   TEXT NOT NULL,
    db_type           TEXT NOT NULL,
    database_name     TEXT NOT NULL,
    status            TEXT NOT NULL,                   -- pending | running | success | warning | error
    started_at        INTEGER,
    finished_at       INTEGER,
    duration_ms       INTEGER,
    raw_size          INTEGER,
    compressed_size   INTEGER,
    final_size        INTEGER,
    compression       TEXT,
    encrypted         INTEGER NOT NULL DEFAULT 0,
    raw_sha256        TEXT,
    checksum_sha256   TEXT,
    file_name         TEXT,
    local_path        TEXT,
    remote_path       TEXT,
    upload_attempts   INTEGER NOT NULL DEFAULT 0,
    warning           TEXT,
    error             TEXT,
    local_deleted_at  INTEGER,
    remote_deleted_at INTEGER,
    created_at        INTEGER NOT NULL
);
CREATE INDEX idx_backups_run ON backups(run_id);
CREATE INDEX idx_backups_group ON backups(connection_id, database_name, status);
CREATE INDEX idx_backups_finished ON backups(finished_at);

CREATE TABLE events (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    run_id      TEXT,
    backup_id   TEXT,
    level       TEXT NOT NULL,
    message     TEXT NOT NULL,
    created_at  INTEGER NOT NULL
);
CREATE INDEX idx_events_run ON events(run_id);
CREATE INDEX idx_events_created ON events(created_at);
