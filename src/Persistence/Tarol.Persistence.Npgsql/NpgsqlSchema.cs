using System.Text.RegularExpressions;

namespace Tarol.Persistence.Npgsql;

/// <summary>
/// The store's SQL schema: one versioned, idempotent script (docs/architecture/persistence.md §2). Run it with
/// <see cref="NpgsqlAggregateStore.EnsureSchema"/> or hand it to your migration tool: <c>NpgsqlSchema.Script("tarol")</c>.
/// </summary>
public static partial class NpgsqlSchema
{
    /// <summary>The version of the script; stored in <c>&lt;schema&gt;.schema_version</c>.</summary>
    public const int Version = 1;

    /// <summary>The default schema name.</summary>
    public const string DefaultSchema = "tarol";

    /// <summary>The script for <paramref name="schema"/>: creates what is missing, changes nothing that exists.</summary>
    /// <param name="schema">The PostgreSQL schema name: lowercase letters, digits and <c>_</c>, starting with a letter or <c>_</c>.</param>
    /// <returns>The SQL.</returns>
    /// <exception cref="ArgumentException"><paramref name="schema"/> is not a plain identifier.</exception>
    public static string Script(string schema = DefaultSchema)
    {
        Validate(schema);
        var s = schema;
        return $"""
            CREATE SCHEMA IF NOT EXISTS {s};

            -- one row per stream: the concurrency guard (version) and the stored state
            CREATE TABLE IF NOT EXISTS {s}.streams (
                tenant      uuid        NOT NULL,          -- 00000000-0000-0000-0000-000000000000 = no tenant
                type        text        NOT NULL,          -- T.StreamType
                id          uuid        NOT NULL,
                version     bigint      NOT NULL,
                schema      integer     NOT NULL,          -- T.StateSchema; 0 = load by replay
                state       jsonb       NULL,
                updated_at  timestamptz NOT NULL,
                PRIMARY KEY (tenant, type, id));

            CREATE TABLE IF NOT EXISTS {s}.events (
                position       bigint      GENERATED ALWAYS AS IDENTITY,
                tenant         uuid        NOT NULL,
                type           text        NOT NULL,
                stream_id      uuid        NOT NULL,
                version        bigint      NOT NULL,
                event_id       uuid        NOT NULL,       -- UUIDv7 from the store's TimeProvider
                alias          text        NOT NULL,
                alias_version  integer     NOT NULL,
                entity_id      uuid        NOT NULL,
                parent_id      uuid        NULL,
                payload        jsonb       NOT NULL,
                recorded_at    timestamptz NOT NULL,       -- one value per commit
                actor_kind     text        NOT NULL,
                actor_id       text        NOT NULL,
                on_behalf_of   text        NULL,
                correlation_id uuid        NOT NULL,
                causation_id   uuid        NULL,
                traceparent    text        NULL,
                tx_id          xid8        NOT NULL DEFAULT pg_current_xact_id(),
                PRIMARY KEY (tenant, type, stream_id, version));
            CREATE UNIQUE INDEX IF NOT EXISTS events_event_id ON {s}.events (event_id);
            CREATE UNIQUE INDEX IF NOT EXISTS events_feed ON {s}.events (tx_id, position);

            CREATE TABLE IF NOT EXISTS {s}.lookups (
                tenant    uuid NOT NULL,
                type      text NOT NULL,
                lookup    text NOT NULL,
                key       text NOT NULL,
                stream_id uuid NOT NULL,
                PRIMARY KEY (tenant, type, lookup, key));
            CREATE UNIQUE INDEX IF NOT EXISTS lookups_stream ON {s}.lookups (tenant, type, lookup, stream_id);

            CREATE TABLE IF NOT EXISTS {s}.idempotency_keys (
                tenant     uuid        NOT NULL,
                scope      text        NOT NULL,
                key        text        NOT NULL,
                stream_id  uuid        NOT NULL,
                version    bigint      NOT NULL,
                events     integer     NOT NULL,
                stored_at  timestamptz NOT NULL,
                fingerprint text       NULL,               -- what the key was used for (e.g. method, path, body hash)
                PRIMARY KEY (tenant, scope, key));
            ALTER TABLE {s}.idempotency_keys ADD COLUMN IF NOT EXISTS fingerprint text NULL;   -- tables created before step 10
            CREATE INDEX IF NOT EXISTS idempotency_keys_stored_at ON {s}.idempotency_keys (stored_at);

            -- one row per (event, reaction), written in the event's commit; deleted when the reaction completed. Only a head is
            -- claimable: the oldest row of its (tenant, type, stream, reaction), or any row of a scheduled reaction (S-8, S-9).
            CREATE TABLE IF NOT EXISTS {s}.outbox (
                id           bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                tenant       uuid        NOT NULL,
                reaction     text        NOT NULL,
                type         text        NOT NULL,
                stream_id    uuid        NOT NULL,
                version      bigint      NOT NULL,
                event_id     uuid        NOT NULL,
                due_at       timestamptz NOT NULL,
                head         boolean     NOT NULL DEFAULT false,
                attempts     integer     NOT NULL DEFAULT 0,
                claim_id     uuid        NULL,             -- the current claim; Complete, Retry and Bury must present it
                lease_until  timestamptz NULL,
                last_error   text        NULL,
                UNIQUE (reaction, event_id));
            CREATE INDEX IF NOT EXISTS outbox_heads ON {s}.outbox (due_at, id) WHERE head;
            CREATE INDEX IF NOT EXISTS outbox_stream ON {s}.outbox (tenant, type, stream_id, reaction, id);

            -- rows given up after the delivery policy's attempts (S-5); a human reads and re-drives them
            CREATE TABLE IF NOT EXISTS {s}.outbox_dead (
                id           bigint      PRIMARY KEY,      -- the outbox row's id
                tenant       uuid        NOT NULL,
                reaction     text        NOT NULL,
                type         text        NOT NULL,
                stream_id    uuid        NOT NULL,
                version      bigint      NOT NULL,
                event_id     uuid        NOT NULL,
                attempts     integer     NOT NULL,
                last_error   text        NULL,
                dead_at      timestamptz NOT NULL);

            -- one row per (tenant, job): the cursor of the oldest occurrence not yet run, claimed one at a time with a lease
            -- (step 14, J-4); tenant-less jobs use the empty uuid
            CREATE TABLE IF NOT EXISTS {s}.jobs (
                tenant       uuid        NOT NULL,
                name         text        NOT NULL,
                schedule     text        NOT NULL,         -- cron and zone; a different text at startup recomputes the cursor
                enabled      boolean     NOT NULL DEFAULT true,
                next_at      timestamptz NOT NULL,         -- the cursor: whole seconds
                claim_id     uuid        NULL,             -- the current claim; every later call must present it
                lease_until  timestamptz NULL,             -- until when a claim, or a retry's backoff, holds the row
                attempts     integer     NOT NULL DEFAULT 0,
                last_error   text        NULL,
                PRIMARY KEY (tenant, name));
            CREATE INDEX IF NOT EXISTS jobs_due ON {s}.jobs (next_at) WHERE enabled;

            -- occurrences given up after the delivery policy's attempts (J-7); the cursor moved on, a human re-drives them
            CREATE TABLE IF NOT EXISTS {s}.jobs_dead (
                id           bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                tenant       uuid        NOT NULL,
                name         text        NOT NULL,
                occurrence   timestamptz NOT NULL,
                attempts     integer     NOT NULL,
                last_error   text        NULL,
                dead_at      timestamptz NOT NULL,
                redrive      boolean     NOT NULL DEFAULT false);   -- a re-drive is pending: the row's next claim runs it
            CREATE INDEX IF NOT EXISTS jobs_dead_redrive ON {s}.jobs_dead (tenant, name, id) WHERE redrive;

            CREATE TABLE IF NOT EXISTS {s}.schema_version (version integer NOT NULL);
            INSERT INTO {s}.schema_version SELECT {Version} WHERE NOT EXISTS (SELECT 1 FROM {s}.schema_version);
            """;
    }

    internal static void Validate(string schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        if (!Identifier().IsMatch(schema))
            throw new ArgumentException($"'{schema}' is not a plain PostgreSQL identifier (lowercase letters, digits and _, at most 63 characters).", nameof(schema));
    }

    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$")]
    private static partial Regex Identifier();
}
