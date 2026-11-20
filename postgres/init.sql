CREATE ROLE transfer_service LOGIN PASSWORD 'transfer_db_pass';
CREATE ROLE fraud_worker LOGIN PASSWORD 'fraud_db_pass';
CREATE ROLE monitoring LOGIN PASSWORD 'monitoring_pass';
GRANT pg_monitor TO monitoring;

CREATE TABLE accounts (
    id          SERIAL PRIMARY KEY,
    owner_name  TEXT          NOT NULL,
    iban        TEXT          NOT NULL UNIQUE,
    balance     NUMERIC(14,2) NOT NULL CHECK (balance >= 0),
    currency    CHAR(3)       NOT NULL DEFAULT 'UAH' CHECK (currency IN ('UAH', 'USD', 'EUR')),
    is_blocked  BOOLEAN       NOT NULL DEFAULT false,
    is_internal BOOLEAN       NOT NULL DEFAULT false,
    created_at  TIMESTAMPTZ   NOT NULL DEFAULT now(),
    updated_at  TIMESTAMPTZ   NOT NULL DEFAULT now()
);

CREATE TABLE transfers (
    id                 UUID          PRIMARY KEY,
    from_account_id    INT           NOT NULL REFERENCES accounts(id),
    to_account_id      INT           NOT NULL REFERENCES accounts(id),
    amount             NUMERIC(14,2) NOT NULL CHECK (amount > 0),
    currency           CHAR(3)       NOT NULL,
    reference          TEXT,
    initiated_by       TEXT          NOT NULL,
    status             TEXT          NOT NULL DEFAULT 'completed',
    from_balance_after NUMERIC(14,2) NOT NULL,
    isolation_level    TEXT          NOT NULL,
    lock_mode          TEXT          NOT NULL,
    attempts           INT           NOT NULL DEFAULT 1,
    fraud_status       TEXT          NOT NULL DEFAULT 'pending' CHECK (fraud_status IN ('pending', 'clean', 'suspicious')),
    fraud_score        INT,
    created_at         TIMESTAMPTZ   NOT NULL DEFAULT clock_timestamp(),
    checked_at         TIMESTAMPTZ,
    CHECK (from_account_id <> to_account_id)
);

CREATE INDEX transfers_from_created_idx ON transfers (from_account_id, created_at);
CREATE INDEX transfers_to_created_idx ON transfers (to_account_id, created_at);
CREATE INDEX transfers_pending_idx ON transfers (fraud_status) WHERE fraud_status = 'pending';

CREATE TABLE fraud_alerts (
    id          BIGSERIAL   PRIMARY KEY,
    transfer_id UUID        NOT NULL REFERENCES transfers(id),
    account_id  INT         NOT NULL REFERENCES accounts(id),
    rule        TEXT        NOT NULL,
    score       INT         NOT NULL,
    details     TEXT        NOT NULL,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX fraud_alerts_account_idx ON fraud_alerts (account_id, created_at);

GRANT SELECT, INSERT, UPDATE ON accounts TO transfer_service;
GRANT SELECT, INSERT ON transfers TO transfer_service;
GRANT SELECT ON fraud_alerts TO transfer_service;
GRANT USAGE ON SEQUENCE accounts_id_seq TO transfer_service;

GRANT SELECT ON accounts TO fraud_worker;
GRANT UPDATE (is_blocked) ON accounts TO fraud_worker;
GRANT SELECT ON transfers TO fraud_worker;
GRANT UPDATE (fraud_status, fraud_score, checked_at) ON transfers TO fraud_worker;
GRANT SELECT, INSERT ON fraud_alerts TO fraud_worker;
GRANT USAGE ON SEQUENCE fraud_alerts_id_seq TO fraud_worker;

INSERT INTO accounts (owner_name, iban, balance, currency, is_internal)
SELECT 'Settlement account ' || n,
       'UA' || lpad((300000000000000000000000000 + n)::text, 27, '0'),
       1000000.00, 'UAH', true
FROM generate_series(1, 6) AS n;

INSERT INTO accounts (owner_name, iban, balance, currency)
SELECT (ARRAY['Olena', 'Taras', 'Iryna', 'Andrii', 'Oksana', 'Dmytro', 'Natalia', 'Serhii', 'Yulia', 'Maksym'])[1 + (n % 10)]
       || ' ' ||
       (ARRAY['Kovalenko', 'Shevchuk', 'Bondar', 'Melnyk', 'Tkachenko', 'Kravets', 'Moroz', 'Lysenko', 'Savchenko', 'Rudenko'])[1 + ((n * 7) % 10)],
       'UA' || lpad((100000000000000000000000000 + n)::text, 27, '0'),
       round((5000 + random() * 145000)::numeric, 2),
       'UAH'
FROM generate_series(1, 40) AS n;

INSERT INTO accounts (owner_name, iban, balance, currency)
SELECT 'USD client ' || n,
       'UA' || lpad((200000000000000000000000000 + n)::text, 27, '0'),
       round((500 + random() * 9500)::numeric, 2),
       'USD'
FROM generate_series(1, 4) AS n;
