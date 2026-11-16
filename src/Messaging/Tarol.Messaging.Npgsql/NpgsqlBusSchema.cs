using System.Text.RegularExpressions;

namespace Tarol.Messaging.Npgsql;

/// <summary>
/// The SQL of the in-database bus (docs/architecture/messaging.md §6): one versioned, idempotent script for the bus schema
/// every system reaches, the consumer's <c>messages_dead</c> table, and the row-level-security policy that binds a
/// publisher's role to its contract prefix. Run them with <see cref="NpgsqlBus.EnsureSchema"/> and
/// <see cref="NpgsqlMessageDeadLetters.EnsureSchema"/>, or hand them to your migration tool.
/// </summary>
public static partial class NpgsqlBusSchema
{
    /// <summary>The version of the bus script; stored in <c>&lt;schema&gt;.schema_version</c>.</summary>
    public const int Version = 1;

    /// <summary>The default bus schema.</summary>
    public const string DefaultSchema = "bus";

    /// <summary>The bus script for <paramref name="schema"/>: creates what is missing, changes nothing that exists.</summary>
    /// <param name="schema">A plain PostgreSQL identifier (lowercase letters, digits, <c>_</c>).</param>
    /// <returns>The SQL.</returns>
    /// <exception cref="ArgumentException"><paramref name="schema"/> is not a plain identifier.</exception>
    public static string Script(string schema = DefaultSchema)
    {
        Validate(schema);
        var s = schema;
        return $"""
            CREATE SCHEMA IF NOT EXISTS {s};

            -- one row per published (event, contract, version): a republish after a crash is dropped (ON CONFLICT)
            CREATE TABLE IF NOT EXISTS {s}.messages (
                id           uuid        NOT NULL,            -- ce-id: the event id
                type         text        NOT NULL,            -- ce-type: the contract, its prefix is the source
                version      integer     NOT NULL,            -- ce-contractversion
                source       text        NOT NULL,            -- ce-source as claimed
                publisher    text        NOT NULL DEFAULT session_user,   -- the role, stamped by the database (M-13)
                headers      jsonb       NOT NULL,            -- the CloudEvents headers (binary mode)
                data         bytea       NOT NULL,            -- the contract JSON
                published_at timestamptz NOT NULL,
                PRIMARY KEY (id, type, version));
            CREATE INDEX IF NOT EXISTS messages_published_at ON {s}.messages (published_at);

            -- which subscription receives which contract (one row per (contract, subscription))
            CREATE TABLE IF NOT EXISTS {s}.subscriptions (
                topic text NOT NULL,
                name  text NOT NULL,
                PRIMARY KEY (topic, name));

            -- one row per (subscription, message) until it is acknowledged; due_at is the next delivery (lease or retry)
            CREATE TABLE IF NOT EXISTS {s}.deliveries (
                id           bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,   -- the publish order of the subscription
                subscription text        NOT NULL,
                message_id   uuid        NOT NULL,
                type         text        NOT NULL,
                version      integer     NOT NULL,
                due_at       timestamptz NOT NULL,
                attempts     integer     NOT NULL DEFAULT 0,
                claim_id     uuid        NULL,             -- the current claim; acknowledge and retry must present it
                UNIQUE (subscription, message_id, type, version));
            CREATE INDEX IF NOT EXISTS deliveries_due ON {s}.deliveries (subscription, due_at, id);
            CREATE INDEX IF NOT EXISTS deliveries_message ON {s}.deliveries (message_id, type, version);

            CREATE TABLE IF NOT EXISTS {s}.schema_version (version integer NOT NULL);
            INSERT INTO {s}.schema_version SELECT {Version} WHERE NOT EXISTS (SELECT 1 FROM {s}.schema_version);
            """;
    }

    /// <summary>The consumer's dead letters (M-9) in its own schema (usually the store's, <c>tarol</c>).</summary>
    /// <param name="schema">A plain PostgreSQL identifier.</param>
    /// <returns>The SQL.</returns>
    /// <exception cref="ArgumentException"><paramref name="schema"/> is not a plain identifier.</exception>
    public static string DeadLettersScript(string schema = "tarol")
    {
        Validate(schema);
        return $"""
            CREATE SCHEMA IF NOT EXISTS {schema};
            CREATE TABLE IF NOT EXISTS {schema}.messages_dead (
                id           bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                subscription text        NOT NULL,
                topic        text        NOT NULL,
                message_id   uuid        NULL,             -- null for a poison message without headers
                attempts     integer     NOT NULL,
                reason       text        NOT NULL,
                headers      jsonb       NOT NULL,
                data         bytea       NOT NULL,
                dead_at      timestamptz NOT NULL);
            """;
    }

    /// <summary>
    /// Row-level security for the bus (docs/migration/messaging.md §3.2): every role may read and acknowledge, but a role
    /// inserts only messages whose contract prefix is its own name (policy <c>publish_own_prefix</c>), so <c>billing</c>
    /// cannot publish <c>artflow.*</c> (SQLSTATE 42501). Run it as the schema's owner, once; give each system a login role
    /// named like its prefix and the grants of <see cref="GrantScript"/>.
    /// </summary>
    /// <param name="schema">The bus schema.</param>
    /// <returns>The SQL.</returns>
    /// <exception cref="ArgumentException"><paramref name="schema"/> is not a plain identifier.</exception>
    public static string RowLevelSecurityScript(string schema = DefaultSchema)
    {
        Validate(schema);
        return $"""
            ALTER TABLE {schema}.messages ENABLE ROW LEVEL SECURITY;
            DROP POLICY IF EXISTS publish_own_prefix ON {schema}.messages;
            CREATE POLICY publish_own_prefix ON {schema}.messages FOR INSERT WITH CHECK (split_part(type, '.', 1) = session_user);
            DROP POLICY IF EXISTS read_all ON {schema}.messages;
            CREATE POLICY read_all ON {schema}.messages FOR SELECT USING (true);
            """;
    }

    /// <summary>The grants a system's role needs on the bus (publish, subscribe, consume).</summary>
    /// <param name="role">The system's login role, named like its contract prefix (e.g. <c>billing</c>).</param>
    /// <param name="schema">The bus schema.</param>
    /// <returns>The SQL.</returns>
    /// <exception cref="ArgumentException">The role or the schema is not a plain identifier.</exception>
    public static string GrantScript(string role, string schema = DefaultSchema)
    {
        Validate(schema);
        Validate(role);
        return $"""
            GRANT USAGE ON SCHEMA {schema} TO {role};
            GRANT SELECT, INSERT ON {schema}.messages, {schema}.subscriptions TO {role};
            GRANT SELECT, INSERT, UPDATE, DELETE ON {schema}.deliveries TO {role};
            GRANT SELECT ON {schema}.schema_version TO {role};
            """;
    }

    internal static void Validate(string identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        if (!Identifier().IsMatch(identifier))
            throw new ArgumentException($"'{identifier}' is not a plain PostgreSQL identifier (lowercase letters, digits and _, at most 63 characters).", nameof(identifier));
    }

    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$")]
    private static partial Regex Identifier();
}
