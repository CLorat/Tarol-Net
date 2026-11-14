namespace Tarol.Persistence.Npgsql;

/// <summary>Candidate S-A: shared tables, the stream type as a column, the tenant first in every key.</summary>
public static class Schema
{
    public const int Version = 1;

    public static string Sql(string s) => $"""
        CREATE SCHEMA IF NOT EXISTS {s};

        -- one row per stream: the concurrency guard (version) and the stored state (primitives.md §16)
        CREATE TABLE IF NOT EXISTS {s}.streams (
            tenant      uuid        NOT NULL,              -- 00000000-… = no tenant
            type        text        NOT NULL,              -- stream type alias, e.g. 'auction' (never a CLR name)
            id          uuid        NOT NULL,
            version     bigint      NOT NULL,
            schema      integer     NOT NULL,              -- T.StateSchema; 0 = load by replay
            state       jsonb       NULL,
            updated_at  timestamptz NOT NULL,
            PRIMARY KEY (tenant, type, id));

        CREATE TABLE IF NOT EXISTS {s}.events (
            position       bigint      GENERATED ALWAYS AS IDENTITY,
            tenant         uuid        NOT NULL,
            type           text        NOT NULL,
            stream_id      uuid        NOT NULL,
            version        bigint      NOT NULL,
            event_id       uuid        NOT NULL,          -- UUIDv7 from the store's TimeProvider
            alias          text        NOT NULL,
            alias_version  integer     NOT NULL,
            entity_id      uuid        NOT NULL,
            parent_id      uuid        NULL,
            payload        jsonb       NOT NULL,
            recorded_at    timestamptz NOT NULL,          -- one value per commit
            actor_kind     text        NOT NULL,
            actor_id       text        NOT NULL,
            on_behalf_of   text        NULL,
            correlation_id uuid        NOT NULL,
            causation_id   uuid        NULL,
            traceparent    text        NULL,
            tx_id          xid8        NOT NULL DEFAULT pg_current_xact_id(),
            PRIMARY KEY (tenant, type, stream_id, version));
        CREATE UNIQUE INDEX IF NOT EXISTS events_position ON {s}.events (position);
        CREATE UNIQUE INDEX IF NOT EXISTS events_event_id ON {s}.events (event_id);

        -- unique lookups (C-16), candidate L-A: keys computed by a function at commit
        CREATE TABLE IF NOT EXISTS {s}.lookups (
            tenant    uuid NOT NULL,
            type      text NOT NULL,
            lookup    text NOT NULL,
            key       text NOT NULL,
            stream_id uuid NOT NULL,
            PRIMARY KEY (tenant, type, lookup, key));
        CREATE UNIQUE INDEX IF NOT EXISTS lookups_stream ON {s}.lookups (tenant, type, lookup, stream_id);

        -- idempotency keys (C-8): successes only, written in the append transaction
        CREATE TABLE IF NOT EXISTS {s}.idempotency_keys (
            tenant     uuid        NOT NULL,
            scope      text        NOT NULL,
            key        text        NOT NULL,
            stream_id  uuid        NOT NULL,
            version    bigint      NOT NULL,
            events     integer     NOT NULL,
            stored_at  timestamptz NOT NULL,
            PRIMARY KEY (tenant, scope, key));
        CREATE INDEX IF NOT EXISTS idempotency_keys_stored_at ON {s}.idempotency_keys (stored_at);

        -- outbox: one row per (event, reaction), written in the append transaction (changes.md §7)
        CREATE TABLE IF NOT EXISTS {s}.outbox (
            id           bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            tenant       uuid        NOT NULL,
            reaction     text        NOT NULL,
            type         text        NOT NULL,
            stream_id    uuid        NOT NULL,
            version      bigint      NOT NULL,
            event_id     uuid        NOT NULL,
            position     bigint      NULL,
            due_at       timestamptz NOT NULL,
            attempts     integer     NOT NULL DEFAULT 0,
            lease_until  timestamptz NULL,
            last_error   text        NULL,
            UNIQUE (reaction, event_id));
        CREATE INDEX IF NOT EXISTS outbox_due ON {s}.outbox (due_at, id);
        CREATE INDEX IF NOT EXISTS outbox_stream ON {s}.outbox (tenant, type, stream_id, reaction, id);

        CREATE TABLE IF NOT EXISTS {s}.schema_version (version integer NOT NULL);
        INSERT INTO {s}.schema_version SELECT {Version} WHERE NOT EXISTS (SELECT 1 FROM {s}.schema_version);
        """;
}
